using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactEvidenceStopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedEvidenceRemainsRejectedWithoutSpendingAnotherCorrection(bool recover)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new()
            {
                ["evidenceRequests"] = null, ["clarifications"] = null, ["clauses"] = new JsonArray(new JsonObject
                {
                    ["text"] = source["text"]!.DeepClone(), ["kind"] = "requirement", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray()
                })
            });
        } };
        var state = await Advance(new() { Request = new()
        {
            TenantId = "evidence-stop", Prompt = "Read the authorized original records.",
            Options = new() { ["automatic_correction"] = true }
        } }, runtime);
        Assert.Empty(state.Diagnostics);
        state.Plan = new() { Root = new() { Tasks = [new()
        {
            Id = "accepted", Kind = "value", Objective = "Retain this independent value.",
            Outputs = [new("result", new() { Kind = "number", Number = 0 })]
        }] } };
        state.Construction!.Compact!.CurrentStep = new()
        {
            Id = "read", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root",
            Kind = "operation", Objective = state.Request.Prompt, RequirementIds = ["req_0"]
        };

        var address = "source:" + state.Construction.Compact.Sources[0].Id;
        PlanningConstruction.LoadEvidence(state, [address]);
        var plan = PlanningConstruction.Hash(state.Plan);
        var evidence = state.Construction.Evidence!.ToJsonString();
        var fingerprints = state.Construction.EvidenceRequests.ToArray();
        string? exactRequest = null;
        if (recover)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
                checkpoint.Status == PlanningStatus.Generating &&
                checkpoint.PendingCall?.Purpose == PlanningCompactConstruction.DiscoveryPurpose));
            exactRequest = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        var calls = state.ModelCalls;
        var corrections = state.ReplanAttempts;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            if (exactRequest is not null)
                Assert.Equal(exactRequest, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            var response = Wire(request, new()
            {
                ["operationIds"] = null, ["discoveryRequests"] = null, ["clarifications"] = null,
                ["reselectStep"] = null, ["evidenceRequests"] = new JsonArray(address)
            });
            var findings = PlanningContractValidation.ValidateInstanceFindings(response.Json!, request.StructuredOutputSchema!);
            Assert.NotEmpty(findings);
            return response;
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("PLANNING_RESPONSE_INVALID", diagnostic.Code);
        Assert.Equal(calls + (recover ? 0 : 1), state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(evidence, state.Construction!.Evidence!.ToJsonString());
        Assert.Equal(fingerprints, state.Construction.EvidenceRequests);
        Assert.Null(state.PendingCall);
        Assert.Null(state.Construction.Candidate);
        Assert.Empty(state.Construction.Checkpoints);
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) =>
        JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    {
        Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())
    };
}
