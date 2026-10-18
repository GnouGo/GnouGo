using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RecordedEnumContextTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedRenderingPreservesDomainsAndReportsOptionalOmissionWithoutMetadataReads(bool freshAllowance)
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
            if (freshAllowance) state.Request.Generation.MaxInputTokensPerRequest = 32000; // Fresh presentation probe only; issued evidence remains unchanged.
            var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
            var presentation = new PlanningPrompt(state);
            var prompt = presentation.Build(presentation.Shortlist(resolvedOnly: true));
            var schema = presentation.Schema;
            var estimate = PlanningJsonTransport.EstimateInputTokens(prompt, schema); estimates.Add(estimate);
            Assert.InRange(estimate, 1, state.Request.Generation.MaxInputTokensPerRequest);
            Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(schema.ToJsonString(), new PlanningPrompt(state).Request().StructuredOutputSchema!.ToJsonString());
            Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
            output.WriteLine($"Call {state.ModelCalls}: complete tokens {PlanningJsonTransport.EstimateInputTokens(issued.Prompt, issued.StructuredOutputSchema!.AsObject())} -> {estimate}; prompt bytes {Encoding.UTF8.GetByteCount(issued.Prompt)} -> {Encoding.UTF8.GetByteCount(prompt)}; schema bytes {Encoding.UTF8.GetByteCount(issued.StructuredOutputSchema.ToJsonString())} -> {Encoding.UTF8.GetByteCount(schema.ToJsonString())}.");
            if (state.ModelCalls == 7)
            {
                var context = JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
                JsonNode? Domain(string id, string input)
                {
                    // Admission may move an operation between the compact directory
                    // and exact-contract view. Either view must retain its domain.
                    var entry = context["coverage"]!.AsArray().SelectMany(s => s!["index"]?.AsArray() ?? [])
                        .SingleOrDefault(c => c!["id"]!.ToString() == id);
                    if (entry is not null)
                    {
                        Assert.False(entry.AsObject().ContainsKey("description"));
                        return entry["constraints"]![input]!;
                    }
                    var operation = context["operations"]!.AsArray().SingleOrDefault(o => o!["id"]!.ToString() == id);
                    if (operation is null)
                    {
                        Assert.False(freshAllowance); // The new creation allowance retains these fixture domains.
                        Assert.DoesNotContain(PlanningDiscoveryContext.Required(state), o => o.Id == id);
                        Assert.Contains(context["coverage"]!.AsArray(), s => s?["omittedFromIndex"]?.GetValue<int>() > 0);
                        return null; // Full serialized admission can omit optional directory entries, with visible uncertainty.
                    }
                    return CommitmentPromptContractTests.Expand(operation["inputs"]!.AsArray().Single(i => i!["name"]!.ToString() == input)!["type"]!, context);
                }
                if (Domain("cap_5c01f9aa688163d23a30ff8b", "commandName") is { } command)
                {
                    var domain = command["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
                    Assert.Contains("delete_directory", domain); Assert.DoesNotContain("bash", domain);
                }
                if (Domain("cap_0c45707eabb02da3b0885cf0", "side") is { } side)
                    Assert.Equal(new[] { "LEFT", "RIGHT" }, side["enum"]!.AsArray().Select(v => v!.ToString()));
                if (Domain("cap_0c45707eabb02da3b0885cf0", "subjectType") is { } subject)
                    Assert.Equal(new[] { "FILE", "LINE" }, subject["enum"]!.AsArray().Select(v => v!.ToString()));
            }
        }
        Assert.Equal(original, recording.ToJsonString()); Assert.Equal(7, estimates.Count);
        output.WriteLine($"Retained-state rendering: maximum={estimates.Max()}, cumulative={estimates.Sum()}; zero inference/metadata calls. Historical calls and repairs are unchanged.");
    }
}
