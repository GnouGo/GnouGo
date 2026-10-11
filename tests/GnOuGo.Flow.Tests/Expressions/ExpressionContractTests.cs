using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed class ExpressionContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonObject Metadata(string pointer = "") => new() { [pointer] = new JsonObject() };

    [Fact]
    public void TraversalRetainsCumulativeAccountingWithoutBecomingRepeatedProgramStatements()
    {
        var rows = new JsonArray(Enumerable.Range(0, 1602).Select(i => (JsonNode)new JsonObject
        {
            ["reference"] = "snapshot:record:" + i, ["text"] = "Observed item", ["value"] = i,
            ["actions"] = new JsonArray("follow"), ["optional"] = null
        }).ToArray());
        var context = new JsonObject { ["inputs"] = new JsonObject { ["records"] = rows } };
        const string historical = "${checkedMapping('source.map((item,index)=>({index:index,value:item}))',data.inputs.records)}";
        const string readable = "{value:data.inputs.records.map((item,index)=>({index:index,value:item}))}";
        var evaluator = new ExpressionEvaluator();
        var expected = new StringInterpolator(evaluator).ResolveDeep(JsonValue.Create(historical), context);
        var allowance = new GnOuGo.Flow.Core.Scripting.JintSandbox.MappingAllowance(100000, TimeSpan.FromSeconds(15), 50000000);
        var actual = evaluator.EvaluateCheckedExpression(readable, context, new(), allowance);
        Assert.True(JsonNode.DeepEquals(expected, actual!["value"]));
        Assert.Equal(1601, actual["value"]![1601]!["index"]!.GetValue<int>());
        var counters = allowance.Snapshot();
        Assert.InRange(counters["statements"]!.GetValue<int>(), 10001, 100000);
        Assert.InRange(counters["materialized_bytes"]!.GetValue<long>(), 1, 50000000);
        Assert.Throws<WorkflowRuntimeException>(() => evaluator.EvaluateCheckedExpression(readable, context, new(),
            new GnOuGo.Flow.Core.Scripting.JintSandbox.MappingAllowance(10000, TimeSpan.FromSeconds(15), 50000000)));
    }

    [Theory]
    [InlineData("{value:data.inputs.value}", "{\"value\":9007199254740993}")]
    [InlineData("{value:data.inputs.value}", "{\"value\":1.23000000000000000000001}")]
    [InlineData("{value:data.inputs.value}", "{\"value\":null}")]
    [InlineData("{value:data.inputs.value.map(item=>item)}", "{\"value\":[null,1,1,[2]]}")]
    [InlineData("{value:data.inputs.value.flatMap(item=>item)}", "{\"value\":[[null,1],[],[1,[2]]]}")]
    public void ExactCopiesDoNotRoundTripThroughJavaScriptNumbers(string program, string input)
    {
        var context = new JsonObject { ["inputs"] = JsonNode.Parse(input) };
        var interpolator = new StringInterpolator(new());
        var actual = interpolator.ResolveDeep(JsonValue.Create("${(" + program + ")}"), context, Metadata());
        var expected = input;
        if (program.Contains("flatMap", StringComparison.Ordinal)) expected = "{\"value\":[null,1,1,[2]]}";
        Assert.Equal(expected, actual!.ToJsonString());
        Assert.Equal(input, context["inputs"]!.ToJsonString());
    }

    [Theory]
    [InlineData("{value:data.inputs.absent}")]
    [InlineData("{value:data.inputs.rows.flatMap(item=>item)}")]
    [InlineData("{value:eval('1')}")]
    [InlineData("{value:Object.keys(data)}")]
    [InlineData("{value:m.scalar()}")]
    [InlineData("{value:m.lookup(data.inputs.rows,[],data.inputs.key)}")]
    public void InvalidOrUnsupportedExpressionsFailWithoutContextFallback(string program)
    {
        var context = JsonNode.Parse("""{"inputs":{"rows":[[],null]},"unrelated":"not imported"}""");
        Assert.Throws<WorkflowRuntimeException>(() => new StringInterpolator(new()).ResolveDeep(JsonValue.Create("${(" + program + ")}"), context, Metadata()));
    }

    [Fact]
    public void FirstPresentNullAndInvalidValuesNeverFallThrough()
    {
        const string program = "${({value:m.select(data.inputs,[[\"first\"],[\"second\"]],false)})}";
        foreach (var value in new[] { "null", "\"invalid\"", "false" })
        {
            var context = new JsonObject { ["inputs"] = JsonNode.Parse("{\"first\":" + value + ",\"second\":true}") };
            var actual = new StringInterpolator(new()).ResolveDeep(JsonValue.Create(program), context, Metadata());
            Assert.True(JsonNode.DeepEquals(context["inputs"]!["first"], actual!["value"]));
        }
    }

    [Fact]
    public void OptionalCompletionChecksShortCircuitWithoutMakingMissingSelectionsValid()
    {
        var context = JsonNode.Parse("""{"steps":{}}""");
        var interpolator = new StringInterpolator(new());
        var result = interpolator.ResolveDeep(JsonValue.Create("${({value:data.steps.optional != null && data.steps.optional.value})}"), context, Metadata());
        Assert.False(result!["value"]!.GetValue<bool>());
        Assert.Throws<WorkflowRuntimeException>(() => interpolator.ResolveDeep(JsonValue.Create("${({value:data.steps.optional.value})}"), context, Metadata()));
    }

    [Theory]
    [InlineData("/absent", "${data.inputs}")]
    [InlineData("/bad~2key", "${data.inputs}")]
    [InlineData("", "prefix ${data.inputs}")]
    [InlineData("", "literal")]
    public void MetadataPointersMustIdentifyExactlyOneCompleteExpression(string pointer, string value)
        => Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.ValidateExpressionContracts(JsonValue.Create(value), Metadata(pointer)));

    [Fact]
    public void EscapedPointersAndArrayIndicesAreLiteralAndNotConsumerArguments()
    {
        var input = JsonNode.Parse("""{"a/b~c":["${({value:data.inputs.value})}"]}""");
        var result = new StringInterpolator(new()).ResolveDeep(input, JsonNode.Parse("""{"inputs":{"value":"é ` ${literal} \\ quote\""}}"""), Metadata("/a~1b~0c/0"));
        Assert.Equal("é ` ${literal} \\ quote\"", result!["a/b~c"]![0]!["value"]!.GetValue<string>());
        Assert.DoesNotContain("expression_contracts", result.ToJsonString());
    }

    [Theory]
    [InlineData("wrong", "{}")]
    [InlineData("selected", "{\"schema\":{}}")]
    [InlineData("selected", "{\"origin\":\"stage\",\"schema\":\"not a schema\"}")]
    public void ContractDeclarationsMustMatchLiteralMetadata(string name, string contract)
    {
        var input = JsonValue.Create("${(()=>{const selected={value:1};return selected;})()}");
        var metadata = new JsonObject { [""] = new JsonObject { [name] = JsonNode.Parse(contract) } };
        Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.ValidateExpressionContracts(input, metadata));
    }

    [Fact]
    public async Task UnusedInvalidIntermediateStopsBeforeDependentEvaluationAndExternalDispatch()
    {
        var step = new StepDef { Id = "checked", Type = "mcp.call", Input = JsonValue.Create("${(()=>{const gate={allowed:false};const selected={value:data.inputs.absent};return selected;})()}"),
            ExpressionContracts = new JsonObject { [""] = new JsonObject {
                ["gate"] = new JsonObject { ["origin"] = "/tasks/permission", ["schema"] = JsonNode.Parse("""{"type":"object","properties":{"allowed":{"const":true}},"required":["allowed"]}""") },
                ["selected"] = new JsonObject { ["origin"] = "/tasks/selection", ["schema"] = new JsonObject() } } } };
        var document = new WorkflowDocument { Workflows = new() { ["main"] = new() { Steps = [step] } } };
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], new JsonObject(), Ct);
        Assert.False(result.Success); Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
        Assert.Equal("/tasks/permission", result.Error.Details!["location"]!.GetValue<string>());
        Assert.DoesNotContain("absent", result.Error.Message);
    }

    [Fact]
    public void CommonMetadataRoundTripsAndRejectsDuplicatePointers()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                - id: value
                  type: set
                  input: |
                    ${({value:1})}
                  expression_contracts:
                    '': {}
            """;
        var step = WorkflowParser.Parse(yaml).Workflows["main"].Steps[0];
        Assert.NotNull(step.ExpressionContracts);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(step, WorkflowRunJsonContext.Default.StepDef)!;
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, WorkflowRunJsonContext.Default.StepDef)!;
        Assert.True(JsonNode.DeepEquals(step.ExpressionContracts, restored.ExpressionContracts));
        Assert.ThrowsAny<Exception>(() => WorkflowParser.Parse(yaml.Replace("'': {}", "'': {}\n        '': {}", StringComparison.Ordinal)));
    }

    [Fact]
    public void LargeUnrelatedSnapshotsAreNeitherImportedNorCopiedForScalarSelection()
    {
        const string historical = "${checkedMapping('({value:source.selected})',data.inputs)}";
        const string readable = "${({value:data.inputs.selected})}";
        var interpolator = new StringInterpolator(new()); var allocations = new List<long>();
        foreach (var characters in new[] { 0, 150000, 300000 })
        {
            var inputs = new JsonObject { ["selected"] = true,
                ["observations"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode)new JsonObject { ["body"] = new string('x', characters) }).ToArray()) };
            var context = new JsonObject { ["inputs"] = inputs };
            var expected = interpolator.Interpolate(historical, context);
            interpolator.ResolveDeep(JsonValue.Create(readable), context, Metadata());
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = interpolator.ResolveDeep(JsonValue.Create(readable), context, Metadata());
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(JsonNode.DeepEquals(expected, actual));
            Assert.All(inputs["observations"]!.AsArray(), item => Assert.Equal(characters, item!["body"]!.GetValue<string>().Length));
        }
        Assert.InRange(allocations.Max() - allocations.Min(), 0, 4096);
        Assert.All(allocations, bytes => Assert.InRange(bytes, 1, 50000));
    }

    [Fact]
    public void NewExpressionsKeepNestedResourceCeilingsAndCannotHideUncheckedConstants()
    {
        var input = JsonValue.Create("${data.inputs.rows.map(item=>({value:item}))}");
        var context = new JsonObject { ["inputs"] = new JsonObject { ["rows"] = new JsonArray(Enumerable.Range(0, 100).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) } };
        Assert.Throws<WorkflowRuntimeException>(() => new StringInterpolator(new(null, 20, TimeSpan.FromSeconds(15))).ResolveDeep(input, context, Metadata()));
        Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.ValidateExpressionContracts(
            JsonValue.Create("${(()=>{const hidden={allowed:false};return {};})()}"), Metadata()));
        var huge = new JsonObject { ["inputs"] = new JsonObject { ["value"] = new string('x', 300000) } };
        Assert.Throws<WorkflowRuntimeException>(() => new StringInterpolator(new(null, 100000, TimeSpan.FromSeconds(15), 1000000))
            .ResolveDeep(JsonValue.Create("${({value:data.inputs.value})}"), huge, Metadata()));
    }

    [Fact]
    public async Task DeferredWhileContractsRunInTheIterationScopeAndReplayCommittedControls()
    {
        var condition = "${(()=>{const gate={index:data._loop.index};return gate.index < 2;})()}";
        var contracts = new JsonObject { ["gate"] = new JsonObject { ["origin"] = "/loop/condition", ["schema"] = JsonNode.Parse("""{"type":"object","properties":{"index":{"type":"integer"}},"required":["index"]}""") } };
        var doc = new WorkflowDocument { Workflows = new() { ["main"] = new() { Steps = [new() { Id = "loop", Type = "loop.sequential",
            Input = new JsonObject { ["while"] = condition, ["max_times"] = 3 }, ExpressionContracts = new JsonObject { ["/while"] = contracts },
            Steps = [new() { Id = "copy", Type = "set", Input = new JsonObject { ["index"] = "${data._loop.index}" } }] }] } } };
        var store = new InMemoryWorkflowRunStore(); var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "deferred" } };
        var workflow = new WorkflowCompiler().Compile(doc).Workflows["main"];
        var result = await engine.ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.StepResults[0].Output!["count"]!.GetValue<int>());
        var saved = (await store.ReadAsync("tenant", "deferred", Ct))!;
        var recovered = await new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "deferred", saved.Revision, workflow, Ct);
        Assert.True(recovered.Success);
        Assert.True(JsonNode.DeepEquals(result.StepResults[0].Output, recovered.StepResults[0].Output));
    }
}
