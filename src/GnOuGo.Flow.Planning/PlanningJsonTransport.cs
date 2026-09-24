using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static class PlanningJsonTransport
{
    // Prompt JSON is model input, never HTML. Literal Unicode avoids expanding business text into escape sequences.
    internal static string Prompt(JsonNode value) => value.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    internal static JsonArray Diagnostics(IEnumerable<PlanningDiagnostic> diagnostics) => new(diagnostics
        .GroupBy(d => (d.Code, d.Message, d.Required, d.ValidationStage, d.Rule,
            Prerequisite: d.Prerequisite is null ? null : JsonSerializer.Serialize(d.Prerequisite, PlanningJsonContext.Default.PlanningPrerequisiteContext))).Select(group =>
        {
            var item = new JsonObject { ["code"] = group.Key.Code, ["message"] = group.Key.Message,
                ["locations"] = new JsonArray(group.Select(d => d.Location).Distinct(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["required"] = group.Key.Required };
            if (group.Key.ValidationStage is not null) item["validationStage"] = group.Key.ValidationStage;
            if (group.Key.Rule is not null) item["rule"] = group.Key.Rule;
            if (group.Key.Prerequisite is not null) item["prerequisite"] = JsonNode.Parse(group.Key.Prerequisite);
            return (JsonNode)item;
        }).ToArray());
    internal static JsonObject? GraphContext(PlanningGraph? graph)
    {
        if (graph is null) return null;
        var json = JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph)!.AsObject();
        Visit(json);
        return json;

        static void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Visit(item); return; }
            if (node is not JsonObject obj) return;
            var schemaReference = obj["capabilityId"] is not null && obj["schemaPointer"] is not null;
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                Visit(obj[key]);
                if (schemaReference && key is not ("capabilityId" or "schemaPointer") ||
                    obj[key] is null || obj[key] is JsonArray { Count: 0 } || obj[key] is JsonObject { Count: 0 }) obj.Remove(key);
            }
        }
    }

    internal static PlanningValue Literal(JsonNode? json) => json switch
    {
        null => new(),
        JsonObject obj => new() { Kind = "object", Members = obj.Select(p => new PlanningMember(p.Key, Literal(p.Value))).ToList() },
        JsonArray array => new() { Kind = "array", Items = array.Select(Literal).ToList() },
        JsonValue value when value.TryGetValue<string>(out var text) => new() { Kind = "string", Text = text },
        JsonValue value when value.TryGetValue<bool>(out var boolean) => new() { Kind = "boolean", Boolean = boolean },
        JsonValue value => new() { Kind = "number", Number = decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) },
        _ => throw new InvalidOperationException("Unsupported literal.")
    };

    public static int EstimateInputTokens(string prompt, JsonObject schema) => checked((Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schema.ToJsonString()) + 2) / 3 + 256);
}
