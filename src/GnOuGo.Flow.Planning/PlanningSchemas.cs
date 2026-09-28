using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>The model sees business tasks and ports, never executor graph plumbing.</summary>
internal static class PlanningSchemas
{
    internal static bool HasQueryProperty(JsonNode? node) => node switch
    {
        JsonObject obj => obj["properties"] is JsonObject properties && properties.ContainsKey("query") || obj.Any(p => HasQueryProperty(p.Value)),
        JsonArray array => array.Any(HasQueryProperty),
        _ => false
    };

    internal static bool AllowsNoPlan(JsonNode? schema) =>
        schema?["properties"]?["discoveryRequests"]?["type"]?.ToString() == "null" &&
        schema["properties"]?["plan"]?["anyOf"] is JsonArray alternatives &&
        alternatives.Any(a => a?["type"]?.ToString() == "null");

    internal static JsonObject Proposal(PlanningSession state)
    {
        var actions = new List<JsonNode?>();
        if (PlanningDiscoveryContext.CanDiscover(state))
            foreach (var source in state.Discovery.Sources)
            {
                var receipts = state.Discovery.Pages.Where(p => p.SourceId == source.Id).ToArray();
                // Even a small exhausted source may fall outside the global shortlist.
                var canRefine = receipts.Any(p => p.UnavailableReason is null && (p.NextCursor is not null || p.Capabilities.Count > 0));
                if (receipts.Length == 0 || canRefine)
                    actions.Add(Object(("sourceId", Enum(source.Id)), ("cursor", Type("null")),
                        ("query", Nullable(new JsonObject { ["type"] = "string", ["pattern"] = @"\S", ["maxLength"] = 512 })), ("operationIds", Type("null"))));
                foreach (var cursor in receipts.Select(p => p.NextCursor).OfType<string>().Distinct(StringComparer.Ordinal)
                    .Where(cursor => !receipts.Any(p => p.Cursor == cursor)))
                    actions.Add(Object(("sourceId", Enum(source.Id)), ("cursor", Enum(cursor)), ("query", Type("null")), ("operationIds", Type("null"))));
                if (receipts.Any(p => p.Capabilities.Count > 0) || state.Discovery.Inspections?.Any(i => i.SourceId == source.Id) == true)
                    actions.Add(Described(Object(("sourceId", Enum(source.Id)), ("cursor", Type("null")), ("query", Type("null")),
                        ("operationIds", Array(Nonblank(), 0, receipts.SelectMany(p => p.Capabilities).Select(c => c.Operation?.Id).OfType<string>().Distinct(StringComparer.Ordinal).Count()))),
                        "Replace this source's inspection selection with already-discovered operation IDs; empty clears it. Selected exact contracts remain visible across pages. This grants no permission."));
            }
        var root = Object(
            ("discoveryRequests", actions.Count == 0 ? Type("null") : Nullable(Array(new JsonObject { ["anyOf"] = new JsonArray(actions.ToArray()) }, 1, 4))),
            ("plan", Nullable(Ref("plan"))));
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
                Described(Object(("kind", Enum("json")), ("items", Array(Ref("value"), 1, 1))), "Deterministically encode the single business value as JSON text; no inference or string interpolation."),
                Described(Object(("kind", Enum("field")), ("items", Array(Ref("value"), 1, 1)), ("port", Nonblank())), "Select one declared field of the single typed business object, including a loop item. The port is a literal field name, not a path. Nest selections for nested fields; no transform is needed."),
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
            ["businessType"] = BusinessTypes(transform: false),
            ["field"] = Input(objectField: true),
            ["resultType"] = BusinessTypes(transform: true),
            ["resultField"] = Object(("name", Nonblank()), ("type", Ref("resultType"))),
            ["input"] = Input(objectField: false),
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
    // Omitted flags use the existing DTO defaults. Every actual business value stays explicit.
    private static JsonObject BusinessTypes(bool transform)
    {
        var alternatives = new List<JsonNode?>();
        foreach (var nullable in new[] { false, true })
        {
            JsonObject Shape(params (string Name, JsonObject Schema)[] fields) => Object(nullable ? [..fields, ("nullable", Boolean(true))] : fields);
            var nested = transform ? "resultType" : "businessType";
            alternatives.Add(Shape(("kind", Enum("array")), ("items", Ref(nested))));
            alternatives.Add(Shape(("kind", Enum("object")), ("fields", Array(Ref(transform ? "resultField" : "field")))));
            alternatives.Add(Shape(("kind", Enum("string")), ("enum", Array(String(), 1, 256))));
            alternatives.Add(Shape(("kind", transform ? Enum("string", "number", "integer", "boolean") : Enum("string", "number", "integer", "boolean", "any"))));
        }
        return new() { ["anyOf"] = new JsonArray(alternatives.ToArray()) };
    }

    private static JsonObject Input(bool objectField)
    {
        var alternatives = new List<JsonNode?>();
        foreach (var required in new[] { true, false })
            foreach (var hasDefault in new[] { false, true })
            {
                if (!objectField && !required && !hasDefault) continue;
                var fields = new List<(string Name, JsonObject Schema)> { ("name", String()), ("type", Ref("businessType")) };
                if (!required) fields.Add(("required", Boolean(false)));
                if (hasDefault) fields.Add(("default", Ref("literal")));
                alternatives.Add(Object(fields.ToArray()));
            }
        return new() { ["anyOf"] = new JsonArray(alternatives.ToArray()) };
    }

    private static JsonObject Boolean(bool value) => new() { ["type"] = "boolean", ["enum"] = new JsonArray(value) };
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
