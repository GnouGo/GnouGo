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
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task CoalescedBatchStillAdmitsTheWholeDistinctFollowupAtomically(int presentation, bool multiple)
    {
        var state = await BatchState(presentation);
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
            var followup = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request;
            Assert.Equal(1, BatchSchema(followup)["maxItems"]!.GetValue<int>());
            if (presentation == 11)
            {
                var diagnosis = Assert.Single(state.Diagnostics);
                Assert.Contains(measured.RequestHash, diagnosis.Message, StringComparison.Ordinal);
                Assert.Contains(measured.CompleteRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnosis.Message, StringComparison.Ordinal);
                Assert.Contains(measured.EstimatedInputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnosis.Message, StringComparison.Ordinal);
                Assert.Contains(measured.InputLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnosis.Message, StringComparison.Ordinal);
                var complete = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose,
                    followup.Prompt, followup.StructuredOutputSchema!.AsObject(), construction.Limits.ReviewOutputTokens);
                var actual = PlanningRequestMeasurements.Measure(complete, PlanningConstruction.LocalPurpose,
                    PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningConstruction.LocalPurpose));
                Assert.Contains(state.RequestMeasurements!, row => row.RequestHash == actual.RequestHash && row.Admission != "rejected_before_dispatch");
                var rejected = Assert.Single(state.Construction.Candidate.Reviews, row => row["outcome"]?.ToString() == "evidence_admission_rejected");
                Assert.True(JsonNode.DeepEquals(raw, rejected["rawResponse"]));
                Assert.NotNull(rejected["sourceCoverage"]);
                runtime.Respond = (_, _) => throw new OperationCanceledException();
                _ = await Advance(PlannerFixture.Clone(state), runtime);
                state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
                var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
                var charged = state.ModelCalls;
                runtime.Respond = (request, _) =>
                {
                    Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                    return Response(EvidenceBatch("task:accepted_label"));
                };
                state = await Advance(state, runtime);
                Assert.Empty(state.Diagnostics);
                Assert.Equal(charged, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
                Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Equal(candidate, state.Construction!.Candidate!.Hash);
                Assert.True(state.Construction.Evidence!.ContainsKey("task:accepted_label"));
                Assert.False(state.Construction.Evidence.ContainsKey("task:large_independent"));
                Assert.Empty(state.Construction.Checkpoints);
            }
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

    [Fact]
    public async Task UnsendableEvidenceControlStopsWithoutAnotherDispatchOrCommittingTheRejectedBatch()
    {
        var state = await BatchState(11);
        state.Plan!.Root.Tasks.Add(new() { Id = "large_independent", Kind = "value", Objective = new string('x', 100_000),
            Outputs = [new("value", new() { Kind = "string", Text = "Retained independent value." })] });
        var construction = state.Construction!;
        construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        construction.Candidate = PlanningConstruction.Apply(state, construction.Candidate!.Extension);
        var first = CompleteRequest();
        SetCeiling(Estimated(first) - 1);
        var compacted = CompleteRequest();
        Assert.True(Estimated(compacted) < Estimated(first));
        SetCeiling(Estimated(compacted));
        var baseline = PlanningConstruction.Hash(state.Plan); var candidate = construction.Candidate.Hash;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.InRange(Estimated(request), 1, construction.Limits.PhasePolicy!.Review.MaxInputTokens);
            var response = EvidenceBatch("task:large_independent", "requirement:work");
            Assert.Empty(PlanningModelCalls.ValidateResponseFindings(response, request.StructuredOutputSchema!));
            return Response(response);
        } };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains("CONSTRUCTION_EVIDENCE_RECOVERY_LIMIT", Assert.Single(state.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Equal(candidate, state.Construction!.Candidate!.Hash);
        Assert.Null(state.Construction.Evidence); Assert.Empty(state.Construction.Checkpoints);
        Assert.False(PlanningConstructionReviewCoverage.Complete(state, state.Construction.Candidate));
        Assert.Equal(2, state.RequestMeasurements!.Count(row => row.Admission == "rejected_before_dispatch"));
        Assert.Single(state.Construction.Candidate.Reviews, row => row["outcome"]?.ToString() == "evidence_admission_rejected");

        LLMRequest CompleteRequest()
        {
            var request = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request;
            return PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, request.Prompt,
                request.StructuredOutputSchema!.AsObject(), construction.Limits.ReviewOutputTokens);
        }
        int Estimated(LLMRequest request) => checked((int)PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose,
            construction.Limits.PhasePolicy!.Review.MaxInputTokens).EstimatedInputTokens);
        void SetCeiling(int ceiling)
        {
            construction.Limits.PhasePolicy!.Review.MaxInputTokens = ceiling;
            construction.Limits.PhasePolicy.Review.TargetInputTokens = Math.Min(ceiling, construction.Limits.PhasePolicy.Review.TargetInputTokens);
            construction.Limits.InputLimits!.Review = ceiling;
            state.Request.Options["construction_limits"] = JsonSerializer.SerializeToNode(construction.Limits, PlanningJsonContext.Default.PlanningConstructionOptions);
            state.Request.Options["construction_input_limits"] = JsonSerializer.SerializeToNode(construction.Limits.InputLimits, PlanningJsonContext.Default.PlanningConstructionInputLimits);
        }
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
