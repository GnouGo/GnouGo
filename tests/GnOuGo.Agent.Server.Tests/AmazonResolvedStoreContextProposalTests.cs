using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonResolvedStoreContextProposalTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void ActualRedirectProofCanExplainEmptyContextsWithoutChangingSourceOrV7()
    {
        var events = AmazonSourceResolutionProposalTests.Events();
        var source = events[0]["result"]!["observationSnapshot"]!;
        var records = source["pages"]![0]!["records"]!.AsArray();
        var repeatedProduct = records[17]!.DeepClone();
        repeatedProduct["ancestorAttributes"]!["data-asin"]!["index"] = 3;
        repeatedProduct["group"] = "#repeated-product";
        repeatedProduct["groupContext"]!["index"] = 1;
        repeatedProduct["occurrence"]!["selector"] = "#repeated-product";
        repeatedProduct["occurrence"]!["index"] = 1;
        repeatedProduct["heading"] = "#repeated-heading";
        records.Insert(19, repeatedProduct);
        records[20]!["occurrence"]!["index"] = 2;
        records[20]!["groupContext"]!["index"] = 2;
        for (var index = 0; index < records.Count; index++) records[index]!["reference"] = "search:record:" + index;
        source["recordCount"] = records.Count;
        var before = new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray());
        var result = Verify(events);
        Assert.True(result["source_analysis_complete"]!.GetValue<bool>());
        Assert.False(result["active"]!.GetValue<bool>());
        Assert.False(result["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(result["business_oracle_passed"]!.GetValue<bool>());
        Assert.Equal(17, result["resolved_store_references"]!.AsArray().Count);
        Assert.Equal(new[] { "B000000001", "B000000001", "B000000002" },
            result["first_products"]!.AsArray().Select(p => p!["identity"]!.ToString()));
        Assert.True(JsonNode.DeepEquals(before, new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray())));
        Assert.Contains("search_product_context_unverified", LiveWorkflowOracles.VerifyAmazonProductContextSelection(events, 3, Query)["findings"]!.AsArray().Select(f => f!.ToString()));
        Assert.Empty(LiveWorkflowOracles.CompleteSearchRedirectEntries(events, AmazonProductContextPolicy.Id));
        Assert.False(AmazonObservedRedirectPolicy.IsObserved(LiveWorkflowOracles.AmazonResolvedStoreContextProposalVersion));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("omit")]
    [InlineData("reorder")]
    [InlineData("product_destination")]
    [InlineData("product_occurrence")]
    [InlineData("product_heading")]
    [InlineData("inconsistent_context")]
    [InlineData("truncated")]
    [InlineData("close")]
    [InlineData("new_search")]
    public void DestinationHintsAndContradictoryOrIncompleteEvidenceCannotExcludeAProduct(string change)
    {
        var events = AmazonSourceResolutionProposalTests.Events();
        var records = events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
        switch (change)
        {
            case "missing": events.RemoveRange(1, 17); break;
            case "omit": events.RemoveAt(1); break;
            case "reorder": (events[1], events[2]) = (events[2], events[1]); break;
            case "product_destination":
                const string product = "https://www.amazon.fr/dp/B000000001";
                events[1]["result"]!["url"] = product;
                events[1]["result"]!["navigationEvidence"]!["finalUrl"] = product;
                events[1]["result"]!["navigationEvidence"]!["hops"]![0]!["location"] = product;
                events[1]["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = product;
                break;
            case "product_occurrence": records[0]!["occurrence"] = new JsonObject { ["selector"] = "#product", ["tag"] = "article", ["index"] = 0 }; break;
            case "product_heading": records[0]!["heading"] = "Product heading"; break;
            case "inconsistent_context": records[1]!["ancestorAttributes"]!["data-asin"]!["tag"] = "span"; break;
            case "truncated": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "close": events.Insert(1, new JsonObject { ["tool"] = "browser_close" }); break;
            case "new_search": events.Insert(1, events[0].DeepClone().AsObject()); break;
        }
        Assert.False(Verify(events)["source_analysis_complete"]!.GetValue<bool>());
        Assert.False(Verify(events)["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonResolvedStoreContextProposal(events, 3, Query);
}
