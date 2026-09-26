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
        var root = Object(
            ("discoveryRequests", pages.Length == 0 ? Type("null") : Nullable(Array(new JsonObject { ["anyOf"] = new JsonArray(pages.Select(p => (JsonNode?)Object(
                ("sourceId", Enum(p.Source)), ("cursor", p.Cursor is null ? Type("null") : Enum(p.Cursor)))).ToArray()) }, 1, 4))),
            ("plan", pages.Length == 0 ? Ref("plan") : Nullable(Ref("plan"))));
        if (state.Requirements is null)
        {
            root["properties"]!["requirements"] = Ref("requirements");
            root["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("requirements"));
        }
        root["$defs"] = new JsonObject
        {
            ["identities"] = Array(Identity()),
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("output")))),
                Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input")), ("source", String())),
                Object(("kind", Enum("choice", "present")), ("source", Identity())),
                Object(("kind", Enum("output")), ("source", Identity()), ("port", Nullable(String()))),
                Object(("kind", Enum("item", "index"))),
                Object(("kind", Enum("predicate")), ("predicate", Enum("not")), ("items", Array(Ref("value"), 1, 1))),
                Object(("kind", Enum("predicate")), ("predicate", Enum("and", "or", "equal", "not_equal", "less", "less_equal", "greater", "greater_equal")), ("items", Array(Ref("value"), 2, 2)))) },
            ["literal"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Object(("name", String()), ("value", Ref("literal")))))),
                Object(("kind", Enum("array")), ("items", Array(Ref("literal"))))) },
            ["businessType"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("array")), ("nullable", Type("boolean")), ("items", Ref("businessType"))),
                Object(("kind", Enum("object")), ("nullable", Type("boolean")), ("fields", Array(Ref("field")))),
                Object(("kind", Enum("string", "number", "integer", "boolean", "any")), ("nullable", Type("boolean")))) },
            ["field"] = Object(("name", String()), ("type", Ref("businessType")), ("required", Type("boolean")), ("default", Nullable(Ref("literal")))),
            ["resultType"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("array")), ("nullable", Type("boolean")), ("items", Ref("resultType"))),
                Object(("kind", Enum("object")), ("nullable", Type("boolean")), ("fields", Array(Ref("resultField")))),
                Object(("kind", Enum("string", "number", "integer", "boolean")), ("nullable", Type("boolean")))) },
            ["resultField"] = Object(("name", Nonblank()), ("type", Ref("resultType"))),
            ["input"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("name", String()), ("type", Ref("businessType")), ("required", new() { ["type"] = "boolean", ["enum"] = new JsonArray(true) }), ("default", Nullable(Ref("literal")))),
                Object(("name", String()), ("type", Ref("businessType")), ("required", new() { ["type"] = "boolean", ["enum"] = new JsonArray(false) }), ("default", Ref("literal")))) },
            ["output"] = Object(("name", String()), ("value", Ref("value"))),
            ["requirements"] = Object(("summary", String()), ("outcomes", NonEmptyArray(Object(("id", String()), ("description", String()))))),
            ["plan"] = Object(("inputs", Array(Ref("input"))), ("root", Ref("scope")), ("groups", Array(Ref("group"))), ("choices", Array(Ref("choice")))),
            ["scope"] = Object(("tasks", Array(Ref("task"))), ("outputs", Array(Ref("output"))), ("always", Array(Ref("task")))),
            ["group"] = Object(("id", Identity()), ("inputs", Array(Ref("input"))), ("body", Ref("scope"))),
            ["choice"] = Object(("id", Identity()), ("question", Nonblank()), ("type", Ref("businessType")),
                ("alternatives", Array(Object(("id", Nonblank()), ("description", String()), ("value", Ref("literal"))), 2)),
                ("recommended", String())),
            ["task"] = Tasks()
        };
        if (state.Requirements is not null) root["$defs"]!.AsObject().Remove("requirements");
        return root;
    }
    private static JsonObject Tasks()
    {
        JsonObject Task(string kind, params (string Name, JsonObject Schema)[] fields) => Object(new (string Name, JsonObject Schema)[]
        { ("id", Identity()), ("kind", Enum(kind)), ("objective", Nonblank()), ("dependsOn", Ref("identities")) }.Concat(fields).ToArray());
        return new() { ["anyOf"] = new JsonArray(
            Task("operation", ("operation", String()), ("inputs", Array(Ref("output")))),
            Described(Task("value", ("outputs", Array(Ref("output")))), "Copies or assembles supplied values; its objective does not execute a computation."),
            Described(Task("transform", ("inputs", NonEmptyArray(Ref("output"))),
                ("resultType", Object(("kind", Enum("object")), ("fields", NonEmptyArray(Ref("resultField")))))),
                "Interprets bound business data using the objective as instruction. Declare required typed result fields; use nullable fields for missing values. No defaults or opaque result types."),
            Task("sequence", ("body", Ref("scope"))),
            Task("conditional", ("condition", Ref("value")), ("body", Ref("scope")), ("otherwise", Ref("scope"))),
            Task("parallel", ("branches", Array(Ref("scope"), 2)), ("maxConcurrency", Integer(1, 100))),
            Task("foreach", ("items", Ref("value")), ("body", Ref("scope")), ("parallel", Type("boolean")), ("maxItems", Integer(1, 10000)), ("maxConcurrency", Integer(1, 100))),
            Task("call", ("group", Identity()), ("inputs", Array(Ref("output"))))) };
    }
    private static JsonObject Described(JsonObject schema, string description) { schema["description"] = description; return schema; }
    private static JsonObject Identity() => new() { ["type"] = "string", ["pattern"] = TaskPlanCompiler.IdentityPattern };
    private static JsonObject Nonblank() => new() { ["type"] = "string", ["pattern"] = @"\S" };
    private static JsonObject Integer(int minimum, int maximum) => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
    internal static JsonObject String() => Type("string");
    internal static JsonObject Type(string type) => new() { ["type"] = type };
    internal static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    internal static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    internal static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    internal static JsonObject Array(JsonObject item, int? minimum = null, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = item };
        if (minimum is { } min) schema["minItems"] = min;
        if (maximum is { } max) schema["maxItems"] = max;
        return schema;
    }
    internal static JsonObject NonEmptyArray(JsonObject item) { var array = Array(item); array["minItems"] = 1; return array; }
    internal static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false
    };
}
