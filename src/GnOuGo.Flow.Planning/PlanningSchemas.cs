using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
internal static class PlanningSchemas
{
    internal static JsonObject ValueDefinitions()
    {
        var definitions = new JsonObject
        {
            ["value"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null", "hole", "omitted"))), Object(("kind", Enum("string", "expression")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("member")))), Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input")), ("source", String()), ("path", Array(String()))),
                Object(("kind", Enum("output")), ("source", String()), ("resultChannel", Enum("default", "structured", "envelope")), ("path", Array(String()))),
                Object(("kind", Enum("workflow")), ("source", String())),
                Object(("kind", Enum("template", "compute")), ("text", String()), ("members", Array(Ref("member")))),
                Object(("kind", Enum("loop_item", "loop_index", "loop_previous", "artifact_collection")), ("source", String()), ("path", Array(String()))))
            },
            ["member"] = Object(("name", String()), ("value", Ref("value"))),
            ["schema"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                Object(("capabilityId", String()), ("schemaPointer", String())),
                Object(("type", Enum("hole"))),
                Object(("type", Enum("string", "number", "integer", "boolean")),
                    ("nullable", Type("boolean")), ("description", Nullable(String())), ("enum", Array(String()))),
                Object(("type", Enum("array")), ("nullable", Type("boolean")), ("description", Nullable(String())), ("items", Ref("schema"))),
                Object(("type", Enum("object")), ("nullable", Type("boolean")), ("description", Nullable(String())),
                    ("properties", NonEmptyArray(Ref("port"))), ("additionalProperties", Nullable(Ref("schema")))),
                Object(("type", Enum("object")), ("nullable", Type("boolean")), ("description", Nullable(String())),
                    ("properties", Array(Ref("port"))), ("additionalProperties", Ref("schema"))))
            },
            ["literal"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Ref("literalObject"), Object(("kind", Enum("array")), ("items", Array(Ref("literal"))))) },
            ["literalObject"] = Object(("kind", Enum("object")), ("members", Array(Ref("literalMember")))),
            ["literalMember"] = Object(("name", String()), ("value", Ref("literal"))),
            ["port"] = Object(("name", String()), ("schema", Ref("schema")), ("required", Type("boolean")), ("default", Nullable(Ref("value")))),
            ["output"] = Object(("name", String()), ("schema", Ref("schema")), ("value", Ref("value"))),
            ["retry"] = Object(("max", Type("integer")), ("backoffMs", Type("integer")), ("backoffMult", Type("number")), ("jitterMs", Type("integer"))),
            ["errorCase"] = Object(("if", Nullable(Ref("value"))), ("action", Enum("stop", "continue")), ("setOutput", Nullable(Ref("value"))), ("retry", Nullable(Ref("retry"))))
        };
        return definitions;
    }

    internal static JsonObject Intent()
    {
        var root = Object(("summary", String()), ("entrypoint", String()), ("functions", Nullable(String())),
            ("workflows", Array(Ref("workflow"))), ("fixtures", Nullable(Ref("fixtures"))), ("questions", Array(Ref("question"))));
        var defs = ValueDefinitions();
        defs["step"] = Object(("key", String()), ("kind", String()), ("purpose", String()), ("capabilityId", Nullable(String())),
            ("dependencies", Array(String())), ("input", Ref("value")), ("if", Nullable(Ref("value"))), ("expr", Nullable(Ref("value"))),
            ("outputSchema", Nullable(Ref("schema"))), ("structuredOutput", Nullable(Ref("structured"))), ("output", Nullable(String())),
            ("itemVar", Nullable(String())), ("indexVar", Nullable(String())), ("retry", Nullable(Ref("retry"))), ("onError", Array(Ref("errorCase"))),
            ("steps", Array(Ref("step"))), ("branches", Array(Ref("branch"))), ("cases", Array(Ref("case"))), ("default", Array(Ref("step"))));
        defs["workflow"] = Object(("key", String()), ("purpose", String()), ("inputs", Array(Ref("port"))), ("outputs", Array(Ref("output"))),
            ("steps", Array(Ref("step"))), ("finally", Array(Ref("step"))), ("functions", Nullable(String())));
        defs["structured"] = Object(("schema", Ref("schema")), ("strict", Type("boolean")));
        defs["branch"] = Object(("steps", Array(Ref("step"))));
        defs["case"] = Object(("value", Nullable(String())), ("when", Nullable(Ref("value"))), ("steps", Array(Ref("step"))));
        defs["fixtures"] = FixtureFields();
        defs["observation"] = ObservationFields();
        defs["question"] = Object(("id", String()), ("question", String()), ("answerSchema", Ref("schema")));
        root["$defs"] = defs;
        return root;
    }
    internal static JsonObject Fixtures()
    {
        var root = FixtureFields();
        var defs = ValueDefinitions(); defs["observation"] = ObservationFields(); root["$defs"] = defs;
        PlanningJsonTransport.PruneDefinitions(root);
        return root;
    }
    private static JsonObject FixtureFields() => Object(("inputs", Nullable(Ref("literalObject"))), ("observations", Array(Ref("observation"))));
    private static JsonObject ObservationFields() => Object(("workflow", String()), ("node", String()), ("responses", Array(Ref("literal"))));
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
