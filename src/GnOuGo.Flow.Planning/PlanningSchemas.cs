using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningSchemas
{
    public static JsonObject Graph(PlanningPreparation preparation, bool fragment = false)
    {
        var definitions = new JsonObject
        {
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string", "expression")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("member")))), Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input")), ("source", String()), ("path", Array(String()))),
                Object(("kind", Enum("output")), ("source", String()), ("resultChannel", Enum("default", "structured", "envelope")), ("path", Array(String()))),
                Object(("kind", Enum("workflow")), ("source", String())),
                Object(("kind", Enum("template")), ("text", String()), ("members", Array(Ref("member"))))) },
            ["member"] = Object(("name", String()), ("value", Ref("value"))),
            ["schema"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("reference")), ("capabilityId", String()), ("schemaPointer", String())),
                Object(("kind", Enum("inline")), ("type", Enum("string", "number", "integer", "boolean", "array", "object")),
                ("nullable", Type("boolean")), ("description", Nullable(String())), ("enum", Array(String())),
                ("items", Nullable(Ref("schema"))), ("properties", Array(Ref("port"))), ("additionalProperties", Nullable(Ref("schema"))))) },
            ["port"] = Object(("name", String()), ("schema", Ref("schema")), ("required", Type("boolean")), ("default", Nullable(Ref("value")))),
            ["output"] = Object(("name", String()), ("schema", Ref("schema")), ("value", Ref("value"))),
            ["retry"] = Object(("max", Type("integer")), ("backoffMs", Type("integer")), ("backoffMult", Type("number")), ("jitterMs", Type("integer"))),
            ["errorCase"] = Object(("if", Nullable(Ref("value"))), ("action", Enum("stop", "continue")), ("setOutput", Nullable(Ref("value"))), ("retry", Nullable(Ref("retry")))),
            ["branch"] = Object(("steps", Array(Ref("node")))),
            ["case"] = Object(("value", Nullable(String())), ("when", Nullable(Ref("value"))), ("steps", Array(Ref("node")))),
            ["node"] = Object(("key", String()), ("type", Enum(preparation.AllowedStepTypes.ToArray())), ("purpose", String()),
                ("capabilityId", Nullable(String())), ("operationIds", Array(String())), ("input", Ref("value")),
                ("if", Nullable(Ref("value"))), ("expr", Nullable(Ref("value"))), ("outputSchema", Nullable(Ref("schema"))),
                ("structuredOutput", Nullable(Object(("schema", Ref("schema")), ("strict", Type("boolean"))))),
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
        ("outcome", Enum("ready", "questions", "unsupported")), ("reason", String()), ("evidence", Evidence()),
        ("questions", Array(Object(("id", String()), ("prompt", String()), ("evidence", Evidence()),
            ("options", Array(Object(("value", String()), ("description", String()), ("recommended", Type("boolean")))))))));

    public static JsonObject Behavior(PlanningPreparation? preparation = null)
    {
        var root = Object(("summary", String()), ("entrypoint", String()), ("workflows", Array(Ref("behaviorWorkflow"))));
        root["$defs"] = new JsonObject
        {
            ["behaviorPort"] = Object(("name", String()), ("description", String()), ("required", Type("boolean"))),
            ["behaviorOutcome"] = Object(("key", String()), ("description", String()), ("isDefault", Type("boolean")), ("steps", Array(Ref("behaviorNode")))),
            ["behaviorNode"] = Object(("key", String()), ("kind", Enum("operation", "decision", "loop", "sequence", "parallel", "confirmation", "workflow")),
                ("purpose", String()), ("operationIds", Array(String())), ("capabilityId", Nullable(String())), ("workflowKey", Nullable(String())),
                ("inputDependencies", Array(String())),
                ("outcomes", Array(Ref("behaviorOutcome"))), ("steps", Array(Ref("behaviorNode")))),
            ["behaviorWorkflow"] = Object(("key", String()), ("purpose", String()), ("operationIds", Array(String())),
                ("inputs", Array(Ref("behaviorPort"))), ("outputs", Array(Ref("behaviorPort"))),
                ("steps", Array(Ref("behaviorNode"))), ("finally", Array(Ref("behaviorNode"))))
        };
        if (preparation is not null)
        {
            var operations = preparation.Capabilities.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var definition in new[] { "behaviorNode", "behaviorWorkflow" })
            {
                var field = Array(operations.Length == 0 ? String() : Enum(operations));
                if (operations.Length == 0) field["maxItems"] = 0;
                root["$defs"]![definition]!["properties"]!["operationIds"] = field;
            }
            var node = root["$defs"]!["behaviorNode"]!;
            var variants = new JsonArray();
            foreach (var kind in new[] { "operation", "decision", "loop", "sequence", "parallel", "confirmation", "workflow" })
            {
                var variant = node.DeepClone(); var properties = variant["properties"]!;
                properties["kind"] = Enum(kind);
                var ids = preparation.Capabilities.Where(c => PlanningCapabilityBindings.SupportsBehavior(c, kind)).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray();
                properties["capabilityId"] = ids.Length == 0 ? Type("null") : Nullable(Enum(ids));
                if (kind != "decision") properties["outcomes"]!["maxItems"] = 0;
                if (kind is not ("loop" or "sequence" or "parallel")) properties["steps"]!["maxItems"] = 0;
                if (kind != "workflow") properties["workflowKey"] = Type("null");
                variants.Add(variant);
            }
            root["$defs"]!["behaviorNode"] = new JsonObject { ["anyOf"] = variants };
        }
        return root;
    }

    internal static JsonObject BehaviorRepair(PlanningPreparation preparation, PlanningBehaviorPlan candidate)
    {
        var schema = Behavior(preparation);
        var names = candidate.Workflows.SelectMany(w => w.Inputs).Select(p => p.Name).Distinct(StringComparer.Ordinal).ToArray();
        var dependencies = Array(names.Length == 0 ? String() : Enum(names));
        if (names.Length == 0) dependencies["maxItems"] = 0;
        foreach (var variant in schema["$defs"]!["behaviorNode"]!["anyOf"]!.AsArray())
            variant!["properties"]!["inputDependencies"] = dependencies.DeepClone();
        return schema;
    }

    private static JsonObject Evidence() => Array(Object(("sourceId", String()), ("excerpt", String())));

    public static JsonObject Review(IEnumerable<string>? workflows = null, IEnumerable<string>? locations = null) => Object(("findings", Array(Object(
        ("code", String()), ("workflow", workflows is null ? String() : Enum(workflows.ToArray())), ("location", locations is null ? String() : Enum(locations.ToArray())),
        ("message", String()), ("evidence", String()), ("blocking", Type("boolean"))))));

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
