using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Planning.Examples;
public static class IntentResponse
{
    public static JsonNode Response(WorkflowIntentPlan plan, JsonObject schema)
    {
        var json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.WorkflowIntentPlan)!;
        return Format(json, schema)!;
        JsonNode? Format(JsonNode? value, JsonObject shape)
        {
            if (shape["$ref"] is { } reference) return Format(value, schema["$defs"]![reference.ToString().Split('/').Last()]!.AsObject());
            if (shape["anyOf"] is JsonArray variants)
            {
                var choices = variants.OfType<JsonObject>().ToArray();
                JsonObject selected;
                if (value is null) selected = choices.First(c => c["type"]?.ToString() == "null");
                else if (value is JsonObject obj && obj["kind"] is { } kind && choices.Any(c => c["properties"]?["kind"] is not null))
                    selected = choices.First(c => (c["properties"]?["kind"]?["enum"] as JsonArray ?? []).Any(v => v?.ToString() == kind.ToString()));
                else if (value is JsonObject schemaValue && choices.Any(c => c["properties"]?["capabilityId"] is not null))
                    selected = choices.First(c => schemaValue["capabilityId"] is not null ? c["properties"]?["capabilityId"] is not null : c["properties"]?["type"] is not null);
                else selected = choices.First(c => c["type"]?.ToString() != "null");
                return Format(value, selected);
            }
            if (shape["properties"] is JsonObject fields)
                return new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Format(value?[p.Key], p.Value!.AsObject()))));
            if (shape["items"] is JsonObject item) return new JsonArray((value as JsonArray ?? []).Select(v => Format(v, item)).ToArray());
            if (value is null && shape["enum"] is JsonArray possible && possible.Any(v => v?.ToString() == "default")) return JsonValue.Create("default");
            return value?.DeepClone();
        }
    }
}
