using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RecordedTargetedDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FullRetainedCatalogKeepsRequestedReviewContractsVisibleWithinSevenCalls()
    {
        var failure = Read("retained-discovery-displacement.json");
        var successful = Read("retained-review.json")["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        var metadata = failure["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        var contracts = metadata.Resolved.Concat(successful.Catalog!.Capabilities.Where(c => c.Kind != "registered"))
            .DistinctBy(c => (c.Id, c.Version)).ToList();
        var catalog = new RecordedCatalog(metadata, contracts);
        var expected = successful.Catalog.Capabilities.Where(c => c.Kind != "registered").Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var inspections = metadata.Pages.SelectMany(p => p.Capabilities).DistinctBy(c => c.Id).Where(c => expected.Contains(c.Id))
            .GroupBy(c => c.SourceId).Select(g => new PlanningDiscoveryRequest(g.Key, OperationIds: g.Select(c => c.Operation!.Id).ToList())).ToList();
        Assert.Equal(10, inspections.Sum(i => i.OperationIds!.Count)); Assert.InRange(inspections.Count, 1, 4);
        var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) =>
        {
            var call = runtime.Calls.Count;
            var proposal = call <= 5
                ? failure["responses"]!.AsArray()[call - 1]!["response"]!["json"]!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!
                : new PlanningProposal { DiscoveryRequests = call == 6 ? inspections : null, Plan = call == 7 ? successful.Plan : null };
            try { return TestRuntime.Response(request, proposal); }
            catch (Exception ex) { output.WriteLine($"Scripted response {call}: {ex}"); throw; }
        };
        var state = failure["initialSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        state.Catalog = failure["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
        state.Discovery.Sources = metadata.Sources;
        var accepted = failure["responses"]!.AsArray()[0]!["response"]!["json"]!["requirements"]!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)!;
        catalog.SanitizedBaseQuery = CapabilityRelevance.Query(state.Request.Prompt + " " + accepted.Summary + " " + string.Join(' ', accepted.Outcomes.Select(o => o.Description)));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(7, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Equal(74, state.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Id).Distinct().Count());
        Assert.Equal(JsonSerializer.Serialize(successful.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var final = TargetedDiscoveryTests.Context(runtime.Calls[^1].Prompt);
        var shown = final["operations"]!.AsArray().Select(o => o!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        Assert.All(inspections.SelectMany(i => i.OperationIds!), id => Assert.Contains(id, shown));
        Assert.Null(final["sources"]); Assert.Equal(19, catalog.Reads);
        foreach (var capability in successful.Catalog.Capabilities.Where(c => c.Kind != "registered"))
            Assert.Equal(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability),
                JsonSerializer.Serialize(state.Catalog!.Capabilities.Single(c => c.Id == capability.Id), PlanningJsonContext.Default.PlanningCapability));
        PlanningArtifactApproval.Verify(state); PlanningArtifactApproval.Verify(PlannerFixture.Clone(state));
        var estimates = runtime.Calls.Select(c => PlanningJsonTransport.EstimateInputTokens(c.Prompt, c.StructuredOutputSchema!.AsObject())).ToArray();
        Assert.All(estimates, n => Assert.InRange(n, 1, 24000));
        Assert.All(runtime.Calls.Take(6), c => Assert.InRange(PlanningJsonTransport.EstimateInputTokens(c.Prompt, c.StructuredOutputSchema!.AsObject()), 1, 21600));
        output.WriteLine($"Synthetic targeted continuation: calls={runtime.Calls.Count}, repairs={state.ReplanAttempts}, discovered=74, requested=10, metadata reads={catalog.Reads}, resolutions={catalog.Resolutions.Count} (unrecorded exact contracts unavailable={catalog.Unavailable}), input estimates=[{string.Join(',', estimates)}], cumulative={estimates.Sum()}, prompt bytes=[{string.Join(',', runtime.Calls.Select(c => Encoding.UTF8.GetByteCount(c.Prompt)))}], detailed counts=[{string.Join(',', runtime.Calls.Select(c => TargetedDiscoveryTests.Context(c.Prompt)["operations"]!.AsArray().Count))}]. Preserved all distractor metadata; exact resolution is limited to retained receipts. No live model or execution claim.");
    }

    [Fact]
    public void RetainedRequestsMeasureBeforeAndAfterWithoutChangingTheNullPlanEvidence()
    {
        var recording = Read("retained-discovery-displacement.json"); long beforeTotal = 0, afterTotal = 0; var i = 0;
        foreach (var entry in recording["responses"]!.AsArray())
        {
            var json = entry!["pendingSession"]!.DeepClone().AsObject();
            var pages = json["recordedPages"]!.GetValue<int>();
            var resolved = json["recordedResolved"]!.AsArray().Select(c => c!.ToString()).ToHashSet(StringComparer.Ordinal);
            var query = json["presentationQuery"]?.ToString();
            json.Remove("recordedPages"); json.Remove("recordedResolved"); json.Remove("presentationQuery");
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            state.Catalog = recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
            state.Discovery = recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
            state.Discovery.Pages = state.Discovery.Pages.Take(pages).ToList();
            state.Discovery.Resolved = state.Discovery.Resolved.Where(c => resolved.Contains(c.Id)).ToList();
            state.Discovery.PresentationQuery = query;
            var issued = state.PendingCall!.Request;
            var before = PlanningJsonTransport.EstimateInputTokens(issued.Prompt, issued.StructuredOutputSchema!.AsObject());
            state.PendingCall = null; state.ModelCalls--; if (state.Usage is not null) state.Usage = state.Usage with { Calls = state.ModelCalls };
            var shortlist = HybridWorkflowPlanner.Shortlist(state); var prompt = HybridWorkflowPlanner.BuildPrompt(state, shortlist);
            var after = PlanningJsonTransport.EstimateInputTokens(prompt, PlanningSchemas.Proposal(state));
            Assert.InRange(after, 1, 21600); beforeTotal += before; afterTotal += after;
            output.WriteLine($"Displacement request {++i}: input {before} -> {after}, bytes {Encoding.UTF8.GetByteCount(issued.Prompt)} -> {Encoding.UTF8.GetByteCount(prompt)}, detailed {TargetedDiscoveryTests.Context(issued.Prompt)["operations"]!.AsArray().Count} -> {TargetedDiscoveryTests.Context(prompt)["operations"]!.AsArray().Count}; pages={pages}.");
        }
        Assert.Equal(7, i); Assert.Null(recording["responses"]!.AsArray()[^1]!["response"]!["json"]!["plan"]);
        output.WriteLine($"Cumulative input estimate {beforeTotal} -> {afterTotal}. Presentation probe only; all seven historical outcomes and requests retained.");
    }

    private static JsonNode Read(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", file)))!;

    private sealed class RecordedCatalog(CapabilityDiscoveryState metadata, List<PlanningCapability> contracts) : ICapabilityCatalog
    {
        internal int Reads, Unavailable;
        internal string SanitizedBaseQuery = "";
        internal List<string> Resolutions = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("Sources are retained");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null)
        {
            Reads++;
            // Sanitizing request paths changes derived base-query tokens, not the
            // recorded page contents. Refined queries still match exactly.
            var recordedQuery = query == SanitizedBaseQuery ? metadata.Pages[0].Query : query;
            return Task.FromResult(metadata.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == recordedQuery) with { Query = query });
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        {
            Resolutions.Add(summary.Id);
            if (contracts.SingleOrDefault(c => c.Id == summary.Id && c.Version == summary.Version) is { } found) return Task.FromResult(found);
            Unavailable++; throw new IOException("No exact receipt in the retained evidence; no invented contract");
        }
    }
}
