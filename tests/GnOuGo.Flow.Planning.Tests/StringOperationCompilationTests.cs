using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class StringOperationCompilationTests
{
    [Theory]
    [InlineData("", "https://catalog.example/find?q=")]
    [InlineData("tea & café/東京", "https://catalog.example/find?q=tea%20%26%20caf%C3%A9%2F%E6%9D%B1%E4%BA%AC")]
    [InlineData("${data.secret}`\\\"", "https://catalog.example/find?q=%24%7Bdata.secret%7D%60%5C%22")]
    [InlineData("!'()*-_.~", "https://catalog.example/find?q=!'()*-_.~")]
    public async Task ConsumerLocalUriAssemblyExecutesWithoutInference(string query, string expected)
    {
        var plan = Plan(Operation("concat", Text("https://catalog.example/find?q="), Operation("uri_component", Input())));
        var catalog = await Catalog();
        var request = new PlanningRequest { Options = new() { ["compilation_profile"] = TaskPlanCompiler.CompactProfile } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compilation.Graph!, catalog, compactBindings: true));
        var graph = JsonSerializer.Serialize(compilation.Graph, PlanningJsonContext.Default.PlanningGraph);
        Assert.Contains("string_operation", graph);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog, "strings", descriptions: true,
            fuseBindings: true, consumerBindings: true, directProjections: true, nativeMappings: true, readableMappings: true,
            leanMappings: true, compactProjections: true, inlineConsumers: true, directValues: true, checkedControls: true,
            readableBranches: true, simplifiedLocals: true);
        Assert.DoesNotContain("llm.call", yaml);
        Assert.DoesNotContain("mapping.dynamic", yaml);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["query"] = query }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, result.Outputs!["result"]!.GetValue<string>());
        Assert.Equal(graph, JsonSerializer.Serialize(compilation.Graph, PlanningJsonContext.Default.PlanningGraph));
    }

    [Theory]
    [InlineData("tea", false)]
    [InlineData(null, false)]
    [InlineData("tea", true)]
    [InlineData(null, true)]
    public async Task ARequiredNonNullOperandCanBeUsedInTheGuardedTask(string? query, bool producerOutput)
    {
        TaskValue Operand() => producerOutput ? new() { Kind = "output", Source = "read", Port = "name" } : Input();
        var requirement = new TaskValue { Kind = "predicate", Predicate = "and", Items = [
            new() { Kind = "boolean", Boolean = true },
            new() { Kind = "predicate", Predicate = "not_equal", Items = [Operand(), new() { Kind = "null" }] }] };
        var plan = Plan(new() { Kind = "output", Source = "assemble", Port = "text" });
        plan.Inputs[0].Type.Nullable = true;
        if (producerOutput) plan.Root.Tasks.Add(new() { Id = "read", Kind = "value", Objective = "Retain the observed name.",
            Outputs = [new("name", Input())] });
        plan.Root.Tasks.Add(new() { Id = "assemble", Kind = "value", Objective = "Use the required input.",
            Requires = requirement, Outputs = [new("text", Operation("concat", Text("prefix:"), Operand()))] });
        var catalog = await Catalog();
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var run = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["query"] = query }, PlannerFixture.Ct);
        Assert.Equal(query is not null, run.Success);
        if (query is not null) Assert.Equal("prefix:" + query, run.Outputs!["result"]!.ToString());

        plan.Root.Outputs = [new("result", Operation("concat", Text("outside:"), Operand()))];
        var outside = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Contains(outside.Diagnostics, d => d.Code == "TASK_STRING_OPERATION_INVALID" && d.Location == "/root/outputs/result");
    }

    [Theory]
    [InlineData("string", true)]
    [InlineData("number", false)]
    [InlineData("any", false)]
    public async Task NullableOrNonStringOperandsFailAtTheExactOutput(string kind, bool nullable)
    {
        var plan = Plan(Operation("uri_component", Input()));
        plan.Inputs[0].Type = new() { Kind = kind, Nullable = nullable };
        var compilation = new TaskPlanCompiler().Compile(plan, await Catalog());
        Assert.Null(compilation.Graph);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "TASK_STRING_OPERATION_INVALID" && diagnostic.Location == "/root/outputs/result");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("false")]
    public async Task MisreportedProducerStringsCannotBeCoerced(string observed)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source", new() { Tools = [new() { Name = "read", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object" },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""") }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = new JsonObject { ["text"] = JsonNode.Parse(observed) } } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        catalog.Policy.RequireExternalConfirmation = false;
        var plan = Plan(Operation("concat", Text("prefix"), new() { Kind = "output", Source = "observe", Port = "text" }));
        plan.Inputs.Clear();
        plan.Root.Tasks.Add(new() { Id = "observe", Objective = "Read the original text.", Operation = catalog.Capabilities.Single(capability => capability.Method == "read").Id });
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compilation.Graph!, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("concat", 1)]
    [InlineData("concat", 17)]
    [InlineData("uri_component", 2)]
    [InlineData("evaluate", 1)]
    public async Task UnsupportedOperatorsAndAritiesNeverReachExecution(string name, int count)
    {
        var result = new TaskPlanCompiler().Compile(Plan(Operation(name, Enumerable.Range(0, count).Select(_ => Text("value")).ToArray())), await Catalog());
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TASK_STRING_OPERATION_INVALID");
    }

    [Theory]
    [InlineData("string", true)]
    [InlineData("number", false)]
    public async Task SharedDraftAndRepairGrammarOfferOnlyStringResults(string kind, bool accepted)
    {
        var state = PlannerFixture.Session(); state.Catalog = await Catalog();
        state.Requirements = new() { Inputs = [], Outputs = [new() { Name = "result", Type = new() { Kind = kind } }], Outcomes = [new("result", "Return the requested value.")] };
        state.Plan = new() { Root = new() { Outputs = [new("result", Text("before"))] } };
        state.RevisionScope = ["/root/outputs/result"];
        var value = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(Operation("concat", Text("a"), Text("b")), PlanningJsonContext.Default.TaskValue));
        var schema = PlanningRepairPatch.Schema(state, PlanningRepairPatch.Template(state));
        var response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
            ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["slot"] = "s0", ["action"] = "replace", ["value"] = value }) } };
        Assert.Equal(accepted, PlanningContractValidation.ValidateInstance(response, schema).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
    }

    private static TaskValue Operation(string name, params TaskValue[] items) => new() { Kind = "string_operation", Text = name, Items = items.ToList() };
    private static TaskValue Text(string value) => new() { Kind = "string", Text = value };
    private static TaskValue Input() => new() { Kind = "input", Source = "query" };
    private static TaskPlan Plan(TaskValue value) => new() { Inputs = [new() { Name = "query", Type = new() { Kind = "string" } }], Root = new() { Outputs = [new("result", value)] } };
    private static Task<PlanningCatalog> Catalog() => new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
}
