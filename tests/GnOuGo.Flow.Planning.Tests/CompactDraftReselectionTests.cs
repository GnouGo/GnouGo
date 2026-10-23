using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDraftReselectionTests
{
    private const string Reason = "The selected value kind cannot format the imported text; the missing prerequisite is an accepted formatted-text producer before the operation can bind its argument.";

    [Fact]
    public async Task VersionFourActionGuidancePreservesRevisionThreeSchemasAndContext()
    {
        var (historical, _) = await Ready(3);
        Assert.Equal("0cd866444892db70ccdf5c485371c2d0def3b4926bacfd71fba38ddc12fe1ea4", PlanningGraphCompiler.Fingerprint(PlanningCompactConstruction.SelectGuidance(historical)));
        Assert.Equal("4bce0868c70e46763d9c5dfbc1c7d8e66564ab0e7e4c5c9ba9616da2756120d2", PlanningGraphCompiler.Fingerprint(PlanningCompactConstruction.DraftGuidance(historical)));
        var current = PlannerFixture.Clone(historical);
        current.Request.Options["construction_presentation_version"] = 4;
        current.Construction!.Compact!.PresentationVersion = 4;
        Assert.Equal(PlanningCompactConstruction.SelectionSchema(historical).ToJsonString(), PlanningCompactConstruction.SelectionSchema(current).ToJsonString());
        Assert.Equal(PlanningCompactConstruction.SelectionContext(historical).ToJsonString(), PlanningCompactConstruction.SelectionContext(current).ToJsonString());
        var before = PlanningConstruction.DraftRequest(historical, repair: false);
        var after = PlanningConstruction.DraftRequest(current, repair: false);
        Assert.Equal(before.StructuredOutputSchema!.ToJsonString(), after.StructuredOutputSchema!.ToJsonString());
        Assert.Equal(Context(before).ToJsonString(), Context(after).ToJsonString());
        Assert.Equal(before.Prompt.Replace(PlanningCompactConstruction.DraftGuidance(historical),
            PlanningCompactConstruction.DraftGuidance(current), StringComparison.Ordinal), after.Prompt);
        Assert.Contains("Commitments accompany extension only and are null otherwise", after.Prompt, StringComparison.Ordinal);
        Assert.Contains("evidence-only responses also keep requirements and rollbackReason null", after.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferedRollbackInvalidatesOnlyDependentWorkAndRecoversAnUnannotatedRequest(bool retainedRequest)
    {
        const string defect = "The retained producer's interface prevents the required consumer binding.";
        var (state, runtime) = await Ready();
        state.Plan!.Root.Tasks.Add(new() { Id = "dependent", Kind = "value", Objective = "Retain the producer result.",
            Outputs = [new("copy", new() { Kind = "output", Source = "accepted", Port = "retained" })] });
        state.Plan.Root.Tasks.Add(new() { Id = "independent", Kind = "value", Objective = "Preserve unrelated text.",
            Outputs = [new("unchanged", new() { Kind = "string", Text = "Independent" })] });
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var independent = JsonSerializer.Serialize(state.Plan.Root.Tasks[^1], PlanningJsonContext.Default.PlanTask);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var sources = JsonSerializer.Serialize(state.Construction.Compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.NotEqual("null", request.StructuredOutputSchema!["properties"]!["rollbackTask"]!["type"]?.ToString());
        Assert.Contains("host-computed invalidation", request.StructuredOutputSchema["description"]!.ToString(), StringComparison.Ordinal);
        string? issued = null;
        if (retainedRequest)
        {
            request.StructuredOutputSchema["description"] = PlanningCompactConstruction.DraftSemantics(state, state.Construction.Compact.CurrentStep);
            request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema.AsObject());
            state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningConstruction.DraftPurpose,
                Request = request, CorrectionAccounting = 0 };
            state.Construction.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
            state.ModelCalls++;
            issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            state = PlannerFixture.Clone(state);
        }
        var calls = state.ModelCalls;
        var corrections = state.ReplanAttempts;
        var response = Reselect(request);
        response["reselectStep"] = null; response["rollbackTask"] = "accepted"; response["rollbackReason"] = defect;
        runtime.Respond = (actual, _) =>
        {
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(actual, PlanningJsonContext.Default.LLMRequest));
            else Assert.Contains("host-computed invalidation", actual.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, actual.StructuredOutputSchema!));
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.PendingCall);
        Assert.Equal(calls + (retainedRequest ? 0 : 1), state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal(independent, JsonSerializer.Serialize(Assert.Single(state.Plan!.Root.Tasks), PlanningJsonContext.Default.PlanTask));
        var invalidation = Assert.Single(state.Construction!.Invalidations);
        Assert.Equal(["accepted", "dependent"], invalidation.Tasks.Select(task => task.Id));
        Assert.Equal(defect, invalidation.Reason);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(sources, JsonSerializer.Serialize(state.Construction.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.Null(state.Construction.Compact.CurrentStep);
    }

    [Fact]
    public async Task RollbackDescriptionMatchesExistingActionAvailabilityAndLeavesHistoricalSchemasUnchanged()
    {
        var (state, _) = await Ready();
        var historical = PlannerFixture.Clone(state);
        historical.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        historical.Construction!.Limits.PhasePolicy = null;
        Assert.Equal(PlanningCompactConstruction.DraftSemantics(historical, historical.Construction.Compact!.CurrentStep!),
            PlanningCompactConstruction.DraftSchema(historical)["description"]!.ToString());
        foreach (var candidate in new[] { false, true })
        {
            var blocked = PlannerFixture.Clone(state);
            if (candidate) blocked.Construction!.Candidate = new();
            else blocked.Phase = PlanningPhase.Replanning;
            var schema = PlanningCompactConstruction.DraftSchema(blocked);
            Assert.Equal("null", schema["properties"]!["rollbackTask"]!["type"]!.ToString());
            Assert.Equal("null", schema["properties"]!["rollbackReason"]!["type"]!.ToString());
            Assert.Equal(PlanningCompactConstruction.DraftSemantics(blocked, blocked.Construction!.Compact!.CurrentStep!), schema["description"]!.ToString());
            var response = Reselect(PlanningConstruction.DraftRequest(state, repair: false));
            response["reselectStep"] = null; response["rollbackTask"] = "accepted"; response["rollbackReason"] = Reason;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        }
    }

    [Theory]
    [InlineData(false, "operation", 2)]
    [InlineData(true, "operation", 2)]
    [InlineData(false, "transform", 2)]
    [InlineData(true, "transform", 2)]
    [InlineData(false, "transform", 3)]
    [InlineData(true, "transform", 3)]
    [InlineData(false, "transform", 4)]
    [InlineData(true, "transform", 4)]
    public async Task InitialDraftReselectionKeepsAcceptedWorkAndChargesOnceAcrossRecovery(bool interrupted, string replacementKind, int presentationVersion)
    {
        var (state, runtime) = await Ready(presentationVersion);
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
            Assert.Contains(PlanningCompactConstruction.ValueSemantics, request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
            var reasonDescription = request.StructuredOutputSchema["properties"]!["reselectStep"]!["description"]!.ToString();
            Assert.Contains("precise reason", reasonDescription, StringComparison.Ordinal);
            Assert.Contains("not a step ID", reasonDescription, StringComparison.Ordinal);
            Assert.Contains("missing prerequisite", reasonDescription, StringComparison.Ordinal);
            Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema, strict: true));
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
            Assert.Contains(PlanningCompactConstruction.ValueSemantics, request.Prompt, StringComparison.Ordinal);
            var reselection = Context(request)["reselection"]!;
            Assert.Equal(Reason, reselection["reason"]!.ToString());
            Assert.Equal(presentationVersion == 4, request.Prompt.Contains("If reselection identifies a missing producer, select that prerequisite before its consumer.", StringComparison.Ordinal));
            Assert.Equal("/root", reselection["scope"]!.ToString());
            Assert.Equal(["req_0"], reselection["requirementIds"]!.AsArray().Select(id => id!.ToString()));
            Assert.True(PlanningCompactConstruction.LoadEvidence(state, reselection["evidence"]!.ToString(), out var evidence));
            Assert.True(JsonNode.DeepEquals(rejected, evidence));
            Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Selection(request, replacementKind)).Json!, request.StructuredOutputSchema!));
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 3000).EstimatedInputTokens, 1, 3000);
            return Wire(request, Selection(request, replacementKind));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(replacementKind, state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Equal(replacementKind != "operation", state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction.DraftCorrections);
        Assert.Equal(accepted, Accepted(state));
        if (replacementKind == "transform")
        {
            var imported = Assert.Single(state.Construction.Compact.CurrentStep.Inputs);
            Assert.Equal("accepted", imported.Source); Assert.Equal("retained", imported.Port);
            runtime.Respond = (request, purpose) =>
            {
                Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
                var response = Reselect(request);
                response["reselectStep"] = null;
                response["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                {
                    Scope = "/root", RequirementIds = ["req_0"], Tasks = [new()
                    {
                        Id = "formatted", Kind = "transform", Mode = "interpret", Objective = "Format the imported text for the next operation.",
                        Inputs = [new("text", new() { Kind = "output", Source = "accepted", Port = "retained" })],
                        ResultType = new() { Kind = "object", Fields = [new() { Name = "formattedText", Type = new() { Kind = "string" } }] }
                    }]
                }, PlanningJsonContext.Default.PlanningConstructionExtension);
                return Wire(request, response);
            };
            state = await Advance(state, runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal("transform", Assert.Single(state.Construction!.Candidate!.Extension.Tasks).Kind);
            Assert.Equal("remaining", Assert.Single(state.Construction.Requirements).Status);
            Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction.DraftCorrections);
            Assert.Equal(accepted, Accepted(state));
        }
    }

    [Fact]
    public async Task PreviouslyIssuedDraftKeepsItsUnannotatedReselectionFieldAndExactReason()
    {
        var (state, runtime) = await Ready();
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Limits.PhasePolicy = null;
        historical.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        Assert.Null(PlanningCompactConstruction.DraftSchema(historical)["properties"]!["reselectStep"]!["description"]);

        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.True(request.StructuredOutputSchema!["properties"]!["reselectStep"]!.AsObject().Remove("description"));
        request = PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.ClientRequestId = "retained-unannotated-reselection";
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = PlanningConstruction.DraftPurpose, Request = request };
        state.Construction!.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var accepted = Accepted(state); var calls = state.ModelCalls;
        runtime.Respond = (issued, _) =>
        {
            Assert.Null(issued.StructuredOutputSchema!["properties"]!["reselectStep"]!["description"]);
            return new() { Json = Reselect(issued) };
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(exact, JsonSerializer.Serialize(runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Equal(accepted, Accepted(state));
        Assert.Equal(Reason, Assert.Single(state.Construction.Compact!.RejectedSteps)["reason"]!.ToString());
        Assert.Equal("/root", state.Construction.Compact.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction.Compact.ReplacementRequirements);
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
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public async Task HistoricalPendingDraftUsesItsExactNullOnlyReselectionContract(bool attemptReselection, int presentationVersion)
    {
        var (state, runtime) = await Ready(presentationVersion);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        if (presentationVersion == 4)
        {
            var historical = PlannerFixture.Clone(state);
            historical.Construction!.Compact!.PresentationVersion = 3;
            request.Prompt = request.Prompt.Replace(PlanningCompactConstruction.DraftGuidance(state),
                PlanningCompactConstruction.DraftGuidance(historical), StringComparison.Ordinal);
        }
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

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(int presentationVersion = 2)
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = Context(request)["sources"]![0]!["text"]!.DeepClone(),
                ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "draft-reselection",
            Prompt = "Preserve the supplied text and perform the requested operation.", Options = new() { ["automatic_correction"] = true } } }, presentationVersion), runtime);
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
        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = kind, ["objective"] = kind == "transform"
                ? "Format the imported text for the next operation." : "Perform the current requested business operation.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = kind == "transform" ? new JsonArray(new JsonObject
                { ["kind"] = "output", ["source"] = "accepted", ["port"] = "retained" }) : new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray("The requested result."), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray() }
    };
}
