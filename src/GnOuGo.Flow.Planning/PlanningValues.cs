using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
internal static class PlanningValues
{
    internal const string Omitted = "omitted";
    internal static string LiteralLocation(PlanningValue value, string root, string pointer)
    {
        foreach (var token in pointer.Split('/').Skip(1))
        {
            var name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.Kind == "object")
            {
                var index = value.Members.FindIndex(m => m.Name == name);
                if (index < 0) break;
                root += "/members/" + index + "/value"; value = value.Members[index].Value;
            }
            else if (value.Kind == "array" && int.TryParse(name, out var index) && index >= 0 && index < value.Items.Count)
            { root += "/items/" + index; value = value.Items[index]; }
            else break;
        }
        return root;
    }
    internal static bool Established(JsonObject schema) => Established(schema, schema, 0);
    private static bool Established(JsonObject schema, JsonObject root, int depth)
    {
        if (PlanningContractShapes.IsOpaque(schema)) return true;
        if (depth > 32) return false;
        if (schema.ContainsKey("const") || schema["enum"] is JsonArray { Count: > 0 }) return true;
        if (schema["$ref"] is JsonValue reference)
        {
            var pointer = reference.ToString();
            return pointer.StartsWith("#/", StringComparison.Ordinal) && PlanningFieldPaths.ReadOptional(root, pointer[1..]) is JsonObject target && Established(target, root, depth + 1);
        }
        if ((schema["anyOf"] ?? schema["oneOf"]) is JsonArray alternatives)
            return alternatives.Count > 0 && alternatives.All(s => s is JsonObject variant && Established(variant, root, depth + 1));
        if (schema["allOf"] is JsonArray parts && parts.OfType<JsonObject>().Any(p => Established(p, root, depth + 1))) return true;
        var types = schema["type"] is JsonArray union ? union.Select(t => t!.ToString()).Where(t => t != "null").ToArray() : [schema["type"]?.ToString() ?? ""];
        if (types.Length != 1) return false;
        return types[0] switch
        {
            "object" => (schema["properties"] as JsonObject ?? new()).All(p => p.Value is JsonObject field && Established(field, root, depth + 1)) &&
                (schema["properties"] is JsonObject { Count: > 0 } || schema["additionalProperties"]?.ToString() == "false" || schema["additionalProperties"] is JsonObject extra && Established(extra, root, depth + 1)),
            "array" => (schema["prefixItems"] is not JsonArray prefix || prefix.All(p => p is JsonObject item && Established(item, root, depth + 1))) &&
                (schema["items"]?.ToString() == "false" || schema["items"] is JsonObject items && Established(items, root, depth + 1)),
            "string" or "number" or "integer" or "boolean" or "null" => true,
            _ => false
        };
    }
}
