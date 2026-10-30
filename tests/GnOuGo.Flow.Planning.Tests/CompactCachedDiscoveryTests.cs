using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task AcceptedStructuralStepRetainsCachedOperationMeaningForTheNextImplementation()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 3);
        state.Discovery.Sources.Add(new("beta", "Another authorized provider."));
        state.Discovery.Pages.Add(new("beta", null, [], null, Query: Objective));
        var originals = PlanningCompactConstruction.ContractDescriptors(state, count: 3);
        var addresses = originals.Select(row => row!["descriptionEvidence"]!.ToString()).ToArray();
        foreach (var address in addresses) PlanningConstruction.LoadEvidence(state, [address]);
        Assert.Equal(3, state.Construction!.Evidence!.Count);
        Assert.Empty(state.Discovery.Resolved);
        // These exact contracts were discovered for an earlier responsibility;
        // changing technical scope must not pretend its provider query has run for this block.
        state.Discovery.Pages = state.Discovery.Pages.Select(page => page with
            { Query = "Inspect provider operations for an earlier independent responsibility." }).ToList();
        var oldPages = JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage);
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "sequence"; step.DiscoveryComplete = true;
        step.Prerequisites = [new() { Need = "The original record still requires selecting and binding its exact provider operation.",
            EvidenceAddresses = ["requirement:req_0"] }];
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            return Wire(request, new JsonObject { ["baseHash"] = step.BaseHash,
                ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["req_0"],
                    Tasks = [new() { Id = "work", Kind = "sequence", Objective = "Own the requested local result and cleanup.", Body = new() }] },
                    PlanningJsonContext.Default.PlanningConstructionExtension) });
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        runtime.Respond = (_, _) => throw new InvalidOperationException("An empty opening needs no model review or selection during acceptance.");
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Single(state.Construction.Checkpoints);
        Assert.Null(state.Construction.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        var block = PlanningCompactConstruction.CurrentBlock(state)!;
        var business = JsonSerializer.Serialize(block, PlanningJsonContext.Default.PlanningConstructionBlock);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.ImplementationPurpose, purpose);
            var response = Implement(request);
            response["step"]!["scope"] = "/tasks/work/body";
            response["step"]!["kind"] = null;
            return Wire(request, response);
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(block.Objective, state.Construction!.Compact!.CurrentStep!.Objective);
        Assert.Equal(block.Id, state.Construction.Compact.CurrentStep.BlockId);
        Assert.Equal(business, JsonSerializer.Serialize(PlanningCompactConstruction.CurrentBlock(state), PlanningJsonContext.Default.PlanningConstructionBlock));
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var cached = context["describedOperations"]!["items"]!.AsArray();
        Assert.Equal(3, cached.Count);
        Assert.Equal(originals.Select(row => row!["id"]!.ToString()).Order(StringComparer.Ordinal),
            cached.Select(row => row!["id"]!.ToString()).Order(StringComparer.Ordinal));
        foreach (var row in cached)
        {
            Assert.Contains(row!["descriptionEvidence"]!.ToString(), addresses);
            Assert.Equal("Read exact original records with declared restrictions.", row["description"]!.ToString());
            Assert.True(row["descriptionComplete"]!.GetValue<bool>());
            Assert.StartsWith("version_", row["version"]!.ToString(), StringComparison.Ordinal);
        }
        Assert.All(context["providerRequests"]!["items"]!.AsArray(), provider =>
        { Assert.Equal(0, provider!["resultCount"]!.GetValue<int>()); Assert.True(provider["canRequestPage"]!.GetValue<bool>()); });
        Assert.Null(context["descriptors"]);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(oldPages, JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
        Assert.Empty(state.Discovery.Resolved);
        var catalog = new CachedMetadataCatalog(state.Discovery.Pages.SelectMany(page => page.Capabilities).ToArray());
        runtime.Capabilities = catalog;
        var chosen = cached[0]!["id"]!.ToString();
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            Assert.Contains(runtime.EvidenceId(request, addresses[0]), request.Prompt, StringComparison.Ordinal);
            return Wire(request, new JsonObject { ["binding"] = new JsonObject { ["kind"] = "operation",
                ["operationIds"] = new JsonArray(chosen), ["groupId"] = null, ["closeScopes"] = null } });
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, catalog.ListCalls);
        Assert.Equal(1, catalog.Resolutions);
        Assert.Equal(new[] { chosen }, state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.True(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Single(state.Discovery.Resolved);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(oldPages, JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
    }

    [Fact]
    public async Task CachedOperationNavigationPagesRemainExactBoundedAndDoNotCountAsNewKnowledge()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 11);
        var originalPages = JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage);
        state.Construction!.Compact!.CurrentStep!.Objective = "A later business action needs an existing operation.";
        var all = PlanningCompactConstruction.ContractDescriptors(state, count: 11, retained: true);
        var offered = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        while (true)
        {
            var context = PlanningCompactConstruction.DiscoveryContext(state);
            var cached = context["describedOperations"]!;
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(cached.ToJsonString()), 1, 2048);
            foreach (var row in cached["items"]!.AsArray()) offered.Add(row!["id"]!.ToString());
            foreach (var page in (context["evidence"] as JsonObject ?? []).Where(pair => pair.Key.StartsWith("page:operations:", StringComparison.Ordinal)))
                foreach (var row in page.Value!["items"]!.AsArray()) offered.Add(row!["id"]!.ToString());
            if (cached["next"] is not { } next) break;
            Assert.True(pages++ < 4);
            var address = next.ToString();
            runtime.Respond = (request, _) => Wire(request, Evidence(address));
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [address]), state.Construction!.EvidenceRequests);
        }
        Assert.Equal(all.Select(row => row!["id"]!.ToString()).Order(StringComparer.Ordinal), offered.Order(StringComparer.Ordinal));
        Assert.Single(state.Discovery.Pages);
        Assert.Equal(originalPages, JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
        Assert.Empty(state.Discovery.Resolved);
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("step-type")]
    [InlineData("version-conflict")]
    [InlineData("description-conflict")]
    [InlineData("summary-version")]
    public async Task EarlierCachedDescriptionsNeverWidenPolicyOrHideConflictingOriginals(string change)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        var page = state.Discovery.Pages.Single();
        var capability = page.Capabilities.Single();
        state.Construction!.Compact!.CurrentStep!.Objective = "A different immediate business action.";
        switch (change)
        {
            case "denied": state.Catalog!.Policy.DeniedCapabilityIds.Add(capability.Id); break;
            case "step-type": state.Catalog!.AllowedStepTypes.Clear(); break;
            case "summary-version": state.Discovery.Pages[0] = page with { Capabilities = [capability with { Version = "conflicting" }] }; break;
            case "version-conflict":
            case "description-conflict":
                var copy = PlannerFixture.Clone(state).Discovery.Pages[0].Capabilities[0];
                copy.Operation!.Description = "A conflicting original description with different behavior.";
                if (change == "version-conflict") { copy.Operation.Version = "conflicting"; copy = copy with { Version = "conflicting" }; }
                state.Discovery.Pages.Add(page with { Query = "A third original provider query.", Capabilities = [copy] });
                break;
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["describedOperations"]);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private sealed class CachedMetadataCatalog(CapabilitySummary[] summaries) : ICapabilityCatalog
    {
        internal int ListCalls;
        internal int Resolutions;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { ListCalls++; throw new InvalidOperationException("Cached metadata cannot substitute a provider response for a new query."); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        {
            Assert.Contains(summaries, original => original.Id == summary.Id && original.Version == summary.Version);
            Resolutions++;
            return Task.FromResult(new PlanningCapability { Id = summary.Id, Version = summary.Version, Kind = "tool", StepType = summary.StepType,
                Server = summary.SourceId, Method = summary.Name, Description = summary.Description,
                InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } });
        }
    }
}
