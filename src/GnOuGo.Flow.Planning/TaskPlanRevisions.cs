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
            if (finding.Location.StartsWith("/tasks/", StringComparison.Ordinal) && finding.Location.Contains("/outputs/", StringComparison.Ordinal)) affected.Add(finding.Location);
            if (finding.Location.StartsWith("/groups/", StringComparison.Ordinal) &&
                (finding.Location.Contains("/inputs/", StringComparison.Ordinal) || finding.Location.Contains("/outputs/", StringComparison.Ordinal))) affected.Add(finding.Location);
            if (finding.Location.StartsWith("/choices/", StringComparison.Ordinal)) affected.Add(finding.Location);
        }
        if (affected.Count == 0) affected.UnionWith(tasks.Select(t => t.Id));
        bool changed;
        do
        {
            changed = false;
            foreach (var (task, inputScope) in Tasks(plan.Root).Select(t => (t, "/inputs/"))
                .Concat(plan.Groups.SelectMany(g => Tasks(g.Body).Select(t => (t, "/groups/" + g.Id + "/inputs/")))))
                if (task.DependsOn.Any(affected.Contains) || TaskPlanCompiler.Values(task).Any(v =>
                    v.Source is not null && (v.Kind is "output" or "present" && affected.Contains(v.Source) || v.Kind == "input" && affected.Contains(inputScope + v.Source))) ||
                    task.Body is not null && Tasks(task.Body).Any(t => affected.Contains(t.Id)) ||
                    task.Otherwise is not null && Tasks(task.Otherwise).Any(t => affected.Contains(t.Id)) ||
                    task.Branches.SelectMany(Tasks).Any(t => affected.Contains(t.Id)) ||
                    task.Kind == "call" && plan.Groups.Where(g => g.Id == task.Group).Any(g => Tasks(g.Body).Any(t => affected.Contains(t.Id)) ||
                        affected.Any(p => p.StartsWith("/groups/" + g.Id + "/", StringComparison.Ordinal))))
                    changed |= affected.Add(task.Id);
        } while (changed);
        return affected.Order(StringComparer.Ordinal).ToArray();
    }
    internal static IEnumerable<PlanningDiagnostic> Validate(TaskPlan? previous, TaskPlan candidate, IReadOnlyList<string> scope)
    {
        if (previous is null || scope.Count == 0) yield break;
        var before = JsonSerializer.SerializeToNode(previous, PlanningJsonContext.Default.TaskPlan)!;
        var after = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)!;
        var baseline = before.DeepClone();
        var outputSlots = new HashSet<string>(StringComparer.Ordinal);
        FindOutputs(before, ""); Mask(before, ""); Mask(after, "");
        foreach (var location in Differences(before, after, "").Distinct(StringComparer.Ordinal))
            yield return new("REVISION_SCOPE_CHANGED", location, "This slot is outside the permitted repair. Preserve unaffected tasks, business interfaces, choices and ordering.");
        void FindOutputs(JsonNode? node, string path)
        {
            if (node is JsonArray array) { for (var i = 0; i < array.Count; i++) FindOutputs(array[i], path + "/" + i); return; }
            if (node is not JsonObject obj) return;
            if (obj["outputs"] is JsonArray outputs)
                for (var i = 0; i < outputs.Count; i++)
                    if (UsesAffected(outputs[i]?["value"], InputScope(path)) || scope.Contains(Location(path + "/outputs/" + i))) outputSlots.Add(path + "/outputs/" + i);
            foreach (var (key, child) in obj) FindOutputs(child, path + "/" + key);
        }
        void Mask(JsonNode? node, string path)
        {
            if (node is JsonArray array) { for (var i = 0; i < array.Count; i++) Mask(array[i], path + "/" + i); return; }
            if (node is not JsonObject obj) return;
            if (obj["id"] is { } id && obj["kind"] is not null && scope.Contains(id.ToString()))
            {
                // A dependent container is invalidated with its child, but does not confer
                // permission to replace unaffected descendants or their control-flow scope.
                var container = obj["body"] is not null || obj["otherwise"] is not null || obj["branches"] is JsonArray { Count: > 0 };
                foreach (var key in obj.Select(p => p.Key).ToArray())
                    if (key != "id" && !(container && key is "kind" or "body" or "otherwise" or "branches")) obj.Remove(key);
            }
            if (obj["name"] is { } input && IsInput(path) && scope.Contains(Location(path)))
            { var name = input.ToString(); obj.Clear(); obj["name"] = name; return; }
            if (path.StartsWith("/choices/", StringComparison.Ordinal) && obj["id"] is { } choice && scope.Contains("/choices/" + choice))
            { var name = choice.ToString(); obj.Clear(); obj["id"] = name; return; }
            if (outputSlots.Contains(path)) obj["value"] = null;
            foreach (var (key, child) in obj) Mask(child, path + "/" + key);
        }
        bool UsesAffected(JsonNode? node, string inputScope) => node is JsonObject obj &&
            (obj["source"] is { } source && (obj["kind"]?.ToString() is "output" or "present" && scope.Contains(source.ToString()) ||
                obj["kind"]?.ToString() == "input" && scope.Contains(inputScope + source)) || obj.Any(p => UsesAffected(p.Value, inputScope))) ||
            node is JsonArray array && array.Any(n => UsesAffected(n, inputScope));
        bool IsInput(string path) => path.StartsWith("/inputs/", StringComparison.Ordinal) ||
            path.StartsWith("/groups/", StringComparison.Ordinal) && path.Split('/') is ["", "groups", _, "inputs", _];
        string InputScope(string path) => path.StartsWith("/groups/", StringComparison.Ordinal)
            ? "/groups/" + previous.Groups[int.Parse(path.Split('/')[2], System.Globalization.CultureInfo.InvariantCulture)].Id + "/inputs/" : "/inputs/";
        string Location(string path)
        {
            // Convert serialized array positions to the same stable business locations
            // used by the compiler. Read the immutable baseline, not the masked copy.
            JsonNode? node = baseline;
            var location = ""; var parts = path.Split('/').Skip(1).ToArray();
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (node is JsonArray array && int.TryParse(part, out var index))
                {
                    node = index < array.Count ? array[index] : null;
                    var collection = i > 0 ? parts[i - 1] : "";
                    if (collection is "tasks" or "always" && node?["id"] is { } taskId) location = "/tasks/" + taskId;
                    else location += "/" + ((node as JsonObject)?[collection is "groups" or "choices" ? "id" : "name"]?.ToString() ?? part);
                }
                else { node = node is JsonObject obj ? obj[part] : null; location += "/" + part; }
            }
            return location;
        }
        IEnumerable<string> Differences(JsonNode? left, JsonNode? right, string path)
        {
            if (JsonNode.DeepEquals(left, right)) yield break;
            if (left is JsonObject a && right is JsonObject b)
            {
                foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key), StringComparer.Ordinal))
                    foreach (var difference in Differences(a[key], b[key], path + "/" + key)) yield return difference;
            }
            else if (left is JsonArray aList && right is JsonArray bList && aList.Count == bList.Count)
            {
                for (var i = 0; i < aList.Count; i++)
                    foreach (var difference in Differences(aList[i], bList[i], path + "/" + i)) yield return difference;
            }
            else yield return Location(path);
        }
    }
}
