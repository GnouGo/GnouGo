using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactLoopCapacityPresentationTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SupportedCapacityIsUnverifiedInsteadOfAnInventedOriginalMaximum(int presentation)
    {
        var (state, runtime) = await Ready(initialCapacity: 1000, callerInput: true, presentation: presentation);
        var requirement = Assert.Single(state.Requirements!.Outcomes);
        state.Requirements.Outcomes[0] = requirement with { Commitments = [] };
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        state = await Advance(state, runtime);
        AssertDefaultGuidance(runtime.Calls[^1]);
        Assert.Equal(1000, Assert.Single(state.Construction!.Candidate!.Extension.Tasks).MaxItems);
        state = await Advance(state, runtime);
        var review = runtime.Calls[^1];
        if (presentation == 3)
        {
            Assert.Contains("An unspecified input length does not require unlimited capacity", review.Prompt, StringComparison.Ordinal);
            Assert.Contains("never returns truncated success", review.Prompt, StringComparison.Ordinal);
            Assert.Contains("explicit required capacities or known required item counts", review.Prompt, StringComparison.Ordinal);
            Assert.Contains("Absent bounds alone are not defects or missing business facts; unspecified capacity remains unverified", review.Prompt, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Without an explicit original bound, use", review.Prompt, StringComparison.Ordinal);
        var imported = Assert.Single(Context(review)["interfaces"]!.AsArray())!;
        Assert.Equal("values", imported["binding"]!["source"]!.ToString());
        Assert.Equal("array", imported["schema"]!["type"]!.ToString());
        Assert.Null(imported["schema"]!["maxItems"]);
        Assert.Null(Assert.Single(state.Requirements!.Inputs!).Type.MaxItems);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(1000, state.Plan!.Root.Tasks.Single(task => task.Id == "iterate").MaxItems);
        Assert.Contains("/tasks/iterate/body", state.Construction.OpenScopes);
        Assert.Equal("in_progress", Assert.Single(state.Construction.Requirements).Status);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task DraftAndCorrectionPresentExistingCapacityWithoutRewritingTheCandidateOrAcceptedWork()
    {
        var (state, runtime) = await Ready();
        var accepted = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        state = await Advance(state, runtime);
        var generation = runtime.Calls[^1];
        AssertDefaultGuidance(generation);
        AssertNumericDomain(generation);
        Assert.Equal(1, Assert.Single(state.Construction!.Candidate!.Extension.Tasks).MaxItems);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));

        state = await Advance(state, runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        state = await Advance(state, runtime);
        var correction = runtime.Calls[^1];
        Assert.Equal("replan", correction.StructuredOutputSchema!["title"]!.ToString());
        AssertDefaultGuidance(correction);
        AssertNumericDomain(correction);
        Assert.Equal(1, Context(correction)["draft"]!["tasks"]![0]!["maxItems"]!.GetValue<int>());
        Assert.Contains("singleton ceiling", Context(correction)["diagnostics"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(state.Construction!.Candidate!.Extension.Tasks).MaxItems);
        Assert.False(state.Construction.Candidate.LocalReviewed);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));

        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, state.Plan!.Root.Tasks.Single(task => task.Id == "iterate").MaxItems);
        Assert.Equal(7, state.Plan.Root.Tasks.Single(task => task.Id == "retained").Outputs[0].Value.Number);
        Assert.Equal(6, state.ModelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedDraftOrCorrectionResumesItsExactPresentationAndAccounting(bool repair)
    {
        var (state, runtime) = await Ready();
        if (repair)
        {
            state = await Advance(state, runtime);
            state = await Advance(state, runtime);
            Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        }
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = 2;
        historical.Request.Options["construction_presentation_version"] = 2;
        var retainedReview = PlanningCompactConstruction.DraftSemantics(historical, historical.Construction.Compact.CurrentStep!, review: true);
        Assert.Contains("Finite supported capacity must respect explicit original bounds; unspecified capacity remains unverified. Runtime ceilings still apply.", retainedReview, StringComparison.Ordinal);
        Assert.DoesNotContain("An unspecified input length does not require unlimited capacity", retainedReview, StringComparison.Ordinal);
        historical.Construction.Compact.PresentationVersion = null;
        historical.Request.Options.Remove("construction_presentation_version");
        var request = PlanningConstruction.DraftRequest(historical, repair);
        Assert.DoesNotContain("existing TaskPlan default", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        request.StructuredOutputStrict = true; request.UseBackgroundMode = true;
        request = PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        state.Construction!.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = repair ? "replan" : PlanningConstruction.DraftPurpose, Request = request };
        state.ModelCalls++;
        if (repair) { state.ReplanAttempts++; state.Construction.Candidate!.Corrections++; }
        var calls = state.ModelCalls; var repairs = state.ReplanAttempts;
        var accepted = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var receipt = Wire(request, Draft(request, 2));
        var recovery = new TestRuntime { Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return receipt; // The already retained response, never a regenerated request.
        } };
        state = await Advance(PlannerFixture.Clone(state), recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Single(recovery.Calls);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(repairs, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Equal(2, Assert.Single(state.Construction!.Candidate!.Extension.Tasks).MaxItems);
        Assert.False(state.Construction.Candidate.LocalReviewed);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    private static void AssertDefaultGuidance(LLMRequest request)
    {
        Assert.Contains("the TaskPlan default maxItems=" + new PlanTask().MaxItems,
            request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, request.StructuredOutputSchema["title"]!.ToString(), 8000).EstimatedInputTokens, 1, 8000);
    }

    private static void AssertNumericDomain(LLMRequest request)
    {
        foreach (var count in new[] { 1, 2, 10000 })
            Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Draft(request, count)).Json!, request.StructuredOutputSchema!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Draft(request, 10001)).Json!, request.StructuredOutputSchema!));
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(int initialCapacity = 1, bool callerInput = false, int presentation = 4)
    {
        var reviews = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            var context = Context(request);
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                return Wire(request, new() { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                    ["clauses"] = new JsonArray(new JsonObject { ["text"] = context["sources"]![0]!["text"]!.ToString(), ["kind"] = "requirement", ["interfaces"] = "none" }) });
            if (purpose == PlanningCompactConstruction.SelectPurpose)
                return Wire(request, new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["evidenceRequests"] = null, ["clarifications"] = null,
                    ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "foreach", ["objective"] = "Process all original numbers.",
                        ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = callerInput
                            ? new JsonArray(new JsonObject { ["kind"] = "input", ["source"] = "values" }) : new JsonArray(),
                        ["dependencyTaskIds"] = new JsonArray(),
                        ["expectedOutputs"] = new JsonArray("Iteration context for each original number."), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["groupId"] = null } });
            if (purpose is PlanningConstruction.DraftPurpose or "replan") return Wire(request, Draft(request, purpose == "replan" ? 2 : initialCapacity, callerInput));
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return Wire(request, new() { ["findings"] = reviews++ == 0 && initialCapacity == 1 ? new JsonArray(new JsonObject
                { ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0", ["evidence"] = new JsonArray("/requirements/0", "/draft/tasks/0"),
                    ["explanation"] = "The singleton ceiling rejects the explicitly supplied two-item array.", ["uncertainty"] = "No execution has occurred." }) : new JsonArray(),
                ["uncertainty"] = "Future body work and execution remain unverified.", ["evidenceRequests"] = null });
        } };
        var state = await Advance(PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "loop-capacity", Prompt = callerInput
            ? "Process every value of caller input values, a numeric array with no declared length bound, preserving order and repetitions."
            : "Process all original numeric values [1,2] through repeated local work.",
            Options = new() { ["automatic_correction"] = true } } }, presentation), runtime);
        Assert.Empty(state.Diagnostics);
        // This fixture starts at loop selection; interface extraction is covered separately.
        if (callerInput) state.Requirements!.Inputs = [new() { Name = "values", Required = true,
            Type = new() { Kind = "array", Items = new() { Kind = "number" } } }];
        state.Plan = new() { Inputs = state.Requirements!.Inputs ?? [], Root = new() { Tasks = [new() { Id = "retained", Kind = "value", Objective = "Retained independent value.",
            Outputs = [new("value", new() { Kind = "number", Number = 7 })] }] } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        return (state, runtime);
    }

    private static JsonObject Draft(LLMRequest request, int maximum, bool callerInput = false) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.DeepClone(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
        {
            Tasks = [new() { Id = "iterate", Kind = "foreach", Objective = "Process all original numbers.", MaxItems = maximum, MaxConcurrency = 1,
                Items = callerInput ? new() { Kind = "input", Source = "values" }
                    : new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = 2 }] }, Body = new() }],
            RequirementIds = ["req_0"]
        }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };

    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
