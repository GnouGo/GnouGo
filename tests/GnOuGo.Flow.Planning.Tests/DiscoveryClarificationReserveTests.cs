using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiscoveryClarificationReserveTests
{
    [Fact]
    public async Task FiveDiscoveryCallsThenClarificationLeaveProposalAndLiteralCorrection()
    {
        var runtime = new TestRuntime { Capabilities = new DiscoveryCallBudgetTests.Pages() };
        var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Plan!.Inputs = [new() { Name = "greeting", Type = new() { Kind = "string" } }];
        runtime.Proposal.Requirements!.Inputs = runtime.Proposal.Plan.Inputs;
        runtime.Respond = (request, _) =>
        {
            if (runtime.Calls.Count <= 5) return TestRuntime.Response(request, new()
            { DiscoveryRequests = [new("source", Query: "refinement " + runtime.Calls.Count)] });
            if (runtime.Calls.Count == 6) return TestRuntime.Response(request, Question());
            runtime.Proposal.Plan.Root.Tasks[0].Outputs[0] = new("message", runtime.Calls.Count == 7
                ? new() { Kind = "string", Text = "${greeting}" } : new() { Kind = "input", Source = "greeting" });
            return TestRuntime.Response(request, runtime.Proposal);
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.Equal(6, state.ModelCalls);
        Assert.Equal("null", runtime.Calls[5].StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]!.ToString());
        var budget = Context(runtime.Calls[5].Prompt)["budget"]!;
        Assert.Equal(3, budget["reservedCalls"]!.GetValue<int>());
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), Answer(state), runtime, PlannerFixture.Ct);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(8, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Equal(2, Context(runtime.Calls[6].Prompt)["budget"]!["reservedCalls"]!.GetValue<int>());
        Assert.True(PlanningRepairPatch.Issued(runtime.Calls[7].StructuredOutputSchema!.AsObject()));
        Assert.Equal("input", state.Plan!.Root.Tasks[0].Outputs[0].Value.Kind);
        Assert.All(runtime.Calls, call => Assert.InRange(PlanningJsonTransport.EstimateInputTokens(call.Prompt, call.StructuredOutputSchema!.AsObject()), 1, state.Request.Generation.MaxInputTokensPerRequest));
        await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(8, runtime.Calls.Count);
    }

    [Theory]
    [InlineData(1, 2, false, false)]
    [InlineData(2, 0, false, false)]
    [InlineData(3, 2, false, false)]
    [InlineData(4, 2, false, true)]
    [InlineData(3, 0, false, true)]
    [InlineData(3, 2, true, true)]
    public async Task ReservesNeverPreventAnEarlyProposal(int calls, int repairs, bool answered, bool discovery)
    {
        var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        state.Request.MaxModelCalls = calls; state.Request.MaxReplanAttempts = repairs;
        if (answered) state.AnswerHistory = [new(0, Question().Clarifications!, [new("intent", "runtime")])];
        var runtime = new TestRuntime { Capabilities = new DiscoveryCallBudgetTests.Pages() };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(discovery, Context(runtime.Calls[0].Prompt)["budget"]!["discoveryAllowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RepeatedClarificationsConsumeTheSameAllowanceAndStopWithoutGuessing()
    {
        var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        state.Request.MaxModelCalls = 2;
        var runtime = new TestRuntime { Respond = (request, _) => TestRuntime.Response(request, Question()) };
        var planner = new HybridWorkflowPlanner();
        for (var call = 1; call <= 2; call++)
        {
            state = await PlannerFixture.RunAsync(runtime, state);
            Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.Equal(call, state.ModelCalls);
            state = await planner.AdvanceAsync(state, Answer(state), runtime, PlannerFixture.Ct);
        }
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Plan); Assert.Null(state.Yaml);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "LLM_BUDGET_EXCEEDED");
    }

    [Fact]
    public async Task IssuedDiscoveryIsReusedEvenWhenNewPolicyWouldCloseDiscovery()
    {
        var state = PlannerFixture.Session(); state.Catalog = new();
        state.Discovery.Sources = [new("source", "Operations")];
        var schema = PlanningSchemas.Proposal(state);
        state.Request.Options["automatic_correction"] = true; state.ModelCalls = 6;
        state.PendingCall = new() { Id = "retained:6", Purpose = "tasks", Request = new() { ClientRequestId = "retained:6", StructuredOutputSchema = schema } };
        var runtime = new TestRuntime { Capabilities = new DiscoveryCallBudgetTests.Pages(),
            Respond = (request, _) => TestRuntime.Response(request, DiscoveryCallBudgetTests.Discovery(6)) };
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new(), runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics); Assert.Equal(PlanningPhase.Discovery, state.Phase); Assert.Equal(6, state.ModelCalls);
        Assert.Equal("retained:6", Assert.Single(runtime.Calls).ClientRequestId);
        Assert.True(JsonNode.DeepEquals(schema, runtime.Calls[0].StructuredOutputSchema));
    }

    private static PlanningProposal Question() => new() { Clarifications = [new("intent", "Which greeting source?",
        [new("runtime", "Caller input"), new("fixed", "Fixed greeting")], "runtime")] };
    private static PlanningCommand Answer(PlanningSession state) => new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = [new("intent", "runtime")] };
    private static JsonNode Context(string prompt) => JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
}
