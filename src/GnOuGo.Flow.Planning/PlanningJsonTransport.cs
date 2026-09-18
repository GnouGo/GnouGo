using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static class PlanningJsonTransport
{
    // The persistence DTO includes every union field. Model context uses the same
    // compact discriminated shape as model responses, without changing the DTO.
    internal static JsonObject Intent(WorkflowIntentPlan plan)
    {
        var json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.WorkflowIntentPlan)!.AsObject();
        Compact(json);
        return json;
    }
    internal static void Compact(JsonNode? value)
    {
        if (value is JsonArray array) { foreach (var child in array) Compact(child); return; }
        if (value is not JsonObject obj) return;
        string[]? fields = null;
        if (obj.ContainsKey("kind") && !obj.ContainsKey("key"))
        {
            fields = obj["kind"]?.ToString() switch
            {
                "null" or "hole" or "omitted" => ["kind"],
                "string" or "expression" => ["kind", "text"],
                "number" => ["kind", "number"], "boolean" => ["kind", "boolean"],
                "object" => ["kind", "members"], "array" => ["kind", "items"],
                "input" or "loop_item" or "loop_index" or "loop_previous" or "artifact_collection" => ["kind", "source", "path"],
                "output" => ["kind", "source", "resultChannel", "path"],
                "workflow" => ["kind", "source"], "template" or "compute" => ["kind", "text", "members"],
                _ => null
            };
            if (obj["kind"]?.ToString() == "output" && obj["resultChannel"] is null) obj["resultChannel"] = "default";
        }
        else if (obj.ContainsKey("type") && obj.ContainsKey("nullable"))
            fields = obj["capabilityId"] is not null ? ["capabilityId", "schemaPointer"] : obj["type"]?.ToString() switch
            {
                "hole" => ["type"],
                "string" or "number" or "integer" or "boolean" => ["type", "nullable", "description", "enum"],
                "array" => ["type", "nullable", "description", "items"],
                "object" => ["type", "nullable", "description", "properties", "additionalProperties"],
                _ => null
            };
        if (fields is not null)
            foreach (var key in obj.Select(p => p.Key).Where(k => !fields.Contains(k, StringComparer.Ordinal)).ToArray())
            {
                // Retain non-default invalid fields as repair evidence, including
                // inline constraints illegally combined with a catalog reference.
                if (obj[key] is null || obj[key] is JsonArray { Count: 0 } ||
                    key == "type" && obj["capabilityId"] is not null && obj[key]?.ToString() == "string" ||
                    key == "nullable" && obj[key] is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && !flag)
                    obj.Remove(key);
            }
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
