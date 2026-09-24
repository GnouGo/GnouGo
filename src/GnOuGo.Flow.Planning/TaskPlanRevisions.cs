using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Repairs edit business tasks and dependent outputs, never generated executor plumbing.</summary>
internal static class TaskPlanRevisions
{
    internal static IEnumerable<PlanTask> Tasks(TaskScope scope) => scope.Tasks.Concat(scope.Always).SelectMany(t => new[] { t }.Concat(
        (t.Body is null ? [] : Tasks(t.Body)).Concat(t.Otherwise is null ? [] : Tasks(t.Otherwise)).Concat(t.Branches.SelectMany(Tasks))));
    internal static IEnumerable<PlanTask> Tasks(TaskPlan plan) => Tasks(plan.Root).Concat(plan.Groups.SelectMany(g => Tasks(g.Body)));
    internal static IReadOnlyList<string> Scope(TaskPlan plan, IReadOnlyList<PlanningDiagnostic> findings)
    {
        var tasks = Tasks(plan).ToArray();
        var affected = findings.Where(d => d.Required).Select(d => d.Location.Split('/')).Where(p => p.Length > 2 && p[1] == "tasks")
            .Select(p => p[2]).Where(id => tasks.Any(t => t.Id == id)).ToHashSet(StringComparer.Ordinal);
        foreach (var finding in findings.Where(d => d.Required))
        {
            if (finding.Location.StartsWith("/inputs/", StringComparison.Ordinal)) affected.Add(finding.Location);
            if (finding.Location.StartsWith("/root/outputs/", StringComparison.Ordinal)) affected.Add(finding.Location);
            if (finding.Location.StartsWith("/choices/", StringComparison.Ordinal)) affected.Add(finding.Location);
        }
        if (affected.Count == 0) affected.UnionWith(tasks.Select(t => t.Id));
        bool changed;
        do
        {
            changed = false;
            foreach (var task in tasks)
                if (task.DependsOn.Any(affected.Contains) || TaskPlanCompiler.Values(task).Any(v =>
                    v.Source is not null && (v.Kind is "output" or "present" && affected.Contains(v.Source) || v.Kind == "input" && affected.Contains("/inputs/" + v.Source))) ||
                    task.Body is not null && Tasks(task.Body).Any(t => affected.Contains(t.Id)) ||
                    task.Otherwise is not null && Tasks(task.Otherwise).Any(t => affected.Contains(t.Id)) ||
                    task.Branches.SelectMany(Tasks).Any(t => affected.Contains(t.Id)) ||
                    task.Kind == "call" && plan.Groups.Where(g => g.Id == task.Group).Any(g => Tasks(g.Body).Any(t => affected.Contains(t.Id))))
                    changed |= affected.Add(task.Id);
        } while (changed);
        return affected.Order(StringComparer.Ordinal).ToArray();
    }
    internal static IEnumerable<PlanningDiagnostic> Validate(TaskPlan? previous, TaskPlan candidate, IReadOnlyList<string> scope)
    {
        if (previous is null || scope.Count == 0) yield break;
        var before = JsonSerializer.SerializeToNode(previous, PlanningJsonContext.Default.TaskPlan)!;
        var after = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)!;
        var outputSlots = new HashSet<string>(StringComparer.Ordinal);
        FindOutputs(before, ""); Mask(before, ""); Mask(after, "");
        if (!JsonNode.DeepEquals(before, after))
            yield return new("REVISION_SCOPE_CHANGED", "/tasks", "Preserve all unaffected tasks, business interfaces, choices and ordering. Repair only the permitted tasks or their dependent output bindings.");
        void FindOutputs(JsonNode? node, string path)
        {
            if (node is JsonArray array) { for (var i = 0; i < array.Count; i++) FindOutputs(array[i], path + "/" + i); return; }
            if (node is not JsonObject obj) return;
            if (obj["outputs"] is JsonArray outputs)
                for (var i = 0; i < outputs.Count; i++)
                    if (UsesAffected(outputs[i]?["value"]) || scope.Contains(path + "/outputs/" + outputs[i]?["name"])) outputSlots.Add(path + "/outputs/" + i);
            foreach (var (key, child) in obj) FindOutputs(child, path + "/" + key);
        }
        void Mask(JsonNode? node, string path)
        {
            if (node is JsonArray array) { for (var i = 0; i < array.Count; i++) Mask(array[i], path + "/" + i); return; }
            if (node is not JsonObject obj) return;
            if (obj["id"] is { } id && obj["kind"] is not null && scope.Contains(id.ToString()))
            { var name = id.ToString(); obj.Clear(); obj["id"] = name; return; }
            if (path.StartsWith("/inputs/", StringComparison.Ordinal) && obj["name"] is { } input && scope.Contains("/inputs/" + input))
            { var name = input.ToString(); obj.Clear(); obj["name"] = name; return; }
            if (path.StartsWith("/choices/", StringComparison.Ordinal) && obj["id"] is { } choice && scope.Contains("/choices/" + choice))
            { var name = choice.ToString(); obj.Clear(); obj["id"] = name; return; }
            if (outputSlots.Contains(path)) obj["value"] = null;
            foreach (var (key, child) in obj) Mask(child, path + "/" + key);
        }
        bool UsesAffected(JsonNode? node) => node is JsonObject obj &&
            (obj["kind"]?.ToString() is "output" or "present" && obj["source"] is { } source && scope.Contains(source.ToString()) || obj.Any(p => UsesAffected(p.Value))) ||
            node is JsonArray array && array.Any(UsesAffected);
    }
}
