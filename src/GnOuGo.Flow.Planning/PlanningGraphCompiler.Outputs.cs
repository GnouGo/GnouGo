using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class PlanningGraphCompiler
{
    private static void PrepareTerminalOutput(PlanningWorkflow workflow, LoweringScope scope)
    {
        // One output preserves the order of all intermediate checks and its final
        // contract. Its enclosing call (or terminal result) is the durable owner.
        // No finalizer, handler, later work or identity observer may see this move.
        if (workflow.Finally.Count != 0 || workflow.Outputs.Count != 1 || workflow.Steps.Count == 0 ||
            scope.Nodes.Values.Any(n => n.OnError.Count != 0)) return;
        if (LowerSchema(workflow.Outputs[0].Schema, scope.Catalog)["type"]?.ToString() == "any") return;
        var last = workflow.Steps[^1];
        var group = scope.FusedOutputs.GetValueOrDefault(last.Key) ?? [last.Key];
        if (!workflow.Steps.TakeLast(group.Length).Select(n => n.Key).SequenceEqual(group)) return;
        if (group.Any(key => scope.Nodes[key] is not { CapabilityId: null, If: null, Expr: null, Output: null, Retry: null } node ||
            node.Input.Kind == "dynamic_mapping" ||
            !(node.Type == "set" && node.Steps.Count + node.Cases.Count + node.Branches.Count + node.Default.Count == 0 ||
              node.InternalRole is "typed_projection" or "typed_index_projection"))) return;
        bool Reads(PlanningValue v) => v.Source is { } source && group.Contains(source, StringComparer.Ordinal);
        var outputs = workflow.Outputs.SelectMany(o => BindingValues(o.Value)).ToArray();
        if (!outputs.Any(v => v.Kind == "output" && Reads(v)) || outputs.Any(v => v.Kind is "expression" or "template" or "present")) return;
        var other = scope.Nodes.Values.Where(n => !group.Contains(n.Key, StringComparer.Ordinal)).SelectMany(n =>
            BindingValues(n.Input).Concat(BindingValues(n.Expr)).Concat(BindingValues(n.If)).Concat(n.Cases.SelectMany(c => BindingValues(c.When))));
        if (other.Any(v => Reads(v) || v.Kind is "expression" or "template")) return;
        foreach (var key in group) scope.FusedOutputs[key] = group;
        scope.TerminalOutput = group;
    }
}
