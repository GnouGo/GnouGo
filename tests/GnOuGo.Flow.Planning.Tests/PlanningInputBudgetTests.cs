using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningInputBudgetTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;

    [Fact]
    public async Task AdvisoryRuntimeWarningsAndReportedUsageSurviveRestartWithoutChangingDecisions()
    {
        var state = PlannerFixture.Session(); state.Request.Options["token_threshold_policy"] = "advisory";
        var runtime = new TestRuntime { Respond = (_, _) =>
        {
            PlanningTokenThresholds.Warn("workflow.plan", "total_tokens", 1500, 1000);
            return new() { Json = new JsonObject { ["answer"] = "received" }, Usage = new JsonObject
            { ["input_tokens"] = 900, ["output_tokens"] = 600, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 500 } } };
        } };
        await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.SelectPurpose, "Read retained evidence",
            PlanningSchemas.Object(("answer", PlanningSchemas.String())), Ct);
        state = PlannerFixture.Clone(state);
        var measurement = Assert.Single(state.RequestMeasurements!);
        Assert.Equal(900, measurement.InputTokens); Assert.Equal(600, measurement.OutputTokens); Assert.Equal(500, measurement.ReasoningTokens);
        Assert.Equal(1500, measurement.CumulativeInputTokens + measurement.CumulativeOutputTokens);
        var warning = Assert.Single(measurement.Warnings!);
        Assert.Equal("total_tokens", warning.Kind); Assert.Equal(1500, warning.Current); Assert.Equal(1000, warning.Threshold);
        Assert.Equal("complete", measurement.Result); Assert.Empty(state.Diagnostics); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task PhysicalOutputExhaustionRetainsExactMetricsAndAcceptsNoFragment()
    {
        var state = PlannerFixture.Session(); state.Request.Options["token_threshold_policy"] = "advisory";
        var runtime = new TestRuntime { Respond = (request, _) => new() { CompletionStatus = "output_limit", Text = "",
            Usage = new JsonObject { ["input_tokens"] = 123, ["output_tokens"] = request.MaxTokens,
                ["reasoning_tokens"] = request.MaxTokens } } };
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, "Construct a bounded fragment", PlanningSchemas.Object(("answer", PlanningSchemas.String())), Ct));
        Assert.Equal("MODEL_OUTPUT_LIMIT", error.Code); Assert.Equal("provider_output_allocation", error.Details!["limit_kind"]!.ToString());
        Assert.False(error.Details["replay_allowed"]!.GetValue<bool>());
        var measurement = Assert.Single(PlannerFixture.Clone(state).RequestMeasurements!);
        Assert.Equal("output_limit", measurement.Result); Assert.Equal(measurement.IssuedOutputTokens, measurement.OutputTokens);
        Assert.Equal(measurement.OutputTokens, measurement.ReasoningTokens); Assert.Single(runtime.Calls);
        Assert.Null(state.Construction!.Candidate); Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task AdvisoryOversizedRequestDispatchesOnceWithDurableWarningAndExactRecovery()
    {
        var state = PlannerFixture.Session(); state.Request.Options["token_threshold_policy"] = "advisory";
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["answer"] = "received" } } };
        var response = await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.SelectPurpose,
            new string('x', 96_000), PlanningSchemas.Object(("answer", PlanningSchemas.String())), Ct);
        Assert.Equal("received", response["answer"]!.ToString());
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        var measurement = Assert.Single(state.RequestMeasurements!);
        Assert.Equal("admitted", measurement.Admission);
        Assert.Contains("advisory", measurement.TargetWarning!);
        Assert.True(measurement.EstimatedInputTokens > measurement.InputLimit);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(state.Request.Options.ToJsonString(), PlannerFixture.Clone(state).Request.Options.ToJsonString());
    }

    [Fact]
    public async Task UnrelatedDetailedCatalogDoesNotExpandLocalPlanningRequests()
    {
        var catalog = new DescribedCatalog(); var model = new CurrentPlanningFixture.LiteralModel();
        var runtime = new TestRuntime { Capabilities = catalog, Respond = (request, _) => model.CallAsync(request, Ct).GetAwaiter().GetResult() };
        var state = PlannerFixture.Session(); state.Request.Prompt = "Return the required public numeric result named result with value 42.";
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); PlanningArtifactApproval.Verify(state);
        Assert.Equal(5, runtime.Calls.Count); Assert.Equal(0, catalog.Pages);
        Assert.All(runtime.Calls, request => Assert.DoesNotContain("Authoritative business port description", request.Prompt, StringComparison.Ordinal));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(12_000)]
    [InlineData(24_000)]
    public async Task SelectedCeilingStillStopsMandatoryOversizedRequestsWithoutEscalation(int ceiling)
    {
        var state = PlannerFixture.Session(); state.Request.Generation.MaxInputTokensPerRequest = ceiling;
        state.ModelCalls = 2; state.ReplanAttempts = 1;
        state.Usage = new() { Calls = 2, InputTokens = 9000, OutputTokens = 1000, TotalTokens = 10000, EstimatedCost = 0.07m, EstimatedCostCurrency = "EUR" };
        var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var runtime = new TestRuntime(); var options = state.Request.Options.ToJsonString();
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.SelectPurpose, new string('x', ceiling * 3), PlanningSchemas.Object(("answer", PlanningSchemas.String())), Ct));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Equal(ceiling, state.Request.Generation.MaxInputTokensPerRequest); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(options, state.Request.Options.ToJsonString()); Assert.Empty(runtime.Calls); Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsCannotReplacePendingRequestsOrUseAStaleRevision(bool pending)
    {
        var state = PlannerFixture.Session(); state.Status = PlanningStatus.Stopped; state.Revision = 5;
        state.ModelCalls = 2; state.ReplanAttempts = 1;
        if (pending) state.PendingCall = new() { Id = "retained:2", Purpose = PlanningConstruction.DraftPurpose, Request = new() { Prompt = "retained" } };
        state = PlannerFixture.Clone(state);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var runtime = new TestRuntime();
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "configure_generation", ExpectedRevision = pending ? 5 : 4,
            Generation = new() { MaxInputTokensPerRequest = 24_000, MaxOutputTokens = 8192 }
        }, runtime, Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls); Assert.Empty(runtime.Checkpoints);
    }

    private sealed class DescribedCatalog : ICapabilityCatalog
    {
        internal int Pages;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapabilitySource>>([new("source1", "First declared source"), new("source2", "Second declared source")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            Pages++;
            Assert.Null(cursor);
            return Task.FromResult(new CapabilityPage(sourceId, null, Enumerable.Range(0, sourceId == "source1" ? 9 : 4).Select(i =>
            {
                var id = sourceId + "_operation_" + i;
                var capability = new PlanningCapability
                {
                    Id = id, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = sourceId, Method = "operation_" + i,
                    EffectKind = "read", Description = string.Concat(Enumerable.Repeat("Declared operation metadata. ", 18)),
                    InputSchema = Contract(), OutputSchema = Contract()
                };
                return new CapabilitySummary(id, sourceId, capability.Method, capability.Description, capability.StepType,
                    capability.EffectKind, capability.Version, Operation: TaskOperations.Describe(capability));
            }).ToList(), null));
        }
        private static JsonObject Contract() => new()
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("value"),
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string",
                ["description"] = string.Concat(Enumerable.Repeat("Authoritative business port description. ", 12)) } }
        };
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) =>
            throw new InvalidOperationException("Unselected contracts must not be resolved.");
    }
}
