using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningObjectView
{
    // Match only names at the same object path. Arrays are indivisible: their
    // original order, duplicates, item contracts and cardinality travel together.
    internal static JsonObject? SelectFields(JsonObject source, TaskType type, PlanningRequest request)
    {
        var expected = TaskPlanCompiler.TypeSchema(type, request);
        if (type.Kind != "object" || PlanningContractCompatibility.Fits(source, expected)) return null;
        var fields = Fields(type);
        return Contract(source, fields) is { } projected && PlanningContractCompatibility.Fits(projected, expected)
            ? fields : null;

        static JsonObject Fields(TaskType type) => new(type.Fields.Select(field =>
            new KeyValuePair<string, JsonNode?>(field.Name, field.Type.Kind == "object" ? Fields(field.Type) : null)));
    }

    internal static JsonObject? Contract(JsonObject source, JsonObject fields)
    {
        var types = PlanningContractCompatibility.Types(source);
        if (fields.Count == 0 || !types.Contains("object", StringComparer.Ordinal) ||
            types.Any(type => type is not ("object" or "null")) || source["properties"] is not JsonObject properties)
            return null;
        var selected = new JsonObject();
        foreach (var (name, children) in fields)
        {
            if (name is "__proto__" or "constructor" or "prototype" || properties[name] is not JsonObject original)
                return null;
            var field = children is null ? original.DeepClone() : children is JsonObject nested ? Contract(original, nested) : null;
            if (field is null) return null;
            selected.Add(name, field);
        }
        return new()
        {
            ["type"] = source["type"]!.DeepClone(), ["properties"] = selected, ["additionalProperties"] = false,
            ["required"] = new JsonArray((source["required"] as JsonArray ?? []).Where(name =>
                name is not null && fields.ContainsKey(name.ToString())).Select(name => name!.DeepClone()).ToArray())
        };
    }

    internal static bool CopiesPath(JsonObject fields, IEnumerable<string?> path)
    {
        foreach (var segment in path)
        {
            if (segment is null || !fields.TryGetPropertyValue(segment, out var child)) return false;
            if (child is null) return true;
            if (child is not JsonObject nested) return false;
            fields = nested;
        }
        return false;
    }

    // Object.entries visits only present own fields. Forward the original null
    // token and copy entire leaves without reading or serializing their scalars.
    internal static string Mapping(JsonObject fields)
    {
        return Select("source", fields);

        static string Select(string input, JsonObject fields)
        {
            string Key(string name) => JsonValue.Create(name)!.ToJsonString();
            var keep = string.Join("||", fields.Select(field => "entry[0]===" + Key(field.Key)));
            var fieldValue = "entry[1]";
            foreach (var (name, children) in fields)
                if (children is JsonObject nested)
                    fieldValue = "entry[0]===" + Key(name) + "?" + Select("entry[1]", nested) + ":" + fieldValue;
            return "(item=>m.isNull(item)?item:Object.fromEntries(Object.entries(item).filter(entry=>" + keep +
                ").map(entry=>[entry[0]," + fieldValue + "])))(" + input + ")";
        }
    }
}
