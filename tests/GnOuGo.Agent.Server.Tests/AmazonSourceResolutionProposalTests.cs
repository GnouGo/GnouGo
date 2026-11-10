using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonSourceResolutionProposalTests
{
    private const string Query = "chaussure geox homme 45";
    private const string Store = "https://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc";

    [Fact]
    public void RetainedSeventeenLinkShapeCannotBeResolvedFromStoreHintsOrChildProducts()
    {
        var events = Events();
        var result = Verify([events[0]]);
        Assert.False(result["resolution_evidence_complete"]!.GetValue<bool>());
        Assert.Equal(17, result["resolutions"]!.AsArray().Count);
        Assert.All(result["resolutions"]!.AsArray(), r => Assert.Equal("indeterminate", r!["destination_kind"]!.ToString()));
        Assert.Contains("search_product_context_unverified", LiveWorkflowOracles.VerifyAmazonProductContextSelection([events[0]], 2, Query)["findings"]!.AsArray().Select(f => f!.ToString()));
        Assert.Empty(LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]], AmazonProductContextPolicy.Id));
    }

    [Fact]
    public void CompleteExactRedirectEvidenceResolvesDestinationsWithoutActivatingAnOracleOrQualifyingSource()
    {
        var events = Events(); var result = Verify(events);
        Assert.True(result["resolution_evidence_complete"]!.GetValue<bool>());
        Assert.False(result["active"]!.GetValue<bool>());
        Assert.False(result["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(result["business_oracle_passed"]!.GetValue<bool>());
        Assert.Equal(Enumerable.Range(0, 17).Select(i => "search:record:" + i),
            result["resolutions"]!.AsArray().Select(r => r!["source_record"]!.ToString()));
        Assert.All(result["resolutions"]!.AsArray(), r => Assert.Equal("store_navigation", r!["destination_kind"]!.ToString()));
        Assert.False(LiveWorkflowOracles.VerifyAmazonProductContextSelection(events, 2, Query)["passed"]!.GetValue<bool>());
        Assert.False(AmazonObservedRedirectPolicy.IsObserved(LiveWorkflowOracles.AmazonSourceResolutionProposalVersion));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Validate(LiveWorkflowOracles.AmazonSourceResolutionProposalVersion, "amazon"));
        Assert.False(AmazonSponsoredHostPolicy.Allows(Store));
    }

    [Fact]
    public void AdvertisedStoreHintDoesNotOverrideAnActuallyObservedProductOrDeduplicateOccurrences()
    {
        var events = Events();
        foreach (var read in events.Skip(1).Take(2)) SetDestination(read, "https://www.amazon.fr/dp/B000000001");
        var result = Verify(events);
        Assert.True(result["resolution_evidence_complete"]!.GetValue<bool>());
        Assert.Equal(17, result["resolutions"]!.AsArray().Count);
        Assert.All(result["resolutions"]!.AsArray().Take(2), r =>
        {
            Assert.Equal("product", r!["destination_kind"]!.ToString());
            Assert.Equal("B000000001", r["product_identity"]!.ToString());
            Assert.False(r["occurrence_verified"]!.GetValue<bool>());
        });
        Assert.False(result["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("omit")]
    [InlineData("reorder")]
    [InlineData("substitute")]
    [InlineData("decoded_target")]
    [InlineData("bad_reference")]
    [InlineData("narrow_search")]
    [InlineData("wrong_query")]
    [InlineData("truncated_search")]
    [InlineData("truncated_destination")]
    [InlineData("missing_redirect")]
    [InlineData("external_hop")]
    [InlineData("unapproved_clk")]
    [InlineData("post")]
    [InlineData("failure")]
    [InlineData("close")]
    [InlineData("new_search")]
    public void IncompleteExpiredOrContradictoryEvidenceStaysUnresolved(string change)
    {
        var events = Events();
        switch (change)
        {
            case "omit": events.RemoveAt(1); break;
            case "reorder": (events[1], events[2]) = (events[2], events[1]); break;
            case "substitute": events[1]["arguments"]!["url"] = Entry(99); break;
            case "decoded_target": events[1]["arguments"]!["url"] = Store; break;
            case "bad_reference": Records(events[0])[0]!["reference"] = "other:record:0"; break;
            case "narrow_search": events[0]["arguments"]!["selector"] = "#collection"; break;
            case "wrong_query": events[0]["arguments"]!["url"] = "https://www.amazon.fr/s?k=wrong"; break;
            case "truncated_search": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "truncated_destination": events[1]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "missing_redirect": events[1]["result"]!["navigationEvidence"] = null; break;
            case "external_hop": SetDestination(events[1], "https://example.org/stores/page/12345678-1234-1234-1234-123456789abc"); break;
            case "unapproved_clk": Records(events[0])[0]!["href"] = Entry(0).Replace("/clk/?", "/clk?", StringComparison.Ordinal); break;
            case "post": events[1]["result"]!["navigationEvidence"]!["hops"]![0]!["method"] = "POST"; break;
            case "failure": events[1]["result"]!["navigationEvidence"]!["hops"]![1]!["statusCode"] = 404; break;
            case "close": events.Insert(1, new JsonObject { ["tool"] = "browser_close" }); break;
            case "new_search": events.Insert(1, events[0].DeepClone().AsObject()); break;
        }
        Assert.False(Verify(events)["resolution_evidence_complete"]!.GetValue<bool>());
        Assert.False(Verify(events)["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("http://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc")]
    [InlineData("https://www.amazon.fr:443/stores/page/12345678-1234-1234-1234-123456789abc")]
    [InlineData("https://user@www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc")]
    [InlineData("https://www.amazon.fr/stores/BrandName")]
    [InlineData("https://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc#fragment")]
    [InlineData("https://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc?invalid=\n")]
    [InlineData("https://sponsored-ads.amazon.fr/clk?opaque=bad")]
    public void ProposalAddsOnlyTheExactStorePageRoute(string url)
        => Assert.False(LiveWorkflowOracles.ProposedSourceResolutionRoute(url));

    internal static List<JsonObject> Events()
    {
        var original = AmazonProductContextOracleTests.Events();
        var source = original[0].DeepClone().AsObject(); var records = Records(source);
        records.RemoveAt(0);
        for (var i = 16; i >= 0; i--) records.Insert(0, new JsonObject
        {
            ["kind"] = "link", ["href"] = Entry(i), ["text"] = i < 8 ? "Sponsored collection" : "Related brand",
            ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject
                { ["value"] = "", ["tag"] = "div", ["role"] = null, ["index"] = i < 4 ? 0 : i < 8 ? 24 : 80 } }
        });
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        List<JsonObject> events = [source];
        for (var i = 0; i < 17; i++)
        {
            var visit = original[1].DeepClone().AsObject();
            visit["arguments"]!["url"] = Entry(i);
            visit["result"]!["navigationEvidence"]!["requestedUrl"] = Entry(i);
            visit["result"]!["navigationEvidence"]!["hops"]![0]!["url"] = Entry(i);
            SetDestination(visit, Store); events.Add(visit);
        }
        return events;
    }

    private static string Entry(int index) => "https://sponsored-ads.amazon.fr/clk/?opaque=fixture-" + index + "&r=" + Uri.EscapeDataString(Store);
    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonSourceResolutionProposal(events, Query);
    private static void SetDestination(JsonObject read, string url)
    {
        read["result"]!["url"] = url;
        var navigation = read["result"]!["navigationEvidence"]!;
        navigation["finalUrl"] = url; navigation["hops"]![0]!["location"] = url; navigation["hops"]![1]!["url"] = url;
    }
}
