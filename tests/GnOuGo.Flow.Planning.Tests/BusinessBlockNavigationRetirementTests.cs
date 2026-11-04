using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessBlockNavigationRetirementTests
{
    [Fact]
    public async Task ReviewedReplacementAbandonsEmptyNavigationWithoutFulfillingItsPrerequisite()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        Suspend(state, "first", new() { Kind = "object", Fields = [new() { Name = "contract", Type = new() { Kind = "string" } }] });
        Suspend(state, "second", new() { Kind = "any" });
        var originals = state.Construction.Compact!.SuspendedSteps!.Select(Serialize).ToArray();
        var rejected = state.Construction.Compact.RejectedSteps.Select(item => item.ToJsonString()).ToArray();
        PrepareReplacement(state);
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };

        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);

        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Compact!.CurrentBlockId);
        Assert.All(state.Construction.Compact.SuspendedSteps!, item => Assert.Equal("abandoned", item.Status));
        Assert.Equal(originals, state.Construction.Compact.SuspendedSteps!.Select(item =>
        {
            var copy = JsonSerializer.Deserialize(Serialize(item), PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!;
            copy.Status = "waiting";
            return Serialize(copy);
        }));
        Assert.Equal(rejected, state.Construction.Compact.RejectedSteps.Select(item => item.ToJsonString()));
        Assert.Equal("in_progress", state.Construction.Requirements.Single(item => item.Id == "work").Status);
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["unresolvedNeeds"]);
        Assert.Null(state.ApprovedHash);
        Assert.Single(state.Construction.Checkpoints);

        // The remaining original work, ordinary completion claim and mandatory
        // global review still run; retiring navigation is never approval.
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = "Supply the remaining original result", ["requirementIds"] = new JsonArray("later"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The later result"),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("The later result is supplied")
        });
        PrepareReplacement(state, "later", "later_value");
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(CompletionEligibilityTests.OffersCompletion(state));
        PlanningCompactConstruction.SelectCompletion(state, ["work", "later"]);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(item => item.Code + ":" + item.Message)));
        PlanningArtifactApproval.Verify(state);
        Assert.Null(state.ApprovedHash);
        Assert.All(PlannerFixture.Clone(state).Construction!.Compact!.SuspendedSteps!, item => Assert.Equal("abandoned", item.Status));
    }

    [Fact]
    public async Task AcceptedPartialConsumerAndItsEmptyAncestorRemainWaiting()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        Suspend(state, "ancestor", new() { Kind = "any" });
        state.Construction.Compact!.CurrentStep = Step(state, "partial");
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [Value("accepted_partial")]
        });
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        var acceptedTasks = checkpoint.Fragment["tasks"]!.ToJsonString();
        var acceptedStep = JsonSerializer.Serialize(checkpoint.Step, PlanningJsonContext.Default.PlanningConstructionStep);
        var acceptedHash = checkpoint.PlanHash;
        Suspend(state, "partial", new() { Kind = "any" });
        PrepareReplacement(state);
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };

        state = await BusinessReviewProtocolTests.Advance(state, runtime);

        Assert.Empty(state.Diagnostics);
        Assert.All(state.Construction!.Compact!.SuspendedSteps!, item => Assert.Equal("waiting", item.Status));
        Assert.Equal(acceptedTasks, state.Construction.Checkpoints[0].Fragment["tasks"]!.ToJsonString());
        Assert.Equal(acceptedStep, JsonSerializer.Serialize(state.Construction.Checkpoints[0].Step, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(acceptedHash, state.Construction.Checkpoints[0].PlanHash);
        Assert.Contains(state.Plan!.Root.Tasks, task => task.Id == "accepted_partial");
        Assert.NotNull(PlanningCompactConstruction.SelectionContext(state)["unresolvedNeeds"]);
        Assert.False(CompletionEligibilityTests.OffersCompletion(state));
    }

    [Theory]
    [InlineData("partial_block")]
    [InlineData("different_block")]
    [InlineData("cross_block_parent")]
    [InlineData("resuming")]
    public async Task NavigationOutsideReviewedEmptyReplacementAuthorityIsPreserved(string boundary)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        Suspend(state, "waiting", new() { Kind = "any" });
        var item = Assert.Single(state.Construction.Compact!.SuspendedSteps!);
        if (boundary == "different_block") item.Consumer.BlockId = "other_block";
        if (boundary == "cross_block_parent")
        {
            var parent = JsonSerializer.Deserialize(Serialize(item), PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!;
            parent.Id = "outside_parent"; parent.Consumer.BlockId = "other_block";
            item.ParentId = parent.Id;
            state.Construction.Compact.SuspendedSteps!.Insert(0, parent);
        }
        if (boundary == "resuming") item.Status = "resuming";
        PrepareReplacement(state);
        if (boundary == "partial_block")
        {
            state.Construction.Candidate!.Extension.CompleteBlock = false;
            state.Construction.Candidate.Extension.ContinueInScope = "/root";
        }
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };

        state = await BusinessReviewProtocolTests.Advance(state, runtime);

        Assert.Empty(state.Diagnostics);
        Assert.All(state.Construction!.Compact!.SuspendedSteps!, retained => Assert.Contains(retained.Status, new[] { "waiting", "resuming" }));
        Assert.Null(state.ApprovedHash);
    }

    private static void Suspend(PlanningSession state, string id, TaskType type)
    {
        state.Construction!.Compact!.CurrentStep = Step(state, id);
        state.Diagnostics = [new("TASK_OPERATION_MISSING", "/step", "The selected implementation needs a different strategy.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection
        {
            Reason = "This implementation cursor needs another strategy.", Disposition = "suspend",
            Prerequisite = new() { Need = "Retain the exact selected prerequisite for " + id + ".", ExpectedType = type,
                EvidenceAddresses = ["source:request:0:clause:0"] }
        });
        state.Diagnostics.Clear();
    }

    private static void PrepareReplacement(PlanningSession state, string requirement = "work", string task = "replacement_value")
    {
        state.Construction!.Compact!.CurrentStep = Step(state, task, requirement);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = [requirement], CompleteBlock = true, Tasks = [Value(task)]
        });
    }

    private static PlanningConstructionStep Step(PlanningSession state, string id, string requirement = "work") => new()
    {
        Id = id, BlockId = state.Construction!.Compact!.CurrentBlockId, Scope = "/root", Kind = "value",
        BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Provide the original numeric result.",
        RequirementIds = [requirement], CloseRequirementIds = [], DiscoveryComplete = true
    };

    private static PlanTask Value(string id) => new()
    {
        Id = id, Kind = "value", Objective = "Double the caller number.",
        Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "x" }, new() { Kind = "number", Number = 2 }] })]
    };

    private static string Serialize(PlanningConstructionSuspendedStep item) =>
        JsonSerializer.Serialize(item, PlanningJsonContext.Default.PlanningConstructionSuspendedStep);
}
