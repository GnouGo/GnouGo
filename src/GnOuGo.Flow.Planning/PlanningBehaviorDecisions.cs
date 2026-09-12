using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Assembles behavior from owned operations; the model selects only unresolved business boundaries.</summary>
internal static class PlanningBehaviorDecisions
{
    internal static async Task<PlanningBehaviorPlan> BuildAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var preparation = state.Preparation!;
        var capabilities = preparation.Capabilities.Where(c => c.OperationIds.All(id => PlanningBusinessAnswers.Included(state, id))).ToList();
        var boundaries = state.Obligations.Where(o => o.Kind == "workflow_boundary").ToArray();
        var iterations = state.Obligations.Where(o => o.Kind == "iteration").ToArray();
        var decisions = new List<PlanningDecisionPages.Decision>();
        var operations = capabilities.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var operation in operations)
        {
            var text = state.Obligations.SingleOrDefault(o => o.Id == operation) is { } obligation ? PlanningSourceDecisions.Text(state, obligation)
                : string.Join("; ", capabilities.Where(c => c.OperationIds.Contains(operation)).Select(c => c.Description).Distinct(StringComparer.Ordinal));
            if (boundaries.Length > 0) decisions.Add(new("owner_" + operation, PlanningHoleRequests.Enum(["main", .. boundaries.Select(b => b.Id)]),
                new JsonObject { ["operation"] = text, ["boundaries"] = Descriptions(boundaries), ["task"] = "Assign the operation to its requested reusable workflow boundary, or main." }, preparation.Fingerprint));
            if (iterations.Length > 0) decisions.Add(new("iteration_" + operation, PlanningHoleRequests.Enum(["once", .. iterations.Select(b => b.Id)]),
                new JsonObject { ["operation"] = text, ["iterations"] = Descriptions(iterations), ["task"] = "Select the explicit iteration whose body contains this operation, or once. Resource setup and final cleanup stay outside per-item work unless explicitly requested." }, preparation.Fingerprint));
        }
        var choices = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "$plan", decisions, ct);
        return Assemble(state, choices);
        JsonObject Descriptions(IEnumerable<PlanningObligation> items) => new(items.Select(o => new KeyValuePair<string, JsonNode?>(o.Id, JsonValue.Create(PlanningSourceDecisions.Text(state, o)))));
    }

    internal static PlanningBehaviorPlan Assemble(PlanningSnapshot state, JsonObject choices)
    {
        var preparation = state.Preparation!;
        var capabilities = preparation.Capabilities.Where(c => c.OperationIds.All(id => PlanningBusinessAnswers.Included(state, id))).ToList();
        var plan = new PlanningBehaviorPlan { Summary = state.Request.Prompt, Entrypoint = "main" };
        var operations = capabilities.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal).ToArray();
        var owners = operations.ToDictionary(id => id, id => choices["owner_" + id]?.ToString() ?? "main", StringComparer.Ordinal);
        var inputs = state.Obligations.Where(o => o.Kind == "business_input").ToDictionary(o => o.Id,
            o => new PlanningBehaviorPort("input_" + o.Id[3..], PlanningSourceDecisions.Text(state, o), o.Required), StringComparer.Ordinal);
        var outputs = state.Obligations.Where(o => o.Kind == "business_output")
            .Select(o => new PlanningBehaviorPort("output_" + o.Id[3..], PlanningSourceDecisions.Text(state, o), o.Required)).ToList();
        var mainInputs = state.Request.Baseline?.Workflows.SingleOrDefault(w => w.Key == state.Request.Baseline.Entrypoint)?.Inputs;
        if (mainInputs is not null)
        {
            // Imported public names and contracts belong to the saved revision.
            // Changes require a separately located human revision.
            foreach (var input in mainInputs)
                if (!inputs.Values.Any(p => p.Name == input.Name)) inputs["baseline:" + input.Name] = new(input.Name, input.Name, input.Required);
        }
        var nodes = capabilities.Select(c => new PlanningBehaviorNode
        {
            Key = "node_" + PlanningGraphCompiler.Fingerprint(c.Id)[..16], CapabilityId = c.Id,
            Kind = c.StepType == "human.input" ? "confirmation" : "operation", Purpose = c.Description,
            OperationIds = c.OperationIds.ToList(), InputDependencies = BusinessInputs(c.OperationIds).Select(id => inputs[id].Name).Distinct(StringComparer.Ordinal).ToList()
        }).ToList();
        foreach (var node in nodes)
            if (node.OperationIds.Select(id => owners[id]).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new WorkflowRuntimeException("BEHAVIOR_OWNER_CONFLICT", "A shared capability must have one workflow owner: " + node.Key);
        var order = Order(capabilities);
        foreach (var owner in owners.Values.Append("main").Distinct(StringComparer.Ordinal).OrderBy(k => k == "main" ? "" : k, StringComparer.Ordinal))
        {
            var owned = nodes.Where(n => n.OperationIds.Any(id => owners[id] == owner)).OrderBy(n => order[n.CapabilityId!]).ToList();
            var workflow = new PlanningBehaviorWorkflow { Key = owner, Purpose = owner == "main" ? state.Request.Prompt : PlanningSourceDecisions.Text(state, state.Obligations.Single(o => o.Id == owner)),
                OperationIds = operations.Where(id => owners[id] == owner).ToList() };
            workflow.Inputs = owner == "main" ? inputs.Values.ToList() : inputs.Values.Where(p => owned.Any(n => n.InputDependencies!.Contains(p.Name))).ToList();
            if (owner == "main") workflow.Outputs = outputs;
            foreach (var node in owned)
            {
                if (node.OperationIds.Any(id => state.Obligations.Any(o => o.Id == id && o.Kind == "cleanup"))) workflow.Finally.Add(node);
                else workflow.Steps.Add(node);
            }
            foreach (var iteration in state.Obligations.Where(o => o.Kind == "iteration"))
            {
                var body = workflow.Steps.Where(n => n.OperationIds.Any(id => choices["iteration_" + id]?.ToString() == iteration.Id)).ToList();
                if (body.Count == 0) continue;
                var at = workflow.Steps.IndexOf(body[0]); foreach (var node in body) workflow.Steps.Remove(node);
                workflow.Steps.Insert(at, new() { Key = "loop_" + iteration.Id, Kind = "loop", Purpose = PlanningSourceDecisions.Text(state, iteration),
                    Steps = body, InputDependencies = body.SelectMany(n => n.InputDependencies ?? []).Distinct(StringComparer.Ordinal).ToList() });
            }
            Route(workflow.Steps); Route(workflow.Finally);
            plan.Workflows.Add(workflow);
        }
        var main = plan.Workflows.Single(w => w.Key == "main");
        foreach (var workflow in plan.Workflows.Where(w => w.Key != "main"))
        {
            var ownedOperations = workflow.OperationIds.ToHashSet(StringComparer.Ordinal);
            var requiredOperations = capabilities.Where(c => c.OperationIds.Any(ownedOperations.Contains)).SelectMany(c => c.InputOperationIds)
                .Where(id => !ownedOperations.Contains(id)).ToHashSet(StringComparer.Ordinal);
            foreach (var producer in capabilities.Where(c => c.OperationIds.Any(requiredOperations.Contains)))
            {
                workflow.Inputs.Add(new(ResultPort(producer), producer.Description, true));
                foreach (var node in PlanningBehaviorPlans.Enumerate(workflow.Steps.Concat(workflow.Finally)))
                    if (capabilities.Any(c => c.OperationIds.Any(node.OperationIds.Contains) && c.InputOperationIds.Any(producer.OperationIds.Contains)))
                        node.InputDependencies = (node.InputDependencies ?? []).Append(ResultPort(producer)).Distinct(StringComparer.Ordinal).ToList();
            }
            foreach (var producer in capabilities.Where(c => c.OperationIds.Any(ownedOperations.Contains) &&
                capabilities.Any(consumer => consumer.OperationIds.Any(id => !ownedOperations.Contains(id)) && consumer.InputOperationIds.Any(c.OperationIds.Contains))))
                workflow.Outputs.Add(new(ResultPort(producer), producer.Description, true));
        }
        // Workflow edges derive from operation producers, never model-owned call IDs.
        foreach (var workflow in plan.Workflows.Where(w => w.Key != "main"))
        {
            var consumers = capabilities.Where(c => c.InputOperationIds.Any(workflow.OperationIds.Contains)).SelectMany(c => c.OperationIds).Select(id => owners[id]).Distinct(StringComparer.Ordinal).ToArray();
            var callers = consumers.Where(k => k != workflow.Key).Select(k => plan.Workflows.Single(w => w.Key == k)).ToArray();
            if (callers.Length == 0) callers = [main];
            if (callers.Length != 1) throw new WorkflowRuntimeException("BEHAVIOR_CALL_OWNERSHIP_UNRESOLVED", "Shared executable resource ownership requires one declared call boundary for " + workflow.Key);
            callers[0].Steps.Add(new() { Key = "call_" + workflow.Key, Kind = "workflow", WorkflowKey = workflow.Key, Purpose = workflow.Purpose,
                InputDependencies = workflow.Inputs.Select(p => p.Name).Intersect(callers[0].Inputs.Select(p => p.Name), StringComparer.Ordinal).ToList() });
        }
        foreach (var workflow in plan.Workflows)
        {
            var produced = workflow.Steps.ToDictionary(n => n, n => PlanningBehaviorPlans.Enumerate([n])
                .SelectMany(child => child.WorkflowKey is { } callee ? plan.Workflows.Single(w => w.Key == callee).OperationIds : child.OperationIds).ToHashSet(StringComparer.Ordinal));
            var ordered = new List<PlanningBehaviorNode>(); var visiting = new HashSet<PlanningBehaviorNode>();
            void Visit(PlanningBehaviorNode node)
            {
                if (ordered.Contains(node)) return;
                if (!visiting.Add(node)) throw new WorkflowRuntimeException("BEHAVIOR_BOUNDARY_CYCLE", "The requested workflow boundary requires interleaved producer and consumer execution: " + workflow.Key);
                var requires = capabilities.Where(c => c.OperationIds.Any(produced[node].Contains)).SelectMany(c => c.InputOperationIds).ToHashSet(StringComparer.Ordinal);
                foreach (var producer in workflow.Steps.Where(p => p != node && produced[p].Overlaps(requires)).OrderBy(p => p.Key, StringComparer.Ordinal)) Visit(producer);
                visiting.Remove(node); ordered.Add(node);
            }
            foreach (var node in workflow.Steps) Visit(node);
            workflow.Steps = ordered;
        }
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation); PlanningBehaviorPlans.CompleteReviewDefaults(plan); PlanningBehaviorPlans.CompleteLockedOutcomes(plan, preparation);
        // Local computations have operation ownership, not an external capability
        // binding. The executable skeleton selects their native executor.
        foreach (var node in PlanningBehaviorPlans.Enumerate(plan.Workflows.SelectMany(w => w.Steps.Concat(w.Finally))))
            if (capabilities.Any(c => c.Id == node.CapabilityId && c.Resolution == "local")) node.CapabilityId = null;
        return plan;

        IEnumerable<string> BusinessInputs(IEnumerable<string> operationIds)
        {
            var pending = new Stack<string>(operationIds); var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryPop(out var id))
            {
                if (!visited.Add(id)) continue;
                if (inputs.ContainsKey(id)) yield return id;
                foreach (var relation in state.ObligationRelations.Where(r => r.Consumer == id && r.Role != "failure")) pending.Push(relation.Producer);
            }
        }
        void Route(List<PlanningBehaviorNode> steps)
        {
            foreach (var node in steps.ToArray()) Route(node.Steps);
            foreach (var group in capabilities.Where(c => c.Activation is not null).GroupBy(c => c.Activation!.Group, StringComparer.Ordinal))
            {
                var body = steps.Where(n => group.Any(c => c.Id == n.CapabilityId)).ToArray();
                if (body.Length == 0) continue;
                var activation = group.First().Activation!;
                if (body.Length != group.Count()) throw new WorkflowRuntimeException("BEHAVIOR_ROUTING_SCOPE_CONFLICT", "A locked activation composition crosses a behavior boundary: " + group.Key);
                var at = steps.IndexOf(body[0]); foreach (var node in body) steps.Remove(node);
                var route = new PlanningBehaviorNode { Key = "route_" + PlanningGraphCompiler.Fingerprint(group.Key)[..16], Kind = "decision", Purpose = "Apply the declared decision outcomes.", InputDependencies = [] };
                foreach (var value in activation.AllowedValues)
                    route.Outcomes.Add(new(value, value, false, body.Where(n => group.Single(c => c.Id == n.CapabilityId).Activation!.BranchValue == value).ToList()));
                route.Outcomes.Add(new("default", "Unmatched or unavailable decision: take no action.", true, []));
                steps.Insert(at, route);
            }
        }
    }

    internal static string ResultPort(PlanningCapability capability) => "result_" + PlanningGraphCompiler.Fingerprint(capability.Id)[..16];

    private static Dictionary<string, int> Order(IReadOnlyList<PlanningCapability> capabilities)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(PlanningCapability capability)
        {
            if (result.ContainsKey(capability.Id)) return;
            if (!visiting.Add(capability.Id)) throw new WorkflowRuntimeException("BEHAVIOR_DEPENDENCY_CYCLE", "Locked operation dependencies contain a cycle at " + capability.Id);
            foreach (var producer in capabilities.Where(c => c.Id != capability.Id && c.OperationIds.Any(capability.InputOperationIds.Contains)).OrderBy(c => c.Id, StringComparer.Ordinal)) Visit(producer);
            visiting.Remove(capability.Id); result[capability.Id] = result.Count;
        }
        foreach (var capability in capabilities.OrderBy(c => c.Id, StringComparer.Ordinal)) Visit(capability);
        return result;
    }
}
