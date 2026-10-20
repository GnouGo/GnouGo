using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactCandidateFailureIdentityTests
{
    [Fact]
    public async Task AReviewedIntermediateCheckpointAllowsTheSamePlanWithAClosedGroupFrontier()
    {
        var (state, runtime) = await Ready();
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => Wire(request, Draft(request, Body(export: true)));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var rejectedPlan = state.Construction!.Candidate!.Hash;
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(1, state.Construction.CandidateFailureIdentityVersion);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            AssertScopeTypes(request);
            return Wire(request, Finding());
        };
        state = await Advance(state, runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.StartsWith("candidate:v1:", Assert.Single(state.Construction!.FailedCandidates), StringComparison.Ordinal);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            return Wire(request, Draft(request, Body(export: false)));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        runtime.Respond = (request, _) => Wire(request, Reviewed());
        state = await Advance(state, runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Null(state.Construction.CandidateFailureIdentityVersion);
        Assert.Equal(2, state.Construction.Checkpoints.Count);
        Assert.Single(state.Plan!.Groups[0].Body.Tasks);
        Assert.Empty(state.Plan.Groups[0].Body.Outputs);
        var accepted = PlanningConstruction.Hash(state.Plan);
        SelectClose(state);
        runtime.Respond = (request, _) => Wire(request, Draft(request, Close()));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(rejectedPlan, state.Construction!.Candidate!.Hash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.DoesNotContain("/groups/worker/body", state.Construction.Candidate.OpenScopes);
        runtime.Respond = (request, _) =>
        {
            AssertScopeTypes(request);
            Assert.Equal("number", Context(request)["scopeInterface"]!["outputs"]![0]!["schema"]!["type"]!.ToString());
            return Wire(request, Reviewed());
        };
        for (var i = 0; i < 2 && state.Construction!.Candidate is not null; i++) state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Candidate);
        Assert.Null(state.Construction.CandidateFailureIdentityVersion);
        Assert.Equal(rejectedPlan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal("number", PlanningCompactConstruction.GroupInterface(state, "worker")!["outputs"]![0]!["schema"]!["type"]!.ToString());
        Assert.Equal("array", Assert.Single(state.Requirements!.Outputs!).Type.Kind);
        Assert.Equal("in_progress", Assert.Single(state.Construction.Requirements).Status);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(3, state.Construction.Checkpoints.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactPendingDraftRetainsItsCapturedFailureIdentity(bool current)
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == PlanningConstruction.DraftPurpose));
        Assert.Equal(1, saved.Construction!.CandidateFailureIdentityVersion);
        if (!current) saved.Construction.CandidateFailureIdentityVersion = null;
        var issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls; var corrections = saved.ReplanAttempts;
        saved.Status = PlanningStatus.Generating;
        runtime.Calls.Clear();
        runtime.Respond = (request, _) => Wire(request, Draft(request, Body(export: true)));
        state = await Advance(saved, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal(current ? 1 : (int?)null, state.Construction!.CandidateFailureIdentityVersion);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        calls = saved.ModelCalls; saved.Status = PlanningStatus.Generating; runtime.Calls.Clear();
        runtime.Respond = (request, _) => Wire(request, Finding());
        state = await Advance(saved, runtime);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        var rejected = Assert.Single(state.Construction!.FailedCandidates);
        Assert.Equal(current, rejected.StartsWith("candidate:v1:", StringComparison.Ordinal));
        if (!current) Assert.Equal(state.Construction.Candidate!.Hash, rejected);
        Assert.Single(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task FailureIdentityUsesSemanticSetsAndPreservesAllHistoricalRejections()
    {
        var (state, _) = await Ready();
        var construction = state.Construction!;
        construction.CandidateFailureIdentityVersion = 1;
        var candidate = PlanningConstruction.Apply(state, Body(export: true));
        construction.Compact!.CurrentStep!.CloseRequirementIds = ["req_0", "other"];
        construction.Compact.PendingCommitments = new()
        {
            ["req_0"] = [new("read") { Result = new("value", "result") }, new("check")],
            ["other"] = [new("write")]
        };
        var identity = PlanningConstruction.FailureIdentity(state, candidate);
        PlanningConstruction.RecordRejection(state, candidate);
        candidate.OpenScopes.Reverse();
        construction.Compact.CurrentStep.CloseRequirementIds.Reverse();
        construction.Compact.PendingCommitments = new()
        {
            ["other"] = [new("write")],
            ["req_0"] = [new("check"), new("read") { Result = new("value", "result") }, new("check")]
        };
        state.ModelCalls += 4; state.ReplanAttempts++;
        Assert.Equal(identity, PlanningConstruction.FailureIdentity(state, candidate));
        Assert.True(PlanningConstruction.PreviouslyRejected(state, candidate));
        candidate.Extension.CloseScopes = ["/groups/worker/body"];
        candidate.OpenScopes.Remove("/groups/worker/body");
        Assert.False(PlanningConstruction.PreviouslyRejected(state, candidate));
        PlanningConstruction.RecordRejection(state, candidate);
        Assert.True(PlanningConstruction.PreviouslyRejected(state, candidate));
        var closed = PlanningConstruction.FailureIdentity(state, candidate);
        candidate.BaseHash = new string('f', 64);
        Assert.NotEqual(closed, PlanningConstruction.FailureIdentity(state, candidate));
        Assert.False(PlanningConstruction.PreviouslyRejected(state, candidate));
        construction.FailedCandidates.Add(candidate.Hash);
        Assert.True(PlanningConstruction.PreviouslyRejected(state, candidate));
        construction.CandidateFailureIdentityVersion = null;
        Assert.Equal(candidate.Hash, PlanningConstruction.FailureIdentity(state, candidate));
    }

    [Fact]
    public async Task OptionalFailureMetadataPreservesHistoricalSerializationAndArtifactIdentity()
    {
        var (state, _) = await Ready();
        state.Yaml = "unchanged artifact identity fixture";
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var artifact = state.ComputeArtifactHash();
        Assert.DoesNotContain("candidateFailureIdentityVersion", before, StringComparison.Ordinal);
        state.Construction!.CandidateFailureIdentityVersion = 1;
        var restored = PlannerFixture.Clone(state);
        Assert.Equal(1, restored.Construction!.CandidateFailureIdentityVersion);
        Assert.Equal(artifact, restored.ComputeArtifactHash());
        restored.Construction.CandidateFailureIdentityVersion = null;
        Assert.Equal(before, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSelectionCannotNameAnOpenCallTargetButRetainsHistoricalIssuedSchemas(bool closed)
    {
        var (state, runtime) = await Ready();
        state.Construction!.Compact!.CurrentStep = null;
        if (closed) state.Construction.OpenScopes.Remove("/groups/worker/body");
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        var response = Selection(state);
        Assert.Equal(closed, PlanningContractValidation.ValidateInstance(response, schema).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        state.Construction.Compact.PresentationVersion = null;
        var historical = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, historical));
        state.Construction.Compact.PresentationVersion = 2;
        var request = PlanningCompactConstruction.Request("Retained exact selection instructions.", PlanningCompactConstruction.SelectionContext(state),
            historical, PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.ClientRequestId = "retained-selection";
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        runtime.Calls.Clear();
        runtime.Respond = (_, _) => new() { Json = response };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        if (closed) Assert.Empty(state.Diagnostics);
        else Assert.Equal("CONSTRUCTION_STEP_INVALID", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(0, state.ReplanAttempts);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone();
            return Wire(request, request.StructuredOutputSchema["properties"]!["clauses"] is not null
                ? new() { ["sourceId"] = source, ["clauses"] = new JsonArray(new JsonObject
                    { ["text"] = Context(request)["sources"]![0]!["text"]!.DeepClone(), ["kind"] = "requirement", ["interfaces"] = "output" }) }
                : new() { ["sourceId"] = source, ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray(new JsonObject
                    { ["name"] = "result", ["required"] = true, ["type"] = new JsonObject { ["kind"] = "array", ["nullable"] = false,
                        ["items"] = new JsonObject { ["kind"] = "number", ["nullable"] = false }, ["minItems"] = null, ["maxItems"] = null } }) });
        } };
        var state = await Advance(new() { Request = new() { TenantId = "scoped-failure",
            Prompt = "Use reusable numeric work to return an ordered numeric array as result.",
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Groups = [new() { Id = "worker", Inputs = [new() { Name = "item", Type = new() { Kind = "number" } }] }] };
        state.Construction!.OpenScopes = ["/root", "/groups/worker/body"];
        state.Construction.Checkpoints.Add(new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(state.Plan), RequirementIds = ["req_0"] });
        state.Construction.Requirements[0].Status = "in_progress";
        state.Construction.Compact!.CurrentStep = new() { Id = "calculate", BaseHash = PlanningConstruction.Hash(state.Plan),
            Scope = "/groups/worker/body", Kind = "value", Objective = "Produce the reusable numeric result.", RequirementIds = ["req_0"],
            Inputs = [new() { Kind = "input", Source = "item" }], OutputNames = ["result"], DiscoveryComplete = true };
        return (state, runtime);
    }

    private static void SelectClose(PlanningSession state) => state.Construction!.Compact!.CurrentStep = new()
    {
        Id = "close", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/groups/worker/body", Kind = "close",
        Objective = "Export the accepted scalar result and close its reusable group.", RequirementIds = ["req_0"], OutputNames = ["result"],
        Inputs = [new() { Kind = "output", Source = "calculate", Port = "result" }], InputTaskIds = ["calculate"], DiscoveryComplete = true
    };
    private static PlanningConstructionExtension Body(bool export) => new()
    {
        Scope = "/groups/worker/body", RequirementIds = ["req_0"],
        Tasks = [new() { Id = "calculate", Kind = "value", Objective = "Produce the reusable numeric result.",
            Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "item" }, new() { Kind = "number", Number = 2 }] })] }],
        Outputs = export ? [new("result", new() { Kind = "output", Source = "calculate", Port = "result" })] : []
    };
    private static PlanningConstructionExtension Close() => new()
    {
        Scope = "/groups/worker/body", RequirementIds = ["req_0"], CloseScopes = ["/groups/worker/body"],
        Outputs = [new("result", new() { Kind = "output", Source = "calculate", Port = "result" })]
    };
    private static JsonObject Selection(PlanningSession state) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "call", ["groupId"] = "worker", ["objective"] = "Use the reusable work.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray("result"), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray("result") }
    };
    private static void AssertScopeTypes(LLMRequest request)
    {
        var context = Context(request);
        Assert.Contains("outputs: public /root results", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("array", context["outputs"]![0]!["type"]!["kind"]!.ToString());
        Assert.Equal("/groups/worker/body", context["step"]!["scope"]!.ToString());
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose, 4000).EstimatedInputTokens, 1, 4000);
    }
    private static JsonObject Draft(LLMRequest request, PlanningConstructionExtension extension) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.DeepClone(), ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["requirements"] = null, ["commitments"] = null, ["discoveryRequests"] = null, ["evidenceRequests"] = null,
        ["clarifications"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static JsonObject Finding() => new()
    {
        ["findings"] = new JsonArray(new JsonObject { ["kind"] = "inconsistency", ["location"] = "/draft/outputs/0",
            ["evidence"] = new JsonArray("/draft/outputs/0", "/outputs/0"),
            ["explanation"] = "The tentative export needs a scoped correction.", ["uncertainty"] = "Business interpretation remains unverified." }),
        ["uncertainty"] = "No execution has occurred.", ["evidenceRequests"] = null
    };
    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
