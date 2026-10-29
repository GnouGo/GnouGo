using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessBlockGranularityTests
{
    [Fact]
    public async Task InterfaceOnlyClosureIsTechnicalPendingAndHasNoBusinessAuthority()
    {
        var (state, runtime) = await BusinessProjectionDeclarationsTests.Ready();
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (_, _) => throw new InvalidOperationException("A declaration-only technical checkpoint does not require a Judge.");
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Empty(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal("in_progress", state.Construction!.Requirements.Single(requirement => requirement.Id == "work").Status);
        var checkpoint = Assert.Single(state.Construction.Checkpoints);
        Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
    }

    [Fact]
    public async Task SeveralTechnicalFragmentsReceiveOneLocalBlockReviewThenOneGlobalReview()
    {
        var (state, runtime) = await BusinessProjectionDeclarationsTests.Ready(numeric: true);
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
        var local = 0; var global = 0;
        runtime.Respond = (request, purpose) =>
        {
            var context = BusinessProjectionDeclarationsTests.Context(request);
            Assert.Equal("request", Assert.Single(context["inputs"]!.AsArray())!["name"]!.ToString());
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                local++;
                Assert.Equal(new[] { "first", "second" }, context["actions"]!.AsArray()
                    .Where(action => action!["kind"]!.ToString() == "value").Select(action => action!["id"]!.ToString()));
            }
            else { Assert.Equal(PlanningConstruction.GlobalPurpose, purpose); global++; }
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        state = await Add(new() { Scope = "/root", RequirementIds = ["later"], Tasks =
            [new() { Id = "block", Kind = "sequence", Objective = "Compute the reusable local result.", Body = new() }] }, "sequence", "/root");
        state = await Add(new() { Scope = "/tasks/block/body", RequirementIds = ["later"], Tasks =
            [new() { Id = "first", Kind = "value", Objective = "Double the caller number.", Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply",
                Items = [new() { Kind = "input", Source = "request" }, new() { Kind = "number", Number = 2 }] })] }] }, "value", "/tasks/block/body");
        state = await Add(new() { Scope = "/tasks/block/body", RequirementIds = ["later"], Tasks =
            [new() { Id = "second", Kind = "value", Objective = "Add one to the doubled result.", Outputs = [new("result", new() { Kind = "arithmetic", Text = "add",
                Items = [new() { Kind = "output", Source = "first", Port = "result" }, new() { Kind = "number", Number = 1 }] })] }] }, "value", "/tasks/block/body");
        Assert.Empty(runtime.Calls);
        state = await Add(new() { Scope = "/tasks/block/body", RequirementIds = ["later"], CloseScopes = ["/tasks/block/body"],
            Outputs = [new("result", new() { Kind = "output", Source = "second", Port = "result" })] }, "close", "/tasks/block/body");
        Assert.Equal(1, local); Assert.Equal(0, global);
        Assert.True(state.Construction!.Checkpoints[0].Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, state.Construction.Checkpoints[0]));
        state = await Add(new() { Scope = "/root", RequirementIds = ["later"], CloseScopes = ["/root"], Complete = true,
            Outputs = [new("result", new() { Kind = "output", Source = "block", Port = "result" })] }, "close", "/root", complete: true);
        Assert.Equal(1, local); Assert.Equal(1, global); Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.All(state.Construction!.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.True(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        PlanningArtifactApproval.Verify(state);

        async Task<PlanningSession> Add(PlanningConstructionExtension extension, string kind, string scope, bool complete = false)
        {
            state.Construction!.Compact!.CurrentStep = new() { Id = "step_" + state.Construction.Checkpoints.Count, Scope = scope, Kind = kind,
                Objective = "Build the next actual computation or close its existing boundary.", RequirementIds = ["later"],
                CloseRequirementIds = complete ? ["later"] : [], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
            state.Construction.Candidate = PlanningConstruction.Apply(state, extension);
            var next = await BusinessReviewProtocolTests.Advance(state, runtime);
            Assert.Empty(next.Diagnostics);
            return next;
        }
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("parallel")]
    [InlineData("foreach")]
    [InlineData("conditional")]
    [InlineData("unused-group")]
    public async Task EmptyControlsAndUnusedDefinitionsDoNotBecomeCompletedBusinessBlocks(string kind)
    {
        var (state, _) = await BusinessProjectionDeclarationsTests.Ready();
        var candidate = state.Construction!.Candidate!;
        if (kind == "unused-group")
        {
            candidate.Plan.Groups.Add(new() { Id = "unused", Body = new() { Tasks = [new() { Id = "definition", Kind = "value",
                Objective = "Declared computation that has no caller.", Outputs = [new("result", new() { Kind = "number", Number = 2 })] }] } });
            candidate.TaskIds = ["definition"];
        }
        else
        {
            candidate.Plan.Root.Tasks.Add(new() { Id = "empty", Kind = kind, Objective = "Own future work.", Body = new() });
            candidate.TaskIds = ["empty"];
        }
        Assert.False(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
    }

}
