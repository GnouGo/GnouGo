using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskJsonEncodingTests
{
    private static TaskPlan Plan() => new()
    {
        Inputs = [new() { Name = "value", Type = new() { Kind = "any" } }],
        Root = new() { Outputs = [new("encoded", new() { Kind = "json", Items = [new() { Kind = "input", Source = "value" }] })] }
    };

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("123.5")]
    [InlineData("\"a\\\"b\\\\c\\n東京 {{value}} ${data.secret}\"")]
    [InlineData("[]")]
    [InlineData("{\"path\":\"a\\\"b\",\"nested\":[null,false,1,{\"unicode\":\"é\"}]}")]
    public async Task EncodesWholeValuesWithoutInferenceOrEvaluation(string json)
    {
        var input = JsonNode.Parse(json);
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("observations", new() { Tools = [new() { Name = "read", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object" },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"payload":{}},"required":["payload"]}""") }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = new JsonObject { ["payload"] = input?.DeepClone() } } } });
        var engine = new WorkflowEngine { McpClientFactory = factory }; // No LLM client or external transport exists.
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime); catalog.Policy.RequireExternalConfirmation = false;
        var plan = Plan(); plan.Inputs.Clear();
        plan.Root.Tasks.Add(new() { Id = "observe", Objective = "Read observed data", Operation = catalog.Capabilities.Single(c => c.Method == "read").Id });
        plan.Root.Outputs[0].Value.Items[0] = new() { Kind = "output", Source = "observe", Port = "payload" };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph!, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(result.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(result.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var execution = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.True(execution.Success, execution.Error?.Message);
        var text = execution.Outputs!["encoded"]!.GetValue<string>();
        Assert.True(JsonNode.DeepEquals(input, JsonNode.Parse(text)), text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task WrongArityAndDeniedAssemblyNeverEmitGraphs(int operands)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = Plan(); plan.Root.Outputs[0].Value.Items = Enumerable.Range(0, operands).Select(_ => new TaskValue()).ToList();
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_JSON_ARITY");
        catalog.AllowedStepTypes.Remove("set");
        result = new TaskPlanCompiler().Compile(Plan(), catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_JSON_POLICY");
    }

    [Theory]
    [InlineData("json(data.steps.value.answer)", true)]
    [InlineData("json(other())", false)]
    [InlineData("json(data.steps.value.answer, data.inputs.extra)", false)]
    [InlineData("JSON.stringify(data.inputs.value)", false)]
    [InlineData("json({path:data.inputs.value})", false)]
    public void GeneratedEncodingAllowsOnlyTheFixedReferenceCall(string expression, bool accepted)
    {
        var graph = PlannerFixture.Greeting(); graph.Workflows[0].Steps[0].Input.Members[0].Value.Kind = "expression";
        graph.Workflows[0].Steps[0].Input.Members[0].Value.Text = expression;
        Assert.Equal(accepted, !PlanningGeneratedGraph.Validate(graph, new()).Any(d => d.Code == "GENERATED_EXPRESSION_DENIED"));
    }

    [Fact]
    public async Task EncodingDoesNotGrantTypedAccessToOpaqueFields()
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "opaque", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read", OutputSchema = new() });
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "producer", Objective = "Read opaque value", Operation = "opaque" }],
            Outputs = [new("encoded", new() { Kind = "json", Items = [new() { Kind = "output", Source = "producer", Port = "invented" }] })] } };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_OUTPUT_UNKNOWN");
    }
}
