using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    private async Task<bool> AssessBehaviorRevisionAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.PreviousGraph is null || state.Feedback is null) return false;
        var findings = state.Attempts.LastOrDefault(a => a.Phase == "semantic_review" &&
            a.CandidateHash == PlanningGraphCompiler.Fingerprint(state.PreviousGraph))?.Diagnostics;
        if (findings is null) return false;
        var locations = state.PreviousGraph.Workflows.SelectMany((w, i) =>
            PlanningGraphValidation.Located(w.Steps, "/workflows/" + i + "/steps")
            .Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + i + "/finally"))
            .Select(n => (Workflow: w, n.Node, n.Path))).ToArray();
        var targets = locations.Where(n => findings.Any(d => d.Required && d.Location == n.Path + "/behavior")).ToArray();
        // Broad workflow changes still need complete behavior assessment. Focused
        // findings can preserve every unrelated operation and avoid whole-plan regeneration.
        if (targets.Length == 0 || findings.Any(d => d.Required && d.Location.EndsWith("/behavior", StringComparison.Ordinal) &&
            !targets.Any(n => d.Location == n.Path + "/behavior"))) return false;
        var baseline = state.BehaviorRevisionSource ??= PlanningBehaviorRevisions.Inspect(state.PreviousGraph);
        var ownedNodes = targets.SelectMany(t => PlanningBehaviorPlans.Enumerate([PlanningBehaviorPlans.Enumerate(
            baseline.Workflows.Single(w => w.Key == t.Workflow.Key).Steps.Concat(baseline.Workflows.Single(w => w.Key == t.Workflow.Key).Finally)).Single(n => n.Key == t.Node.Key)])).ToArray();
        var ownedIds = ownedNodes.Select(n => n.CapabilityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var operations = ownedNodes.SelectMany(n => n.OperationIds).ToHashSet(StringComparer.Ordinal);
        var scoped = new PlanningPreparation { Capabilities = state.Preparation!.Capabilities.Where(c => ownedIds.Contains(c.Id) || c.OperationIds.Any(operations.Contains)).ToList() };
        var incoming = scoped.Capabilities.SelectMany(c => c.InputOperationIds).Where(id => !operations.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var schema = PlanningBehaviorRevisions.Schema(scoped, baseline, targets.Select(t => (t.Workflow.Key, t.Node.Key)));
        var prompt = "Revise only the named behavior nodes to address the evidenced coverage findings. " +
            "Use wrap_loop to repeat an existing operation; supply an empty loop node and the host retains the original child. " +
            "Use replace for a changed subtree. Preserve original node keys, capabilities, business inputs, ownership, effects, confirmations and cleanup. " +
            "Only behavior descriptions belong here; executable code and schemas belong to later construction. " +
            "This candidate will require new human behavior approval. Return only the supplied revision fields.\nRequest and answers:\n" + Context(state) +
            "\nRequired coverage findings:\n" + state.Feedback +
            "\nTarget nodes:\n" + new JsonArray(targets.Select(t => (JsonNode)JsonSerializer.SerializeToNode(
                PlanningBehaviorPlans.Enumerate(baseline.Workflows.Single(w => w.Key == t.Workflow.Key).Steps.Concat(baseline.Workflows.Single(w => w.Key == t.Workflow.Key).Finally)).Single(n => n.Key == t.Node.Key),
                PlanningJsonContext.Default.PlanningBehaviorNode)!).ToArray()).ToJsonString() +
            "\nExisting node keys (do not duplicate):\n" + string.Join(", ", baseline.Workflows.SelectMany(w => PlanningBehaviorPlans.Enumerate(w.Steps.Concat(w.Finally))).Select(n => n.Key)) +
            "\nBusiness inputs:\n" + string.Join(", ", baseline.Workflows.SelectMany(w => w.Inputs).Select(p => p.Name)) +
            "\nOwned capability contracts:\n" + BehaviorCapabilities(scoped) +
            "\nIncoming operation boundaries (existing producers outside the revision):\n" + new JsonArray(state.Preparation.Capabilities.Where(c => c.OperationIds.Any(incoming.Contains))
                .Select(c => (JsonNode)new JsonObject { ["operationIds"] = new JsonArray(c.OperationIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["description"] = c.Description }).ToArray()).ToJsonString();
        var diagnostics = new List<PlanningDiagnostic>();
        while (state.BehaviorAssessmentCalls < 2)
        {
            var actual = prompt + (state.BehaviorRevisionPatch is null ? "" : "\nRepair this revision only:\n" + state.BehaviorRevisionPatch.ToJsonString()) +
                (diagnostics.Count == 0 ? "" : "\nDiagnostics:\n" + JsonSerializer.Serialize(diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
            var estimatedTokens = PlanningConstruction.EstimateInputTokens(actual, schema);
            if (estimatedTokens > state.Request.Generation.MaxInputTokensPerUnit)
            { diagnostics = [new("BEHAVIOR_REVISION_CONTEXT_TOO_LARGE", "/behavior", "The focused behavior revision needs " + estimatedTokens + " estimated input tokens; its limit is " + state.Request.Generation.MaxInputTokensPerUnit + ". No request was sent.")]; break; }
            var generator = state.Request.Options["generator"];
            var response = await runtime.CallAsync(PlanningGenerationPolicy.Apply(new LLMRequest
            {
                Prompt = actual, Model = generator?["model"]?.GetValue<string>() ?? "", Provider = generator?["provider"]?.GetValue<string>(),
                StructuredOutputSchema = schema, StructuredOutputStrict = true, UseBackgroundMode = true
            }, state.Request.Generation), "behavior_revision", ct);
            state.BehaviorAssessmentCalls++;
            state.BehaviorRevisionPatch = response.Json as JsonObject;
            diagnostics = response.CompletionStatus == "output_limit"
                ? [new("MODEL_OUTPUT_LIMIT", "/behavior", "The focused behavior revision reached its configured output ceiling without a complete candidate. No executable change was accepted.")]
                : PlanningContractValidation.ValidateInstance(state.BehaviorRevisionPatch, schema).Select(e => new PlanningDiagnostic("BEHAVIOR_SCHEMA_INVALID", "/behavior", e)).ToList();
            if (diagnostics.Count == 0)
            {
                try
                {
                    var revised = PlanningBehaviorRevisions.Apply(baseline, state.BehaviorRevisionPatch!);
                    PlanningBehaviorPlans.CompleteOwnership(revised, state.Preparation!);
                    diagnostics.AddRange(PlanningBehaviorPlans.Validate(revised, state.Preparation!));
                    if (diagnostics.Count == 0)
                    { state.BehaviorRevisionSource = null; state.BehaviorRevisionPatch = null; ReadyForBehaviorReview(state, revised); return true; }
                }
                catch (InvalidOperationException ex) { diagnostics.Add(new("BEHAVIOR_PATCH_INVALID", "/behavior", ex.Message)); }
            }
            state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(response.Json?.ToJsonString() ?? response.Text), "behavior_revision", 0, false, diagnostics.ToList()));
            await runtime.CheckpointAsync(state, ct);
            if (response.CompletionStatus == "output_limit") break;
        }
        state.Status = PlanningStatus.Recovery; state.Diagnostics = diagnostics;
        state.Diagnostics.Add(new("BEHAVIOR_REVISION_PAUSED", "/behavior", "The behavior revision is retained for review or targeted repair; no new behavior was approved."));
        return true;
    }
}

internal static class PlanningBehaviorRevisions
{
    internal static JsonObject Schema(PlanningPreparation preparation, PlanningBehaviorPlan baseline, IEnumerable<(string Workflow, string Node)> targets)
    {
        var keys = targets.Select(t => PlanningSchemaReferences.Escape(t.Workflow) + "/" + PlanningSchemaReferences.Escape(t.Node)).Distinct(StringComparer.Ordinal).ToArray();
        var schema = PlanningSchemas.BehaviorRepair(preparation, baseline);
        schema["properties"] = new JsonObject(keys.Distinct(StringComparer.Ordinal).Select(key => new KeyValuePair<string, JsonNode?>(key, new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("action", "node"),
            ["properties"] = new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("replace", "wrap_loop") }, ["node"] = new JsonObject { ["$ref"] = "#/$defs/behaviorNode" } }
        })));
        schema["required"] = new JsonArray(keys.Distinct(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        PlanningConstruction.PruneDefinitions(schema);
        return schema;
    }

    internal static PlanningBehaviorPlan Apply(PlanningBehaviorPlan baseline, JsonObject patch)
    {
        var result = JsonSerializer.Deserialize(JsonSerializer.Serialize(baseline, PlanningJsonContext.Default.PlanningBehaviorPlan), PlanningJsonContext.Default.PlanningBehaviorPlan)!;
        foreach (var (coordinate, value) in patch)
        {
            var parts = coordinate.Split('/');
            if (parts.Length != 2) throw new InvalidOperationException("A behavior patch needs an exact workflow/node coordinate.");
            static string Decode(string value) => value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            var workflowKey = Decode(parts[0]); var key = Decode(parts[1]);
            var node = JsonSerializer.Deserialize(value!["node"]!, PlanningJsonContext.Default.PlanningBehaviorNode)!;
            var action = value["action"]!.GetValue<string>(); var applied = 0;
            foreach (var workflow in result.Workflows.Where(w => w.Key == workflowKey)) { ApplyIn(workflow.Steps); ApplyIn(workflow.Finally); }
            if (applied != 1) throw new InvalidOperationException("A behavior patch must identify exactly one existing node.");
            void ApplyIn(List<PlanningBehaviorNode> nodes)
            {
                for (var i = 0; i < nodes.Count; i++)
                {
                    var current = nodes[i];
                    if (current.Key == key)
                    {
                        if (action == "wrap_loop")
                        {
                            if (node.Kind != "loop" || node.Key == key || node.Steps.Count != 0 || node.CapabilityId is not null || node.Outcomes.Count != 0)
                                throw new InvalidOperationException("A loop wrapper needs a distinct key, empty children and no action capability. The host preserves the original operation.");
                            node.Steps.Add(current);
                        }
                        else if (action != "replace" || node.Key != key) throw new InvalidOperationException("A replacement must preserve its target key.");
                        nodes[i] = node; applied++; continue;
                    }
                    ApplyIn(current.Steps); foreach (var outcome in current.Outcomes) ApplyIn(outcome.Steps);
                }
            }
        }
        return result;
    }

    // Older snapshots retain the executable baseline but not its behavior source.
    // This projection is a proposal only; validation and new approval remain mandatory.
    internal static PlanningBehaviorPlan Inspect(PlanningGraph graph) => new()
    {
        Summary = graph.Summary, Entrypoint = graph.Entrypoint,
        Workflows = graph.Workflows.Select(w => new PlanningBehaviorWorkflow
        {
            Key = w.Key, Purpose = w.Purpose, OperationIds = w.OperationIds.ToList(),
            Inputs = w.Inputs.Select(p => new PlanningBehaviorPort(p.Name, p.Schema.Description ?? p.Name, p.Required)).ToList(),
            Outputs = w.Outputs.Select(p => new PlanningBehaviorPort(p.Name, p.Schema.Description ?? p.Name, true)).ToList(),
            Steps = w.Steps.Select(n => Node(w, n)).ToList(), Finally = w.Finally.Select(n => Node(w, n)).ToList()
        }).ToList()
    };

    private static PlanningBehaviorNode Node(PlanningWorkflow workflow, PlanningNode node)
    {
        if (node.Cases.Any(c => c.Value is null) || node.Branches.Any(b => b.Steps.Count != 1) || node.If is not null)
            throw new InvalidOperationException("The retained behavior cannot be projected without an explicit review of its control flow.");
        var kind = node.Type switch { "switch" => "decision", "loop.sequential" => "loop", "human.input" => "confirmation", "workflow.call" => "workflow", "parallel" => "parallel", "sequence" => "sequence", _ => "operation" };
        return new()
        {
            Key = node.Key, Kind = kind, Purpose = string.IsNullOrWhiteSpace(node.Purpose) ? node.Key : node.Purpose, CapabilityId = node.CapabilityId, OperationIds = node.OperationIds.ToList(),
            InputDependencies = PlanningDataflow.BusinessInputs(workflow, node).ToList(),
            WorkflowKey = kind == "workflow" ? node.Input.Members.Single(m => m.Name == "ref").Value.Source : null,
            Steps = (kind == "parallel" ? node.Branches.SelectMany(b => b.Steps) : node.Steps).Select(n => Node(workflow, n)).ToList(),
            Outcomes = kind == "decision" ? node.Cases.Select(c => new PlanningBehaviorOutcome(c.Value!, c.Value!, false, c.Steps.Select(n => Node(workflow, n)).ToList()))
                .Append(new("default", "No external action for an unresolved outcome", true, node.Default.Select(n => Node(workflow, n)).ToList())).ToList() : []
        };
    }
}
