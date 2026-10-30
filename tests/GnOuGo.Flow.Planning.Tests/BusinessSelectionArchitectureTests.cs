using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessSelectionArchitectureTests
{
    [Fact]
    public async Task BusinessSelectionHasOnlyBusinessInputsAndDecisions()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep = null;
        state.Construction.Compact.CurrentBlockId = null;
        state.Construction.Compact.Blocks!.Clear();
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal(new[] { "availableResults", "evidence", "globalConstraints", "remainingObligations", "requirements" },
            context.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var block = PlanningCompactConstruction.SelectionSchema(state)["properties"]!["block"]!["anyOf"]![0]!["properties"]!.AsObject();
        Assert.Equal(new[] { "dependencies", "expectedResults", "informationNeeds", "objective", "requirementIds", "successCriteria" },
            block.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        foreach (var key in new[] { "scope", "kind", "operationIds", "inputs", "outputNames", "closeScopes", "maxTasksPerDraft", "baseHash", "step" })
        {
            Assert.False(context.ContainsKey(key));
            Assert.False(block.ContainsKey(key));
        }
    }

    [Fact]
    public async Task NoRemainingBusinessObligationOffersNoInventedBlockOrEmptyEnum()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        foreach (var requirement in state.Construction!.Requirements) requirement.Status = "closed";
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal("null", schema["properties"]!["block"]!["type"]!.ToString());
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.DoesNotContain("\"enum\":[]", schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullTechnicalContinuationCannotCompleteTheBusinessBlock()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var block = PlanningCompactConstruction.CurrentBlock(state)!;
        var original = JsonSerializer.Serialize(block, PlanningJsonContext.Default.PlanningConstructionBlock);
        state.Construction!.Candidate!.Extension.CompleteBlock = false;
        Assert.False(HybridWorkflowPlanner.NeedsBusinessReview(state, state.Construction.Candidate));
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Empty(runtime.Calls);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.Equal(block.Id, state.Construction.Compact.CurrentBlockId);
        Assert.Equal(original, JsonSerializer.Serialize(PlanningCompactConstruction.CurrentBlock(state), PlanningJsonContext.Default.PlanningConstructionBlock));
        Assert.Equal("in_progress", state.Construction.Requirements.Single(item => item.Id == "work").Status);
        Assert.Empty(Assert.Single(state.Construction.Checkpoints).ClosedRequirementIds!);
        var restored = PlannerFixture.Clone(state);
        PlanningCompactConstruction.ValidateBusinessContract(restored);
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.ImplementationContext(state), PlanningCompactConstruction.ImplementationContext(restored)));
    }

    [Fact]
    public async Task CompleteBlockRequiresBusinessReviewAndDischargesOnlyThatBlock()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var id = state.Construction!.Compact!.CurrentBlockId;
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, state.Construction.Candidate!));
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Null(state.Construction!.Compact!.CurrentBlockId);
        Assert.Equal(1, state.Construction.Compact.Blocks!.Single(block => block.Id == id).CompletionCheckpoint);
        Assert.Equal("closed", state.Construction.Requirements.Single(item => item.Id == "work").Status);
        Assert.Equal("remaining", state.Construction.Requirements.Single(item => item.Id == "later").Status);
        Assert.True(HybridWorkflowPlanner.ReviewedCheckpoint(state, state.Construction.Checkpoints[0]) is not null);
    }

    [Fact]
    public async Task FinalBusinessClaimUsesOneGlobalReviewAfterMechanicalClosure()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var compact = state.Construction!.Compact!;
        compact.Blocks![0].RequirementIds.Add("later");
        compact.CurrentStep!.RequirementIds.Add("later");
        compact.CurrentStep.CloseRequirementIds.Add("later");
        state.Construction.Candidate!.Extension.RequirementIds.Add("later");
        Assert.False(HybridWorkflowPlanner.NeedsBusinessReview(state, state.Construction.Candidate));
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Empty(runtime.Calls);
        Assert.NotNull(state.Construction!.Compact!.CurrentBlockId);
        Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Candidate!.Extension.Complete);
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.True(state.Construction!.Complete);
        Assert.Null(state.Construction.Compact!.CurrentBlockId);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ChangedBusinessSuccessCriteriaInvalidateRetainedReview()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        state.Construction.Compact!.Blocks![0].SuccessCriteria.Add("A different result is required");
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
    }

    [Fact]
    public async Task NextBusinessSelectionUsesIssuedResultReferencesAndRetainsCanonicalDependency()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var canonical = "result:" + state.Construction!.Compact!.Blocks![0].Id;
        string? issued = null;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var context = BusinessProjectionDeclarationsTests.Context(request);
            issued = context["availableResults"]![0]!["id"]!.ToString();
            Assert.NotEqual(canonical, issued);
            Assert.DoesNotContain(canonical, request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(canonical, request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
            return new() { Json = new JsonObject { ["block"] = new JsonObject
            {
                ["objective"] = "Produce the later independent result", ["requirementIds"] = new JsonArray("later"),
                ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The later result"),
                ["dependencies"] = new JsonArray(new JsonObject { ["resultId"] = issued, ["reason"] = "Preserve the earlier result" }),
                ["successCriteria"] = new JsonArray("The later result is present")
            }, ["evidenceRequests"] = null, ["clarifications"] = null } };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(canonical, Assert.Single(PlanningCompactConstruction.CurrentBlock(state)!.Dependencies).ResultId);
        var saved = runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == PlanningCompactConstruction.SelectPurpose).PendingCall!;
        PlanningModelCalls.ValidateEvidenceAddresses(saved);
        Assert.Equal(canonical, saved.EvidenceAddresses!.Addresses[issued!]);
        Assert.Throws<PlanningConflictException>(() => PlanningModelCalls.NormalizeEvidenceResponse(new JsonObject
            { ["block"] = new JsonObject { ["dependencies"] = new JsonArray(new JsonObject { ["resultId"] = canonical }) } }, saved.EvidenceAddresses));
    }

    [Fact]
    public async Task ScopedRepairWithdrawsAffectedBusinessResultReviewButPreservesIndependentBlock()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = "Contribute the independent later result", ["requirementIds"] = new JsonArray("later"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("An independent result"),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("Independent result is present")
        });
        state.Construction!.Compact!.CurrentStep = new() { BlockId = state.Construction.Compact.CurrentBlockId, Id = "independent",
            BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "value", RequirementIds = ["later"], DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"], CompleteBlock = true,
            Tasks = [new() { Id = "independent", Kind = "value", Outputs = [new("result", new() { Kind = "number", Number = 7 })] }] });
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var compact = state.Construction!.Compact!;
        var before = PlanningCompactConstruction.SelectionContext(state)["availableResults"]!.AsArray();
        Assert.All(before, result => Assert.Equal("business_completion_reviewed", result!["reviewStatus"]!.ToString()));
        var revised = PlanningConstruction.Clone(state.Plan!);
        revised.Root.Tasks[0].Outputs[0] = new("result", new() { Kind = "number", Number = 99 });
        state.Construction.Candidate = new() { Plan = revised, BaseHash = PlanningConstruction.Hash(state.Plan), Hash = PlanningConstruction.Hash(revised),
            AuthorizedPaths = ["/tasks/compute/outputs/result"] };
        PlanningConstruction.InvalidateRevisedCheckpoints(state);
        var after = PlanningCompactConstruction.SelectionContext(state)["availableResults"]!.AsArray();
        Assert.DoesNotContain(after, result => result!["id"]!.ToString() == "result:" + compact.Blocks![0].Id &&
            result["reviewStatus"]!.ToString() == "business_completion_reviewed");
        Assert.Equal("business_completion_reviewed", after.Single(result => result!["id"]!.ToString() == "result:" + compact.Blocks![1].Id)!["reviewStatus"]!.ToString());
    }

    [Fact]
    public async Task LaterImplementationCursorCanContinueInItsOwnPreexistingScope()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Root.Tasks = [new() { Id = "a", Kind = "sequence", Body = new() }, new() { Id = "b", Kind = "sequence", Body = new() }];
        state.Construction.OpenScopes = ["/root", "/tasks/a/body", "/tasks/b/body"];
        var compact = state.Construction.Compact!;
        compact.CurrentStep!.Scope = "/tasks/a/body";
        compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/tasks/a/body", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "first", Kind = "value", Outputs = [new("result", new() { Kind = "number", Number = 3 })] }] });
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        compact = state.Construction!.Compact!;
        var blockId = compact.CurrentBlockId;
        compact.CurrentStep = new() { Id = "second_cursor", BlockId = blockId, Scope = "/tasks/b/body", Kind = "value",
            RequirementIds = ["work"], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/tasks/b/body", RequirementIds = ["work"], CompleteBlock = false,
            ContinueInScope = "/tasks/b/body", Tasks = [new() { Id = "second", Kind = "value", Outputs = [new("result", new() { Kind = "number", Number = 4 })] }] });
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(blockId, state.Construction!.Compact!.CurrentBlockId);
        Assert.Equal("/tasks/b/body", state.Construction.Compact.CurrentStep!.Scope);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task CursorCannotBorrowAnotherBlockIdentity()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Compact!.CurrentStep!.BlockId = "other";
        Assert.Throws<PlanningConflictException>(() => PlanningCompactConstruction.ValidateBusinessContract(state));
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate!));
    }

    [Fact]
    public async Task CompletedClaimCannotRetainAnImplementationContinuation()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate!.Extension.ContinueInScope = "/root";
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldContractIsRejectedBeforeMutationOrDispatch(bool issued)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Request.Options.Remove(PlanningCompactConstruction.BusinessSelectionContract);
        state.Construction!.Compact!.Blocks = null;
        state.Construction.Compact.CurrentBlockId = null;
        state.Construction.Compact.CurrentStep!.BlockId = null;
        if (issued) state.PendingCall = new() { Id = "retained", Purpose = PlanningCompactConstruction.SelectPurpose };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<PlanningConflictException>(() => BusinessReviewProtocolTests.Advance(state, runtime));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls);
        Assert.Empty(runtime.Checkpoints);
        Assert.Equal(11, state.SchemaVersion);
    }

    [Fact]
    public async Task CapturedBusinessDecisionAndImplementationGrammarSurviveRestart()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep = null;
        var restored = PlannerFixture.Clone(state);
        Assert.Equal(PlanningCompactConstruction.BusinessBlockContext(PlanningCompactConstruction.CurrentBlock(state)!).ToJsonString(),
            PlanningCompactConstruction.BusinessBlockContext(PlanningCompactConstruction.CurrentBlock(restored)!).ToJsonString());
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.ImplementationSchema(state), PlanningCompactConstruction.ImplementationSchema(restored)));
        var text = PlanningCompactConstruction.ImplementationSchema(restored).ToJsonString();
        Assert.DoesNotContain("\"expectedOutputs\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"objective\"", text, StringComparison.Ordinal);
    }
}
