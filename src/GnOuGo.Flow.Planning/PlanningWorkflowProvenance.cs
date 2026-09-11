using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Operation evidence crosses typed call arguments and returned workflow outputs.</summary>
internal static class PlanningWorkflowProvenance
{
    internal static IEnumerable<string> ReturnedOperations(PlanningGraph graph, PlanningNode call, PlanningPreparation preparation, IReadOnlyList<string>? resultPath = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<(string Workflow, string Node, string Path)>();
        void Value(PlanningWorkflow workflow, PlanningValue value)
        {
            foreach (var reference in PlanningDataflow.References(value))
                if (reference.Kind is "output" or "loop_item" or "loop_previous" or "artifact_collection" &&
                    PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == reference.Source) is { } producer)
                    Node(workflow, producer, reference.Path);
        }
        void Node(PlanningWorkflow workflow, PlanningNode node, IReadOnlyList<string>? selected = null)
        {
            if (!visited.Add((workflow.Key, node.Key, string.Join("/", selected ?? [])))) return;
            found.UnionWith(node.OperationIds);
            found.UnionWith(preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.OperationIds ?? []);
            Value(workflow, node.Input);
            foreach (var condition in PlanningDataflow.Conditions(node)) Value(workflow, condition);
            if (Target(node) is { } target && graph.Workflows.FirstOrDefault(w => w.Key == target) is { } callee)
                foreach (var output in callee.Outputs.Where(o => selected is not { Count: > 0 } || o.Name == selected[0])) Value(callee, output.Value);
            foreach (var child in node.Steps.Concat(node.Default).Concat(node.Cases.SelectMany(c => c.Steps)).Concat(node.Branches.SelectMany(b => b.Steps))) Node(workflow, child);
        }
        if (Target(call) is { } key && graph.Workflows.FirstOrDefault(w => w.Key == key) is { } called)
            foreach (var output in called.Outputs.Where(o => resultPath is not { Count: > 0 } || o.Name == resultPath[0])) Value(called, output.Value);
        return found;
    }

    internal static IEnumerable<string> RequiredAtInvocation(PlanningGraph graph, PlanningWorkflow workflow, PlanningNode consumer, PlanningPreparation preparation)
    {
        if (!graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally))).Any(n => Target(n) == workflow.Key)) yield break;
        var local = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SelectMany(n => n.OperationIds
            .Concat(preparation.Capabilities.FirstOrDefault(c => c.Id == n.CapabilityId)?.OperationIds ?? [])).ToHashSet(StringComparer.Ordinal);
        foreach (var operation in PlanningOperationCompositions.RequiredInputs(workflow, consumer, preparation))
            if (!local.Contains(operation)) yield return operation;
    }

    internal static IReadOnlyList<PlanningDiagnostic>? InputFindings(PlanningGraph graph, PlanningWorkflow callee,
        string requiredOperation, IReadOnlySet<string> consumedInputs, PlanningPreparation preparation)
    {
        if (consumedInputs.Count == 0) return null;
        // A local producer must still be consumed locally; an invocation cannot replace it.
        if (PlanningGraphCompiler.Enumerate(callee.Steps.Concat(callee.Finally)).Any(n => n.OperationIds.Contains(requiredOperation))) return null;
        var callers = graph.Workflows.SelectMany(workflow => PlanningGraphValidation.Located(workflow.Steps, "/workflows/" + graph.Workflows.IndexOf(workflow) + "/steps")
            .Concat(PlanningGraphValidation.Located(workflow.Finally, "/workflows/" + graph.Workflows.IndexOf(workflow) + "/finally"))
            .Where(p => Target(p.Node) == callee.Key).Select(p => (Workflow: workflow, p.Node, p.Path))).ToArray();
        if (callers.Length == 0) return null;
        var diagnostics = new List<PlanningDiagnostic>();
        foreach (var caller in callers)
        {
            var arguments = caller.Node.Input.Members.FirstOrDefault(m => m.Name == "args")?.Value;
            var invocation = caller.Node.OperationIds.Contains(requiredOperation);
            var proven = false;
            foreach (var name in consumedInputs)
            {
                var port = callee.Inputs.FirstOrDefault(p => p.Name == name);
                var argument = arguments?.Members.FirstOrDefault(m => m.Name == name)?.Value;
                if (port is null || argument is null) continue;
                try
                {
                    var expected = PlanningGraphCompiler.ToJsonSchema(port.Schema, preparation);
                    // Dynamic computations have no static return contract. The typed
                    // workflow.call boundary validates them before invoking the callee,
                    // just as native and MCP arguments do. Their explicit dependencies
                    // still have to establish the required operation below.
                    var actual = PlanningGraphValidation.OptionalValueContractResolver(graph, caller.Workflow, preparation)(argument);
                    var valid = PlanningGraphValidation.IsLiteral(argument)
                        ? PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(argument), expected).Count == 0
                        : actual is null
                            ? argument.Kind == "compute" && !PlanningComputations.HasNullResult(argument.Text)
                            : PlanningGraphValidation.TypesFit(actual, expected);
                    if (!valid) continue;
                    // The invocation's own dependencies are checked on the caller separately.
                    // Only the consumed argument can prove another upstream operation.
                    var operations = PlanningDataflow.OperationDependencies(caller.Workflow, caller.Node, preparation, graph, inputOverride: argument).Operations;
                    proven |= invocation || operations.Contains(requiredOperation);
                }
                catch (InvalidOperationException) { /* Unresolved argument contracts cannot establish provenance. */ }
            }
            if (!proven)
                diagnostics.Add(new("WORKFLOW_INPUT_PROVENANCE_MISSING", caller.Path + "/input",
                    "The consumed input boundary of workflow '" + callee.Key + "' must carry locked operation '" + requiredOperation +
                    "' through a schema-valid argument. Resolve this caller's arguments before validating its dependents.", Rule: "operation:" + requiredOperation));
        }
        return diagnostics;
    }

    internal static string? Target(PlanningNode node) => node.Type == "workflow.call"
        ? node.Input.Members.FirstOrDefault(m => m.Name == "ref")?.Value is { Kind: "workflow" } reference ? reference.Source : null
        : null;
}
