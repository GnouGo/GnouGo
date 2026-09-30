using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDiscoveryContextTests
{
    internal static PlanningSession State()
    {
        var state = PlannerFixture.Session(); state.Request.Prompt = "Find the needle";
        state.Requirements = PlannerFixture.Requirements();
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        var capabilities = Enumerable.Range(0, 100).Select(i => new PlanningCapability
        {
            Id = $"c{i:D3}", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = i == 99 ? "find_needle" : $"utility_{i}",
            Description = i == 99 ? "Find the needle" : "An unrelated utility",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string", ["description"] = $"contract-only-{i:D3}-" + new string('x', 1500) } }, ["required"] = new JsonArray("value") },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject() }
        }).ToList();
        state.Discovery.Sources = [new("source", "Available utilities")];
        state.Discovery.Pages = [new("source", null, capabilities.Select(c => new CapabilitySummary(c.Id, "source", c.Method!, c.Description, c.StepType, c.EffectKind, c.Version, Operation: TaskOperations.Describe(c))).ToList(), null)];
        state.Discovery.Resolved = capabilities;
        return state;
    }

    [Fact]
    public void CachedUnselectedSchemasStayOutOfThePromptWithoutChangingReceipts()
    {
        var state = State(); var before = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var prompt = HybridWorkflowPlanner.Prompt(state);
        Assert.Contains("contract-only-099-", prompt);
        Assert.DoesNotContain("contract-only-098-", prompt);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(prompt, PlanningSchemas.Proposal(state)) < 24000);
        Assert.Equal(before, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
    }

    [Fact]
    public void MandatoryContractsAreNotRemovedToFitTheBudget()
    {
        var state = State(); state.Request.Generation.MaxInputTokensPerRequest = 1024;
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "required", Kind = "operation", Operation = "c098", Objective = "Use the chosen operation" }] } };
        var prompt = HybridWorkflowPlanner.Prompt(state);
        Assert.Contains("contract-only-098-", prompt);
        Assert.DoesNotContain("contract-only-099-", prompt);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(prompt, PlanningSchemas.Proposal(state)) > 1024);
    }
    [Fact]
    public async Task FreshShortlistResolvesOnlyAdmittedContractsAndRecoveryKeepsTheIssuedRequest()
    {
        var state = State(); var catalog = new ResolvingCatalog(state.Discovery.Resolved); state.Discovery.Resolved.Clear();
        var runtime = new TestRuntime { Capabilities = catalog, Respond = (_, _) => throw new IOException("Interrupted request") };
        var expected = HybridWorkflowPlanner.Shortlist(state).Select(c => c.Id).ToArray();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(expected, catalog.Resolutions);
        Assert.Empty(state.Catalog!.Capabilities); Assert.Equal(expected.Length, state.Discovery.Resolved.Count);
        var pending = JsonSerializer.Serialize(state.PendingCall, PlanningJsonContext.Default.PlanningModelCall);
        var receipts = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        state.Status = PlanningStatus.Generating; state.Request.Generation.MaxInputTokensPerRequest = 1024;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(expected, catalog.Resolutions); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(pending, JsonSerializer.Serialize(state.PendingCall, PlanningJsonContext.Default.PlanningModelCall));
        Assert.Equal(receipts, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
    }

    [Fact]
    public async Task RefinedQueriesAreRetainedAndNormalizedDuplicatesNeverReadPages()
    {
        var state = State(); var catalog = new ResolvingCatalog(state.Discovery.Resolved); var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Proposal.Plan = null; runtime.Proposal.DiscoveryRequests = [new("source", Query: "OtherRésumé")];
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal("other resume", state.Discovery.Pages[^1].Query); Assert.Single(catalog.Reads);
        var receipt = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        runtime.Proposal.DiscoveryRequests = [new("source", Query: "OTHER resume")];
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "DISCOVERY_NO_PROGRESS"); Assert.Single(catalog.Reads);
        Assert.Equal(receipt, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        runtime.Proposal.DiscoveryRequests = [new("source", Query: "new_query"), new("source", Query: "NEW Query")];
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "DISCOVERY_NO_PROGRESS"); Assert.Single(catalog.Reads);
    }

    private sealed class ResolvingCatalog(IEnumerable<PlanningCapability> entries) : ICapabilityCatalog
    {
        private readonly List<PlanningCapability> capabilities = entries.ToList();
        internal readonly List<string> Resolutions = []; internal readonly List<string?> Reads = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            Reads.Add(query);
            return Task.FromResult(new CapabilityPage(sourceId, cursor, capabilities.Take(8).Select(c => new CapabilitySummary(c.Id, sourceId,
                c.Method!, c.Description, c.StepType, c.EffectKind, c.Version, Operation: TaskOperations.Describe(c))).ToList(), "next", Query: query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        { Resolutions.Add(summary.Id); return Task.FromResult(capabilities.Single(c => c.Id == summary.Id)); }
    }

}
