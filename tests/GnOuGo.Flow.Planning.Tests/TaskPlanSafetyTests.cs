using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPlanSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMappingsBindRenamedReorderedCapabilitiesWithoutGuessing(bool reverse)
    {
        var (catalog, plan) = await Fixture();
        var selected = catalog.Capabilities.Single(c => c.Id == "issued");
        selected.Method = reverse ? "renamed_operation" : "operation";
        selected.Operation = new() { Id = "business-operation", Version = "v1", Description = "Declared operation",
            Inputs = [new() { Name = "amount", Path = ["wire_input"], Schema = new() { ["type"] = "number" }, Required = true }],
            Outputs = [new() { Name = "answer", Path = ["wire_output"], Schema = new() { ["type"] = "number" }, Required = true }] };
        for (var i = 0; i < 12; i++) catalog.Capabilities.Add(new() { Id = "distractor" + i, StepType = "mcp.call", Version = "v1", Description = "No instructions override binding", InputSchema = new() });
        if (reverse) catalog.Capabilities.Reverse();
        plan.Root.Tasks[0].Operation = "business-operation";
        plan.Root.Tasks[0].Inputs = [new("amount", PlanningCorpus.Number(42))]; plan.Root.Outputs[0].Value.Port = "answer";
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        Assert.Contains(selected.Method, yaml); Assert.Contains("wire_input", yaml); Assert.Contains("wire_output", yaml); Assert.DoesNotContain("distractor", yaml);
        selected.Operation.Outputs[0].Schema["type"] = "string";
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "OPERATION_MAPPING_INVALID");
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("opaque")]
    [InlineData("cycle")]
    [InlineData("unknown")]
    public async Task AmbiguityOpaqueFieldAccessAndCyclesFailAtBusinessLocations(string fault)
    {
        var (catalog, plan) = await Fixture();
        if (fault == "duplicate") catalog.Capabilities.Add(catalog.Capabilities.Single(c => c.Id == "issued"));
        if (fault == "opaque") catalog.Capabilities.Single(c => c.Id == "issued").OutputSchema.Clear();
        if (fault == "cycle") plan.Root.Tasks[0].DependsOn = ["work"];
        if (fault == "unknown") plan.Root.Tasks[0].Operation = "unissued";
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compiled.Graph); Assert.NotEmpty(compiled.Diagnostics);
        Assert.DoesNotContain(compiled.Diagnostics, d => d.Location.Contains("workflows", StringComparison.Ordinal));
    }
    [Fact]
    public async Task ParallelBranchesExportOnlyDeclaredBusinessOutputs()
    {
        var (catalog, _) = await Fixture();
        var plan = new TaskPlan { Root = new() { Tasks = [new()
        {
            Id = "both", Objective = "Produce both values concurrently", Kind = "parallel", Branches = [
                new() { Tasks = [new() { Id = "first", Objective = "First value", Kind = "value", Outputs = [new("number", PlanningCorpus.Number(1))] }], Outputs = [new("left", PlanningCorpus.Business("output", "first", "number"))] },
                new() { Outputs = [new("right", PlanningCorpus.Number(2))] }]
        }], Outputs = [new("result", PlanningCorpus.Business("output", "both", "left"))] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compilation.Graph!, catalog)));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], null, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("1", result.Outputs!["result"]!.ToString());
        plan.Root.Outputs[0].Value.Source = "first";
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_REFERENCE_UNKNOWN");
    }
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("absent")]
    public async Task CompilerOwnsCleanupGuardsInsideNestedScopes(string mode)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        var effects = new List<string>();
        var factory = new InMemoryMcpClientFactory();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!;
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        factory.RegisterServer("source", new() { Tools = [
            new() { Name = "open", InputSchema = empty, OutputSchema = schema, EffectKind = "read" },
            new() { Name = "use", InputSchema = schema, OutputSchema = empty, EffectKind = "read" },
            new() { Name = "close", InputSchema = schema, OutputSchema = empty, EffectKind = "lifecycle" }], ToolHandlers = new()
        {
            ["open"] = _ => { if (mode == "absent") throw new IOException("Absent producer"); effects.Add("open"); return new() { Content = new JsonObject { ["value"] = "resource" } }; },
            ["use"] = _ => { if (mode == "cancelled") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); } if (mode == "failure") throw new IOException("Failure"); return new() { Content = new JsonObject() }; },
            ["close"] = input => { Assert.Equal("resource", input!["value"]!.ToString()); effects.Add("close"); return new() { Content = new JsonObject() }; }
        } });
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime); catalog.Policy.RequireExternalConfirmation = false;
        PlanTask Op(string id, string method, bool reference) => new() { Id = id, Objective = "Manage resource", Operation = catalog.Capabilities.Single(c => c.Method == method).Id,
            Inputs = reference ? [new("value", PlanningCorpus.Business("output", "acquire", "value"))] : [] };
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "scope", Objective = "Use resource and clean up", Kind = "sequence", Body = new()
            { Tasks = [Op("acquire", "open", false), Op("work", "use", true)], Always = [Op("cleanup", "close", true)] } }] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows["main"], null, cancellation.Token);
        Assert.Equal(mode == "success", result.Success);
        Assert.Equal(mode == "absent" ? [] : new[] { "open", "close" }, effects);
    }
    [Theory]
    [InlineData("input")]
    [InlineData("choice")]
    [InlineData("interpolation")]
    public async Task AgentWorkspaceCannotBecomeDynamicThroughSemanticBindings(string kind)
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.AllowedStepTypes.Add("agent.run");
        var schema = JsonNode.Parse("""{"type":"object","properties":{"workspace":{"type":"string"}},"required":["workspace"],"additionalProperties":false}""")!.AsObject();
        catalog.Capabilities.Add(new() { Id = "runner", Version = "v1", StepType = "agent.run", InputSchema = schema });
        var workspace = kind == "interpolation" ? PlanningCorpus.String("${data.inputs.folder}") : PlanningCorpus.Business(kind, kind == "input" ? "folder" : "workspace");
        var plan = new TaskPlan { Inputs = [new() { Name = "folder" }], Root = new() { Tasks = [new() { Id = "agent", Objective = "Code", Operation = "runner", Inputs = [new("workspace", workspace)] }] } };
        if (kind == "choice") plan.Choices = [new() { Id = "workspace", Question = "Where?", Recommended = "one", Selected = "one", Alternatives = [new("one", "One", PlanningCorpus.String("one")), new("two", "Two", PlanningCorpus.String("two"))] }];
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code is "AGENT_SCOPE_DYNAMIC" or "TASK_LITERAL_INVALID");
    }
    [Theory]
    [InlineData("recommendation")]
    [InlineData("type")]
    [InlineData("multiple_slots")]
    public async Task InvalidBusinessChoicesFailClosed(string invalid)
    {
        var runtime = new TestRuntime { Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = PlanningCorpus.Decision() } };
        if (invalid == "recommendation") runtime.Proposal.Plan.Choices[0].Recommended = "unissued";
        if (invalid == "type") runtime.Proposal.Plan.Choices[0].Alternatives[0].Value.Kind = "number";
        if (invalid == "multiple_slots") runtime.Proposal.Plan.Root.Outputs.Add(new("again", PlanningCorpus.Business("choice", "tone")));
        var session = PlannerFixture.Session(); session.Request.Mode = PlanningMode.Auto;
        var result = await PlannerFixture.RunAsync(runtime, session);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Null(result.Yaml); Assert.Null(result.ApprovedHash);
    }
    [Fact]
    public async Task ApprovalRecompilesIntentAndDetectsCatalogMappingChanges()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        PlanningArtifactApproval.Verify(state); var hash = state.ComputeArtifactHash();
        state.Plan!.Root.Tasks[0].Outputs[0].Value.Text = "Changed";
        Assert.NotEqual(hash, state.ComputeArtifactHash()); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }
    private static async Task<(PlanningCatalog Catalog, TaskPlan Plan)> Fixture()
    {
        var catalog = await new TestRuntime().DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "issued", Version = "v1", StepType = "mcp.call", Kind = "tool", Server = "source", Method = "operation", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"wire_input":{"type":"number"}},"required":["wire_input"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"wire_output":{"type":"number"}},"required":["wire_output"],"additionalProperties":false}""")!.AsObject() });
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "work", Objective = "Produce the requested value", Operation = "issued", Inputs = [new("wire_input", PlanningCorpus.Number(42))] }],
            Outputs = [new("result", PlanningCorpus.Business("output", "work", "wire_output"))] } };
        return (catalog, plan);
    }
}
