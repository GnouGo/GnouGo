using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InitialRequirementsCorrectionTests
{
    [Theory]
    [InlineData("missing", "REQUIREMENTS_INVALID", "/requirements")]
    [InlineData("summary", "REQUIREMENTS_INVALID", "/requirements/summary")]
    [InlineData("id", "REQUIREMENTS_INVALID", "/requirements/outcomes/0/id")]
    [InlineData("description", "REQUIREMENTS_INVALID", "/requirements/outcomes/0/description")]
    [InlineData("duplicate", "REQUIREMENTS_INVALID", "/requirements/outcomes/1/id")]
    [InlineData("inputs", "REQUIREMENTS_INPUTS_REQUIRED", "/requirements/inputs")]
    public async Task InitialSemanticDefectRegeneratesOnceThenRequiresArtifactReview(string defect, string code, string location)
    {
        var runtime = new TestRuntime();
        var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) =>
        {
            var response = TestRuntime.Response(request, runtime.Proposal);
            if (runtime.Calls.Count == 1) Break(response.Json!, defect);
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json, request.StructuredOutputSchema!));
            return response;
        };
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == code && d.Location == location);
        Assert.Null(state.Requirements); Assert.Null(state.Plan); Assert.Null(state.Yaml);
        var originalRequest = JsonSerializer.Serialize(runtime.Calls[0], PlanningJsonContext.Default.LLMRequest);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash); Assert.Null(state.Request.Baseline);
        Assert.Equal("regenerate", state.Request.Options["automatic_correction_progress"]!["action"]!.ToString());
        Assert.Contains(code, runtime.Calls[1].Prompt);
        Assert.Contains(location, runtime.Calls[1].Prompt);
        Assert.False(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
        Assert.Equal(originalRequest, JsonSerializer.Serialize(runtime.Calls[0], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, runtime.Discoveries);
        PlanningArtifactApproval.Verify(state);
        Assert.Equal((await PlannerFixture.RunAsync(new TestRuntime())).Yaml, state.Yaml);
        foreach (var request in runtime.Calls)
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()),
                1, state.Request.Generation.MaxInputTokensPerRequest);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("outputs")]
    public async Task InvalidInitialRequirementCollectionsStillRequireFullSchemaValidation(string defect)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) =>
        {
            var response = TestRuntime.Response(request, runtime.Proposal);
            if (runtime.Calls.Count == 1)
            {
                if (defect == "empty") response.Json!["requirements"]!["outcomes"] = new JsonArray();
                else response.Json!["requirements"]!["outputs"] = null;
                Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response.Json, request.StructuredOutputSchema!));
            }
            return response;
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains("PLANNING_RESPONSE_INVALID", runtime.Calls[1].Prompt);
        Assert.NotEmpty(state.Requirements!.Outcomes); Assert.NotNull(state.Requirements.Outputs);
    }

    [Fact]
    public async Task DiscoveryMayLeaveRequirementsNullAndItsReceiptsSurviveInitialCorrection()
    {
        var catalog = new Metadata(); var runtime = new TestRuntime { Capabilities = catalog };
        var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) =>
        {
            if (runtime.Calls.Count <= 2) return TestRuntime.Response(request, new()
                { DiscoveryRequests = [new(runtime.Calls.Count == 1 ? "first" : "second")] });
            var response = TestRuntime.Response(request, runtime.Proposal);
            if (runtime.Calls.Count == 3) Break(response.Json!, "missing");
            return response;
        };
        var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 2; i++)
        {
            state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
            Assert.Null(state.Requirements); Assert.Empty(state.Diagnostics); Assert.Equal(0, state.ReplanAttempts);
        }
        var discovered = JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(new[] { "first", "second" }, catalog.Reads);
        Assert.Equal(discovered, JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("summary")]
    [InlineData("malformed")]
    public async Task SecondInvalidProposalStopsWithoutAnotherCorrectionOrPartialAcceptance(string second)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["automatic_correction"] = true;
        state.Request.MaxReplanAttempts = 5; // A larger allowance does not repeat regeneration.
        runtime.Respond = (request, _) =>
        {
            var response = TestRuntime.Response(request, runtime.Proposal);
            if (runtime.Calls.Count > 1 && second == "malformed") response.Json = new JsonObject();
            else Break(response.Json!, runtime.Calls.Count == 1 ? "missing" : second);
            return response;
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.Plan); Assert.Null(state.Requirements); Assert.Null(state.Yaml);
        Assert.NotEmpty(state.Diagnostics);
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("repairs")]
    [InlineData("baseline")]
    [InlineData("uncertain")]
    public async Task MissingRequirementsDoesNotBypassAdmissionOrAnExistingBaseline(string blocker)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) => { var response = TestRuntime.Response(request, runtime.Proposal); Break(response.Json!, "missing"); return response; };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        if (blocker == "calls") state.Request.MaxModelCalls = 1;
        if (blocker == "repairs") state.Request.MaxReplanAttempts = 0;
        if (blocker == "baseline") state.Request.Baseline = runtime.Proposal.Plan;
        if (blocker == "uncertain")
        {
            state.Status = PlanningStatus.Stopped;
            state.PendingCall = new() { Id = "unresolved", Purpose = "tasks", Request = runtime.Calls[0] };
        }
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.Yaml);
        if (blocker == "uncertain") Assert.Equal("unresolved", state.PendingCall!.Id);
    }

    [Fact]
    public async Task OmittedPolicyRetainsTheHistoricalRetryPath()
    {
        var results = new List<(int Calls, int Replans, string Status)>();
        foreach (var explicitDisabled in new[] { false, true })
        {
            var runtime = new TestRuntime(); var state = PlannerFixture.Session();
            if (explicitDisabled) state.Request.Options["automatic_correction"] = false;
            runtime.Respond = (request, _) => { var response = TestRuntime.Response(request, runtime.Proposal); Break(response.Json!, "missing"); return response; };
            state = await PlannerFixture.RunAsync(runtime, state);
            Assert.Null(state.Request.Options["automatic_correction_progress"]);
            results.Add((state.ModelCalls, state.ReplanAttempts, state.Status));
        }
        Assert.Equal(results[0], results[1]);
        Assert.Equal((3, 2, PlanningStatus.Stopped), results[0]);
    }

    [Fact]
    public async Task CorrectionMayAskForIntentInsteadOfAcceptingInventedRequirements()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) =>
        {
            if (runtime.Calls.Count == 1) { var response = TestRuntime.Response(request, runtime.Proposal); Break(response.Json!, "missing"); return response; }
            return TestRuntime.Response(request, new() { Clarifications = [new("subject", "Which subject should be used?", [], null)] });
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        Assert.Null(state.Requirements); Assert.Null(state.Plan); Assert.Null(state.ApprovedHash);
        Assert.Single(state.PendingQuestions!); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("REQUIREMENTS_OUTPUTS_REQUIRED", "/requirements/outputs")]
    [InlineData("REQUIREMENTS_INVALID", "/requirements/outputs")]
    public async Task MissingInitialInterfaceRoutesWithoutChangingExistingAcceptedIntent(string code, string path)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["automatic_correction"] = true;
        state.ModelCalls = 1; state.Diagnostics = [new(code, path, "Retained initial interface finding")];
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal("Return a greeting", state.Requirements!.Summary);
    }

    [Fact]
    public async Task CommittedInitialCorrectionUsesTheIssuedSchemaAtAnExhaustedAllowance()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["automatic_correction"] = true; state.Request.MaxModelCalls = 2; state.Request.MaxReplanAttempts = 1;
        runtime.Respond = (request, _) => { var response = TestRuntime.Response(request, runtime.Proposal); if (runtime.Calls.Count == 1) Break(response.Json!, "missing"); return response; };
        var completed = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, completed.Status);
        var pending = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == "replan"));
        var original = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var receipt = TestRuntime.Response(pending.PendingCall.Request, runtime.Proposal);
        var replay = new ReceiptOnly(runtime, pending.Request.TenantId, original, receipt);
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, replay, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Equal(completed.Yaml, recovered.Yaml);
        Assert.Equal(2, recovered.ModelCalls); Assert.Equal(1, recovered.ReplanAttempts);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, replay.Reads);
        Assert.Null(recovered.ApprovedHash);
    }

    [Fact]
    public async Task ActualFreshPromptExplainsRequirementsAndSchemaHintsChangeOnlyAnnotations()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var request = Assert.Single(runtime.Calls);
        Assert.Contains("first plan must include requirements and caller/output interfaces", request.Prompt);
        Assert.Contains("Discovery may leave requirements null", request.Prompt);
        Assert.Contains("plan:null and requirements:null", request.Prompt);
        Assert.Contains("First plan needs requirements/interfaces", request.StructuredOutputSchema!.ToJsonString());
        var initial = PlannerFixture.Session(); initial.Catalog = state.Catalog;
        var full = PlanningSchemas.FullProposal(initial, compact: false);
        var historical = PlanningSchemas.Compact(full.DeepClone().AsObject());
        var current = PlanningSchemas.Compact(full.DeepClone().AsObject(), compositionHints: true);
        foreach (var schema in new[] { historical, current })
            foreach (var (_, node) in PlanningSchemaReferences.Walk(schema, "", 0)) node.Remove("description");
        Assert.True(JsonNode.DeepEquals(historical, current));
        Assert.Null(historical["properties"]!["requirements"]!["description"]);
    }

    private sealed class ReceiptOnly(TestRuntime runtime, string tenant, string issued, LLMResponse receipt) : IPlanningRuntime
    {
        internal int Reads;
        public ICapabilityCatalog Capabilities => runtime.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("No discovery on receipt recovery");
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal("replan", purpose); Assert.Equal(0, Reads++);
            return Task.FromResult(receipt);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct)
        { Assert.Equal(tenant, request.Request.TenantId); return runtime.ValidateAsync(request, ct); }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => runtime.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => runtime.CheckpointAsync(state, ct);
    }

    private sealed class Metadata : ICapabilityCatalog
    {
        internal readonly List<string> Reads = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapabilitySource>>([new("first", "First source"), new("second", "Second source")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { Reads.Add(sourceId); return Task.FromResult(new CapabilityPage(sourceId, cursor, [], null)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException("No operations offered");
    }

    private static void Break(JsonNode response, string defect)
    {
        if (defect == "missing") { response["requirements"] = null; return; }
        var requirements = response["requirements"]!;
        switch (defect)
        {
            case "summary": requirements["summary"] = " "; break;
            case "id": requirements["outcomes"]![0]!["id"] = " "; break;
            case "description": requirements["outcomes"]![0]!["description"] = " "; break;
            case "duplicate": requirements["outcomes"]!.AsArray().Add(requirements["outcomes"]![0]!.DeepClone()); break;
            case "inputs": requirements["inputs"] = null; break;
            default: throw new ArgumentException("Unknown fixture defect", nameof(defect));
        }
    }
}
