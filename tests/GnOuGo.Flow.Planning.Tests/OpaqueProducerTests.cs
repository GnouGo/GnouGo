using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class OpaqueProducerTests
{
    [Fact]
    public void MissingEmptyContainerContractsAreCompletedWithoutARepairOrChangingTheReceipt()
    {
        var preparation = Preparation(); var graph = Graph(); var workflow = graph.Workflows[0];
        var route = new PlanningNode { Key = "route", Type = "switch", Expr = Str("take"), Cases = [new("take", null, [])] };
        var loop = new PlanningNode { Key = "each", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "array" })) };
        var read = new PlanningNode { Key = "read", Type = "mcp.call", CapabilityId = "opaque", Input = Obj(("request", Obj())) };
        workflow.Steps.AddRange([route, loop, read]);
        preparation.Capabilities.Add(new() { Id = "opaque", StepType = "mcp.call", Server = "neutral", Method = "observe", Kind = "tool" });
        var unit = new PlanningConstructionUnit { Key = "contracts", Kind = "contracts", WorkflowKey = workflow.Key, NodeKeys = [route.Key, loop.Key, read.Key] };
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        var receipt = new JsonObject { ["nodes"] = new JsonObject { [read.Key] = new JsonObject { ["structuredOutput"] = null } } };
        var hash = PlanningGraphCompiler.Fingerprint(receipt.ToJsonString());
        Assert.Empty(PlanningConstruction.ShapeFindings(receipt, schema, unit));
        var lowered = PlanningConstruction.Apply(graph, unit, receipt, preparation);
        Assert.Equal(PlanningGraphCompiler.Fingerprint(graph), PlanningGraphCompiler.Fingerprint(lowered));
        Assert.Equal(hash, PlanningGraphCompiler.Fingerprint(receipt.ToJsonString()));
        receipt["nodes"]!.AsObject().Remove(read.Key);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(receipt, schema, unit));
    }

    [Theory]
    [InlineData("first", "observe")]
    [InlineData("renamed", "lire")]
    public void DeclaredObjectResultsDoNotAskTheModelToRecreateTheirSchema(string server, string method)
    {
        var preparation = Preparation(); var graph = Graph(); var workflow = graph.Workflows[0];
        preparation.Capabilities.Add(new() { Id = "typed", StepType = "mcp.call", Server = server, Method = method, Kind = "tool",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""")!.AsObject() });
        var node = new PlanningNode { Key = "read", Type = "mcp.call", CapabilityId = "typed", Input = Obj(("request", Obj())) }; workflow.Steps.Insert(0, node);
        var unit = new PlanningConstructionUnit { Key = "contract", Kind = "contracts", WorkflowKey = workflow.Key, NodeKeys = [node.Key] };
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.Equal("null", schema["properties"]!["nodes"]!["properties"]![node.Key]!["properties"]!["structuredOutput"]!["type"]!.GetValue<string>());
        var candidate = new JsonObject { ["nodes"] = new JsonObject { [node.Key] = new JsonObject { ["structuredOutput"] = null } } };
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        Assert.True(JsonNode.DeepEquals(candidate, TypedWorkflowPlanner.EmptyConstruction(schema)));
        var omitted = new JsonObject { ["nodes"] = new JsonObject() };
        Assert.Empty(PlanningConstruction.ShapeFindings(omitted, schema, unit));
        Assert.True(JsonNode.DeepEquals(candidate, PlanningConstruction.CompleteFixedFields(omitted, schema)));
        Assert.Empty(omitted["nodes"]!.AsObject());
        omitted["nodes"]![node.Key] = new JsonObject { ["structuredOutput"] = "invalid" };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(omitted, schema, unit));
        var result = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        Assert.Contains(PlanningDataflow.Index(result.Workflows[0], preparation, result, "greeting").Values, b => b.Value.Source == node.Key && b.Value.Path.SequenceEqual(new[] { "message" }));
        node.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "legacy", Required = true, Schema = new() { Type = "string" } }] });
        Assert.False(PlanningProducerContracts.UsesDeclaredObject(node, preparation));
    }

    [Theory]
    [InlineData("source", "consumer", false)]
    [InlineData("source_renommee", "consommateur", false)]
    [InlineData("source", "consumer", true)]
    public async Task ConsumedOpaqueResultsNeedAValidatedProducerContract(string read, string consume, bool malformed)
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject();
        var prep = Preparation();
        prep.Capabilities = [new() { Id = "source", Server = "neutral", Method = read, Kind = "tool", StepType = "mcp.call", InputSchema = schema, OperationIds = ["read"] },
            new() { Id = "consumer", Server = "neutral", Method = consume, Kind = "tool", StepType = "mcp.call", InputSchema = schema, OperationIds = ["use"], InputOperationIds = ["read"] }];
        var producer = new PlanningNode { Key = read, Type = "mcp.call", CapabilityId = "source", OperationIds = ["read"], Input = Obj(("request", Obj(("value", new() { Kind = "input", Source = "value" })))) };
        var consumer = new PlanningNode { Key = consume, Type = "mcp.call", CapabilityId = "consumer", OperationIds = ["use"], Input = Obj(("request", Obj(("value", new() { Kind = "output", Source = read, ResultChannel = "structured", Path = ["value"] })))) };
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "value", Required = true, Schema = new() { Type = "string" } }], Steps = [producer, consumer] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        Assert.Contains(PlanningExecutableValidation.Validate(graph, prep), d => d.Code == "OPAQUE_PRODUCER_CONTRACT_REQUIRED" && d.Location == "/workflows/0/steps/0/structuredOutput");
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "contracts", NodeKeys = [read], ContractVersion = PlanningDataflow.ContractVersion };
        var responseSchema = PlanningConstruction.Schema(workflow, unit, prep, graph);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(PlanningConstruction.Values(workflow, unit), responseSchema, unit));
        producer.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "value", Required = true, Schema = new() { Type = "string" } }] });
        Assert.Empty(PlanningConstruction.ShapeFindings(PlanningConstruction.Values(workflow, unit), responseSchema, unit));
        var bindings = PlanningDataflow.CompactIndex(workflow, prep, graph, consume).Values.Where(b => b.Value.Source == read).ToArray();
        Assert.Contains(bindings, b => b.Value.ResultChannel == "structured" && b.Value.Path.SequenceEqual(["value"]));
        Assert.DoesNotContain(bindings, b => b.Availability == "opaque");
        // The general catalog retains whole raw-result serialization for advanced consumers.
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, consume).Values, b => b.Value.Source == read && b.Availability == "opaque");
        var calls = 0; string? received = null;
        var factory = new InMemoryMcpClientFactory(); factory.RegisterServer("neutral", new() { Tools = [new() { Name = read, InputSchema = schema }, new() { Name = consume, InputSchema = schema }], ToolHandlers = new()
        { [read] = args => new() { Content = JsonValue.Create("opaque " + args!["value"]!.GetValue<string>()) },
          [consume] = args => { calls++; received = args!["value"]!.GetValue<string>(); return new() { Content = JsonValue.Create("done") }; } } });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var engine = new WorkflowEngine { McpClientFactory = factory, LlmDefaults = new() { Model = "fake" }, LLMClient = new StructuredClient(malformed) };
        var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["value"] = "original" }, TestContext.Current.CancellationToken);
        Assert.Equal(!malformed, result.Success);
        Assert.Equal(malformed ? 0 : 1, calls);
        if (!malformed) Assert.Equal("validated", received);
    }

    private sealed class StructuredClient(bool malformed) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Assert.Contains("opaque original", request.Prompt);
            var value = malformed ? new JsonObject() : new JsonObject { ["value"] = "validated" };
            return Task.FromResult(new LLMResponse { Json = value, Text = value.ToJsonString() });
        }
    }
}
