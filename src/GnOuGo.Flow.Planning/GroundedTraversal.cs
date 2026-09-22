using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class GroundedTraversal
{
    internal static IEnumerable<(GroundedOperation Operation, string Path)> Located(GroundedPlan intent)
    {
        foreach (var item in Located(intent.Operations, "/operations")) yield return item;
        for (var i = 0; i < intent.Subflows.Count; i++) foreach (var item in Located(intent.Subflows[i].Operations, "/subflows/" + i + "/operations")) yield return item;
    }
    private static IEnumerable<(GroundedOperation Operation, string Path)> Located(List<GroundedOperation> operations, string root)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i]; var path = root + "/" + i; yield return (op, path);
            foreach (var (children, suffix) in Children(op)) foreach (var child in Located(children, path + suffix)) yield return child;
        }
    }
    internal static IEnumerable<GroundedOperation> Operations(IEnumerable<GroundedOperation> operations)
    {
        foreach (var op in operations) { yield return op; foreach (var (children, _) in Children(op)) foreach (var child in Operations(children)) yield return child; }
    }
    private static IEnumerable<(List<GroundedOperation> Operations, string Path)> Children(GroundedOperation op)
    {
        switch (op)
        {
            case CleanupGroundedOperation c: yield return (c.Operations, "/operations"); break;
            case EachGroundedOperation e: yield return (e.Body.Operations, "/body/operations"); break;
            case ChooseGroundedOperation c: yield return (c.Then.Operations, "/then/operations"); yield return (c.Otherwise.Operations, "/otherwise/operations"); break;
            case ParallelGroundedOperation p: for (var i = 0; i < p.Branches.Count; i++) yield return (p.Branches[i].Body.Operations, "/branches/" + i + "/body/operations"); break;
        }
    }
    internal static IEnumerable<(string Workflow, string Path, GroundedBlock Block)> Blocks(GroundedPlan intent)
    {
        foreach (var (op, path) in Located(intent))
        {
            IEnumerable<(string Path, GroundedBlock Block)> blocks = op switch
            {
                EachGroundedOperation each => [(path + "/body", each.Body)],
                ChooseGroundedOperation choice => [(path + "/then", choice.Then), (path + "/otherwise", choice.Otherwise)],
                ParallelGroundedOperation parallel => parallel.Branches.Select((b, i) => (path + "/branches/" + i + "/body", b.Body)), _ => []
            };
            foreach (var block in blocks) yield return (GraphOwner(intent, block.Path + "/operations/0"), block.Path, block.Block);
        }
    }
    internal static string GraphOwner(GroundedPlan intent, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var owner = "main"; var list = intent.Operations; var position = 0;
        if (parts[0] == "subflows") { var subflow = intent.Subflows[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)]; owner = subflow.Name; list = subflow.Operations; position = 2; }
        while (position + 2 < parts.Length && parts[position] == "operations")
        {
            var op = list[int.Parse(parts[position + 1], System.Globalization.CultureInfo.InvariantCulture)]; position += 2;
            if (op is CleanupGroundedOperation cleanup) { list = cleanup.Operations; continue; }
            if (op is EachGroundedOperation each && parts[position] == "body") { owner = PlanningGraphBuilder.BlockKey(owner, "body", op.Id); list = each.Body.Operations; position++; }
            else if (op is ChooseGroundedOperation choose && parts[position] is "then" or "otherwise") { owner = PlanningGraphBuilder.BlockKey(owner, parts[position], op.Id); list = parts[position] == "then" ? choose.Then.Operations : choose.Otherwise.Operations; position++; }
            else if (op is ParallelGroundedOperation parallel && parts[position] == "branches") { var branch = parallel.Branches[int.Parse(parts[position + 1], System.Globalization.CultureInfo.InvariantCulture)]; owner = PlanningGraphBuilder.BlockKey(owner, "branch", op.Id, branch.Name); list = branch.Body.Operations; position += 3; }
            else break;
        }
        return owner;
    }
    internal static IEnumerable<GroundedValue> Values(IEnumerable<GroundedOperation> operations)
        => Operations(operations).SelectMany(o => OwnValues(o)).SelectMany(Values);
    internal static IEnumerable<GroundedValue> OwnValues(GroundedOperation operation, bool includeBlockResults = true)
    {
        if (operation.When is not null) yield return operation.When;
        IEnumerable<GroundedValue> roots = operation switch
        {
            InvokeGroundedOperation v => v.Arguments.Select(a => a.Value).Concat(v.Fallback is null ? [] : [v.Fallback]),
            CalculateGroundedOperation c => [c.Value], ValidateGroundedOperation v => [v.Value], TransformGroundedOperation t => t.Data.Select(a => a.Value),
            CallGroundedOperation c => c.Arguments.Select(a => a.Value), EachGroundedOperation e => [e.Items],
            ChooseGroundedOperation c => [c.Condition], _ => []
        };
        foreach (var root in roots) yield return root;
        if (!includeBlockResults) yield break;
        // Block exports are consumed by their container and participate in capture/dependency discovery.
        foreach (var root in operation switch {
            EachGroundedOperation e => new[] { e.Body.Result }, ChooseGroundedOperation c => new[] { c.Then.Result, c.Otherwise.Result },
            ParallelGroundedOperation p => p.Branches.Select(b => b.Body.Result), _ => [] }) yield return root;
    }
    internal static IEnumerable<GroundedValue> Values(GroundedValue value)
    {
        yield return value;
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var nested in Values(child)) yield return nested;
    }
}
