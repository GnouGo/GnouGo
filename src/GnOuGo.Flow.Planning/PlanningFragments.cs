using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Elaborates executable fields while keeping accepted topology outside model control.</summary>
public static class PlanningFragments
{
    private static readonly string[] Fields = ["input", "outputSchema", "structuredOutput", "expr", "output", "itemVar", "indexVar", "retry", "onError"];

    public static JsonObject Schema(PlanningWorkflow workflow, PlanningPreparation preparation)
    {
        var graph = PlanningSchemas.Graph(preparation, fragment: true);
        var definitions = graph["$defs"]!.DeepClone().AsObject();
        var nodeProperties = definitions["node"]!["properties"]!.AsObject();
        var variants = new JsonArray();
        foreach (var group in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).GroupBy(n => n.Type, StringComparer.Ordinal))
        {
            var properties = new JsonObject { ["key"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(group.Select(n => (JsonNode?)JsonValue.Create(n.Key)).ToArray()) } };
            foreach (var field in Fields) properties[field] = nodeProperties[field]!.DeepClone();
            // These executable fields are fixed by the accepted native step kind.
            // Do not ask the model to invent unsupported post-processing or annotations.
            if (group.Key is not ("mcp.call" or "llm.call")) properties["structuredOutput"] = new JsonObject { ["type"] = "null" };
            if (group.Key != "set") properties["outputSchema"] = new JsonObject { ["type"] = "null" };
            if (group.Key != "switch") properties["expr"] = new JsonObject { ["type"] = "null" };
            if (group.Key is not ("loop.sequential" or "loop.parallel"))
                foreach (var field in new[] { "itemVar", "indexVar" }) properties[field] = new JsonObject { ["type"] = "null" };
            properties["caseConditions"] = Array(Object(new()
            {
                ["index"] = new JsonObject { ["type"] = "integer" },
                ["when"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/$defs/value" }, new JsonObject { ["type"] = "null" }) }
            }));
            if (group.Key != "switch") properties["caseConditions"]!["maxItems"] = 0;
            variants.Add((JsonNode)Object(properties));
        }
        var nodes = variants.Count == 0 ? Array(new JsonObject { ["type"] = "string" }) : Array(new JsonObject { ["anyOf"] = variants });
        if (variants.Count == 0) nodes["maxItems"] = 0;
        var root = Object(new()
        {
            ["functions"] = graph["properties"]!["functions"]!.DeepClone(),
            ["inputs"] = graph["properties"]!["inputs"]!.DeepClone(),
            ["outputs"] = graph["properties"]!["outputs"]!.DeepClone(),
            ["nodes"] = nodes
        });
        root["$defs"] = definitions;
        return root;
    }

    public static JsonObject Values(PlanningWorkflow workflow)
    {
        var full = PlanningModelValues.Workflow(workflow);
        var nodes = new JsonArray();
        foreach (var node in Nodes(full))
        {
            var value = new JsonObject { ["key"] = node["key"]!.DeepClone() };
            foreach (var field in Fields) value[field] = node[field]?.DeepClone();
            value["caseConditions"] = new JsonArray(node["cases"]!.AsArray().Select((c, i) => (JsonNode)new JsonObject { ["index"] = i, ["when"] = c!["when"]?.DeepClone() }).ToArray());
            nodes.Add((JsonNode)value);
        }
        return new() { ["functions"] = full["functions"]?.DeepClone(), ["inputs"] = full["inputs"]!.DeepClone(), ["outputs"] = full["outputs"]!.DeepClone(), ["nodes"] = nodes };
    }

    public static PlanningWorkflow Elaborate(PlanningWorkflow workflow, JsonObject response, PlanningPreparation preparation)
    {
        var errors = PlanningContractValidation.ValidateInstance(response, Schema(workflow, preparation));
        if (errors.Count != 0) throw new InvalidOperationException("Invalid executable fields: " + string.Join("; ", errors));
        var result = JsonSerializer.SerializeToNode(workflow, PlanningJsonContext.Default.PlanningWorkflow)!.AsObject();
        var nodes = Nodes(result).ToDictionary(n => n["key"]!.GetValue<string>(), StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in response["nodes"]!.AsArray().OfType<JsonObject>())
        {
            var key = value["key"]!.GetValue<string>();
            if (!completed.Add(key) || !nodes.TryGetValue(key, out var target)) throw new InvalidOperationException("Executable fields require each accepted node exactly once.");
            foreach (var field in Fields) target[field] = value[field]?.DeepClone();
            var cases = target["cases"]!.AsArray();
            var assigned = new HashSet<int>();
            foreach (var condition in value["caseConditions"]!.AsArray().OfType<JsonObject>())
            {
                var index = condition["index"]!.GetValue<int>();
                if (index < 0 || index >= cases.Count || !assigned.Add(index)) throw new InvalidOperationException("Unknown or duplicate decision condition.");
                cases[index]!["when"] = condition["when"]?.DeepClone();
            }
            if (assigned.Count != cases.Count) throw new InvalidOperationException("Declare a condition entry for every accepted decision case; value-match cases use null.");
        }
        if (completed.Count != nodes.Count) throw new InvalidOperationException("Executable fields omitted accepted nodes.");
        foreach (var field in new[] { "functions", "inputs", "outputs" }) result[field] = response[field]?.DeepClone();
        return JsonSerializer.Deserialize(result, PlanningJsonContext.Default.PlanningWorkflow)!;
    }

    private static IEnumerable<JsonObject> Nodes(JsonObject workflow)
    {
        return Walk(workflow["steps"]!.AsArray().Concat(workflow["finally"]!.AsArray()));
        static IEnumerable<JsonObject> Walk(IEnumerable<JsonNode?> items)
        {
            foreach (var node in items.OfType<JsonObject>())
            {
                yield return node;
                foreach (var child in Walk(node["steps"]!.AsArray().Concat(node["default"]!.AsArray()).Concat(node["cases"]!.AsArray().Concat(node["branches"]!.AsArray()).OfType<JsonObject>().SelectMany(b => b["steps"]!.AsArray())))) yield return child;
            }
        }
    }
    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject Object(JsonObject properties) => new()
    {
        ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false
    };
}
