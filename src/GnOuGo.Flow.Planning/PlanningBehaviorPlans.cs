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
        foreach (var capability in preparation.Capabilities.Where(c => c.Required && c.StepType == "decision.evaluate"))
        {
            if (plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Any(n => n.CapabilityId == capability.Id)) continue;
            var owners = plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally)))
                .Where(n => n.Kind == "decision" && capability.OperationIds.All(n.OperationIds.Contains)).ToArray();
            if (owners.Length != 1 || capability.OperationIds.Count == 0) continue;
            var routing = owners[0];
            var key = routing.Key + "_producer_" + PlanningGraphCompiler.Fingerprint(capability.Id)[..8];
            if (plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Any(n => n.Key == key)) continue;
            var producer = new PlanningBehaviorNode
            {
                Key = key,
                Kind = "operation",
                CapabilityId = capability.Id,
                Purpose = routing.Purpose,
                OperationIds = capability.OperationIds.ToList(),
                InputDependencies = routing.InputDependencies?.ToList()
            };
            foreach (var workflow in plan.Workflows) { Insert(workflow.Steps); Insert(workflow.Finally); }
            void Insert(List<PlanningBehaviorNode> nodes)
            {
                var index = nodes.IndexOf(routing);
                if (index >= 0) { nodes.Insert(index, producer); return; }
                foreach (var node in nodes) { Insert(node.Steps); foreach (var outcome in node.Outcomes) Insert(outcome.Steps); }
            }
        }
        foreach (var operation in preparation.Capabilities.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal))
        {
            if (plan.Workflows.Any(w => w.OperationIds.Contains(operation, StringComparer.Ordinal))) continue;
            var capabilities = preparation.Capabilities.Where(c => c.OperationIds.Contains(operation, StringComparer.Ordinal)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var owners = plan.Workflows.Where(w => Enumerate(w.Steps.Concat(w.Finally)).Any(n => n.CapabilityId is { } id && capabilities.Contains(id))).ToArray();
            if (owners.Length == 0)
                owners = plan.Workflows.Where(w => Enumerate(w.Steps.Concat(w.Finally)).Any(n => n.OperationIds.Contains(operation, StringComparer.Ordinal))).ToArray();
            if (owners.Length == 1) owners[0].OperationIds.Add(operation);
        }
    }

    /// <summary>Make the mandatory no-action fallback visible before behavior approval.</summary>
    internal static void CompleteReviewDefaults(PlanningBehaviorPlan plan)
    {
        foreach (var node in plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))))
        {
            if (node.Kind != "decision" || node.Outcomes.Count == 0 || node.Outcomes.Any(o => o.IsDefault)) continue;
            var key = "default";
            for (var suffix = 1; node.Outcomes.Any(o => o.Key == key); suffix++) key = "default_" + suffix;
            // This is the planner's existing mandatory fallback policy, not an
            // inferred business outcome. Explicit cases and existing defaults are
            // never rewritten. Missing finite outcomes still fail validation.
            node.Outcomes.Add(new(key, "Unmatched or unavailable decision: take no action.", true, []));
        }
    }

    public static IReadOnlyList<PlanningDiagnostic> Validate(PlanningBehaviorPlan plan, PlanningPreparation preparation)
    {
        var findings = new List<PlanningDiagnostic>();
        void Error(string location, string message, string rule) => findings.Add(new("BEHAVIOR_CONTRACT_INVALID", location, message, Rule: rule));
        var known = preparation.Capabilities.SelectMany(c => c.OperationIds).ToHashSet(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(plan.Summary) || plan.Workflows.Count is < 1 or > 100 || !plan.Workflows.Any(w => w.Key == plan.Entrypoint)) Error("/", "Declare a summary, workflows and an existing entrypoint.", "behavior_01");
        if (plan.Workflows.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != plan.Workflows.Count) Error("/workflows", "Workflow keys must be unique.", "behavior_02");
        foreach (var workflow in plan.Workflows)
        {
            var path = "/workflows/" + plan.Workflows.IndexOf(workflow);
            if (string.IsNullOrWhiteSpace(workflow.Key) || string.IsNullOrWhiteSpace(workflow.Purpose)) Error(path, "A workflow needs a key and purpose.", "behavior_03");
            foreach (var id in workflow.OperationIds)
                if (!known.Contains(id) || !owners.Add(id)) Error(path + "/operationIds/" + workflow.OperationIds.IndexOf(id), "Unknown or duplicate operation owner: " + id, "behavior_04");
            foreach (var ports in new[] { workflow.Inputs, workflow.Outputs })
                if (ports.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Description)) || ports.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != ports.Count) Error(path, "Business ports need unique names and descriptions.", "behavior_05");
            var nodes = Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
            if (nodes.Length > 300 || nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != nodes.Length) Error(path, "Behavior node keys must be unique and the node limit is 300.", "behavior_06");
            foreach (var node in nodes)
            {
                var location = Located(workflow.Steps, path + "/steps").Concat(Located(workflow.Finally, path + "/finally")).Single(p => ReferenceEquals(p.Node, node)).Path;
                if (string.IsNullOrWhiteSpace(node.Key)) Error(location + "/key", "A behavior node needs a stable key.", "node_key");
                if (string.IsNullOrWhiteSpace(node.Purpose)) Error(location + "/purpose", "A behavior node needs a purpose.", "node_purpose");
                foreach (var claimed in node.OperationIds.Where(operation => !workflow.OperationIds.Contains(operation, StringComparer.Ordinal))) Error(location + "/operationIds/" + node.OperationIds.IndexOf(claimed), "The node claimed another workflow's operation.", "behavior_08");
                if (node.InputDependencies is { } inputs && (inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Count || inputs.Any(name => !workflow.Inputs.Any(p => p.Name == name)))) Error(location + "/inputDependencies", "Input dependencies must name unique business inputs of this workflow. Allowed inputs: " + string.Join(", ", workflow.Inputs.Select(p => p.Name)) + ". Producer node keys are not business inputs. Submitted: " + string.Join(", ", inputs), "behavior_09");
                if (node.Kind is not ("operation" or "decision" or "loop" or "sequence" or "parallel" or "confirmation" or "workflow")) Error(location + "/kind", "Unknown behavior kind.", "behavior_10");
                if (node.Kind is "operation" or "confirmation" or "workflow" or "decision" && node.Steps.Count != 0) Error(location, "Only sequence, parallel and loop nodes declare direct child steps.", "behavior_11");
                if (node.Kind == "workflow")
                {
                    if (node.CapabilityId is not null || node.WorkflowKey == workflow.Key || !plan.Workflows.Any(w => w.Key == node.WorkflowKey)) Error(location + "/workflowKey", "A workflow call needs an existing distinct target and no capability binding.", "behavior_12");
                }
                else if (node.WorkflowKey is not null) Error(location + "/workflowKey", "Only workflow calls declare a workflow target.", "behavior_13");
                if (node.CapabilityId is { } id)
                {
                    var cap = preparation.Capabilities.FirstOrDefault(c => c.Id == id);
                    if (cap is null || cap.OperationIds.Any(op => !workflow.OperationIds.Contains(op, StringComparer.Ordinal))) Error(location + "/capabilityId", cap is null ? "Unknown capability: " + id : "Capability " + id + " requires this workflow to own operations: " + string.Join(", ", cap.OperationIds), "behavior_14");
                    else if (node.Kind == "confirmation" && cap.StepType != "human.input" || node.Kind == "operation" && cap.StepType == "human.input") Error(location + "/capabilityId", "Confirmation behavior must use the declared human-input contract.", "behavior_15");
                    if (cap is not null)
                    {
                        if (node.OperationIds.Count > 0 && cap.OperationIds.Any(op => !node.OperationIds.Contains(op, StringComparer.Ordinal)))
                            Error(location + "/capabilityId", "The selected capability belongs to operations outside this node's declared ownership. Select its exact operation contract or leave an unbound native container's capability null.", "behavior_16");
                        if (!PlanningCapabilityBindings.SupportsBehavior(cap, node.Kind)) Error(location + "/capabilityId", "This behavior kind does not implement the selected native or external capability. A native decision.evaluate is an operation that produces a decision; a separate decision node routes its outcomes.", "behavior_17");
                    }
                }
                if (node.Kind == "decision")
                {
                    if (node.Outcomes.Count < 2 || node.Outcomes.Count(o => o.IsDefault) != 1 || node.Outcomes.Any(o => string.IsNullOrWhiteSpace(o.Key) || string.IsNullOrWhiteSpace(o.Description)) || node.Outcomes.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != node.Outcomes.Count) Error(location, "A decision needs distinct described outcomes and exactly one explicit default, including any no-action outcome.", "behavior_18");
                    foreach (var fallback in node.Outcomes.Where(o => o.IsDefault))
                        if (MayMutate(fallback.Steps, new(StringComparer.Ordinal))) Error(location + "/outcomes/" + node.Outcomes.IndexOf(fallback) + "/steps", "The default must be non-mutating. Place write and lifecycle operations under explicit decision outcomes; retain cleanup in finally.", "behavior_19");
                }
                else if (node.Outcomes.Count != 0) Error(location, "Only decisions declare outcomes.", "behavior_20");
            }
            foreach (var id in workflow.OperationIds)
                if (!nodes.Any(n => n.OperationIds.Contains(id, StringComparer.Ordinal) || preparation.Capabilities.FirstOrDefault(c => c.Id == n.CapabilityId)?.OperationIds.Contains(id, StringComparer.Ordinal) == true)) Error(path + "/operationIds/" + workflow.OperationIds.IndexOf(id), "Operation " + id + " has no implementing behavior node.", "behavior_21");
        }
        foreach (var cap in preparation.Capabilities.Where(c => c.Required))
        {
            if (cap.OperationIds.Any(id => !owners.Contains(id))) Error("/workflows", "Required operations have no owner: " + string.Join(", ", cap.OperationIds.Where(id => !owners.Contains(id))), "behavior_22");
            if (cap.Resolution != "local" && !plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Any(n => n.CapabilityId == cap.Id))
            {
                var declaredOwners = plan.Workflows.Where(w => cap.OperationIds.Count > 0 && cap.OperationIds.All(w.OperationIds.Contains)).ToArray();
                if (declaredOwners.Length == 1 && cap.Activation is null)
                    Error("/workflows/" + plan.Workflows.IndexOf(declaredOwners[0]) + "/steps/" + declaredOwners[0].Steps.Count,
                        "Insert the missing required capability under its declared owner: " + cap.Id, "insert_behavior_node:" + cap.Id);
                else Error("/workflows", "Required capability has no uniquely located implementation: " + cap.Id, "missing_capability:" + cap.Id);
            }
        }
        foreach (var cap in preparation.Capabilities.Where(c => c.ArtifactContract?.Produces.Any(p => p.Mode == "materialize") == true))
        {
            var occurrences = plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally)).Where(n => n.CapabilityId == cap.Id).Select(n => (Workflow: w, Node: n))).ToArray();
            foreach (var occurrence in occurrences.Skip(1))
                findings.Add(new("BEHAVIOR_MATERIALIZER_REUSED", Located(occurrence.Workflow.Steps, "/workflows/" + plan.Workflows.IndexOf(occurrence.Workflow) + "/steps").Concat(Located(occurrence.Workflow.Finally, "/workflows/" + plan.Workflows.IndexOf(occurrence.Workflow) + "/finally")).Single(p => ReferenceEquals(p.Node, occurrence.Node)).Path + "/capabilityId",
                    "This operation repeats an artifact materializer beyond its locked occurrence. Select a capability that implements this operation's effect; a producer cannot stand in for a different lifecycle action. Correct the capability binding before accepting behavior."));
        }
        foreach (var group in preparation.Capabilities.Where(c => c.Required && c.Activation is not null).GroupBy(c => c.Activation!.Group, StringComparer.Ordinal))
        {
            var activation = group.First().Activation!;
            foreach (var capability in group)
                if (plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Count(n => n.CapabilityId == capability.Id) != 1)
                    Error("/workflows", "Conditional capability " + capability.Id + " must occur exactly once in its declared activation outcome.", "behavior_24");
            var decisions = plan.Workflows.SelectMany(w => Enumerate(w.Steps.Concat(w.Finally))).Where(n => n.Kind == "decision").Where(n =>
                n.Outcomes.Where(o => !o.IsDefault).Select(o => o.Key).Order(StringComparer.Ordinal).SequenceEqual(activation.AllowedValues.Order(StringComparer.Ordinal)) &&
                group.All(c => n.Outcomes.Count(o => !o.IsDefault && o.Key == c.Activation!.BranchValue && Enumerate(o.Steps).Count(s => s.CapabilityId == c.Id) == 1) == 1)).ToArray();
            if (decisions.Length != 1) Error("/workflows", "Conditional group " + group.Key + " needs one decision with every exact explicit outcome [" + string.Join(", ", activation.AllowedValues) + "] and its declared actions under their branch values, plus a separate non-mutating default.", "behavior_25");
            else foreach (var outcome in decisions[0].Outcomes.Where(o => activation.NoEffectValues.Contains(o.Key, StringComparer.Ordinal)))
                if (MayMutate(outcome.Steps, new(StringComparer.Ordinal))) Error("/workflows", "Declared no-effect outcome " + outcome.Key + " must not mutate external state.", "behavior_26");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        Visit(plan.Entrypoint);
        if (plan.Workflows.Any(w => !visited.Contains(w.Key))) Error("/workflows", "Every workflow must be reachable from the entrypoint through declared workflow calls.", "behavior_27");
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
            if (active.Contains(key)) { Error("/workflows/" + key, "Workflow calls cannot form a cycle.", "behavior_28"); return; }
            if (!visited.Add(key)) return;
            active.Add(key);
            var target = plan.Workflows.FirstOrDefault(w => w.Key == key);
            if (target is not null)
                foreach (var call in Enumerate(target.Steps.Concat(target.Finally)).Where(n => n.Kind == "workflow" && n.WorkflowKey is not null)) Visit(call.WorkflowKey!);
            active.Remove(key);
        }
    }

    internal static IEnumerable<(PlanningBehaviorNode Node, string Path)> Located(List<PlanningBehaviorNode> nodes, string root)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]; var path = root + "/" + i;
            yield return (node, path);
            foreach (var child in Located(node.Steps, path + "/steps")) yield return child;
            for (var outcome = 0; outcome < node.Outcomes.Count; outcome++)
                foreach (var child in Located(node.Outcomes[outcome].Steps, path + "/outcomes/" + outcome + "/steps")) yield return child;
        }
    }

    public static PlanningGraph Display(PlanningBehaviorPlan plan, PlanningPreparation? preparation)
    {
        return new()
        {
            Summary = plan.Summary,
            Entrypoint = plan.Entrypoint,
            Workflows = plan.Workflows.Select(w => new PlanningWorkflow
            {
                Key = w.Key,
                Purpose = w.Purpose,
                OperationIds = w.OperationIds.ToList(),
                Inputs = w.Inputs.Select(p => new PlanningPort { Name = p.Name, Required = p.Required }).ToList(),
                Outputs = w.Outputs.Select(p => new PlanningOutput { Name = p.Name }).ToList(),
                Steps = w.Steps.Select(Node).ToList(),
                Finally = w.Finally.Select(Node).ToList()
            }).ToList()
        };

        PlanningNode Node(PlanningBehaviorNode n) => new()
        {
            Key = n.Key,
            Purpose = n.Purpose,
            CapabilityId = n.CapabilityId,
            OperationIds = n.OperationIds.ToList(),
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
                if (item.InputDependencies is { Count: > 0 })
                {
                    var owner = graph.Workflows.Single(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)).Contains(node));
                    var dependencies = PlanningDataflow.BusinessInputs(owner, node);
                    var root = "/workflows/" + graph.Workflows.IndexOf(owner);
                    var inputPath = PlanningGraphValidation.Located(owner.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(owner.Finally, root + "/finally")).Single(p => ReferenceEquals(p.Node, node)).Path + "/input";
                    foreach (var input in item.InputDependencies.Where(p => !dependencies.Contains(p)))
                        errors.Add(new("BUSINESS_INPUT_BINDING_MISSING", inputPath, "The accepted operation must consume business input '" + input + "'. A default or example cannot replace its dynamic binding.", Rule: "input:" + input));
                }
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
                if (extra.Type != "set" || extra.CapabilityId is not null || extra.OperationIds.Count != 0 || extra.If is not null || extra.Steps.Count + extra.Cases.Count + extra.Branches.Count + extra.Default.Count != 0) errors.Add(new("BEHAVIOR_IMPLEMENTATION_CHANGED", path + "/" + extra.Key, "Added local shaping nodes require type=set, capabilityId=null, operationIds=[], if=null, and empty control-flow children. Keep ownership on the accepted business node; do not copy it onto new result-shaping nodes."));
        }
    }

    public static IEnumerable<PlanningBehaviorNode> Enumerate(IEnumerable<PlanningBehaviorNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in Enumerate(node.Steps.Concat(node.Outcomes.SelectMany(o => o.Steps)))) yield return child; }
    }
}
