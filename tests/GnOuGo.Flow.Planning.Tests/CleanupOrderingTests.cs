using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CleanupOrderingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanupOrderingSurvivesFailureAndCancellationWithoutBypassingApproval(bool nested, bool groupDependency)
    {
        var effects = new List<string>(); var variant = "nominal"; CancellationTokenSource? cancellation = null;
        var factory = Factory(false, (method, _) =>
        {
            effects.Add(method);
            if (method == "perform" && variant == "failure") throw new InvalidOperationException("Injected write failure");
            if (method == "perform" && variant == "cancelled") cancellation!.Cancel();
            return new();
        });
        var runtime = new TestRuntime(mcp: factory); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var cleanup = new CleanupIntentOperation { Id = "finalize", After = groupDependency ? ["perform"] : [], Operations =
            [Invoke(catalog, "release", groupDependency ? [] : ["perform"])] };
        var plan = Plan([Invoke(catalog, "perform"), cleanup], nested);
        runtime.Plans.Clear(); runtime.Plans.Enqueue(plan);
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Empty(effects);
        var body = state.Graph!.Workflows.Single(w => w.Finally.Any(n => n.Key == "release"));
        Assert.Equal(["perform"], body.Finally[0].Dependencies); Assert.Null(body.Finally[0].If);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        foreach (var scenario in new[] { "nominal", "failure", "cancelled", "denied", "unavailable" })
        {
            variant = scenario; effects.Clear(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct); cancellation = stop;
            var engine = new WorkflowEngine { McpClientFactory = factory,
                HumanInputProvider = scenario == "unavailable" ? null : new PlanningCorpus.Human(scenario != "denied") };
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), stop.Token);
            Assert.True(result.Success == (scenario == "nominal"), scenario + ": " + result.Error?.Message);
            Assert.Equal(scenario is "denied" or "unavailable" ? [] : new[] { "perform", "release" }, effects);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredResourcesAndConditionsRemainGuardedIndependentlyOfOrdering(bool nested)
    {
        foreach (var scenario in new[] { "nominal", "acquisition_failure", "acquisition_skipped", "work_failure", "cancelled", "condition_false" })
        {
            var effects = new List<string>(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var factory = Factory(true, (method, arguments) =>
            {
                effects.Add(method);
                if (method == "acquire")
                {
                    if (scenario == "acquisition_failure") throw new InvalidOperationException("Injected acquisition failure");
                    return new() { ["resource"] = "actual-resource", ["ready"] = scenario != "condition_false" };
                }
                if (method == "perform" && scenario == "work_failure") throw new InvalidOperationException("Injected work failure");
                if (method == "perform" && scenario == "cancelled") stop.Cancel();
                if (method == "release") Assert.Equal("actual-resource", arguments?["resource"]?.ToString());
                return new();
            });
            var runtime = new TestRuntime(mcp: factory); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
            var acquire = Invoke(catalog, "acquire"); acquire.When = new() { Kind = "boolean", Boolean = scenario != "acquisition_skipped" };
            var release = Invoke(catalog, "release", ["perform"]);
            release.Arguments = [new("resource", new() { Kind = "result", Source = "acquire", Path = ["resource"] })];
            release.When = new() { Kind = "result", Source = "acquire", Path = ["ready"] };
            var graph = PlanningGraphBuilder.Build(Plan([acquire, Invoke(catalog, "perform", ["acquire"]),
                new CleanupIntentOperation { Id = "finalize", Operations = [release] }], nested), catalog);
            PlanningConfirmationGuards.Apply(graph, catalog);
            var body = graph.Workflows.Single(w => w.Finally.Any(n => n.Key == "release"));
            var finalizer = Assert.Single(body.Finally);
            Assert.True(PlanningGraphBuilder.GuardsFinalizerSource(finalizer, "acquire"));
            Assert.False(PlanningGraphBuilder.GuardsFinalizerSource(finalizer, "perform"));
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
            var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human() };
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), stop.Token);
            Assert.True(result.Success == (scenario is "nominal" or "acquisition_skipped" or "condition_false"), scenario + ": " + result.Error?.Message);
            Assert.Equal(scenario switch
            {
                "acquisition_failure" => ["acquire"],
                "acquisition_skipped" => ["perform"],
                "condition_false" => ["acquire", "perform"],
                _ => new[] { "acquire", "perform", "release" }
            }, effects);

            // An ordering edge is not proof that a referenced result exists. Removing the
            // host-generated guard must fail validation and compilation, even with after set.
            finalizer.If = null;
            Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "BINDING_UNAVAILABLE");
            Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, catalog));
        }
    }

    [Fact]
    public async Task ConditionOnlyReferencesAreGuardedAndExplicitFalseIsPreserved()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        foreach (var condition in new[] { new IntentValue { Kind = "result", Source = "resource" }, new IntentValue { Kind = "boolean", Boolean = false } })
        {
            var graph = PlanningGraphBuilder.Build(new() { Operations = [
                new CalculateIntentOperation { Id = "resource", When = new() { Kind = "boolean", Boolean = false }, Value = new() { Kind = "boolean", Boolean = true } },
                new CleanupIntentOperation { Id = "finalize", When = condition, After = ["resource"], Operations = [Number("release")] }
            ] }, catalog);
            Assert.Equal(condition.Kind == "result", PlanningGraphBuilder.GuardsFinalizerSource(graph.Workflows[0].Finally[0], "resource"));
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
            Assert.True(result.Success, result.Error?.Message); Assert.Equal(GnOuGo.Flow.Core.Models.StepStatus.Skipped, result.StepResults.Last().Status);
        }
    }

    [Fact]
    public async Task StoredGraphSurvivesRestartAndExplicitRevisionNeedsFreshApproval()
    {
        var plan = new WorkflowIntentPlan { Operations = [Number("main"),
            new CleanupIntentOperation { Id = "finalize", Operations = [Number("release", "main")] }] };
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        // A persisted graph may still contain the former completion guard. Restart keeps
        // its executable meaning; only an explicit revision rebuilds from business intent.
        state.Graph!.Workflows[0].Finally[0].If = new() { Kind = "expression", Text = "data.steps[\"main\"] != null" };
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog!, state.Request.Name);
        state.Status = PlanningStatus.Approved; state.ApprovedHash = PlanningArtifactApproval.Hash(state);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var restarted = JsonSerializer.Deserialize(original, PlanningJsonContext.Default.PlanningSession)!;
        var nextRuntime = new TestRuntime(plan); var planner = new TypedWorkflowPlanner();
        var resumed = await planner.AdvanceAsync(restarted, new() { ExpectedRevision = restarted.Revision }, nextRuntime, Ct);
        Assert.Empty(nextRuntime.Calls);
        Assert.Equal(original, JsonSerializer.Serialize(resumed, PlanningJsonContext.Default.PlanningSession));
        var revised = await planner.AdvanceAsync(resumed, new() { Kind = "revise", Text = "Keep the same business operations.", ExpectedRevision = resumed.Revision }, nextRuntime, Ct);
        Assert.Null(revised.ApprovedHash); Assert.Null(revised.Graph); Assert.Null(revised.Yaml);
        revised = await PlannerFixture.RunAsync(nextRuntime, revised);
        Assert.Equal(PlanningStatus.FinalReview, revised.Status); Assert.Null(revised.Graph!.Workflows[0].Finally[0].If);
        Assert.NotEqual(state.Yaml, revised.Yaml); Assert.Equal(state.ModelCalls + 1, revised.ModelCalls);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(revised,
            new() { Kind = "approve", ExpectedRevision = revised.Revision, ArtifactHash = state.ApprovedHash }, nextRuntime, Ct));
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task CleanupOrderingRetainsSiblingOrderAndLocatedDependencyErrors()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var first = Number("first"); var last = Number("last", "first");
        var plan = new WorkflowIntentPlan { Operations = [Number("main"), new CleanupIntentOperation { Id = "finalize", After = ["main"], Operations = [last, first] }] };
        var graph = PlanningGraphBuilder.Build(plan, catalog);
        Assert.Equal(["first", "last"], graph.Workflows[0].Finally.Select(n => n.Key));
        Assert.All(graph.Workflows[0].Finally, n => Assert.Null(n.If));
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        first.After = ["last"];
        Assert.Contains(PlanningExecutableValidation.Validate(PlanningGraphBuilder.Build(plan, catalog), catalog), d => d.Code == "DEPENDENCY_CYCLE" && d.Location.StartsWith("/workflows/0/finally/", StringComparison.Ordinal));
        first.After = ["missing"];
        Assert.Contains(PlanningExecutableValidation.Validate(PlanningGraphBuilder.Build(plan, catalog), catalog), d => d.Code == "DEPENDENCY_UNKNOWN" && d.Location == "/workflows/0/finally/0/dependencies");
        first.After = []; plan.Operations[0].After = ["last"];
        Assert.Contains(PlanningExecutableValidation.Validate(PlanningGraphBuilder.Build(plan, catalog), catalog), d => d.Code == "DEPENDENCY_SCOPE" && d.Location == "/workflows/0/steps/0/dependencies");
    }

    private static CalculateIntentOperation Number(string id, params string[] after) => new() { Id = id, After = [.. after], Value = new() { Kind = "number", Number = 1 } };
    private static InvokeIntentOperation Invoke(PlanningCatalog catalog, string method, string[]? after = null) => new()
    { Id = method, Capability = catalog.Capabilities.Single(c => c.Method == method).Id, After = after?.ToList() ?? [] };
    private static WorkflowIntentPlan Plan(List<IntentOperation> operations, bool nested)
    {
        operations.Add(Number("finish", "perform"));
        var outputs = new List<IntentOutput> { new("result", new() { Kind = "result", Source = "finish" }) };
        return nested
            ? new() { Operations = [new CallIntentOperation { Id = "run", Flow = "job" }], Subflows = [new("job", [], operations, outputs)],
                Outputs = [new("result", new() { Kind = "result", Source = "run", Path = ["result"] })] }
            : new() { Operations = operations, Outputs = outputs };
    }
    private static InMemoryMcpClientFactory Factory(bool resource, Func<string, JsonNode?, JsonObject> execute)
    {
        const string empty = """{"type":"object","properties":{},"additionalProperties":false}""";
        const string resourceInput = """{"type":"object","properties":{"resource":{"type":"string"}},"required":["resource"],"additionalProperties":false}""";
        const string resourceOutput = """{"type":"object","properties":{"resource":{"type":"string"},"ready":{"type":"boolean"}},"required":["resource","ready"],"additionalProperties":false}""";
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        foreach (var method in resource ? new[] { "acquire", "perform", "release" } : ["perform", "release"])
        {
            server.Tools.Add(new() { Name = method, EffectKind = method == "release" ? "lifecycle" : "write",
                InputSchema = JsonNode.Parse(resource && method == "release" ? resourceInput : empty), OutputSchema = JsonNode.Parse(method == "acquire" ? resourceOutput : empty),
                ExampleResponse = method == "acquire" ? new JsonObject { ["resource"] = "sample", ["ready"] = true } : new JsonObject() });
            server.ToolHandlers[method] = args => new() { Content = execute(method, args) };
        }
        factory.RegisterServer("fixture", server); return factory;
    }
}
