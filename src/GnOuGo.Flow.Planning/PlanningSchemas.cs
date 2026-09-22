using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningSchemas
{
    internal static JsonObject Grounded(IEnumerable<string>? capabilityIds = null)
    {
        var root = Object(("summary", String()), ("inputs", Array(Ref("input"))), ("operations", Array(Ref("operation"))),
            ("outputs", Array(Ref("output"))), ("subflows", Array(Ref("subflow"))));
        var definitions = Definitions(capabilityIds);
        if (capabilityIds is not null)
        {
            root["properties"]!["blockedActions"] = Array(Object(("actionId", String()), ("reason", String())));
            root["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("blockedActions"));
        }
        if (capabilityIds is not null && !capabilityIds.Any()) definitions["operation"]!["anyOf"]!.AsArray().RemoveAt(0);
        if (capabilityIds is not null)
        {
            var variants = definitions["operation"]!["anyOf"]!.AsArray();
            string[] common = ["id", "semanticAction", "businessOutputs", "purpose", "after", "when"];
            var commonFields = variants[0]!["properties"]!.AsObject().Where(p => common.Contains(p.Key)).Select(p => (p.Key, p.Value!.DeepClone().AsObject())).ToArray();
            definitions["implementation"] = new JsonObject { ["anyOf"] = new JsonArray(variants.Select(v => (JsonNode)Object(v!["properties"]!.AsObject()
                .Where(p => !common.Contains(p.Key)).Select(p => (p.Key, p.Value!.DeepClone().AsObject())).ToArray())).ToArray()) };
            definitions["operation"] = Object(commonFields.Concat(new[] { ("implementation", Ref("implementation")) }).ToArray());
        }
        root["$defs"] = definitions; PlanningJsonTransport.PruneDefinitions(root); return root;
    }
    internal static JsonObject Definitions(IEnumerable<string>? capabilityIds = null)
    {
        // The unrestricted form validates stored intent shape, without executable escape hatches.
        // Newly issued model requests always supply their exposed/allowed identities.
        var ids = capabilityIds?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var capability = ids is null || ids.Length == 0 ? String() : Enum(ids);
        JsonObject Operation(string kind, params (string Name, JsonObject Schema)[] fields) => Object(new[] {
            ("kind", Enum(kind)), ("id", String()), ("semanticAction", String()), ("businessOutputs", Ref("business_outputs")), ("purpose", String()), ("after", Ref("strings")), ("when", Ref("optional_value")) }.Concat(fields).ToArray());
        JsonObject Input(JsonObject type, JsonObject value) => Object(("name", String()), ("type", type), ("optional", Type("boolean")), ("default", value));
        JsonObject BusinessType(bool nullableOnly = false)
        {
            JsonObject Nullability() => nullableOnly ? new() { ["type"] = "boolean", ["enum"] = new JsonArray(true) } : Type("boolean");
            return new() { ["anyOf"] = new JsonArray(
                Object(("type", Enum("string", "number", "integer", "boolean", "opaque")), ("nullable", Nullability()), ("enum", Array(String()))),
                Object(("type", Enum("array")), ("nullable", Nullability()), ("items", Ref("type"))),
                Object(("type", Enum("object")), ("nullable", Nullability()), ("fields", NonEmptyArray(Ref("field"))))) };
        }
        return new()
        {
            ["strings"] = Array(String()),
            ["optional_value"] = Nullable(Ref("value")),
            ["business_outputs"] = Array(Object(("name", String()), ("path", Array(String())))),
            ["result_contract"] = Object(("capability", capability.DeepClone().AsObject()), ("direction", Enum("input", "output")), ("path", Array(String()))),
            ["value"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("null"))), Object(("kind", Enum("string")), ("text", String())),
                Object(("kind", Enum("number")), ("number", Type("number"))), Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))),
                Object(("kind", Enum("object")), ("members", Array(Ref("member")))), Object(("kind", Enum("array")), ("items", Array(Ref("value")))),
                Object(("kind", Enum("input", "result", "item", "index")), ("source", String()), ("path", Array(String()))),
                Object(("kind", Enum("compute", "template")), ("text", String()), ("members", Array(Ref("member"))))) },
            ["member"] = Object(("name", String()), ("value", Ref("value"))),
            ["literal_null"] = Object(("kind", Enum("null"))),
            ["literal_nonnull"] = new JsonObject { ["anyOf"] = new JsonArray(
                Object(("kind", Enum("string")), ("text", String())), Object(("kind", Enum("number")), ("number", Type("number"))),
                Object(("kind", Enum("boolean")), ("boolean", Type("boolean"))), Ref("literal_object"), Ref("literal_array")) },
            ["literal"] = new JsonObject { ["anyOf"] = new JsonArray(Ref("literal_null"), Ref("literal_nonnull")) },
            ["literal_object"] = Object(("kind", Enum("object")), ("members", Array(Object(("name", String()), ("value", Ref("literal")))))),
            ["literal_array"] = Object(("kind", Enum("array")), ("items", Array(Ref("literal")))),
            ["type"] = BusinessType(),
            ["nullable_type"] = BusinessType(nullableOnly: true),
            ["field"] = Object(("name", String()), ("type", Ref("type")), ("optional", Type("boolean"))),
            // JSON null is absence. A literal null requires a nullable declaration, or a
            // derived contract whose nullability is checked by deterministic validation.
            ["input"] = Input(Nullable(Ref("type")), Nullable(Ref("literal"))),
            ["output"] = Object(("name", String()), ("value", Ref("value"))),
            ["block"] = Object(("operations", Array(Ref("operation"))), ("result", Ref("value"))),
            ["branch"] = Object(("name", String()), ("body", Ref("block"))),
            ["subflow"] = Object(("name", String()), ("inputs", Array(Ref("input"))), ("operations", Array(Ref("operation"))), ("outputs", Array(Ref("output")))),
            ["question"] = Object(("id", String()), ("question", String()), ("answerType", Ref("type"))),
            ["operation"] = new JsonObject { ["anyOf"] = new JsonArray(
                Operation("invoke", ("capability", capability), ("arguments", Array(Ref("member"))), ("fallback", Nullable(Ref("value")))),
                Operation("calculate", ("value", Ref("value"))),
                Operation("transform", ("instruction", String()), ("data", Array(Ref("member"))), ("resultType", Ref("type")), ("resultContract", Type("null"))),
                Operation("transform", ("instruction", String()), ("data", Array(Ref("member"))), ("resultType", Type("null")), ("resultContract", Ref("result_contract"))),
                Operation("choose", ("condition", Ref("value")), ("then", Ref("block")), ("otherwise", Ref("block"))),
                Operation("each", ("items", Ref("value")), ("parallel", Type("boolean")), ("body", Ref("block"))),
                Operation("parallel", ("branches", Array(Ref("branch")))),
                Operation("call", ("flow", String()), ("arguments", Array(Ref("member")))),
                Operation("cleanup", ("operations", Array(Ref("operation")))),
                Operation("validate", ("value", Ref("value")), ("format", Enum("json_value", "json_text")), ("resultType", Ref("type")), ("resultContract", Type("null"))),
                Operation("validate", ("value", Ref("value")), ("format", Enum("json_value", "json_text")), ("resultType", Type("null")), ("resultContract", Ref("result_contract")))) }
        };
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
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()),
        ["additionalProperties"] = false
    };
}
