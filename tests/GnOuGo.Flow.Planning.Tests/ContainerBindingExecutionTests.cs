using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContainerBindingExecutionTests
{
    [Theory]
    [InlineData("no_action")]
    [InlineData("aucune_action")]
    public async Task EmptyReviewedSequenceCompilesToANativeNoOpWithTheSameEmptyResult(string key)
    {
        var prep = Preparation(); var graph = Graph(); var workflow = graph.Workflows[0];
        var empty = new PlanningNode { Key = key, Type = "sequence" };
        workflow.Steps.Insert(0, empty);
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "compute", Text = "Object.keys(result).length === 0 ? 'empty' : 'unexpected'", Members = [new("result", new() { Kind = "output", Source = key })] }));
        workflow.Steps[1].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = new() { Type = "string" } }] };
        var fingerprint = PlanningGraphCompiler.Fingerprint(graph);
        var yaml = new PlanningGraphCompiler().Compile(graph, prep);
        var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml);
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(document);
        Assert.Equal("set", document.Workflows["main"].Steps[0].Type); Assert.NotNull(document.Workflows["main"].Steps[0].Input);
        Assert.Empty(document.Workflows["main"].Steps[0].Input!.AsObject());
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success); Assert.Equal("empty", result.Outputs!["message"]!.GetValue<string>());
        Assert.Equal(fingerprint, PlanningGraphCompiler.Fingerprint(graph)); Assert.Equal("sequence", empty.Type);
        prep.AllowedStepTypes.Remove("set"); Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, prep));
    }

    [Fact]
    public async Task WholePreviousIterationUsesLogicalNamesAfterTheFirstNullObservation()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        var child = new PlanningNode { Key = "read", Input = Obj(("value", new() { Kind = "compute", Text = "previous == null ? 'first' : previous.read.value + ':next'",
            Members = [new("previous", new() { Kind = "loop_previous", Source = "loop" })] })),
            OutputSchema = new() { Type = "object", Properties = [new() { Name = "value", Required = true, Schema = new() { Type = "string" } }] } };
        workflow.Steps.Insert(0, new() { Key = "loop", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "array", Items = [Str("one"), Str("two")] })), Steps = [child] });
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "output", Source = "loop", Path = ["results", "1", "read", "value"] }));
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("first:next", result.Outputs!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task NestedProjectionPreservesRawAndStructuredEnvelopesWithoutRenamingToolFields()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        var collision = "n_" + PlanningGraphCompiler.Fingerprint("read")[..16];
        var output = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["location"] = new JsonObject { ["type"] = "string" },
            [collision] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("location", collision) };
        preparation.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", Server = "renamed", Method = "observe", Kind = "tool", InputSchema = new() { ["type"] = "object" }, OutputSchema = output });
        var read = new PlanningNode { Key = "read", Type = "mcp.call", CapabilityId = "source", Input = Obj(("request", Obj())), StructuredOutput = new(new()
            { Type = "object", Properties = [new() { Name = "summary", Required = true, Schema = new() { Type = "string" } }] }) };
        var inner = new PlanningNode { Key = "inner", Type = "sequence", Steps = [read] };
        workflow.Steps.Insert(0, new() { Key = "group", Type = "parallel", Branches = [new([inner])] });
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "compute", Text = "value.inner.read.response.location + ':' + value.inner.read.json.summary + ':' + value.inner.read.response." + collision,
            Members = [new("value", new() { Kind = "output", Source = "group", Path = ["branches", "0"] })] }));
        workflow.Steps[1].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = new() { Type = "string" } }] };
        var factory = new InMemoryMcpClientFactory(); factory.RegisterServer("renamed", new() { Tools = [new() { Name = "observe", InputSchema = preparation.Capabilities[0].InputSchema, OutputSchema = output }],
            ToolHandlers = new() { ["observe"] = _ => new McpCallResult { Content = new JsonObject { ["location"] = "original", [collision] = "unchanged" } } } });
        Assert.Empty(PlanningGraphValidation.Validate(graph, preparation));
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { McpClientFactory = factory, LLMClient = new StructuredClient(), LlmDefaults = new() { Model = "fixture" } }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("original:derived:unchanged", result.Outputs!["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionKeepsSkippedChildrenAbsentAndNoOutcomeNull(bool noOutcome)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        var child = new PlanningNode { Key = "skipped", If = new() { Kind = "boolean", Boolean = false }, Input = Obj(("value", Str("must not appear"))) };
        workflow.Inputs.Add(new() { Name = "choice", Required = true, Schema = new() { Type = "string" } });
        workflow.Steps.Insert(0, noOutcome ? new() { Key = "group", Type = "switch", Expr = new() { Kind = "input", Source = "choice" }, Cases = [new("selected", null, [child])] }
            : new() { Key = "group", Type = "sequence", Steps = [child] });
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "compute", Text = noOutcome ? "value === null ? 'absent' : 'invented'" : "Object.keys(value).length === 0 ? 'absent' : 'invented'",
            Members = [new("value", new() { Kind = "output", Source = "group" })] }));
        workflow.Steps[1].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = new() { Type = "string" } }] };
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["choice"] = "missing" }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("absent", result.Outputs!["message"]!.GetValue<string>());
    }

    private sealed class StructuredClient : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => Task.FromResult(new LLMResponse { Json = new JsonObject { ["summary"] = "derived" } });
    }

    [Theory]
    [InlineData("sequence", "reader")]
    [InlineData("parallel", "lecteur")]
    [InlineData("switch", "selected")]
    [InlineData("loop.sequential", "item_result")]
    public async Task WholeContainerParametersUseTheirDeclaredLogicalFieldNames(string type, string childKey)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        var child = new PlanningNode { Key = childKey, Input = Obj(("value", Str("observed"))) };
        var container = new PlanningNode { Key = "container", Type = type };
        string path;
        switch (type)
        {
            case "parallel": container.Branches = [new([child])]; path = "result.branches[0]"; break;
            case "switch": container.Expr = Str("selected"); container.Cases = [new("selected", null, [child])]; path = "result"; break;
            case "loop.sequential": container.Input = Obj(("items", new() { Kind = "array", Items = [Str("one")] })); container.Steps = [child]; path = "result.results[0]"; break;
            default: container.Steps = [child]; path = "result"; break;
        }
        workflow.Steps.Insert(0, container);
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "compute", Text = path + "." + childKey + ".value",
            Members = [new("result", new() { Kind = "output", Source = "container" })] }));
        workflow.Steps[1].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = new() { Type = "string" } }] };
        Assert.Empty(PlanningGraphValidation.Validate(graph, preparation));
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("observed", result.Outputs!["message"]!.GetValue<string>());
    }
}
