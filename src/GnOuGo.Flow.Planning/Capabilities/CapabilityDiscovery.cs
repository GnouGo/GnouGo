using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
namespace GnOuGo.Flow.Planning.Capabilities;

/// <summary>Lazy source discovery. Only resolved contracts are authorized for graph binding.</summary>
public sealed class CapabilityDiscovery(WorkflowEngine engine) : ICapabilityCatalog
{
    private readonly Dictionary<string, List<PlanningCapability>> _cache = new(StringComparer.Ordinal);
    internal static Task<PlanningCatalog> DiscoverAsync(WorkflowEngine engine, PlanningRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var catalog = new PlanningCatalog { Policy = request.Policy };
        foreach (var (type, contract) in engine.Registry.GetContracts().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (type is "workflow.plan" or "workflow.execute" or "workflow.route" or "mcp.list" ||
                type == "agent.run" && engine.AgentTaskRunners.Count == 0 ||
                request.Policy.AllowedStepTypes.Count > 0 && !request.Policy.AllowedStepTypes.Contains(type)) continue;
            catalog.AllowedStepTypes.Add(type);
            catalog.StepContracts[type] = new JsonObject { ["input"] = contract.InputSchema.DeepClone(), ["output"] = contract.OutputSchema.DeepClone() };
            if (contract.PlanningEffectKind is { } effect)
            {
                var capability = new PlanningCapability { Id = Identity("runtime", "registered", type), Kind = "registered", StepType = type,
                    Description = contract.InputSchema["description"]?.ToString() ?? "Registered typed operation",
                    InputSchema = contract.InputSchema.DeepClone().AsObject(), OutputSchema = contract.OutputSchema.DeepClone().AsObject(), EffectKind = effect };
                capability.Version = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
                catalog.Capabilities.Add(capability);
            }
        }
        return Task.FromResult(catalog);
    }

