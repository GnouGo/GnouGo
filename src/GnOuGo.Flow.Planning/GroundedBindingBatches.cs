using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
namespace GnOuGo.Flow.Planning;

/// <summary>Bind complete semantic subgraphs within the existing per-request and cumulative budgets.</summary>
internal static class GroundedBindingBatches
{
    internal static bool Required(PlanningSession state) => state.BindingProgress is not null ||
        SemanticPlanning.Actions(state.SemanticPlan!).Sum(a => Weight(state, a, descendants: false)) > OutputCapacity(state) ||
        PlanningJsonTransport.EstimateInputTokens(CapabilityGrounder.BindingPrompt(state), PlanningSchemas.Grounded(state.Grounding!.Selections!.SelectMany(s => s.CapabilityIds))) > state.Request.Generation.MaxInputTokensPerRequest;

    internal static async Task ApplyAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct, bool replan = false)
    {
        var progress = state.BindingProgress ??= new();
        // Durable prefixes are data, not validation receipts. Re-establish their types after restart.
        var accepted = progress.CompletedActions.Count == 0 ? null : GroundedPlanValidator.RequireValid(progress.Accepted, state.Catalog!);
        var boundary = Boundary(progress.Accepted, accepted, state.Catalog!);
        var remaining = Ordered(state.SemanticPlan!).Where(a => !progress.CompletedActions.Contains(a.Id)).ToArray();
        if (progress.CurrentActions.Count == 0)
        {
            var all = Request(state, remaining.Select(a => a.Id).ToList(), boundary);
            var total = remaining.Sum(a => Weight(state, a));
            var batches = Math.Max(PlanningJsonTransport.EstimateInputTokens(all.Prompt, all.Schema) <= state.Request.Generation.MaxInputTokensPerRequest ? 1 : 2,
                (int)Math.Ceiling((double)total / OutputCapacity(state)));
            if (state.ModelCalls + batches > state.Request.MaxModelCalls)
                throw new WorkflowRuntimeException("BINDING_BUDGET_INSUFFICIENT", "The complete binding work requires at least " + batches + " bounded batches.", details: new JsonObject { ["location"] = "/actions/" + remaining[0].Id });
            var target = (double)total / batches; var weight = 0;
            foreach (var action in remaining)
            {
                var nextWeight = Weight(state, action);
                if (progress.CurrentActions.Count > 0 && weight + nextWeight > target && target - weight < weight + nextWeight - target) break;
                var next = progress.CurrentActions.Append(action.Id).ToList();
                var request = Request(state, next, boundary);
                if (PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.Schema) > state.Request.Generation.MaxInputTokensPerRequest) break;
                progress.CurrentActions = next;
                weight += nextWeight;
                if (weight >= target) break;
            }
            if (progress.CurrentActions.Count == 0)
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", "An indivisible semantic subgraph and its established boundary exceed the input allowance.", details: new JsonObject { ["location"] = "/actions/" + remaining[0].Id });
            // At least one more complete request is necessary when this batch is not final.
            var minimum = progress.CurrentActions.Count == remaining.Length ? 1 : 2;
            if (state.ModelCalls + minimum > state.Request.MaxModelCalls)
                throw new WorkflowRuntimeException("BINDING_BUDGET_INSUFFICIENT", "The remaining complete binding subgraphs require at least " + minimum + " calls.", details: new JsonObject { ["location"] = "/actions/" + progress.CurrentActions[0] });
        }
        var current = Request(state, progress.CurrentActions, boundary);
        var prompt = current.Prompt;
        if (replan)
        {
            var context = new JsonObject { ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics) };
            var instruction = "\nReplace this entire binding subgraph atomically; established boundaries remain unchanged.\n";
            prompt += instruction + PlanningJsonTransport.Prompt(context);
            if (progress.Candidate is not null)
            {
                context["candidate"] = PlanningJsonTransport.Grounded(progress.Candidate);
                var withCandidate = current.Prompt + instruction + PlanningJsonTransport.Prompt(context);
                if (PlanningJsonTransport.EstimateInputTokens(withCandidate, current.Schema) <= state.Request.Generation.MaxInputTokensPerRequest) prompt = withCandidate;
            }
        }
        var json = await PlanningModelCalls.CallAsync(state, runtime, replan ? "replan" : "binding", prompt, current.Schema, ct);
        var candidate = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GroundedPlan)!;
        if (replan && progress.Candidate is not null && JsonNode.DeepEquals(PlanningJsonTransport.Grounded(progress.Candidate), PlanningJsonTransport.Grounded(candidate)))
        { state.Diagnostics.Add(new("REPLAN_NO_PROGRESS", "/actions/" + progress.CurrentActions[0], "The replacement subgraph is unchanged.")); state.Status = PlanningStatus.Stopped; return; }
        progress.Candidate = candidate;
        var final = progress.CurrentActions.Count == remaining.Length;
        var first = progress.CompletedActions.Count == 0;
        if (!first && (candidate.Inputs.Count != 0 || candidate.Subflows.Count != 0) || !final && candidate.Outputs.Count != 0)
            throw new PlanningResponseException([new("BINDING_BOUNDARY_INVALID", "/actions/" + progress.CurrentActions[0], "Only the first batch declares workflow inputs/subflows; only the last batch declares workflow outputs.")]);
        var combined = new GroundedPlan { Summary = state.SemanticPlan!.Summary,
            Inputs = first ? candidate.Inputs : progress.Accepted.Inputs,
            Operations = [.. progress.Accepted.Operations, .. candidate.Operations],
            Subflows = first ? candidate.Subflows : progress.Accepted.Subflows,
            Outputs = candidate.Outputs };
        var scope = new SemanticPlan { Actions = state.SemanticPlan.Actions.Where(a => progress.CompletedActions.Contains(a.Id) || progress.CurrentActions.Contains(a.Id)).ToList(), Subflows = state.SemanticPlan.Subflows };
        var check = new PlanningSession { Request = state.Request, Catalog = state.Catalog, SemanticPlan = state.SemanticPlan, Grounding = state.Grounding, GroundedPlan = combined };
        var actionIds = SemanticPlanning.Actions(scope).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        state.Diagnostics = CapabilityGrounder.ValidateBindings(check, actionIds);
        // A batch must not add implementations of earlier actions; their outputs and behavior are immutable here.
        var currentIds = SemanticPlanning.Actions(new() { Actions = state.SemanticPlan.Actions.Where(a => progress.CurrentActions.Contains(a.Id)).ToList(), Subflows = first ? state.SemanticPlan.Subflows : [] }).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (GroundedTraversal.Located(candidate).Any(p => !currentIds.Contains(p.Operation.SemanticAction)))
            state.Diagnostics.Add(new("BINDING_BOUNDARY_INVALID", "/operations", "The batch may implement only its issued semantic actions."));
        if (state.Diagnostics.Count != 0) return;
        var validation = GroundedPlanValidator.Validate(combined, state.Catalog!);
        state.Diagnostics = validation.Diagnostics.ToList();
        if (validation.Plan is null) return;
        state.Diagnostics = CapabilityGrounder.ValidateBusinessOutputs(check, validation.Plan);
        if (state.Diagnostics.Count != 0) return;
        progress.Accepted = combined;
        progress.CompletedActions.AddRange(progress.CurrentActions);
        progress.CurrentActions.Clear(); progress.Candidate = null;
        if (final) { state.GroundedPlan = combined; state.BindingProgress = null; }
    }

    // A planning estimate, not a raised ceiling: reserve response space for reasoning and JSON framing.
    // Partition complete business work rather than treating an almost-full input as a small output request.
    private static int OutputCapacity(PlanningSession state) => Math.Max(1, state.Request.Generation.MaxOutputTokens / 2);
    private static int Weight(PlanningSession state, SemanticAction action, bool descendants = true)
    {
        var selected = state.Grounding!.Selections!.FirstOrDefault(s => s.ActionId == action.Id)?.CapabilityIds ?? [];
        var weight = 300 + action.Outputs.Count * 60 + selected.Sum(id => 200 + (state.Catalog!.Capabilities.Single(c => c.Id == id).InputSchema["properties"] as JsonObject ?? new()).Count * 45);
        return weight + (descendants ? action.Blocks.SelectMany(b => b.Actions).Sum(a => Weight(state, a)) : 0);
    }

    private static (string Prompt, JsonObject Schema) Request(PlanningSession state, List<string> ids, JsonObject boundary)
    {
        var first = state.BindingProgress!.CompletedActions.Count == 0;
        var final = state.SemanticPlan!.Actions.All(a => state.BindingProgress.CompletedActions.Contains(a.Id) || ids.Contains(a.Id));
        var fragment = new SemanticPlan { Summary = state.SemanticPlan.Summary,
            Inputs = first ? state.SemanticPlan.Inputs : [],
            Actions = state.SemanticPlan.Actions.Where(a => ids.Contains(a.Id)).ToList(),
            Subflows = first ? state.SemanticPlan.Subflows : [], Outputs = final ? state.SemanticPlan.Outputs : [] };
        var actionIds = SemanticPlanning.Actions(fragment).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var capabilities = state.Grounding!.Selections!.Where(s => actionIds.Contains(s.ActionId)).SelectMany(s => s.CapabilityIds);
        var prompt = CapabilityGrounder.BindingPrompt(state, fragment, boundary) + "\n" +
            "This is one complete binding subgraph. Implement only the issued semantic actions. Use established result operation IDs and exact contracts to consume previous values. " +
            (first ? "Declare the workflow inputs and named subflows. Give every input an explicit business type, including inputs first consumed by later batches." : "Return empty inputs and subflows; they are already established.") +
            (final ? " Declare all required workflow outputs." : " Return empty outputs; workflow outputs are bound in the last batch.");
        var schema = PlanningSchemas.Grounded(capabilities);
        void Empty(string field)
        {
            schema["properties"]![field] = new JsonObject { ["type"] = "array", ["items"] = PlanningSchemas.String(), ["maxItems"] = 0 };
        }
        if (!first) { Empty("inputs"); Empty("subflows"); }
        if (!final) Empty("outputs");
        PlanningJsonTransport.PruneDefinitions(schema);
        return (prompt, schema);
    }

    private static JsonObject Boundary(GroundedPlan plan, ValidatedGroundedPlan? validated, PlanningCatalog catalog)
    {
        if (validated is null) return new();
        return new()
        {
            ["inputs"] = new JsonArray(plan.Inputs.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["contract"] = PlanningJsonTransport.ContractPrompt(validated.Types.Input("main", i.Name)) }).ToArray()),
            // Intermediate operations are private to accepted subgraphs. Only named business results cross the boundary.
            ["results"] = new JsonArray(plan.Operations.Where(o => o is not CleanupGroundedOperation && o.BusinessOutputs.Count > 0).Select(o => (JsonNode)new JsonObject
            { ["id"] = o.Id, ["semanticAction"] = o.SemanticAction,
                ["businessOutputs"] = new JsonArray(o.BusinessOutputs.Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["path"] = new JsonArray(b.Path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) }).ToArray()),
                ["when"] = o.When is null ? null : JsonSerializer.SerializeToNode(o.When, PlanningJsonContext.Default.GroundedValue),
                ["artifacts"] = o is InvokeGroundedOperation invoke ? JsonSerializer.SerializeToNode(catalog.Capabilities.Single(c => c.Id == invoke.Capability).ArtifactContract, PlanningJsonContext.Default.McpArtifactContract) : null,
                ["contract"] = PlanningJsonTransport.ContractPrompt(validated.Types.Result("main", o.Id)) }).ToArray())
        };
    }

    private static List<SemanticAction> Ordered(SemanticPlan plan)
    {
        var owners = plan.Actions.SelectMany(a => SemanticPlanning.Actions(new() { Actions = [a] }).Select(child => (child.Id, Root: a.Id))).ToDictionary(p => p.Id, p => p.Root, StringComparer.Ordinal);
        var pending = plan.Actions.ToList(); var result = new List<SemanticAction>();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(a => Dependencies(a).All(id => id == a.Id || result.Any(done => done.Id == id)));
            if (ready is null) throw new PlanningResponseException([new("SEMANTIC_DEPENDENCY_CYCLE", "/actions", "Binding subgraphs require acyclic business dependencies.")]);
            result.Add(ready); pending.Remove(ready);
        }
        return result;
        IEnumerable<string> Dependencies(SemanticAction root) => SemanticPlanning.Actions(new() { Actions = [root] })
            .SelectMany(a => a.After.Concat(a.Inputs.Select(i => i.Source.Split('.')[0])))
            .Where(owners.ContainsKey).Select(id => owners[id]).Distinct(StringComparer.Ordinal);
    }
}
