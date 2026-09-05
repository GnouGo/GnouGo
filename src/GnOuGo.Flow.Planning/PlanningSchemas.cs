using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningSchemas
{
    public static JsonObject Graph(PlanningPreparation preparation, bool fragment = false)
    {
        var definitions = new JsonObject
        {
            ["value"] = Object(
                ("kind", Enum("null", "string", "number", "boolean", "object", "array", "input", "output", "expression", "template", "workflow")),
                ("text", Nullable(String())), ("number", Nullable(Type("number"))), ("boolean", Nullable(Type("boolean"))),
                ("source", Nullable(String())), ("path", Array(String())), ("members", Array(Ref("member"))), ("items", Array(Ref("value")))),
            ["member"] = Object(("name", String()), ("value", Ref("value"))),
            ["schema"] = Object(("type", Enum("string", "number", "integer", "boolean", "array", "object")),
                ("nullable", Type("boolean")), ("description", Nullable(String())), ("enum", Array(String())),
                ("items", Nullable(Ref("schema"))), ("properties", Array(Ref("port"))), ("additionalProperties", Nullable(Ref("schema"))),
                ("capabilityId", Nullable(String())), ("schemaPointer", Nullable(String()))),
            ["port"] = Object(("name", String()), ("schema", Ref("schema")), ("required", Type("boolean")), ("default", Nullable(Ref("value")))),
            ["output"] = Object(("name", String()), ("schema", Ref("schema")), ("value", Ref("value"))),
            ["retry"] = Object(("max", Type("integer")), ("backoffMs", Type("integer")), ("backoffMult", Type("number")), ("jitterMs", Type("integer"))),
            ["errorCase"] = Object(("if", Nullable(Ref("value"))), ("action", Enum("stop", "continue")), ("setOutput", Nullable(Ref("value"))), ("retry", Nullable(Ref("retry")))),
            ["branch"] = Object(("steps", Array(Ref("node")))),
            ["case"] = Object(("value", Nullable(String())), ("when", Nullable(Ref("value"))), ("steps", Array(Ref("node")))),
            ["node"] = Object(("key", String()), ("type", Enum(preparation.AllowedStepTypes.ToArray())), ("purpose", String()),
                ("capabilityId", Nullable(String())), ("operationIds", Array(String())), ("input", Ref("value")),
                ("if", Nullable(Ref("value"))), ("expr", Nullable(Ref("value"))), ("outputSchema", Nullable(Ref("schema"))),
                ("output", Nullable(String())), ("itemVar", Nullable(String())), ("indexVar", Nullable(String())),
                ("retry", Nullable(Ref("retry"))), ("onError", Array(Ref("errorCase"))), ("steps", Array(Ref("node"))),
                ("branches", Array(Ref("branch"))), ("cases", Array(Ref("case"))), ("default", Array(Ref("node")))),
            ["workflow"] = Object(("key", String()), ("purpose", String()), ("operationIds", Array(String())),
                ("inputs", Array(Ref("port"))), ("outputs", Array(Ref("output"))),
                ("steps", Array(Ref("node"))), ("finally", Array(Ref("node"))), ("functions", Nullable(String())))
        };
        var root = fragment
            ? (JsonObject)definitions["workflow"]!.DeepClone()
            : Object(("summary", String()), ("workflows", Array(Ref("workflow"))), ("entrypoint", String()), ("functions", Nullable(String())));
        root["$defs"] = definitions;
        return root;
    }

    public static JsonObject Intent() => Object(
        ("outcome", Enum("ready", "questions", "unsupported")), ("reason", String()), ("evidence", String()),
        ("questions", Array(Object(("id", String()), ("prompt", String()), ("evidence", String()),
            ("options", Array(Object(("value", String()), ("description", String()), ("recommended", Type("boolean")))))))));

    public static JsonObject Review() => Object(("findings", Array(Object(
        ("code", String()), ("workflow", String()), ("message", String()), ("evidence", String()), ("blocking", Type("boolean"))))));

    public static JsonObject Revision() => Object(("affectedWorkflows", Array(String())), ("changesBehavior", Type("boolean")), ("evidence", String()));

    private static JsonObject String() => Type("string");
    private static JsonObject Type(string type) => new() { ["type"] = type };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    private static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    private static JsonObject Array(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()),
        ["additionalProperties"] = false
    };
}
