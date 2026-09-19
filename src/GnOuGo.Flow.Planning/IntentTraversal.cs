using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class IntentTraversal
{
    internal static IEnumerable<(IntentOperation Operation, string Path)> Located(WorkflowIntentPlan intent)
    {
        foreach (var item in Located(intent.Operations, "/operations")) yield return item;
        for (var i = 0; i < intent.Subflows.Count; i++) foreach (var item in Located(intent.Subflows[i].Operations, "/subflows/" + i + "/operations")) yield return item;
    }
    private static IEnumerable<(IntentOperation Operation, string Path)> Located(List<IntentOperation> operations, string root)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i]; var path = root + "/" + i; yield return (op, path);
            foreach (var (children, suffix) in Children(op)) foreach (var child in Located(children, path + suffix)) yield return child;
        }
    }
    internal static IEnumerable<IntentOperation> Operations(IEnumerable<IntentOperation> operations)
    {
        foreach (var op in operations) { yield return op; foreach (var (children, _) in Children(op)) foreach (var child in Operations(children)) yield return child; }
    }
    private static IEnumerable<(List<IntentOperation> Operations, string Path)> Children(IntentOperation op)
    {
        switch (op)
        {
            case CleanupIntentOperation c: yield return (c.Operations, "/operations"); break;
            case EachIntentOperation e: yield return (e.Body.Operations, "/body/operations"); break;
            case ChooseIntentOperation c: yield return (c.Then.Operations, "/then/operations"); yield return (c.Otherwise.Operations, "/otherwise/operations"); break;
            case ParallelIntentOperation p: for (var i = 0; i < p.Branches.Count; i++) yield return (p.Branches[i].Body.Operations, "/branches/" + i + "/body/operations"); break;
        }
    }
    internal static IEnumerable<IntentValue> Values(IEnumerable<IntentOperation> operations)
    {
        foreach (var operation in Operations(operations))
        {
            if (operation.When is not null) foreach (var value in Values(operation.When)) yield return value;
            IEnumerable<IntentValue> roots = operation switch
            {
                InvokeIntentOperation v => v.Arguments.Select(a => a.Value).Concat(v.Fallback is null ? [] : [v.Fallback]),
                CalculateIntentOperation c => [c.Value], TransformIntentOperation t => t.Data.Select(a => a.Value),
                CallIntentOperation c => c.Arguments.Select(a => a.Value), EachIntentOperation e => [e.Items, e.Body.Result],
                ChooseIntentOperation c => [c.Condition, c.Then.Result, c.Otherwise.Result], ParallelIntentOperation p => p.Branches.Select(b => b.Body.Result), _ => []
            };
            foreach (var root in roots) foreach (var value in Values(root)) yield return value;
        }
    }
    internal static IEnumerable<IntentValue> Values(IntentValue value)
    {
        yield return value;
        foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var nested in Values(child)) yield return nested;
    }
}
