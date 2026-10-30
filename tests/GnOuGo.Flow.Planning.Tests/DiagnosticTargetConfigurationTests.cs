using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiagnosticTargetConfigurationTests
{
    public static TheoryData<string, int> LargerTargets => new()
    {
        { "calls", 1001 }, { "repairs", 11 }, { "largeRepairs", 65 }, { "tasks", 33 },
        { "checkpoints", 1025 }, { "draftCorrections", 65 }, { "nonproductive", 65 }
    };

    [Theory]
    [MemberData(nameof(LargerTargets))]
    public async Task DiagnosticTargetsSurviveTheOrdinaryWorkflowAndEveryRestart(string target, int value)
    {
        var state = Session(target, value, diagnostic: true);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = new CurrentPlanningFixture.LiteralModel() },
            (_, _) => Task.CompletedTask);
        var planner = new HybridWorkflowPlanner();
        var capturedRequest = JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest);
        for (var advance = 0; advance < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
        {
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
            state = PlannerFixture.Clone(state);
        }
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(5, state.ModelCalls);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.ApprovedHash);
        Assert.Equal(capturedRequest, JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest));
        Assert.Equal(42, Assert.Single(state.Plan!.Root.Tasks).Outputs[0].Value.Number);
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [MemberData(nameof(LargerTargets))]
    public async Task OrdinaryAndTokenOnlyPoliciesRetainConfigurationBounds(string target, int value)
    {
        foreach (var tokenOnly in new[] { false, true })
        {
            var state = Session(target, value, diagnostic: false);
            if (tokenOnly) state.Request.Options[PlanningTokenThresholds.OptionName] = "advisory";
            await RejectBeforeDispatch(state, target);
        }
    }

    [Theory]
    [InlineData("calls", 0)]
    [InlineData("repairs", -1)]
    [InlineData("largeRepairs", -1)]
    [InlineData("tasks", 0)]
    [InlineData("checkpoints", 0)]
    [InlineData("draftCorrections", -1)]
    [InlineData("nonproductive", 0)]
    [InlineData("draftOutput", 0)]
    [InlineData("draftOutput", 65537)]
    [InlineData("reviewOutput", 0)]
    [InlineData("reviewOutput", 65537)]
    public Task DiagnosticPolicyPreservesValidTargetsAndExplicitOutputAllocationBounds(string target, int value)
        => RejectBeforeDispatch(Session(target, value, diagnostic: true), target);

    [Fact]
    public void DiagnosticPolicyDoesNotCoerceMalformedRetainedTargetTypes()
    {
        var node = JsonSerializer.SerializeToNode(Session("calls", 1001, diagnostic: true), PlanningJsonContext.Default.PlanningSession)!;
        node["request"]!["maxModelCalls"] = "1001";
        Assert.Throws<JsonException>(() => node.Deserialize(PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task RejectBeforeDispatch(PlanningSession state, string target)
    {
        var runtime = new TestRuntime();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var attempt = () => new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        if (target is "calls" or "repairs" or "largeRepairs") await Assert.ThrowsAsync<ArgumentException>(attempt);
        else await Assert.ThrowsAsync<PlanningConflictException>(attempt);
        Assert.Empty(runtime.Calls);
        Assert.Empty(runtime.Checkpoints);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static PlanningSession Session(string target, int value, bool diagnostic)
    {
        var state = new PlanningSession { Request = new()
        {
            TenantId = "target-configuration", Prompt = "Return the required public numeric result named result with value 42.",
            MaxModelCalls = 64, MaxReplanAttempts = 4
        }, Construction = new() };
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        switch (target)
        {
            case "calls": state.Request.MaxModelCalls = value; break;
            case "repairs": state.Request.MaxReplanAttempts = value; break;
            case "largeRepairs": state.Request.Options["construction_preset"] = PlanningConstructionPresets.Large;
                state.Request.MaxReplanAttempts = value; break;
            case "tasks": state.Construction.Limits.MaxTasksPerDraft = value; break;
            case "checkpoints": state.Construction.Limits.MaxCheckpoints = value; break;
            case "draftCorrections": state.Construction.Limits.MaxCorrectionsPerDraft = value; break;
            case "nonproductive": state.Construction.Limits.MaxNonProductiveModelCalls = value; break;
            case "draftOutput" or "reviewOutput": break;
            default: throw new ArgumentException("Unknown fixture target.", nameof(target));
        }
        state = PlannerFixture.Capture(state);
        // Exercise malformed retained output allocations after normal phase capture.
        if (target == "draftOutput") state.Construction!.Limits.DraftOutputTokens = value;
        if (target == "reviewOutput") state.Construction!.Limits.ReviewOutputTokens = value;
        state.Request.Options["construction_limits"] = JsonSerializer.SerializeToNode(state.Construction!.Limits,
            PlanningJsonContext.Default.PlanningConstructionOptions);
        return state;
    }
}