    public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sources = (engine.McpClientFactory?.ServerMetadata ?? []).Select(s => new CapabilitySource(SourceId(s.Name), s.Description ?? s.Name)).ToList();
        foreach (var (name, runner) in engine.AgentTaskRunners) sources.Add(new(RunnerSource(name), runner.Description));
        return Task.FromResult<IReadOnlyList<CapabilitySource>>(sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray());
    }

    public async Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct)
    {
        if (cursor is not null && (!int.TryParse(cursor, out var parsed) || parsed < 0)) throw new ArgumentException("Invalid discovery cursor.");
        try
        {
            var capabilities = await ReadSourceAsync(sourceId, ct);
            var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            if (offset > capabilities.Count) throw new ArgumentException("Discovery cursor is outside this source.");
            const int pageSize = 24;
            return new(sourceId, cursor, capabilities.Skip(offset).Take(pageSize).Select(c => new CapabilitySummary(c.Id, sourceId,
                c.Method ?? c.Id, c.Description, c.StepType, c.EffectKind, c.Version, c.Composition, TaskOperations.Describe(c))).ToList(),
                offset + pageSize < capabilities.Count ? (offset + pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not ArgumentException)
        { return new(sourceId, cursor, [], null, "The source could not be discovered. Its capabilities remain unavailable."); }
    }

    public async Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
    {
        var entries = await ReadSourceAsync(summary.SourceId, ct);
        var capability = entries.SingleOrDefault(c => c.Id == summary.Id && c.Version == summary.Version)
            ?? throw new PlanningConflictException("The selected capability changed. Rediscover and review it.");
        if (PlanningContractValidation.ValidateSchema(capability.InputSchema).Count > 0 ||
            capability.OutputSchema.Count > 0 && PlanningContractValidation.ValidateSchema(capability.OutputSchema).Count > 0)
            throw new PlanningConflictException("The selected capability has an invalid declared schema.");
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
    }

    private async Task<List<PlanningCapability>> ReadSourceAsync(string id, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var capabilities = new List<PlanningCapability>();
        var selectedRunner = engine.AgentTaskRunners.FirstOrDefault(p => RunnerSource(p.Key) == id);
        if (selectedRunner.Value is { } runner)
        {
            var declared = await runner.DescribeAsync(ct);
            if (string.IsNullOrWhiteSpace(declared.Description) || PlanningContractValidation.ValidateSchema(declared.InputSchema).Count > 0)
                throw new InvalidOperationException("The task runner declares an invalid contract.");
            capabilities.Add(new() { Id = Identity(id, "agent", selectedRunner.Key), Method = selectedRunner.Key, Kind = "agent", StepType = "agent.run", EffectKind = "execute",
                Description = declared.Description, InputSchema = declared.InputSchema.DeepClone().AsObject(),
                OutputSchema = new AgentRunExecutor().Contract.OutputSchema.DeepClone().AsObject(), FixedInput = new() { ["runner"] = selectedRunner.Key } });
        }
        else
        {
            var factory = engine.McpClientFactory ?? throw new InvalidOperationException("No capability sources are configured.");
            var source = factory.ServerMetadata.SingleOrDefault(s => SourceId(s.Name) == id) ?? throw new ArgumentException("Unknown capability source.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(source.DiscoveryTimeoutSeconds ?? 30));
            await using var session = await factory.GetClientAsync(source.Name, timeout.Token);
            foreach (var tool in await session.ListToolsAsync(timeout.Token))
            {
                if (tool.Meta?["gnougo"]?["management"]?["visibility"]?.ToString() == "management_only") continue;
                if (tool.ArtifactContract?.Errors.Count > 0 || tool.CompositionContract?.Errors.Count > 0) continue;
                capabilities.Add(Tool(source.Name, tool));
            }
            foreach (var prompt in await session.ListPromptsAsync(timeout.Token))
                capabilities.Add(new() { Id = Identity(source.Name, "prompt", prompt.Name), StepType = "mcp.call", Server = source.Name, Method = prompt.Name,
                    Kind = "prompt", Description = prompt.Description ?? "", EffectKind = "read", InputSchema = new()
                    { ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JsonObject((prompt.Arguments ?? []).Select(a => new KeyValuePair<string, JsonNode?>(a.Name, new JsonObject { ["type"] = "string" }))),
                        ["required"] = new JsonArray((prompt.Arguments ?? []).Where(a => a.Required).Select(a => (JsonNode?)JsonValue.Create(a.Name)).ToArray()) } });
        }
        foreach (var capability in capabilities)
            capability.Version = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
        return _cache[id] = capabilities.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    internal static string RunnerSource(string name) => "runner_" + PlanningGraphCompiler.Fingerprint(name)[..24];
    internal static string SourceId(string name) => "source_" + PlanningGraphCompiler.Fingerprint(name)[..24];
    internal static string Identity(string server, string kind, string method) => "cap_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(new[] { server, kind, method }, PlanningJsonContext.Default.StringArray))[..24];
    internal static PlanningCapability Tool(string server, McpToolInfo tool)
    {
        var capability = new PlanningCapability
        {
        Id = Identity(server, "tool", tool.Name), StepType = "mcp.call", Server = server, Kind = "tool", Method = tool.Name,
        Description = tool.Description ?? "", InputSchema = tool.InputSchema?.DeepClone() as JsonObject ?? new() { ["type"] = "object" },
        OutputSchema = McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone() as JsonObject ?? new(),
        ArtifactContract = tool.ArtifactContract?.Contract, Composition = tool.CompositionContract?.Contract, Metadata = tool.Meta?.DeepClone(), ExampleResponse = tool.ExampleResponse?.DeepClone(),
        EffectKind = tool.EffectKind is "read" or "write" or "execute" or "lifecycle" or "none" ? tool.EffectKind : "unknown"
        };
        if (tool.Meta?["gnougo"]?["result"]?["detect_errors"] is { } detection)
        {
            if (detection is not JsonValue scalar || !scalar.TryGetValue<bool>(out var enabled)) throw new InvalidOperationException("Declared result error detection must be boolean.");
            capability.FixedInput["error_policy"] = new JsonObject { ["detect_result_errors"] = enabled };
        }
        return capability;
    }
}
