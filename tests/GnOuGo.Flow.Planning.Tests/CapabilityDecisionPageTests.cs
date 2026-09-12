using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityDecisionPageTests
{
    [Fact]
    public async Task FrozenCatalogIsCompletelyCoveredWithHeadroomAndWithoutModelQuotations()
    {
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodeReviewMatchingCatalog.json")))!.AsArray();
        var entries = data.Select(n => new CapabilityCatalogEntry(n!["id"]!.ToString(), n["resolution"]!.ToString(), n["server"]?.ToString(), n["kind"]?.ToString(), n["method"]!.ToString(),
            "Frozen public metadata", [], n["card"]!.ToString(), [], [], null, null)).ToArray();
        var catalog = new CapabilityCatalog(entries, ""); var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = new JsonObject(
            request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["relation"] = "unrelated" }))) }) };
        var relations = await CapabilityDecisionPages.AssessAsync(state, runtime, [("operation", "requirement", "Use the original owned resource.")], catalog, TestContext.Current.CancellationToken);
        Assert.Equal(entries.Select(e => e.Id).Order(), relations.Select(r => r.Candidate).Order());
        Assert.All(relations, r => Assert.False(r.Supported));
        Assert.Null(state.Outcome); // Negative model assertions are not business unsupportedness.
        Assert.All(runtime.Requests, r =>
        {
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()), 1, 9600);
            Assert.InRange(PlanningDecisionPages.AnswerTokens(r.StructuredOutputSchema.AsObject()), 1, 2048);
            Assert.DoesNotContain("excerpt", r.StructuredOutputSchema.ToJsonString());
        });
        foreach (var entry in entries)
        {
            var card = CapabilityCoverageContext.BuildCapabilityCoverageCard(entry, catalog);
            var references = state.References.Where(r => r.SourceId == "catalog:" + entry.Id).OrderBy(r => r.Start).ToArray();
            Assert.Equal(card, string.Concat(references.Select(r => card.Substring(r.Start, r.Length))));
        }
        var count = runtime.Requests.Count;
        await CapabilityDecisionPages.AssessAsync(PlanningContext.Clone(state), runtime, [("operation", "requirement", "Use the original owned resource.")], catalog, TestContext.Current.CancellationToken);
        Assert.Equal(count, runtime.Requests.Count);
    }

    [Fact]
    public void CrossPageCompositionNeedsDeclaredOriginalArtifactEdges()
    {
        var producer = Entry("producer", new(1, [new("owned.resource", "/resource", "materialize")], []));
        var consumer = Entry("consumer", new(1, [], [new("owned.resource", "/resource", true)]));
        var relations = new[] { new CapabilityDecisionPages.Relationship("producer", "acquire", true, false, ["proof_a"]), new CapabilityDecisionPages.Relationship("consumer", "use", true, false, ["proof_b"]) };
        Assert.Equal(2, CapabilityDecisionPages.MinimalComposition(new([producer, consumer], ""), new HashSet<string>(["acquire", "use"]), relations, new HashSet<string>()).Count);
        Assert.Empty(CapabilityDecisionPages.MinimalComposition(new([producer, consumer with { ArtifactContract = null }], ""), new HashSet<string>(["acquire", "use"]), relations, new HashSet<string>()));
        Assert.Empty(CapabilityDecisionPages.MinimalComposition(new([producer, consumer], ""), new HashSet<string>(["acquire", "use"]), relations, new HashSet<string>(["consumer"])));
        static CapabilityCatalogEntry Entry(string id, McpArtifactContract artifact) => new(id, "mcp", "provider", "tool", id, "Declared operation", [], "Declared operation", [], [], artifact, null);
    }
}
