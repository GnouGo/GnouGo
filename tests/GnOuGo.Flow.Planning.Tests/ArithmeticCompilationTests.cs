using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArithmeticCompilationTests
{
    [Theory]
    [InlineData("add", 0.1, 0.2, 0.30000000000000004)]
    [InlineData("subtract", 7, 3, 4)]
    [InlineData("multiply", 1e100, 1e100, 1e200)]
    [InlineData("divide", 7, 2, 3.5)]
    [InlineData("remainder", -7, 3, -1)]
    [InlineData("negate", 0.25, 0, -0.25)]
    [InlineData("multiply", 1e-200, 1e-100, 1e-300)]
    public async Task FormulasRemainStructuredUntilLoweringAndExecuteAsJavaScript(string operation, double left, double right, double expected)
    {
        var formula = Formula(operation, operation == "negate" ? [Number(left)] : [Number(left), Number(right)]);
        var catalog = await Catalog(); var plan = Plan(formula);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        var graph = compilation.Graph!;
        var saved = JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph);
        Assert.Contains("arithmetic", saved); Assert.DoesNotContain("expression", saved);
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Equal(saved, JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph));
        Assert.DoesNotContain("number.", yaml); Assert.DoesNotContain("llm.call", yaml); Assert.DoesNotContain("mapping.dynamic", yaml);
        var result = await Execute(yaml);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, double.Parse(result.Outputs!["result"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task NestedBusinessFormulaUsesOnePlanningCallAndNoRepair()
    {
        var runtime = new TestRuntime();
        runtime.Proposal = new() { Requirements = new() { Summary = "Compute (2 + 3) * 4", Inputs = [],
            Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }], Outcomes = [new("result", "Return (2 + 3) * 4")] },
            Plan = Plan(Formula("multiply", Formula("add", Number(2), Number(3)), Number(4))) };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        var result = await Execute(state.Yaml!);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("20", result.Outputs!["result"]!.ToJsonString());
    }

    [Theory]
    [InlineData("add", "null")]
    [InlineData("multiply", "string")]
    [InlineData("power", "number")]
    public async Task InvalidOperandsAndOperatorsFailBeforeLowering(string operation, string kind)
    {
        var result = new TaskPlanCompiler().Compile(Plan(Formula(operation, new() { Kind = kind, Text = kind == "string" ? "2" : null, Number = kind == "number" ? 2 : null }, Number(3))), await Catalog());
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_ARITHMETIC_INVALID");
    }

    [Theory]
    [InlineData("divide", 1, 0)]
    [InlineData("divide", 0, 0)]
    [InlineData("multiply", 1e308, 1e308)]
    public async Task NonfiniteResultsFailCleanly(string operation, double left, double right)
    {
        var catalog = await Catalog(); var compilation = new TaskPlanCompiler().Compile(Plan(Formula(operation, Number(left), Number(right))), catalog);
        Assert.Empty(compilation.Diagnostics);
        var result = await Execute(new PlanningGraphCompiler().Compile(compilation.Graph!, catalog));
        Assert.False(result.Success); Assert.Equal(ErrorCodes.EvalError, result.Error!.Code);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonfiniteLiteralApisFailContractValidation(double value)
    {
        var result = new TaskPlanCompiler().Compile(Plan(Number(value)), await Catalog());
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID");
    }

    [Theory]
    [InlineData("number", true)]
    [InlineData("string", false)]
    public async Task ReferencedOperandsRequireNumericNonnullableContracts(string type, bool nullable)
    {
        var plan = Plan(Formula("add", new() { Kind = "input", Source = "amount" }, Number(1)));
        plan.Inputs = [new() { Name = "amount", Type = new() { Kind = type, Nullable = nullable } }];
        var result = new TaskPlanCompiler().Compile(plan, await Catalog());
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_ARITHMETIC_INVALID");
    }

    [Fact]
    public async Task RepeatedInputReferencesRemainTypedAndAvailable()
    {
        var plan = Plan(Formula("add", new() { Kind = "input", Source = "amount" }, new() { Kind = "input", Source = "amount" }));
        plan.Inputs = [new() { Name = "amount", Type = new() { Kind = "number" }, Required = false, Default = Number(0.25) }];
        var catalog = await Catalog(); var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        var result = await Execute(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog));
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(0.5, result.Outputs!["result"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("\"4\"")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task MisreportedProducerNumbersCannotBeImplicitlyCoerced(string observed)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("producer", new() { Tools = [new() { Name = "measure", InputSchema = new JsonObject { ["type"] = "object" },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"amount":{"type":"number"}},"required":["amount"]}""") }],
            ToolHandlers = new() { ["measure"] = _ => new() { Content = new JsonObject { ["amount"] = JsonNode.Parse(observed) } } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "measure")).Id;
        var plan = Plan(Formula("multiply", PlanningCorpus.Business("output", "observe", "amount"), Number(2)));
        plan.Root.Tasks.Insert(0, new() { Id = "observe", Objective = "Read the observed amount", Operation = operation });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        engine.HumanInputProvider = new PlanningCorpus.Human();
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.False(result.Success); Assert.Equal(ErrorCodes.EvalError, result.Error!.Code);
    }

    [Theory]
    [InlineData("0.1234567890123456789012345678")]
    [InlineData("1.00")]
    [InlineData("1e20")]
    public void HistoricalNumberTokensSurviveReadingWithoutChangingSerializedHashes(string token)
    {
        var task = JsonSerializer.Deserialize("{\"kind\":\"number\",\"number\":" + token + "}", PlanningJsonContext.Default.TaskValue)!;
        var graph = JsonSerializer.Deserialize("{\"kind\":\"number\",\"number\":" + token + "}", PlanningJsonContext.Default.PlanningValue)!;
        Assert.Equal(token, JsonSerializer.SerializeToNode(task, PlanningJsonContext.Default.TaskValue)!["number"]!.ToJsonString());
        Assert.Equal(token, JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningValue)!["number"]!.ToJsonString());
        Assert.True(double.IsFinite(task.Number!.Value)); Assert.Equal(task.Number, graph.Number);
    }

    private static TaskValue Number(double value) => new() { Kind = "number", Number = value };
    private static TaskValue Formula(string operation, params TaskValue[] operands) => new() { Kind = "arithmetic", Text = operation, Items = operands.ToList() };
    private static TaskPlan Plan(TaskValue value) => new() { Root = new() { Tasks = [new() { Id = "calculate", Kind = "value", Objective = "Calculate the requested value", Outputs = [new("value", value)] }], Outputs = [new("result", PlanningCorpus.Business("output", "calculate", "value"))] } };
    private static Task<PlanningCatalog> Catalog() => new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
    private static Task<RunResult> Execute(string yaml)
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
    }
}
