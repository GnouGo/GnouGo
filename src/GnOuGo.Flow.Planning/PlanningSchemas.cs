using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningSchemas
{
    internal static JsonObject Intent()
    {
        var root = Object(("summary", String()), ("inputs", Array(Ref("input"))), ("operations", Array(Ref("operation"))),
            ("outputs", Array(Ref("output"))), ("subflows", Array(Ref("subflow"))), ("questions", Array(Ref("question"))));
        root["$defs"] = Definitions(); return root;
    }
    internal static JsonObject Definitions()
    {
        JsonObject Operation(string kind, params (string Name, JsonObject Schema)[] fields) => Object(new[] {
            ("kind", Enum(kind)), ("id", String()), ("purpose", String()), ("after", Array(String())), ("when", Nullable(Ref("value"))) }.Concat(fields).ToArray());
        return new()
        {
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null", "missing"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("member")))), Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input", "result", "item", "index")), ("source", String()), ("path", Array(String()))),
                Object(("kind", Enum("compute", "template")), ("text", String()), ("members", Array(Ref("member"))))) },
            ["member"] = Object(("name", String()), ("value", Ref("value"))),
            ["type"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("type", Enum("string", "number", "integer", "boolean")), ("nullable", Type("boolean")), ("enum", Array(String()))),
                Object(("type", Enum("array")), ("nullable", Type("boolean")), ("items", Ref("type"))),
                Object(("type", Enum("object")), ("nullable", Type("boolean")), ("fields", NonEmptyArray(Ref("field"))))) },
            ["field"] = Object(("name", String()), ("type", Ref("type")), ("optional", Type("boolean"))),
            ["input"] = Object(("name", String()), ("type", Nullable(Ref("type"))), ("optional", Type("boolean")), ("default", Nullable(Ref("value")))),
            ["output"] = Object(("name", String()), ("value", Ref("value"))),
            ["block"] = Object(("operations", Array(Ref("operation"))), ("result", Ref("value"))),
            ["branch"] = Object(("name", String()), ("body", Ref("block"))),
            ["subflow"] = Object(("name", String()), ("inputs", Array(Ref("input"))), ("operations", Array(Ref("operation"))), ("outputs", Array(Ref("output")))),
            ["question"] = Object(("id", String()), ("question", String()), ("answerType", Ref("type"))),
            ["operation"] = new JsonObject { ["anyOf"] = new JsonArray(
                Operation("invoke", ("capability", Nullable(String())), ("arguments", Array(Ref("member"))), ("fallback", Nullable(Ref("value")))),
                Operation("calculate", ("value", Ref("value")), ("resultType", Nullable(Ref("type")))),
                Operation("transform", ("instruction", String()), ("data", Array(Ref("member"))), ("resultType", Nullable(Ref("type")))),
                Operation("choose", ("condition", Ref("value")), ("then", Ref("block")), ("otherwise", Ref("block"))),
                Operation("each", ("items", Ref("value")), ("parallel", Type("boolean")), ("body", Ref("block"))),
                Operation("parallel", ("branches", Array(Ref("branch")))),
                Operation("call", ("flow", String()), ("arguments", Array(Ref("member")))),
                Operation("cleanup", ("operations", Array(Ref("operation"))))) }
        };
    }
    internal static JsonObject Choices(IEnumerable<(PlanningHole Hole, IReadOnlyList<PlanningChoice> Choices)> holes)
        => Object(holes.Select(h => (h.Hole.Id, Enum(h.Choices.Select(c => c.Id).ToArray()))).ToArray());
    private static JsonObject String() => Type("string");
    private static JsonObject Type(string type) => new() { ["type"] = type };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    private static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    private static JsonObject Array(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject NonEmptyArray(JsonObject item) { var array = Array(item); array["minItems"] = 1; return array; }
    private static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()),
        ["additionalProperties"] = false
    };
}
