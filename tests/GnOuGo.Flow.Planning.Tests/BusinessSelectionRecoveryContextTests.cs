using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessSelectionRecoveryContextTests
{
    [Fact]
    public async Task SuspendedNeedReachesBusinessSelectionWithoutItsTechnicalCursorOrNewAuthority()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var compact = state.Construction!.Compact!;
        compact.CurrentStep = new() { Id = "completion", Scope = "/root", Kind = "close", RequirementIds = ["later"],
            Objective = "Complete the original deliverable", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        const string need = "The accepted contribution does not yet provide the remaining requested deliverable.";
        state.Diagnostics = [new("REQUIREMENT_OPERATION_MISSING", "/requirements/outcomes/1", "The accepted contribution has no reachable use.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = "Technical rejection details stay in implementation context.", Disposition = "suspend",
            Prerequisite = new() { Need = need, ExpectedType = new() { Kind = "any" }, EvidenceAddresses = ["source:request:0:clause:1"] } });
        state.Diagnostics.Clear();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.NotNull(context["unresolvedNeeds"]);
        var note = Assert.Single(context["unresolvedNeeds"]!.AsArray())!;
        Assert.Equal(need, note["need"]!.ToString());
        Assert.Equal("later", Assert.Single(note["requirementIds"]!.AsArray())!.ToString());
        Assert.Equal("model_authored_prerequisite", note["basis"]!.ToString());
        foreach (var key in new[] { "scope", "kind", "consumer", "expectedType", "operationIds", "binding", "rejectionReason" })
            Assert.Null(note[key]);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var planHash = PlanningConstruction.Hash(state.Plan);
        var accepted = JsonSerializer.Serialize(state.Construction.Checkpoints[0], PlanningJsonContext.Default.PlanningConstructionCheckpoint);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            Assert.Equal(need, Context(request)["unresolvedNeeds"]![0]!["need"]!.ToString());
            return new() { Json = Block("later", runtime.EvidenceId(request, "result:block_1")) };
        };
        var next = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(next.Diagnostics);
        Assert.Equal(planHash, PlanningConstruction.Hash(next.Plan));
        Assert.Equal(accepted, JsonSerializer.Serialize(next.Construction!.Checkpoints[0], PlanningJsonContext.Default.PlanningConstructionCheckpoint));
        Assert.Equal("later", Assert.Single(PlanningCompactConstruction.CurrentBlock(next)!.RequirementIds));
        var retained = PlannerFixture.Clone(state);
        retained.Construction!.Compact!.SuspendedSteps![0].Status = "completed";
        Assert.Null(PlanningCompactConstruction.SelectionContext(retained)["unresolvedNeeds"]);
        retained = PlannerFixture.Clone(state);
        retained.Construction!.Requirements.Single(requirement => requirement.Id == "later").Status = "closed";
        Assert.Null(PlanningCompactConstruction.SelectionContext(retained)["unresolvedNeeds"]);
        retained.Diagnostics = [new("TASK_INPUT_TYPE", "/tasks/private", "Technical details stay with implementation."),
            new("PROPOSAL_ACTION_INVALID", "/", "An optional historical note.", Required: false)];
        Assert.Null(PlanningCompactConstruction.SelectionContext(retained)["actionFindings"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSelectionActionIsExplainedOnRetryWithoutResettingTheRepeatGuard(bool repeat)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep = null;
        state.Construction.Compact.CurrentBlockId = null;
        state.Construction.Compact.Blocks!.Clear();
        var planHash = PlanningConstruction.Hash(state.Plan);
        var calls = 0;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            if (++calls == 1) Assert.Null(Context(request)["actionFindings"]);
            else
            {
                Assert.NotNull(Context(request)["actionFindings"]);
                var finding = Assert.Single(Context(request)["actionFindings"]!.AsArray())!;
                Assert.Equal("PROPOSAL_ACTION_INVALID", finding["code"]!.ToString());
                Assert.Equal("Return exactly one business-selection action.", finding["message"]!.ToString());
            }
            return new() { Json = calls == 1 || repeat ? new() { ["block"] = null, ["evidenceRequests"] = null, ["clarifications"] = null } : Block("work") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, finding => finding.Code == "PROPOSAL_ACTION_INVALID");
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(2, calls);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(planHash, PlanningConstruction.Hash(state.Plan));
        if (repeat)
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Contains(state.Diagnostics, finding => finding.Code == "REPLAN_NO_PROGRESS");
        }
        else
        {
            Assert.Empty(state.Diagnostics);
            Assert.Equal("work", Assert.Single(PlanningCompactConstruction.CurrentBlock(state)!.RequirementIds));
            Assert.Null(PlanningCompactConstruction.SelectionContext(state)["actionFindings"]);
        }
    }

    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();

    private static JsonObject Block(string requirement, string? dependency = null) => new()
    {
        ["block"] = new JsonObject { ["objective"] = "Deliver the remaining original result", ["requirementIds"] = new JsonArray(requirement),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The requested result"),
            ["dependencies"] = dependency is null ? new JsonArray() : new JsonArray(new JsonObject { ["resultId"] = dependency, ["reason"] = "Reuse the accepted contribution" }),
            ["successCriteria"] = new JsonArray("The requested result is available") },
        ["evidenceRequests"] = null, ["clarifications"] = null
    };
}
