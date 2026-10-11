using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class SchemaExpressionAlternativeTests
{
    private static JsonObject RowsSchema(string keyword = "oneOf", bool reverse = false)
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"path":{"type":"string"},"payload":{"type":["string","null"]},
             "entries":{"type":["array","null"],"items":{"type":"array","items":{"type":["string","null"]}}}},"required":["path"],
             "oneOf":[{"required":["payload"],"properties":{"payload":{"type":"string"},"entries":{"type":"null"}}},
                      {"required":["entries"],"properties":{"entries":{"type":"array"},"payload":{"type":"null"}}}]}
            """)!.AsObject();
        var branches = schema["oneOf"]!.AsArray().Select(n => n!.DeepClone()).ToArray();
        schema.Remove("oneOf"); schema[keyword] = new JsonArray(reverse ? branches.Reverse().ToArray() : branches);
        return schema;
    }
    private static readonly Dictionary<string, JsonNode?> Sources = new()
    {
        ["source"] = JsonNode.Parse("""{"type":"object","properties":{"rows":{"type":"array","items":{"type":"array","items":{"type":["string","null"]}}},"number":{"type":"number"},"text":{"type":"string"},"nullable":{"type":["string","null"]}},"required":["rows","number","text","nullable"]}""")
    };
    private static IReadOnlyList<StepExpressionTypeMismatch> Check(JsonNode? value, JsonObject schema) =>
        StepExpressionTypeValidator.ValidateInput(value, schema, null, Sources);

    [Theory]
    [InlineData("oneOf", false)] [InlineData("oneOf", true)]
    [InlineData("anyOf", false)] [InlineData("anyOf", true)]
    public void TypedRowsRetainSiblingItemContractsWithoutDependingOnBranchOrder(string keyword, bool reverse)
    {
        var schema = RowsSchema(keyword, reverse);
        var request = new JsonObject { ["path"] = "output.xlsx", ["entries"] = "${data.steps.source.rows}" };
        Assert.Empty(Check(request, schema));
        schema["properties"]!["entries"]!["items"]!["items"] = JsonNode.Parse("""{"type":"number"}""");
        Assert.NotEmpty(Check(request, schema));
        var literal = JsonNode.Parse("""{"path":"output.xlsx","entries":[[" exact ",null]]}""");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(literal, schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(literal, RowsSchema(keyword, reverse)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WholeObjectsCannotCombineFieldsFromDifferentAlternatives(bool reverse)
    {
        var schema = JsonNode.Parse("""{"anyOf":[{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}}},{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}}}]}""")!.AsObject();
        if (reverse) schema["anyOf"] = new JsonArray(schema["anyOf"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
        var request = new JsonObject { ["a"] = "${data.steps.source.text}", ["b"] = "${data.steps.source.number}" };
        Assert.NotEmpty(Check(request, schema));
        request["b"] = "${data.steps.source.text}";
        Assert.Empty(Check(request, schema));
    }

    [Theory]
    [InlineData("const")] [InlineData("enum")]
    public void LiteralDiscriminatorSelectsCompleteAlternative(string constraint)
    {
        var schema = JsonNode.Parse("""{"anyOf":[{"properties":{"tag":{"const":"numeric"},"value":{"type":"number"}}},{"properties":{"tag":{"const":"text"},"value":{"type":"string"}}}]}""")!.AsObject();
        if (constraint == "enum") foreach (var b in schema["anyOf"]!.AsArray())
        { var tag = b!["properties"]!["tag"]!.AsObject(); tag["enum"] = new JsonArray(tag["const"]!.DeepClone()); tag.Remove("const"); }
        Assert.Empty(Check(new JsonObject { ["tag"] = "text", ["value"] = "${data.steps.source.text}" }, schema));
        Assert.NotEmpty(Check(new JsonObject { ["tag"] = "numeric", ["value"] = "${data.steps.source.text}" }, schema));
    }

    [Fact]
    public void ReferencesAllOfAndNestedAlternativesPreserveCommonAssertions()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"nested":{"$ref":"#/$defs/value","allOf":[{"properties":{"entries":{"type":"array","items":{"type":"array","items":{"type":"number"}}}}}]}},
             "$defs":{"value":SCHEMA}}
            """.Replace("SCHEMA", RowsSchema().ToJsonString(), StringComparison.Ordinal))!.AsObject();
        Assert.NotEmpty(Check(new JsonObject { ["nested"] = new JsonObject { ["path"] = "out.xlsx", ["entries"] = "${data.steps.source.rows}" } }, schema));
        schema["properties"]!["nested"]!.AsObject().Remove("allOf");
        Assert.Empty(Check(new JsonObject { ["nested"] = new JsonObject { ["path"] = "out.xlsx", ["entries"] = "${data.steps.source.rows}" } }, schema));
    }

    [Fact]
    public void NullableSourceAndNumericOverlapRemainTyped()
    {
        Assert.NotEmpty(Check(JsonValue.Create("${data.steps.source.nullable}"), JsonNode.Parse("""{"anyOf":[{"type":"string"},{"type":"number"}]}""")!.AsObject()));
        Assert.Empty(Check(JsonValue.Create("${data.steps.source.nullable}"), JsonNode.Parse("""{"anyOf":[{"type":"string"},{"type":"null"}]}""")!.AsObject()));
        var overlap = JsonNode.Parse("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""")!.AsObject();
        Assert.Empty(Check(JsonValue.Create("${data.steps.source.number}"), overlap));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create(3), overlap));
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(3.5), overlap));
    }

    [Fact]
    public void ActualSemanticValidatorAcceptsTypedRowsAndDefersUnresolvedOneOfMembership()
    {
        var doc = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: source
                    type: set
                    input: { rows: [[exact, null]] }
                    output_schema:
                      type: object
                      required: [rows]
                      properties:
                        rows: {type: array, items: {type: array, items: {type: [string, 'null']}}}
                  - id: store
                    type: mcp.call
                    input: {server: generic, method: persist, request: {path: output.xlsx, entries: '${data.steps.source.rows}'}}
            """);
        WorkflowPlanSemanticValidator.Validate(doc, [new("generic", "persist", RowsSchema(), null, null)]);
        var schema = JsonNode.Parse("""{"type":"object","properties":{"entries":{"oneOf":[{"type":"array","minItems":1},{"type":"array","maxItems":2}]}}}""")!;
        WorkflowPlanSemanticValidator.Validate(doc, [new("generic", "persist", schema, null, null)]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"entries":[["exact",null]]}"""), schema));
    }

    [Fact]
    public void AssembledRowsNullAndExclusiveArgumentsKeepTheirContracts()
    {
        var schema = RowsSchema();
        var assembled = JsonNode.Parse("""{"path":"out.xlsx","payload":null,"entries":[["${data.steps.source.text}",null]]}""");
        Assert.Empty(Check(assembled, schema));
        assembled!["payload"] = "${data.steps.source.text}";
        Assert.NotEmpty(Check(assembled, schema));
        var before = Check(assembled, schema).Select(e => (e.Field, e.ExpectedType, e.ActualType, e.Message)).ToArray();
        Assert.Equal(before, Check(assembled, RowsSchema(reverse: true)).Select(e => (e.Field, e.ExpectedType, e.ActualType, e.Message)));
        foreach (var json in new[] { "{}", "{\"path\":\"out.xlsx\",\"entries\":null}", "{\"path\":\"out.xlsx\",\"entries\":[null]}" })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse(json), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"path":"out.xlsx","payload":""}"""), schema));
    }

    [Fact]
    public void UnknownTagsDoNotProveUniqueMembershipButKnownEnumsStillExcludeAlternatives()
    {
        var schema = JsonNode.Parse("""{"oneOf":[{"const":"one"},{"const":"two"}]}""")!.AsObject();
        Assert.Empty(Check(JsonValue.Create("${data.steps.source.text}"), schema));
        var sources = new Dictionary<string, JsonNode?> { ["source"] = JsonNode.Parse("""{"type":"string","enum":["one","two"]}""") };
        Assert.Empty(StepExpressionTypeValidator.ValidateInput(JsonValue.Create("${data.steps.source}"), schema, null, sources));
        sources["source"]!["enum"] = new JsonArray("one", "other");
        Assert.NotEmpty(StepExpressionTypeValidator.ValidateInput(JsonValue.Create("${data.steps.source}"), schema, null, sources));
        sources["source"]!["enum"] = new JsonArray("one");
        schema["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["const"] = "one" });
        Assert.NotEmpty(StepExpressionTypeValidator.ValidateInput(JsonValue.Create("${data.steps.source}"), schema, null, sources));
    }

    [Fact]
    public async Task UnresolvedOneOfOverlapIsRejectedBeforeExternalDispatch()
    {
        var schema = JsonNode.Parse("""{"type":"object","required":["entries"],"properties":{"entries":{"oneOf":[{"type":"array","minItems":1},{"type":"array","maxItems":2}]}}}""")!.AsObject();
        var parsed = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: source
                    type: set
                    input: {entries: [[exact]]}
                  - id: persist
                    type: mcp.call
                    input: {server: generic, method: store, request: {entries: '${data.steps.source.entries}'}}
            """);
        WorkflowPlanSemanticValidator.Validate(parsed, [new("generic", "store", schema, null, null)]);
        var calls = 0;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("generic", new()
        {
            Tools = [new() { Name = "store", InputSchema = schema }],
            ToolHandlers = { ["store"] = _ => { calls++; return new() { Content = new JsonObject() }; } }
        });
        var compiled = new WorkflowCompiler().Compile(parsed);
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.False(result.Success); Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("exactly one", result.Error.Message); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeepTaggedAlternativesKeepChildTypesAndReferenceSiblings(bool tagFirst)
    {
        JsonObject schema = new() { ["type"] = "string" };
        JsonNode value = JsonValue.Create("${data.steps.source.text}")!;
        for (var depth = 0; depth < 24; depth++)
        {
            var alternatives = new JsonArray();
            foreach (var tag in new[] { "left", "right" })
            {
                var properties = new JsonObject();
                if (tagFirst) properties["tag"] = new JsonObject { ["const"] = tag };
                properties["child"] = schema.DeepClone();
                if (!tagFirst) properties["tag"] = new JsonObject { ["const"] = tag };
                alternatives.Add(new JsonObject { ["type"] = "object", ["required"] = new JsonArray("tag", "child"), ["properties"] = properties });
            }
            // Use references instead of exponentially copying the schema tree.
            if (depth == 0) schema = new() { ["anyOf"] = alternatives };
            else
            {
                foreach (var branch in alternatives) branch!["properties"]!["child"] = new JsonObject { ["$ref"] = "#/$defs/level" + (depth - 1) };
                var definitions = schema["$defs"]?.DeepClone().AsObject() ?? new JsonObject();
                var previous = schema.DeepClone().AsObject(); previous.Remove("$defs");
                definitions["level" + (depth - 1)] = previous;
                schema = new() { ["anyOf"] = alternatives, ["$defs"] = definitions };
            }
            value = new JsonObject { ["child"] = value, ["tag"] = "right" };
        }
        Assert.Empty(Check(value, schema));
        var leaf = value;
        while (leaf["child"] is JsonObject) leaf = leaf["child"]!;
        leaf["child"] = "${data.steps.source.number}";
        Assert.NotEmpty(Check(value, schema));
    }
}
