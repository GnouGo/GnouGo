using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactConvergentJudgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkedJudgeBatchLoadsExactFirstOccurrencesWithoutChangingRawResponse(bool recover)
    {
        var state = await BatchState();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var candidate = state.Construction!.Candidate!.Hash;
        var originals = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var raw = EvidenceBatch("task:accepted_label", "requirement:work", "task:accepted_label", "requirement:work");
        var exactRaw = raw.ToJsonString();
        var runtime = new TestRuntime();
        string? issued = null;
        if (recover)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        runtime.Respond = (request, _) =>
        {
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(PlanningCompactConstruction.ReviewEvidenceBatchSemantics, BatchSchema(request)["description"]!.ToString());
            Assert.Equal(8, BatchSchema(request)["maxItems"]!.GetValue<int>());
            Assert.Empty(PlanningModelCalls.ValidateResponseFindings(raw, request.StructuredOutputSchema!));
            return Response(raw);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(exactRaw, raw.ToJsonString());
        Assert.Equal(new[] { "task:accepted_label", "requirement:work" }, state.Construction!.Evidence!.Select(row => row.Key));
        var supplied = Assert.Single(state.Construction.Candidate!.Reviews, row => row["outcome"]?.ToString() == "evidence_supplied");
        Assert.Equal(new[] { "task:accepted_label", "requirement:work" }, supplied["support"]!.AsArray().Select(row => row!["address"]!.ToString()));
        Assert.DoesNotContain(PlanningConstruction.EvidenceBatchFingerprint(state,
            ["task:accepted_label", "requirement:work", "task:accepted_label", "requirement:work"]), state.Construction.EvidenceRequests);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(candidate, state.Construction.Candidate.Hash);
        Assert.Equal(originals, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.False(state.Construction.Candidate.LocalReviewed);
        Assert.All(state.Construction.Candidate.Reviews, row => Assert.Null(row["reassessment"]));
        Assert.Null(state.PendingCall);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task UnmarkedIssuedJudgeBatchRetainsHistoricalDuplicateRejection(int presentation)
    {
        var state = await BatchState(presentation);
        var (context, review) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        if (presentation == 10)
        {
            BatchSchema(review).Remove("description");
            // Preserve a pre-clarification issued envelope, not a request rebuilt
            // with current guidance when its historical response is recovered.
            var historical = review.Prompt.Split('\n').Where(line =>
                !line.StartsWith("Review this window's exact original clauses", StringComparison.Ordinal));
            review.Prompt = string.Join('\n', historical);
            Assert.DoesNotContain("A clean window closes no requirement.", review.Prompt, StringComparison.Ordinal);
        }
        Assert.False(PlanningCompactConstruction.CoalesceReviewEvidenceIssued(review.StructuredOutputSchema));
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, review.Prompt,
            review.StructuredOutputSchema!.AsObject());
        RetainIssuedBatch(state, context, issued);
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var raw = presentation >= 5 ? EvidenceBatch("task:accepted_label", "task:accepted_label") : new JsonObject
        {
            ["findings"] = null, ["evidenceRequests"] = new JsonArray("task:accepted_label", "task:accepted_label"),
            ["uncertainty"] = "The exact task evidence is required."
        };
        if (presentation == 4) raw["inconclusive"] = null;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Empty(PlanningModelCalls.ValidateResponseFindings(raw, request.StructuredOutputSchema!));
            return Response(raw);
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.All(state.Construction.Candidate!.Reviews, row => Assert.Null(row["reassessment"]));
    }

    [Theory]
    [InlineData("unoffered")]
    [InlineData("ordinal_case")]
    [InlineData("raw_count")]
    public async Task MarkedBatchCannotNormalizeAnInvalidRawResponse(string invalid)
    {
        var state = await BatchState();
        var raw = invalid switch
        {
            "unoffered" => EvidenceBatch("task:accepted_label", "task:missing", "task:missing"),
            "ordinal_case" => EvidenceBatch("task:accepted_label", "task:ACCEPTED_LABEL"),
            _ => EvidenceBatch(Enumerable.Repeat("task:accepted_label", 9).ToArray())
        };
        var original = raw.ToJsonString();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.True(PlanningCompactConstruction.CoalesceReviewEvidenceIssued(request.StructuredOutputSchema));
            Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(raw, request.StructuredOutputSchema!));
            return Response(raw);
        } };
        state = await Advance(state, runtime);
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        var repair = Assert.Single(state.Construction.Candidate!.Reviews, row => row["reportRepair"]?.GetValue<bool>() == true);
        Assert.Equal(original, repair["response"]!.ToJsonString());
        runtime.Respond = (request, _) =>
        {
            Assert.NotNull(Context(request)["reportRepair"]);
            Assert.False(PlanningCompactConstruction.CoalesceReviewEvidenceIssued(request.StructuredOutputSchema));
            return Response(Inconclusive("The invalid evidence request cannot establish this review."));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction.Candidate!.Reviews, row => row["reassessment"] is not null);
    }

    [Fact]
    public async Task CoalescedBatchCannotTurnASuccessfulEarlierReadIntoProgress()
    {
        var state = await BatchState();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (_, _) => Response(EvidenceBatch("task:accepted_label", "task:accepted_label")) };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var evidence = state.Construction!.Evidence!.ToJsonString();
        var reads = state.Construction.EvidenceRequests.ToArray();
        runtime.Respond = (request, _) =>
        {
            Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(EvidenceBatch("task:accepted_label", "task:accepted_label"), request.StructuredOutputSchema!));
            return Response(EvidenceBatch("task:accepted_label", "task:accepted_label"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(evidence, state.Construction!.Evidence!.ToJsonString());
        Assert.Equal(reads, state.Construction.EvidenceRequests);
        runtime.Respond = (_, _) => Response(Inconclusive("The already supplied task is not new evidence."));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(evidence, state.Construction!.Evidence!.ToJsonString());
        Assert.Equal(reads, state.Construction.EvidenceRequests);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(3, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction.Candidate!.Reviews, row => row["reassessment"] is not null);
        var exception = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["task:accepted_label"]));
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescedBatchStillAdmitsTheWholeDistinctFollowupAtomically(bool multiple)
    {
        var state = await BatchState();
        // Its navigation is bounded, but the requested original remains whole.
        // Keep it outside the current consumed producer cone so the initial
        // review fits and only the proposed evidence follow-up is oversized.
        state.Plan!.Root.Tasks.Add(new()
        {
            Id = "large_independent", Kind = "value", Objective = new string('x', 100_000),
            Outputs = [new("value", new() { Kind = "string", Text = "An independent retained value." })]
        });
        var construction = state.Construction!;
        var extension = construction.Candidate!.Extension;
        construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        construction.Candidate = PlanningConstruction.Apply(state, extension);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var candidate = construction.Candidate.Hash;
        var reads = construction.EvidenceRequests.ToArray();
        var raw = multiple ? EvidenceBatch("task:large_independent", "requirement:work", "task:large_independent")
            : EvidenceBatch("task:large_independent", "task:large_independent");
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Empty(PlanningModelCalls.ValidateResponseFindings(raw, request.StructuredOutputSchema!));
            return Response(raw);
        } };
        state = await Advance(state, runtime);
        var measured = Assert.Single(state.RequestMeasurements!, row => row.Admission == "rejected_before_dispatch");
        Assert.True(measured.EstimatedInputTokens > measured.InputLimit);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(candidate, state.Construction!.Candidate!.Hash);
        Assert.Null(state.Construction.Evidence);
        Assert.Equal(reads, state.Construction.EvidenceRequests);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.All(state.Construction.Candidate.Reviews, row => Assert.Null(row["reassessment"]));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        if (multiple)
        {
            Assert.Equal(PlanningStatus.Generating, state.Status);
            Assert.Equal("CONSTRUCTION_EVIDENCE_LIMIT", Assert.Single(state.Diagnostics).Code);
            Assert.Equal(1, BatchSchema(HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request)["maxItems"]!.GetValue<int>());
        }
        else
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Contains("CONSTRUCTION_EVIDENCE_LIMIT", Assert.Single(state.Diagnostics).Message, StringComparison.Ordinal);
        }
    }

    private static async Task<PlanningSession> BatchState(int presentation = 10)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        return state;
    }

    private static JsonObject EvidenceBatch(params string[] addresses)
    {
        var raw = EvidenceAction(addresses[0]);
        raw["action"]!["addresses"] = new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray());
        return raw;
    }

    private static JsonObject BatchSchema(LLMRequest request) => request.StructuredOutputSchema!["properties"]!["action"]!["anyOf"]!
        .AsArray().Single(action => action!["properties"]!["kind"]!["enum"]![0]!.ToString() == "evidenceRequests")!["properties"]!["addresses"]!.AsObject();

    private static void RetainIssuedBatch(PlanningSession state, JsonObject context, LLMRequest issued)
    {
        state.Construction!.PendingReviewContext = context;
        state.Construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            state.Construction.Candidate!.Hash + "\n" + context.ToJsonString());
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued };
        state.ModelCalls++;
    }
}
