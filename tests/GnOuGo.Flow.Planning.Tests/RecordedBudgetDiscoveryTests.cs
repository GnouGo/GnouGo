using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RecordedBudgetDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void IncompleteRunKeepsAllSixIdentitiesWhileNewContextUsesTokenHeadroom()
    {
        var recording = Read("retained-discovery-incomplete.json");
        long beforeTotal = 0, afterTotal = 0; var index = 0;
        foreach (var entry in recording["responses"]!.AsArray())
        {
            var compact = entry!["pendingSession"]!;
            var json = compact.DeepClone().AsObject();
            json.Remove("recordedPages"); json.Remove("recordedResolved"); json.Remove("presentationQuery");
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            state.Catalog = recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
            state.Discovery = recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
            state.Discovery.Pages = state.Discovery.Pages.Take(compact["recordedPages"]!.GetValue<int>()).ToList();
            var ids = compact["recordedResolved"]!.AsArray().Select(c => c!.ToString()).ToHashSet(StringComparer.Ordinal);
            state.Discovery.Resolved = state.Discovery.Resolved.Where(c => ids.Contains(c.Id)).ToList();
            state.Discovery.PresentationQuery = compact["presentationQuery"]?.ToString();
            var issued = state.PendingCall!.Request;
            var before = PlanningJsonTransport.EstimateInputTokens(issued.Prompt, issued.StructuredOutputSchema!.AsObject());
            var original = Context(issued.Prompt)["operations"]!.AsArray().Count - PlanningDiscoveryContext.Required(state).Count;
            state.PendingCall = null; state.ModelCalls--; // Compare just before this retained dispatch, without altering its receipt.
            if (state.Usage is not null) state.Usage = state.Usage with { Calls = state.ModelCalls };
            var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
            var selected = HybridWorkflowPlanner.Shortlist(state);
            var prompt = HybridWorkflowPlanner.BuildPrompt(state, selected);
            var after = PlanningJsonTransport.EstimateInputTokens(prompt, PlanningSchemas.Proposal(state));
            Assert.InRange(after, 1, 21600);
            Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
            foreach (var candidate in selected)
            {
                var operation = Context(prompt)["operations"]!.AsArray().Single(o => o!["id"]!.ToString() == candidate.Operation!.Id)!;
                foreach (var port in candidate.Operation!.Inputs)
                    Assert.True(JsonNode.DeepEquals(port.Schema, operation["inputs"]!.AsArray().Single(p => p!["name"]!.ToString() == port.Name)!["type"]));
            }
            beforeTotal += before; afterTotal += after;
            output.WriteLine($"Incomplete run request {++index}: tokens {before} -> {after}; prompt bytes {Encoding.UTF8.GetByteCount(issued.Prompt)} -> {Encoding.UTF8.GetByteCount(prompt)}; optional contracts {original} -> {selected.Count}; pages retained={state.Discovery.Pages.Count}; historical identity={issued.ClientRequestId}.");
        }
        Assert.Equal(6, index);
        Assert.Null(recording["responses"]!.AsArray()[^1]!["response"]!["json"]!["plan"]);
        output.WriteLine($"Incomplete-run cumulative estimate: {beforeTotal} -> {afterTotal}. Six historical calls, no new inference or metadata reads. Fresh contract-selection size probe only; no replacement success is claimed.");
    }

    [Fact]
    public async Task TenRecordedMcpContractsReachReviewAfterSixScriptedDiscoveryCalls()
    {
        // Synthetic response schedule, explicitly distinct from both retained recordings.
        // The ten operation contracts and complete proposal are copied unchanged from
        // the successful compiler replay; no simplification of their types or bindings.
        var recording = Read("retained-review.json");
        var original = recording["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        var catalog = new RecordedContracts(original.Discovery);
        var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => TestRuntime.Response(request, new()
        {
            Requirements = original.Requirements,
            DiscoveryRequests = runtime.Calls.Count <= 6 ? runtime.Calls.Count == 1
                ? catalog.Sources.Select(s => new PlanningDiscoveryRequest(s.Id)).ToList()
                : [new(catalog.Sources[0].Id, Query: "refinement " + runtime.Calls.Count)] : null,
            Plan = runtime.Calls.Count == 7 ? original.Plan : null
        });
        var state = PlannerFixture.Session(); state.Request.Prompt = original.Request.Prompt;
        state.Request.Generation.MaxInputTokensPerRequest = 24000;
        state.Request.Generation.MaxOutputTokens = 32768;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(7, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(10, TaskPlanRevisions.Tasks(state.Plan!).Where(t => t.Kind == "operation").Select(t => t.Operation).Distinct().Count());
        Assert.Equal(JsonSerializer.Serialize(original.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(10, catalog.Resolutions.Count); Assert.Equal(10, catalog.Resolutions.Distinct().Count());
        Assert.Equal(catalog.Sources.Count + 5, catalog.Reads);
        Assert.Null(state.ApprovedHash); Assert.Empty(state.RevisionScope);
        PlanningArtifactApproval.Verify(state);
        var restored = PlannerFixture.Clone(state); PlanningArtifactApproval.Verify(restored);
        Assert.Equal(state.ComputeArtifactHash(), restored.ComputeArtifactHash());
        var counts = runtime.Calls.Select(c => Context(c.Prompt)["operations"]!.AsArray().Count).ToArray();
        var issuedState = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null)); issuedState.PendingCall = null; issuedState.ModelCalls--;
        // Removing duplicate summaries and closed navigation now fits all ten
        // exact contracts, without changing this retained proposal or its oracle.
        var allDetails = PlanningJsonTransport.EstimateInputTokens(HybridWorkflowPlanner.BuildPrompt(issuedState, PlanningDiscoveryContext.Candidates(issuedState)), PlanningSchemas.Proposal(issuedState));
        Assert.InRange(allDetails, 1, 21600);
        Assert.Equal(10 + state.Catalog!.Capabilities.Count(c => c.Kind == "registered"), counts[^1]);
        var presented = Context(runtime.Calls[^1].Prompt)["operations"]!.AsArray().Select(o => o!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(10, original.Discovery.Resolved.Count(c => presented.Contains(TaskOperations.Describe(c).Id)));
        Assert.All(original.Discovery.Resolved, c => Assert.Equal(JsonSerializer.Serialize(c, PlanningJsonContext.Default.PlanningCapability),
            JsonSerializer.Serialize(state.Catalog.Capabilities.Single(selected => selected.Id == c.Id), PlanningJsonContext.Default.PlanningCapability)));
        var tokens = runtime.Calls.Select(c => PlanningJsonTransport.EstimateInputTokens(c.Prompt, c.StructuredOutputSchema!.AsObject())).ToArray();
        Assert.All(tokens, n => Assert.InRange(n, 1, 21600));
        output.WriteLine($"Synthetic ten-contract review: calls={runtime.Calls.Count}; repairs={state.ReplanAttempts}; metadata reads={catalog.Reads}; contract resolutions={catalog.Resolutions.Count}; detailed operations=[{string.Join(',', counts)}]; input estimates=[{string.Join(',', tokens)}]; cumulative={tokens.Sum()}; prompt bytes=[{string.Join(',', runtime.Calls.Select(c => Encoding.UTF8.GetByteCount(c.Prompt)))}]. No approval, execution or live inference.");
    }

    private static JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", name)))!;
    private static JsonNode Context(string prompt) => JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;

    private sealed class RecordedContracts(CapabilityDiscoveryState snapshot) : ICapabilityCatalog
    {
        // A synthetic catalog containing exactly the ten fully recorded contracts.
        private readonly List<CapabilitySummary> summaries = snapshot.Pages.SelectMany(p => p.Capabilities)
            .Where(s => snapshot.Resolved.Any(c => c.Id == s.Id && c.Version == s.Version)).DistinctBy(c => (c.Id, c.Version)).ToList();
        internal List<CapabilitySource> Sources => snapshot.Sources.Where(s => summaries.Any(c => c.SourceId == s.Id)).ToList();
        internal int Reads;
        internal List<string> Resolutions = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(Sources);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            Reads++;
            return Task.FromResult(new CapabilityPage(sourceId, cursor, summaries.Where(c => c.SourceId == sourceId).ToList(), null, Query: query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        {
            Resolutions.Add(summary.Id);
            return Task.FromResult(snapshot.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        }
    }
}
