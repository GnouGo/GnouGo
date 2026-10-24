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
    [InlineData(5)]
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
        if (presentation is 3 or 4 or 5)
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

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task AcceptedLoopConsumersAndRootClosuresRetainCapacityPolicyWithoutChangingCompilerFacts(int presentation, bool closeRoot)
    {
        var state = await AfterLoop(presentation, closeRoot);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var intermediate in new[] { false, true })
        {
            var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate);
            Assert.Equal(presentation == 5 ? 1 : 0, request.Prompt.Split(CapacityPolicy, StringSplitOptions.None).Length - 1);
            Assert.Equal("passed", context["constructionStage"]!["preflight"]!["status"]!.ToString());
            Assert.Equal(!closeRoot, context["constructionStage"]!["preflight"]!["scopeOpen"]!.GetValue<bool>());
            var imported = Assert.Single(context["interfaces"]!.AsArray())!;
            Assert.Equal("iterate", imported["binding"]!["source"]!.ToString());
            Assert.Equal("array", imported["schema"]!["type"]!.ToString());
            Assert.Equal("number", imported["schema"]!["items"]!["type"]!.ToString());
            Assert.Contains("preserving order and repetitions", context["requirements"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        }
        Assert.Null(Assert.Single(state.Requirements!.Inputs!).Type.MaxItems);
        Assert.Equal(1000, state.Plan!.Root.Tasks.Single(task => task.Id == "iterate").MaxItems);
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task ExplicitKnownCountStillProducesABusinessFindingUnderSharedCapacityPolicy()
    {
        var (state, runtime) = await Ready(presentation: 5);
        var accepted = PlanningConstruction.Hash(state.Plan);
        state = await Advance(state, runtime);
        state = await Advance(state, runtime);
        Assert.Equal(1, runtime.Calls[^1].Prompt.Split(CapacityPolicy, StringSplitOptions.None).Length - 1);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Contains("singleton ceiling", state.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(1, Assert.Single(state.Construction.Candidate!.Extension.Tasks).MaxItems);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task IssuedConsumerReviewWithoutTheNewHintRetainsExactBytesAndAccounting(int presentation)
    {
        var state = await AfterLoop(presentation, closeRoot: false);
        var construction = state.Construction!;
        var (context, generated) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        // Retain the old request, whose non-foreach hints did not include the capacity policy.
        var prompt = generated.Prompt.Replace(CapacityPolicy + " ", "", StringComparison.Ordinal);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, prompt,
            generated.StructuredOutputSchema!.AsObject(), construction.Limits.ReviewOutputTokens);
        construction.PendingReviewContext = context;
        construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            construction.Candidate!.Hash + "\n" + context.ToJsonString());
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued };
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.DoesNotContain(CapacityPolicy, request.Prompt, StringComparison.Ordinal);
            return Wire(request, Reviewed(request));
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("iterate", state.Plan!.Root.Tasks.Single(task => task.Id == "consume").Outputs[0].Value.Source);
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
            return Wire(request, Reviewed(request, reviews++ == 0 && initialCapacity == 1));
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

    private const string CapacityPolicy = "An unspecified input length does not require unlimited capacity. Preserve every item within admitted capacity; excess rejects the input, never returns truncated success. Report conflicts with explicit required capacities or known required item counts. Absent bounds alone are not defects or missing business facts; unspecified capacity remains unverified. Runtime ceilings still apply.";

    private static async Task<PlanningSession> AfterLoop(int presentation, bool closeRoot)
    {
        var (state, _) = await Ready(initialCapacity: 1000, callerInput: true, presentation: presentation);
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Commitments = [] };
        state.Plan!.Root.Tasks.Add(new()
        {
            Id = "iterate", Kind = "foreach", Objective = "Preserve every original number.", MaxItems = 1000, MaxConcurrency = 2, Parallel = true,
            Items = new() { Kind = "input", Source = "values" },
            Body = new() { Outputs = [new("value", new() { Kind = "item" })] }
        });
        var construction = state.Construction!;
        construction.OpenScopes = ["/root"];
        construction.Requirements[0].Status = closeRoot ? "closed" : "in_progress";
        construction.CandidateFailureIdentityVersion = 1;
        construction.Compact!.CurrentStep = new()
        {
            Id = closeRoot ? "finish" : "consume", Kind = closeRoot ? "close" : "value", Scope = "/root",
            Objective = "Retain the ordered loop result.", RequirementIds = ["req_0"],
            Inputs = [new() { Kind = "output", Source = "iterate", Port = "value" }],
            DiscoveryComplete = true, BaseHash = PlanningConstruction.Hash(state.Plan)
        };
        construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["req_0"], CloseScopes = closeRoot ? ["/root"] : [], Complete = closeRoot,
            Outputs = closeRoot ? [new("result", new() { Kind = "output", Source = "iterate", Port = "value" })] : [],
            Tasks = closeRoot ? [] : [new() { Id = "consume", Kind = "value", Objective = "Retain the ordered loop result.",
                Outputs = [new("value", new() { Kind = "output", Source = "iterate", Port = "value" })] }]
        });
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(construction.Candidate.Plan, state.Catalog!, state.Request,
            construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        return state;
    }

    private static JsonObject Reviewed(LLMRequest request, bool insufficient = false)
    {
        var findings = insufficient ? new JsonArray(new JsonObject
        {
            ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0",
            ["explanation"] = "The singleton ceiling rejects the explicitly supplied two-item array.", ["uncertainty"] = "No execution has occurred."
        }) : new JsonArray();
        if (HybridWorkflowPlanner.ConvergentConstructionReviewIssued(request.StructuredOutputSchema))
        {
            foreach (var finding in findings)
            {
                finding!["obligation"] = "/requirements/0"; finding["implementation"] = "/draft/tasks/0";
                finding["fact"] = null; finding["requiredBinding"] = null;
            }
            return new() { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = findings },
                ["uncertainty"] = "Future work and execution remain unverified." };
        }
        foreach (var finding in findings) finding!["evidence"] = new JsonArray("/requirements/0", "/draft/tasks/0");
        return new() { ["findings"] = findings, ["evidenceRequests"] = null, ["inconclusive"] = null,
            ["uncertainty"] = "Future body work and execution remain unverified." };
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
