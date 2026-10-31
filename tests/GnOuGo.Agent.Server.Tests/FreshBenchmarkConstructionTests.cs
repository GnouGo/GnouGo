using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class FreshBenchmarkConstructionTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("token-only")]
    [InlineData("diagnostic")]
    public async Task ActualFreshBenchmarkPathCapturesBusinessContractBeforeFirstIssuedRequest(string profile)
    {
        var state = Fresh(profile);
        var before = Serialize(state);
        var frozen = LiveWorkflowEvaluation.FrozenPlanningConfiguration(state.Request).ToJsonString();
        var issued = new List<LLMRequest>();
        var checkpoints = new List<PlanningSession>();
        var model = new CurrentPlanningFixture.LiteralModel(onRequest: request => issued.Add(request));
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model, LLMCapabilities = model },
            (checkpoint, _) => { checkpoints.Add(Clone(checkpoint)); return Task.CompletedTask; });

        var advanced = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = 0 }, runtime,
            TestContext.Current.CancellationToken);

        Assert.Equal(before, Serialize(state));
        Assert.Empty(advanced.Diagnostics);
        Assert.Equal(1, advanced.ModelCalls);
        Assert.Equal("construction_intent", Assert.Single(issued).StructuredOutputSchema!["title"]!.ToString());
        Assert.Equal(1, advanced.Request.Options["business_selection_contract"]!.GetValue<int>());
        Assert.Empty(advanced.Construction!.Compact!.Blocks!);
        Assert.Null(advanced.Construction.Compact.CurrentBlockId);
        Assert.Equal(frozen, LiveWorkflowEvaluation.FrozenPlanningConfiguration(advanced.Request).ToJsonString());
        Assert.NotEmpty(checkpoints);
        Assert.All(checkpoints, checkpoint =>
        {
            Assert.Equal(1, checkpoint.Request.Options["business_selection_contract"]!.GetValue<int>());
            Assert.NotNull(checkpoint.Construction!.Compact!.Blocks);
        });

        // The real first checkpoint now restarts through the retained contract;
        // no creation authority is inferred from its revision or configuration.
        advanced = await new HybridWorkflowPlanner().AdvanceAsync(Clone(advanced),
            new() { ExpectedRevision = advanced.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Empty(advanced.Diagnostics);
        Assert.Equal(2, advanced.ModelCalls);
        Assert.Equal("construction_select", issued[^1].StructuredOutputSchema!["title"]!.ToString());
        Assert.Single(advanced.Construction!.Compact!.Blocks!);
        Assert.Null(advanced.ApprovedHash);
    }

    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("ordinary", true)]
    [InlineData("token-only", false)]
    [InlineData("token-only", true)]
    [InlineData("diagnostic", false)]
    [InlineData("diagnostic", true)]
    public async Task RestoredRevisionZeroWithoutBusinessContractIsRejectedUnchanged(string profile, bool pending)
    {
        var state = Fresh(profile);
        if (pending) state.PendingCall = new() { Id = "historical-issued-request", Purpose = "construction_select",
            Request = new() { Prompt = "Retained exact request", StructuredOutputSchema = new JsonObject { ["title"] = "construction_select" } } };
        state = Clone(state);
        var before = Serialize(state);
        var calls = 0;
        var checkpoints = 0;
        var model = new CurrentPlanningFixture.LiteralModel(onRequest: _ => calls++);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model },
            (_, _) => { checkpoints++; return Task.CompletedTask; });

        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = 0 }, runtime, TestContext.Current.CancellationToken));

        Assert.Equal(before, Serialize(state));
        Assert.Equal(0, calls);
        Assert.Equal(0, checkpoints);
        Assert.False(state.Request.Options.ContainsKey("business_selection_contract"));
        Assert.Null(state.Construction!.Compact!.Blocks);
    }

    private static PlanningSession Fresh(string profile)
    {
        string[] options = profile switch
        {
            "token-only" => ["--advisory-token-thresholds"],
            "diagnostic" => ["--advisory-token-thresholds", "--diagnostic-optimization-thresholds"],
            _ => []
        };
        var state = new PlanningSession { Request = LiveWorkflowEvaluation.FreshPlanningRequest(options,
            "fresh-contract-" + profile, "amazon", "Return the required public numeric result named result with value 42.",
            "fixture", "pinned") };
        PlanningBenchmarkMeasurements.CaptureConstructionSettings(state);
        Assert.NotNull(state.Request.Options["construction_limits"]);
        Assert.False(state.Request.Options.ContainsKey("business_selection_contract"));
        Assert.Null(state.Construction!.Compact!.Blocks);
        return state;
    }

    private static string Serialize(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
    private static PlanningSession Clone(PlanningSession state) => JsonSerializer.Deserialize(Serialize(state), PlanningJsonContext.Default.PlanningSession)!;
}
