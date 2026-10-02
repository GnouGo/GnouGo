using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningExportRepair
{
    internal sealed record Export(TaskPlanSymbols.Scope Scope, string Name, TaskValue? Value, TaskValue Producer, TaskPlanSymbols.Scope ProducerScope);
    internal Dictionary<string, TaskValue> Consumers { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Export> Additions { get; } = new(StringComparer.Ordinal);

    internal PlanningExportRepair(TaskPlan plan, IEnumerable<string> paths)
    {
        var symbols = new TaskPlanSymbols(plan);
        foreach (var path in paths.Order(StringComparer.Ordinal))
            if (symbols.Values.TryGetValue(path, out var site))
            {
                var rewritten = Rewrite(site.Value, site.Scope);
                if (!Same(rewritten, site.Value)) Consumers[path] = rewritten;
            }

        TaskValue Rewrite(TaskValue value, TaskPlanSymbols.Scope scope)
        {
            var copy = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
            if (value.Kind == "output" && value.Source is { } id && symbols.ExportRoute(scope, id) is { Count: > 0 } route)
            {
                var current = copy;
                foreach (var boundary in route)
                {
                    var existing = boundary.Source.Outputs.Where(o => Same(o.Value, current)).OrderBy(o => o.Name, StringComparer.Ordinal).FirstOrDefault();
                    var name = existing?.Name ?? Additions.Values.FirstOrDefault(e => e.Scope == boundary && e.Value is not null && Same(e.Value, current))?.Name;
                    if (name is null)
                    {
                        var basis = current.Port ?? current.Source + "Result";
                        name = basis;
                        for (var suffix = 2; boundary.Source.Outputs.Any(o => o.Name == name) || Additions.ContainsKey(boundary.Path + "/outputs/" + name); suffix++)
                            name = basis + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Additions.Add(boundary.Path + "/outputs/" + name, new(boundary, name, current, value, route[0]));
                    }
                    if (boundary.Owner?.Kind == "conditional")
                        foreach (var other in symbols.Scopes.Where(s => s.Owner == boundary.Owner && s != boundary && s.Source.Outputs.All(o => o.Name != name)))
                            Additions.TryAdd(other.Path + "/outputs/" + name, new(other, name, null, value, route[0]));
                    current = new() { Kind = "output", Source = boundary.Owner!.Id, Port = name };
                }
                return current;
            }
            copy.Items = value.Items.Select(v => Rewrite(v, scope)).ToList();
            copy.Members = value.Members.Select(m => new TaskOutput(m.Name, Rewrite(m.Value, scope))).ToList();
            return copy;
        }
    }

    internal static bool Same(TaskValue left, TaskValue right) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(left, PlanningJsonContext.Default.TaskValue), JsonSerializer.SerializeToNode(right, PlanningJsonContext.Default.TaskValue));

    internal static JsonObject Exact(TaskValue value, JsonObject? definitions = null)
    {
        if (definitions is null || value.Items.Count + value.Members.Count == 0)
            return Shape(PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue))!);
        // Factor composite values through a bounded vocabulary of only this repaired
        // value's leaves. Recursive wire factoring avoids provider nesting limits;
        // atomic revision validation still preserves exact member identity/order.
        var name = "repairValue" + definitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        definitions[name] = new JsonObject { ["anyOf"] = new JsonArray(TaskPlanCompiler.Values(value)
            .Select(v => Shape(PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(v, PlanningJsonContext.Default.TaskValue))!, name))
            .DistinctBy(s => s.ToJsonString()).Select(s => (JsonNode)s).ToArray()) };
        return PlanningSchemas.Ref(name);
    }

    private static JsonObject Shape(JsonNode value, string? recursive = null, bool nested = false) => value switch
    {
        JsonObject obj when nested && recursive is not null && obj.ContainsKey("kind") => PlanningSchemas.Ref(recursive),
        JsonObject obj => PlanningSchemas.Object(obj.Select(p => (p.Key, p.Value is null ? PlanningSchemas.Type("null") : Shape(p.Value, recursive, true))).ToArray()),
        JsonArray array => array.Count == 0 ? PlanningSchemas.Array(PlanningSchemas.Ref("value"), 0, 0) :
            PlanningSchemas.Array(new JsonObject { ["anyOf"] = new JsonArray(array.Select(v => (JsonNode)Shape(v!, recursive, true)).ToArray()) }, array.Count, array.Count),
        _ => new() { ["type"] = value.GetValueKind() switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", _ => "boolean" }, ["enum"] = new JsonArray(value.DeepClone()) }
    };
}
