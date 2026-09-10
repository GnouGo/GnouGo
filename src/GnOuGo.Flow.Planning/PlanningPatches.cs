using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Atomic, field-scoped patches; model responses cannot replace a workflow graph.</summary>
public static class PlanningPatches
{
    private static readonly string[] NodeFields = ["input", "outputSchema", "structuredOutput", "expr", "if", "onError"];

    public static JsonObject Schema(PlanningPreparation preparation)
    {
        var definitions = PlanningSchemas.WholeWorkflow(preparation)["$defs"]!.DeepClone().AsObject();
        JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
        JsonObject Nullable(JsonObject type) => new() { ["anyOf"] = new JsonArray(type, new JsonObject { ["type"] = "null" }) };
        JsonObject Variant(string field, JsonObject value, bool workflow = false, bool root = false)
        {
            var properties = new JsonObject
            {
                ["workflow"] = new JsonObject { ["type"] = root ? "null" : "string" },
                ["node"] = new JsonObject { ["type"] = workflow ? "null" : "string" },
                ["field"] = field switch
                {
                    "cases/when" => new JsonObject { ["type"] = "string", ["pattern"] = "^cases/(0|[1-9][0-9]*)/when$" },
                    "outputs/value" => new JsonObject { ["type"] = "string", ["pattern"] = "^outputs/(0|[1-9][0-9]*)/value$" },
                    _ => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(field) }
                },
                ["value"] = value
            };
            return new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("workflow", "node", "field", "value"), ["additionalProperties"] = false };
        }
        var structured = definitions["node"]!["properties"]!["structuredOutput"]!.DeepClone().AsObject();
        var variants = new JsonArray(
            Variant("input", Ref("value")), Variant("outputSchema", Nullable(Ref("schema"))), Variant("structuredOutput", structured),
            Variant("cases/when", Nullable(Ref("value"))), Variant("expr", Nullable(Ref("value"))), Variant("if", Nullable(Ref("value"))),
            Variant("onError", new() { ["type"] = "array", ["items"] = Ref("errorCase") }),
            Variant("functions", Nullable(new() { ["type"] = "string" }), true),
            Variant("functions", Nullable(new() { ["type"] = "string" }), true, true),
            Variant("outputs/value", Ref("value"), true),
            Variant("inputs", new() { ["type"] = "array", ["items"] = Ref("port") }, true),
            Variant("outputs", new() { ["type"] = "array", ["items"] = Ref("output") }, true));
        return new() { ["type"] = "object", ["properties"] = new JsonObject { ["patches"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = variants } } }, ["required"] = new JsonArray("patches"), ["additionalProperties"] = false, ["$defs"] = definitions };
    }

    public static PlanningGraph Apply(PlanningGraph original, JsonObject response, IReadOnlySet<string> allowed, PlanningPreparation preparation)
    {
        var errors = PlanningContractValidation.ValidateInstance(response, ScopedSchema(preparation, allowed));
        if (errors.Count != 0) throw new InvalidOperationException("Invalid patch response: " + string.Join("; ", errors));
        var graph = JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.PlanningGraph)!.AsObject();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in response["patches"]!.AsArray().OfType<JsonObject>())
        {
            var workflowKey = patch["workflow"]?.GetValue<string>(); var nodeKey = patch["node"]?.GetValue<string>(); var field = patch["field"]!.GetValue<string>();
            var coordinate = Coordinate(workflowKey, nodeKey, field);
            if (!changed.Add(coordinate)) throw new InvalidOperationException("A patch field was submitted more than once.");
            var workflow = workflowKey is null ? graph : graph["workflows"]!.AsArray().OfType<JsonObject>().SingleOrDefault(w => w["key"]!.GetValue<string>() == workflowKey) ?? throw new InvalidOperationException("Unknown patch workflow.");
            JsonObject target = workflow;
            if (nodeKey is not null)
                target = Nodes(workflow["steps"]!.AsArray().Concat(workflow["finally"]!.AsArray())).SingleOrDefault(n => n["key"]!.GetValue<string>() == nodeKey) ?? throw new InvalidOperationException("Unknown patch node.");
            if (!allowed.Contains(coordinate)) throw new InvalidOperationException("The patch targets a field outside its diagnosed scope: " + coordinate);
            if (field.StartsWith("cases/", StringComparison.Ordinal) || field.StartsWith("outputs/", StringComparison.Ordinal))
            {
                var parts = field.Split('/');
                if (!int.TryParse(parts[1], out var index) || target[parts[0]] is not JsonArray items || index < 0 || index >= items.Count)
                    throw new InvalidOperationException("Unknown indexed patch field.");
                items[index]![parts[2]] = patch["value"]?.DeepClone();
            }
            else target[field] = patch["value"]?.DeepClone();
        }
        if (changed.Count == 0) throw new InvalidOperationException("An empty repair cannot resolve diagnostics.");
        return JsonSerializer.Deserialize(graph, PlanningJsonContext.Default.PlanningGraph)!;
    }

    public static HashSet<string> Scope(PlanningGraph graph, IReadOnlyList<PlanningDiagnostic> diagnostics)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(d => d.Required))
        {
            if (diagnostic.Location == "/functions" || diagnostic.Location.StartsWith("/functions/", StringComparison.Ordinal)) allowed.Add(Coordinate(null, null, "functions"));
            for (var wi = 0; wi < graph.Workflows.Count; wi++)
            {
                var workflow = graph.Workflows[wi]; var root = "/workflows/" + wi;
                foreach (var field in new[] { "inputs", "outputs", "functions" })
                    if (diagnostic.Location == root + "/" + field || diagnostic.Location.StartsWith(root + "/" + field + "/", StringComparison.Ordinal))
                    {
                        var suffix = diagnostic.Location[(root + "/" + field).Length..].TrimStart('/').Split('/');
                        if (field != "functions" && suffix[0].Length > 0 &&
                            (!int.TryParse(suffix[0], out var index) || index < 0 || index >= (field == "outputs" ? workflow.Outputs.Count : workflow.Inputs.Count) ||
                                suffix[0] != index.ToString(System.Globalization.CultureInfo.InvariantCulture))) continue;
                        allowed.Add(Coordinate(workflow.Key, null, field == "outputs" && suffix.Length > 1 && suffix[1] == "value" ? "outputs/" + suffix[0] + "/value" : field));
                    }
                foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
                {
                    foreach (var field in NodeFields)
                        if (diagnostic.Location == path + "/" + field || diagnostic.Location.StartsWith(path + "/" + field + "/", StringComparison.Ordinal)) allowed.Add(Coordinate(workflow.Key, node.Key, field));
                    for (var ci = 0; ci < node.Cases.Count; ci++)
                        if (diagnostic.Location == path + "/cases/" + ci + "/when" || diagnostic.Location.StartsWith(path + "/cases/" + ci + "/when/", StringComparison.Ordinal)) allowed.Add(Coordinate(workflow.Key, node.Key, "cases/" + ci + "/when"));
                }
            }
        }
        return allowed;
    }

    public static string Coordinate(string? workflow, string? node, string field)
        => JsonSerializer.Serialize(new[] { workflow, node, field }, PlanningJsonContext.Default.StringArray);

    internal static JsonObject ScopedSchema(PlanningPreparation preparation, IReadOnlySet<string> allowed)
    {
        if (allowed.Count == 0) throw new InvalidOperationException("A repair requires diagnosed typed fields.");
        var schema = Schema(preparation);
        var variants = schema["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray();
        var scoped = new JsonArray();
        foreach (var coordinate in allowed.Order(StringComparer.Ordinal))
        {
            var parts = JsonSerializer.Deserialize(coordinate, PlanningJsonContext.Default.StringArray)!;
            var variant = variants.OfType<JsonObject>().Single(v =>
            {
                var properties = v["properties"]!;
                var field = properties["field"]!;
                return properties["workflow"]!["type"]!.ToString() == (parts[0] is null ? "null" : "string") &&
                    properties["node"]!["type"]!.ToString() == (parts[1] is null ? "null" : "string") &&
                    (field["enum"] is JsonArray values ? values.Any(value => value?.ToString() == parts[2]) :
                        Regex.IsMatch(parts[2], field["pattern"]!.GetValue<string>(), RegexOptions.CultureInvariant));
            }).DeepClone().AsObject();
            foreach (var (name, index) in new[] { ("workflow", 0), ("node", 1), ("field", 2) })
                variant["properties"]![name] = parts[index] is null ? new JsonObject { ["type"] = "null" } :
                    new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(parts[index]) };
            scoped.Add((JsonNode)variant);
        }
        schema["properties"]!["patches"]!["items"]!["anyOf"] = scoped;
        schema["properties"]!["patches"]!["minItems"] = 1;
        schema["properties"]!["patches"]!["maxItems"] = allowed.Count;
        // Retain only definitions reachable from the exact editable value contracts.
        var definitions = schema["$defs"]!.AsObject(); var needed = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference && reference.GetValue<string>().StartsWith("#/$defs/", StringComparison.Ordinal))
                {
                    var key = reference.GetValue<string>()["#/$defs/".Length..];
                    if (needed.Add(key)) Visit(definitions[key]);
                }
                foreach (var property in obj.Where(p => p.Key != "$defs")) Visit(property.Value);
            }
            else if (value is JsonArray array) foreach (var item in array) Visit(item);
        }
        Visit(schema);
        foreach (var key in definitions.Select(p => p.Key).Where(k => !needed.Contains(k)).ToArray()) definitions.Remove(key);
        return schema;
    }

    private static IEnumerable<JsonObject> Nodes(IEnumerable<JsonNode?> nodes)
    {
        foreach (var node in nodes.OfType<JsonObject>())
        {
            yield return node;
            foreach (var child in Nodes(node["steps"]!.AsArray().Concat(node["default"]!.AsArray()).Concat(node["cases"]!.AsArray().Concat(node["branches"]!.AsArray()).OfType<JsonObject>().SelectMany(b => b["steps"]!.AsArray())))) yield return child;
        }
    }
}
