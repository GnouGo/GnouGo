using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstantWorkspaceTests
{
    [Fact]
    public async Task RunnerPolicyContractsAreVersionedAndValidatedWithoutProviderRules()
    {
        var (plan, catalog) = Fixture(); var runner = new PolicyRunner(); var engine = new WorkflowEngine();
        engine.AgentTaskRunners["renamed-adaptive-host"] = runner;
        var discovery = new CapabilityDiscovery(engine); var source = Assert.Single(await discovery.ListSourcesAsync(TestContext.Current.CancellationToken));
        var page = await discovery.ListAsync(source.Id, null, TestContext.Current.CancellationToken);
        var capability = await discovery.ResolveAsync(Assert.Single(page.Capabilities), TestContext.Current.CancellationToken);
        catalog.Capabilities = [capability]; var task = plan.Root.Tasks[1]; task.Operation = capability.Id;
        task.Inputs.Single(i => i.Name == "capabilities").Value.Items[0].Text = "fixture.adapt";
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var session = new PlanningSession { Plan = plan, Graph = compiled.Graph, Catalog = catalog, Yaml = "reviewed" };
        var approved = session.ComputeArtifactHash();
        runner.Ready = false;
        var refreshed = new CapabilityDiscovery(engine); var changed = Assert.Single((await refreshed.ListAsync(source.Id, null, TestContext.Current.CancellationToken)).Capabilities);
        Assert.NotEqual(capability.Version, changed.Version);
        await Assert.ThrowsAsync<PlanningConflictException>(() => refreshed.ResolveAsync(page.Capabilities[0], TestContext.Current.CancellationToken));
        catalog.Capabilities = [await refreshed.ResolveAsync(changed, TestContext.Current.CancellationToken)];
        Assert.NotEqual(approved, session.ComputeArtifactHash());
        var rejected = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(rejected.Graph); Assert.Contains(rejected.Diagnostics, d => d.Location == "/tasks/work/inputs/capabilities");
        Assert.Equal(0, runner.Dispatches);
    }

    private sealed class PolicyRunner : IAgentTaskRunner
    {
        internal bool Ready = true;
        internal int Dispatches;
        public Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct)
        {
            var schema = AgentTaskContracts.InputSchema;
            schema["properties"]!["capabilities"]!["items"]!["enum"] = Ready ? new JsonArray("fixture.inspect", "fixture.adapt") : new JsonArray("fixture.inspect");
            return Task.FromResult(new AgentTaskRunnerContract("Declared host policy; runtime scope checks remain mandatory", schema));
        }
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct) { Dispatches++; throw new InvalidOperationException("Discovery cannot dispatch"); }
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact]
    public void SharedValueAndFieldSelectionCompileToLiteralWithoutChangingIntent()
    {
        var (plan, catalog) = Fixture();
        var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph!, catalog));
        var agent = compiled.Graph!.Workflows[0].Steps.Single(s => s.Type == "agent.run");
        Assert.Equal("workflows/example/project", agent.Input.Members.Single(m => m.Name == "workspace").Value.Text);
        Assert.Equal("string", agent.Input.Members.Single(m => m.Name == "workspace").Value.Kind);
        Assert.NotEmpty(agent.Dependencies);
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        var session = new PlanningSession { Plan = plan, Graph = compiled.Graph, Catalog = catalog, Yaml = "reviewed" };
        var before = session.ComputeArtifactHash();
        plan.Root.Tasks[0].Outputs[0].Value.Members[0].Value.Text = "workflows/other/project";
        var next = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(next.Diagnostics); session.Graph = next.Graph;
        Assert.NotEqual(before, session.ComputeArtifactHash());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("operation")]
    [InlineData("choice")]
    public void RuntimeAndChoiceValuesNeverBecomeApprovedConstants(string source)
    {
        var (plan, catalog) = Fixture();
        if (source == "input")
        {
            plan.Inputs.Add(new() { Name = "location", Type = new() { Kind = "string" } });
            plan.Root.Tasks[0].Outputs[0].Value.Members[0] = new("path", new() { Kind = "input", Source = "location" });
        }
        else if (source == "operation")
        {
            catalog.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" },
                OutputSchema = JsonNode.Parse("""{"type":"object","required":["location"],"properties":{"location":{"type":"object","required":["path"],"properties":{"path":{"type":"string","const":"workflows/example/project"}}}}} """)!.AsObject() });
            plan.Root.Tasks[0].Kind = "operation"; plan.Root.Tasks[0].Operation = "source"; plan.Root.Tasks[0].Outputs.Clear();
        }
        else
        {
            plan.Choices.Add(new() { Id = "where", Question = "Location", Recommended = "a", Selected = "a", Alternatives = [new("a", "First", Text("workflows/a")), new("b", "Second", Text("workflows/b"))] });
            plan.Root.Tasks[0].Outputs[0].Value.Members[0] = new("path", new() { Kind = "choice", Source = "where" });
        }
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compiled.Graph);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "AGENT_SCOPE_DYNAMIC" && d.Location == "/tasks/work/inputs/workspace");
    }

    [Fact]
    public void UnavailableSiblingAndCyclicConstantsFailBeforeLowering()
    {
        var (plan, catalog) = Fixture();
        var paths = plan.Root.Tasks[0]; var work = plan.Root.Tasks[1];
        plan.Root.Tasks = [new() { Id = "branches", Kind = "parallel", Objective = "Separate work", Branches = [new() { Tasks = [paths] }, new() { Tasks = [work] }] }];
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph);
        (plan, catalog) = Fixture();
        plan.Root.Tasks[0].Outputs[0] = new("location", new() { Kind = "output", Source = "paths", Port = "location" });
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_DEPENDENCY_CYCLE");
    }

    [Fact]
    public void CapturedAncestorConstantRemainsAHostApprovedLiteral()
    {
        var (plan, catalog) = Fixture(); var work = plan.Root.Tasks[1];
        plan.Root.Tasks[1] = new() { Id = "scope", Kind = "sequence", Objective = "Nested work", Body = new() { Tasks = [work] } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph!, catalog));
        Assert.Equal("string", compiled.Graph!.Workflows.SelectMany(w => w.Steps).Single(s => s.Type == "agent.run").Input.Members.Single(m => m.Name == "workspace").Value.Kind);
    }

    [Fact]
    public void LoopLocalConstantsAndOtherDynamicScopeFieldsRemainForbidden()
    {
        var (plan, catalog) = Fixture();
        plan.Root.Tasks = [new() { Id = "repeat", Kind = "foreach", Objective = "Bounded work", Items = new() { Kind = "array", Items = [Text("item")] },
            MaxItems = 1, MaxConcurrency = 1, Body = new() { Tasks = plan.Root.Tasks } }];
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "AGENT_SCOPE_DYNAMIC");
        (plan, catalog) = Fixture();
        var task = plan.Root.Tasks[1]; task.Inputs[task.Inputs.FindIndex(i => i.Name == "objective")] = new("objective", task.Inputs.Single(i => i.Name == "workspace").Value);
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "AGENT_SCOPE_DYNAMIC" && d.Location.EndsWith("/objective", StringComparison.Ordinal));
        var changed = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        changed.Root.Tasks[0].Outputs[0].Value.Members[0].Value.Text = "workflows/elsewhere";
        Assert.Contains(TaskPlanRevisions.Validate(plan, changed, ["/tasks/work/inputs/objective"], catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    private static (TaskPlan, PlanningCatalog) Fixture()
    {
        var definition = JsonNode.Parse("""{"objective":"Inspect project","workspace":"workflows/example/project","capabilities":["project.read"],"budget":{"max_model_calls":1,"max_total_tokens":1000,"max_elapsed_milliseconds":1000},"output_schema":{"type":"object"},"verification":[{"id":"file","kind":"file.content","subject":"result.txt","facts_schema":{"type":"object"}}]}""")!.AsObject();
        var task = new PlanTask { Id = "work", Kind = "operation", Operation = "runner", Objective = "Inspect project", Inputs = definition.Select(kv => new TaskOutput(kv.Key, Literal(kv.Value))).ToList() };
        task.Inputs[task.Inputs.FindIndex(i => i.Name == "workspace")] = new("workspace", new() { Kind = "field", Port = "path", Items = [new() { Kind = "output", Source = "paths", Port = "location" }] });
        return (new() { Root = new() { Tasks = [new() { Id = "paths", Kind = "value", Objective = "Declare location", Outputs = [new("location", new() { Kind = "object", Members = [new("path", Text("workflows/example/project"))] })] }, task] } },
            new() { AllowedStepTypes = ["agent.run", "set", "workflow.call"], Capabilities = [new() { Id = "runner", Kind = "agent", StepType = "agent.run", FixedInput = new() { ["runner"] = "test" }, InputSchema = AgentTaskContracts.InputSchema, OutputSchema = new() { ["type"] = "object" } }] });
    }
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskValue Literal(JsonNode? node) => node switch
    {
        JsonObject o => new() { Kind = "object", Members = o.Select(p => new TaskOutput(p.Key, Literal(p.Value))).ToList() },
        JsonArray a => new() { Kind = "array", Items = a.Select(Literal).ToList() },
        JsonValue v when v.TryGetValue<string>(out var s) => Text(s),
        JsonValue v when v.TryGetValue<int>(out var n) => new() { Kind = "number", Number = n },
        _ => throw new InvalidOperationException()
    };
}
