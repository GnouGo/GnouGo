using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningOutcomeTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;
    internal static PlanningRequirement Outcome(string id, string execution = "write", bool always = false, bool conditional = false) =>
        new(id, "Perform the requested " + id) { Execution = execution, Always = always, Conditional = conditional };

    [Fact]
    public async Task DiscoveryBeforeRequirementsDoesNotConsumeRepairsAndCachedRecoveryPreservesIt()
    {
        var catalog = new Sources(); var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Calls.Count == 1
            ? new() { DiscoveryRequests = [new("one"), new("two")] } : runtime.Proposal);
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        Assert.Null(state.Requirements); Assert.Empty(state.Diagnostics); Assert.Equal(2, catalog.Reads);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, catalog.Reads); PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task SanitizedPlaceholderCanClaimSuccessButHasNoObservedExternalWork()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OutcomeCoverage", "retained-placeholder-plan.json"), Ct))!;
        var state = PlannerFixture.Session(); state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, Ct); state.Requirements = PlannerFixture.Requirements(); state.Plan = fixture["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Requirements.Outcomes = [Outcome("perform", "execute"), Outcome("publish", "write"), Outcome("cleanup", "lifecycle", always: true)];
        state.OutcomeBindings = [new("perform", ["execute_pr_review_workflow"], []), new("publish", ["execute_pr_review_workflow"], []), new("cleanup", ["cleanup_clone_directory"], [])];
        var compilation = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!); Assert.Empty(compilation.Diagnostics);
        var model = new Claims(); var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "deterministic" } };
        // Execute only the unsafe historical artifact with an injected deterministic model:
        // a successful JSON response demonstrably provides no external execution evidence.
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, state.Catalog!);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject
            { ["pullRequestUrl"] = "https://example.invalid/project/pull/1", ["reviewInstructions"] = "Check correctness" }, Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(1, model.Calls);
        Assert.True(result.Outputs!["reviewResult"]!["githubReviewSubmitted"]!.GetValue<bool>());
        Assert.DoesNotContain(PlanningGraphCompiler.Enumerate(compilation.Graph!.Workflows.SelectMany(w => w.Steps.Concat(w.Finally))), n => n.Type is "mcp.call" or "agent.run");
        state.Yaml = yaml; state.Graph = compilation.Graph;
        // A model claim is not the execution oracle: zero external calls means requested work was not performed.
        Assert.Empty(PlanningReviewFormatter.Operations(state));
    }

    private sealed class Sources : ICapabilityCatalog
    {
        internal int Reads;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("one", "First source"), new("two", "Second source")]);
        public Task<CapabilityPage> ListAsync(string id, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { Reads++; return Task.FromResult(new CapabilityPage(id, cursor, [], null, Query: query)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class Claims : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""
                {"pullRequestUrl":"https://example.invalid/project/pull/1","decision":"accept","githubReviewSubmitted":true,
                 "summary":"Claimed success","diffComments":[],"checks":{"dependenciesInstalled":true,"lintersRun":true,"unitTestsRun":true,"integrationTestsRun":true,"blockingFindings":[]}}
                """) });
        }
    }
}
