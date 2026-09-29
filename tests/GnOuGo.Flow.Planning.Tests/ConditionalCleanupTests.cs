using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConditionalCleanupTests
{
    private static TaskValue Output(string source, string? port = null) => new() { Kind = "output", Source = source, Port = port };
    private static TaskValue Field(TaskValue source, string port) => new() { Kind = "field", Port = port, Items = [source] };
    private static TaskScope Export(string text) => new() { Outputs = [new("result", new() { Kind = "object", Members =
        [new("status", new() { Kind = "string", Text = text }), new("values", new() { Kind = "array", Items = [new() { Kind = "null" }] })] })] };
    private static TaskPlan Plan(string id = "finish") => new()
    {
        Inputs = [new() { Name = "selected", Type = new() { Kind = "boolean" } }],
        Root = new()
        {
            Tasks = [new() { Id = "prepare", Kind = "value", Objective = "Retain the selected condition",
                Outputs = [new("flag", new() { Kind = "input", Source = "selected" })] }],
            Always = [new() { Id = id, Kind = "conditional", Objective = "Export the explicit finalization outcome",
                Condition = Field(Output("prepare"), "flag"), Body = Export("performed"), Otherwise = Export("skipped") }],
            Outputs = [new("finalization", Output(id, "result"))]
        }
    };

    [Theory]
    [InlineData(true, "finish", "root")]
    [InlineData(false, "renamed_scope", "root")]
    [InlineData(true, "finish", "nested")]
    [InlineData(false, "renamed_scope", "nested")]
    [InlineData(true, "finish", "group")]
    [InlineData(false, "renamed_scope", "group")]
    [InlineData(true, "finish", "iteration")]
    [InlineData(false, "renamed_scope", "iteration")]
    public async Task ConditionalCleanupExportsTheSelectedComposedResult(bool selected, string id, string scope)
    {
        var engine = new WorkflowEngine();
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = Plan(id);
        if (scope != "root")
        {
            var inner = plan.Root;
            var wrapper = new PlanTask { Id = "container", Kind = "sequence", Objective = "Use the declared inner result", Body = inner };
            if (scope == "nested")
            { wrapper.Kind = "conditional"; wrapper.Condition = new() { Kind = "boolean", Boolean = true }; wrapper.Otherwise = new() { Outputs = [new("finalization", new() { Kind = "string", Text = "unselected" })] }; }
            if (scope == "group")
            { plan.Groups.Add(new() { Id = "reusable", Inputs = plan.Inputs, Body = inner }); wrapper.Kind = "call"; wrapper.Group = "reusable"; wrapper.Body = null; wrapper.Inputs = [new("selected", new() { Kind = "input", Source = "selected" })]; }
            if (scope == "iteration")
            { wrapper.Kind = "foreach"; wrapper.MaxItems = 2; wrapper.Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = 2 }] }; }
            plan.Root = new() { Tasks = [wrapper], Outputs = [new("finalization", Output("container", "finalization"))] };
        }
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(new TaskPlanCompiler().Compile(plan, catalog).Graph!, catalog));
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["selected"] = selected }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        JsonNode expected = new JsonObject { ["status"] = selected ? "performed" : "skipped", ["values"] = new JsonArray((JsonNode?)null) };
        if (scope == "iteration") expected = new JsonArray(expected.DeepClone(), expected.DeepClone());
        Assert.True(JsonNode.DeepEquals(expected, result.Outputs!["finalization"]));
    }

    [Fact]
    public void SwitchEnvelopeProofRequiresEveryBranchAndSafeProducerAvailability()
    {
        var producer = new PlanningNode { Key = "condition", Type = "set", Input = new() { Kind = "object" } };
        var branch = new PlanningNode { Key = "branch", Type = "switch", If = new() { Kind = "present", Source = "condition" },
            Cases = [new("true", null, [new() { Key = "yes" }])], Default = [new() { Key = "no" }] };
        var export = new PlanningNode { Key = "export", Type = "set", Input = new() { Kind = "object" }, If = new() { Kind = "present", Source = "branch" } };
        var workflow = new PlanningWorkflow { Steps = [producer], Finally = [branch, export] };
        Assert.True(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        branch.Default.Clear();
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        branch.Default.Add(new() { Key = "no" }); branch.Cases[0].Steps.Clear();
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        branch.Cases[0].Steps.Add(new() { Key = "yes" }); branch.OnError.Add(new(null, "continue", new() { Kind = "null" }, null));
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        branch.OnError.Clear(); producer.If = new() { Kind = "boolean", Boolean = false };
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        producer.If = null; branch.If!.Source = "export";
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
    }

    [Theory]
    [InlineData("true", "read")]
    [InlineData("false", "renamed_read")]
    [InlineData("absent", "read")]
    [InlineData("failure", "read")]
    [InlineData("cancelled", "renamed_read")]
    [InlineData("cleanup_failure", "read")]
    [InlineData("invalid", "read")]
    [InlineData("missing", "read")]
    [InlineData("both_fail", "read")]
    public async Task CleanupReadsCapturedValuesOnlyAfterItsProducerCompletes(string mode, string method)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        var effects = new List<string>(); var factory = new InMemoryMcpClientFactory();
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        var record = JsonNode.Parse("""{"type":"object","properties":{"flag":{"type":"boolean"},"label":{"type":"string"}},"required":["flag","label"],"additionalProperties":false}""")!;
        var argument = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false}""")!;
        factory.RegisterServer("independent_source", new() { Tools =
            [new() { Name = method, InputSchema = empty, OutputSchema = record, EffectKind = "none" },
             new() { Name = "work", InputSchema = empty, OutputSchema = empty, EffectKind = "none" },
             new() { Name = "finish", InputSchema = argument, OutputSchema = empty, EffectKind = "none" }], ToolHandlers = new()
            {
                [method] = _ => { effects.Add("read"); if (mode == "absent") throw new IOException("Producer failed");
                    var content = new JsonObject { ["flag"] = mode == "invalid" ? JsonValue.Create("invalid") : JsonValue.Create(mode != "false"), ["label"] = "the intended resource" };
                    if (mode == "missing") content.Remove("flag"); return new() { Content = content }; },
                ["work"] = _ => { effects.Add("work"); if (mode is "failure" or "both_fail") throw new IOException("Primary failure");
                    if (mode == "cancelled") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                    return new() { Content = new JsonObject() }; },
                ["finish"] = input => { Assert.Equal("the intended resource", input!["label"]!.ToString()); effects.Add("finish");
                    if (mode is "cleanup_failure" or "both_fail") throw new IOException("Cleanup failure"); return new() { Content = new JsonObject() }; }
            } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        PlanTask Op(string id, string name) => new() { Id = id, Kind = "operation", Objective = "Use the declared operation", Operation = catalog.Capabilities.Single(c => c.Method == name).Id };
        var plan = Plan(); plan.Inputs.Clear(); plan.Root.Tasks = [Op("prepare", method), Op("work", "work")];
        var finish = Op("release", "finish"); finish.Inputs = [new("label", Field(Output("prepare"), "label"))];
        plan.Root.Always[0].Body!.Tasks.Add(finish);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject(), cancellation.Token);
        Assert.Equal(mode is "true" or "false", result.Success);
        Assert.Equal(mode is "true" or "failure" or "cancelled" or "cleanup_failure" or "both_fail", effects.Contains("finish"));
        Assert.Equal(1, effects.Count(e => e == "read")); Assert.InRange(effects.Count(e => e == "finish"), 0, 1);
        if (result.Success) Assert.Equal(mode == "true" ? "performed" : "skipped", result.Outputs!["finalization"]!["status"]!.ToString());
        else { Assert.Null(result.Outputs); Assert.NotNull(result.Error); }
        if (mode is "failure" or "both_fail") Assert.Contains("Primary failure", result.Error!.Message);
        if (mode is "cleanup_failure" or "both_fail") Assert.Contains("Cleanup failure", result.Error!.Details!.ToJsonString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PresenceConditionsKeepTheAbsentProducerAlternative(bool absent, bool negated)
    {
        var engine = new WorkflowEngine(); var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct); var plan = Plan();
        var condition = new TaskValue { Kind = "present", Source = "prepare" };
        plan.Root.Always[0].Condition = negated ? new() { Kind = "predicate", Predicate = "not", Items = [condition] } : condition;
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        // A skipped producer models an interrupted main scope without changing the semantic fixture.
        if (absent) compiled.Graph!.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        Assert.Null(compiled.Graph!.Workflows[0].Finally.Single(n => n.Type == "switch").If);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["selected"] = true }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(absent == negated ? "performed" : "skipped", result.Outputs!["finalization"]!["status"]!.ToString());
    }

    [Theory]
    [InlineData("and", false, true, "skipped")]
    [InlineData("or", true, true, "performed")]
    [InlineData("and", true, false, "performed")]
    [InlineData("or", false, false, "performed")]
    public async Task SelectorsPreserveShortCircuitEvaluation(string predicate, bool selected, bool absent, string expected)
    {
        var engine = new WorkflowEngine(); var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct); var plan = Plan();
        plan.Root.Tasks[0].Outputs[0] = new("flag", new() { Kind = "boolean", Boolean = true });
        plan.Root.Always[0].Condition = new() { Kind = "predicate", Predicate = predicate,
            Items = [new() { Kind = "input", Source = "selected" }, Field(Output("prepare"), "flag")] };
        // Observe the finalizer's own result; a skipped main producer cannot establish root exports.
        plan.Root.Outputs.Clear();
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        if (absent) compiled.Graph!.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["selected"] = selected }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        var aggregate = compiled.Graph!.Workflows[0].Finally.Last(n => n.Type == "set");
        var output = Assert.Single(result.StepResults, r => r.StepId == "n_" + PlanningGraphCompiler.Fingerprint(aggregate.Key)[..16]);
        Assert.Equal(expected, output.Output!["result"]!["status"]!.ToString());
    }
}
