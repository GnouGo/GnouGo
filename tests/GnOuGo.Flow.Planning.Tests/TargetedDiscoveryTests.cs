using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TargetedDiscoveryTests
{
    [Fact]
    public async Task InspectionReplacesOnlyItsSourceAndSurvivesRecoveryAndPages()
    {
        var state = GlobalDiscoveryTests.State();
        var runtime = new TestRuntime { Capabilities = new Catalog(state) };
        runtime.Proposal.Plan = null;
        runtime.Proposal.DiscoveryRequests = [new("b", OperationIds: ["b7"]), new("c", OperationIds: ["c6"])];
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(2, state.Discovery.Inspections!.Count);
        Assert.Contains(PlanningDiscoveryContext.Required(state), o => o.Id == "b7");
        runtime.Proposal.DiscoveryRequests = [new("b", Query: "other")];
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(2, state.Discovery.Inspections!.Count);
        runtime.Proposal.DiscoveryRequests = [new("b", OperationIds: ["b0"])];
        state = await Advance(state, runtime);
        Assert.Equal(new[] { "b0", "c6" }, PlanningDiscoveryContext.Inspected(state).Select(c => c.Id).Order());
        runtime.Proposal.DiscoveryRequests = [new("b", OperationIds: [])];
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("c6", Assert.Single(PlanningDiscoveryContext.Inspected(state)).Id);
        Assert.Equal(4, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(((Catalog)runtime.Capabilities).Pages);
        Assert.Empty(((Catalog)runtime.Capabilities).Resolutions); // All receipts were already resolved.
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong_source")]
    [InlineData("duplicate")]
    [InlineData("conflicting_version")]
    [InlineData("denied")]
    public async Task InvalidInspectionRejectsWholeBatchWithoutFetchingOrReplacingSelections(string failure)
    {
        var state = GlobalDiscoveryTests.State();
        state.Discovery.Inspections = [new("c", OperationIds: ["c1"])];
        if (failure == "conflicting_version") state.Discovery.Pages.Add(state.Discovery.Pages[1] with
        { Cursor = "changed", Capabilities = [state.Discovery.Pages[1].Capabilities[0] with { Version = "v2" }] });
        if (failure == "denied") state.Catalog!.Policy.DeniedCapabilityIds.Add("b0");
        var before = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var catalog = new Catalog(state); var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Proposal.Plan = null;
        runtime.Proposal.DiscoveryRequests = [new("a", Query: "new metadata"), new("b", OperationIds:
            failure == "unknown" ? ["unknown"] : failure == "wrong_source" ? ["a0"] : failure == "duplicate" ? ["b0", "b0"] : ["b0"])];
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "DISCOVERY_SELECTION_INVALID");
        Assert.Equal(before, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Empty(catalog.Pages); Assert.Empty(catalog.Resolutions);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task RequiredInspectionOverHardLimitStopsBeforeAnotherDispatchWithoutDroppingTheContract()
    {
        var state = GlobalDiscoveryTests.State();
        state.Discovery.Pages[1].Capabilities[0].Operation!.Inputs[0].Schema["description"] = new string('x', 72000);
        state.Discovery.Inspections = [new("b", OperationIds: ["b0"])];
        var runtime = new TestRuntime();
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("MODEL_INPUT_LIMIT", Assert.Single(state.Diagnostics).Code);
        Assert.Empty(runtime.Calls); Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("b0", Assert.Single(state.Discovery.Inspections!).OperationIds![0]);
        Assert.Contains(new string('x', 72000), HybridWorkflowPlanner.Prompt(state));
    }

    [Fact]
    public async Task UnavailableRequestedContractStopsRatherThanSilentlyFallingBack()
    {
        var state = GlobalDiscoveryTests.State(); state.Discovery.Resolved.RemoveAll(c => c.Id == "b0");
        state.Discovery.Inspections = [new("b", OperationIds: ["b0"])];
        var catalog = new Catalog(state) { Unavailable = true }; var runtime = new TestRuntime { Capabilities = catalog };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("DISCOVERY_CONTRACT_UNAVAILABLE", Assert.Single(state.Diagnostics).Code);
        Assert.Empty(runtime.Calls); Assert.Single(catalog.Resolutions);
        Assert.Single(state.Discovery.Limitations);
    }

    [Fact]
    public void ClosedAndFixedOperationRequestsDoNotPermitInspection()
    {
        var state = GlobalDiscoveryTests.State(); state.ModelCalls = 6;
        Assert.True(PlanningSchemas.AllowsNoPlan(PlanningSchemas.Proposal(state)));
        state.ModelCalls = 0;
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Operation = "a0", Objective = "Use" }] } };
        state.Diagnostics = [new("TASK_INPUT_TYPE", "/tasks/use/inputs/value", "Fix binding")];
        state.RevisionScope = ["/tasks/use/inputs/value"];
        state.Discovery.Inspections = [new("b", OperationIds: ["b0"])];
        Assert.True(PlanningSchemas.AllowsNoPlan(PlanningSchemas.Proposal(state)));
        Assert.DoesNotContain("schema-only-b0", HybridWorkflowPlanner.Prompt(state));
    }

    [Fact]
    public async Task RecoveredInvalidSelectionStopsBeforeInferenceAndRetainsUsage()
    {
        var state = GlobalDiscoveryTests.State(); state.ModelCalls = 2;
        state.Usage = new() { Calls = 2, TotalTokens = 123, EstimatedCost = 0.1m };
        state.Discovery.Inspections = [new("b", OperationIds: ["a0"])];
        var runtime = new TestRuntime();
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("DISCOVERY_SELECTION_INVALID", Assert.Single(state.Diagnostics).Code);
        Assert.Empty(runtime.Calls); Assert.Equal(2, state.ModelCalls); Assert.Equal(123, state.Usage!.TotalTokens); Assert.Equal(0.1m, state.Usage.EstimatedCost);
    }

    [Fact]
    public async Task PendingInspectionResponseReplaysItsOriginalSchemaWithoutAnotherCallReservation()
    {
        var state = GlobalDiscoveryTests.State();
        var runtime = new TestRuntime { Respond = (_, _) => throw new IOException("Retain request") };
        state = await Advance(state, runtime);
        var pending = state.PendingCall!; var schema = pending.Request.StructuredOutputSchema!.DeepClone();
        state.Status = PlanningStatus.Generating; state.Diagnostics.Clear();
        runtime.Respond = null; runtime.Proposal.Plan = null; runtime.Proposal.DiscoveryRequests = [new("b", OperationIds: ["b7"])];
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics); Assert.Null(state.PendingCall);
        Assert.Equal(pending.Id, runtime.Calls[^1].ClientRequestId);
        Assert.True(JsonNode.DeepEquals(schema, runtime.Calls[^1].StructuredOutputSchema));
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("b7", Assert.Single(state.Discovery.Inspections!).OperationIds![0]);
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    private sealed class Catalog(PlanningSession initial) : ICapabilityCatalog
    {
        internal List<string> Pages = [], Resolutions = [];
        internal bool Unavailable;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null)
        { Pages.Add(sourceId); return Task.FromResult(initial.Discovery.Pages.First(p => p.SourceId == sourceId) with { Cursor = cursor, Query = query }); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        {
            Resolutions.Add(summary.Id);
            return Unavailable ? throw new IOException("Private failure") : Task.FromResult(initial.Discovery.Resolved.Single(c => c.Id == summary.Id));
        }
    }

    [Fact]
    public void EarlierUnpresentedOperationRemainsVisibleAfterPagination()
    {
        var state = GlobalDiscoveryTests.State();
        state.Discovery.Pages.Add(new("b", "next-b", [new("later", "b", "later", "Later page", "mcp.call", "read", "v1",
            Operation: new() { Id = "later", Description = "A later operation" })], null, Query: "find needle records"));
        var context = Context(HybridWorkflowPlanner.BuildPrompt(state, []));
        var index = context["coverage"]!.AsArray().Single(c => c!["sourceId"]!.ToString() == "b")!["index"]!.AsArray();
        Assert.Contains(index, c => c!["id"]!.ToString() == "b0");
        Assert.Contains(index, c => c!["id"]!.ToString() == "later");
    }

    [Fact]
    public void ClosedDiscoveryOmitsUnusableNavigationAndDuplicateDescriptions()
    {
        var state = GlobalDiscoveryTests.State(); state.ModelCalls = 6;
        var optional = HybridWorkflowPlanner.Shortlist(state);
        var context = Context(HybridWorkflowPlanner.BuildPrompt(state, optional));
        Assert.All(optional, c =>
        {
            var shown = context["operations"]!.AsArray().Single(o => o!["id"]!.ToString() == c.Operation!.Id)!;
            Assert.Equal(c.SourceId, shown["sourceId"]!.ToString()); Assert.Equal(c.Name, shown["name"]!.ToString());
        });
        Assert.Null(context["sources"]);
        Assert.All(context["coverage"]!.AsArray(), c =>
        {
            Assert.False(c!.AsObject().ContainsKey("query"));
            Assert.False(c.AsObject().ContainsKey("continuations"));
        });
        Assert.All(context["coverage"]!.AsArray().SelectMany(c => c!["index"]!.AsArray()), c =>
            Assert.DoesNotContain(optional, o => o.Operation!.Id == c!["id"]!.ToString()));
    }

    internal static JsonNode Context(string prompt) => JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
}
