using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Checks declared business lineage against producer metadata, before lowering.</summary>
internal sealed class TaskArtifactBindings(TaskPlan plan, PlanningCatalog catalog, TaskPlanSymbols symbols)
{
    private readonly HashSet<(TaskValue Value, TaskPlanSymbols.Scope Scope, string Kind)> _visiting = [];

    internal bool Proves(TaskValue value, TaskPlanSymbols.Scope scope, IReadOnlyList<string> path, string kind) =>
        Trace(value, scope, path.Cast<string?>().ToArray(), kind);

    internal bool MissingProducer(string kind)
    {
        var operations = TaskPlanRevisions.Tasks(plan).Where(t => t.Kind == "operation").Select(t => t.Operation).Distinct(StringComparer.Ordinal);
        var resolved = new List<PlanningCapability>();
        foreach (var operation in operations)
        {
            var matches = catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == operation).ToArray();
            if (matches.Length != 1 || TaskOperations.Validate(matches[0]).Count > 0 ||
                !catalog.AllowedStepTypes.Contains(matches[0].StepType) || catalog.Policy.DeniedCapabilityIds.Contains(matches[0].Id)) return false;
            resolved.Add(matches[0]);
        }
        return !resolved.Any(c => c.ArtifactContract?.Produces.Any(p => p.Kind == kind) == true);
    }

    private bool Trace(TaskValue value, TaskPlanSymbols.Scope scope, IReadOnlyList<string?> path, string kind)
    {
        var key = (value, scope, kind);
        if (!_visiting.Add(key)) return false;
        try
        {
            switch (value.Kind)
            {
                case "flatten":
                    // Every output item retains its original two-level element origin.
                    // Unknown lengths cannot establish a fixed indexed artifact.
                    return value.Items.Count == 1 && path.Count > 0 && path[0] is null &&
                        Trace(value.Items[0], scope, new string?[] { null, null }.Concat(path.Skip(1)).ToArray(), kind);
                case "field":
                    return value.Items.Count == 1 && value.Port is not null && Trace(value.Items[0], scope, new[] { value.Port }.Concat(path).ToArray(), kind);
                case "object":
                    return path.Count > 0 && value.Members.Count(m => m.Name == path[0]) == 1 &&
                        Trace(value.Members.Single(m => m.Name == path[0]).Value, scope, path.Skip(1).ToArray(), kind);
                case "array":
                    if (path.Count == 0) return false;
                    if (path[0] is null) return value.Items.Count > 0 && value.Items.All(v => Trace(v, scope, path.Skip(1).ToArray(), kind));
                    return int.TryParse(path[0], out var i) && i >= 0 && i < value.Items.Count && Trace(value.Items[i], scope, path.Skip(1).ToArray(), kind);
                case "input":
                    var root = scope; while (root.Parent is not null) root = root.Parent;
                    var group = plan.Groups.SingleOrDefault(g => ReferenceEquals(g.Body, root.Source));
                    if (group is null) return false; // Workflow inputs and defaults establish no origin.
                    var callers = symbols.Tasks.Values.Where(t => t.Task.Kind == "call" && t.Task.Group == group.Id).ToArray();
                    return callers.Length > 0 && callers.All(c => c.Task.Inputs.Count(p => p.Name == value.Source) == 1 &&
                        Trace(c.Task.Inputs.Single(p => p.Name == value.Source).Value, c.Scope, path, kind));
                case "item":
                    var iteration = scope;
                    while (iteration.Owner?.Kind != "foreach" && iteration.Parent is not null) iteration = iteration.Parent;
                    return iteration.Owner is { Kind: "foreach", Items: { } items } && iteration.Parent is not null &&
                        Trace(items, iteration.Parent, new string?[] { null }.Concat(path).ToArray(), kind);
                case "output":
                    if (value.Source is null || !symbols.Tasks.TryGetValue(value.Source, out var source)) return false;
                    var task = source.Task;
                    var selection = value.Port is null ? path : new[] { value.Port }.Concat(path).ToArray();
                    if (task.Kind == "operation")
                    {
                        var matches = catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
                        if (matches.Length != 1 || matches[0].ArtifactContract is not { } contract) return false;
                        var selected = path;
                        if (value.Port is not null)
                        {
                            var ports = TaskOperations.Describe(matches[0]).Outputs.Where(p => p.Name == value.Port).ToArray();
                            if (ports.Length != 1) return false;
                            selected = ports[0].Path.Concat(path).ToArray();
                        }
                        return selected.All(p => p is not null) && contract.Produces.Any(p => p.Kind == kind &&
                            Decode(p.Pointer).SequenceEqual(selected, StringComparer.Ordinal));
                    }
                    if (task.Kind == "value") return Export(task.Outputs, source.Scope, selection, kind);
                    if (task.Kind == "call")
                    {
                        var target = plan.Groups.SingleOrDefault(g => g.Id == task.Group);
                        return target is not null && ScopeExport(target.Body, selection, kind);
                    }
                    if (task.Kind == "sequence") return task.Body is not null && ScopeExport(task.Body, selection, kind);
                    if (task.Kind == "conditional") return task.Body is not null && task.Otherwise is not null &&
                        ScopeExport(task.Body, selection, kind) && ScopeExport(task.Otherwise, selection, kind);
                    if (task.Kind == "parallel")
                    {
                        var branches = task.Branches.Where(b => selection.Count > 0 && b.Outputs.Any(o => o.Name == selection[0])).ToArray();
                        return branches.Length == 1 && ScopeExport(branches[0], selection, kind);
                    }
                    if (task.Kind == "foreach" && task.Body is not null && selection.Count >= 2 && selection[1] is null)
                        return ScopeExport(task.Body, new[] { selection[0] }.Concat(selection.Skip(2)).ToArray(), kind);
                    return false; // Transforms, literals and matching types never manufacture provenance.
                default: return false;
            }
        }
        finally { _visiting.Remove(key); }
    }

    private bool ScopeExport(TaskScope source, IReadOnlyList<string?> path, string kind)
    {
        var scopes = symbols.Scopes.Where(s => ReferenceEquals(s.Source, source)).ToArray();
        return scopes.Length == 1 && Export(source.Outputs, scopes[0], path, kind);
    }

    private bool Export(List<TaskOutput> outputs, TaskPlanSymbols.Scope scope, IReadOnlyList<string?> path, string kind) =>
        path.Count > 0 && outputs.Count(o => o.Name == path[0]) == 1 &&
        Trace(outputs.Single(o => o.Name == path[0]).Value, scope, path.Skip(1).ToArray(), kind);

    internal static string[] Decode(string pointer) => pointer.Length == 0 ? [] : pointer[1..].Split('/').Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();

    internal static bool ExplicitlyAbsent(TaskValue value, IReadOnlyList<string> path)
    {
        foreach (var part in path)
        {
            if (value.Kind != "object") return false;
            var fields = value.Members.Where(m => m.Name == part).ToArray();
            if (fields.Length == 0) return true;
            if (fields.Length != 1) return false;
            value = fields[0].Value;
        }
        return false; // Null is a supplied value, never omission.
    }
}
