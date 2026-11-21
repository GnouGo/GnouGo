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
    public async Task SanitizedPlaceholderCanClaimSuccessButHasNoObservedExternalWork()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OutcomeCoverage", "retained-placeholder-plan.json"), Ct))!;
        var state = PlannerFixture.Session(); state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, Ct); state.Requirements = PlannerFixture.Requirements(); state.Plan = fixture["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Requirements.Outcomes = [Outcome("perform", "execute"), Outcome("publish", "write"), Outcome("cleanup", "lifecycle", always: true)];
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
