using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public static class PlanningBehaviorPlans
{
    public static string Fingerprint(PlanningBehaviorPlan plan) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.PlanningBehaviorPlan));

    /// <summary>Project mandatory ownership from exact selected contracts when the owner is unambiguous.</summary>
    public static void CompleteOwnership(PlanningBehaviorPlan plan, PlanningPreparation preparation)
    {
        foreach (var operation in preparation.Capabilities.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal))
        {
            if (plan.Workflows.Any(w => w.OperationIds.Contains(operation, StringComparer.Ordinal))) continue;
            var capabilities = preparation.Capabilities.Where(c => c.OperationIds.Contains(operation, StringComparer.Ordinal)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var owners = plan.Workflows.Where(w => Enumerate(w.Steps.Concat(w.Finally)).Any(n => n.CapabilityId is { } id && capabilities.Contains(id))).ToArray();
            if (owners.Length == 1) owners[0].OperationIds.Add(operation);
        }
    }

    public static IReadOnlyList<PlanningDiagnostic> Validate(PlanningBehaviorPlan plan, PlanningPreparation preparation)
    {
        var findings = new List<PlanningDiagnostic>();
        void Error(string location, string message) => findings.Add(new("BEHAVIOR_CONTRACT_INVALID", location, message));
        var known = preparation.Capabilities.SelectMany(c => c.OperationIds).ToHashSet(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(plan.Summary) || plan.Workflows.Count is < 1 or > 100 || !plan.Workflows.Any(w => w.Key == plan.Entrypoint)) Error("/", "Declare a summary, workflows and an existing entrypoint.");
        if (plan.Workflows.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != plan.Workflows.Count) Error("/workflows", "Workflow keys must be unique.");
        foreach (var workflow in plan.Workflows)
        {
            var path = "/workflows/" + plan.Workflows.IndexOf(workflow);
            if (string.IsNullOrWhiteSpace(workflow.Key) || string.IsNullOrWhiteSpace(workflow.Purpose)) Error(path, "A workflow needs a key and purpose.");
            foreach (var id in workflow.OperationIds)
                if (!known.Contains(id) || !owners.Add(id)) Error(path + "/operationIds/" + workflow.OperationIds.IndexOf(id), "Unknown or duplicate operation owner: " + id);
            foreach (var ports in new[] { workflow.Inputs, workflow.Outputs })
                if (ports.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Description)) || ports.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != ports.Count) Error(path, "Business ports need unique names and descriptions.");
            var nodes = Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
            if (nodes.Length > 300 || nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != nodes.Length) Error(path, "Behavior node keys must be unique and the node limit is 300.");
            foreach (var node in nodes)
            {
                var location = path + "/nodes/" + node.Key;
                if (string.IsNullOrWhiteSpace(node.Key) || string.IsNullOrWhiteSpace(node.Purpose)) Error(location, "A behavior node needs a stable key and purpose.");
                if (node.OperationIds.Any(id => !workflow.OperationIds.Contains(id, StringComparer.Ordinal))) Error(location, "The node claimed another workflow's operation.");
                if (node.Kind is not ("operation" or "decision" or "loop" or "sequence" or "parallel" or "confirmation" or "workflow")) Error(location, "Unknown behavior kind.");
                if (node.Kind is "operation" or "confirmation" or "workflow" or "decision" && node.Steps.Count != 0) Error(location, "Only sequence, parallel and loop nodes declare direct child steps.");
                if (node.Kind == "workflow")
                {
                    if (node.CapabilityId is not null || node.WorkflowKey == workflow.Key || !plan.Workflows.Any(w => w.Key == node.WorkflowKey)) Error(location, "A workflow call needs an existing distinct target and no capability binding.");
                }
                else if (node.WorkflowKey is not null) Error(location, "Only workflow calls declare a workflow target.");
                if (node.CapabilityId is { } id)
                {
                    var cap = preparation.Capabilities.FirstOrDefault(c => c.Id == id);
                    if (cap is null || cap.OperationIds.Any(op => !workflow.OperationIds.Contains(op, StringComparer.Ordinal))) Error(location + "/capabilityId", cap is null ? "Unknown capability: " + id : "Capability " + id + " requires this workflow to own operations: " + string.Join(", ", cap.OperationIds));
                    else if (node.Kind == "confirmation" && cap.StepType != "human.input" || node.Kind == "operation" && cap.StepType == "human.input") Error(location, "Confirmation behavior must use the declared human-input contract.");
                }
                if (node.Kind == "decision")
                {
                    if (node.Outcomes.Count < 2 || node.Outcomes.Count(o => o.IsDefault) != 1 || node.Outcomes.Any(o => string.IsNullOrWhiteSpace(o.Key) || string.IsNullOrWhiteSpace(o.Description)) || node.Outcomes.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != node.Outcomes.Count) Error(location, "A decision needs distinct described outcomes and exactly one explicit default, including any no-action outcome.");
                    foreach (var fallback in node.Outcomes.Where(o => o.IsDefault))
                        if (MayMutate(fallback.Steps, new(StringComparer.Ordinal))) Error(location + "/outcomes/" + fallback.Key, "The default must be non-mutating. Place write and lifecycle operations under explicit decision outcomes; retain cleanup in finally.");
                }
                else if (node.Outcomes.Count != 0) Error(location, "Only decisions declare outcomes.");
            }
            foreach (var id in workflow.OperationIds)
                if (!nodes.Any(n => n.OperationIds.Contains(id, StringComparer.Ordinal) || preparation.Capabilities.FirstOrDefault(c => c.Id == n.CapabilityId)?.OperationIds.Contains(id, StringComparer.Ordinal) == true)) Error(path + "/operationIds/" + workflow.OperationIds.IndexOf(id), "Operation " + id + " has no implementing behavior node.");
        }
        foreach (var cap in preparation.Capabilities.Where(c => c.Required))
        {
            if (cap.OperationIds.Any(id => !owners.Contains(id))) Error("/workflows", "Required operations have no owner: " + string.Join(", ", cap.OperationIds.Where(id => !owners.Contains(id))));
            if (cap.StepType == "mcp.call" && !plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Any(n => n.CapabilityId == cap.Id)) Error("/workflows", "Required external capability " + cap.Id + " is missing from the behavior. Add its action under its operation owner: " + string.Join(", ", cap.OperationIds));
        }
        foreach (var group in preparation.Capabilities.Where(c => c.Required && c.Activation is not null).GroupBy(c => c.Activation!.Group, StringComparer.Ordinal))
        {
            var activation = group.First().Activation!;
            foreach (var capability in group)
                if (plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Count(n => n.CapabilityId == capability.Id) != 1)
                    Error("/workflows", "Conditional capability " + capability.Id + " must occur exactly once in its declared activation outcome.");
            var decisions = plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Where(n => n.Kind == "decision").Where(n =>
                n.Outcomes.Where(o => !o.IsDefault).Select(o => o.Key).Order(StringComparer.Ordinal).SequenceEqual(activation.AllowedValues.Order(StringComparer.Ordinal)) &&
                group.All(c => n.Outcomes.Count(o => !o.IsDefault && o.Key == c.Activation!.BranchValue && Enumerate(o.Steps).Count(s => s.CapabilityId == c.Id) == 1) == 1)).ToArray();
            if (decisions.Length != 1) Error("/workflows", "Conditional group " + group.Key + " needs one decision with every exact explicit outcome [" + string.Join(", ", activation.AllowedValues) + "] and its declared actions under their branch values, plus a separate non-mutating default.");
            else foreach (var outcome in decisions[0].Outcomes.Where(o => activation.NoEffectValues.Contains(o.Key, StringComparer.Ordinal)))
                if (MayMutate(outcome.Steps, new(StringComparer.Ordinal))) Error("/workflows", "Declared no-effect outcome " + outcome.Key + " must not mutate external state.");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        Visit(plan.Entrypoint);
        if (plan.Workflows.Any(w => !visited.Contains(w.Key))) Error("/workflows", "Every workflow must be reachable from the entrypoint through declared workflow calls.");
        return findings;

        bool MayMutate(IEnumerable<PlanningBehaviorNode> nodes, HashSet<string> visitedWorkflows)
        {
            foreach (var node in Enumerate(nodes))
            {
                var cap = preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
                if (cap?.StepType == "mcp.call" && cap.EffectKind is not ("read" or "none")) return true;
                if (node.Kind == "workflow" && node.WorkflowKey is { } target && visitedWorkflows.Add(target))
                {
                    var workflow = plan.Workflows.FirstOrDefault(w => w.Key == target);
                    if (workflow is not null && MayMutate(workflow.Steps.Concat(workflow.Finally), visitedWorkflows)) return true;
                }
            }
            return false;
        }

        void Visit(string key)
        {
            if (active.Contains(key)) { Error("/workflows/" + key, "Workflow calls cannot form a cycle."); return; }
            if (!visited.Add(key)) return;
            active.Add(key);
            var target = plan.Workflows.FirstOrDefault(w => w.Key == key);
            if (target is not null)
                foreach (var call in Enumerate(target.Steps.Concat(target.Finally)).Where(n => n.Kind == "workflow" && n.WorkflowKey is not null)) Visit(call.WorkflowKey!);
            active.Remove(key);
        }
    }

    public static PlanningGraph Display(PlanningBehaviorPlan plan, PlanningPreparation? preparation)
    {
        return new() { Summary = plan.Summary, Entrypoint = plan.Entrypoint, Workflows = plan.Workflows.Select(w => new PlanningWorkflow
        {
            Key = w.Key, Purpose = w.Purpose, OperationIds = w.OperationIds.ToList(),
            Inputs = w.Inputs.Select(p => new PlanningPort { Name = p.Name, Required = p.Required }).ToList(),
            Outputs = w.Outputs.Select(p => new PlanningOutput { Name = p.Name }).ToList(),
            Steps = w.Steps.Select(Node).ToList(), Finally = w.Finally.Select(Node).ToList()
        }).ToList() };

        PlanningNode Node(PlanningBehaviorNode n) => new()
        {
            Key = n.Key, Purpose = n.Purpose, CapabilityId = n.CapabilityId, OperationIds = n.OperationIds.ToList(),
            Type = n.Kind switch { "decision" => "switch", "loop" => "loop.sequential", "confirmation" => "human.input", "workflow" => "workflow.call", "operation" => preparation?.Capabilities.FirstOrDefault(c => c.Id == n.CapabilityId)?.StepType ?? "set", _ => n.Kind },
            Input = n.Kind == "workflow" ? new() { Kind = "object", Members = [new("ref", new() { Kind = "workflow", Source = n.WorkflowKey })] } : new() { Kind = "object" },
            Steps = n.Kind == "parallel" ? [] : n.Steps.Select(Node).ToList(),
            Branches = n.Kind == "parallel" ? n.Steps.Select(s => new PlanningBranch([Node(s)])).ToList() : [],
            Cases = n.Outcomes.Where(o => !o.IsDefault).Select(o => new PlanningCase(o.Key, null, o.Steps.Select(Node).ToList())).ToList(),
            Default = n.Outcomes.Where(o => o.IsDefault).SelectMany(o => o.Steps).Select(Node).ToList()
        };
    }

    public static IReadOnlyList<PlanningDiagnostic> ValidateImplementation(PlanningBehaviorPlan plan, PlanningGraph graph, PlanningPreparation preparation)
    {
        var errors = new List<PlanningDiagnostic>();
        foreach (var behavior in plan.Workflows)
        {
            var workflow = graph.Workflows.FirstOrDefault(w => w.Key == behavior.Key);
            if (workflow is null || !behavior.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(workflow.OperationIds.Order(StringComparer.Ordinal)) ||
                !behavior.Inputs.Select(p => (p.Name, p.Required)).SequenceEqual(workflow.Inputs.Select(p => (p.Name, p.Required))) || !behavior.Outputs.Select(p => p.Name).SequenceEqual(workflow.Outputs.Select(p => p.Name)))
            { errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", behavior.Key, "Preserve the accepted workflow ownership and business inputs/outputs.")); continue; }
            Check(behavior.Steps, workflow.Steps, behavior.Key + "/steps");
            Check(behavior.Finally, workflow.Finally, behavior.Key + "/finally");
        }
        if (graph.Workflows.Any(w => !plan.Workflows.Any(b => b.Key == w.Key))) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", "/workflows", "New workflow ownership requires another behavior review."));
        return errors;

        void Check(List<PlanningBehaviorNode> expected, List<PlanningNode> actual, string path)
        {
            var last = -1;
            foreach (var item in expected)
            {
                var index = actual.FindIndex(n => n.Key == item.Key);
                if (index <= last) { errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path, "Preserve accepted actions, ordering, branches and finalizers.")); continue; }
                last = index;
                var node = actual[index];
                if (item.Kind == "workflow" && !node.Input.Members.Any(m => m.Name == "ref" && m.Value.Kind == "workflow" && m.Value.Source == item.WorkflowKey)) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path, "Preserve the accepted workflow-call target."));
                if (node.If is not null) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path + "/" + item.Key + "/if", "Conditional actions must remain inside accepted decision outcomes; a new guard requires review."));
                var type = item.Kind switch { "decision" => "switch", "loop" => "loop.sequential", "confirmation" => "human.input", "workflow" => "workflow.call", "operation" => preparation.Capabilities.FirstOrDefault(c => c.Id == item.CapabilityId)?.StepType ?? "set", _ => item.Kind };
                if (node.Type != type || node.CapabilityId != item.CapabilityId || !item.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(node.OperationIds.Order(StringComparer.Ordinal))) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path + "/" + item.Key, "An accepted action or its capability changed."));
                if (item.Kind == "decision")
                {
                    var cases = item.Outcomes.Where(o => !o.IsDefault).ToArray();
                    if (node.Cases.Count != cases.Length || !node.Cases.Select(c => c.Value).SequenceEqual(cases.Select(c => c.Key))) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path + "/" + item.Key, "Preserve every accepted decision outcome."));
                    for (var i = 0; i < Math.Min(cases.Length, node.Cases.Count); i++) Check(cases[i].Steps, node.Cases[i].Steps, path + "/" + item.Key + "/" + cases[i].Key);
                    Check(item.Outcomes.Single(o => o.IsDefault).Steps, node.Default, path + "/" + item.Key + "/default");
                }
                else if (item.Kind == "parallel")
                {
                    if (node.Branches.Count != item.Steps.Count) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path, "Preserve every accepted parallel branch."));
                    for (var i = 0; i < Math.Min(node.Branches.Count, item.Steps.Count); i++) Check([item.Steps[i]], node.Branches[i].Steps, path + "/" + item.Key + "/branches/" + i);
                }
                else Check(item.Steps, node.Steps, path + "/" + item.Key + "/steps");
            }
            // Extra shaping is allowed; new external operations and control flow require review.
            foreach (var extra in actual.Where(n => !expected.Any(b => b.Key == n.Key)))
                if (extra.Type != "set" || extra.CapabilityId is not null || extra.OperationIds.Count != 0 || extra.If is not null || extra.Steps.Count + extra.Cases.Count + extra.Branches.Count + extra.Default.Count != 0) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path + "/" + extra.Key, "Only unconditional local shaping may be added without another behavior review."));
        }
    }

    public static IEnumerable<PlanningBehaviorNode> Enumerate(IEnumerable<PlanningBehaviorNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in Enumerate(node.Steps.Concat(node.Outcomes.SelectMany(o => o.Steps)))) yield return child; }
    }
}
