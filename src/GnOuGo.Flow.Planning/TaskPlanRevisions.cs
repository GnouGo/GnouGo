using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Only diagnosed semantic slots are editable. Revalidation never grants edit permission.</summary>
internal static class TaskPlanRevisions
{
    internal static IEnumerable<PlanTask> Tasks(TaskScope scope) => scope.Tasks.Concat(scope.Always).SelectMany(t => new[] { t }.Concat(
        (t.Body is null ? [] : Tasks(t.Body)).Concat(t.Otherwise is null ? [] : Tasks(t.Otherwise)).Concat(t.Branches.SelectMany(Tasks))));
    internal static IEnumerable<PlanTask> Tasks(TaskPlan plan) => Tasks(plan.Root).Concat(plan.Groups.SelectMany(g => Tasks(g.Body)));
    internal static IReadOnlyList<string> Scope(TaskPlan plan, IReadOnlyList<PlanningDiagnostic> findings)
    {
        if (TaskPlanCompiler.InvalidDeclarations(plan).Count > 0) return [];
        var symbols = new TaskPlanSymbols(plan);
        var inputs = plan.Inputs.Select(i => "/inputs/" + i.Name).Concat(plan.Groups.SelectMany(g => g.Inputs.Select(i => "/groups/" + g.Id + "/inputs/" + i.Name))).ToHashSet(StringComparer.Ordinal);
        var scope = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings.Where(d => d.Required && d.Code != "REVISION_SCOPE_CHANGED"))
        {
            var path = finding.Location;
            if (symbols.Values.ContainsKey(path) || inputs.Contains(path) || plan.Choices.Any(c => path == "/choices/" + c.Id)) scope.Add(path);
            else if (finding.Code == "TASK_EXPORT_REQUIRED" && symbols.Scopes.Any(s => s.Path + "/outputs" == path)) scope.Add(path);
            else if (finding.Code == "TASK_BRANCH_OUTPUTS" && symbols.Scopes.Any(s => s.Owner?.Kind == "conditional" && path.StartsWith(s.Path + "/outputs/", StringComparison.Ordinal))) scope.Add(path);
            else if (path.Split('/') is ["", "tasks", var id, var field] && symbols.Tasks.ContainsKey(id) &&
                field is "objective" or "operation" or "group" or "condition" or "items" or "dependsOn" or "maxItems" or "maxConcurrency") scope.Add(path);
            else if (finding.Code is "TASK_INPUT_REQUIRED" or "TASK_GROUP_INPUTS" && path.Split('/') is ["", "tasks", var taskId, "inputs", _] && symbols.Tasks.ContainsKey(taskId)) scope.Add(path);
        }
        return scope.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<PlanningDiagnostic> Validate(TaskPlan? previous, TaskPlan candidate, IReadOnlyList<string> scope)
    {
        var identities = TaskPlanCompiler.IdentityDiagnostics(candidate);
        if (previous is not null) identities = identities.Concat(TaskPlanCompiler.IdentityDiagnostics(previous, includeReferences: false)).Distinct().ToArray();
        if (identities.Count > 0)
        {
            foreach (var finding in identities) yield return finding;
            if (TaskPlanCompiler.InvalidDeclarations(candidate).Count > 0 || previous is not null && TaskPlanCompiler.InvalidDeclarations(previous).Count > 0) yield break;
        }
        if (previous is null) yield break;
        var symbols = new TaskPlanSymbols(previous); var revised = new TaskPlanSymbols(candidate);
        var before = JsonSerializer.SerializeToNode(previous, PlanningJsonContext.Default.TaskPlan)!;
        var after = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)!;
        var additions = new HashSet<string>(StringComparer.Ordinal);
        var permittedValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in scope)
        {
            if (symbols.Values.TryGetValue(path, out var site) && revised.Values.TryGetValue(path, out var replacement))
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                if (Related(site.Value, replacement.Value, site.Scope, used)) { permittedValues.Add(path); additions.UnionWith(used); }
            }
            // A diagnosed missing conditional counterpart has a fixed name. Its value
            // must be explicitly supplied and will undergo the complete compiler preflight.
            foreach (var boundary in symbols.Scopes.Where(s => s.Owner?.Kind == "conditional"))
            {
                var prefix = boundary.Path + "/outputs/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = path[prefix.Length..];
                var other = boundary.Owner!.Body == boundary.Source ? boundary.Owner.Otherwise : boundary.Owner.Body;
                if (boundary.Source.Outputs.All(o => o.Name != name) && other?.Outputs.Any(o => o.Name == name) == true) additions.Add(path);
            }
            if (path.Split('/') is ["", "tasks", var id, "inputs", var input] && symbols.Tasks.TryGetValue(id, out var task) && task.Task.Inputs.All(i => i.Name != input)) additions.Add(path);
        }
        Mask(before, "", false); Mask(after, "", true);
        foreach (var location in Differences(before, after, "").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            yield return new("REVISION_SCOPE_CHANGED", location, "This slot is outside the permitted repair. Preserve unaffected tasks, business interfaces, choices and ordering.");

        bool Related(TaskValue original, TaskValue replacement, TaskPlanSymbols.Scope owner, HashSet<string> used, bool preserve = false)
        {
            var routes = original.Kind == "output" && original.Source is { } producer ? symbols.ExportRoute(owner, producer) : [];
            if (routes.Count > 0)
            {
                if (Same(original, replacement)) return true; // Unchanged errors remain errors; no addition is authorized.
                var current = replacement;
                foreach (var boundary in routes.Reverse())
                {
                    if (!scope.Contains(boundary.Path + "/outputs") || current.Kind != "output" || current.Source != boundary.Owner!.Id || current.Port is null) return false;
                    var replacementScope = revised.Scopes.SingleOrDefault(s => s.Path == boundary.Path);
                    var exports = replacementScope?.Source.Outputs.Where(o => o.Name == current.Port).ToArray();
                    if (exports is not { Length: 1 }) return false;
                    if (boundary.Source.Outputs.All(o => o.Name != current.Port)) used.Add(boundary.Path + "/outputs/" + current.Port);
                    if (boundary.Owner.Kind == "conditional")
                    {
                        var other = symbols.Scopes.Single(s => s.Owner == boundary.Owner && s != boundary);
                        var counterpart = revised.Scopes.SingleOrDefault(s => s.Path == other.Path);
                        if (!scope.Contains(other.Path + "/outputs") || counterpart?.Source.Outputs.Count(o => o.Name == current.Port) != 1) return false;
                        if (other.Source.Outputs.All(o => o.Name != current.Port)) used.Add(other.Path + "/outputs/" + current.Port);
                    }
                    current = exports[0].Value;
                }
                return Same(original, current);
            }
            // A composite's unaffected members cannot hide unrelated edits when only
            // descendant references need an export route.
            if (TaskPlanCompiler.Values(original).Any(v => v.Kind == "output" && v.Source is { } id && symbols.ExportRoute(owner, id).Count > 0))
            {
                if (original.Kind != replacement.Kind || original.Members.Count != replacement.Members.Count || original.Items.Count != replacement.Items.Count) return false;
                for (var i = 0; i < original.Members.Count; i++)
                    if (original.Members[i].Name != replacement.Members[i].Name || !Related(original.Members[i].Value, replacement.Members[i].Value, owner, used, true)) return false;
                for (var i = 0; i < original.Items.Count; i++) if (!Related(original.Items[i], replacement.Items[i], owner, used, true)) return false;
                var a = JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.TaskValue)!.AsObject();
                var b = JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue)!.AsObject();
                a.Remove("members"); a.Remove("items"); b.Remove("members"); b.Remove("items");
                return JsonNode.DeepEquals(a, b);
            }
            return !preserve || Same(original, replacement);
        }
        void Mask(JsonNode? node, string path, bool updated)
        {
            if (node is JsonArray list)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var location = Element(path, list[i], i);
                    if (updated && additions.Contains(location)) { list.RemoveAt(i); continue; }
                    Mask(list[i], location, updated);
                }
                return;
            }
            if (node is not JsonObject obj) return;
            if (obj["name"] is not null && obj.ContainsKey("value") && permittedValues.Contains(path)) { obj["value"] = null; return; }
            if (obj["name"] is not null && obj.ContainsKey("required") && scope.Contains(path))
            { foreach (var key in obj.Select(p => p.Key).Where(k => k != "name").ToArray()) obj.Remove(key); return; }
            if (obj["id"] is not null && path.StartsWith("/choices/", StringComparison.Ordinal) && scope.Contains(path))
            { foreach (var key in obj.Select(p => p.Key).Where(k => k is not ("id" or "selected")).ToArray()) obj.Remove(key); return; }
            foreach (var (key, child) in obj.ToArray())
            {
                var location = path + "/" + key;
                if (scope.Contains(location) && (permittedValues.Contains(location) || location.Split('/') is ["", "tasks", _, var field] &&
                    field is "objective" or "operation" or "group" or "dependsOn" or "maxItems" or "maxConcurrency")) obj[key] = null;
                else Mask(child, location, updated);
            }
        }
    }
    private static bool Same(TaskValue left, TaskValue right) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left, PlanningJsonContext.Default.TaskValue), JsonSerializer.SerializeToNode(right, PlanningJsonContext.Default.TaskValue));
    private static string Element(string path, JsonNode? node, int index)
    {
        var collection = path.Split('/')[^1];
        if (collection is "tasks" or "always" && node?["id"] is { } task) return "/tasks/" + task;
        return path + "/" + ((node as JsonObject)?[collection is "groups" or "choices" ? "id" : "name"]?.ToString() ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static IEnumerable<string> Differences(JsonNode? left, JsonNode? right, string path)
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
                foreach (var difference in Differences(aList[i], bList[i], Element(path, aList[i], i))) yield return difference;
        }
        else yield return path;
    }
}
