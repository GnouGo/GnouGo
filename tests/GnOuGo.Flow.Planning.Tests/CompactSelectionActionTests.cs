using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedSelectionAndEvidenceRejectAtomicallyAndCorrectOnceWithExactRecovery(bool interrupted)
    {
        const string objective = "Return the exact accepted greeting.";
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(new() { Request = new() { TenantId = "selection-actions", Prompt = objective,
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        state.Request.MaxReplanAttempts = 1;
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "accepted", Kind = "value",
            Objective = "Preserve this already accepted greeting.",
            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }] } };
        PlanningConstruction.LoadEvidence(state, ["source:" + state.Construction!.Compact!.Sources[0].Id]);
        var planHash = PlanningConstruction.Hash(state.Plan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var coverage = JsonSerializer.Serialize(state.Construction.Requirements, PlanningJsonContext.Default.ListPlanningConstructionRequirement);
        var evidence = state.Construction.Evidence!.ToJsonString();
        var evidenceRequests = state.Construction.EvidenceRequests.ToArray();
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var mixed = Selection(request);
            mixed["evidenceRequests"] = new JsonArray("task:accepted");
            Assert.Empty(PlanningContractValidation.ValidateInstance(mixed, request.StructuredOutputSchema!));
            return Wire(request, mixed);
        };
        state = await Advance(state, runtime);
        Assert.Equal("PROPOSAL_ACTION_INVALID", Assert.Single(state.Diagnostics).Code);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(evidence, state.Construction.Evidence!.ToJsonString());
        Assert.Equal(evidenceRequests, state.Construction.EvidenceRequests);
        Assert.Equal(planHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(coverage, JsonSerializer.Serialize(state.Construction.Requirements, PlanningJsonContext.Default.ListPlanningConstructionRequirement));
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);

        if (interrupted)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(PlannerFixture.Clone(state), runtime);
            var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.Status == PlanningStatus.Generating &&
                checkpoint.PendingCall?.Purpose == PlanningCompactConstruction.SelectPurpose));
            Assert.Equal(0, saved.ReplanAttempts);
            Assert.Equal(1, saved.PendingCall!.CorrectionAccounting);
            Assert.Equal(calls + 2, saved.ModelCalls);
            var issued = saved.PendingCall!.Request;
            var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
            var receipt = Wire(issued, Selection(issued));
            var recovery = new TestRuntime { Respond = (request, purpose) =>
            {
                Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
                Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                return receipt;
            } };
            state = await Advance(saved, recovery);
            Assert.Single(recovery.Calls);
        }
        else
        {
            runtime.Respond = (request, _) => Wire(request, Selection(request));
            state = await Advance(PlannerFixture.Clone(state), runtime);
        }
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.PendingCall);
        Assert.Equal(calls + 2, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(planHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(coverage, JsonSerializer.Serialize(state.Construction!.Requirements, PlanningJsonContext.Default.ListPlanningConstructionRequirement));
        Assert.Equal("/root", state.Construction.Compact!.CurrentStep!.Scope);
        Assert.Equal("accepted", Assert.Single(state.Construction.Compact.CurrentStep.Inputs).Source);
        Assert.Equal(["req_0"], state.Construction.Compact.CurrentStep.RequirementIds);
        Assert.Null(state.Construction.Candidate);
    }

    private static JsonObject Selection(LLMRequest request) => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(),
        ["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Expose the accepted greeting unchanged.",
            ["requirementIds"] = new JsonArray("req_0"),
            ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "output", ["source"] = "accepted", ["port"] = "message" }),
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The exact accepted greeting."),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["groupId"] = null
        },
        ["evidenceRequests"] = null, ["clarifications"] = null
    };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
