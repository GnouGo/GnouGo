using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LoopBindingTests
{
    [Theory]
    [InlineData("pages", "observe", 0)]
    [InlineData("étapes", "lire", 7)]
    public async Task SequentialContinuationUsesThePreviousTypedResultAndDynamicInput(string loopKey, string tool, int start)
    {
        var preparation = Preparation();
        var inputSchema = JsonNode.Parse("""{"type":"object","properties":{"cursor":{"type":"integer"}},"required":["cursor"]}""")!.AsObject();
        var outputSchema = JsonNode.Parse("""{"type":"object","properties":{"next":{"type":"integer"},"more":{"type":"boolean"}},"required":["next","more"]}""")!.AsObject();
        preparation.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed", Method = tool, Kind = "tool", InputSchema = inputSchema, OutputSchema = outputSchema });
        PlanningValue Previous() => new() { Kind = "loop_previous", Source = loopKey, Path = ["fetch", "response"] };
        var loop = new PlanningNode
        {
            Key = loopKey,
            Type = "loop.sequential",
            Input = Obj(("while", new()
            { Kind = "compute", Text = "previous == null || previous.more === true", Members = [new("previous", Previous())] }), ("max_times", new() { Kind = "number", Number = 4 })),
            Steps =
            [new() { Key = "fetch", Type = "mcp.call", CapabilityId = "cap", Input = Obj(("request", Obj(("cursor", new()
                { Kind = "compute", Text = "previous == null ? start : previous.next", Members = [new("previous", Previous()), new("start", new() { Kind = "input", Source = "start" })] })))) }]
        };
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "start", Required = true, Schema = new() { Type = "integer" } }], Steps = [loop] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        Assert.Contains(PlanningDataflow.Index(workflow, preparation, graph, loopKey).Values, b => b.Value.Kind == "loop_previous" && b.Value.Path.SequenceEqual(new[] { "fetch", "response" }) && b.Availability == "nullable");
        Assert.Empty(PlanningExecutableValidation.Validate(graph, preparation));
        var observed = new List<int>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new()
        {
            Tools = [new() { Name = tool, InputSchema = inputSchema, OutputSchema = outputSchema }],
            ToolHandlers = new()
            {
                [tool] = args =>
        { var cursor = args!["cursor"]!.GetValue<int>(); observed.Add(cursor); return new() { Content = new JsonObject { ["next"] = cursor + 1, ["more"] = cursor == start } }; }
            }
        });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["start"] = start }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(new[] { start, start + 1 }, observed);
        workflow.Steps.Add(new() { Key = "outside", Input = Obj(("invalid", Previous())) });
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "LOOP_BINDING_SCOPE_INVALID");
    }

    [Fact]
    public async Task FailureEnvelopeCanRetainTheCurrentLoopItemWithoutACyclicResultContract()
    {
        var prep = Preparation(); prep.AllowedStepTypes.Add("loop.sequential");
        var schema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject();
        prep.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed", Method = "observe", Kind = "tool", InputSchema = schema, OutputSchema = schema });
        var loop = new PlanningNode
        {
            Key = "each",
            Type = "loop.sequential",
            Input = Obj(("items", new() { Kind = "array", Items = [Str("retained item")] })),
            Steps =
        [new() { Key = "observe", Type = "mcp.call", CapabilityId = "cap", Input = Obj(("request", Obj())), OnError =
            [new(null, "continue", Obj(("item", new() { Kind = "loop_item", Source = "each" }), ("success", new() { Kind = "boolean", Boolean = false })), null)] }]
        };
        var workflow = new PlanningWorkflow
        {
            Key = "main",
            Steps = [loop],
            Outputs =
            [new() { Name = "result", Schema = new() { Type = "string" }, Value = new() { Kind = "compute", Text = "JSON.stringify(result)", Members = [new("result", new() { Kind = "output", Source = "each" })] } }]
        };
        var graph = new PlanningGraph { Workflows = [workflow] };
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, PlanningDataflow.WorkflowOutputs).Values, b => b.Value.Source == "each" && b.Value.Path.Count == 0);
        var factory = new InMemoryMcpClientFactory(); factory.RegisterServer("renamed", new() { Tools = [new() { Name = "observe", InputSchema = schema, OutputSchema = schema }], ToolHandlers = new() { ["observe"] = _ => throw new InvalidOperationException("injected failure") } });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Contains("retained item", result.Outputs!["result"]!.GetValue<string>());
        loop.Input = Obj(("items", new() { Kind = "loop_item", Source = "each" }));
        Assert.Throws<InvalidOperationException>(() => PlanningGraphValidation.ResolveValueContract(graph, workflow, new() { Kind = "loop_item", Source = "each" }, prep));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompleteResultEnvelopePreservesSuccessAndFailure_IncludingLoopChildren(bool wrapped, bool failure)
    {
        var prep = Preparation(); prep.AllowedStepTypes.Add("loop.sequential");
        var input = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject();
        var output = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""")!.AsObject();
        prep.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed", Method = "observe", Kind = "tool", InputSchema = input, OutputSchema = output });
        var call = new PlanningNode
        {
            Key = "observe",
            Type = "mcp.call",
            CapabilityId = "cap",
            Input = Obj(("request", Obj())),
            OnError = [new(null, "continue", Obj(("status", Str("failure"))), null)]
        };
        var source = wrapped ? new PlanningValue { Kind = "output", Source = "each", Path = ["results"] } : new PlanningValue { Kind = "output", Source = "observe", ResultChannel = "envelope" };
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = "JSON.stringify(result)", Members = [new("result", source)] }));
        workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = new() { Type = "string" } }] };
        workflow.Steps.Insert(0, wrapped ? new() { Key = "each", Type = "loop.sequential", Input = Obj(("times", new() { Kind = "number", Number = 1 })), Steps = [call] } : call);
        var bindings = PlanningDataflow.Index(workflow, prep, graph, "greeting").Values;
        Assert.Contains(bindings, b => b.Value.Source == source.Source && b.Value.ResultChannel == source.ResultChannel && b.Value.Path.SequenceEqual(source.Path));
        Assert.DoesNotContain(bindings, b => b.Value.Source == "observe" && b.Value.ResultChannel is null or "default");
        var factory = new InMemoryMcpClientFactory(); factory.RegisterServer("renamed", new()
        {
            Tools = [new() { Name = "observe", InputSchema = input, OutputSchema = output }],
            ToolHandlers = new()
            { ["observe"] = _ => failure ? throw new InvalidOperationException("deterministic failure") : new() { Content = new JsonObject { ["value"] = "success" } } }
        });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(failure ? "failure" : "success", result.Outputs!["message"]!.GetValue<string>());
    }
}
