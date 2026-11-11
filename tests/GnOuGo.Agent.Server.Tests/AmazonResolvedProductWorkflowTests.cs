using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonResolvedProductWorkflowTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void ProductSourceProofCannotReplaceOrderedBusinessVisitsOrUpgradeV8()
    {
        var events = Events(); var original = new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString();
        var source = Verify(events.Take(21).ToList());
        Assert.True(source["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(source["passed"]!.GetValue<bool>());
        Assert.Equal(3, source["source_product_reads"]!.GetValue<int>());
        Assert.Equal(17, source["source_store_reads"]!.GetValue<int>());
        Assert.Equal(0, source["visited_product_count"]!.GetValue<int>());
        Assert.All(source["source_resolution_evidence"]!.AsArray(), e => Assert.False(e!["business_visit"]!.GetValue<bool>()));
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonResolvedStoreSelection(events, 3, Query)["passed"]!.GetValue<bool>());
        Assert.Equal(original, new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString());
        Assert.All(Records(events[0]).Skip(17).Take(3), r => Assert.Equal("", r!["ancestorAttributes"]!["data-asin"]!["value"]!.ToString()));
    }

    [Fact]
    public void EqualDestinationsInDifferentContextsRetainBothOccurrences()
    {
        var events = Events(2);
        var report = LiveWorkflowOracles.VerifyAmazonResolvedProductSelection(events, 4, Query);
        Assert.True(report["passed"]!.GetValue<bool>());
        Assert.Equal(6, report["source_product_reads"]!.GetValue<int>());
        Assert.Equal(4, report["expected_product_count"]!.GetValue<int>());
        events.RemoveAt(24);
        Assert.False(LiveWorkflowOracles.VerifyAmazonResolvedProductSelection(events, 4, Query)["passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("omit_proof")]
    [InlineData("reorder_proof")]
    [InlineData("different_identity")]
    [InlineData("history_identity")]
    [InlineData("missing_heading")]
    [InlineData("second_heading")]
    [InlineData("semantic_occurrence")]
    [InlineData("different_context")]
    [InlineData("bad_tag")]
    [InlineData("navigation_context")]
    [InlineData("bad_reference")]
    [InlineData("borrowed_source_identity")]
    [InlineData("unresolved_context_link")]
    [InlineData("business_before_proof")]
    [InlineData("omitted_business_product")]
    [InlineData("reordered_business_products")]
    [InlineData("invented_business_url")]
    [InlineData("close")]
    [InlineData("failed_read")]
    [InlineData("truncated_source")]
    public void IncompleteConflictingOrReorderedEvidenceCannotQualify(string change)
    {
        var events = Events(); var records = Records(events[0]);
        switch (change)
        {
            case "omit_proof": events.RemoveAt(18); break;
            case "reorder_proof": (events[18], events[19]) = (events[19], events[18]); break;
            case "different_identity": Navigate(events[18], Entry(0), "https://www.amazon.fr/dp/B000000004"); break;
            case "history_identity": events[18]["result"]!["url"] = "https://www.amazon.fr/dp/B000000004"; events[18]["result"]!["navigationEvidence"]!["documentUrl"] = "https://www.amazon.fr/dp/B000000004"; break;
            case "missing_heading": records[18]!["heading"] = null; break;
            case "second_heading": records[17]!["heading"] = "other > h2"; break;
            case "semantic_occurrence": records[17]!["occurrence"] = new JsonObject { ["selector"] = "#other", ["tag"] = "article", ["index"] = 99 }; break;
            case "different_context": records[17]!["ancestorAttributes"]!["data-asin"]!["index"] = 91; break;
            case "bad_tag": foreach (var r in records.Skip(17).Take(3)) r!["ancestorAttributes"]!["data-asin"]!["tag"] = "bad<tag"; break;
            case "navigation_context": foreach (var r in records.Skip(17).Take(3)) r!["ancestorAttributes"]!["data-asin"]!["tag"] = "nav"; break;
            case "bad_reference": records[17]!["reference"] = "other:record:17"; break;
            case "borrowed_source_identity": records[17]!["ancestorAttributes"]!["data-asin"]!["value"] = "B000000001"; break;
            case "unresolved_context_link":
                var extra = records[17]!.DeepClone(); extra["reference"] = "search:record:" + records.Count; extra["href"] = "https://www.amazon.fr/dp/B000000004"; records.Add(extra);
                events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count; break;
            case "business_before_proof": (events[20], events[21]) = (events[21], events[20]); break;
            case "omitted_business_product": events.RemoveAt(21); break;
            case "reordered_business_products": (events[21], events[22]) = (events[22], events[21]); break;
            case "invented_business_url": events[21]["arguments"]!["url"] += "&invented=1"; break;
            case "close": events.Insert(21, new JsonObject { ["tool"] = "browser_close" }); break;
            case "failed_read": events[18]["error"] = true; break;
            case "truncated_source": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void SourceGuardGrantsEachExactResolutionThenOnlyProvedPrimaryLinks()
    {
        var events = Events();
        for (var count = 1; count <= 20; count++) Assert.Equal([events[count]["arguments"]!["url"]!.ToString()],
            LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(count).ToArray(), AmazonResolvedProductPolicy.Id));
        var allowed = LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(21).ToArray(), AmazonResolvedProductPolicy.Id).ToArray();
        Assert.Contains(Entry(1), allowed);
        Assert.DoesNotContain(Entry(0), allowed); // Only the original heading link represents this product's primary entry.
        Assert.DoesNotContain(events[1]["arguments"]!["url"]!.ToString(), allowed);
    }

    [Fact]
    public void CaptureRequiresTheExplicitProductPhaseGrant()
    {
        Assert.Equal(AmazonResolvedProductPolicy.Id, AmazonObservedRedirectPolicy.Capture(
            ["--amazon-resolved-product-context", "--amazon-scope-approval", AmazonResolvedProductPolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-resolved-product-context", "--amazon-scope-approval", AmazonResolvedStorePolicy.Approval], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-resolved-product-context", "--amazon-resolved-store-context", "--amazon-scope-approval", AmazonResolvedProductPolicy.Approval], "amazon"));
        Assert.False(AmazonResolvedStorePolicy.Allows(Entry(0)));
        Assert.True(AmazonResolvedProductPolicy.IsEntry(Entry(0)));
        Assert.False(AmazonResolvedProductPolicy.IsEntry("https://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc"));
        Assert.False(AmazonResolvedProductPolicy.Allows(Entry(0).Replace("amazon.fr/", "amazon.fr:443/", StringComparison.Ordinal)));
        Assert.False(AmazonResolvedProductPolicy.Allows(Entry(0) + "#fragment"));
    }

    [Fact]
    public void V9DoesNotSilentlyReuseRedirectProofForAFragmentAlias()
    {
        var events = Events(); var records = Records(events[0]);
        var alias = records[17]!.DeepClone(); alias["href"] = Entry(1) + "#customerReviews";
        alias["reference"] = "search:record:" + records.Count; records.Add(alias);
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        var report = Verify(events);
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.Contains("search_product_redirect_route_unapproved", report["findings"]!.AsArray().Select(f => f!.ToString()));
        Assert.Empty(LiveWorkflowOracles.ResolvedSourceAllowedEntries([events[0]], AmazonResolvedProductPolicy.Id));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("header_only", false)]
    [InlineData("missing_header", false)]
    [InlineData("swapped_rows", false)]
    [InlineData("invented_field", false)]
    [InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)]
    [InlineData("not_closed", false)]
    [InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)]
    [InlineData("source_only", false)]
    [InlineData("early_fields", false)]
    public Task FullBusinessOracleRequiresLaterBusinessVisitsAndTheirOwnObservedFields(string change, bool passed)
        => AmazonResolvedStoreWorkflowTests.CheckBusinessAsync(change, passed, productResolution: true);

    internal static List<JsonObject> Events(int groups = 1)
    {
        var original = AmazonResolvedStoreWorkflowTests.Events(); var source = original[0]; var records = Records(source);
        List<JsonObject> proofs = []; List<JsonObject> business = [];
        for (var group = 0; group < groups; group++)
        {
            for (var index = 0; index < 3; index++)
            {
                records.Insert(17 + group * 3 + index, new JsonObject { ["kind"] = "link", ["href"] = Entry(index), ["heading"] = index == 1 ? "a > h2" : null,
                    ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject { ["value"] = "", ["tag"] = "div", ["role"] = null, ["index"] = 90 + group } } });
                var proof = original[18].DeepClone().AsObject(); Navigate(proof, Entry(index), "https://www.amazon.fr/dp/B000000003");
                var texts = Records(proof); texts.Clear(); texts.Add(new JsonObject { ["text"] = "Early source evidence" });
                proof["result"]!["observationSnapshot"]!["recordCount"] = 1; proofs.Add(proof);
            }
            var visit = original[18].DeepClone().AsObject(); Navigate(visit, Entry(1), "https://www.amazon.fr/dp/B000000003");
            var fields = Records(visit); fields.Clear();
            foreach (var text in new[] { "Product 3", "Description 3", "Price: 14,00 €" }) fields.Add(new JsonObject { ["text"] = text });
            visit["result"]!["observationSnapshot"]!["recordCount"] = fields.Count; business.Add(visit);
        }
        for (var index = 0; index < records.Count; index++) records[index]!["reference"] = "search:record:" + index;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        return [.. original.Take(18), .. proofs, .. business, .. original.Skip(18)];
    }

    private static string Entry(int index) => "https://sponsored-ads.amazon.fr/clk?opaque=product-" + index;
    private static void Navigate(JsonObject read, string requested, string final)
    {
        read["arguments"]!["url"] = requested; read["result"]!["url"] = final;
        read["result"]!["acquisition"]!["navigation"]!["url"] = final;
        read["result"]!["navigationEvidence"] = new JsonObject { ["complete"] = true, ["requestedUrl"] = requested, ["finalUrl"] = final,
            ["hops"] = new JsonArray(new JsonObject { ["url"] = requested, ["method"] = "GET", ["statusCode"] = 302, ["location"] = final },
                new JsonObject { ["url"] = final, ["method"] = "GET", ["statusCode"] = 200 }) };
    }
    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonResolvedProductSelection(events, 3, Query);
}
