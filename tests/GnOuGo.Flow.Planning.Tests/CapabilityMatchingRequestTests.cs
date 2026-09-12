using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityMatchingRequestTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(160)]
    public void CompactContractMembersAreSharedWithoutChangingTheirTextOrCandidateScope(int length)
    {
        // Compact JSON has no whitespace at which the word-fragment packer can
        // find repeated constraints. The distinct contracts must all remain eligible.
        var bound = new string('a', length);
        var entries = Enumerable.Range(0, 12).Select(i => new CapabilityCatalogEntry("entry" + i, "mcp", "provider", "tool", "method" + i,
            "Declared metadata", [], $$"""{"method":"method{{i}}","pattern":"^{{bound}}$","description":"literal,comma 🧪 must remain intact","const":"value{{i}}"}""",
            [], [], null, null)).ToArray();
        var original = string.Join('\n', entries.Select(e => e.Id + " " + e.Card));
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, original));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(compact, new()) < PlanningJsonTransport.EstimateInputTokens(original, new()),
            "Repeated compact contract members should not make a bounded matching request oversized.");
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        var recovered = expanded["groups"]!.AsArray().OfType<JsonObject>().SelectMany(group => group["entries"]!.AsObject().Select(e =>
            new KeyValuePair<string, string>(e.Key, group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), e.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"])))
            .ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(entries.Length, recovered.Count);
        Assert.All(entries, entry => Assert.Equal(entry.Card, recovered[entry.Id]));
    }

    [Fact]
    public void RepeatedCatalogEncodingMetadataIsSentOnce()
    {
        var description = string.Concat(Enumerable.Repeat("A declared source supplies this exact immutable argument and result contract. ", 20));
        var entries = Enumerable.Range(0, 8).Select(i => new CapabilityCatalogEntry("entry" + i, "mcp", "provider", "tool", "method" + i,
            description, [], description + i, [], [], null, null)).ToArray();
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, string.Join('\n', entries.Select(e => e.Id + " " + e.Card))));
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        var groups = expanded["groups"]!.AsArray();
        Assert.Single(groups); // The common prefix is shared across distinct methods too.
        Assert.Equal(8, groups.OfType<JsonObject>().Sum(group => group["entries"]!.AsObject().Count));
        Assert.All(groups.OfType<JsonObject>(), group => Assert.False(group.ContainsKey("separator")));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(compact, new()) <
            PlanningJsonTransport.EstimateInputTokens(string.Join('\n', entries.Select(e => e.Id + " " + e.Card)), new()));
        foreach (var group in groups.OfType<JsonObject>())
            foreach (var entry in group["entries"]!.AsObject())
                Assert.Equal(entries.Single(e => e.Id == entry.Key).Card,
                    group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), entry.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"]);
    }

    [Fact]
    public void CatalogFactoringPreservesUnicodeAndLiteralSeparators()
    {
        var shared = string.Concat(Enumerable.Repeat("Declared contract: 🧪 Unicode é accents; original \"quotes\", commas, newlines\n", 20));
        var entries = new[] { "🧪", "🧬", "literal" }.Select((suffix, index) => new CapabilityCatalogEntry("c" + index, "mcp", "provider", "tool", "method",
            "Declared metadata", [], shared + suffix + " -> tail 🧪", [], [], null, null)).ToArray();
        var original = string.Join('\n', entries.Select(e => e.Id + " " + e.Card));
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, original));
        Assert.True(compact.Length < original.Length);
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        foreach (var group in expanded["groups"]!.AsArray().OfType<JsonObject>())
            foreach (var entry in group["entries"]!.AsObject())
                Assert.Equal(entries.Single(e => e.Id == entry.Key).Card,
                    group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), entry.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"]);
    }
    [Fact]
    public void SavedCodeReviewSelectorCatalogIsLosslesslySharedWithFewerTokens()
    {
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodeReviewMatchingCatalog.json")))!.AsArray();
        var entries = data.Select(n => new CapabilityCatalogEntry(n!["id"]!.ToString(), n["resolution"]!.ToString(), n["server"]?.ToString(), n["kind"]?.ToString(), n["method"]!.ToString(),
            "Frozen public metadata", [], n["card"]!.ToString(), [], [], null, null)).ToArray();
        var original = string.Join('\n', entries.Select(e => e.Card));
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, original));
        Assert.True(compact.Length < original.Length);
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        var recovered = expanded["groups"]!.AsArray().OfType<JsonObject>().SelectMany(group => group["entries"]!.AsObject().Select(e =>
            new KeyValuePair<string, string>(e.Key, group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), e.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"]))).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(entries.Length, recovered.Count);
        Assert.All(entries, entry => Assert.Equal(entry.Card, recovered[entry.Id]));
        var schema = PlanningHoleRequests.Object(("result", PlanningHoleRequests.Type("string")));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(compact, schema) < PlanningJsonTransport.EstimateInputTokens(original, schema));
    }
}
