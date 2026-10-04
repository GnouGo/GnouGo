using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class McpDecimalBoundaryTests
{
    [Fact]
    public async Task NestedDecimalContractsConvertWithoutChangingProducerDataAndRetainRawReceipt()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"values":{"type":"array","items":{"$ref":"#/$defs/amount"}}},"required":["values"],"$defs":{"amount":{"type":"number","format":"decimal"}}}""")!.AsObject();
        var source = JsonNode.Parse("""{"values":[0.1234567890123456789012345678,2.5]}""")!;
        var original = source.ToJsonString(); var fixture = new Fixture(schema, schema, source);
        var result = await fixture.Run(new JsonObject { ["values"] = new JsonArray(0.1 + 0.2, 2.5) });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0.30000000000000004m, fixture.Request!["values"]![0]!.GetValue<decimal>());
        Assert.Equal((double)0.1234567890123456789012345678m, result.Outputs!["value"]!["values"]![0]!.GetValue<double>());
        Assert.Equal(original, source.ToJsonString());
        var run = await fixture.Store.ReadAsync("tenant", "numeric", TestContext.Current.CancellationToken);
        var invocation = Assert.Single(run!.Invocations.Values, i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "calculate");
        Assert.Equal(original, invocation.Observation!["raw_response"]!.ToJsonString());
        Assert.Null(invocation.Output!["raw_response"]);
        Assert.True(invocation.ExternalCompletionObserved);
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, fixture.Cleanup);
    }

    [Theory]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    [InlineData("\"12.5\"")]
    [InlineData("null")]
    public async Task InvalidDecimalInputsNeverDispatchAndDoNotRequireReconciliation(string value)
    {
        var fixture = new Fixture(Amount(), Amount(), new JsonObject { ["amount"] = 1 });
        var result = await fixture.Run(new JsonObject { ["amount"] = JsonNode.Parse(value) });
        Assert.False(result.Success); Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Equal("/amount", result.Error.Details!["instance_pointer"]!.ToString());
        Assert.Equal("input", result.Error.Details["direction"]!.ToString());
        Assert.Equal(0, fixture.Calls); Assert.Equal(1, fixture.Cleanup);
        Assert.Equal(WorkflowRunStatus.Failed, (await fixture.Store.ReadAsync("tenant", "numeric", TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    [InlineData("\"12.5\"")]
    public async Task InvalidDecimalOutputsHaveDurableFailureReceiptsAndAreNotExecutedAgain(string value)
    {
        var fixture = new Fixture(Amount(), Amount(), new JsonObject { ["amount"] = JsonNode.Parse(value) });
        var result = await fixture.Run(new JsonObject { ["amount"] = 1 });
        Assert.False(result.Success); Assert.Equal(ErrorCodes.McpCallError, result.Error!.Code);
        Assert.Contains("/amount", result.Error.Details!.ToJsonString());
        Assert.Contains("output", result.Error.Details.ToJsonString());
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, fixture.Cleanup);
        var saved = await fixture.Store.ReadAsync("tenant", "numeric", TestContext.Current.CancellationToken);
        var resumed = await fixture.Engine.ResumeAsync("tenant", "numeric", saved!.Revision, fixture.Document.Workflows["main"], TestContext.Current.CancellationToken);
        Assert.False(resumed.Success); Assert.Equal(ErrorCodes.McpCallError, resumed.Error!.Code);
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, fixture.Cleanup);
    }

    [Fact]
    public async Task AmbiguousDecimalUnionFailsWithoutGuessing()
    {
        var input = Amount(); input["properties"]!["amount"] = JsonNode.Parse("""{"anyOf":[{"type":"number","format":"decimal"},{"type":"number"}]}""");
        var fixture = new Fixture(input, Amount(), new JsonObject { ["amount"] = 1 });
        var result = await fixture.Run(new JsonObject { ["amount"] = 1 });
        Assert.False(result.Success); Assert.Equal(0, fixture.Calls);
        Assert.Contains("disagree", result.Error!.Message);
    }

    [Fact]
    public async Task ProducerOutputConstraintsAreCheckedBeforePermittedBinary64Rounding()
    {
        var output = Amount();
        output["properties"]!["amount"]!["maximum"] = JsonNode.Parse("79228162514264337593543950335");
        var fixture = new Fixture(Amount(), output, JsonNode.Parse("""{"amount":79228162514264337593543950335}""")!);
        var result = await fixture.Run(new JsonObject { ["amount"] = 1 });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal((double)decimal.MaxValue, result.Outputs!["value"]!["amount"]!.GetValue<double>());
        // A later decimal consumer cannot accept a rounded value beyond its own range.
        var consumer = new Fixture(Amount(), new() { ["type"] = "object" }, new JsonObject());
        var rejected = await consumer.Run(result.Outputs["value"]!.DeepClone());
        Assert.False(rejected.Success); Assert.Equal(ErrorCodes.InputValidation, rejected.Error!.Code);
        Assert.Equal(0, consumer.Calls);
    }

    [Fact]
    public async Task DiscriminatorSelectsTheDecimalBranchAndConflictingDeclarationsFail()
    {
        var schema = JsonNode.Parse("""{"oneOf":[{"type":"object","properties":{"kind":{"const":"money"},"amount":{"type":"number","format":"decimal"}},"required":["kind","amount"]},{"type":"object","properties":{"kind":{"const":"ordinary"},"amount":{"type":"number"}},"required":["kind","amount"]}]}""")!.AsObject();
        var fixture = new Fixture(schema, new() { ["type"] = "object" }, new JsonObject());
        var result = await fixture.Run(new JsonObject { ["kind"] = "money", ["amount"] = 0.25 });
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(0.25m, fixture.Request!["amount"]!.GetValue<decimal>());

        var conflicting = Amount(); conflicting["properties"]!["amount"]!["allOf"] = JsonNode.Parse("""[{"type":"number","format":"double"}]""");
        var rejected = new Fixture(conflicting, new() { ["type"] = "object" }, new JsonObject());
        var failure = await rejected.Run(new JsonObject { ["amount"] = 0.25 });
        Assert.False(failure.Success); Assert.Equal(0, rejected.Calls); Assert.Contains("conflicting", failure.Error!.Message);
    }

    [Fact]
    public async Task SharedDecimalDeclarationRemainsAuthoritativeAcrossMatchingAlternatives()
    {
        var input = Amount();
        input["properties"]!["amount"]!["anyOf"] = JsonNode.Parse("""[{"type":"number"},{"type":"number","format":"decimal"}]""");
        var fixture = new Fixture(input, new() { ["type"] = "object" }, new JsonObject());
        var result = await fixture.Run(new JsonObject { ["amount"] = 0.25 });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0.25m, fixture.Request!["amount"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecimalDeclarationsReuseExistingRootAndArrayPointerResolution(bool rootReference)
    {
        var input = JsonNode.Parse(rootReference
            ? """{"type":"object","properties":{"amount":{"type":"number","format":"decimal"},"next":{"anyOf":[{"type":"null"},{"$ref":"#"}]}},"required":["amount"]}"""
            : """{"type":"object","properties":{"amount":{"$ref":"#/allOf/0/properties/amount"}},"allOf":[{"properties":{"amount":{"type":"number","format":"decimal"}}}],"required":["amount"]}""")!.AsObject();
        var fixture = new Fixture(input, new() { ["type"] = "object" }, new JsonObject());
        var request = new JsonObject { ["amount"] = 0.25 };
        if (rootReference) request["next"] = new JsonObject { ["amount"] = 0.5, ["next"] = null };
        var result = await fixture.Run(request);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0.25m, fixture.Request!["amount"]!.GetValue<decimal>());
        if (rootReference) Assert.Equal(0.5m, fixture.Request["next"]!["amount"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("79228162514264337593543950335", true)]
    [InlineData("79228162514264337593543950336", false)]
    [InlineData("0.0000000000000000000000000001", true)]
    [InlineData("0.00000000000000000000000000001", false)]
    public async Task DecimalRangeAndUnderflowAreCheckedBeforeDispatch(string token, bool valid)
    {
        var fixture = new Fixture(Amount(), new() { ["type"] = "object" }, new JsonObject());
        var result = await fixture.Run(new JsonObject { ["amount"] = JsonNode.Parse(token) });
        Assert.Equal(valid, result.Success); Assert.Equal(valid ? 1 : 0, fixture.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullableAndOmittedValuesArePreserved(bool present)
    {
        var input = Amount(); input.Remove("required"); input["properties"]!["amount"]!["type"] = new JsonArray("number", "null");
        var fixture = new Fixture(input, new JsonObject { ["type"] = "object" }, new JsonObject());
        var result = await fixture.Run(present ? new JsonObject { ["amount"] = null } : new JsonObject());
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(present, fixture.Request!.AsObject().ContainsKey("amount")); Assert.Null(fixture.Request["amount"]);
    }

    [Theory]
    [InlineData("1e200", "{\"type\":\"number\",\"minimum\":1e100}", true)]
    [InlineData("1e-200", "{\"type\":\"number\",\"minimum\":1e-100}", false)]
    [InlineData("0.3", "{\"type\":\"number\",\"multipleOf\":0.1}", true)]
    [InlineData("0.30000000000000004", "{\"type\":\"number\",\"multipleOf\":0.1}", false)]
    [InlineData("1e200", "{\"type\":\"integer\",\"multipleOf\":1e100}", true)]
    [InlineData("1.0000000000000001", "{\"type\":\"integer\"}", false)]
    public void NumericSchemaConstraintsUseSerializedValuesWithoutDecimalRangeOrEpsilon(string value, string schema, bool valid)
    {
        Assert.Empty(PlanningContractValidation.ValidateSchema(JsonNode.Parse(schema)!));
        Assert.Equal(valid, PlanningContractValidation.ValidateInstance(JsonNode.Parse(value), JsonNode.Parse(schema)!).Count == 0);
    }

    private static JsonObject Amount() => JsonNode.Parse("""{"type":"object","properties":{"amount":{"type":"number","format":"decimal"}},"required":["amount"]}""")!.AsObject();

    private sealed class Fixture
    {
        public int Calls, Cleanup;
        public JsonNode? Request;
        public InMemoryWorkflowRunStore Store { get; } = new();
        public WorkflowEngine Engine { get; }
        public CompiledDocument Document { get; }
        public Fixture(JsonObject input, JsonObject output, JsonNode content)
        {
            var factory = new InMemoryMcpClientFactory();
            factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "calculate", InputSchema = input, OutputSchema = output }, new() { Name = "cleanup", InputSchema = new JsonObject { ["type"] = "object" } }],
                ToolHandlers = new() { ["calculate"] = request => { Calls++; Request = request!.DeepClone(); return new() { Content = content }; },
                    ["cleanup"] = _ => { Cleanup++; return new() { Content = new JsonObject() }; } } });
            Engine = new() { McpClientFactory = factory, RunStore = Store, Limits = new() { TenantId = "tenant", RunId = "numeric" } };
            Document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: call
                        type: mcp.call
                        input: {server: arbitrary, method: calculate, request: "${data.inputs.request}", preserve_optional_nulls: true}
                    finally:
                      - id: cleanup
                        type: mcp.call
                        input: {server: arbitrary, method: cleanup, request: {}}
                    outputs:
                      value: "${data.steps.call.response}"
                """));
        }
        public Task<RunResult> Run(JsonNode request) => Engine.ExecuteAsync(Document.Workflows["main"], new JsonObject { ["request"] = request }, TestContext.Current.CancellationToken);
    }
}
