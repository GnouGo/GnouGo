using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static class PlanningJsonTransport
{
    internal static JsonObject Intent(WorkflowIntentPlan plan)
    {
        var json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.WorkflowIntentPlan)!.AsObject();
        Compact(json); return json;
    }
    internal static void Compact(JsonNode? value)
    {
        if (value is JsonArray array) { foreach (var child in array) Compact(child); return; }
        if (value is not JsonObject obj) return;
        string[]? fields = null;
        if (obj.ContainsKey("kind") && !obj.ContainsKey("id") && !obj.ContainsKey("key"))
            fields = obj["kind"]?.ToString() switch
            {
                "null" or "missing" => ["kind"], "string" => ["kind", "text"], "number" => ["kind", "number"], "boolean" => ["kind", "boolean"],
                "object" => ["kind", "members"], "array" => ["kind", "items"],
                "input" or "result" or "item" or "index" => ["kind", "source", "path"],
                "compute" or "template" => ["kind", "text", "members"], _ => null
            };
        else if (obj["type"] is JsonValue && obj.ContainsKey("nullable") && obj.ContainsKey("fields"))
            fields = obj["type"]?.ToString() switch
            {
                "object" => ["type", "nullable", "fields"], "array" => ["type", "nullable", "items"],
                "string" or "number" or "integer" or "boolean" => ["type", "nullable", "enum"], _ => null
            };
        if (fields is not null)
            foreach (var key in obj.Select(p => p.Key).Where(k => !fields.Contains(k, StringComparer.Ordinal)).ToArray())
                if (obj[key] is null || obj[key] is JsonArray { Count: 0 }) obj.Remove(key);
        foreach (var (_, child) in obj) Compact(child);
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

    internal static void PruneDefinitions(JsonObject schema)
    {
        var definitions = schema["$defs"]!.AsObject(); var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (value is not JsonObject obj) return;
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var name) && name.StartsWith("#/$defs/", StringComparison.Ordinal))
            { var key = name[8..]; if (used.Add(key)) Visit(definitions[key]); }
            foreach (var (key, child) in obj) if (key != "$defs") Visit(child);
        }
        Visit(schema);
        foreach (var key in definitions.Select(p => p.Key).Where(k => !used.Contains(k)).ToArray()) definitions.Remove(key);
    }
}
