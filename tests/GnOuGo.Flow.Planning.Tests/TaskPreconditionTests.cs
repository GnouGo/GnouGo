using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPreconditionTests
{
    private static TaskValue Bool(bool value) => new() { Kind = "boolean", Boolean = value };
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskPlan Plan(TaskValue requirement) => new()
    {
        Inputs = [new() { Name = "complete", Type = new() { Kind = "boolean" } }],
        Root = new() { Tasks = [new() { Id = "publish", Kind = "value", Objective = "Publish the requested result only after a complete observation",
            Requires = requirement, Outputs = [new("path", new() { Kind = "string", Text = "observed-result" })] }],
            Outputs = [new("path", new() { Kind = "output", Source = "publish", Port = "path" })] }
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiredConditionFailsInsteadOfSkippingOrInventingAnOutput(bool complete)
    {
        var engine = new WorkflowEngine(); var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = Plan(Input("complete"));
        plan.Root.Always.Add(new() { Id = "cleanup", Kind = "value", Objective = "Finalize after success or verified failure", Outputs = [new("ran", Bool(true))] });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var saved = JsonSerializer.Serialize(compiled.Graph, PlanningJsonContext.Default.PlanningGraph);
        Assert.DoesNotContain("\"kind\":\"expression\"", saved);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        Assert.DoesNotContain("llm.call", yaml); Assert.DoesNotContain("mapping.dynamic", yaml);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["complete"] = complete }, PlannerFixture.Ct);
        Assert.Equal(complete, result.Success);
        Assert.Contains(result.StepResults, r => r.Output?["ran"]?.GetValue<bool>() == true);
        if (complete) Assert.Equal("observed-result", result.Outputs!["path"]!.GetValue<string>());
        else { Assert.Null(result.Outputs); Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.DoesNotContain(result.StepResults, r => r.Output?["path"] is not null); }
    }

    [Theory]
    [InlineData("null", "TASK_CONDITION_TYPE")]
    [InlineData("string", "TASK_CONDITION_TYPE")]
    [InlineData("output", "TASK_REFERENCE_UNKNOWN")]
    public async Task InvalidConditionsFailBeforeLowering(string kind, string code)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(Plan(new() { Kind = kind, Text = kind == "string" ? "true" : null, Source = kind == "output" ? "unknown" : null }), catalog);
        Assert.Null(compiled.Graph); Assert.Contains(compiled.Diagnostics, d => d.Code == code && d.Location == "/tasks/publish/requires");
    }

    [Theory]
    [InlineData("and", false, false)]
    [InlineData("or", true, true)]
    public async Task PreconditionChecksShortCircuitBeforeMissingOptionalFields(string predicate, bool left, bool success)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source", new() { Tools = [new() { Name = "read", InputSchema = new JsonObject { ["type"] = "object" },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"optional":{"type":"boolean"}},"additionalProperties":false}"""), EffectKind = "none" }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = new JsonObject() } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var plan = Plan(new() { Kind = "predicate", Predicate = predicate, Items = [Bool(left), new() { Kind = "field", Port = "optional", Items = [new() { Kind = "output", Source = "observe" }] }] });
        plan.Root.Tasks.Insert(0, new() { Id = "observe", Objective = "Observe an optional flag", Operation = catalog.Capabilities.Single(c => c.Method == "read").Id });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["complete"] = true }, PlannerFixture.Ct);
        Assert.Equal(success, result.Success);
        if (!success) Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerItemRequirementsRunBeforeEffectsInBothLoopModes(bool parallel)
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<bool>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("sink", new() { Tools = [new() { Name = "write", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"item":{"type":"boolean"}},"required":["item"]}"""), OutputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { ["write"] = input => { calls.Add(input!["item"]!.GetValue<bool>()); return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory }; var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "items", Kind = "foreach", Objective = "Process only qualifying entries", Parallel = parallel, MaxConcurrency = 2, MaxItems = 3,
            Items = new() { Kind = "array", Items = [Bool(true), Bool(false), Bool(true)] },
            Body = new() { Tasks = [new() { Id = "publish", Objective = "Write each qualifying entry", Requires = new() { Kind = "item" },
                Operation = catalog.Capabilities.Single(c => c.Method == "write").Id, Inputs = [new("item", new() { Kind = "item" })] }] } }],
            Always = [new() { Id = "cleanup", Kind = "value", Objective = "Finalize", Outputs = [new("finished", Bool(true))] }] } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.False(result.Success); Assert.All(calls, value => Assert.True(value)); Assert.NotEmpty(calls);
        Assert.Contains(result.StepResults, r => r.Output?["finished"]?.GetValue<bool>() == true);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("absent")]
    public async Task FinalizerRequirementsCannotBeSkippedBeforeSideEffects(string condition)
    {
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("sink", new() { Tools = [new() { Name = "write", EffectKind = "none", InputSchema = new JsonObject { ["type"] = "object" }, OutputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { ["write"] = _ => { calls++; return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory }; var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var plan = new TaskPlan { Root = new()
        {
            Tasks = [new() { Id = "produce", Kind = "value", Objective = "Observe a required flag", Requires = Bool(condition != "absent"), Outputs = [new("flag", Bool(condition == "true"))] }],
            Always = [new() { Id = "publish", Objective = "Publish only when the observation permits it", Operation = catalog.Capabilities.Single(c => c.Method == "write").Id,
                Requires = new() { Kind = "output", Source = "produce", Port = "flag" } },
                new() { Id = "cleanup", Kind = "value", Objective = "Finalize independently", Outputs = [new("finished", Bool(true))] }]
        } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(condition == "true", result.Success); Assert.Equal(condition == "true" ? 1 : 0, calls);
        // A failed finalizer stops its sequential block. Cleanup that must survive
        // that failure belongs in a nested always (covered by FinalizerCaptureTests).
        Assert.Equal(condition != "false", result.StepResults.Any(r => r.Output?["finished"]?.GetValue<bool>() == true));
    }

    [Fact]
    public void CompactSchemaRemovesOnlyAnnotationsNotNamedFieldsOrLiteralData()
    {
        var schema = JsonNode.Parse("""{"type":"object","description":"duplicate context","properties":{"description":{"type":"string","description":"duplicate field explanation","const":"keep business description"}},"required":["description"],"additionalProperties":false,"$defs":{}}""")!.AsObject();
        PlanningSchemas.Compact(schema);
        Assert.False(schema.ContainsKey("description"));
        Assert.NotNull(schema["properties"]!["description"]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["description"] = "keep business description" }, schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["description"] = "fabricated" }, schema));
    }

    [Fact]
    public void OmittedPreconditionsPreserveSerializationAndCannotBeWeakenedByRepair()
    {
        var plan = Plan(Bool(false)); var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        Assert.Contains("requires", original);
        var changed = JsonSerializer.Deserialize(original, PlanningJsonContext.Default.TaskPlan)!;
        changed.Root.Tasks[0].Requires = Bool(true);
        Assert.Contains(TaskPlanRevisions.Validate(plan, changed, ["/tasks/publish/requires"]), d => d.Code == "REVISION_SCOPE_CHANGED");
        changed.Root.Tasks[0].Requires = null;
        Assert.Contains(TaskPlanRevisions.Validate(plan, changed, ["/tasks/publish/requires"]), d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.DoesNotContain("requires", JsonSerializer.Serialize(changed, PlanningJsonContext.Default.TaskPlan));
        Assert.Empty(TaskPlanRevisions.Scope(plan, [new("TASK_CONDITION_TYPE", "/tasks/publish/requires", "invalid") ]));
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Fact]
    public async Task GenerationWithExplicitPreconditionUsesOneCallAndNoRepair()
    {
        var runtime = new TestRuntime(); var plan = Plan(Input("complete"));
        runtime.Proposal = new() { Plan = plan, Requirements = new() { Summary = "Return the result only when complete", Inputs = plan.Inputs,
            Outputs = [new() { Name = "path", Type = new() { Kind = "string" } }], Outcomes = [new("result", "Require completeness before returning the result")] } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Message)));
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains("required precondition", PlanningReviewFormatter.TaskDiagram(state.Plan));
    }
}
