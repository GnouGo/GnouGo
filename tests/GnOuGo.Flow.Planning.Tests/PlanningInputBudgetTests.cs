using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningInputBudgetTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;

    [Fact]
    public async Task DiscoveryGrowthStopsBeforeThirdDispatchAndExplicitResumeRetainsAccounting()
    {
        // Sanitized reproduction: two discovery responses, 9 + 4 operations with
        // authoritative port descriptions. No provider names or private payloads.
        var catalog = new DescribedCatalog();
        var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => TestRuntime.Response(request, new()
        {
            Requirements = PlannerFixture.Requirements(),
            SourceId = runtime.Calls.Count <= 2 ? "source" + runtime.Calls.Count : null,
            Plan = runtime.Calls.Count <= 2 ? null : PlanningCorpus.Greeting()
        });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.PendingCall);
        Assert.Null(state.Plan); Assert.Equal(2, state.Discovery.Pages.Count);
        Assert.Equal(13, state.Discovery.Pages.Sum(p => p.Capabilities.Count));
        var finding = Assert.Single(state.Diagnostics);
        Assert.Equal("MODEL_INPUT_LIMIT", finding.Code);
        Assert.Equal("/phases/tasks", finding.Location);
        Assert.Contains("conservative", finding.Message);
        Assert.Contains("response schema", finding.Message);
        Assert.Contains("12000", finding.Message);
        Assert.Contains("before dispatch", finding.Message);
        Assert.Contains("resume", finding.Message);

        // Recovery alone must not advance or reset the stopped request budget.
        var planner = new HybridWorkflowPlanner();
        state.Usage = new() { Calls = 2, InputTokens = 9000, OutputTokens = 1000,
            TotalTokens = 10000, EstimatedCost = 0.07m, EstimatedCostCurrency = "EUR" };
        var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var identities = runtime.Calls.Select(c => c.ClientRequestId).ToArray();
        var options = state.Request.Options.ToJsonString();
        var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(12_000, state.Request.Generation.MaxInputTokensPerRequest);
        state = await planner.AdvanceAsync(state, new()
        {
            Kind = "configure_generation", ExpectedRevision = state.Revision,
            Generation = new() { MaxInputTokensPerRequest = 24_000 }
        }, runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Empty(state.Diagnostics);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(options, state.Request.Options.ToJsonString());
        Assert.Equal(8, state.Request.MaxModelCalls); Assert.Equal(2, state.Request.MaxReplanAttempts);
        Assert.Equal(8192, state.Request.Generation.MaxOutputTokens);
        Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(3, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(identities, runtime.Calls.Take(2).Select(c => c.ClientRequestId));
        Assert.StartsWith(state.Request.SessionId + ":3:", runtime.Calls[2].ClientRequestId);
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(1, runtime.Discoveries); Assert.Equal(2, catalog.Pages);
    }

    [Theory]
    [InlineData(12_000)]
    [InlineData(24_000)]
    public async Task SelectedCeilingStillStopsOversizedRequestsWithoutEscalation(int ceiling)
    {
        var runtime = new TestRuntime();
        var state = PlannerFixture.Session(); state.Request.Generation.MaxInputTokensPerRequest = ceiling;
        state.Request.Prompt = new string('x', ceiling * 3);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("MODEL_INPUT_LIMIT", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(ceiling, state.Request.Generation.MaxInputTokensPerRequest);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(runtime.Calls); Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsCannotReplacePendingRequestsOrUseAStaleRevision(bool pending)
    {
        var state = PlannerFixture.Session(); state.Status = PlanningStatus.Stopped; state.Revision = 5;
        state.ModelCalls = 2; state.ReplanAttempts = 1;
        if (pending) state.PendingCall = new() { Id = "retained:2", Purpose = "tasks", Request = new() { Prompt = "retained" } };
        state = PlannerFixture.Clone(state);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var runtime = new TestRuntime();
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "configure_generation", ExpectedRevision = pending ? 5 : 4,
            Generation = new() { MaxInputTokensPerRequest = 24_000 }
        }, runtime, Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls); Assert.Empty(runtime.Checkpoints);
    }

    private sealed class DescribedCatalog : ICapabilityCatalog
    {
        internal int Pages;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapabilitySource>>([new("source1", "First declared source"), new("source2", "Second declared source")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct)
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
