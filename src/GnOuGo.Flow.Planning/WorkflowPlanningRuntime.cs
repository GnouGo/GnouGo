using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
namespace GnOuGo.Flow.Planning;

/// <summary>Provider-neutral catalog, model, runtime validation, and checkpoint effects.</summary>
public sealed class WorkflowPlanningRuntime : IPlanningRuntime
{
    private readonly WorkflowEngine _engine;
    public ICapabilityCatalog Capabilities { get; }
    private readonly StepExecutionContext _context;
    private readonly ILLMClient? _model;
    private readonly Func<PlanningSession, CancellationToken, Task> _checkpoint;
    public WorkflowPlanningRuntime(WorkflowEngine engine, Func<PlanningSession, CancellationToken, Task> checkpoint, ICapabilityCatalog? capabilities = null)
    {
        _engine = engine; Capabilities = capabilities ?? new CapabilityDiscovery(engine); _checkpoint = checkpoint;
        _context = new() { Engine = engine, Step = new() { Source = new() { Id = "planning", Type = "workflow.plan" } },
            Data = new(), Limits = engine.Limits, LLMUsageBudget = engine.LLMUsageBudget };
    }
    public WorkflowPlanningRuntime(StepExecutionContext context, ILLMClient model, Func<PlanningSession, CancellationToken, Task> checkpoint)
    { _context = context; _engine = context.Engine; Capabilities = new CapabilityDiscovery(context.Engine); _model = model; _checkpoint = checkpoint; }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => CapabilityDiscovery.DiscoverAsync(_engine, request, ct);
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        => _context.CallLLMAsync(_model ?? _engine.LLMClient ?? throw new InvalidOperationException("No planning model configured."), request, "workflow.plan." + purpose, ct);
    public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => _checkpoint(session, ct);

    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var findings = new List<PlanningDiagnostic>();
        try
        {
            var document = WorkflowParser.Parse(request.Yaml);
            findings.AddRange(new WorkflowValidator(_engine.Registry).Validate(document).Select(e => new PlanningDiagnostic(e.Code, "workflow:" + e.WorkflowName + "/step:" + e.StepId + "/field:" + e.Field, e.Message)));
            new WorkflowCompiler().Compile(document);
            var contracts = request.Catalog.Capabilities.Where(c => c.Kind == "tool").Select(c => new McpToolOutputContract(c.Server!, c.Method!, c.InputSchema, c.OutputSchema, c.ExampleResponse)).ToArray();
            WorkflowPlanSemanticValidator.ValidateWithStepContracts(document, contracts, _engine.Registry.GetContracts());
            foreach (var (workflow, definition) in document.Workflows)
                foreach (var node in Enumerate(definition.Steps.Concat(definition.Finally)))
                {
                    if (node.Type is "workflow.plan" or "workflow.execute" || !request.Catalog.AllowedStepTypes.Contains(node.Type))
                        findings.Add(new("STEP_TYPE_DENIED", workflow + "/" + node.Id, "The executable step violates host policy."));
                    if (node.Type != "mcp.call") continue;
                    var binding = request.Bindings.SingleOrDefault(b => b.Workflow == workflow && b.Step == node.Id);
                    var capability = request.Catalog.Capabilities.SingleOrDefault(c => c.Id == binding?.CapabilityId);
                    if (capability is null || node.Input?["server"]?.ToString() != capability.Server || node.Input?["method"]?.ToString() != capability.Method || node.Input?["kind"]?.ToString() != capability.Kind)
                        findings.Add(new("CAPABILITY_BINDING_INVALID", workflow + "/" + node.Id, "The compiled call does not match its catalog binding."));
                }
        }
        catch (WorkflowSemanticValidationException ex)
        { findings.AddRange(ex.Errors.Select(e => new PlanningDiagnostic(e.Code, "workflow:" + e.WorkflowName + "/step:" + e.StepId + "/field:" + e.Field, e.Message))); }
        catch (WorkflowCompilationException ex)
        { findings.AddRange(ex.Errors.Select(e => new PlanningDiagnostic(e.Code, "workflow:" + e.WorkflowName + "/step:" + e.StepId + "/field:" + e.Field, e.Message))); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { findings.Add(new("EXECUTABLE_INVALID", "$", ex.Message)); }
        return Task.FromResult<IReadOnlyList<PlanningDiagnostic>>(findings);
    }
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct)
    {
        var fake = new InMemoryMcpClientFactory();
        foreach (var group in request.Catalog.Capabilities.Where(c => c.Server is not null).GroupBy(c => c.Server!))
        {
            var config = new MockMcpServerConfig();
            foreach (var capability in group.Where(c => c.Kind == "tool"))
            {
                config.Tools.Add(new() { Name = capability.Method!, InputSchema = capability.InputSchema, OutputSchema = capability.OutputSchema, EffectKind = capability.EffectKind,
                    ArtifactContract = capability.ArtifactContract is null ? null : new(capability.ArtifactContract, []) });
                config.ToolHandlers[capability.Method!] = _ => new()
                {
                    Content = capability.ExampleResponse is { } example && PlanningContractValidation.ValidateInstance(example, capability.OutputSchema).Count == 0
                        ? example.DeepClone() : WorkflowPlanDryRunValidator.CreateArtifactSample(capability.OutputSchema, capability.ArtifactContract)
                };
            }
            foreach (var capability in group.Where(c => c.Kind == "prompt")) config.Prompts.Add(new() { Name = capability.Method!, Description = capability.Description });
            fake.RegisterServer(group.Key, config);
        }
        return WorkflowPlanScenarioValidator.ValidateAsync(WorkflowParser.Parse(request.Yaml), fake, ct, request.Inputs, request.LoopItemSchemas, request.Observations);
    }
    public async Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct)
    {
        try
        {
            if (_engine.PlanningPolicy is { } policy && !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(policy, PlanningJsonContext.Default.PlanningPolicy), JsonSerializer.SerializeToNode(catalog.Policy, PlanningJsonContext.Default.PlanningPolicy)))
                return [new("POLICY_CHANGED", "/policy", "The current host policy differs from the reviewed policy; rebuild and review the workflow.")];
            var current = await CapabilityDiscovery.DiscoverAsync(_engine, new PlanningRequest { Policy = catalog.Policy }, ct);
            if (!JsonNode.DeepEquals(catalog.StepContracts, current.StepContracts) || !catalog.AllowedStepTypes.SequenceEqual(current.AllowedStepTypes))
                return [new("CATALOG_CHANGED", "/stepContracts", "The host's executable contracts changed; rebuild and review the workflow.")];
            var fresh = Capabilities is CapabilityDiscovery ? new CapabilityDiscovery(_engine) : Capabilities;
            var findings = new List<PlanningDiagnostic>();
            foreach (var capability in catalog.Capabilities)
            {
                try
                {
                var summary = new CapabilitySummary(capability.Id,
                    capability.Kind == "agent" ? "agent-runners" : CapabilityDiscovery.SourceId(capability.Server!),
                    capability.Method ?? capability.Id, capability.Description, capability.StepType, capability.EffectKind, capability.Version);
                var resolved = await fresh.ResolveAsync(summary, ct);
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(capability, PlanningJsonContext.Default.PlanningCapability),
                    JsonSerializer.SerializeToNode(resolved, PlanningJsonContext.Default.PlanningCapability)))
                    findings.Add(new("CATALOG_CHANGED", capability.Id, "A selected capability changed; regenerate and approve the workflow."));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is PlanningConflictException or ArgumentException)
                { findings.Add(new("CATALOG_CHANGED", capability.Id, "A selected capability changed or was removed. Regenerate and approve the workflow.")); }
                catch (Exception) { findings.Add(new("CATALOG_UNAVAILABLE", capability.Id, "The selected capability could not be verified.")); }
            }
            return findings;

        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return [new("CATALOG_UNAVAILABLE", "$", "Current capability contracts could not be verified.")]; }
    }
    private static IEnumerable<StepDef> Enumerate(IEnumerable<StepDef> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Enumerate((node.Steps ?? []).Concat(node.Default ?? []).Concat((node.Branches ?? []).SelectMany(b => b.Steps)).Concat((node.Cases ?? []).SelectMany(c => c.Steps)))) yield return child;
        }
    }
}
