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
            if (task.Requires is not null) AddValue(task.Requires, scope, location + "/requires");
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

    internal string Phase(string id) => Tasks[id].Scope.Source.Always.Contains(Tasks[id].Task) ? "always" : "tasks";

    // Local ordering targets only. Data captures and exports are not task aliases.
    internal IReadOnlyList<string> DependencyTargets(string consumer)
    {
        var site = Tasks[consumer];
        return site.Scope.Source.Tasks.Concat(Phase(consumer) == "always" ? site.Scope.Source.Always : [])
            .Where(t => t.Id != consumer && !Reaches(t.Id, consumer, new(StringComparer.Ordinal)))
            .Select(t => t.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        bool Reaches(string id, string target, HashSet<string> visited)
        {
            if (id == target) return true;
            if (!visited.Add(id) || !Tasks.TryGetValue(id, out var producer) || producer.Scope != site.Scope) return false;
            // Nested captures can also make a container depend on a local task.
            var nested = TaskPlanRevisions.Tasks(new TaskScope { Tasks = [producer.Task] }).ToArray();
            var references = nested.SelectMany(TaskPlanCompiler.Values)
                .Concat(Scopes.Where(s => s.Owner is not null && nested.Contains(s.Owner)).SelectMany(s => s.Source.Outputs).SelectMany(o => TaskPlanCompiler.Values(o.Value)))
                .Where(v => v.Kind is "output" or "present" && v.Source is not null)
                .Select(v => v.Kind == "output" ? ExportedReference(site.Scope, v)?.Source ?? v.Source! : v.Source!);
            return producer.Task.DependsOn.Concat(references).Any(d => Reaches(d, target, visited));
        }
    }

    internal string ReferenceContext(string location, string? producer)
    {
        var taskId = location.Split('/') is ["", "tasks", var id, ..] && Tasks.ContainsKey(id) ? id : null;
        var scope = Values.GetValueOrDefault(location)?.Scope ?? (taskId is null ? null : Tasks[taskId].Scope);
        var context = $" Consumer scope: {scope?.Path ?? location}; phase: {(taskId is null ? "outputs" : Phase(taskId))}.";
        if (producer is null || !Tasks.TryGetValue(producer, out var source)) return context + $" Unknown task identity '{producer}'.";
        context += $" Producer '{producer}': scope {source.Scope.Path}; phase: {Phase(producer)}.";
        var boundaries = Scopes.Where(s => s.Owner == source.Task).Concat(scope is null ? [] : ExportRoute(scope, producer)).Distinct();
        foreach (var boundary in boundaries)
            context += $" Scope {boundary.Path} exports [{string.Join(", ", boundary.Source.Outputs.Select(o => o.Name).Order(StringComparer.Ordinal))}] through '{boundary.Owner!.Id}'.";
        return context;
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

    // Reuse only declarations already present in the business plan. Missing branch
    // values and group boundaries require explicit intent, never generated defaults.
    internal TaskValue? ExportedReference(Scope consumer, TaskValue value)
    {
        if (value.Kind != "output" || value.Source is null) return null;
        var route = ExportRoute(consumer, value.Source);
        if (route.Count == 0) return null;
        var current = value;
        foreach (var boundary in route)
        {
            var export = boundary.Source.Outputs.Where(o => o.Value.Kind == "output" &&
                o.Value.Source == current.Source && o.Value.Port == current.Port).OrderBy(o => o.Name, StringComparer.Ordinal).FirstOrDefault();
            if (export is null || boundary.Owner?.Kind == "conditional" && Scopes.Any(s => s.Owner == boundary.Owner &&
                s.Source.Outputs.Count(o => o.Name == export.Name) != 1)) return null;
            current = new() { Kind = "output", Source = boundary.Owner!.Id, Port = export.Name };
        }
        return current;
    }
}
