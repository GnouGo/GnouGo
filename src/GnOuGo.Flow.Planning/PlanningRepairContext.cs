using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Read-only slices of the host-owned plan. Context inclusion never grants edits.
internal static class PlanningRepairContext
{
    internal sealed record Selection(TaskPlanSymbols Symbols, HashSet<string> EditableTasks, HashSet<string> Tasks, HashSet<string> Scopes);
    internal static Selection Select(PlanningSession state)
    {
        var symbols = new TaskPlanSymbols(state.Plan!);
        var editable = state.RevisionScope.Select(p => p.Split('/')).Where(p => p.Length >= 3 && p[1] == "tasks")
            .Select(p => p[2]).Where(symbols.Tasks.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var tasks = editable.ToHashSet(StringComparer.Ordinal); var scopes = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Scope(TaskPlanSymbols.Scope? scope)
        {
            for (; scope is not null; scope = scope.Parent)
            {
                scopes.Add(scope.Path);
                if (scope.Owner is { } owner) tasks.Add(owner.Id);
            }
        }
        void References(TaskValue value)
        {
            foreach (var reference in TaskPlanCompiler.Values(value).Where(v => v.Kind is "output" or "present" && v.Source is not null))
                Producer(reference.Source!, reference.Port);
        }
        void Producer(string id, string? port)
        {
            if (!symbols.Tasks.TryGetValue(id, out var site) || !visited.Add(id + ":" + port)) return;
            tasks.Add(id); Scope(site.Scope);
            // Typed operations/transforms already declare their result contracts.
            // Follow only forwarding/assembly and scope exports, not execution chains.
            if (site.Task.Kind == "value")
                foreach (var output in site.Task.Outputs.Where(o => port is null || o.Name == port)) References(output.Value);
            foreach (var child in symbols.Scopes.Where(s => s.Owner == site.Task))
            {
                scopes.Add(child.Path);
                foreach (var output in child.Source.Outputs.Where(o => port is null || o.Name == port)) References(output.Value);
            }
        }
        foreach (var path in state.RevisionScope)
        {
            if (symbols.Values.TryGetValue(path, out var value)) { Scope(value.Scope); References(value.Value); }
            foreach (var scope in symbols.Scopes.Where(s => path == s.Path + "/outputs" || path.StartsWith(s.Path + "/outputs/", StringComparison.Ordinal)))
            {
                Scope(scope);
                foreach (var output in scope.Source.Outputs) References(output.Value);
            }
        }
        foreach (var id in editable)
        {
            var site = symbols.Tasks[id]; Scope(site.Scope);
            foreach (var value in site.Task.Inputs.Concat(site.Task.Outputs).Select(v => v.Value)) References(value);
            if (site.Task.Items is { } items) References(items);
            if (site.Task.Condition is { } condition) References(condition);
        }
        // Producer constraints can affect other consumers. Include their contracts
        // without authorizing them or recursively retaining unrelated work.
        var changed = state.RevisionScope.Where(p => p.Contains("/resultType", StringComparison.Ordinal)).Select(p => p.Split('/')[2]).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(changed);
        while (pending.TryDequeue(out var producer))
            foreach (var (path, site) in symbols.Values.Where(p => TaskPlanCompiler.Values(p.Value.Value).Any(v => v.Kind == "output" && v.Source == producer)))
            {
                Scope(site.Scope);
                if (path.Split('/') is ["", "tasks", var id, ..] && symbols.Tasks.TryGetValue(id, out var consumer))
                {
                    tasks.Add(id);
                    if (consumer.Task.Kind == "value" && changed.Add(id)) pending.Enqueue(id);
                }
                foreach (var boundary in symbols.Scopes.Where(s => s.Owner is not null && path.StartsWith(s.Path + "/outputs/", StringComparison.Ordinal)))
                    if (changed.Add(boundary.Owner!.Id)) pending.Enqueue(boundary.Owner.Id);
            }
        return new(symbols, editable, tasks, scopes);
    }

    internal static HashSet<string> Operations(PlanningSession state)
    {
        var selected = Select(state);
        return selected.Tasks.Select(id => selected.Symbols.Tasks[id].Task.Operation).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    internal static JsonObject Build(PlanningSession state)
    {
        var selection = Select(state); var symbols = selection.Symbols;
        var slots = PlanningRepairPatch.Slots(state, PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.AsObject());
        var taskNodes = new JsonArray(); var scopeNodes = new JsonArray();
        var inputs = new HashSet<string>(StringComparer.Ordinal); var choices = new HashSet<string>(StringComparer.Ordinal);
        void Referenced(TaskValue value)
        {
            foreach (var v in TaskPlanCompiler.Values(value))
                if (v.Kind == "input" && v.Source is not null) inputs.Add(v.Source);
                else if (v.Kind == "choice" && v.Source is not null) choices.Add(v.Source);
        }
        foreach (var id in selection.Tasks.Order(StringComparer.Ordinal))
        {
            var task = symbols.Tasks[id].Task;
            var node = JsonSerializer.SerializeToNode(task, PlanningJsonContext.Default.PlanTask)!.AsObject();
            node.Remove("body"); node.Remove("otherwise"); node.Remove("branches");
            if (!selection.EditableTasks.Contains(id) && task.Kind is "operation" or "transform") node.Remove("inputs");
            // A context task is not a replacement payload; omitted bodies stay host-owned.
            taskNodes.Add(PlanningJsonTransport.TaskPlanPart(node));
            foreach (var value in task.Inputs.Concat(task.Outputs).Select(o => o.Value)) Referenced(value);
            if (task.Items is { } items) Referenced(items);
            if (task.Condition is { } condition) Referenced(condition);
        }
        foreach (var scope in symbols.Scopes.Where(s => selection.Scopes.Contains(s.Path)).OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            var node = new JsonObject { ["location"] = scope.Path,
                ["taskOrder"] = new JsonArray(scope.Source.Tasks.Select(t => (JsonNode?)JsonValue.Create(t.Id)).ToArray()),
                ["alwaysOrder"] = new JsonArray(scope.Source.Always.Select(t => (JsonNode?)JsonValue.Create(t.Id)).ToArray()),
                ["outputs"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(scope.Source.Outputs, PlanningJsonContext.Default.ListTaskOutput)) };
            if (scope.Owner?.Kind == "foreach") node["items"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(scope.Owner.Items, PlanningJsonContext.Default.TaskValue));
            // Names of alternative ports are enough to discover an explicit binding;
            // full authoritative contracts accompany the relevant producers above.
            node["availablePorts"] = new JsonArray(scope.Source.Tasks.Where(t => !selection.Tasks.Contains(t.Id)).Select(t => (JsonNode)new JsonObject
            {
                ["task"] = t.Id,
                ["ports"] = new JsonArray(Ports(t, state).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray())
            }).ToArray());
            scopeNodes.Add((JsonNode)node);
        }
        foreach (var path in state.RevisionScope)
        {
            if (path.Split('/') is ["", "inputs", var name]) inputs.Add(name);
            if (path.Split('/') is ["", "choices", var id]) choices.Add(id);
        }
        var groups = state.Plan!.Groups.Where(g => selection.Scopes.Any(p => p.StartsWith("/groups/" + g.Id + "/", StringComparison.Ordinal)) ||
            state.RevisionScope.Any(p => p.StartsWith("/groups/" + g.Id + "/", StringComparison.Ordinal)) ||
            selection.Tasks.Any(id => symbols.Tasks[id].Task.Group == g.Id));
        return new JsonObject
        {
            ["version"] = 2, ["authority"] = PlanningRepairPatch.Authority(state, 2),
            ["slots"] = new JsonArray(slots.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["location"] = s.Location, ["kind"] = s.Kind,
                ["actions"] = new JsonArray(s.Actions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) }).ToArray()),
            ["tasks"] = taskNodes, ["scopes"] = scopeNodes,
            ["inputs"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(state.Plan.Inputs.Where(i => inputs.Contains(i.Name)).ToList(), PlanningJsonContext.Default.ListTaskInput)),
            ["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject { ["id"] = g.Id,
                ["inputs"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(g.Inputs, PlanningJsonContext.Default.ListTaskInput)) }).ToArray()),
            ["choices"] = JsonSerializer.SerializeToNode(state.Plan.Choices.Where(c => choices.Contains(c.Id)).ToList(), PlanningJsonContext.Default.ListPlanningChoice)
        };
    }

    private static IEnumerable<string> Ports(PlanTask task, PlanningSession state)
    {
        if (task.Kind == "transform") return task.ResultType?.Fields.Select(f => f.Name) ?? [];
        if (task.Kind == "operation") return state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).FirstOrDefault(c => TaskOperations.Describe(c).Id == task.Operation) is { } cap
            ? TaskOperations.Describe(cap).Outputs.Select(o => o.Name) : [];
        if (task.Kind == "value") return task.Outputs.Select(o => o.Name);
        return (task.Body?.Outputs.Select(o => o.Name) ?? []).Concat(task.Branches.SelectMany(b => b.Outputs.Select(o => o.Name))).Distinct(StringComparer.Ordinal);
    }
}
