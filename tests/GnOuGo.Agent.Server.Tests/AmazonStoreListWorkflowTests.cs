using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonStoreListWorkflowTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void EveryInheritedStoreLinkKeepsItsOwnProofAndNoBusinessCredit()
    {
        var events = Events(); var raw = Encode(events); var source = Verify(events.Take(15).ToList());
        Assert.True(source["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(source["passed"]!.GetValue<bool>());
        Assert.Equal(8, source["source_store_reads"]!.GetValue<int>());
        Assert.Equal(6, source["source_product_reads"]!.GetValue<int>());
        Assert.Equal(8, source["source_store_list_evidence"]!.AsArray().Count);
        Assert.All(source["source_store_list_evidence"]!.AsArray(), e =>
        {
            Assert.False(e!["business_visit"]!.GetValue<bool>());
            Assert.False(e["http_call_created"]!.GetValue<bool>());
            Assert.Null(e["product_identity"]);
            Assert.Equal("inherited_store_list_navigation", e["proof_kind"]!.ToString());
        });
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonCardReviewSelection(events, 3, Query)["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.Equal(raw, Encode(events));
        Assert.Equal(Verify(events).ToJsonString(), Verify(JsonNode.Parse(raw)!.AsArray().OfType<JsonObject>().ToList()).ToJsonString());
    }

    [Theory]
    [InlineData("missing_own_proof")]
    [InlineData("reordered_proof")]
    [InlineData("missing_sibling")]
    [InlineData("heading")]
    [InlineData("self_occurrence")]
    [InlineData("own_semantic_role")]
    [InlineData("other_tag")]
    [InlineData("other_role")]
    [InlineData("bad_metadata")]
    [InlineData("mixed_product_destination")]
    [InlineData("direct_product")]
    [InlineData("failed_read")]
    [InlineData("truncated_destination")]
    [InlineData("close")]
    [InlineData("new_search")]
    [InlineData("missing_business_visit")]
    public void MissingMixedConflictingOrExpiredEvidenceNeverQualifies(string change)
    {
        var events = Events(); var records = Records(events[0]); var link = records[9]!;
        switch (change)
        {
            case "missing_own_proof": events.RemoveAt(7); break;
            case "reordered_proof": (events[7], events[8]) = (events[8], events[7]); break;
            case "missing_sibling": var added = link.DeepClone(); added["href"] += "&unresolved=1"; records.Add(added); Reindex(events[0]); break;
            case "heading": link["heading"] = "a > h2"; break;
            case "self_occurrence": link["selector"] = link["occurrence"]!["selector"]!.DeepClone(); break;
            case "own_semantic_role": link["role"] = "listitem"; break;
            case "other_tag": foreach (var r in records.Skip(9).Take(4)) r!["occurrence"]!["tag"] = "article"; break;
            case "other_role": foreach (var r in records.Skip(9).Take(4)) r!["occurrence"]!["role"] = null; break;
            case "bad_metadata": link["occurrence"]!["index"] = 999; break;
            case "mixed_product_destination": SetDestination(events[7], "https://www.amazon.fr/dp/B000000001"); break;
            case "direct_product": link["href"] = "https://www.amazon.fr/dp/B000000001"; break;
            case "failed_read": events[7]["error"] = true; break;
            case "truncated_destination": events[7]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "close": events.Insert(15, new JsonObject { ["tool"] = "browser_close" }); break;
            case "new_search": events.Insert(15, events[0].DeepClone().AsObject()); break;
            case "missing_business_visit": events.RemoveAt(15); break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void ExactGrantAndOriginalRoutesRemainMandatory()
    {
        Assert.Equal(AmazonCardReviewPolicy.UrlPattern, AmazonStoreListPolicy.UrlPattern);
        Assert.Equal(AmazonStoreListPolicy.Id, AmazonObservedRedirectPolicy.Capture(
            ["--amazon-store-list-context", "--amazon-scope-approval", AmazonStoreListPolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-store-list-context", "--amazon-scope-approval", AmazonCardReviewPolicy.Approval], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-store-list-context", "--amazon-card-review-context", "--amazon-scope-approval", AmazonStoreListPolicy.Approval], "amazon"));
        Assert.False(AmazonStoreListPolicy.Allows("https://sponsored-ads.amazon.fr:443/clk?x=1"));
        Assert.False(AmazonStoreListPolicy.Allows("https://sponsored-ads.amazon.fr/clk?x=1#customerReviews"));
    }

    [Fact]
    public void EveryOwnResolutionPrecedesOnlyOriginalProductAuthority()
    {
        var events = Events();
        for (var count = 1; count < 15; count++) Assert.Equal([events[count]["arguments"]!["url"]!.ToString()],
            LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(count).ToArray(), AmazonStoreListPolicy.Id));
        var entries = LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(15).ToArray(), AmazonStoreListPolicy.Id).ToArray();
        Assert.Contains(events[15]["arguments"]!["url"]!.ToString(), entries);
        Assert.DoesNotContain(events[7]["arguments"]!["url"]!.ToString(), entries);
    }

    [Theory]
    [InlineData("none", true)][InlineData("header_only", false)][InlineData("missing_header", false)]
    [InlineData("swapped_rows", false)][InlineData("invented_field", false)][InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)][InlineData("not_closed", false)][InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)][InlineData("source_only", false)][InlineData("early_fields", false)]
    public Task FullV12OracleRetainsTheCompleteBusinessBoundary(string change, bool passed)
        => AmazonResolvedStoreWorkflowTests.CheckBusinessAsync(change, passed, productResolution: true, cardReview: true, storeList: true);

    [Fact]
    public void DuplicateStoreUrlsStillRequireEveryOwnOrderedReceipt()
    {
        var events = Events(); var records = Records(events[0]); var first = records[9]!["href"]!.ToString();
        for (var i = 0; i < 4; i++)
        {
            records[9 + i]!["href"] = first; events[7 + i]["arguments"]!["url"] = first;
            var nav = events[7 + i]["result"]!["navigationEvidence"]!;
            nav["requestedUrl"] = first; nav["hops"]![0]!["url"] = first;
        }
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.Equal(14, Verify(events)["source_resolution_count"]!.GetValue<int>());
        events.RemoveAt(8);
        Assert.False(Verify(events)["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    internal static List<JsonObject> Events()
    {
        var events = AmazonCardReviewWorkflowTests.Events(); var records = Records(events[0]);
        var template = AmazonResolvedStoreWorkflowTests.Events()[1]; List<JsonObject> proofs = [];
        for (var i = 0; i < 8; i++)
        {
            var href = "https://sponsored-ads.amazon.fr/clk/?opaque=store-list-" + i;
            records.Add(new JsonObject { ["kind"] = "link", ["tag"] = "a", ["selector"] = "#store-link-" + i,
                ["href"] = href, ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject
                    { ["value"] = "", ["tag"] = "div", ["role"] = null, ["index"] = 900 } },
                ["occurrence"] = new JsonObject { ["selector"] = "#store-card-" + i / 4, ["tag"] = "li", ["role"] = "listitem", ["index"] = 119 + i / 4 } });
            var proof = template.DeepClone().AsObject(); proof["arguments"]!["url"] = href;
            proof["result"]!["navigationEvidence"]!["requestedUrl"] = href;
            proof["result"]!["navigationEvidence"]!["hops"]![0]!["url"] = href; proofs.Add(proof);
        }
        Reindex(events[0]); events.InsertRange(7, proofs); return events;
    }
    private static void SetDestination(JsonObject read, string target)
    {
        read["result"]!["url"] = target; read["result"]!["acquisition"] = new JsonObject { ["navigation"] = new JsonObject { ["url"] = target } };
        var n = read["result"]!["navigationEvidence"]!; n["finalUrl"] = target; n["hops"]![0]!["location"] = target; n["hops"]![1]!["url"] = target;
    }
    private static void Reindex(JsonObject source)
    {
        var records = Records(source); for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
    }
    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static string Encode(IEnumerable<JsonObject> events) => new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString();
    private static JsonObject Verify(IReadOnlyList<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonStoreListSelection(events, 3, Query);
}
