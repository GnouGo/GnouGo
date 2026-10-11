using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class ValueProjectionTests
{
    [Theory]
    [InlineData(false, "{\"a\":null,\"b\":\"fallback\"}", "null", true)]
    [InlineData(false, "{\"b\":\"fallback\"}", "\"fallback\"", true)]
    [InlineData(false, "{}", null, false)]
    [InlineData(false, "{\"a\":17,\"b\":\"fallback\"}", null, false)]
    [InlineData(true, "[]", "[]", true)]
    [InlineData(true, "[{\"a\":\"x\"},{\"b\":\"y\"},{\"a\":null},{\"a\":\"x\"}]", "[\"x\",\"y\",null,\"x\"]", true)]
    [InlineData(true, "[{\"a\":\"x\"},{}]", null, false)]
    [InlineData(true, "[{\"a\":\"x\"},{\"a\":17,\"b\":\"fallback\"}]", null, false)]
    [InlineData(true, "null", null, false)]
    [InlineData(true, "{}", null, false)]
    public async Task CheckedSelectionPreservesPresenceAndPublishesAtomically(bool each, string source, string? expected, bool valid)
    {
        var schema = JsonNode.Parse("""{"type":["string","null"]}""")!.AsObject();
        if (each) schema = new() { ["type"] = "array", ["items"] = schema };
        var document = Document(new JsonObject { ["value"] = JsonNode.Parse(source), ["paths"] = new JsonArray(new JsonArray("a"), new JsonArray("b")), ["each"] = each }, schema);
        var original = document.Workflows["main"].Steps[0].Input!.ToJsonString();
        var result = await Run(document);
        Assert.Equal(valid, result.Success);
        if (valid) Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected!), result.StepResults[0].Output!["value"]));
        else { Assert.Null(result.StepResults[0].Output); Assert.Contains(result.Error!.Code, new[] { ErrorCodes.InputValidation, "CONTRACT_UNSATISFIED" }); }
        Assert.Equal(valid ? 2 : 1, result.StepResults.Count);
        Assert.Equal(original, document.Workflows["main"].Steps[0].Input!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyPathChecksWholeValuesWithoutFlattening(bool each)
    {
        var nested = JsonNode.Parse("[[1,2],[],[1,2]]");
        var schema = JsonNode.Parse("""{"type":"array","items":{"type":"array","items":{"type":"integer"}}}""")!.AsObject();
        var input = new JsonObject { ["value"] = nested, ["paths"] = new JsonArray(new JsonArray()) };
        if (each) input["each"] = true; // omission preserves historical scalar behavior
        var result = await Run(Document(input, schema));
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(nested, result.StepResults[0].Output!["value"]));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public async Task InvalidProjectionModeFailsBeforeConsumption(string mode)
    {
        var result = await Run(Document(new JsonObject { ["value"] = "ok", ["paths"] = new JsonArray(new JsonArray()), ["each"] = JsonNode.Parse(mode) }, new() { ["type"] = "string" }));
        Assert.False(result.Success); Assert.Single(result.StepResults);
    }

    [Theory]
    [InlineData("{\"required\":\"present\",\"rows\":[\"a\"]}", true)]
    [InlineData("{\"required\":null,\"rows\":[\"a\"]}", false)]
    [InlineData("{\"rows\":[\"a\"]}", false)]
    [InlineData("{\"required\":\"present\",\"rows\":[null]}", false)]
    public async Task ExplicitWholeValueContractChecksRequirednessAndNestedNulls(string source, bool valid)
    {
        var schema = JsonNode.Parse("""{"type":"object","required":["required","rows"],"properties":{"required":{"type":"string"},"rows":{"type":"array","items":{"type":"string"}}}}""")!.AsObject();
        var result = await Run(Document(new() { ["value"] = JsonNode.Parse(source), ["paths"] = new JsonArray(new JsonArray()) }, schema));
        Assert.Equal(valid, result.Success);
    }

    [Theory]
    [InlineData("\"{\\\"x\\\":7}\"", true)]
    [InlineData("\"null\"", true)]
    [InlineData("\"{bad\"", false)]
    [InlineData("\"\"", false)]
    [InlineData("null", false)]
    [InlineData("17", false)]
    public async Task StrictTextParsingFailsEvenWhenTargetAllowsNull(string encoded, bool valid)
    {
        var document = Document(new() { ["value"] = "${data.steps.parse.value}", ["paths"] = new JsonArray(new JsonArray()) },
            JsonNode.Parse("""{"type":["object","null"],"required":["x"],"properties":{"x":{"type":"integer"}}}""")!.AsObject());
        document.Workflows["main"].Steps.Insert(0, new() { Id = "parse", Type = "set", Input = new JsonObject
        {
            ["value"] = "${((text) => { if (typeof text !== 'string') throw new Error('Expected JSON text'); return JSON.parse(text); })(data.inputs.text)}"
        } });
        var result = await Run(document, new() { ["text"] = JsonNode.Parse(encoded) });
        Assert.Equal(valid, result.Success);
        if (!valid) Assert.DoesNotContain(result.StepResults, s => s.StepId == "consume");
    }

    [Fact]
    public async Task CancellationPublishesNoProjection()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var document = Document(new() { ["value"] = new JsonArray("a"), ["paths"] = new JsonArray(new JsonArray()), ["each"] = true }, new() { ["type"] = "array" });
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], new JsonObject(), cancellation.Token);
        Assert.False(result.Success); Assert.DoesNotContain(result.StepResults, s => s.Output is not null);
    }

    private static WorkflowDocument Document(JsonObject input, JsonObject valueSchema)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - {id: project, type: set, input: {}}
                  - {id: consume, type: set, input: {done: true}}
            """);
        var project = document.Workflows["main"].Steps[0];
        var source = input["value"] is JsonValue text && text.TryGetValue<string>(out var binding) && binding.StartsWith("${", StringComparison.Ordinal)
            ? binding[2..^1] : input["value"]?.ToJsonString() ?? "null";
        var script = "({value:m.select(source," + input["paths"]!.ToJsonString() + "," + (input.ContainsKey("each") ? input["each"]?.ToJsonString() ?? "null" : "false") + ")})";
        project.Input = JsonValue.Create("${checkedMapping(" + JsonValue.Create(script)!.ToJsonString() + "," + source + ")}");
        project.OutputSchema = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("value"), ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["value"] = valueSchema } };
        return document;
    }
    private static Task<RunResult> Run(WorkflowDocument document, JsonObject? inputs = null) => new WorkflowEngine().ExecuteAsync(
        new WorkflowCompiler().Compile(document).Workflows["main"], inputs ?? new JsonObject(), TestContext.Current.CancellationToken);
}
