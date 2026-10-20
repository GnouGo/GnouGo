using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDraftReselectionTests
{
    private const string Reason = "The selected value kind cannot format the imported text; inspect the current business operation before preparing its exact arguments.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialDraftReselectionKeepsAcceptedWorkAndChargesOnceAcrossRecovery(bool interrupted)
    {
        var (state, runtime) = await Ready();
        var accepted = Accepted(state);
        var calls = state.ModelCalls;
        string? issued = null;
        if (interrupted)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            state = await Advance(state, runtime);
            Assert.NotNull(state.PendingCall);
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Required);
            state = PlannerFixture.Clone(state);
            state.Status = PlanningStatus.Generating;
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
            calls = state.ModelCalls;
        }
        runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            Assert.Contains("cannot concatenate or format strings", request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
            Assert.Empty(PlanningContractValidation.ValidateInstance(Reselect(request), request.StructuredOutputSchema!));
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 8000).EstimatedInputTokens, 1, 8000);
            return new() { Json = Reselect(request) };
        } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + (interrupted ? 0 : 1), state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Equal(accepted, Accepted(state));
        Assert.Null(state.Construction.Candidate); Assert.Null(state.Construction.Compact!.CurrentStep);
        var rejected = Assert.Single(state.Construction.Compact.RejectedSteps);
        Assert.Equal(Reason, rejected["reason"]!.ToString());
        Assert.Contains(rejected["diagnostics"]!.AsArray(), diagnostic => diagnostic!["code"]!.ToString() == "CONSTRUCTION_TENTATIVE_SELECTION");
        Assert.Equal("/root", state.Construction.Compact.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction.Compact.ReplacementRequirements);
        if (issued is not null)
            Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));

        foreach (var replacement in new[]
        {
            new PlanningConstructionStep { Scope = "/groups/elsewhere/body", Kind = "operation", RequirementIds = ["req_0"] },
            new PlanningConstructionStep { Scope = "/root", Kind = "operation", RequirementIds = ["another"] }
        })
            Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, replacement)).Diagnostics,
                diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");
        state = PlannerFixture.Clone(state);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Selection(request)).Json!, request.StructuredOutputSchema!));
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 3000).EstimatedInputTokens, 1, 3000);
            return Wire(request, Selection(request));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("operation", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction.DraftCorrections);
        Assert.Equal(accepted, Accepted(state));
    }

    [Theory]
    [InlineData("total")]
    [InlineData("draft")]
    [InlineData("policy")]
    [InlineData("revision")]
    [InlineData("historical")]
    public async Task InitialReselectionCannotExpandCorrectionOrRevisionAuthority(string restriction)
    {
        var (state, runtime) = await Ready();
        switch (restriction)
        {
            case "total": state.ReplanAttempts = state.Request.MaxReplanAttempts; break;
            case "draft": state.Construction!.DraftCorrections = state.Construction.Limits.MaxCorrectionsPerDraft; break;
            case "policy": state.Request.Options["automatic_correction"] = false; break;
            case "revision": state.EditablePaths = ["/root/outputs/retained"]; break;
            case "historical":
                state.Request.Options.Remove("construction_presentation_version");
                state.Construction!.Compact!.PresentationVersion = null;
                state.Construction.Compact.InterfaceExtractions = null;
                break;
        }
        Assert.False(PlanningCompactConstruction.CanReselectInitialDraft(state));
        Assert.Equal("null", PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]!["type"]!.ToString());
        var accepted = Accepted(state); var corrections = state.ReplanAttempts; var draftCorrections = state.Construction!.DraftCorrections;
        runtime.Respond = (request, _) => new() { Json = Reselect(request) };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "PLANNING_RESPONSE_INVALID");
        Assert.Equal(corrections, state.ReplanAttempts); Assert.Equal(draftCorrections, state.Construction!.DraftCorrections);
        Assert.NotNull(state.Construction.Compact!.CurrentStep);
        Assert.Empty(state.Construction.Compact.RejectedSteps);
        Assert.Equal(accepted, Accepted(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalPendingDraftUsesItsExactNullOnlyReselectionContract(bool attemptReselection)
    {
        var (state, runtime) = await Ready();
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        request.StructuredOutputSchema!["properties"]!["reselectStep"] = PlanningSchemas.Type("null");
        request = PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.ClientRequestId = "retained-draft";
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = PlanningConstruction.DraftPurpose, Request = request };
        state.Construction!.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var accepted = Accepted(state); var calls = state.ModelCalls;
        Assert.True(PlanningCompactConstruction.CanReselectInitialDraft(state));
        runtime.Respond = (issued, _) =>
        {
            var response = Reselect(issued);
            if (!attemptReselection)
            {
                response["reselectStep"] = null;
                response["evidenceRequests"] = new JsonArray("requirement:req_0");
            }
            return new() { Json = response };
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(exact, JsonSerializer.Serialize(runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Empty(state.Construction.Compact!.RejectedSteps);
        Assert.NotNull(state.Construction.Compact.CurrentStep);
        Assert.Equal(accepted, Accepted(state));
        if (attemptReselection) Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "PLANNING_RESPONSE_INVALID");
        else
        {
            Assert.Empty(state.Diagnostics);
            Assert.NotNull(state.Construction.Evidence!["requirement:req_0"]);
        }
    }

    [Fact]
    public async Task RepeatedUnsupportedSelectionStopsWithoutChargingAnotherCorrection()
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (request, _) => new() { Json = Reselect(request) };
        state = await Advance(state, runtime);
        runtime.Respond = (request, _) => Wire(request, Selection(request, "value"));
        state = await Advance(state, runtime);
        runtime.Respond = (request, _) => new() { Json = Reselect(request) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Single(state.Construction.Compact!.RejectedSteps);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = Context(request)["sources"]![0]!["text"]!.DeepClone(),
                ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(new() { Request = new() { TenantId = "draft-reselection",
            Prompt = "Preserve the supplied text and perform the requested operation.", Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "accepted", Kind = "value", Objective = "Preserve the exact supplied text.",
            Outputs = [new("retained", new() { Kind = "string", Text = "Original text" })] }] } };
        state.Construction!.Checkpoints.Add(new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(state.Plan),
            TaskIds = ["accepted"], RequirementIds = ["req_0"] });
        state.Construction.Compact!.CurrentStep = new() { Id = "tentative", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root",
            Kind = "value", Objective = "Format the imported text for the next operation.", RequirementIds = ["req_0"],
            Inputs = [new() { Kind = "output", Source = "accepted", Port = "retained" }], InputTaskIds = ["accepted"], DiscoveryComplete = true };
        return (state, runtime);
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static string Accepted(PlanningSession state) => new JsonObject
    {
        ["plan"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan),
        ["requirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["checkpoints"] = JsonSerializer.SerializeToNode(state.Construction!.Checkpoints, PlanningJsonContext.Default.ListPlanningConstructionCheckpoint)
    }.ToJsonString();
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static JsonObject Reselect(LLMRequest request) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.DeepClone(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = null, ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = Reason
    };
    private static JsonObject Selection(LLMRequest request, string kind = "operation") => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = kind, ["objective"] = "Perform the current requested business operation.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray("The requested result."), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray() }
    };
}
