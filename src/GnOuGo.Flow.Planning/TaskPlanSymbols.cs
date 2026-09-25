using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Transient source locations and ownership, referencing the original TaskPlan. Never serialized.</summary>
internal sealed class TaskPlanSymbols
{
    internal sealed record Scope(TaskScope Source, string Path, Scope? Parent, PlanTask? Owner);
    internal sealed record Site(TaskValue Value, Scope Scope, string Path);
    internal List<Scope> Scopes { get; } = [];
    internal Dictionary<string, (PlanTask Task, Scope Scope)> Tasks { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Site> Values { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> InvalidIds { get; }
    private readonly HashSet<string> _ambiguousValues = new(StringComparer.Ordinal);

    internal TaskPlanSymbols(TaskPlan plan)
    {
        InvalidIds = TaskPlanCompiler.InvalidDeclarations(plan);
        Add(plan.Root, "/root", null, null);
        foreach (var group in plan.Groups.Where(g => !InvalidIds.Contains(g.Id))) Add(group.Body, "/groups/" + group.Id + "/body", null, null);
    }
    private void Add(TaskScope source, string path, Scope? parent, PlanTask? owner)
    {
        var scope = new Scope(source, path, parent, owner); Scopes.Add(scope);
        foreach (var output in source.Outputs) AddValue(output.Value, scope, path + "/outputs/" + output.Name);
        foreach (var task in source.Tasks.Concat(source.Always))
        {
            if (InvalidIds.Contains(task.Id)) continue;
            Tasks.Add(task.Id, (task, scope));
            var location = "/tasks/" + task.Id;
            foreach (var input in task.Inputs) AddValue(input.Value, scope, location + "/inputs/" + input.Name);
            foreach (var output in task.Outputs) AddValue(output.Value, scope, location + "/outputs/" + output.Name);
            if (task.Condition is not null) AddValue(task.Condition, scope, location + "/condition");
            if (task.Items is not null) AddValue(task.Items, scope, location + "/items");
            if (task.Body is not null) Add(task.Body, location + "/body", scope, task);
            if (task.Otherwise is not null) Add(task.Otherwise, location + "/otherwise", scope, task);
            for (var i = 0; i < task.Branches.Count; i++) Add(task.Branches[i], location + "/branches/" + i, scope, task);
        }
    }

    private void AddValue(TaskValue value, Scope scope, string path)
    {
        if (_ambiguousValues.Contains(path)) return;
        if (Values.TryAdd(path, new(value, scope, path))) return;
        // Business-port names remain unrestricted. Ambiguous display locations must
        // never become authority to edit more than one semantic slot.
        Values.Remove(path); _ambiguousValues.Add(path);
    }

    // Only a descendant-to-ancestor export chain is repairable through declarations.
    // A sibling reference, unknown ID or group boundary never grants export permission.
    internal IReadOnlyList<Scope> ExportRoute(Scope consumer, string producer)
    {
        if (!Tasks.TryGetValue(producer, out var found) || found.Scope == consumer) return [];
        var result = new List<Scope>();
        for (var current = found.Scope; current.Parent is not null; current = current.Parent)
        {
            result.Add(current);
            if (current.Parent == consumer) return result;
        }
        return [];
    }
}
