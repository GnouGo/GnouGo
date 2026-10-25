using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionCorrectionAccountingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ValidEvidenceDoesNotConsumeCorrectionAndRecoveredReceiptKeepsIssuedAccounting(bool draft, bool interrupted)
    {
        var (state, runtime) = await Ready(draft);
        state.Diagnostics = [new("TEST_REPAIR", "/extension", "Resolve this diagnosed candidate before acceptance.")];
        var accepted = PlanningConstruction.Hash(state.Plan);
        string? issued = null;
        if (interrupted)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null && s.Status == PlanningStatus.Generating));
            Assert.Equal(draft ? 3 : 1, state.PendingCall!.CorrectionAccounting);
            Assert.Equal(0, state.ReplanAttempts);
            issued = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        }
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) =>
        {
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Evidence(request, "requirement:req_0");
        };
        state = await Advance(state, runtime);
        Assert.Equal(calls + (interrupted ? 0 : 1), state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.NotNull(state.Construction.Evidence!["requirement:req_0"]);
        Assert.Null(state.PendingCall);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Contains(state.Diagnostics, d => d.Code == "TEST_REPAIR");
        var persisted = PlannerFixture.Clone(runtime.Checkpoints[^1]);
        Assert.Equal(0, persisted.ReplanAttempts);
        Assert.Null(persisted.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidEvidenceDuringCorrectionChargesOnceWithoutAcceptingEvidence(bool draft)
    {
        var (state, runtime) = await Ready(draft);
        state.Diagnostics = [new("TEST_REPAIR", "/extension", "Resolve the diagnosed fragment.")];
        runtime.Respond = (request, _) => Evidence(request, "requirement:missing");
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(draft ? 1 : 0, state.Construction!.DraftCorrections);
        Assert.Null(state.Construction.Evidence);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Null(state.PendingCall);
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_RESPONSE_INVALID");
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task EvidenceWithCommitmentsIsRejectedWithoutStagingOrLoadingAcrossRecovery(int presentationVersion, bool interrupted)
    {
        var (state, runtime) = await Ready(draft: true);
        state.Request.Options["construction_presentation_version"] = presentationVersion;
        state.Construction!.Compact!.PresentationVersion = presentationVersion;
        state.Catalog!.AllowedStepTypes.Add("mcp.call");
        state.Discovery.Resolved.Add(new()
        {
            Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read", EffectKind = "read",
            Description = "Read the requested value.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["result"] = new JsonObject { ["type"] = "number" } }, ["required"] = new JsonArray("result"), ["additionalProperties"] = false }
        });
        state.Construction.Compact.CurrentStep!.Kind = "operation";
        state.Construction.Compact.CurrentStep.OperationIds = ["read"];
        state.Diagnostics = [new("TEST_REPAIR", "/extension", "Resolve the diagnosed fragment.")];
        var accepted = PlanningConstruction.Hash(state.Plan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        string? exact = null;
        if (interrupted)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null && s.Status == PlanningStatus.Generating));
            exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) =>
        {
            if (exact is not null) Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            var response = Evidence(request, "requirement:req_0");
            response.Json!["commitments"] = new JsonArray(new JsonObject { ["requirementId"] = "req_0", ["operation"] = "read", ["result"] = null });
            Assert.Empty(PlanningContractValidation.ValidateInstance(response.Json, request.StructuredOutputSchema!));
            return response;
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "PROPOSAL_ACTION_INVALID" && diagnostic.Location == "/evidenceRequests");
        Assert.Equal(calls + (interrupted ? 0 : 1), state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Null(state.PendingCall);
        Assert.Null(state.Construction.Candidate);
        Assert.Empty(state.Construction.Compact!.PendingCommitments);
        Assert.Null(state.Construction.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionAndUncertainDispatchDoNotClassifyAResponse(bool oversized)
    {
        var (state, runtime) = await Ready(draft: true);
        state.Diagnostics = [new("TEST_REPAIR", "/extension", oversized ? new string('x', 100_000) : "Repair.")];
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        var calls = state.ModelCalls;
        state = await Advance(state, runtime);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Equal(calls + (oversized ? 0 : 1), state.ModelCalls);
        if (oversized) Assert.Null(state.PendingCall);
        else Assert.Equal(3, state.PendingCall!.CorrectionAccounting);
    }

    [Fact]
    public async Task HistoricalPrechargedRequestKeepsExactBytesAndChargesWithoutRefund()
    {
        var (state, runtime) = await Ready(draft: true);
        state.Diagnostics = [new("TEST_REPAIR", "/extension", "Repair.")];
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null && s.Status == PlanningStatus.Generating));
        state.PendingCall!.CorrectionAccounting = null;
        state.ReplanAttempts = 1; state.Construction!.DraftCorrections = 1;
        var exact = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Evidence(request, "requirement:req_0");
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Null(state.PendingCall);
        Assert.NotNull(state.Construction.Evidence);
    }

    [Fact]
    public async Task RequiredCorrectionCapacityIsReservedEvenForAnEvidenceResponse()
    {
        var (state, runtime) = await Ready(draft: true);
        state.Diagnostics = [new("TEST_REPAIR", "/extension", "Repair.")];
        state.ReplanAttempts = state.Request.MaxReplanAttempts;
        var calls = runtime.Calls.Count;
        runtime.Respond = (request, _) => Evidence(request, "requirement:req_0");
        state = await Advance(state, runtime);
        Assert.Equal(calls, runtime.Calls.Count);
        Assert.Equal(state.Request.MaxReplanAttempts, state.ReplanAttempts);
        Assert.Null(state.Construction!.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCandidateRepairClassifiesEvidenceWithoutChargingItsCandidateTwice(bool valid)
    {
        var (state, runtime) = await Ready(draft: true);
        var candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["req_0"],
            Tasks = [new() { Id = "constant", Kind = "value", Objective = "Produce the constant.",
                Outputs = [new("value", new() { Kind = "number", Number = 1 })] }]
        });
        state.Construction!.Candidate = candidate;
        candidate.Diagnostics = [new("TASK_VALUE_TYPE", "/tasks/constant/outputs/value", "Correct this value.")];
        state.Diagnostics = candidate.Diagnostics;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            return Wire(request, new() { ["evidenceRequests"] = new JsonArray(valid ? "requirement:req_0" : "requirement:unknown") });
        };
        state = await Advance(state, runtime);
        Assert.True(runtime.Calls.Count == 2, string.Join("; ", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(valid ? 0 : 1, state.ReplanAttempts);
        Assert.Equal(valid ? 0 : 1, state.Construction!.Candidate!.Corrections);
        Assert.Equal(0, state.Construction.DraftCorrections);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredTypedRepairExpiresOnlyFreshCandidateEvidenceAndKeepsConsumedHistory(bool historical)
    {
        var (state, runtime) = await Ready(draft: true);
        if (historical)
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
        state.Construction!.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["req_0"],
            Tasks = [new() { Id = "constant", Kind = "value", Objective = "Produce the constant.",
                Outputs = [new("value", new() { Kind = "number", Number = 1 })] }]
        });
        var candidate = state.Construction.Candidate;
        candidate.Diagnostics = [new("TASK_VALUE_TYPE", "/tasks/constant/outputs/value", "Correct this value.")];
        state.Diagnostics = candidate.Diagnostics;
        PlanningConstruction.LoadEvidence(state, ["task:constant"]);
        var originalEvidence = state.Construction.Evidence!.ToJsonString();
        var fingerprints = state.Construction.EvidenceRequests.ToArray();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var before = candidate.Hash;
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        runtime.Respond = (_, _) => { interruption.Cancel(); throw new OperationCanceledException(interruption.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, interruption.Token));
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            var context = PlanningRepairPatch.RequestContext(request);
            var slot = context["repair"]!["slots"]!.AsArray().Single(value =>
                value!["location"]!.ToString() == "/tasks/constant/outputs/value")!;
            return Wire(request, new()
            {
                ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                {
                    ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace",
                    ["value"] = new JsonObject { ["kind"] = "number", ["number"] = 2 }
                }) }
            });
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.Candidate!.Corrections);
        Assert.NotEqual(before, state.Construction.Candidate.Hash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        if (historical) Assert.Equal(originalEvidence, state.Construction.Evidence!.ToJsonString());
        else Assert.Null(state.Construction.Evidence);
        Assert.All(fingerprints, fingerprint => Assert.Contains(fingerprint, state.Construction.EvidenceRequests));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("json")]
    [InlineData("output_limit")]
    public async Task KnownUnusableCorrectionReceiptStillConsumesOneAttempt(string failure)
    {
        var (state, runtime) = await Ready(draft: true);
        state.Diagnostics = [new("TEST_REPAIR", "/extension", "Repair.")];
        runtime.Respond = (_, _) => failure switch
        {
            "schema" => new() { Json = new JsonObject { ["unexpected"] = true } },
            "json" => new() { Text = "invalid JSON" },
            _ => new() { CompletionStatus = "output_limit" }
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Null(state.PendingCall);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task CandidateRepairReceivesTheRejectedEvidenceDiagnosticAndClearsItOnlyAfterSuccess(int presentation, bool typed)
    {
        var (state, runtime) = await Ready(draft: true);
        state.Request.Options["construction_presentation_version"] = presentation;
        state.Construction!.Compact!.PresentationVersion = presentation;
        var candidate = DiagnoseCandidate(state, typed);
        var finding = Assert.Single(candidate.Diagnostics);
        var loaderRejects = typed && presentation < 6;
        var evidenceCode = loaderRejects ? "CONSTRUCTION_EVIDENCE_UNKNOWN" : "PLANNING_RESPONSE_INVALID";
        var accepted = PlanningConstruction.Hash(state.Plan);
        var hash = candidate.Hash;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.Equal(typed, PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            return Wire(request, new() { ["baseHash"] = accepted,
                ["evidenceRequests"] = new JsonArray("requirement:req_0", "requirement:missing") });
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        Assert.Equal(finding, Assert.Single(state.Construction.Candidate!.Diagnostics));
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == evidenceCode);
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            var diagnostics = Context(request)["diagnostics"]!.AsArray();
            Assert.Equal(2, diagnostics.Count);
            Assert.Single(diagnostics, diagnostic => diagnostic!["code"]!.ToString() == finding.Code);
            var rejected = Assert.Single(diagnostics, diagnostic => diagnostic!["code"]!.ToString() == evidenceCode)!;
            Assert.Contains("requirement:missing", rejected["message"]!.ToString(), StringComparison.Ordinal);
            Assert.Equal(loaderRejects ? "/evidenceRequests" : "/evidenceRequests/1", Assert.Single(rejected["locations"]!.AsArray())!.ToString());
            return Evidence(request, "requirement:req_0");
        };
        state = await Advance(state, runtime);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(hash, state.Construction!.Candidate!.Hash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.NotNull(state.Construction.Evidence!["requirement:req_0"]);
        Assert.DoesNotContain(state.Diagnostics, diagnostic => diagnostic.Code == evidenceCode);
        Assert.Equal(finding, Assert.Single(state.Construction.Candidate.Diagnostics));
        Assert.Equal(finding.Code, Assert.Single(PlanningCompactConstruction.Context(state)["diagnostics"]!.AsArray())!["code"]!.ToString());
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task PendingRepairKeepsItsIssuedDiagnosticContextAndAccountingAfterNewHostFeedback()
    {
        var (state, runtime) = await Ready(draft: true);
        var candidate = DiagnoseCandidate(state, typed: false);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var candidateHash = candidate.Hash;
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        runtime.Respond = (_, _) => { interruption.Cancel(); throw new OperationCanceledException(interruption.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, interruption.Token));
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var request = state.PendingCall!.Request;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        state.Diagnostics.Add(new("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests/1", "A later host diagnostic cannot rewrite an issued request."));
        runtime.Respond = (issued, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            Assert.DoesNotContain(Context(issued)["diagnostics"]!.AsArray(),
                diagnostic => diagnostic!["code"]!.ToString() == "CONSTRUCTION_EVIDENCE_INVALID");
            return Evidence(issued, "requirement:req_0");
        };
        state = await Advance(state, runtime);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Equal(0, state.Construction.Candidate!.Corrections);
        Assert.Null(state.PendingCall);
        Assert.Equal(candidateHash, state.Construction.Candidate.Hash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.DoesNotContain(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_INVALID");
        Assert.Equal(candidate.Diagnostics, state.Construction.Candidate.Diagnostics);
        Assert.NotNull(state.Construction.Evidence!["requirement:req_0"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceCleanupPreservesOtherDiagnosticsAndHistoricalProjection(bool historical)
    {
        var (state, _) = await Ready(draft: true);
        var candidate = DiagnoseCandidate(state, typed: false);
        var finding = Assert.Single(candidate.Diagnostics);
        if (historical)
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
        state.Diagnostics =
        [
            finding,
            new("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests/0", "The requested address was invalid."),
            new("CONSTRUCTION_EVIDENCE_INVALID", "/extension", "An unrelated location remains diagnosed."),
            new("OTHER_FAILURE", "/evidenceRequests", "An unrelated failure remains diagnosed.")
        ];
        var before = PlanningCompactConstruction.Context(state)["diagnostics"]!.AsArray();
        Assert.Equal(historical ? 1 : 4, before.Count);
        Assert.Single(before, diagnostic => diagnostic!["code"]!.ToString() == finding.Code);
        PlanningConstruction.LoadEvidence(state, ["requirement:req_0"]);
        Assert.Equal(historical ? 4 : 3, state.Diagnostics.Count);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Location == "/extension");
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "OTHER_FAILURE");
        Assert.Equal(finding, Assert.Single(candidate.Diagnostics));
        Assert.Equal(historical ? 1 : 3, PlanningCompactConstruction.Context(state)["diagnostics"]!.AsArray().Count);
    }

    private static PlanningConstructionCandidate DiagnoseCandidate(PlanningSession state, bool typed)
    {
        var candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["req_0"],
            Tasks = [new() { Id = "constant", Kind = "value", Objective = "Produce the requested constant.",
                Outputs = [new("value", new() { Kind = "number", Number = 1 })] }] });
        candidate.Diagnostics = [typed
            ? new("TASK_VALUE_TYPE", "/tasks/constant/outputs/value", "Correct this value.")
            : new("CONSTRUCTION_BUSINESS_REVIEW", "/construction/candidate", "The candidate does not satisfy the current business action.")];
        state.Construction!.Candidate = candidate;
        state.Diagnostics = candidate.Diagnostics.ToList();
        return candidate;
    }

    private static JsonObject Context(LLMRequest request) =>
        JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();

    private static async Task<(PlanningSession, TestRuntime)> Ready(bool draft)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            var source = context["sources"]![0]!;
            return Wire(request, new()
            {
                ["sourceId"] = source["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = source["text"]!.DeepClone(),
                    ["kind"] = "requirement", ["interfaces"] = "none" })
            });
        } };
        var state = await Advance(new() { Request = new() { TenantId = "accounting", Prompt = "Produce a constant value.",
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(PlanningConstructionPhasePolicies.IsFresh(state.Request));
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new();
        if (draft)
            state.Construction!.Compact!.CurrentStep = new()
            {
                Id = "constant", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "value",
                Objective = state.Request.Prompt, RequirementIds = ["req_0"], DiscoveryComplete = true
            };
        return (state, runtime);
    }

    private static LLMResponse Evidence(LLMRequest request, string address) => Wire(request,
        new() { ["baseHash"] = PlanningConstruction.Hash(new TaskPlan()), ["evidenceRequests"] = new JsonArray(address) });
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
