using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContractClosureTests
{
    [Theory]
    [InlineData("alpha", "exact", true)]
    [InlineData("omega", "exact", true)]
    [InlineData("alpha", "different", false)]
    public async Task WholeValueChecksAcceptComputedBindingsAndValidateBeforeConsumption(string name, string observed, bool succeeds)
    {
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new()
        {
            Tools = [new() { Name = name, InputSchema = ObjectSchema(("payload", new() { ["type"] = "string", ["pattern"] = "^\"exact\"$" })), OutputSchema = ObjectSchema() }],
            ToolHandlers = new() { [name] = input => { calls++; Assert.Equal("\"exact\"", input!["payload"]!.GetValue<string>()); return new() { Content = new JsonObject() }; } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human() };
        var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        var plan = new TaskPlan { Inputs = [new() { Name = "observed", Type = new() }], Root = new() { Tasks = [new()
        {
            Id = "consume", Operation = catalog.Capabilities.Single(c => c.Method == name).Id, Objective = "Consume the encoded observation",
            Inputs = [new("payload", new() { Kind = "json", Items = [new() { Kind = "input", Source = "observed" }] })]
        }] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var graph = compilation.Graph!; PlanningConfirmationGuards.Apply(graph, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var fingerprint = PlanningGraphCompiler.Fingerprint(graph);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
        Assert.Equal(fingerprint, PlanningGraphCompiler.Fingerprint(graph));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["observed"] = observed }, PlannerFixture.Ct);
        Assert.Equal(succeeds, result.Success);
        Assert.Equal(succeeds ? 1 : 0, calls);
        if (!succeeds) Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
    }

    [Theory, MemberData(nameof(TaskPlanCompilerTests.Scenarios), MemberType = typeof(TaskPlanCompilerTests))]
    public async Task BusinessGraphsContainNoMappingScriptsAndLowerWithoutMutation(string name)
    {
        var sample = new PlanningBenchmarkCases.Environment(name);
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = sample.Factory() }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var compilation = new TaskPlanCompiler().Compile(PlanningCorpus.Tasks(name, catalog), catalog);
        Assert.Empty(compilation.Diagnostics);
        var graph = compilation.Graph!; PlanningConfirmationGuards.Apply(graph, catalog);
        foreach (var workflow in graph.Workflows)
        {
            Assert.Null(workflow.Functions);
            var values = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SelectMany(PlanningGraphTopology.Values)
                .Concat(workflow.Outputs.Select(o => o.Value));
            Assert.DoesNotContain(values.SelectMany(Descendants), v => v.Kind is "expression" or "template");
        }
        var original = PlanningGraphCompiler.Fingerprint(graph);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        _ = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Equal(original, PlanningGraphCompiler.Fingerprint(graph));
    }

    [Theory]
    [InlineData("alpha", "{\"label\":\"exact\"}", true)]
    [InlineData("omega", "{\"label\":\"exact\"}", true)]
    [InlineData("alpha", "{}", false)]
    [InlineData("omega", "{\"label\":null}", false)]
    [InlineData("alpha", "\"not structured\"", false)]
    [InlineData("omega", "[{\"type\":\"text\",\"text\":\"exact\"}]", false)]
    public async Task OpaqueResultsUseDeferredChecksAndCannotInventMissingData(string operation, string payload, bool succeeds)
    {
        var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
        var expected = Shape();
        factory.RegisterServer("arbitrary", new()
        {
            Tools = [new() { Name = operation, InputSchema = ObjectSchema(), OutputSchema = null },
                new() { Name = operation + "_consume", InputSchema = ObjectSchema(("payload", expected)), OutputSchema = ObjectSchema() }],
            ToolHandlers = new()
            {
                [operation] = _ => { calls.Add("produce"); return new() { Content = JsonNode.Parse(payload) }; },
                [operation + "_consume"] = input => { Assert.Equal("exact", input!["payload"]!["label"]!.ToString()); calls.Add("consume"); return new() { Content = new JsonObject() }; }
            }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(), LLMClient = new IdentityModel(), LlmDefaults = new() { Model = "test" } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var plan = Pipeline(catalog.Capabilities.Single(c => c.Method == operation).Id, catalog.Capabilities.Single(c => c.Method == operation + "_consume").Id);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var graph = compilation.Graph!;
        Assert.Single(graph.Workflows[0].Steps, n => n.Type == "set" && n.Input.Kind == "dynamic_mapping");
        Assert.DoesNotContain(graph.Workflows.SelectMany(w => w.Steps), n => n.Type == "llm.call");
        PlanningConfirmationGuards.Apply(graph, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(succeeds, result.Success); Assert.Equal(succeeds ? new[] { "produce", "consume" } : ["produce"], calls);
        if (!succeeds) Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code);
        Assert.Empty(catalog.Capabilities.Single(c => c.Method == operation).OutputSchema);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultsAreCopiedOnlyFromContractsAndNeverReplaceNull(bool nested)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var schema = new JsonObject { ["type"] = "string", ["default"] = "declared" };
        var contract = ObjectSchema(("setting", nested ? ObjectSchema(("name", schema)) : schema));
        catalog.Capabilities.Add(new() { Id = "consume", StepType = "mcp.call", Server = "test", Method = "consume", Kind = "tool", InputSchema = contract, OutputSchema = ObjectSchema(), EffectKind = "read" });
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "consume", Objective = "Use declared settings", Operation = "consume",
            Inputs = nested ? [new("setting", new() { Kind = "object" })] : [] }] } };
        var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        Assert.Contains("declared", new PlanningGraphCompiler().Compile(compiled.Graph!, catalog));
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        plan.Root.Tasks[0].Inputs = [new("setting", nested ? new() { Kind = "object", Members = [new("name", new())] } : new())];
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_INPUT_TYPE");
        // The operation owns a cloned contract, so change the actual published default.
        var leaf = nested ? contract["properties"]!["setting"]!["properties"]!["name"]! : contract["properties"]!["setting"]!;
        leaf["default"] = 17;
        plan.Root.Tasks[0].Inputs = nested ? [new("setting", new() { Kind = "object" })] : [];
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "CATALOG_DEFAULT_INVALID");
    }

    [Fact]
    public async Task ImpossibleInputStopsWithContractDiagnosticAndPreservesRepairLocation()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "consume", StepType = "mcp.call", Server = "test", Method = "consume", Kind = "registered", InputSchema = ObjectSchema(("payload", Shape())), OutputSchema = ObjectSchema(), EffectKind = "read" });
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "consume", Operation = "consume", Objective = "Consume required data" }] } };
        runtime.Proposal = new() { Plan = plan, Requirements = new() { Summary = "Consume required data", Inputs = [], Outcomes = [new("result", "Consume required data")] } };
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Request.MaxReplanAttempts = 0;
        var result = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Null(result.Yaml);
        Assert.Contains(result.Diagnostics, d => d.Code == "CONTRACT_UNSATISFIED" && d.Location == "/tasks/consume/inputs/payload");
        Assert.Single(runtime.Calls); Assert.Equal(0, result.ReplanAttempts);
    }

    [Fact]
    public async Task ClosureRejectsNestedCallArgumentsAndOutputTypesBeforeLowering()
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var graph = new PlanningGraph { Workflows = [new() { Steps = [new() { Key = "call", Type = "workflow.call", Input = PlanningCorpus.Obj(
            ("ref", new() { Kind = "workflow", Source = "child" }), ("args", PlanningCorpus.Obj())) }] },
            new() { Key = "child", Inputs = [new() { Name = "required", Schema = new() { Type = "string" } }], Steps = [new() { Key = "value", Type = "set", Input = PlanningCorpus.Obj() }],
                Outputs = [new() { Name = "wrong", Schema = new() { Type = "number" }, Value = PlanningCorpus.Text("invalid") }] }] };
        var errors = PlanningExecutableValidation.Validate(graph, catalog);
        Assert.Contains(errors, d => d.Code == "CONTRACT_UNSATISFIED" && d.Location.EndsWith("/args", StringComparison.Ordinal));
        Assert.Contains(errors, d => d.Code == "OUTPUT_TYPE_MISMATCH");
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, catalog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteRequestsRetainLiteralArrayConstraintsBesideDynamicBindings(bool duplicate)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "arbitrary", StepType = "mcp.call", Kind = "tool", Server = "test", Method = "act", EffectKind = "read",
            InputSchema = ObjectSchema(("dynamic", new() { ["type"] = "string" }), ("options", new()
                { ["type"] = "array", ["minItems"] = 2, ["uniqueItems"] = true, ["items"] = new JsonObject { ["type"] = "string" } })), OutputSchema = ObjectSchema() });
        var graph = new PlanningGraph { Workflows = [new() { Inputs = [new() { Name = "input", Schema = new() { Type = "string" } }], Steps = [new()
        { Key = "call", Type = "mcp.call", CapabilityId = "arbitrary", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj(
            ("dynamic", new() { Kind = "input", Source = "input" }), ("options", new() { Kind = "array", Items = [PlanningCorpus.Text("a"), PlanningCorpus.Text(duplicate ? "a" : "b")] })))) }] }] };
        var errors = PlanningExecutableValidation.Validate(graph, catalog);
        if (duplicate) Assert.Contains(errors, d => d.Code == "CONTRACT_UNSATISFIED");
        else Assert.Empty(errors);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"optional\":\"provided\"}", true)]
    [InlineData("{\"optional\":null}", false)]
    public async Task ConfirmationForwardsOptionalInputsWithoutManufacturingValues(string supplied, bool succeeds)
    {
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        var contract = ObjectSchema(("optional", new() { ["type"] = "string" })); contract["required"] = new JsonArray();
        factory.RegisterServer("arbitrary", new()
        {
            Tools = [new() { Name = "write", InputSchema = contract, OutputSchema = ObjectSchema() }],
            ToolHandlers = new() { ["write"] = input => { calls++; Assert.True(JsonNode.DeepEquals(JsonNode.Parse(supplied), input)); return new() { Content = new JsonObject() }; } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(), LLMClient = new IdentityModel(), LlmDefaults = new() { Model = "test" } };
        var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        var graph = new PlanningGraph { Workflows = [new() { Inputs = [new() { Name = "optional", Required = false, Schema = new() { Type = "string" } }], Steps = [new()
        { Key = "write", Type = "mcp.call", CapabilityId = catalog.Capabilities.Single(c => c.Method == "write").Id,
            Input = PlanningCorpus.Obj(("request", new() { Kind = "input" })) }] }] };
        PlanningConfirmationGuards.Apply(graph, catalog);
        Assert.Equal(2, graph.Workflows.Count);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
        if (succeeds)
            Assert.True((await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], JsonNode.Parse(supplied), PlannerFixture.Ct)).Success);
        else
            Assert.Equal(ErrorCodes.InputValidation, (await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(
                () => engine.ExecuteAsync(document.Workflows[document.Entrypoint!], JsonNode.Parse(supplied), PlannerFixture.Ct))).Code);
        Assert.Equal(succeeds ? 1 : 0, calls);
    }

    private static TaskPlan Pipeline(string producer, string consumer) => new() { Root = new() { Tasks = [
        new() { Id = "observe", Operation = producer, Objective = "Observe data" },
        new() { Id = "consume", Operation = consumer, Objective = "Consume checked data", Inputs = [new("payload", new() { Kind = "output", Source = "observe" })] }] } };
    private static JsonObject Shape() => ObjectSchema(("label", new() { ["type"] = "string" }));
    private static JsonObject ObjectSchema(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema.DeepClone()))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false
    };
    private static IEnumerable<PlanningValue> Descendants(PlanningValue value) => new[] { value }.Concat(value.Members.SelectMany(m => Descendants(m.Value))).Concat(value.Items.SelectMany(Descendants));
    private sealed class IdentityModel : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => Task.FromResult(new LLMResponse
            { Json = new JsonObject { ["script"] = "source.value" } });
    }
}
