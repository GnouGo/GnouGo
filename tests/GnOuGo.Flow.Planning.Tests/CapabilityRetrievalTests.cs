using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityRetrievalTests
{
    [Fact]
    public async Task RelevantDeclaredOperationInsideLongDescriptionRecoversWithMatchingExcerpt()
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(30));
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        foreach (var capability in state.Catalog.Capabilities)
        { capability.Method = "archive_export"; capability.Description = "Calibrate laboratory sensors using general guidance and archive information"; }
        var expected = state.Catalog.Capabilities.Last(); expected.Method = "executor";
        const string passage = "Calibrate laboratory sensors using measured reference standards";
        expected.Description = string.Join('\n', Enumerable.Range(0, 300).Select(i => "Unrelated archive operation variant" + i)) + "\n" + passage;
        var invoke = new InvokeIntentOperation { Id = "calibrate", Purpose = passage };
        Assert.DoesNotContain(expected, PlanningCapabilityCards.Shortlist(state.Catalog, "archive export information", 12_000));
        state.IntentPlan = new() { Operations = [invoke] }; state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog).ToList();
        var prompt = PlanningCorrections.Prompt(state, PlanningCorrections.Batch(state));
        var context = JsonNode.Parse(prompt[prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
        var advice = Assert.Single(context["alternatives"]!.AsArray())!["capabilities"]!.AsArray();
        Assert.Equal(expected.Id, advice[0]!["id"]!.ToString());
        Assert.Contains(passage, advice[0]!["description"]!.ToString());
        Assert.True(advice[0]!["description"]!.ToString().Length <= 300);
        Assert.Null(invoke.Capability); Assert.Empty(runtime.Calls);
        var ranked = PlanningCapabilityCards.Rank(state.Catalog.Capabilities, passage).Select(c => c.Id).ToArray();
        expected.Description += "\n" + string.Join('\n', Enumerable.Repeat(passage, 20));
        Assert.Equal(ranked, PlanningCapabilityCards.Rank(state.Catalog.Capabilities.AsEnumerable().Reverse(), passage).Select(c => c.Id));
    }

    [Fact]
    public async Task ConciseReadCapabilityRemainsVisibleAmongVerboseIncidentalMatches()
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(6));
        var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        var expected = state.Catalog.Capabilities.Last();
        expected.Method = "sample_read";
        expected.Description = "Get information on a specific laboratory sample.";
        foreach (var capability in state.Catalog.Capabilities.Where(c => c != expected))
        {
            capability.Method = "archive_export";
            capability.Description = "Export historical laboratory sample metadata from a read-only archive. " +
                "Generate aggregate reports with charts, tables, legends, annotations, attribution, summaries, " +
                "formatting, pagination, compression, checksums, retention policies, scheduling, delivery destinations, " +
                "localization, fonts, images, attachments, filters, ordering, grouping, totals, averages and statistical distributions.";
        }
        var invoke = new InvokeIntentOperation { Id = "observe", Purpose = "Read laboratory sample metadata.",
            Arguments = [new("sample", new() { Kind = "string", Text = "unresolved" })] };
        state.IntentPlan = new() { Operations = [invoke] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog).ToList();

        var prompt = PlanningCorrections.Prompt(state, PlanningCorrections.Batch(state));
        var context = JsonNode.Parse(prompt[prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
        var advice = Assert.Single(context["alternatives"]!.AsArray())!["capabilities"]!.AsArray();
        Assert.Equal(expected.Id, advice[0]!["id"]!.ToString());
        Assert.Equal(4, advice.Count);
        Assert.Equal(expected.Id, PlanningCapabilityCards.Shortlist(state.Catalog, invoke.Purpose, 12_000)[0].Id);
        Assert.Null(invoke.Capability);
        Assert.NotEmpty(PlanningExecutableValidation.Validate(state.Graph, state.Catalog));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void EmptyDocumentsAndEqualScoresKeepStableIdentityOrder()
    {
        PlanningCapability[] capabilities = [new() { Id = "b" }, new() { Id = "a" }];
        Assert.Equal(["a", "b"], PlanningCapabilityCards.Rank(capabilities, "unmatched").Select(c => c.Id));
        Assert.Equal(["a", "b"], PlanningCapabilityCards.Rank(capabilities.Reverse(), "").Select(c => c.Id));
    }
}
