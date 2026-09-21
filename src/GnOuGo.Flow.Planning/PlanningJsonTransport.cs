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
    internal static JsonObject BusinessContext(JsonObject plan)
    {
        var result = plan.DeepClone().AsObject();
        void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Visit(item); return; }
            if (node is not JsonObject obj) return;
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                Visit(obj[key]);
                if (obj[key] is null || obj[key] is JsonArray { Count: 0 } || obj[key] is JsonObject { Count: 0 } ||
                    key is "optional" or "nullable" && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag) obj.Remove(key);
            }
        }
        Visit(result); return result;
    }
    internal static JsonObject ContractPrompt(JsonObject schema)
    {
        var result = schema.DeepClone().AsObject();
        void Visit(JsonObject current)
        {
            foreach (var annotation in new[] { "description", "title", "examples", "$comment", "$schema" }) current.Remove(annotation);
            foreach (var map in new[] { "properties", "$defs", "definitions", "patternProperties", "dependentSchemas" })
                if (current[map] is JsonObject children) foreach (var child in children.Select(p => p.Value).OfType<JsonObject>()) Visit(child);
            foreach (var key in new[] { "items", "additionalProperties", "contains", "not", "if", "then", "else", "propertyNames", "unevaluatedProperties", "unevaluatedItems" })
                if (current[key] is JsonObject child) Visit(child);
            foreach (var key in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
                if (current[key] is JsonArray children) foreach (var child in children.OfType<JsonObject>()) Visit(child);
        }
        Visit(result); return result;
    }
    internal static JsonNode ModelGrounded(JsonNode json, JsonNode schema, bool unpack = false)
    {
        var result = json.DeepClone();
        if (schema["$defs"]?["implementation"] is null) return result;
        void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Visit(item); return; }
            if (node is not JsonObject obj) return;
            foreach (var child in obj.Select(p => p.Value).ToArray()) Visit(child);
            if (unpack && obj["implementation"] is JsonObject implementation && obj.ContainsKey("id"))
            {
                obj.Remove("implementation");
                foreach (var field in implementation) obj.Add(field.Key, field.Value?.DeepClone());
            }
            else if (!unpack && obj.ContainsKey("id") && obj.ContainsKey("kind"))
            {
                string[] common = ["id", "semanticAction", "businessOutputs", "purpose", "after", "when"];
                var body = new JsonObject();
                foreach (var key in obj.Select(p => p.Key).Where(k => !common.Contains(k)).ToArray())
                { body[key] = obj[key]?.DeepClone(); obj.Remove(key); }
                obj["implementation"] = body;
            }
        }
        Visit(result); return result;
    }
    internal static JsonObject Grounded(GroundedPlan plan)
    {
        var json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.GroundedPlan)!.AsObject();
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
                "string" or "number" or "integer" or "boolean" or "opaque" => ["type", "nullable", "enum"], _ => null
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
