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
        Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
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
