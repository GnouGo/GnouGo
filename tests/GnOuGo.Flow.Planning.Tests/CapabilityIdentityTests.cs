using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EquivalentJsonPreservesVersionsCursorsAndResolutionAcrossRuntimes()
    {
        var source = new FocusedDiscoveryTests.Source("unrelated-catalog", 48);
        source.Tools[0].InputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"record\":{\"type\":\"string\"}},\"required\":[\"record\"]}");
        var first = new CapabilityDiscovery(new() { McpClientFactory = source });
        var snapshot = await first.CaptureAsync(source.Id, Ct);
        var page = first.ReadPage(snapshot, null, "requested record");
        source.Tools[0].InputSchema = JsonNode.Parse("{ \"required\": [\"record\"], \"properties\": {\"record\": {\"type\": \"string\"}}, \"type\": \"object\" }");
        var recreated = new CapabilityDiscovery(new() { McpClientFactory = source });
        var current = await recreated.CaptureAsync(source.Id, Ct);
        Assert.Equal(snapshot.Capabilities.Select(c => c.Version), current.Capabilities.Select(c => c.Version));
        Assert.Equal(page.NextCursor, recreated.ReadPage(current, null, "requested record").NextCursor);
        foreach (var summary in first.ReadPage(snapshot, null, null).Capabilities)
            Assert.Equal(summary.Version, (await recreated.ResolveAsync(summary, Ct)).Version);
        Assert.NotEqual(PlanningDiscoverySnapshots.Version(snapshot), PlanningDiscoverySnapshots.Version(current));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("constraint")]
    [InlineData("description")]
    [InlineData("effect")]
    [InlineData("array_order")]
    public async Task MeaningfulChangesCannotResolveAnOldSelection(string change)
    {
        var source = new FocusedDiscoveryTests.Source("renamed", 1);
        source.Tools[0].InputSchema!["examples"] = new JsonArray("first", "second");
        var original = new CapabilityDiscovery(new() { McpClientFactory = source });
        var summary = Assert.Single((await original.ListAsync(source.Id, null, Ct)).Capabilities);
        switch (change)
        {
            case "type": source.Tools[0].OutputSchema!["type"] = "integer"; break;
            case "constraint": source.Tools[0].InputSchema!["maxProperties"] = 1; break;
            case "description": source.Tools[0].Description = "Changed authoritative meaning"; break;
            case "effect": source.Tools[0].EffectKind = "write"; break;
            default: source.Tools[0].InputSchema!["examples"] = new JsonArray("second", "first"); break;
        }
        var recreated = new CapabilityDiscovery(new() { McpClientFactory = source });
        await Assert.ThrowsAsync<PlanningConflictException>(() => recreated.ResolveAsync(summary, Ct));
    }

    [Fact]
    public async Task ExactLegacyVersionsResolveWithoutRewritingHistoricalEvidence()
    {
        var source = new FocusedDiscoveryTests.Source("legacy", 1);
        var catalog = new CapabilityDiscovery(new() { McpClientFactory = source });
        var snapshot = await catalog.CaptureAsync(source.Id, Ct);
        var capability = snapshot.Capabilities[0]; capability.Version = "";
        capability.Version = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
        var original = JsonSerializer.Serialize(snapshot, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot);
        var summary = Assert.Single(catalog.ReadPage(snapshot, null, null).Capabilities);
        var recreated = new CapabilityDiscovery(new() { McpClientFactory = source });
        Assert.Equal(summary.Version, (await recreated.ResolveAsync(summary, Ct)).Version);
        Assert.Equal(original, JsonSerializer.Serialize(snapshot, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot));
    }

    [Fact]
    public async Task RemovedSourceIsCatalogChangeRatherThanTransportUnavailability()
    {
        var source = new FocusedDiscoveryTests.Source("removed-source", 1);
        var original = new CapabilityDiscovery(new() { McpClientFactory = source });
        var selected = Assert.Single((await original.ListAsync(source.Id, null, Ct)).Capabilities);
        var current = new CapabilityDiscovery(new() { McpClientFactory = new FocusedDiscoveryTests.Source("different-source", 1) });
        var error = await Assert.ThrowsAsync<PlanningConflictException>(() => current.ResolveAsync(selected, Ct));
        Assert.Equal("catalog_changed", error.Category);
    }

    [Theory]
    [InlineData("denied", "access_denied")]
    [InlineData("forbidden", "access_denied")]
    [InlineData("timeout", "timeout")]
    [InlineData("transport", "transport_unavailable")]
    public async Task CaptureRetainsSafeFailureCategory(string failure, string expected)
    {
        var catalog = new CapabilityDiscovery(new() { McpClientFactory = new FailedSource(failure) });
        var source = Assert.Single(await catalog.ListSourcesAsync(Ct));
        var error = await Assert.ThrowsAsync<CapabilityDiscoveryException>(() => catalog.CaptureAsync(source.Id, Ct));
        Assert.Equal(expected, error.Category); Assert.DoesNotContain("PRIVATE", error.Message);
        var page = await catalog.ListAsync(source.Id, null, Ct);
        Assert.Equal(expected, page.FailureCategory); Assert.DoesNotContain("PRIVATE", page.UnavailableReason);
    }

    private sealed class FailedSource(string failure) : IMcpClientFactory
    {
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "arbitrary" }];
        public Task<IMcpSession> GetClientAsync(string name, CancellationToken ct) => throw (failure switch
        {
            "denied" => new UnauthorizedAccessException("PRIVATE"),
            "forbidden" => new HttpRequestException("PRIVATE", null, HttpStatusCode.Forbidden),
            "timeout" => new TimeoutException("PRIVATE"),
            _ => new IOException("PRIVATE")
        });
    }
}
