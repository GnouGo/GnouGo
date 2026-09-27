using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RankedDiscoveryTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("field")]
    public async Task FullSourceIsRankedBeforePagingAndMetadataIsReadOnce(string match)
    {
        var factory = new Source();
        var target = factory.Tools[^1];
        if (match == "name") target.Name = "ReadRésuméEntries";
        if (match == "description") target.Description = "Read résumé entries";
        if (match == "field") target.InputSchema!["properties"]!["résuméEntries"] = new JsonObject { ["type"] = "string" };
        var catalog = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var source = Assert.Single(await catalog.ListSourcesAsync(PlannerFixture.Ct));
        var first = await catalog.ListAsync(source.Id, null, PlannerFixture.Ct, "RESUME entries");
        Assert.Equal(8, first.Capabilities.Count); Assert.Equal(target.Name, first.Capabilities[0].Name);
        var reversed = new Source { Tools = factory.Tools.AsEnumerable().Reverse().ToList() };
        var reordered = new CapabilityDiscovery(new() { McpClientFactory = reversed });
        Assert.Equal(first.Capabilities.Select(c => c.Id), (await reordered.ListAsync(source.Id, null, PlannerFixture.Ct, "RESUME entries")).Capabilities.Select(c => c.Id));
        var seen = first.Capabilities.Select(c => c.Id).ToList(); var page = first;
        while (page.NextCursor is { } cursor)
        {
            page = await catalog.ListAsync(source.Id, cursor, PlannerFixture.Ct, "RESUME entries");
            seen.AddRange(page.Capabilities.Select(c => c.Id));
        }
        Assert.Equal(1000, seen.Count); Assert.Equal(1000, seen.Distinct().Count()); Assert.Equal(1, factory.Reads);
        var again = await catalog.ListAsync(source.Id, null, PlannerFixture.Ct, "RESUME entries");
        Assert.Equal(first.NextCursor, again.NextCursor);
        await catalog.ResolveAsync(first.Capabilities[0], PlannerFixture.Ct); Assert.Equal(1, factory.Reads);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListAsync(source.Id, first.NextCursor, PlannerFixture.Ct, "different query"));
        factory.Tools[0].Description = "A changed authoritative description";
        var refreshed = new CapabilityDiscovery(new() { McpClientFactory = factory });
        await Assert.ThrowsAsync<ArgumentException>(() => refreshed.ListAsync(source.Id, first.NextCursor, PlannerFixture.Ct, "RESUME entries"));
    }

    [Fact]
    public async Task RefinementFindsOtherOperationsAndZeroScoresHaveStableTies()
    {
        var factory = new Source(); factory.Tools[^1].Description = "unique observation";
        var catalog = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var source = Assert.Single(await catalog.ListSourcesAsync(PlannerFixture.Ct));
        var zero = await catalog.ListAsync(source.Id, null, PlannerFixture.Ct, "unmatched");
        Assert.Equal(zero.Capabilities.Select(c => c.Id).Order(StringComparer.Ordinal), zero.Capabilities.Select(c => c.Id));
        var refined = await catalog.ListAsync(source.Id, null, PlannerFixture.Ct, "unique observation");
        Assert.Equal(factory.Tools[^1].Name, refined.Capabilities[0].Name); Assert.Equal(1, factory.Reads);
        // Relevance is presentation only: misleading text cannot change the exact type.
        factory.Tools[^1].Description += " Produces an integer";
        var actual = await catalog.ResolveAsync(refined.Capabilities[0], PlannerFixture.Ct);
        Assert.Equal("string", actual.OutputSchema["type"]!.GetValue<string>());
    }

    private sealed class Source : IMcpClientFactory
    {
        internal int Reads;
        internal List<McpToolInfo> Tools = Enumerable.Range(0, 1000).Select(i => new McpToolInfo
        {
            Name = "operation_" + i, Description = "Unrelated utility", EffectKind = "read",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new JsonObject { ["type"] = "string" }
        }).ToList();
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "arbitrary", Description = "Many utilities" }];
        public Task<IMcpSession> GetClientAsync(string name, CancellationToken ct)
        {
            Reads++; var inner = new InMemoryMcpClientFactory(); inner.RegisterServer(name, new() { Tools = Tools }); return inner.GetClientAsync(name, ct);
        }
    }
}
