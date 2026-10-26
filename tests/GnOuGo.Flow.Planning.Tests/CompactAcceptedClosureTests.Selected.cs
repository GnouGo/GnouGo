using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactAcceptedClosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitClosurePreparesUnchangedWorkButRequiresItsOwnReview(bool closeRoot)
    {
        var state = await SelectedClosure(closeRoot);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
        var probe = Assert.IsType<PlanningSession>(PlanningCompactConstruction.PrepareSelectedClosure(state));
        var candidate = probe.Construction!.Candidate!;
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(PlanningConstruction.Hash(state.Plan), candidate.Hash);
        Assert.Empty(candidate.TaskIds); Assert.Empty(candidate.ImportedTaskIds);
        Assert.Empty(candidate.Extension.Tasks); Assert.Empty(candidate.Extension.Outputs);
        Assert.Equal(closeRoot, candidate.Extension.Complete);
        Assert.Equal(closeRoot ? new[] { "/root" } : [], candidate.Extension.CloseScopes);
        Assert.False(candidate.LocalReviewed); Assert.False(candidate.IntermediateReviewed);
        Assert.Empty(candidate.Reviews);
        Assert.False(PlanningConstructionReviewCoverage.Complete(probe, candidate));
        Assert.Equal("in_progress", Assert.Single(probe.Construction.Requirements).Status);
        Assert.Single(probe.Construction.Checkpoints);

        // The same coordinator path validates and reviews the structural candidate;
        // it does not dispatch selection or manufacture a construction receipt.
        var runtime = new TestRuntime { Respond = (_, purpose) =>
        {
            Assert.Contains(purpose, new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose });
            return new() { Json = new JsonObject
            {
                ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
                ["uncertainty"] = "No execution observed."
            } };
        } };
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.NotNull(state.Construction!.Candidate);
        for (var attempt = 0; attempt < 5 && state.Construction!.Candidate is not null; attempt++)
            state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.NotEmpty(runtime.Calls); Assert.Empty(state.Diagnostics);
        Assert.Equal(closeRoot, state.Construction.Complete);
        Assert.Equal(closeRoot ? Array.Empty<string>() : ["/root"], state.Construction.OpenScopes);
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.Equal(candidate.Hash, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Plan!.Root.Tasks); Assert.Single(state.Plan.Root.Outputs);
        Assert.Equal(2, state.Construction.Checkpoints.Count);
        Assert.Null(state.ApprovedHash);
        if (!closeRoot)
        {
            var reviewed = Assert.IsType<PlanningSession>(PlanningCompactConstruction.PrepareReviewedClosure(state));
            Assert.Equal(new[] { "/root" }, reviewed.Construction!.Compact!.CurrentStep!.CloseScopes);
            Assert.True(reviewed.Construction.Candidate!.Extension.Complete);
            Assert.False(reviewed.Construction.Candidate.LocalReviewed);
        }
    }

    [Theory]
    [InlineData("input")]
    [InlineData("dependency")]
    [InlineData("expected_work")]
    [InlineData("missing_output")]
    [InlineData("required_diagnostic")]
    [InlineData("repair_authority")]
    [InlineData("stale_base")]
    [InlineData("continuation")]
    public async Task SelectedClosureDoesNotGuessBindingsOrBypassRepair(string reason)
    {
        var state = await SelectedClosure(true);
        var step = state.Construction!.Compact!.CurrentStep!;
        switch (reason)
        {
            case "input": step.Inputs.Add(new() { Kind = "output", Source = "greet", Port = "message" }); break;
            case "dependency": step.DependencyTaskIds.Add("greet"); break;
            case "expected_work": step.ExpectedOutputs.Add("A new local result."); break;
            case "missing_output": state.Plan!.Root.Outputs.Clear(); step.BaseHash = PlanningConstruction.Hash(state.Plan); break;
            case "required_diagnostic": state.Diagnostics.Add(new("NEEDS_REPAIR", "/root", "Repair the retained defect.")); break;
            case "repair_authority": state.EditablePaths = ["/tasks/greet/outputs"]; break;
            case "stale_base": step.BaseHash = "stale"; break;
            case "continuation": step.ContinuationBinding = new() { ContinuationId = "waiting", Binding = new() { Kind = "output", Source = "greet", Port = "message" } }; break;
        }
        var exact = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.PrepareSelectedClosure(state));
        Assert.Equal(exact, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("foreign_scope")]
    [InlineData("duplicate_scope")]
    [InlineData("non_close")]
    [InlineData("open_child")]
    [InlineData("unclaimed_requirement")]
    public async Task ExplicitScopeClosureRetainsExistingScopeAndFullRequirementConditions(string invalid)
    {
        var state = await SelectedClosure(true);
        var step = state.Construction!.Compact!.CurrentStep!;
        switch (invalid)
        {
            case "null": step.CloseScopes = null; break;
            case "foreign_scope": step.CloseScopes = ["/tasks/another/body"]; break;
            case "duplicate_scope": step.CloseScopes = ["/root", "/root"]; break;
            case "non_close": step.Kind = "value"; break;
            case "open_child":
                state.Plan!.Root.Tasks.Add(new() { Id = "child", Kind = "sequence", Body = new() });
                state.Construction.OpenScopes.Add("/tasks/child/body"); break;
            case "unclaimed_requirement": state.Construction.Requirements.Add(new() { Id = "other", Status = "remaining" }); break;
        }
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelectedCloseScopes(state, step));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelDraftCannotChangeExplicitClosureClaim(bool closeRoot)
    {
        var state = await SelectedClosure(closeRoot);
        var candidate = PlanningCompactConstruction.PrepareSelectedClosure(state)!.Construction!.Candidate!;
        candidate.Extension.CloseScopes = closeRoot ? [] : ["/root"];
        candidate.Extension.Complete = !closeRoot;
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task HistoricalSelectionKeepsItsPlanAndRequirementPresentation(int version)
    {
        var state = await SelectedClosure(true);
        state.Construction!.Compact!.PresentationVersion = version;
        state.Request.Options["construction_presentation_version"] = version;
        state.Construction.Compact.CurrentStep!.CloseScopes = null;
        var exact = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.PrepareSelectedClosure(state));
        Assert.All(PlanningCompactConstruction.OriginalRequirementRows(state), row => Assert.Null(row!["acceptedProgress"]));
        Assert.Equal(exact, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task IssuedClosureRequestRecoveryPreservesThePreparedEnvelope(int version)
    {
        var state = await SelectedClosure(true);
        state.Construction!.Compact!.PresentationVersion = version;
        state.Request.Options["construction_presentation_version"] = version;
        if (version == 6) state.Construction.Compact.CurrentStep!.CloseScopes = null;
        var proposal = PlanningConstruction.DraftRequest(state, false);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, proposal.Prompt,
            proposal.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        Assert.False(string.IsNullOrEmpty(issued.ClientRequestId)); Assert.True(issued.MaxTokens > 0);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.DraftPurpose, Request = issued };
        state.ModelCalls = 1; state = PlannerFixture.Clone(state);
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        Assert.Null(PlanningCompactConstruction.PrepareSelectedClosure(state));
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = PlanningCorpus.Transport(new JsonObject
            {
                ["baseHash"] = PlanningConstruction.Hash(state.Plan),
                ["extension"] = PlanningConstruction.Node(new PlanningConstructionExtension
                {
                    Scope = "/root", CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"]
                }, PlanningJsonContext.Default.PlanningConstructionExtension)
            }, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        } };
        await PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.DraftPurpose,
            "A later unissued prompt.", new JsonObject { ["type"] = "object" }, PlannerFixture.Ct, 1);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Null(state.PendingCall);
    }

    [Fact]
    public async Task AcceptedProgressJoinsCurrentContributionsWithoutClaimingFulfillment()
    {
        var state = await SelectedClosure(false);
        var checkpoint = state.Construction!.Checkpoints[0];
        checkpoint.Fragment = new()
        {
            ["scope"] = "/root",
            ["outputs"] = PlanningConstruction.Node(state.Plan!.Root.Outputs, PlanningJsonContext.Default.ListTaskOutput)
        };
        checkpoint.TaskIds.Add("invalidated"); checkpoint.InvalidatedTaskIds.Add("invalidated");
        state.Plan.Root.Tasks.Add(new() { Id = "invalidated", Kind = "value", Outputs = [new("removed", new() { Kind = "number", Number = 0 })] });
        checkpoint.TaskIds.Add("missing");
        var tentative = PlanningConstruction.Clone(state.Plan);
        tentative.Root.Tasks.Add(new() { Id = "tentative", Kind = "value", Outputs = [new("new", new() { Kind = "number", Number = 1 })] });
        state.Construction.Candidate = new() { Plan = tentative };
        var exact = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var row = Assert.Single(PlanningCompactConstruction.OriginalRequirementRows(state))!;
        Assert.Equal("in_progress", row["status"]!.ToString());
        var item = Assert.Single(row["acceptedProgress"]!["items"]!.AsArray())!;
        Assert.True(item["scopeOpen"]!.GetValue<bool>());
        var task = Assert.Single(item["tasks"]!.AsArray())!;
        Assert.Equal("greet", task["task"]!.ToString());
        Assert.Equal("message", Assert.Single(task["ports"]!.AsArray())!.ToString());
        Assert.Equal("message", Assert.Single(item["outputs"]!.AsArray())!["name"]!.ToString());
        Assert.Contains("task:greet", PlanningCompactConstruction.NavigationEvidence([row["acceptedProgress"]]));
        Assert.Contains("page:checkpoints:0", PlanningCompactConstruction.NavigationEvidence([row["acceptedProgress"]]));
        Assert.Equal(exact, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task AcceptedProgressRetainsOpenResultBoundariesAndPagedCheckpointEvidence()
    {
        var state = await SelectedClosure(false);
        state.Plan!.Root.Tasks.Add(new() { Id = "loop", Kind = "foreach", Items = new() { Kind = "array" },
            Body = new() { Outputs = [new("scalar", new() { Kind = "number", Number = 2 })] } });
        state.Construction!.OpenScopes.Add("/tasks/loop/body");
        for (var i = 2; i <= 6; i++)
        {
            state.Construction.Checkpoints.Add(new() { Sequence = i, TaskIds = ["loop"], RequirementIds = ["greeting"],
                Step = new() { Scope = "/root" } });
            state.Construction.Requirements[0].Checkpoints.Add(i);
        }
        var progress = PlanningCompactConstruction.OriginalRequirementRows(state)[0]!["acceptedProgress"]!;
        Assert.Equal(6, progress["checkpointCount"]!.GetValue<int>());
        Assert.Equal("page:checkpoints:0", progress["directory"]!.ToString());
        Assert.Equal(4, progress["items"]!.AsArray().Count);
        Assert.All(progress["items"]!.AsArray(), item => Assert.Equal(1,
            Assert.Single(item!["tasks"]!.AsArray())!["openResultScopeCount"]!.GetValue<int>()));
        var context = PlanningCompactConstruction.SelectionContext(state);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
        var links = PlanningCompactConstruction.NavigationEvidence([progress]);
        foreach (var link in links) Assert.Contains(link, schema["properties"]!["evidenceRequests"]!.ToJsonString(), StringComparison.Ordinal);
    }

    private static async Task<PlanningSession> SelectedClosure(bool closeRoot)
    {
        var state = await State();
        state.Request.Options["construction_presentation_version"] = 7;
        state.Construction!.Compact!.PresentationVersion = 7;
        state.Construction.Requirements[0].Status = "in_progress";
        state.Construction.Checkpoints[0].ClosedRequirementIds = [];
        state.Construction.Checkpoints[0].Reviews.Clear();
        state.Construction.Compact.CurrentStep = new()
        {
            Id = "step_2", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "close",
            Objective = "Close the explicit greeting obligation using its accepted public binding.",
            RequirementIds = ["greeting"], CloseRequirementIds = ["greeting"], CloseScopes = closeRoot ? ["/root"] : [],
            DiscoveryComplete = true
        };
        state.Construction.Compact.NextStep = 2;
        return state;
    }
}
