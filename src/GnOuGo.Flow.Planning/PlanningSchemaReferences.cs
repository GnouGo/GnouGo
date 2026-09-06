using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Only schema-bearing positions in declared contracts are selectable references.</summary>
internal static class PlanningSchemaReferences
{
    internal static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static JsonArray Index(PlanningPreparation preparation) => new(preparation.Capabilities
        .OrderBy(c => c.Id, StringComparer.Ordinal).SelectMany(c => Entries(c)
        .Where(entry => PlanningContractValidation.ValidateSchema(entry.Schema).Count == 0).Select(entry => (JsonNode)new JsonObject
        {
            ["capabilityId"] = c.Id, ["schemaPointer"] = entry.Path
        })).ToArray());

    internal static JsonObject Resolve(PlanningSchema schema, PlanningPreparation preparation)
    {
        if (schema.Type != "string" || schema.Nullable || schema.Description is not null || schema.Enum.Count != 0 ||
            schema.Items is not null || schema.Properties.Count != 0 || schema.AdditionalProperties is not null)
            throw new InvalidOperationException("A capability schema reference cannot also declare inline constraints. Leave structural fields at their defaults.");
        var capability = preparation.Capabilities.SingleOrDefault(c => c.Id == schema.CapabilityId)
            ?? throw new InvalidOperationException("Unknown schema capability. Select a capabilityId from the supplied schema reference index.");
        var pointer = schema.SchemaPointer ?? "/output";
        ValidatePointer(pointer);
        var found = Entries(capability).FirstOrDefault(entry => entry.Path == pointer);
        if (found.Schema is null)
            throw new InvalidOperationException("The schema reference is unresolved. Select an exact schemaPointer from the supplied index; data paths are not schema paths.");
        var result = (JsonObject)found.Schema.DeepClone();
        // A detached subschema must still resolve its own references. Do not silently
        // drop/rebase constraints or borrow a different capability's definitions.
        var errors = PlanningContractValidation.ValidateSchema(result);
        if (errors.Count != 0)
            throw new InvalidOperationException("The selected schema cannot be preserved independently: " + string.Join("; ", errors));
        return result;
    }

    internal static JsonNode? Read(JsonNode? node, string pointer)
    {
        ValidatePointer(pointer);
        foreach (var part in pointer[1..].Split('/'))
        {
            var segment = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when (segment == "0" || !segment.StartsWith('0')) &&
                    int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
                _ => null
            };
        }
        return node;
    }

    private static void ValidatePointer(string pointer)
    {
        if (!pointer.StartsWith('/')) throw new InvalidOperationException("A schema reference requires an absolute JSON pointer beginning with '/'.");
        for (var i = 0; i < pointer.Length; i++)
            if (pointer[i] == '~' && (++i == pointer.Length || pointer[i] is not ('0' or '1')))
                throw new InvalidOperationException("A JSON pointer escape must be ~0 or ~1.");
    }

    private static IEnumerable<(string Path, JsonObject Schema)> Entries(PlanningCapability capability)
        => Walk(capability.InputSchema, "/input", 0).Concat(Walk(capability.OutputSchema, "/output", 0));

    private static IEnumerable<(string Path, JsonObject Schema)> Walk(JsonObject schema, string path, int depth)
    {
        if (depth > 32) yield break;
        yield return (path, schema);
        foreach (var (keyword, child) in schema.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var childPath = path + "/" + Escape(keyword);
            if (keyword is "properties" or "$defs" or "definitions" or "patternProperties" or "dependentSchemas" && child is JsonObject map)
            {
                foreach (var property in map.OrderBy(p => p.Key, StringComparer.Ordinal))
                    if (property.Value is JsonObject target)
                        foreach (var entry in Walk(target, childPath + "/" + Escape(property.Key), depth + 1)) yield return entry;
            }
            else if (keyword is "anyOf" or "oneOf" or "allOf" or "prefixItems" && child is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonObject target)
                        foreach (var entry in Walk(target, childPath + "/" + i, depth + 1)) yield return entry;
            }
            else if (keyword is "items" or "additionalProperties" or "contains" or "not" or "if" or "then" or "else" or "propertyNames" or "unevaluatedProperties" && child is JsonObject target)
                foreach (var entry in Walk(target, childPath, depth + 1)) yield return entry;
        }
    }
}
