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
        // Frozen 52683a4 measurements; historical implementations are not retained.
        int[] previousTokens = [9312, 18040, 20574, 20574], previousBytes = [8022, 34123, 41725, 41725];
        var index = 0;
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
            Assert.InRange(after, 1, 21600);
            Assert.InRange(shortlist.Count, 0, PlanningDiscoveryContext.Candidates(state).Count);
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
            output.WriteLine($"Compared with 52683a4: prompt bytes {previousBytes[index]} -> {Encoding.UTF8.GetByteCount(prompt)}; complete tokens {previousTokens[index++]} -> {after}.");
        }
        Assert.True(afterMaximum <= beforeMaximum / 2);
        output.WriteLine($"All four retained request identities (including the unconfirmed attempt): maximum {beforeMaximum} -> {afterMaximum}; cumulative {beforeTotal} -> {afterTotal}. No model dispatch or metadata read.");
        Assert.Equal(4, index); // More admitted contracts may legitimately consume more of the unchanged allowance.
        output.WriteLine($"52683a4 maximum {previousTokens.Max()} -> {afterMaximum}; cumulative {previousTokens.Sum()} -> {afterTotal}.");
    }

    [Fact]
    public void RetainedResponseCompactsLosslesslyAndRepairKeepsMandatoryContext()
    {
        var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", "retained-review.json")))!;
        var state = recording["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        var original = recording["responses"]!.AsArray()[^1]!["response"]!["json"]!;
        var compact = original.DeepClone(); compact["plan"] = PlanningJsonTransport.TaskPlanPrompt(state.Plan);
        int Bytes(JsonNode value) => Encoding.UTF8.GetByteCount(PlanningJsonTransport.Prompt(value));
        Assert.Equal(16784, Bytes(original["plan"]!)); Assert.Equal(16818, Bytes(original));
        Assert.Equal(16344, Bytes(compact["plan"]!)); Assert.Equal(16378, Bytes(compact));
        Assert.Empty(PlanningContractValidation.ValidateInstance(compact, PlanningSchemas.Proposal(state)));
        var restored = compact["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(restored, PlanningJsonContext.Default.TaskPlan));
        var compiler = new TaskPlanCompiler(); var a = compiler.Compile(state.Plan!, state.Catalog!); var b = compiler.Compile(restored, state.Catalog!);
        Assert.Empty(a.Diagnostics); Assert.Empty(b.Diagnostics);
        PlanningConfirmationGuards.Apply(a.Graph!, state.Catalog!); PlanningConfirmationGuards.Apply(b.Graph!, state.Catalog!);
        Assert.Equal(new PlanningGraphCompiler().Compile(a.Graph!, state.Catalog!), new PlanningGraphCompiler().Compile(b.Graph!, state.Catalog!));
        var hash = state.ComputeArtifactHash(); state.Plan = restored; Assert.Equal(hash, state.ComputeArtifactHash());
        output.WriteLine($"TaskPlan bytes: {Bytes(original["plan"]!)} -> {Bytes(compact["plan"]!)}; response JSON bytes: {Bytes(original)} -> {Bytes(compact)}; serialized output estimate={(Bytes(compact) + 2) / 3}, excluding reasoning. Tasks={TaskPlanRevisions.Tasks(restored).Count()}, transforms={TaskPlanRevisions.Tasks(restored).Count(t => t.Kind == "transform")}.");

        // Synthetic binding-only repair of this exact plan. No claim that the historical run needed it.
        state.PendingCall = null; state.Request.Generation.MaxInputTokensPerRequest = 24000;
        state.RevisionScope = ["/root/outputs/" + restored.Root.Outputs[0].Name];
        state.Diagnostics = [new("TASK_INPUT_TYPE", state.RevisionScope[0], "Synthetic binding-only size probe")];
        var prompt = HybridWorkflowPlanner.Prompt(state); var estimate = PlanningJsonTransport.EstimateInputTokens(prompt, PlanningSchemas.Proposal(state));
        var context = JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        Assert.Equal(PlanningDiscoveryContext.Required(state).Count, context["operations"]!.AsArray().Count);
        Assert.Empty(HybridWorkflowPlanner.Shortlist(state));
        Assert.All(context["coverage"]!.AsArray(), c => Assert.False(c!.AsObject().ContainsKey("index")));
        output.WriteLine($"Synthetic fixed-operation repair: prompt bytes={Encoding.UTF8.GetByteCount(prompt)}, complete tokens={estimate}; original presentation probe at f46a40d=29018. Saved admission limit remains 24000; mandatory contracts are never pruned.");
    }
}
