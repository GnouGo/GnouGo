using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
using static GnOuGo.Flow.Planning.Tests.BusinessReviewProtocolTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RejectedCompletionClaimTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalClaimCanBeWithdrawnToCheckAnAcceptedResult(bool consumesResult)
    {
        var (state, runtime) = await Ready();
        var con = state.Construction!;
        con.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        con.Candidate!.Extension.CompleteBlock = false;
        con.Candidate.Extension.ContinueInScope = "/root";
        state = await Advance(state, runtime);
        con = state.Construction!;
        var baseline = PlanningConstruction.Hash(state.Plan);
        con.Compact!.CurrentStep = new() { Id = "claim", BlockId = con.Compact.CurrentBlockId, Kind = "sequence",
            Scope = "/root", Objective = "Finish the result.", RequirementIds = ["work"], BaseHash = baseline, DiscoveryComplete = true };
        con.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true });
        PlanningCompactConstruction.StageCommitments(state, null, con.Candidate);
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change",
                ["reason"] = "Check the computed result before using it.", ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("compute") } } };
        };
        state = await Advance(state, runtime);
        Assert.NotEmpty(state.Construction!.Candidate!.AuthorizedPaths!);
        Assert.True(HybridWorkflowPlanner.CanWithdrawRejectedClaim(state));
        var explicitRevision = Clone(state);
        explicitRevision.EditablePaths = explicitRevision.Construction!.Candidate!.AuthorizedPaths;
        Assert.False(HybridWorkflowPlanner.CanWithdrawRejectedClaim(explicitRevision));
        var changedCandidate = Clone(state);
        changedCandidate.Construction!.Candidate!.Plan.Root.Tasks[0].Objective = "Changed implementation.";
        Assert.False(HybridWorkflowPlanner.CanWithdrawRejectedClaim(changedCandidate));
        var workflowClosure = Clone(state);
        workflowClosure.Construction!.Candidate!.Extension.Complete = true;
        Assert.False(HybridWorkflowPlanner.CanWithdrawRejectedClaim(workflowClosure));
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.Reselect(Clone(state),
            new PlanningConstructionReselection { Reason = "Keep the claim pending.", Disposition = "suspend",
                Prerequisite = new() { Need = "Another producer.", ExpectedType = new() { Kind = "number" } } }));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            var schema = request.StructuredOutputSchema!.AsObject();
            var property = schema["properties"]!["reselectStep"]!.ToJsonString();
            Assert.Contains("\"abandon\"", property);
            Assert.DoesNotContain("\"suspend\"", property);
            var answer = new JsonObject { ["reselectStep"] = new JsonObject { ["reason"] = "The check must run after its producer.",
                ["disposition"] = "abandon", ["prerequisite"] = null } };
            return new() { Json = PlanningCorpus.Transport(answer, schema, schema) };
        };
        state = await Advance(Clone(state), runtime);
        con = state.Construction!;
        Assert.Empty(state.Diagnostics);
        Assert.Null(con.Candidate);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(["work"], con.Compact!.ReplacementRequirements);
        Assert.Equal("/root", con.Compact.ReplacementScope);
        Assert.NotNull(Assert.Single(con.Compact.RejectedSteps)["reviews"]!.AsArray().Single(row => row!["outcome"]?.ToString() == "needs_change")!["defectWitness"]);
        con.Compact.CurrentStep = new() { Id = "guard", Kind = "value", Scope = "/root", BlockId = con.Compact.CurrentBlockId,
            Objective = "Check the result before returning it.", RequirementIds = ["work"], BaseHash = baseline, DiscoveryComplete = true,
            InputTaskIds = ["compute"], Inputs = [new() { Kind = "output", Source = "compute", Port = "result" }] };
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            TaskValue amount = new() { Kind = "output", Source = "compute", Port = "result" };
            var ext = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true,
                Tasks = [new() { Id = "checked", Kind = "value", Objective = "Retain the validated result.",
                    Requires = consumesResult ? new() { Kind = "predicate", Predicate = "equal", Items = [amount,
                        new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "x" }, new() { Kind = "number", Number = 2 }] }] }
                        : new() { Kind = "boolean", Boolean = true },
                    Outputs = [new("result", amount)] }] };
            var response = new JsonObject { ["baseHash"] = baseline, ["extension"] = JsonSerializer.SerializeToNode(ext, PlanningJsonContext.Default.PlanningConstructionExtension) };
            var schema = request.StructuredOutputSchema!.AsObject();
            return new() { Json = PlanningCorpus.Transport(response, schema, schema) };
        };
        state = await Advance(state, runtime);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        if (!consumesResult)
        {
            Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_DEFECT_UNCHANGED");
            return;
        }
        Assert.Empty(state.Diagnostics);
        Assert.False(state.Construction!.Candidate!.BusinessReviewed);
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = Report("ok") };
        };
        state = await Advance(state, runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(["compute", "checked"], state.Plan!.Root.Tasks.Select(task => task.Id));
    }
}
