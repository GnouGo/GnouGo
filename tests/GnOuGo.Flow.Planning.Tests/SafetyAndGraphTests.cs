using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class SafetyAndGraphTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("write")]
    [InlineData("unknown")]
    public async Task ProtectedBodyAndCleanupCannotRunWithoutConfirmation(string effect)
    {
        var effects = new List<string>(); var factory = new InMemoryMcpClientFactory();
        var config = new MockMcpServerConfig();
        foreach (var method in new[] { "action", "cleanup" })
        {
            config.Tools.Add(new() { Name = method, EffectKind = effect, InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""), OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""") });
            config.ToolHandlers[method] = _ => { effects.Add(method); return new() { Content = new JsonObject { ["ok"] = true } }; };
        }
        factory.RegisterServer("test", config);
        var runtime = new TestRuntime(mcp: factory);
        var initial = PlannerFixture.Session(); var catalog = await runtime.DiscoverAsync(initial.Request, Ct);
        var plan = new WorkflowIntentPlan { Summary = "Perform the action and clean up", Workflows = [new() {
            Steps = [new() { Key = "action", CapabilityId = catalog.Capabilities.Single(c => c.Method == "action").Id }],
            Finally = [new() { Key = "cleanup", Dependencies = ["action"], CapabilityId = catalog.Capabilities.Single(c => c.Method == "cleanup").Id }]
        }] };
        plan.Fixtures = new() { Observations = [new("main", "action", [new() { Kind = "object", Members = [new("ok", new() { Kind = "boolean", Boolean = true })] }])] };
        runtime.Plans.Clear(); runtime.Plans.Enqueue(plan);
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Single(runtime.Calls);
        Assert.Empty(effects); // All validation uses isolated integrations.
        Assert.Contains(state.Scenarios, s => s.Id.StartsWith("confirmation:rejected", StringComparison.Ordinal) && s.Outcome == "passed");
        foreach (var answer in new bool?[] { false, null, true })
        {
            effects.Clear();
            var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = answer is null ? null : new Human(answer.Value) };
            var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
            var result = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject(), Ct);
            Assert.Equal(answer == true, result.Success);
            if (answer == true) Assert.Equal(["action", "cleanup"], effects); else Assert.Empty(effects);
        }
        state.Graph!.Workflows[0].Steps.RemoveAt(1);
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(state.Graph, state.Catalog!));
    }

    [Fact]
    public async Task FinalizerDependencyRunsOnlyAfterItsProducerCompletes()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        foreach (var producerRuns in new[] { true, false })
        {
            var plan = new WorkflowIntentPlan { Workflows = [new() {
                Steps = [new() { Key = "resource", Kind = "set", If = new() { Kind = "boolean", Boolean = producerRuns } }],
                Finally = [new() { Key = "cleanup", Kind = "set", Dependencies = ["resource"] }]
            }] };
            var graph = PlanningGraphBuilder.Build(plan, catalog);
            var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
            var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(producerRuns ? StepStatus.Succeeded : StepStatus.Skipped, result.StepResults.Last().Status);
        }
    }

    [Fact]
    public async Task ExplicitDependenciesOrderStepsAndCyclesAreRejected()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var plan = PlannerFixture.Greeting(); plan.Workflows[0].Steps.Insert(0, new() { Key = "after", Kind = "set", Dependencies = ["greet"] });
        var graph = PlanningGraphBuilder.Build(plan, catalog);
        Assert.Equal(["greet", "after"], graph.Workflows[0].Steps.Select(s => s.Key));
        plan.Workflows[0].Steps[1].Dependencies.Add("after"); graph = PlanningGraphBuilder.Build(plan, catalog);
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "DEPENDENCY_CYCLE");
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "DEPENDENCY_CYCLE" && d.Message.Contains("greet") && d.Message.Contains("after"));
    }
    [Fact]
    public async Task ConditionalResultsAreNotAvailableToUnconditionalConsumers()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var plan = PlannerFixture.Greeting(); plan.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        var graph = PlanningGraphBuilder.Build(plan, catalog);
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "BINDING_UNAVAILABLE");
    }
    [Fact]
    public async Task RestartResumesResolvedGraphAndReviewWithoutInterpretation()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var restarted = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var newRuntime = new TestRuntime(); var resumed = await PlannerFixture.RunAsync(newRuntime, restarted);
        Assert.Empty(newRuntime.Calls); Assert.Equal(state.Yaml, resumed.Yaml); Assert.Equal(state.ModelCalls, resumed.ModelCalls);
        Assert.Equal(state.ActiveMilliseconds, resumed.ActiveMilliseconds);
    }
    [Fact]
    public async Task InvalidCandidateIdsAreRejectedAndRepairedAsIntent()
    {
        var plan = PlannerFixture.Greeting(); plan.Workflows[0].Steps[0].Input.Members.Add(new("alternative", new() { Kind = "string", Text = "Other" }));
        plan.Workflows[0].Outputs[0].Value = new() { Kind = "hole" };
        var runtime = new TestRuntime(plan); var calls = 0;
        runtime.Respond = request => ++calls == 1 ? new() { Json = PlannerFixture.Response(plan, request.StructuredOutputSchema!.AsObject()) }
            : calls == 2 ? new() { Json = new JsonObject { [request.StructuredOutputSchema!["properties"]!.AsObject().First().Key] = "unissued" } }
            : new() { Json = PlannerFixture.Response(PlannerFixture.Greeting(), request.StructuredOutputSchema!.AsObject()) };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(3, calls); Assert.Equal(1, state.RepairAttempts);
        Assert.Contains("INTENT_SCHEMA_INVALID", runtime.Calls[2].Prompt);
    }
    [Fact]
    public async Task CancellationAndInputBudgetPreventDispatch()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Generation.MaxInputTokensPerRequest = 1024;
        state.Request.Prompt = new string('x', 100000);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Empty(runtime.Calls); Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TypedWorkflowPlanner().AdvanceAsync(PlannerFixture.Session(), new(), runtime, cancellation.Token));
    }
    private sealed class Human(bool answer) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer });
    }
}
