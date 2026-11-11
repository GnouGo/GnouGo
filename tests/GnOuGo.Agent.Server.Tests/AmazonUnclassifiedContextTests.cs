using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonUnclassifiedContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedHeadingContextRemainsUnverifiedWithoutChangingV12OrGrantingNavigation(bool duplicateHrefs)
    {
        var events = AmazonStoreListWorkflowTests.Events();
        var records = events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
        for (var i = 0; i < 4; i++)
            records.Insert(i, new JsonObject
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
        var report = LiveWorkflowOracles.AnalyzeAmazonUnclassifiedHeadingContexts(events, "chaussure geox homme 45");
        var context = Assert.Single(report["unclassified_heading_contexts"]!.AsArray())!;
        Assert.Equal("search:record:1", context["heading_record"]!.ToString());
        Assert.Equal(4, context["source_records"]!.AsArray().Count);
        Assert.Equal("", context["asin_context"]!["value"]!.ToString());
        Assert.Null(context["product_identity"]);
        Assert.Equal("unverified", context["classification"]!.ToString());
        Assert.False(context["navigation_authorized"]!.GetValue<bool>());
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(report["business_oracle_passed"]!.GetValue<bool>());
        Assert.Equal(0, report["navigation_calls"]!.GetValue<int>());
        Assert.False(AmazonStoreListPolicy.Allows(records[0]!["href"]!.ToString()));
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
    }

    private static string Encode(IEnumerable<JsonObject> events) => new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString();
}
