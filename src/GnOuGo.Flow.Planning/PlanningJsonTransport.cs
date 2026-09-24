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
    internal static JsonObject ContractPrompt(JsonObject schema, bool retainDescriptions = false)
    {
        var result = schema.DeepClone().AsObject();
        void Visit(JsonObject current)
        {
            foreach (var annotation in new[] { "description", "title", "examples", "$comment", "$schema" })
                if (annotation != "description" || !retainDescriptions) current.Remove(annotation);
            foreach (var map in new[] { "properties", "$defs", "definitions", "patternProperties", "dependentSchemas" })
                if (current[map] is JsonObject children) foreach (var child in children.Select(p => p.Value).OfType<JsonObject>()) Visit(child);
            foreach (var key in new[] { "items", "additionalProperties", "contains", "not", "if", "then", "else", "propertyNames", "unevaluatedProperties", "unevaluatedItems" })
                if (current[key] is JsonObject child) Visit(child);
            foreach (var key in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
                if (current[key] is JsonArray children) foreach (var child in children.OfType<JsonObject>()) Visit(child);
        }
        Visit(result);
        // Keep every assertion while factoring repeated schema subtrees into ordinary local references.
        // Existing reference scopes are left intact; literal defaults/consts are never traversed as schemas.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasReferences = false;
        void Count(JsonObject node)
        {
            if (node.ContainsKey("$ref") || node.ContainsKey("$id") || node.ContainsKey("$defs") || node.ContainsKey("definitions")) hasReferences = true;
            var key = Prompt(node); if (key.Length >= 192) counts[key] = counts.GetValueOrDefault(key) + 1;
            foreach (var child in Children(node)) Count(child);
        }
        Count(result);
        if (hasReferences) return result;
        var definitions = new JsonObject(); var names = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject Factor(JsonObject node, bool root = false)
        {
            var key = Prompt(node);
            if (!root && counts.GetValueOrDefault(key) > 1)
            {
                if (!names.TryGetValue(key, out var name))
                {
                    name = "contract_" + names.Count; names.Add(key, name);
                    definitions[name] = Factor(node.DeepClone().AsObject(), root: true);
                }
                return new() { ["$ref"] = "#/$defs/" + name };
            }
            foreach (var child in Children(node).ToArray())
            {
                var replacement = Factor(child);
                if (!ReferenceEquals(child, replacement))
                {
                    if (child.Parent is JsonObject parent) parent[parent.Single(p => ReferenceEquals(p.Value, child)).Key] = replacement;
                    else if (child.Parent is JsonArray array) array[array.IndexOf(child)] = replacement;
                }
            }
            return node;
        }
        result = Factor(result, root: true);
        if (definitions.Count > 0) result["$defs"] = definitions;
        return result;

        static IEnumerable<JsonObject> Children(JsonObject node)
        {
            foreach (var map in new[] { "properties", "patternProperties", "dependentSchemas" })
                if (node[map] is JsonObject children) foreach (var child in children.Select(p => p.Value).OfType<JsonObject>()) yield return child;
            foreach (var key in new[] { "items", "additionalProperties", "contains", "not", "if", "then", "else", "propertyNames", "unevaluatedProperties", "unevaluatedItems" })
                if (node[key] is JsonObject child) yield return child;
            foreach (var key in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
                if (node[key] is JsonArray children) foreach (var child in children.OfType<JsonObject>()) yield return child;
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
