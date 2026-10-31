using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessBlockGranularityTests
{
    [Fact]
    public async Task CompletedProductiveFragmentIsReviewedWhileItsOriginalRequirementRemainsOpen()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Equal("in_progress", state.Construction!.Requirements.Single(requirement => requirement.Id == "work").Status);
        Assert.False(Assert.Single(state.Construction.Checkpoints).Fragment["businessReviewPending"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ReviewedContributionCanRequestFinalReviewWithoutAnotherBusinessBlock()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Requirements!.Outcomes.RemoveAll(requirement => requirement.Id != "work");
        state.Construction!.Requirements.RemoveAll(requirement => requirement.Id != "work");
        state.Construction.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var blocks = state.Construction!.Compact!.Blocks!.Count;
        Assert.Equal("in_progress", Assert.Single(state.Construction.Requirements).Status);
        Assert.NotNull(PlanningCompactConstruction.SelectionSchema(state)["properties"]!["completionRequirementIds"]);
        runtime.Calls.Clear();
        var purposes = new List<string>();
        runtime.Respond = (_, purpose) =>
        {
            purposes.Add(purpose);
            return purpose switch
            {
                PlanningCompactConstruction.SelectPurpose => new() { Json = new JsonObject
                    { ["block"] = null, ["completionRequirementIds"] = new JsonArray("work"), ["evidenceRequests"] = null, ["clarifications"] = null } },
                PlanningConstruction.GlobalPurpose => new() { Json = BusinessReviewProtocolTests.Report("ok") },
                _ => throw new InvalidOperationException("Finalizing accepted work needs no implementation or draft call.")
            };
        };
        for (var transition = 0; transition < 6 && state.Status != PlanningStatus.FinalReview; transition++)
        {
            state = await BusinessReviewProtocolTests.Advance(state, runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
            Assert.Equal(blocks, state.Construction!.Compact!.Blocks!.Count);
        }
        Assert.Equal(new[] { PlanningCompactConstruction.SelectPurpose, PlanningConstruction.GlobalPurpose }, purposes);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.True(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task InterfaceOnlyClosureIsTechnicalPendingAndHasNoBusinessAuthority()
    {
        var (state, runtime) = await BusinessProjectionDeclarationsTests.Ready();
        state.Construction!.Candidate!.Extension.CompleteBlock = false;
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
    public async Task SeveralTechnicalFragmentsShareOneBusinessBlockAndOneGlobalReview()
    {
        var (state, runtime) = await BusinessProjectionDeclarationsTests.Ready(numeric: true, combined: true);
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
        state.Construction!.Candidate!.Extension.CompleteBlock = false;
        var local = 0; var global = 0;
        runtime.Respond = (request, purpose) =>
        {
            var context = BusinessProjectionDeclarationsTests.Context(request);
            Assert.Equal("request", Assert.Single(context["inputs"]!.AsArray())!["name"]!.ToString());
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                local++;
                Assert.Equal(new[] { runtime.EvidenceId(request, "task:first"), runtime.EvidenceId(request, "task:second") }, context["actions"]!.AsArray()
                    .Where(action => action!["kind"]!.ToString() == "value").Select(action => action!["id"]!.ToString()));
            }
            else { Assert.Equal(PlanningConstruction.GlobalPurpose, purpose); global++; }
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        state = await Add(new() { Scope = "/root", RequirementIds = ["later"], ContinueInScope = "/tasks/block/body", Tasks =
            [new() { Id = "block", Kind = "sequence", Objective = "Compute the reusable local result.", Body = new() { Tasks =
                [new() { Id = "first", Kind = "value", Objective = "Double the caller number.", Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply",
                    Items = [new() { Kind = "input", Source = "request" }, new() { Kind = "number", Number = 2 }] })] }] } }] }, "sequence", "/root");
        state = await Add(new() { Scope = "/tasks/block/body", RequirementIds = ["later"], ContinueInScope = "/tasks/block/body", Tasks =
            [new() { Id = "second", Kind = "value", Objective = "Add one to the doubled result.", Outputs = [new("result", new() { Kind = "arithmetic", Text = "add",
                Items = [new() { Kind = "output", Source = "first", Port = "result" }, new() { Kind = "number", Number = 1 }] })] }] }, "value", "/tasks/block/body");
        Assert.Empty(runtime.Calls);
        state = await Add(new() { Scope = "/tasks/block/body", RequirementIds = ["later"], CloseScopes = ["/tasks/block/body"],
            Outputs = [new("result", new() { Kind = "output", Source = "second", Port = "result" })] }, "close", "/tasks/block/body");
        Assert.Equal(0, local); Assert.Equal(0, global);
        Assert.True(state.Construction!.Checkpoints[0].Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, state.Construction.Checkpoints[0]));
        state = await Add(new() { Scope = "/root", RequirementIds = ["later"], CloseScopes = ["/root"], Complete = true,
            Outputs = [new("result", new() { Kind = "output", Source = "block", Port = "result" })] }, "close", "/root", complete: true);
        Assert.Equal(0, local); Assert.Equal(1, global); Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.All(state.Construction!.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.True(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        PlanningArtifactApproval.Verify(state);

        async Task<PlanningSession> Add(PlanningConstructionExtension extension, string kind, string scope, bool complete = false)
        {
            state.Construction!.Compact!.CurrentStep ??= new() { BlockId = state.Construction.Compact.CurrentBlockId, Id = "step_" + state.Construction.Checkpoints.Count, Scope = scope, Kind = kind,
                Objective = "Build the next actual computation or close its existing boundary.", RequirementIds = ["later"],
                CloseRequirementIds = complete ? ["work", "later"] : [], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
            extension.CompleteBlock = complete;
            if (complete) extension.RequirementIds = ["work", "later"];
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
        candidate.Extension.CompleteBlock = false;
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
