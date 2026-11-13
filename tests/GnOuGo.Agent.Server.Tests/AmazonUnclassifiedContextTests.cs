using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonUnclassifiedContextTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnsupportedHeadingContextRemainsUnverifiedWithoutChangingV12OrGrantingNavigation(bool duplicateHrefs, bool afterKnownProducts)
    {
        var events = AmazonStoreListWorkflowTests.Events();
        var records = events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
        var offset = afterKnownProducts ? records.Count : 0;
        for (var i = 0; i < 4; i++)
            records.Insert(offset + i, new JsonObject
            {
                ["kind"] = "link", ["tag"] = "a", ["selector"] = "#unsupported-" + i,
                ["href"] = "https://unapproved.example/x/c/" + (duplicateHrefs ? 0 : i),
                ["heading"] = i == 1 ? "#unsupported-heading" : null,
                ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject
                    { ["value"] = "", ["tag"] = "div", ["role"] = null, ["index"] = 901 } }
            });
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        var original = Encode(events);
        // Preserve the historical verdict while exposing its coverage gap. A
        // complete execution under that version cannot settle this missing proof.
        Assert.True(LiveWorkflowOracles.VerifyAmazonStoreListSelection(events, 3, "chaussure geox homme 45")["passed"]!.GetValue<bool>());
        var strict = LiveWorkflowOracles.VerifyAmazonSourceCoverageSelection(events, 3, "chaussure geox homme 45");
        Assert.False(strict["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.Equal(LiveWorkflowOracles.AmazonSourceCoverageVersion, strict["version"]!.ToString());
        Assert.Single(strict["unclassified_heading_contexts"]!.AsArray());
        Assert.Contains("search_heading_context_unaccounted", strict["findings"]!.AsArray().Select(f => f!.ToString()));
        Assert.Empty(LiveWorkflowOracles.ResolvedSourceAllowedEntries(events, AmazonSourceCoveragePolicy.Id));
        var report = LiveWorkflowOracles.AnalyzeAmazonUnclassifiedHeadingContexts(events, "chaussure geox homme 45");
        var context = Assert.Single(report["unclassified_heading_contexts"]!.AsArray())!;
        Assert.Equal("search:record:" + (offset + 1), context["heading_record"]!.ToString());
        Assert.Equal(4, context["source_records"]!.AsArray().Count);
        Assert.Equal("", context["asin_context"]!["value"]!.ToString());
        Assert.Null(context["product_identity"]);
        Assert.Equal("unverified", context["classification"]!.ToString());
        Assert.False(context["navigation_authorized"]!.GetValue<bool>());
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(report["business_oracle_passed"]!.GetValue<bool>());
        Assert.Equal(0, report["navigation_calls"]!.GetValue<int>());
        Assert.False(AmazonStoreListPolicy.Allows(records[offset]!["href"]!.ToString()));
        Assert.Equal(original, Encode(events));
        Assert.Equal(report.ToJsonString(), LiveWorkflowOracles.AnalyzeAmazonUnclassifiedHeadingContexts(
            JsonNode.Parse(original)!.AsArray().OfType<JsonObject>().ToList(), "chaussure geox homme 45").ToJsonString());
    }

    [Fact]
    public void SupportedProvedContextsAreNotReclassifiedByTheOfflineInvestigation()
    {
        var report = LiveWorkflowOracles.AnalyzeAmazonUnclassifiedHeadingContexts(AmazonStoreListWorkflowTests.Events(), "chaussure geox homme 45");
        Assert.Empty(report["unclassified_heading_contexts"]!.AsArray());
        Assert.True(report["v12_source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(report["active"]!.GetValue<bool>());
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.True(LiveWorkflowOracles.VerifyAmazonSourceCoverageSelection(AmazonStoreListWorkflowTests.Events(), 3, "chaussure geox homme 45")["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void StricterCoveragePreservesRoutesAndExistingGrantWithoutActivatingAaxWorkflowNavigation()
    {
        Assert.Equal(AmazonStoreListPolicy.UrlPattern, AmazonSourceCoveragePolicy.UrlPattern);
        Assert.Equal(AmazonStoreListPolicy.Approval, AmazonSourceCoveragePolicy.Approval);
        Assert.False(AmazonSourceCoveragePolicy.Allows(AmazonAaxSourceDiagnosticTests.First));
        Assert.Equal(AmazonSourceCoveragePolicy.Id, AmazonObservedRedirectPolicy.Capture(
            ["--amazon-source-coverage", "--amazon-scope-approval", AmazonStoreListPolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-source-coverage", "--amazon-scope-approval", AmazonAaxSourceDiagnostic.Approval], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-source-coverage", "--amazon-store-list-context", "--amazon-scope-approval", AmazonStoreListPolicy.Approval], "amazon"));
    }

    [Theory]
    [InlineData("none", true)][InlineData("header_only", false)][InlineData("missing_header", false)]
    [InlineData("swapped_rows", false)][InlineData("invented_field", false)][InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)][InlineData("not_closed", false)][InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)][InlineData("source_only", false)][InlineData("early_fields", false)]
    public Task StricterCoverageRetainsEveryIndependentBusinessRequirement(string change, bool passed)
        => AmazonResolvedStoreWorkflowTests.CheckBusinessAsync(change, passed, productResolution: true, cardReview: true, storeList: true, sourceCoverage: true);

    private static string Encode(IEnumerable<JsonObject> events) => new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString();
}
