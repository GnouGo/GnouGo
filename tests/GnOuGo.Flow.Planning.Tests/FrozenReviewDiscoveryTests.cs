using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FrozenReviewDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void CapturedReviewMetadataUsesLessThanHalfTheCompleteRequestBudget()
    {
        var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", "retained-review.json")))!;
        long beforeTotal = 0, afterTotal = 0, beforeMaximum = 0, afterMaximum = 0;
        foreach (var entry in recording["responses"]!.AsArray())
        {
            var state = entry!["pendingSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            var issued = state.PendingCall!.Request;
            var before = PlanningJsonTransport.EstimateInputTokens(issued.Prompt, issued.StructuredOutputSchema!.AsObject());
            // Compare fresh presentation of the same frozen metadata. This is a
            // deterministic size measurement, not a replacement inference or receipt.
            state.PendingCall = null; state.Request.Generation.MaxInputTokensPerRequest = 24000;
            var metadata = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
            var shortlist = HybridWorkflowPlanner.Shortlist(state);
            var prompt = HybridWorkflowPlanner.BuildPrompt(state, shortlist);
            var after = PlanningJsonTransport.EstimateInputTokens(prompt, PlanningSchemas.Proposal(state));
            Assert.InRange(after, 1, 24000);
            Assert.All(shortlist.GroupBy(c => c.SourceId), source => Assert.InRange(source.Count(), 1, 4));
            var context = JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
            var detailed = context["operations"]!.AsArray();
            Assert.Equal(shortlist.Count + PlanningDiscoveryContext.Required(state).Count, detailed.Count);
            foreach (var candidate in shortlist)
            {
                var actual = detailed.Single(o => o!["id"]!.ToString() == candidate.Operation!.Id)!;
                Assert.Equal(candidate.Operation!.Description, actual["description"]!.ToString());
                foreach (var port in candidate.Operation.Inputs)
                    Assert.True(JsonNode.DeepEquals(port.Schema, actual["inputs"]!.AsArray().Single(p => p!["name"]!.ToString() == port.Name)!["type"]));
            }
            Assert.Equal(metadata, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
            beforeTotal += before; afterTotal += after; beforeMaximum = Math.Max(beforeMaximum, before); afterMaximum = Math.Max(afterMaximum, after);
            output.WriteLine($"{entry["id"]}: before prompt bytes={Encoding.UTF8.GetByteCount(issued.Prompt)}, complete tokens={before}; after prompt bytes={Encoding.UTF8.GetByteCount(prompt)}, complete tokens={after}; detailed candidates={shortlist.Count}; recorded metadata pages={state.Discovery.Pages.Count}; response bytes={Encoding.UTF8.GetByteCount(entry["response"]?.ToJsonString() ?? "null")}");
        }
        Assert.True(afterMaximum <= beforeMaximum / 2);
        output.WriteLine($"All four retained request identities (including the unconfirmed attempt): maximum {beforeMaximum} -> {afterMaximum}; cumulative {beforeTotal} -> {afterTotal}. No model dispatch or metadata read.");
    }
}
