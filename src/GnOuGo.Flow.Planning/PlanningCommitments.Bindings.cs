using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// A bounded traversal of existing symbols/exports, not a second plan or executable graph.
internal sealed class CommitmentBindings
{
    internal enum Evidence { Established, Disconnected, Unknown }
    internal sealed record Occurrence(PlanTask Task, bool Conditional);
    private readonly TaskPlan _plan;
    private readonly TaskPlanSymbols _symbols;
    private readonly Dictionary<string, Occurrence> _reached = new(StringComparer.Ordinal);
    private readonly Dictionary<TaskScope, int> _visits = [];
    private readonly HashSet<(TaskValue, TaskPlanSymbols.Scope)> _tracing = [];

    internal CommitmentBindings(TaskPlan plan)
    {
        _plan = plan; _symbols = new(plan); Visit(plan.Root, false);
    }

    internal List<Occurrence> Operations(string operation) => _reached.Values
        .Where(o => o.Task.Kind == "operation" && o.Task.Operation == operation).ToList();

    internal Evidence Result(TaskValue value, string operation, string port) =>
        Trace(value, _symbols.Scopes.Single(s => ReferenceEquals(s.Source, _plan.Root)), [], operation, port);

    private void Visit(TaskScope scope, bool conditional)
    {
        var visits = _visits.GetValueOrDefault(scope);
        if (visits >= 2) return;
        _visits[scope] = visits + 1;
        conditional |= visits > 0;
        foreach (var task in scope.Tasks.Concat(scope.Always))
        {
            var uncertain = conditional || task.Kind is "conditional" or "foreach";
            _reached[task.Id] = new(task, uncertain || _reached.ContainsKey(task.Id));
            if (task.Kind == "call" && _plan.Groups.SingleOrDefault(g => g.Id == task.Group) is { } group) Visit(group.Body, uncertain);
            if (task.Body is not null) Visit(task.Body, uncertain);
            if (task.Otherwise is not null) Visit(task.Otherwise, true);
            foreach (var branch in task.Branches) Visit(branch, uncertain);
        }
    }

