using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningSchemas
{
    internal static JsonObject Proposal(PlanningSession state)
    {
        var root = Object(("requirements", Ref("requirements")), ("sourceId", Nullable(String())), ("cursor", Nullable(String())),
            ("capabilityIds", Array(String())), ("graph", Nullable(Ref("graph"))), ("explanation", String()));
        var definitions = new JsonObject
        {
            ["strings"] = Array(String()),
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("member")))),
                Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input", "output", "loop_item", "loop_index", "workflow")), ("source", String()), ("path", Ref("strings"))),
                Object(("kind", Enum("expression")), ("text", String()))) },
            ["member"] = Object(("name", String()), ("value", Ref("value"))),
            ["schema"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("capabilityId", state.Catalog!.Capabilities.Count == 0 ? String() : Enum(state.Catalog.Capabilities.Select(c => c.Id).ToArray())),
                    ("schemaPointer", Describe(String(), "Exact schema position: /input or /output followed by JSON Schema segments, e.g. /output/properties/value. Never /outputSchema. No inline constraints on a reference."))),
                Object(("type", Enum("string", "number", "integer", "boolean", "array", "object", "any")),
                ("nullable", Type("boolean")), ("description", Nullable(String())), ("enum", Ref("strings")),
                ("items", Nullable(Ref("schema"))), ("properties", Array(Ref("port"))))) },
            ["port"] = Object(("name", String()), ("schema", Ref("schema")), ("required", Type("boolean")), ("default", Nullable(Ref("value")))),
            ["output"] = Object(("name", String()), ("schema", Ref("schema")), ("value", Ref("value"))),
            ["requirements"] = Object(("summary", String()),
                ("outcomes", NonEmptyArray(Object(("id", String()), ("description", String())))),
                ("questions", Array(Object(("id", String()), ("question", String()), ("answerType", Ref("schema")))))),
            ["graph"] = Object(("summary", String()), ("entrypoint", String()), ("workflows", NonEmptyArray(Ref("workflow")))),
            ["workflow"] = Object(("key", String()), ("purpose", String()), ("inputs", Array(Ref("port"))),
                ("outputs", Array(Ref("output"))), ("steps", Array(Ref("node"))), ("finally", Array(Ref("node")))),
            ["node"] = Nodes(state.Catalog!)
        };
        if (state.Catalog!.Capabilities.Count == 0) definitions["schema"] = definitions["schema"]!["anyOf"]![1]!.DeepClone();
        root["$defs"] = definitions;
        return root;
    }
    private static JsonObject Nodes(PlanningCatalog catalog)
    {
        var variants = new JsonArray();
        var remaining = catalog.AllowedStepTypes.ToHashSet(StringComparer.Ordinal);
        void Add(string[] types, params (string Name, JsonObject Schema)[] fields)
        {
            var allowed = types.Where(remaining.Remove).ToArray();
            if (allowed.Length == 0) return;
            variants.Add((JsonNode)Object(new (string Name, JsonObject Schema)[] {
                ("key", String()), ("type", Enum(allowed)), ("purpose", String()),
                ("dependencies", Ref("strings")), ("input", Ref("value")), ("if", Nullable(Ref("value")))
            }.Concat(fields).ToArray()));
        }
        JsonObject Structured() => Nullable(Object(("schema", Ref("schema")), ("strict", Type("boolean"))));
        foreach (var type in new[] { "mcp.call", "agent.run" })
        {
            var ids = catalog.Capabilities.Where(c => c.StepType == type).Select(c => c.Id).ToArray();
            if (ids.Length == 0) { remaining.Remove(type); continue; }
            if (type == "mcp.call") Add([type], ("capabilityId", Enum(ids)), ("structuredOutput", Structured()));
            else Add([type], ("capabilityId", Enum(ids)));
        }
        Add(["llm.call"], ("structuredOutput", Structured()));
        Add(["set", "value.validate", "value.project", "array.project"], ("outputSchema", Nullable(Ref("schema"))));
        Add(["sequence"], ("steps", Array(Ref("node"))));
        Add(["parallel"], ("branches", Array(Object(("steps", Array(Ref("node")))))));
        Add(["loop.sequential", "loop.parallel"], ("steps", Array(Ref("node"))), ("itemVar", Nullable(String())), ("indexVar", Nullable(String())));
        Add(["switch"], ("expr", Nullable(Ref("value"))),
            ("cases", Array(Object(("value", Nullable(String())), ("when", Nullable(Ref("value"))), ("steps", Array(Ref("node")))))),
            ("default", Array(Ref("node"))));
        Add(remaining.Order(StringComparer.Ordinal).ToArray());
        return variants.Count == 0 ? Type("null") : new() { ["anyOf"] = variants };
    }
    internal static JsonObject String() => Type("string");
    private static JsonObject Describe(JsonObject schema, string description) { schema["description"] = description; return schema; }
    internal static JsonObject Type(string type) => new() { ["type"] = type };
    internal static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    internal static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    internal static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    internal static JsonObject Array(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    internal static JsonObject NonEmptyArray(JsonObject item) { var array = Array(item); array["minItems"] = 1; return array; }
    internal static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()),
        ["additionalProperties"] = false
    };
}
