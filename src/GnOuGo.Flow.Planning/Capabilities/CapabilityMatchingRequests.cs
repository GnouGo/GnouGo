using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Capabilities;

/// <summary>Matches only recorded candidates for each operation; the combined result still passes global validation.</summary>
internal static class CapabilityMatchingRequests
{
    internal sealed record Request(string Owner, string Prompt, JsonObject Schema);

    internal static async Task<LLMResponse> CallAsync(StepExecutionContext ctx, ILLMClient client, CapabilityInventory inventory,
        CapabilityCatalog catalog, IReadOnlyList<McpServerDiscovery> discovery, string? provider, string model, string reasoning, CancellationToken ct, CapabilityMatchingEvaluation? previous = null)
    {
        var selection = ctx.PreparationCheckpoint?.ValidatedResults["physical_candidates"] is { } retained
            ? JsonSerializer.Deserialize(retained, TypedContractJsonContext.Default.PhysicalCandidateSelection) : null;
        var scopes = CandidateScopes(inventory, catalog, discovery, selection);
        var requests = Build(inventory, catalog, scopes, ctx.PlanningGeneration?.MaxInputTokensPerRequest ?? 12000, previous);
        var combined = previous is null ? new JsonObject { ["operation_matches"] = new JsonObject(), ["constraint_matches"] = new JsonObject() }
            : CapabilityMatchAssessment.TypedMatchingCandidate(previous);
        foreach (var operation in inventory.Operations.Where(o => o.ExecutionKind == "local_processing"))
            combined["operation_matches"]![operation.Id] = new JsonObject
            {
                ["status"] = "local", ["reason"] = "Validated local operation.", ["catalog_ids"] = new JsonArray(),
                ["candidate_catalog_ids"] = new JsonArray(), ["decision_operation_id"] = "", ["conditional_mode"] = ""
            };
        foreach (var constraint in inventory.Constraints.Where(c => c.EnforcementKind == "workflow_policy" || CapabilityMatchAssessment.NoPhysicalAuthority(inventory, catalog)))
            combined["constraint_matches"]![constraint.Id] = new JsonObject
            {
                ["status"] = constraint.EnforcementKind == "workflow_policy" ? "policy_only" : "enforced",
                ["reason"] = constraint.EnforcementKind == "workflow_policy" ? "Validated workflow policy." : "The physical capability allowlist is empty.",
                ["denied_catalog_ids"] = new JsonArray(), ["candidate_catalog_ids"] = new JsonArray()
            };
        foreach (var request in requests)
        {
            var key = "matching_scope_" + PlanningGraphCompiler.Fingerprint(request.Prompt + request.Schema.ToJsonString());
            var candidate = ctx.PreparationCheckpoint?.ValidatedResults[key] as JsonObject;
            if (candidate is null)
            {
                var response = await ctx.CallLLMAsync(client, new LLMRequest
                {
                    Provider = provider, Model = model, Reasoning = reasoning, UseBackgroundMode = true,
                    Prompt = request.Prompt, StructuredOutputSchema = request.Schema, StructuredOutputStrict = true
                }, previous is null ? "workflow.plan.capability_matching" : "workflow.plan.capability_matching_repair", ct);
                candidate = CapabilityInventoryValidation.ParseStructuredObject(response, "scoped capability matching");
            }
            if (PlanningContractValidation.ValidateInstance(candidate, request.Schema).Count != 0)
                throw new WorkflowRuntimeException("CAPABILITY_MATCH_SCOPE_INVALID", $"Capability matching for '{request.Owner}' did not match its recorded candidate scope.");
            await PlanningArtifactValidation.SaveTypedPreparationResultAsync(ctx, key, candidate, ct);
            foreach (var field in new[] { "operation_matches", "constraint_matches" })
                foreach (var (id, value) in candidate[field]!.AsObject()) combined[field]![id] = value!.DeepClone();
        }
        return new() { Json = combined };
    }

    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> CandidateScopes(CapabilityInventory inventory, CapabilityCatalog catalog,
        IReadOnlyList<McpServerDiscovery> discovery, PhysicalCandidateSelection? selection)
    {
        var physical = CapabilityDiscovery.BuildPhysicalCapabilityCatalog(discovery);
        var scopes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var operation in inventory.Operations.Where(o => o.ExecutionKind == "external_effect"))
        {
            if (selection is null || !selection.OperationCandidates.TryGetValue(operation.Id, out var ids))
                throw new WorkflowRuntimeException("CAPABILITY_SELECTION_REQUIRED", $"Operation '{operation.Id}' needs a recorded physical candidate selection before matching.");
            scopes[operation.Id] = Expand(ids);
        }
        foreach (var constraint in inventory.Constraints.Where(c => c.EnforcementKind == "exact_denial"))
        {
            if (selection?.ConstraintCandidates.TryGetValue(constraint.Id, out var ids) == true) scopes[constraint.Id] = Expand(ids, false);
            else if (inventory.Operations.All(o => o.ExecutionKind != "external_effect")) scopes[constraint.Id] = new HashSet<string>(StringComparer.Ordinal);
            else throw new WorkflowRuntimeException("CAPABILITY_SELECTION_REQUIRED", $"Constraint '{constraint.Id}' needs a recorded physical candidate selection before matching.");
        }
        return scopes;

