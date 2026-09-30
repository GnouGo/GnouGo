using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RecordedEnumContextTests(ITestOutputHelper output)
{
    [Fact]
    public void AllRetainedRequestsExposeDomainsWithinSavedLimitsWithoutMetadataReads()
    {
        var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", "retained-enums.json")))!;
        var original = recording.ToJsonString(); var estimates = new List<int>();
        foreach (var entry in recording["responses"]!.AsArray())
        {
            var compact = entry!["pendingSession"]!;
            var json = compact.DeepClone().AsObject();
            foreach (var key in new[] { "recordedPages", "recordedResolved", "recordedCatalogIds", "presentationQuery", "recordedInspections" }) json.Remove(key);
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            var ids = compact["recordedCatalogIds"]!.AsArray().Select(n => n!.ToString()).ToHashSet();
            var resolved = compact["recordedResolved"]!.AsArray().Select(n => n!.ToString()).ToHashSet();
            state.Catalog = recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
            state.Catalog.Capabilities.RemoveAll(c => !ids.Contains(c.Id));
            state.Discovery = recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
            state.Discovery.Pages = state.Discovery.Pages.Take(compact["recordedPages"]!.GetValue<int>()).ToList();
            state.Discovery.Resolved.RemoveAll(c => !resolved.Contains(c.Id));
            state.Discovery.Inspections = compact["recordedInspections"]?.Deserialize(PlanningJsonContext.Default.ListPlanningDiscoveryRequest);
            state.Discovery.PresentationQuery = compact["presentationQuery"]?.ToString();
            var issued = state.PendingCall!.Request; state.PendingCall = null;
            var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
            var schema = PlanningSchemas.Proposal(state);
            var prompt = HybridWorkflowPlanner.BuildPrompt(state, HybridWorkflowPlanner.Shortlist(state, resolvedOnly: true));
            var estimate = PlanningJsonTransport.EstimateInputTokens(prompt, schema); estimates.Add(estimate);
            Assert.InRange(estimate, 1, state.Request.Generation.MaxInputTokensPerRequest);
            Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(schema.ToJsonString(), PlanningSchemas.Proposal(state).ToJsonString());
            Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
            output.WriteLine($"Call {state.ModelCalls}: complete tokens {PlanningJsonTransport.EstimateInputTokens(issued.Prompt, issued.StructuredOutputSchema!.AsObject())} -> {estimate}; prompt bytes {Encoding.UTF8.GetByteCount(issued.Prompt)} -> {Encoding.UTF8.GetByteCount(prompt)}; schema bytes {Encoding.UTF8.GetByteCount(issued.StructuredOutputSchema.ToJsonString())} -> {Encoding.UTF8.GetByteCount(schema.ToJsonString())}.");
            if (state.ModelCalls == 7)
            {
                var context = JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
                var command = context["coverage"]!.AsArray().SelectMany(s => s!["index"]?.AsArray() ?? [])
                    .Single(c => c!["id"]!.ToString() == "cap_5c01f9aa688163d23a30ff8b")!;
                var domain = command["constraints"]!["commandName"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
                Assert.Contains("delete_directory", domain); Assert.DoesNotContain("bash", domain);
                Assert.False(command.AsObject().ContainsKey("description")); // No full command catalog repeated in the directory.
                var comments = context["coverage"]!.AsArray().SelectMany(s => s!["index"]?.AsArray() ?? [])
                    .Single(c => c!["id"]!.ToString() == "cap_0c45707eabb02da3b0885cf0")!;
                Assert.Equal(new[] { "LEFT", "RIGHT" }, comments["constraints"]!["side"]!["enum"]!.AsArray().Select(v => v!.ToString()));
                Assert.Equal(new[] { "FILE", "LINE" }, comments["constraints"]!["subjectType"]!["enum"]!.AsArray().Select(v => v!.ToString()));
            }
        }
        Assert.Equal(original, recording.ToJsonString()); Assert.Equal(7, estimates.Count);
        output.WriteLine($"Retained-state rendering: maximum={estimates.Max()}, cumulative={estimates.Sum()}; zero inference/metadata calls. Historical calls and repairs are unchanged.");
    }
}
