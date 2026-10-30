using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessRequirementCompletionTests
{
    [Fact]
    public async Task EmptyGroupOpeningRetainsUnreviewedClaimWithoutHidingOriginalWork()
    {
        var (state, runtime) = await Opening();
        state = await Advance(state, runtime);

        Assert.Empty(runtime.Calls);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(new[] { "work" }, checkpoint.ClosedRequirementIds);
        Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Empty(checkpoint.Reviews);
        Assert.Equal("in_progress", Requirement(state, "work").Status);
        Assert.Contains(checkpoint.Sequence, Requirement(state, "work").Checkpoints);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var original = Assert.Single(context["requirements"]!.AsArray(), item => item!["id"]!.ToString() == "work")!;
        Assert.Equal("Double the supplied number. ", original["original"]!.ToString());
        Assert.NotEqual("closed", original["status"]!.ToString());
        Assert.True(original["acceptedProgress"]!["completionClaimPendingReview"]!.GetValue<bool>());
        Assert.False(PlanningCompactConstruction.CompletionOnly(state));
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
    }

    [Fact]
    public async Task LocalOkCommitsOnlyItsExplicitClaimAndLeavesUncalledGroupClaimPending()
    {
        var (state, runtime) = await Opening(extraRequirement: true);
        state = await Advance(state, runtime);
        var opening = Assert.Single(state.Construction!.Checkpoints);
        var accepted = PlanningConstruction.Hash(state.Plan);
        state.Construction.Compact!.CurrentStep = new()
        {
            Id = "later-value", Scope = "/root", Kind = "value", BaseHash = accepted,
            Objective = "Compute the later independent number.", RequirementIds = ["later"], CloseRequirementIds = ["later"], DiscoveryComplete = true
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["later"], Tasks = [new()
            {
                Id = "later-value", Kind = "value", Objective = "Compute the later independent number.",
                Outputs = [new("number", new() { Kind = "number", Number = 7 })]
            }]
        });
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Equal(new[] { "later" }, context["claims"]!["completedRequirements"]!.AsArray().Select(id => id!.ToString()));
            Assert.DoesNotContain(context["actions"]!.AsArray(), action => action!["id"]!.ToString() == "unbuilt-helper");
            return Ok();
        };

        state = await Advance(state, runtime);

        Assert.Single(runtime.Calls);
        Assert.Equal("closed", Requirement(state, "later").Status);
        Assert.Equal("in_progress", Requirement(state, "work").Status);
        Assert.Equal("remaining", Requirement(state, "unrelated").Status);
        Assert.True(opening.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, state.Construction!.Checkpoints[^1]));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task MoreThanFourPendingClaimsReachOneHostPreparedGlobalReview()
    {
        var (state, runtime) = await PendingDeclarations();
        Assert.Empty(runtime.Calls);
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal("in_progress", requirement.Status));
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));

        state = await PrepareGlobal(state, runtime);
        var expected = state.Requirements!.Outcomes.Select(requirement => requirement.Id).ToArray();
        Assert.Equal(6, expected.Length);
        Assert.Equal(expected, state.Construction!.Compact!.CurrentStep!.CloseRequirementIds);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            Assert.Equal(expected, Context(request)["claims"]!["completedRequirements"]!.AsArray().Select(id => id!.ToString()));
            Assert.Equal(expected, Context(request)["requirements"]!.AsArray().Select(requirement => requirement!["id"]!.ToString()));
            return Ok();
        };
        state = await Advance(state, runtime);

        Assert.Single(runtime.Calls);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal("closed", requirement.Status));
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitFinalGlobalReviewCoversPendingClaimsOutsideCurrentStep(bool adverse)
    {
        var (state, runtime) = await PendingDeclarations();
        state.Construction!.Compact!.CurrentStep = new()
        {
            Id = "finalize-explicitly", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "close",
            Objective = "Finish the whole workflow against all original obligations.", RequirementIds = ["requirement_0"],
            CloseRequirementIds = ["requirement_0"], CloseScopes = ["/root"], DiscoveryComplete = true
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["requirement_0"], CloseScopes = ["/root"], Complete = true
        });
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            var context = Context(request);
            Assert.True(context["claims"]!["workflowComplete"]!.GetValue<bool>());
            Assert.Equal(new[] { "requirement_0" }, context["claims"]!["completedRequirements"]!.AsArray().Select(id => id!.ToString()));
            Assert.Equal(state.Requirements!.Outcomes.Select(requirement => requirement.Id),
                context["requirements"]!.AsArray().Select(requirement => requirement!["id"]!.ToString()));
            return adverse ? new() { Json = new JsonObject { ["decision"] = new JsonObject
            {
                ["verdict"] = "needs_change", ["reason"] = "An original obligation outside this step remains unsatisfied.",
                ["obligations"] = new JsonArray("requirement_5"), ["targets"] = new JsonArray("/root")
            } } } : Ok();
        };

        state = await Advance(state, runtime);

        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal(adverse ? "in_progress" : "closed", requirement.Status));
        Assert.Equal(!adverse, HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        if (!adverse) PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData("invalidated")]
    [InlineData("membership")]
    [InlineData("step_claim")]
    [InlineData("association")]
    [InlineData("checkpoint_claim")]
    public async Task InvalidatedOrUnassociatedOldClaimsCannotPrepareFinalization(string mutation)
    {
        var (state, runtime) = await PendingDeclarations();
        Assert.NotNull(PlanningCompactConstruction.PrepareReviewedClosure(state));
        var checkpoint = state.Construction!.Checkpoints[0];
        var id = state.Requirements!.Outcomes[0].Id;
        if (mutation == "invalidated") checkpoint.InvalidatedTaskIds.Add("invalidated-producer");
        if (mutation == "membership") Requirement(state, id).Checkpoints.Remove(checkpoint.Sequence);
        if (mutation == "step_claim") checkpoint.Step!.CloseRequirementIds.Clear();
        if (mutation == "association") checkpoint.RequirementIds.Clear();
        if (mutation == "checkpoint_claim") checkpoint.ClosedRequirementIds!.Clear();

        Assert.False(PlanningCompactConstruction.CompletionOnly(state));
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.NotEqual("closed", Requirement(state, id).Status);
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdverseOrExhaustedGlobalReviewCannotCommitPendingClaims(bool exhausted)
    {
        var (state, runtime) = await PendingDeclarations();
        state = await PrepareGlobal(state, runtime);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            return exhausted ? new() { CompletionStatus = "output_limit", Text = "" } : new()
            {
                Json = new JsonObject { ["decision"] = new JsonObject
                {
                    ["verdict"] = "needs_change", ["reason"] = "The current completion claim requires a business correction.",
                    ["obligations"] = new JsonArray("requirement_0"), ["targets"] = new JsonArray("/root")
                } }
            };
        };

        state = await Advance(state, runtime);

        Assert.All(state.Construction!.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        Assert.False(state.Construction.Candidate!.BusinessReviewed);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == (exhausted ? "CONSTRUCTION_REVIEW_INCONCLUSIVE" : "CONSTRUCTION_BUSINESS_REVIEW"));
    }

    [Fact]
    public async Task InterruptedGlobalIssuanceRecoversExactRequestAndClosesClaimsOnce()
    {
        var (saved, issued, runtime) = await InterruptedGlobal();
        Assert.Empty(runtime.Calls);
        var recovered = Clone(saved);
        var recovery = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Ok();
        } };
        recovered = await Advance(recovered, recovery);

        Assert.Single(recovery.Calls);
        Assert.Equal(1, recovered.ModelCalls);
        Assert.Equal(0, recovered.ReplanAttempts);
        Assert.Equal(7, recovered.Construction!.Checkpoints.Count);
        Assert.All(recovered.Construction.Requirements, requirement => Assert.Equal("closed", requirement.Status));
        PlanningArtifactApproval.Verify(recovered);
        var checkpointCount = recovered.Construction.Checkpoints.Count;
        recovered = await Advance(Clone(recovered), recovery);
        Assert.Single(recovery.Calls);
        Assert.Equal(checkpointCount, recovered.Construction!.Checkpoints.Count);
    }

    [Fact]
    public async Task ChangedCompletionClaimsRejectAnIssuedReviewBeforeDispatch()
    {
        var (saved, _, _) = await InterruptedGlobal();
        var changed = Clone(saved);
        changed.Construction!.Compact!.CurrentStep!.CloseRequirementIds.RemoveAt(0);
        var recovery = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("A stale claim must fail before dispatch.") };

        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(changed, recovery));

        Assert.Empty(recovery.Calls);
        Assert.All(changed.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        Assert.False(changed.Construction.Candidate!.BusinessReviewed);
    }

    [Fact]
    public async Task SameBasisPendingClaimIsNotProgressButNewAcceptedWorkCanRenewIt()
    {
        var (state, runtime) = await Opening();
        state = await Advance(state, runtime);
        SetCloseStep(state);
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state,
            new() { Scope = "/root", RequirementIds = ["work"] }));
        Assert.Contains(repeated.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Empty(runtime.Calls);

        state.Construction!.Compact!.CurrentStep = new()
        {
            Id = "build-work", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "value",
            Objective = "Implement the original multiplication.", RequirementIds = ["work"], CloseRequirementIds = ["work"], DiscoveryComplete = true
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/root", Tasks = [new()
            {
                Id = "implemented-work", Kind = "value", Objective = "Double the actual caller number.",
                Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply",
                    Items = [new() { Kind = "input", Source = "x" }, new() { Kind = "number", Number = 2 }] })]
            }]
        });
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.Equal("build-work", state.Construction!.Compact!.CurrentStep!.Id);
        state.Construction!.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"] });
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return Ok();
        };
        state = await Advance(state, runtime);
        Assert.Single(runtime.Calls);
        Assert.Equal("closed", Requirement(state, "work").Status);
        Assert.Equal(0, state.ReplanAttempts);

        static void SetCloseStep(PlanningSession session) => session.Construction!.Compact!.CurrentStep = new()
        {
            Id = "claim-work", BaseHash = PlanningConstruction.Hash(session.Plan), Scope = "/root", Kind = "close",
            Objective = "Claim the original numeric work complete.", RequirementIds = ["work"], CloseRequirementIds = ["work"],
            CloseScopes = [], DiscoveryComplete = true
        };
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Opening(bool extraRequirement = false)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        if (extraRequirement)
            SetOriginals(state, [("work", "Double the supplied number. "), ("later", "Return the independent number seven. "),
                ("unrelated", "The unrelated result remains future work.")]);
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep = new()
        {
            Id = "open-helper", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "group",
            Objective = "Open the reusable helper for the original numeric work.", RequirementIds = ["work"], CloseRequirementIds = ["work"], DiscoveryComplete = true
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], Groups = [new()
            {
                Id = "unbuilt-helper", Inputs = [new() { Name = "x", Type = new() { Kind = "number" }, Required = true }], Body = new()
            }]
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("A technical opening must not dispatch a Judge.");
        return (state, runtime);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> PendingDeclarations()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        SetOriginals(state, Enumerable.Range(0, 6).Select(index => ("requirement_" + index,
            "Accept numeric input input_" + index + " and return it unchanged as public result_" + index + ". ")).ToArray());
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep = null;
        state.Requirements!.Inputs = Enumerable.Range(0, 6).Select(index => new TaskInput
            { Name = "input_" + index, Type = new() { Kind = "number" }, Required = true }).ToList();
        state.Requirements.Outputs = Enumerable.Range(0, 6).Select(index => new TaskInput
            { Name = "result_" + index, Type = new() { Kind = "number" }, Required = true }).ToList();
        state.Plan = new()
        {
            Inputs = state.Requirements.Inputs.ToList(), Root = new() { Outputs = Enumerable.Range(0, 6).Select(index => new TaskOutput(
                "result_" + index, new() { Kind = "input", Source = "input_" + index })).ToList() }
        };
        runtime.Respond = (_, _) => throw new InvalidOperationException("A declaration-only claim must not dispatch a Judge.");
        foreach (var requirement in state.Requirements.Outcomes)
        {
            state.Construction!.Compact!.CurrentStep = new()
            {
                Id = "declare-" + requirement.Id, BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "close",
                Objective = "Retain the declared interface and its pending completion claim.", RequirementIds = [requirement.Id],
                CloseRequirementIds = [requirement.Id], CloseScopes = [], DiscoveryComplete = true
            };
            state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = [requirement.Id] });
            state = await Advance(state, runtime);
            Assert.Null(state.Construction!.Candidate);
            Assert.DoesNotContain(state.Diagnostics, diagnostic => diagnostic.Required);
        }
        return (state, runtime);
    }

    private static async Task<PlanningSession> PrepareGlobal(PlanningSession state, TestRuntime runtime)
    {
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.True(state.Construction!.Candidate!.Extension.Complete);
        Assert.Equal("close", state.Construction.Compact!.CurrentStep!.Kind);
        return state;
    }

    private static async Task<(PlanningSession State, string Issued, TestRuntime Runtime)> InterruptedGlobal()
    {
        var (state, runtime) = await PendingDeclarations();
        state = await PrepareGlobal(state, runtime);
        PlanningSession? saved = null;
        runtime.Checkpointed = current =>
        {
            if (saved is not null || current.PendingCall?.Purpose != PlanningConstruction.GlobalPurpose) return;
            saved = Clone(current);
            throw new OperationCanceledException("Interrupt after the exact global request is checkpointed, before dispatch.");
        };
        await Advance(state, runtime);
        Assert.NotNull(saved);
        Assert.NotNull(saved.PendingCall!.EvidenceAddresses);
        return (saved, JsonSerializer.Serialize(saved.PendingCall.Request, PlanningJsonContext.Default.LLMRequest), runtime);
    }

    private static void SetOriginals(PlanningSession state, (string Id, string Original)[] originals)
    {
        state.Request.Prompt = string.Concat(originals.Select(item => item.Original));
        state.Requirements!.Outcomes = originals.Select(item => new PlanningRequirement(item.Id, item.Original)).ToList();
        var construction = state.Construction!;
        construction.Requirements = originals.Select(item => new PlanningConstructionRequirement { Id = item.Id }).ToList();
        var compact = construction.Compact!;
        compact.Sources.Clear(); compact.RequirementSources.Clear();
        compact.Sources.Add(new() { Id = "request:0", Kind = "page", Indexed = true, Inputs = [], Outputs = [],
            Text = state.Request.Prompt, Version = PlanningGraphCompiler.Fingerprint(state.Request.Prompt) });
        for (var index = 0; index < originals.Length; index++)
        {
            var (id, text) = originals[index];
            var source = "request:0:clause:" + index;
            compact.Sources.Add(new() { Id = source, Kind = "requirement", Indexed = true, Inputs = [], Outputs = [], Text = text,
                Version = PlanningGraphCompiler.Fingerprint(text) });
            compact.RequirementSources[id] = [source];
        }
    }

    private static PlanningConstructionRequirement Requirement(PlanningSession state, string id) =>
        state.Construction!.Requirements.Single(requirement => requirement.Id == id);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!.AsObject();
    private static LLMResponse Ok() => new() { Json = BusinessReviewProtocolTests.Report("ok") };
    private static PlanningSession Clone(PlanningSession state) => BusinessReviewProtocolTests.Clone(state);
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => BusinessReviewProtocolTests.Advance(state, runtime);
}