        IReadOnlySet<string> Expand(IReadOnlyList<string> ids, bool prerequisites = true)
        {
            if (ids.Any(id => physical.Entries.All(e => e.Id != id)))
                throw new WorkflowRuntimeException("CAPABILITY_SELECTION_REQUIRED", "The recorded physical candidates do not belong to the current catalog.");
            var selected = CapabilityDiscovery.FilterDiscoveryToPhysicalEntries(discovery, physical.Entries.Where(e => ids.Contains(e.Id, StringComparer.Ordinal)).ToArray());
            if (prerequisites)
            {
                selected = CapabilityPrerequisites.ExpandSelectedOperationalArtifactPrerequisites(selected, discovery);
                selected = CapabilityDiscovery.ExpandSelectedCompositionWrappers(selected, discovery);
            }
            var keys = selected.SelectMany(s => s.Tools.Select(t => (s.Name, Kind: "tool", t.Name))
                .Concat(s.Prompts.Select(p => (s.Name, Kind: "prompt", p.Name)))).ToHashSet();
            return catalog.Entries.Where(e => keys.Contains((e.Server!, e.Kind!, e.Method))).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        }
    }

    internal static IReadOnlyList<Request> Build(CapabilityInventory inventory, CapabilityCatalog catalog,
        IReadOnlyDictionary<string, IReadOnlySet<string>> scopes, int inputCeiling, CapabilityMatchingEvaluation? previous = null)
    {
        var requests = new List<Request>();
        var affected = previous is null ? null : previous.OperationMatches.Where(m => m.Status is not ("matched" or "composed" or "conditional" or "local"))
            .Select(m => m.Operation.Id).Concat(previous.ConstraintMatches.Where(m => m.Status is not ("enforced" or "policy_only")).Select(m => m.Constraint.Id))
            .Concat(CapabilityMatchRecovery.GetDependencyUnlockedDecisionOperationIds(previous)).ToHashSet(StringComparer.Ordinal);
        var native = catalog.Entries.Where(e => e.Resolution != "mcp").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var interactions = inventory.Operations.Where(o => o.ExecutionKind == "human_interaction" && (affected is null || affected.Contains(o.Id))).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (interactions.Count > 0) Add("$interaction", interactions, new HashSet<string>(StringComparer.Ordinal), native);
        foreach (var operation in inventory.Operations.Where(o => o.ExecutionKind == "external_effect" && (affected is null || affected.Contains(o.Id))))
            Add(operation.Id, new HashSet<string>([operation.Id], StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), scopes[operation.Id].Concat(native).ToHashSet(StringComparer.Ordinal));
        foreach (var constraint in inventory.Constraints.Where(c => c.EnforcementKind == "exact_denial" && !CapabilityMatchAssessment.NoPhysicalAuthority(inventory, catalog) && (affected is null || affected.Contains(c.Id))))
            Add(constraint.Id, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>([constraint.Id], StringComparer.Ordinal), scopes[constraint.Id].Concat(native).ToHashSet(StringComparer.Ordinal));
        return requests;

        void Add(string owner, IReadOnlySet<string> operations, IReadOnlySet<string> constraints, IReadOnlySet<string> ids)
        {
            var entries = catalog.Entries.Where(e => ids.Contains(e.Id)).ToArray();
            var scopedCatalog = new CapabilityCatalog(entries, string.Join('\n', entries.Select(e => e.Id + " " + e.Card)));
            var schema = CapabilityMatchAssessment.BuildTypedCapabilityMatchingSchema(inventory, scopedCatalog);
            foreach (var (field, targets) in new[] { ("operation_matches", operations), ("constraint_matches", constraints) })
            {
                var fields = schema["properties"]![field]!["properties"]!.AsObject();
                foreach (var key in fields.Select(p => p.Key).Where(k => !targets.Contains(k)).ToArray()) fields.Remove(key);
                schema["properties"]![field]!["required"] = new JsonArray(fields.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            }
            PlanningJsonTransport.PruneDefinitions(schema);
            var scoped = inventory with
            {
                Operations = inventory.Operations.Where(o => operations.Contains(o.Id)).ToArray(),
                Constraints = inventory.Constraints.Where(c => constraints.Contains(c.Id)).ToArray()
            };
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(scoped.Operations.SelectMany(o => o.InputOperationIds.Append(o.DecisionSourceOperationId)));
            var byId = inventory.Operations.ToDictionary(o => o.Id, StringComparer.Ordinal);
            while (pending.TryPop(out var id))
            {
                if (!dependencies.Add(id) || !byId.TryGetValue(id, out var upstream)) continue;
                foreach (var input in upstream.InputOperationIds.Append(upstream.DecisionSourceOperationId)) pending.Push(input);
            }
            var evidence = new JsonObject(inventory.Operations.Where(o => dependencies.Contains(o.Id) && !operations.Contains(o.Id))
                .Select(o => new KeyValuePair<string, JsonNode?>(o.Id, new JsonObject { ["description"] = o.Description, ["execution_kind"] = o.ExecutionKind, ["inputs"] = new JsonArray(o.InputOperationIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) })));
            var prompt = CapabilityInventoryContext.BuildCapabilityMatchingPrompt(scoped, scopedCatalog) + "\nDeclared upstream operations:\n" + evidence.ToJsonString();
            if (previous is not null)
            {
                var retained = CapabilityMatchAssessment.TypedMatchingCandidate(previous);
                var current = new JsonObject();
                foreach (var id in operations) current[id] = retained["operation_matches"]![id]!.DeepClone();
                foreach (var id in constraints) current[id] = retained["constraint_matches"]![id]!.DeepClone();
                var diagnostics = new JsonArray(previous.Issues.Where(i => operations.Contains(i.OperationId) || constraints.Contains(i.OperationId))
                    .Select(i => (JsonNode)new JsonObject { ["owner"] = i.OperationId, ["code"] = i.ReasonCode, ["fields"] = new JsonArray(i.InvalidFields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()), ["message"] = i.Reason }).ToArray());
                prompt += "\nRepair these retained decisions only:\n" + current.ToJsonString() + "\nDiagnostics:\n" + diagnostics.ToJsonString();
            }
            var tokens = PlanningJsonTransport.EstimateInputTokens(prompt, schema);
            if (tokens > inputCeiling)
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", $"Capability matching for '{owner}' needs approximately {tokens} input tokens; the ceiling is {inputCeiling}. Narrow this operation or its capability candidates. No matching request was dispatched.");
            requests.Add(new(owner, prompt, schema));
        }
    }

}
