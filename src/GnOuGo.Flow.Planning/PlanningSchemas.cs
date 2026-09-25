using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The model sees business tasks and ports, never executor graph plumbing.</summary>
internal static class PlanningSchemas
{
    internal static JsonObject Proposal(PlanningSession state)
    {
        var pages = state.Discovery.Sources.SelectMany(source =>
            (state.Discovery.Pages.Any(p => p.SourceId == source.Id && p.Cursor is null) ? Enumerable.Empty<string?>() : [null])
            .Concat(state.Discovery.Pages.Where(p => p.SourceId == source.Id).Select(p => p.NextCursor).OfType<string>())
            .Where(cursor => !state.Discovery.Pages.Any(p => p.SourceId == source.Id && p.Cursor == cursor))
            .Select(cursor => (Source: source.Id, Cursor: cursor))).Distinct().ToArray();
        var cursors = pages.Select(p => p.Cursor).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var root = Object(("requirements", Ref("requirements")),
            ("sourceId", pages.Length == 0 ? Type("null") : Nullable(Enum(pages.Select(p => p.Source).Distinct(StringComparer.Ordinal).ToArray()))),
            ("cursor", cursors.Length == 0 ? Type("null") : Nullable(Enum(cursors))),
            ("plan", pages.Length == 0 ? Ref("plan") : Nullable(Ref("plan"))), ("explanation", String()));
        root["$defs"] = new JsonObject
        {
            ["strings"] = Array(String()),
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("output")))),
                Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input", "choice", "present")), ("source", String())),
                Object(("kind", Enum("output")), ("source", String()), ("port", Nullable(String()))),
                Object(("kind", Enum("item", "index"))),
                Object(("kind", Enum("predicate")), ("predicate", Enum("not", "and", "or", "equal", "not_equal", "less", "less_equal", "greater", "greater_equal")), ("items", Array(Ref("value"))))) },
            ["literal"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Object(("name", String()), ("value", Ref("literal")))))),
                Object(("kind", Enum("array")), ("items", Array(Ref("literal"))))) },
            ["businessType"] = Object(("kind", Enum("string", "number", "integer", "boolean", "array", "object", "any")),
                ("nullable", Type("boolean")), ("items", Nullable(Ref("businessType"))), ("fields", Array(Ref("field")))),
            ["field"] = Object(("name", String()), ("type", Ref("businessType")), ("required", Type("boolean")), ("default", Nullable(Ref("literal")))),
            ["input"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("name", String()), ("type", Ref("businessType")), ("required", new() { ["type"] = "boolean", ["enum"] = new JsonArray(true) }), ("default", Nullable(Ref("literal")))),
                Object(("name", String()), ("type", Ref("businessType")), ("required", new() { ["type"] = "boolean", ["enum"] = new JsonArray(false) }), ("default", Ref("literal")))) },
            ["output"] = Object(("name", String()), ("value", Ref("value"))),
            ["requirements"] = Object(("summary", String()), ("outcomes", NonEmptyArray(Object(("id", String()), ("description", String()))))),
            ["plan"] = Object(("inputs", Array(Ref("input"))), ("root", Ref("scope")), ("groups", Array(Ref("group"))), ("choices", Array(Ref("choice")))),
            ["scope"] = Object(("tasks", Array(Ref("task"))), ("outputs", Array(Ref("output"))), ("always", Array(Ref("task")))),
            ["group"] = Object(("id", String()), ("inputs", Array(Ref("input"))), ("body", Ref("scope"))),
            ["choice"] = Object(("id", String()), ("question", String()), ("type", Ref("businessType")),
                ("alternatives", Array(Object(("id", String()), ("description", String()), ("value", Ref("value"))))),
                ("recommended", String()), ("selected", Type("null"))),
            ["task"] = Tasks()
        };
        return root;
    }
    private static JsonObject Tasks()
    {
        JsonObject Task(string kind, params (string Name, JsonObject Schema)[] fields) => Object(new (string Name, JsonObject Schema)[]
        { ("id", String()), ("kind", Enum(kind)), ("objective", String()), ("dependsOn", Ref("strings")) }.Concat(fields).ToArray());
        return new() { ["anyOf"] = new JsonArray(
            Task("operation", ("operation", String()), ("inputs", Array(Ref("output")))),
            Task("value", ("outputs", Array(Ref("output")))),
            Task("sequence", ("body", Ref("scope"))),
            Task("conditional", ("condition", Ref("value")), ("body", Ref("scope")), ("otherwise", Ref("scope"))),
            Task("parallel", ("branches", Array(Ref("scope"))), ("maxConcurrency", Type("integer"))),
            Task("foreach", ("items", Ref("value")), ("body", Ref("scope")), ("parallel", Type("boolean")), ("maxItems", Type("integer")), ("maxConcurrency", Type("integer"))),
            Task("call", ("group", String()), ("inputs", Array(Ref("output"))))) };
    }
    internal static JsonObject String() => Type("string");
    internal static JsonObject Type(string type) => new() { ["type"] = type };
    internal static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    internal static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    internal static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    internal static JsonObject Array(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    internal static JsonObject NonEmptyArray(JsonObject item) { var array = Array(item); array["minItems"] = 1; return array; }
    internal static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false
    };
}
