using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningDataflowResolver
{
    internal static void Resolve(PlanningSnapshot state)
    {
        var graph = state.Graph ?? throw new InvalidOperationException("Accepted behavior has no typed graph.");
        var keys = graph.Workflows.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        var progress = graph.Workflows.Select(w => new PlanningWorkflowProgress
        {
            WorkflowKey = w.Key,
            Dependencies = PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)).Where(n => n.Type == "workflow.call")
                .Select(n => n.Input.Members.SingleOrDefault(m => m.Name == "ref")?.Value)
                .Select(v => v is { Kind: "workflow", Source: not null } ? v.Source : throw new InvalidOperationException("A workflow call needs an explicit typed callee reference."))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
        }).ToList();
        if (progress.Any(p => p.Dependencies.Any(d => !keys.Contains(d))))
            throw new InvalidOperationException("A workflow dependency has no declared callee.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Count < progress.Count)
        {
            var ready = progress.Where(p => !visited.Contains(p.WorkflowKey) && p.Dependencies.All(visited.Contains)).ToArray();
            if (ready.Length == 0) throw new InvalidOperationException("Workflow dependencies contain a cycle.");
            foreach (var p in ready) visited.Add(p.WorkflowKey);
        }
        var flow = new PlanningDataflowContract { ContractFingerprint = PlanningContext.Contracts(state) };
        foreach (var workflow in state.BehaviorPlan!.Workflows)
            foreach (var node in PlanningBehaviorPlans.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                if (node.InputDependencies is null) throw new InvalidOperationException("Every behavior node must declare its business input dependencies before construction.");
                if (node.InputDependencies.Any(name => !workflow.Inputs.Any(p => p.Name == name)))
                    throw new InvalidOperationException("A dataflow obligation refers to an undeclared business input.");
                flow.InputObligations[workflow.Key + "/" + node.Key] = node.InputDependencies.ToList();
            }
        flow.Fingerprint = PlanningGraphCompiler.Fingerprint(state.ApprovedBehaviorHash + "\n" + state.Preparation!.Fingerprint + "\n" + JsonSerializer.Serialize(flow, PlanningJsonContext.Default.PlanningDataflowContract));
        state.Construction.Dataflow = flow;
        state.Construction.Workflows = progress;
    }

    internal static void Refresh(PlanningSnapshot state)
    {
        var flow = state.Construction.Dataflow!;
        flow.Bindings.Clear(); flow.Operations.Clear();
        foreach (var workflow in state.Graph!.Workflows.Where(w => state.Construction.Workflows.Any(p => p.WorkflowKey == w.Key && p.Status != "pending")))
        {
            flow.Bindings.AddRange(PlanningDataflow.Index(workflow, state.Preparation!, state.Graph).Values);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
                flow.Operations.Add(new(workflow.Key, node.Key, PlanningDataflow.References(node.Input).Select(PlanningBindingIdentity.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), PlanningDataflow.BusinessInputs(workflow, node).Order(StringComparer.Ordinal).ToList()));
        }
        flow.GraphFingerprint = PlanningGraphCompiler.Fingerprint(state.Graph);
    }

    internal static IEnumerable<PlanningDiagnostic> Validate(PlanningSnapshot state, PlanningGraph graph)
    {
        if (state.Construction.Dataflow is null) { yield return new("DATAFLOW_REQUIRED", "/dataflow", "Resolve dataflow before construction."); yield break; }
        for (var i = 0; i < graph.Workflows.Count; i++)
        {
            var workflow = graph.Workflows[i];
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, $"/workflows/{i}/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, $"/workflows/{i}/finally")))
            {
                if (!state.Construction.Dataflow.InputObligations.TryGetValue(workflow.Key + "/" + node.Key, out var required)) continue;
                var actual = PlanningDataflow.BusinessInputs(workflow, node);
                foreach (var name in required.Where(n => !actual.Contains(n)))
                    yield return new("BUSINESS_INPUT_BINDING_MISSING", path + "/input", "This operation must depend on business input '" + name + "'.");
            }
        }
    }

    internal static bool Owns(PlanningDiagnostic diagnostic, int index, string workflow) =>
        diagnostic.Location == $"/workflows/{index}" || diagnostic.Location.StartsWith($"/workflows/{index}/", StringComparison.Ordinal) ||
        diagnostic.Location == workflow || diagnostic.Location.StartsWith(workflow + "/", StringComparison.Ordinal);
}
