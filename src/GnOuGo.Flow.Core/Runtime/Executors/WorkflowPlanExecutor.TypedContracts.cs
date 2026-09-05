using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Parsing;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    /// <summary>Reuses the established exact-capability inventory and validation boundary.</summary>
    public async Task<PlanningPreparation> PrepareTypedContractsAsync(StepExecutionContext ctx, PlanningRequest request, CancellationToken ct)
    {
        var input = (JsonObject)request.Options.DeepClone();
        input["planner_version"] = 2;
        input["raw_prompt"] = request.Prompt;
        input["capability_preflight"] ??= new JsonObject { ["mode"] = "infer" };
        if (input["capability_preflight"]?["mode"]?.GetValue<string>() == "off")
            throw new InvalidOperationException("Typed planning requires capability preflight.");
        input["generator"] ??= new JsonObject();
        var preflight = await RunCapabilityPreflightAsync(ctx, input, intentClarification: null, ct);
        var state = JsonSerializer.SerializeToNode(preflight, TypedContractJsonContext.Default.CapabilityPreflightResult) as JsonObject
            ?? throw new InvalidOperationException("Could not serialize the planning contract.");
        var allowed = (input["policy"]?["allowed_step_types"] as JsonArray)?.Select(v => v!.GetValue<string>()).ToList()
            ?? ctx.Engine.Registry.RegisteredTypes.ToList();
        var denied = (input["policy"]?["denied_step_types"] as JsonArray)?.Select(v => v!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        allowed.RemoveAll(type => denied.Contains(type) || type is "workflow.plan" or "workflow.execute");
        var declaredStepContracts = ctx.Engine.Registry.GetContracts();
        var capabilities = new List<PlanningCapability>();
        foreach (var capability in preflight.Capabilities.Where(c => c.Resolution != "unavailable"))
        {
            var tool = preflight.DiscoveredServers.FirstOrDefault(s => s.Name == capability.Server)?.Tools.FirstOrDefault(t => t.Name == capability.Method);
            var prompt = capability.Kind == "prompt" ? preflight.DiscoveredServers.FirstOrDefault(s => s.Name == capability.Server)?.Prompts.FirstOrDefault(p => p.Name == capability.Method) : null;
            var stepType = capability.Resolution == "mcp" ? "mcp.call" : capability.Resolution == "native" ? capability.Method ?? "" : "set";
            var contract = declaredStepContracts.GetValueOrDefault(stepType);
            capabilities.Add(new PlanningCapability
            {
                Id = "capability_" + capabilities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Description = capability.Description,
                StepType = stepType,
                Server = capability.Server,
                Method = capability.Method,
                Kind = capability.Kind,
                Required = capability.Required,
                DeclarationFingerprint = prompt is not null ? TypedPromptFingerprint(prompt) : tool is null ? null : TypedDeclarationFingerprint(tool),
                EffectKind = capability.ExternalEffectKind ?? (capability.Resolution == "mcp" ? "unknown" : "none"),
                OperationIds = GetResolvedCapabilityOperationIds(capability).ToList(),
                InputSchema = prompt is not null ? TypedPromptInputSchema(prompt) : (tool?.InputSchema?.DeepClone() ?? contract?.InputSchema.DeepClone()) as JsonObject ?? new JsonObject(),
                OutputSchema = prompt is not null ? new JsonObject() : (tool is null ? contract?.OutputSchema.DeepClone() : McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone()) as JsonObject ?? new JsonObject(),
                RequestBindings = capability.RequestBindings.Select(b => new PlanningLiteralBinding(b.Path, b.Value?.DeepClone())).ToList()
            });
        }
        var locked = BuildCapabilityPreflightJson(preflight);
        var stepContracts = new JsonObject(allowed.Select(type => new KeyValuePair<string, JsonNode?>(type,
            declaredStepContracts.GetValueOrDefault(type) is { } contract ? new JsonObject
            {
                ["input"] = contract.InputSchema.DeepClone(), ["output"] = contract.OutputSchema.DeepClone()
            } : null)));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString() + input["policy"]?.ToJsonString() + stepContracts.ToJsonString())));
        return new PlanningPreparation
        {
            Fingerprint = fingerprint,
            LockedContract = locked,
            RuntimeState = state,
            Capabilities = capabilities,
            AllowedStepTypes = allowed,
            StepContracts = stepContracts
        };
    }

    public async Task<IReadOnlyList<PlanningDiagnostic>> ValidateTypedArtifactAsync(
        StepExecutionContext ctx, string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct)
    {
        try
        {
            var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
                ?? throw new InvalidOperationException("The persisted capability contract is missing.");
            var document = ParseAndValidateGeneratedWorkflow(yaml);
            var validate = new JsonObject { ["compile"] = true, ["mode"] = "strict", ["dry_run"] = false };
            await RunStandardPlanValidationSequenceAsync(document, request.Options["policy"] as JsonObject,
                request.Options["limits"] as JsonObject, validate, preflight.DiscoveredServers, ctx, NullTelemetrySpan.Instance, ct);
            ValidateLockedCapabilitiesInDocument(document, preflight);
            return [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ex is Expressions.WorkflowRuntimeException runtime && runtime.Details?["diagnostics"] is JsonArray diagnostics)
                return diagnostics.OfType<JsonObject>().Select(d => new PlanningDiagnostic(
                    d["code"]?.GetValue<string>() ?? runtime.Code,
                    d["location"]?.GetValue<string>() ?? d["step"]?.GetValue<string>() ?? "$",
                    d["message"]?.GetValue<string>() ?? "The generated artifact violates its contract.")).ToArray();
            return [new PlanningDiagnostic(ex is Expressions.WorkflowRuntimeException failure ? failure.Code : "PLANNING_VALIDATION", "$", ex.Message)];
        }
    }

    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateTypedScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct)
    {
        var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
            ?? throw new InvalidOperationException("The persisted capability contract is missing.");
        return WorkflowPlanScenarioValidator.ValidateAsync(WorkflowParser.Parse(yaml), BuildDryRunMcpClientFactory(preflight.DiscoveredServers), ct);
    }

    public async Task<IReadOnlyList<PlanningDiagnostic>> ValidateTypedCatalogAsync(WorkflowEngine engine, PlanningPreparation preparation, CancellationToken ct)
    {
        var diagnostics = new List<PlanningDiagnostic>();
        var currentSteps = engine.Registry.GetContracts();
        foreach (var (type, contract) in preparation.StepContracts)
            if (contract is not null && (!currentSteps.TryGetValue(type, out var current) ||
                !JsonNode.DeepEquals(contract["input"], current.InputSchema) || !JsonNode.DeepEquals(contract["output"], current.OutputSchema)))
                diagnostics.Add(new("CATALOG_CHANGED", type, "A declared step contract changed. Revise the plan before approval."));
        foreach (var group in preparation.Capabilities.Where(c => c.StepType == "mcp.call").GroupBy(c => c.Server, StringComparer.Ordinal))
        {
            if (engine.McpClientFactory is null || group.Key is null) return [new("CATALOG_UNAVAILABLE", "$", "The current capability catalog is unavailable.")];
            await using var session = await engine.McpClientFactory.GetClientAsync(group.Key, ct);
            var tools = await session.ListToolsAsync(ct);
            var prompts = group.Any(c => c.Kind == "prompt") ? await session.ListPromptsAsync(ct) : [];
            foreach (var capability in group)
            {
                var tool = tools.SingleOrDefault(t => t.Name == capability.Method);
                var prompt = prompts.SingleOrDefault(p => p.Name == capability.Method);
                var current = capability.Kind == "prompt" ? (prompt is null ? null : TypedPromptFingerprint(prompt)) : tool is null ? null : TypedDeclarationFingerprint(tool);
                if (current is null || capability.DeclarationFingerprint is null || current != capability.DeclarationFingerprint)
                    diagnostics.Add(new("CATALOG_CHANGED", capability.Id, "A selected capability's declared contract changed. Revise the plan against the current catalog before approval."));
            }
        }
        return diagnostics;
    }

    private static string TypedDeclarationFingerprint(McpToolInfo tool) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["description"] = tool.Description, ["input"] = tool.InputSchema?.DeepClone(), ["output"] = McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone(), ["meta"] = tool.Meta?.DeepClone()
    }.ToJsonString())));

    private static JsonObject TypedPromptInputSchema(McpPromptInfo prompt) => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject((prompt.Arguments ?? []).Select(argument => new KeyValuePair<string, JsonNode?>(argument.Name, new JsonObject { ["type"] = "string", ["description"] = argument.Description }))),
        ["required"] = new JsonArray((prompt.Arguments ?? []).Where(argument => argument.Required).Select(argument => (JsonNode?)JsonValue.Create(argument.Name)).ToArray())
    };
    private static string TypedPromptFingerprint(McpPromptInfo prompt) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.Description + TypedPromptInputSchema(prompt).ToJsonString())));

    private static void RequestTypedCapabilityClarification(CapabilityInventory inventory, CapabilityMatchingEvaluation? evaluation, CapabilityCatalog? catalog)
    {
        var context = BuildCapabilityClarificationContext(inventory, evaluation, catalog);
        var issues = context["issues"]!.AsArray().OfType<JsonObject>().ToList();
        var question = new HumanInputRequest
        {
            StepId = "capability-clarification", Prompt = "Clarify the unresolved behavior before capability planning continues.", Mode = "form", AllowAbandon = true,
            Fields = issues.Select((issue, index) => new HumanInputFieldDef
            {
                Name = "behavior_" + index, Description = issue["description"]!.GetValue<string>(), Type = "text", Required = true, AllowCustomAnswer = true
            }).ToList()
        };
        throw new Expressions.WorkflowRuntimeException("PLANNING_CLARIFICATION_REQUIRED", "Observable behavior needs clarification.",
            details: new JsonObject { ["question"] = JsonSerializer.SerializeToNode(question, PlanningJsonContext.Default.HumanInputRequest) });
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(CapabilityPreflightResult))]
    private partial class TypedContractJsonContext : JsonSerializerContext;
}
