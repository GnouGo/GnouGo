using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiscoveryCallBudgetTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;

    [Fact]
    public async Task SixDiscoveryCallsLeaveAProposalAndOneRepair()
    {
        var catalog = new Pages(); var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => runtime.Calls.Count switch
        {
            <= 6 => TestRuntime.Response(request, Discovery(runtime.Calls.Count)),
            7 => new() { Json = new JsonObject { ["malformed"] = true } },
            _ => TestRuntime.Response(request, runtime.Proposal)
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(8, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Equal(6, catalog.Reads);
        Assert.All(runtime.Calls.Take(6), c => Assert.NotEqual("null", c.StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]?.ToString()));
        Assert.All(runtime.Calls.Skip(6), c => Assert.Equal("null", c.StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]?.ToString()));
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task EarlierProposalStillAllowsTwoRepairs()
    {
        var catalog = new Pages();
        var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => runtime.Calls.Count switch
        {
            <= 5 => TestRuntime.Response(request, Discovery(runtime.Calls.Count)),
            6 or 7 => new() { Json = new JsonObject { ["malformed"] = runtime.Calls.Count } },
            _ => TestRuntime.Response(request, runtime.Proposal)
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(8, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Equal(5, catalog.Reads); Assert.Null(state.ApprovedHash);
        Assert.All(runtime.Calls.Take(5), c => Assert.NotEqual("null", c.StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]?.ToString()));
        Assert.All(runtime.Calls.Skip(6), c => Assert.Equal("null", c.StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]?.ToString()));
        var context = Context(runtime.Calls[5].Prompt);
        Assert.Equal(3, context["budget"]!["remainingCalls"]!.GetValue<int>());
        Assert.True(context["budget"]!["discoveryAllowed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false, "DISCOVERY_INCOMPLETE")]
    [InlineData(true, "DISCOVERY_NOT_ALLOWED")]
    public async Task ClosedDiscoveryStopsWithoutFetchingOrAnotherRepair(bool forbiddenDiscovery, string code)
    {
        var catalog = new Pages(); var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => TestRuntime.Response(request, forbiddenDiscovery ? Discovery(6) : new() { Requirements = PlannerFixture.Requirements() });
        var state = PlannerFixture.Session(); state.ModelCalls = 6;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(code, Assert.Single(state.Diagnostics).Code);
        Assert.Single(runtime.Calls); Assert.Equal(7, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, catalog.Reads); Assert.Null(state.PendingCall); Assert.Null(state.Yaml);
        var recovered = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(7, recovered.ModelCalls); Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData(1, 2, 0, false)]
    [InlineData(2, 2, 0, false)]
    [InlineData(3, 2, 0, true)]
    [InlineData(4, 2, 0, true)]
    [InlineData(8, 0, 6, true)]
    [InlineData(8, 0, 7, false)]
    public async Task SmallAndZeroRepairBudgetsKeepGenerationAvailable(int ceiling, int repairs, int used, bool discovery)
    {
        var state = PlannerFixture.Session(); state.Request.MaxModelCalls = ceiling;
        state.Request.MaxReplanAttempts = repairs; state.ModelCalls = used;
        var runtime = new TestRuntime { Capabilities = new Pages() };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(used + 1, state.ModelCalls);
        Assert.Equal(discovery, runtime.Calls[0].StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]?.ToString() != "null");
    }

    [Fact]
    public async Task LastRepairCannotBeUsedForDiscoveryEvenWithSpareCalls()
    {
        var state = PlannerFixture.Session(); state.Request.MaxModelCalls = 20;
        state.ReplanAttempts = 1; state.Diagnostics = [new("PLANNING_RESPONSE_INVALID", "/", "Invalid response")];
        var runtime = new TestRuntime { Capabilities = new Pages() };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ReplanAttempts);
        Assert.Equal("null", Assert.Single(runtime.Calls).StructuredOutputSchema!["properties"]!["discoveryRequests"]!["type"]?.ToString());
    }

    [Fact]
    public async Task PendingDiscoveryRetainsIssuedSchemaAndIdentityAtTheNewBoundary()
    {
        var state = PlannerFixture.Session(); state.Catalog = new();
        state.Discovery.Sources = [new("source", "Declared operations"), new("unused", "Unrelated")];
        var schema = PlanningSchemas.Proposal(state);
        state.ModelCalls = 6;
        state.PendingCall = new() { Id = "original:6", Purpose = "tasks", Request = new() { ClientRequestId = "original:6", StructuredOutputSchema = schema } };
        var runtime = new TestRuntime { Capabilities = new Pages() };
        runtime.Respond = (request, _) => TestRuntime.Response(request, Discovery(6));
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new(), runtime, Ct);
        Assert.Empty(state.Diagnostics); Assert.Equal(PlanningPhase.Discovery, state.Phase);
        Assert.Equal(6, state.ModelCalls); Assert.Equal("original:6", Assert.Single(runtime.Calls).ClientRequestId);
        Assert.True(JsonNode.DeepEquals(schema, runtime.Calls[0].StructuredOutputSchema));
    }

    [Fact]
    public async Task HistoricalSchemaDoesNotReinterpretANullProposalAsAnAbstention()
    {
        var state = PlannerFixture.Session(); state.Catalog = new();
        state.Discovery.Sources = [new("source", "Declared operations")];
        var schema = PlanningSchemas.Proposal(state);
        state.ModelCalls = 6; state.PendingCall = new() { Id = "old", Purpose = "tasks", Request = new() { StructuredOutputSchema = schema } };
        var runtime = new TestRuntime { Respond = (request, _) => TestRuntime.Response(request, new() { Requirements = PlannerFixture.Requirements() }) };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal("PROPOSAL_ACTION_INVALID", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(6, state.ModelCalls); Assert.Equal(PlanningStatus.Generating, state.Status);
    }

    [Fact]
    public async Task SavedBudgetOptionAlsoBoundsDiscoveryAndDispatch()
    {
        var state = PlannerFixture.Session(); state.ModelCalls = 3;
        state.Request.Options["llm_budget"] = new JsonObject { ["max_calls"] = 3 };
        var runtime = new TestRuntime();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls);
        Assert.Equal("LLM_BUDGET_EXCEEDED", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(PlanningPhase.Requirements, state.Phase);
    }

    internal static PlanningProposal Discovery(int number) => new()
    { Requirements = PlannerFixture.Requirements(), DiscoveryRequests = [new("source", Query: "refinement " + number)] };

    private static JsonNode Context(string prompt) => JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;

    internal sealed class Pages : ICapabilityCatalog
    {
        internal int Reads;
        private readonly PlanningCapability capability = new()
        {
            Id = "declared", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "inspect",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "string" }
        };
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("source", "Declared operations"), new("unused", "Unrelated")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null)
        {
            Reads++;
            return Task.FromResult(new CapabilityPage(sourceId, cursor, [new(capability.Id, sourceId, "inspect", "Inspect data", capability.StepType, "read", capability.Version, Operation: TaskOperations.Describe(capability))], null, Query: query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(capability);
    }
}
