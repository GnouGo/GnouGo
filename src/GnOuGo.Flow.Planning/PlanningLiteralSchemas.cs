using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningHoleUnavailableException(string location, string message) : InvalidOperationException(message)
{
    internal string Location { get; } = location;
}

/// <summary>Literal response data obeys the destination schema before typed materialization.</summary>
internal static class PlanningLiteralSchemas
{
    internal static JsonObject Create(JsonObject? expected, string location = "")
    {
        if (expected is null || expected.Count == 0)
            return new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "boolean" }, new JsonObject { ["type"] = "null" }) };
        try { return Strict(expected, expected, 0); }
        catch (PlanningHoleUnavailableException error) when (error.Location.Length == 0 && location.Length > 0)
        { throw new PlanningHoleUnavailableException(location, error.Message); }
    }
    private static JsonObject Strict(JsonObject schema, JsonObject root, int depth)
    {
        if (depth > 24) throw new PlanningHoleUnavailableException("", "The literal contract exceeds the supported schema depth.");
        if (schema["$ref"] is JsonValue reference)
        {
            var path = reference.ToString();
            if (!path.StartsWith("#/", StringComparison.Ordinal) || PlanningFieldPaths.ReadOptional(root, path[1..]) is not JsonObject target)
                throw new PlanningHoleUnavailableException("", "Resolve the literal's declared schema reference before generation.");
            if (schema.Any(p => p.Key is not ("$ref" or "$defs" or "definitions" or "description" or "title" or "default" or "examples")))
                throw new PlanningHoleUnavailableException("", "Resolve the literal reference and its sibling constraints before generation.");
            return Strict(target, root, depth + 1);
        }
        if (schema["oneOf"] is not null)
            throw new PlanningHoleUnavailableException("", "Resolve the exclusive literal contract before generation; overlapping choices cannot be widened.");
        if (schema["anyOf"] is JsonArray alternatives)
        {
            if (schema.Any(p => p.Key is not ("anyOf" or "$defs" or "definitions" or "description" or "title" or "default" or "examples")))
                throw new PlanningHoleUnavailableException("", "Resolve the literal union's shared constraints before generation.");
            return new() { ["anyOf"] = new JsonArray(alternatives.OfType<JsonObject>().Select(s => (JsonNode?)Strict(s, root, depth + 1)).ToArray()) };
        }
        var result = schema.DeepClone().AsObject();
        foreach (var annotation in new[] { "$defs", "definitions", "description", "title", "default", "examples" }) result.Remove(annotation);
        if (schema["items"] is JsonObject item) result["items"] = Strict(item, root, depth + 1);
        if (PlanningContractCompatibility.Types(schema).Contains("object", StringComparer.Ordinal))
        {
            if (schema["additionalProperties"]?.ToString() != "false")
                throw new PlanningHoleUnavailableException("", "An open object literal has no bounded member contract. Establish its required members before generation.");
            var fields = schema["properties"] as JsonObject ?? new();
            var required = (schema["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var optional = fields.Select(p => p.Key).Where(k => !required.Contains(k)).ToArray();
            if (optional.Length > 6) throw new PlanningHoleUnavailableException("", "The literal has too many independent optional members for one bounded strict request. Resolve member presence first.");
            var variants = new JsonArray();
            for (var mask = 0; mask < (1 << optional.Length); mask++)
            {
                var names = required.Concat(optional.Where((_, i) => (mask & (1 << i)) != 0)).ToHashSet(StringComparer.Ordinal);
                var variant = result.DeepClone().AsObject();
                variant["properties"] = new JsonObject(fields.Where(p => names.Contains(p.Key)).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Strict(p.Value!.AsObject(), root, depth + 1))));
                variant["required"] = new JsonArray(names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
                variant["additionalProperties"] = false; variants.Add((JsonNode)variant);
            }
            return variants.Count == 1 ? variants[0]!.DeepClone().AsObject() : new() { ["anyOf"] = variants };
        }
        return result;
    }
}