    private Evidence Trace(TaskValue value, TaskPlanSymbols.Scope scope, IReadOnlyList<string?> path, string operation, string port)
    {
        if (!_tracing.Add((value, scope))) return Evidence.Unknown;
        try
        {
            switch (value.Kind)
            {
                case "field":
                    return value.Items.Count == 1 && value.Port is not null
                        ? Trace(value.Items[0], scope, new[] { value.Port }.Concat(path).ToArray(), operation, port) : Evidence.Unknown;
                case "object":
                    return path.Count == 0 ? All(value.Members.Select(m => Trace(m.Value, scope, [], operation, port))) :
                        path[0] is { } name && value.Members.SingleOrDefault(m => m.Name == name) is { } member
                            ? Trace(member.Value, scope, path.Skip(1).ToArray(), operation, port) : Evidence.Disconnected;
                case "array":
                    if (path.Count == 0) return All(value.Items.Select(v => Trace(v, scope, [], operation, port)));
                    if (path[0] is null) return All(value.Items.Select(v => Trace(v, scope, path.Skip(1).ToArray(), operation, port)));
                    return int.TryParse(path[0], out var index) && index >= 0 && index < value.Items.Count
                        ? Trace(value.Items[index], scope, path.Skip(1).ToArray(), operation, port) : Evidence.Unknown;
                case "lookup":
                    // Reconnection preserves records, not the source of the selection IDs.
                    return value.Items.Count == 2 ? Trace(value.Items[0], scope,
                        path.Count == 0 ? [] : new string?[] { null }.Concat(path.Skip(1)).ToArray(), operation, port) : Evidence.Unknown;
                case "flatten":
                    return value.Items.Count == 1 ? Trace(value.Items[0], scope,
                        path.Count == 0 ? [] : new string?[] { null, null }.Concat(path.Skip(1)).ToArray(), operation, port) : Evidence.Unknown;
                case "item":
                    var iteration = scope;
                    while (iteration.Owner?.Kind != "foreach" && iteration.Parent is not null) iteration = iteration.Parent;
                    return iteration.Owner?.Items is { } items && iteration.Parent is not null
                        ? Trace(items, iteration.Parent, new string?[] { null }.Concat(path).ToArray(), operation, port) : Evidence.Unknown;
                case "input":
                    var root = scope; while (root.Parent is not null) root = root.Parent;
                    var group = _plan.Groups.SingleOrDefault(g => ReferenceEquals(g.Body, root.Source));
                    if (group is null) return Evidence.Disconnected;
                    var callers = _reached.Values.Where(o => o.Task.Kind == "call" && o.Task.Group == group.Id).ToArray();
                    if (callers.Length != 1 || callers[0].Conditional) return Evidence.Unknown;
                    var input = callers[0].Task.Inputs.SingleOrDefault(i => i.Name == value.Source);
                    return input is null ? Evidence.Unknown : Trace(input.Value, _symbols.Tasks[callers[0].Task.Id].Scope, path, operation, port);
                case "output":
                    if (value.Source is null || !_symbols.Tasks.TryGetValue(value.Source, out var source) || !_reached.ContainsKey(value.Source)) return Evidence.Unknown;
                    var selection = value.Port is null ? path : new[] { value.Port }.Concat(path).ToArray();
                    var task = source.Task;
                    if (task.Kind == "operation")
                        return task.Operation == operation && selection.Count > 0 && selection[0] == port
                            ? Evidence.Established : Evidence.Disconnected;
                    if (task.Kind == "value") return Export(task.Outputs, source.Scope, selection, operation, port);
                    if (task.Kind == "sequence" && task.Body is { } body) return ExportScope(body, selection, operation, port);
                    if (task.Kind == "call" && _plan.Groups.SingleOrDefault(g => g.Id == task.Group) is { } target)
                        return ExportScope(target.Body, selection, operation, port);
                    if (task.Kind == "conditional")
                    {
                        var alternatives = new[] { task.Body, task.Otherwise }.OfType<TaskScope>().Select(s => ExportScope(s, selection, operation, port)).ToArray();
                        return alternatives.Length == 2 && alternatives.All(e => e == Evidence.Disconnected) ? Evidence.Disconnected : Evidence.Unknown;
                    }
                    if (task.Kind == "foreach" && task.Body is { } loop && selection.Count > 0)
                    {
                        var nested = ExportScope(loop, new[] { selection[0] }.Concat(selection.Count > 1 ? selection.Skip(2) : []).ToArray(), operation, port);
                        return nested == Evidence.Disconnected ? nested : Evidence.Unknown; // Empty collections/per-item execution need evidence.
                    }
                    if (task.Kind == "parallel")
                    {
                        var branches = task.Branches.Where(b => selection.Count > 0 && b.Outputs.Any(o => o.Name == selection[0])).ToArray();
                        return branches.Length == 1 ? ExportScope(branches[0], selection, operation, port) : Evidence.Unknown;
                    }
                    if (task.Kind == "transform")
                    {
                        // Inference cannot certify an exact producer-owned result. A dependency
                        // can justify uncertainty, never verification of a generated replacement.
                        var dependencies = task.Inputs.Select(i => Trace(i.Value, source.Scope, [], operation, port)).ToArray();
                        return dependencies.All(e => e == Evidence.Disconnected) ? Evidence.Disconnected : Evidence.Unknown;
                    }
                    return Evidence.Unknown;
                case "string": case "number": case "boolean": case "null": case "index":
                    return Evidence.Disconnected;
                default: return Evidence.Unknown;
            }
        }
        finally { _tracing.Remove((value, scope)); }
    }

    private Evidence ExportScope(TaskScope source, IReadOnlyList<string?> path, string operation, string port) =>
        Export(source.Outputs, _symbols.Scopes.Single(s => ReferenceEquals(s.Source, source)), path, operation, port);

    private Evidence Export(List<TaskOutput> outputs, TaskPlanSymbols.Scope scope, IReadOnlyList<string?> path, string operation, string port) =>
        path.Count == 0 ? All(outputs.Select(o => Trace(o.Value, scope, [], operation, port))) :
        outputs.SingleOrDefault(o => o.Name == path[0]) is { } export ? Trace(export.Value, scope, path.Skip(1).ToArray(), operation, port) : Evidence.Unknown;

    private static Evidence All(IEnumerable<Evidence> values)
    {
        var array = values.ToArray();
        if (array.Length == 0) return Evidence.Unknown;
        if (array.Any(e => e == Evidence.Disconnected)) return Evidence.Disconnected;
        return array.All(e => e == Evidence.Established) ? Evidence.Established : Evidence.Unknown;
    }
}
