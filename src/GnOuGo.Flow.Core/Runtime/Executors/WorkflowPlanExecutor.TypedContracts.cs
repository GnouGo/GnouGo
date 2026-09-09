using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    internal static void ScopeTypedPreparationFeedback(JsonObject input, PlanningPreparationCheckpoint? checkpoint, JsonNode catalog)
    {
        if (input["planner_version"]?.GetValue<int>() != 2 || input["preparation_feedback"] is not JsonArray { Count: > 0 }) return;
        if (checkpoint is { FeedbackSuperseded: false } && checkpoint.FeedbackCatalogHash == PlanningPreparationCheckpoint.CatalogHash(catalog)) return;
        // Technical findings refer to their assessed producer contracts, never to user
        // intent. Old or unscoped findings cannot establish missing data in a new catalog.
        input.Remove("preparation_feedback");
        if (checkpoint is null || checkpoint.FeedbackSuperseded) return;
        checkpoint.FeedbackSuperseded = true;
        checkpoint.ValidatedResults.Remove("inventory");
        checkpoint.ValidatedResults.Remove("selection");
        checkpoint.ValidatedResults.Remove("matching_candidate");
    }

    internal static string TypedPreparationFeedback(JsonObject input) => input["planner_version"]?.GetValue<int>() == 2 && input["preparation_feedback"] is JsonArray { Count: > 0 } findings
        ? "\nTechnical findings from validation of the previous construction (advisory, NOT user intent evidence):\n" + findings.ToJsonString() +
          "\nReassess required runtime observations and their exact operation ownership. A resource handle is not its contents; local processing cannot obtain absent external data. " +
          "Repair the inventory where needed, retaining every user requirement and answer. Cite only the identified intent sources, never these machine-written findings."
        : "";

    /// <summary>Reuses the established exact-capability inventory and validation boundary.</summary>
    public async Task<PlanningPreparation> PrepareTypedContractsAsync(StepExecutionContext ctx, PlanningRequest request, CancellationToken ct)
    {
        var input = (JsonObject)request.Options.DeepClone();
        input["planner_version"] = 2;
        input["raw_prompt"] = request.Prompt;
        if (request.PreparationFeedback.Count != 0)
            input["preparation_feedback"] = JsonSerializer.SerializeToNode(request.PreparationFeedback, PlanningJsonContext.Default.ListPlanningDiagnostic);
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
                ArtifactContract = tool is null ? null : GetValidatedMcpArtifactContract(tool, capability.Server),
                Activation = capability.Activation,
                CatalogId = capability.CatalogId,
                Resolution = capability.Resolution,
                OperationIds = GetResolvedCapabilityOperationIds(capability).ToList(),
                InputOperationIds = (capability.InputOperationIds ?? []).ToList(),
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
        var preparation = new PlanningPreparation
        {
            Fingerprint = fingerprint,
            LockedContract = locked,
            RuntimeState = state,
            Capabilities = capabilities,
            AllowedStepTypes = allowed,
            StepContracts = stepContracts
        };
        PopulateTypedDecisions(preparation);
        return preparation;
    }

    public async Task<IReadOnlyList<PlanningDiagnostic>> ValidateTypedArtifactAsync(
        StepExecutionContext ctx, string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct,
        IReadOnlyList<PlanningArtifactBinding>? bindings = null)
    {
        var stage = PlanningValidationStage.RuntimeContracts;
        try
        {
            var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
                ?? throw new InvalidOperationException("The persisted capability contract is missing.");
            EnrichTypedPreparation(preparation);
            var document = ParseAndValidateGeneratedWorkflow(yaml);
            var validate = new JsonObject { ["compile"] = true, ["mode"] = "strict", ["dry_run"] = false };
            await RunStandardPlanValidationSequenceAsync(document, request.Options["policy"] as JsonObject,
                request.Options["limits"] as JsonObject, validate, preflight.DiscoveredServers, ctx, NullTelemetrySpan.Instance, ct);
            stage = PlanningValidationStage.CapabilityContracts;
            var owners = bindings is null ? null : ResolveTypedArtifactOwners(document, preflight, preparation, bindings);
            ValidateLockedCapabilitiesInDocument(document, preflight, current => stage = current, owners);
            return [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ex is Expressions.WorkflowRuntimeException runtime && runtime.Details?["diagnostics"] is JsonArray diagnostics)
                return diagnostics.OfType<JsonObject>().Select(d => TypedArtifactDiagnostic(d, runtime.Code, stage)).ToArray();
            if (ex is Expressions.WorkflowRuntimeException detailed && detailed.Details is JsonObject details)
            {
                if (details["redundant_calls"] is JsonArray calls)
                    return calls.OfType<JsonObject>().Select(call => TypedArtifactDiagnostic(new JsonObject
                    {
                        ["step"] = call["step_id"]?.DeepClone(),
                        ["message"] = "This operation invokes an artifact materializer beyond the locked occurrence allowance. Correct the behavior's capability binding; changing arguments cannot turn a producer into another lifecycle action."
                    }, detailed.Code, stage)).ToArray();
                var finding = details.DeepClone().AsObject(); finding["message"] ??= detailed.Message;
                if (details["unavailable_capabilities"] is JsonArray missing)
                    finding["message"] = detailed.Message + "\nMissing obligations: " + string.Join("; ", missing.OfType<JsonObject>().Select(c => c["description"]?.ToString() ?? c["id"]?.ToString()));
                if (details["validation_issue"]?.GetValue<string>() == "conditional_local_decision_inputs_unproven")
                    RetargetTypedDecisionFinding(finding, WorkflowParser.Parse(yaml));
                return [TypedArtifactDiagnostic(finding, detailed.Code, stage)];
            }
            return [new PlanningDiagnostic(ex is Expressions.WorkflowRuntimeException failure ? failure.Code : "PLANNING_VALIDATION", "$", ex.Message, ValidationStage: stage)];
        }
    }

    private static IReadOnlyDictionary<StepDef, ResolvedCapability> ResolveTypedArtifactOwners(WorkflowDocument document,
        CapabilityPreflightResult preflight, PlanningPreparation preparation, IReadOnlyList<PlanningArtifactBinding> bindings)
    {
        var calls = document.Workflows.SelectMany(w => EnumerateSteps(w.Value.Steps).Concat(EnumerateSteps(w.Value.Finally))
            .Select(n => (Workflow: w.Key, Step: n))).ToArray();
        var owners = new Dictionary<StepDef, ResolvedCapability>(ReferenceEqualityComparer.Instance);
        foreach (var binding in bindings)
        {
            var nodes = calls.Where(c => c.Workflow == binding.Workflow && c.Step.Id == binding.Step).ToArray();
            var declared = preparation.Capabilities.Where(c => c.Id == binding.CapabilityId).ToArray();
            var matches = declared.Length == 1 ? preflight.Capabilities.Where(c => c.Resolution == declared[0].Resolution &&
                c.CatalogId == declared[0].CatalogId && c.Server == declared[0].Server && c.Kind == declared[0].Kind && c.Method == declared[0].Method &&
                GetResolvedCapabilityOperationIds(c).ToHashSet(StringComparer.Ordinal).SetEquals(declared[0].OperationIds) &&
                c.RequestBindings.Count == declared[0].RequestBindings.Count && c.RequestBindings.All(b => declared[0].RequestBindings.Any(d => d.Path == b.Path && JsonNode.DeepEquals(d.Value, b.Value)))).ToArray() : [];
            if (nodes.Length != 1 || matches.Length != 1 || owners.ContainsKey(nodes[0].Step) ||
                matches[0].Resolution != "local" && nodes[0].Step.Type != declared[0].StepType || matches[0].Resolution == "mcp" &&
                !McpStepMatchesCapability(nodes[0].Step, matches[0].Server!, matches[0].Kind!, matches[0].Method!, matches[0].RequestBindings))
                throw Invalid(binding.Workflow, binding.Step);
            owners.Add(nodes[0].Step, matches[0]);
        }
        if (calls.FirstOrDefault(c => c.Step.Type == "mcp.call" && !owners.ContainsKey(c.Step)) is { Step: not null } missing)
            throw Invalid(missing.Workflow, missing.Step.Id);
        return owners;

        static Expressions.WorkflowRuntimeException Invalid(string workflow, string step) => new("ARTIFACT_OWNERSHIP_INVALID",
            "Every executable MCP call must have one compiler-derived owner matching its locked capability and request bindings.",
            details: new JsonObject { ["workflow"] = workflow, ["step"] = step });
    }

    internal static void RetargetTypedDecisionFinding(JsonObject finding, WorkflowDocument document)
    {
        if (finding["decision_field"] is not JsonValue field || !field.TryGetValue<string>(out var name)) return;
        var producers = document.Workflows.SelectMany(w => EnumerateSteps(w.Value.Steps).Concat(EnumerateSteps(w.Value.Finally))
            .Where(n => n.Type == "decision.evaluate" && n.Input?["decisions"] is JsonObject fields && fields.ContainsKey(name))
            .Select(n => (Workflow: w.Key, Node: n))).ToArray();
        if (producers.Length != 1) return;
        finding["workflow"] = producers[0].Workflow; finding["step"] = producers[0].Node.Id; finding["field"] = "input.decisions." + name;
        finding["hint"] = "Repair this decision producer's conditions. Bind declared upstream results directly or through provable field projections; an opaque computed object cannot establish which result field depends on an input. Preserve every required permission.";
    }

    internal static PlanningDiagnostic TypedArtifactDiagnostic(JsonObject diagnostic, string fallbackCode, string stage)
    {
        string? Read(string key) => diagnostic[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var parts = new List<string>();
        if (Read("workflow") is { } workflow) parts.Add("workflow:" + workflow);
        if ((Read("step") ?? Read("consumer_step") ?? Read("switch_id")) is { } step) parts.Add("step:" + step);
        var targetField = Read("field") ?? (Read("request_pointer") is { } pointer ? "input.request" + pointer.Replace('/', '.') : null);
        if (targetField is null && Read("switch_id") is not null && Read("validation_issue") == "conditional_decision_lineage_unproven") targetField = "expr";
        if (targetField is { } field) parts.Add("field:" + field);
        var message = Read("message") ?? Read("expected") ?? Read("reason") ?? "The generated artifact violates its contract.";
        if (Read("artifact_kind") is { } kind) message = "Required artifact '" + kind + "': " + message;
        if (Read("invalid_path") is { Length: > 0 } invalidPath) message += "\nInvalid reference: " + invalidPath;
        if (diagnostic["allowed_paths"] is JsonArray paths && paths.Count > 0) message += "\nAllowed paths: " + paths.ToJsonString();
        if ((Read("hint") ?? Read("llm_guidance")) is { Length: > 0 } guidance) message += "\nRepair: " + guidance;
        if (Read("validation_issue") is { } issue) message += "\nContract finding: " + issue;
        if (Read("decision_field") is { } decisionField) message += "\nDeclared decision field: " + decisionField;
        return new(Read("code") ?? fallbackCode, Read("location") ?? (parts.Count == 0 ? "$" : string.Join("/", parts)), message, ValidationStage: stage);
    }

    private static async Task SaveTypedPreparationResultAsync(StepExecutionContext ctx, string stage, JsonNode? value, CancellationToken ct)
    {
        if (ctx.PreparationCheckpoint is not { } checkpoint || value is null) return;
        checkpoint.ValidatedResults[stage] = value.DeepClone(); checkpoint.Stage = stage;
        if (ctx.PersistPreparation is not null) await ctx.PersistPreparation(ct);
    }

    private static JsonObject BuildTypedCapabilityMatchingSchema(CapabilityInventory inventory, CapabilityCatalog catalog)
    {
        var schema = BuildCapabilityMatchingSchema();
        var operation = schema["properties"]!["operation_matches"]!["items"]!.DeepClone().AsObject();
        var constraint = schema["properties"]!["constraint_matches"]!["items"]!.DeepClone().AsObject();
        var ids = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(catalog.Entries.Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray()) };
        schema["$defs"] = new JsonObject { ["catalogId"] = ids };
        JsonObject Entry(JsonObject template, string idField)
        {
            var entry = template.DeepClone().AsObject(); entry["properties"]!.AsObject().Remove(idField);
            entry["required"] = new JsonArray(entry["required"]!.AsArray().Where(n => n?.ToString() != idField).Select(n => n!.DeepClone()).ToArray());
            foreach (var pair in entry["properties"]!.AsObject().Where(p => p.Key.EndsWith("catalog_ids", StringComparison.Ordinal)))
                pair.Value!["items"] = new JsonObject { ["$ref"] = "#/$defs/catalogId" };
            return entry;
        }
        static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
        static JsonObject Object(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false };
        var operations = new JsonObject();
        foreach (var item in inventory.Operations)
        {
            var statuses = item.ExecutionKind == "local_processing" ? new[] { "local" }
                : item.DecisionSourceOperationId.Length == 0 ? new[] { "matched", "composed", "unavailable" } : new[] { "conditional", "unavailable" };
            var variants = new JsonArray();
            foreach (var status in statuses)
            {
                var entry = Entry(operation, "operation_id"); var properties = entry["properties"]!.AsObject();
                properties["status"] = Enum(status);
                properties["decision_operation_id"] = Enum(status == "conditional" ? item.DecisionSourceOperationId : "");
                properties["conditional_mode"] = status != "conditional" ? Enum("") : item.AllowNoEffectOutcome ? Enum("exactly_one", "all_on_value") : Enum("exactly_one");
                properties["candidate_catalog_ids"]!["maxItems"] = 0;
                var selected = properties["catalog_ids"]!;
                selected["minItems"] = status is "matched" or "conditional" ? 1 : status == "composed" ? 2 : 0;
                selected["maxItems"] = status == "matched" ? 1 : status is "composed" or "conditional" ? 16 : 0;
                variants.Add((JsonNode)entry);
            }
            operations[item.Id] = variants.Count == 1 ? variants[0]!.DeepClone() : new JsonObject { ["anyOf"] = variants };
        }

        schema["properties"]!["operation_matches"] = Object(operations);
        schema["properties"]!["constraint_matches"] = Object(new JsonObject(inventory.Constraints.Select(c =>
            new KeyValuePair<string, JsonNode?>(c.Id, Entry(constraint, "constraint_id")))));
        return schema;
    }

    private static JsonObject TypedMatchingCandidate(CapabilityMatchingEvaluation evaluation) => new()
    {
        ["operation_matches"] = new JsonObject(evaluation.OperationMatches.Select(m => new KeyValuePair<string, JsonNode?>(m.Operation.Id, new JsonObject
        {
            ["status"] = m.Status, ["reason"] = m.Reason,
            ["catalog_ids"] = new JsonArray(m.CatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(m.CandidateCatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["decision_operation_id"] = m.DecisionOperationId ?? "", ["conditional_mode"] = m.ConditionalActivationMode
        }))),
        ["constraint_matches"] = new JsonObject(evaluation.ConstraintMatches.Select(m => new KeyValuePair<string, JsonNode?>(m.Constraint.Id, new JsonObject
        {
            ["status"] = m.Status, ["reason"] = m.Reason,
            ["denied_catalog_ids"] = new JsonArray(m.DeniedCatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(m.CandidateCatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        })))
    };

    private static void PopulateTypedDecisions(PlanningPreparation preparation)
    {
        preparation.DecisionContractVersion = PlanningDecisionContract.CurrentVersion;
        preparation.Decisions.Clear(); preparation.Interactions.Clear();
        foreach (var group in preparation.Capabilities.Where(c => c.Activation is not null).GroupBy(c => c.Activation!.Group))
        {
            var activation = group.First().Activation!;
            var source = preparation.Capabilities.Single(c => c.CatalogId == activation.DecisionProducerCatalogId && c.OperationIds.Contains(activation.DecisionOperationId));
            var confirmation = activation.DecisionContractSource == PlanningDecisionContract.HumanConfirmation;
            preparation.Decisions.Add(new()
            {
                Group = group.Key, SourceOperationId = activation.DecisionOperationId, SourceCapabilityId = source.Id,
                SourcePointer = activation.DecisionOutputPath, ContractSource = activation.DecisionContractSource,
                ResponseSchema = confirmation ? new JsonObject { ["type"] = "boolean" } : new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(activation.AllowedValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) },
                AllowedValues = activation.AllowedValues.ToList(), NoEffectValues = activation.NoEffectValues.ToList(),
                EffectOperationIds = group.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal).ToList(), InputOperationIds = activation.DecisionInputOperationIds.ToList(),
                PermissionOperationIds = confirmation ? [activation.DecisionOperationId] : activation.DecisionInputOperationIds.Where(id =>
                    preparation.Capabilities.Any(c => c.StepType == "human.input" && c.OperationIds.Contains(id))).ToList()
            });
            if (confirmation)
            {
                var interaction = new PlanningInteractionContract { OperationId = activation.DecisionOperationId, CapabilityId = source.Id };
                source.OutputSchema = interaction.OutputSchema.DeepClone().AsObject();
                if (!preparation.Interactions.Any(i => i.OperationId == interaction.OperationId)) preparation.Interactions.Add(interaction);
            }
        }
    }

    public static void EnrichTypedPreparation(PlanningPreparation preparation)
    {
        var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
            ?? throw new InvalidOperationException("The persisted capability contract is missing.");
        foreach (var capability in preparation.Capabilities)
        {
            var matches = preflight.Capabilities.Where(c => c.Resolution != "unavailable" && c.Server == capability.Server && c.Method == capability.Method && c.Kind == capability.Kind &&
                GetResolvedCapabilityOperationIds(c).ToHashSet(StringComparer.Ordinal).SetEquals(capability.OperationIds) && c.RequestBindings.Count == capability.RequestBindings.Count &&
                c.RequestBindings.All(b => capability.RequestBindings.Any(p => p.Path == b.Path && JsonNode.DeepEquals(p.Value, b.Value)))).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The locked capability metadata is missing or ambiguous.");
            capability.InputOperationIds = (matches[0].InputOperationIds ?? []).ToList();
            capability.Activation = matches[0].Activation; capability.CatalogId = matches[0].CatalogId; capability.Resolution = matches[0].Resolution;
            var tool = preflight.DiscoveredServers.FirstOrDefault(s => s.Name == capability.Server)?.Tools.FirstOrDefault(t => t.Name == capability.Method);
            capability.ArtifactContract = tool is null ? null : GetValidatedMcpArtifactContract(tool, capability.Server);
        }
    }

    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateTypedScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct, JsonObject? inputs = null, JsonObject? loopItemSchemas = null)
        => ValidateTypedScenariosAsync(yaml, preparation, ct, inputs, loopItemSchemas, null);

    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateTypedScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct, JsonObject? inputs, JsonObject? loopItemSchemas, JsonObject? observations)
    {
        var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
            ?? throw new InvalidOperationException("The persisted capability contract is missing.");
        return WorkflowPlanScenarioValidator.ValidateAsync(WorkflowParser.Parse(yaml), BuildDryRunMcpClientFactory(preflight.DiscoveredServers), ct, inputs, loopItemSchemas, observations);
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
    [JsonSerializable(typeof(CapabilityInventory))]
    [JsonSerializable(typeof(List<McpServerDiscovery>))]
    [JsonSerializable(typeof(CapabilityMatchingEvaluation))]
    [JsonSerializable(typeof(CapabilityPreflightResult))]
    private partial class TypedContractJsonContext : JsonSerializerContext;
}
