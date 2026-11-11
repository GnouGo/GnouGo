using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonCardReviewWorkflowTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void SemanticChildCardsRetainEachOwnProofAndLaterBusinessVisit()
    {
        var events = Events(); var raw = Encode(events);
        var source = Verify(events.Take(7).ToList());
        Assert.True(source["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(source["passed"]!.GetValue<bool>());
        Assert.Equal(6, source["source_product_reads"]!.GetValue<int>());
        Assert.Equal(3, source["expected_product_count"]!.GetValue<int>());
        Assert.Equal(3, source["source_proof_aliases"]!.AsArray().Count);
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 3, Query)["passed"]!.GetValue<bool>());
        Assert.Equal(raw, Encode(events));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownCardMembershipDoesNotClaimAnExactHttpTwinOrRedirectReceipt(bool heading)
    {
        var events = Known(heading); var result = Verify(events);
        Assert.True(result["passed"]!.GetValue<bool>());
        Assert.Equal(0, result["source_product_reads"]!.GetValue<int>());
        var alias = Assert.Single(result["source_proof_aliases"]!.AsArray())!;
        Assert.Equal("known_source_card_membership", alias["proof_kind"]!.ToString());
        Assert.Empty(alias["proof_event_indices"]!.AsArray());
        Assert.False(alias["http_call_created"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 3, Query)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void OriginalReviewProofCaseRemainsSupportedWithoutChangingV10()
    {
        var events = AmazonReviewProofWorkflowTests.Events();
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.True(LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 3, Query)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void AlreadySupportedSspaKnownContextKeepsItsV10Behavior()
    {
        var events = Known(true);
        foreach (var record in Records(events[0])) record!["href"] = record["href"]!.ToString().Replace("https://sponsored-ads.amazon.fr/clk?", "https://www.amazon.fr/sspa/click?", StringComparison.Ordinal);
        Navigate(events[1], Records(events[0])[0]!["href"]!.ToString(), "B000000001");
        Assert.True(LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 3, Query)["passed"]!.GetValue<bool>());
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("identity")][InlineData("missing_entry")][InlineData("conflict")]
    [InlineData("heading")][InlineData("self")][InlineData("role")][InlineData("context")]
    public void KnownMembershipCannotBorrowAnotherCardOrReplaceMissingIdentity(string change)
    {
        var events = Known(true); var records = Records(events[0]); var alias = records[1]!;
        switch (change)
        {
            case "identity": foreach (var r in records) r!["ancestorAttributes"]!["data-asin"]!["value"] = ""; break;
            case "missing_entry": records.RemoveAt(0); break;
            case "conflict": records[0]!["href"] = "https://www.amazon.fr/dp/B000000004"; break;
            case "heading": alias["heading"] = "h2"; break;
            case "self": alias["selector"] = alias["occurrence"]!["selector"]!.DeepClone(); break;
            case "role": alias["role"] = "listitem"; break;
            case "context": alias["ancestorAttributes"]!["data-asin"]!["index"] = 91; break;
        }
        Reindex(events[0]); Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void NewCaptureRequiresItsOwnGrantAndNeverChangesNavigationRoutes()
    {
        Assert.Equal(AmazonReviewProofPolicy.UrlPattern, AmazonCardReviewPolicy.UrlPattern);
        Assert.Equal(AmazonCardReviewPolicy.Id, AmazonObservedRedirectPolicy.Capture(
            ["--amazon-card-review-context", "--amazon-scope-approval", AmazonCardReviewPolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-card-review-context", "--amazon-scope-approval", AmazonReviewProofPolicy.Approval], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-card-review-context", "--amazon-review-proof-context", "--amazon-scope-approval", AmazonCardReviewPolicy.Approval], "amazon"));
        Assert.False(AmazonCardReviewPolicy.Allows(Records(Events()[0])[2]!["href"]!.ToString()));
        Assert.False(AmazonCardReviewPolicy.Allows("https://sponsored-ads.amazon.fr:443/clk?x=1"));
    }

    [Theory]
    [InlineData("none", true)][InlineData("header_only", false)][InlineData("missing_header", false)]
    [InlineData("swapped_rows", false)][InlineData("invented_field", false)][InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)][InlineData("not_closed", false)][InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)][InlineData("source_only", false)][InlineData("early_fields", false)]
    public Task FullV11OracleRetainsAllWorkbookVisitExecutionAndCleanupChecks(string change, bool passed)
        => AmazonResolvedStoreWorkflowTests.CheckBusinessAsync(change, passed, productResolution: true, cardReview: true);

    [Theory]
    [InlineData("missing_proof")][InlineData("reorder_proof")][InlineData("query")]
    [InlineData("heading")][InlineData("self_occurrence")][InlineData("context")]
    [InlineData("metadata")][InlineData("fragment")][InlineData("missing_sibling")]
    [InlineData("wrong_product")][InlineData("missing_business")][InlineData("close")]
    [InlineData("failed_read")][InlineData("incomplete_source")]
    public void CardBoundariesNeverWaiveProofIdentityOrderCoverageOrCleanup(string change)
    {
        var events = Events(); var records = Records(events[0]); var alias = records[2]!;
        switch (change)
        {
            case "missing_proof": events.RemoveAt(2); break;
            case "reorder_proof": (events[2], events[3]) = (events[3], events[2]); break;
            case "query": alias["href"] += "&changed=1"; break;
            case "heading": alias["heading"] = "a > h2"; break;
            case "self_occurrence": alias["selector"] = alias["occurrence"]!["selector"]!.DeepClone(); break;
            case "context": alias["occurrence"]!["selector"] = "#other"; break;
            case "metadata": alias["ancestorAttributes"]!["data-asin"]!["value"] = "B000000004"; break;
            case "fragment": alias["href"] = alias["href"]!.ToString().Replace("#customerReviews", "#other", StringComparison.Ordinal); break;
            case "missing_sibling": records[1]!["occurrence"] = null; break;
            case "wrong_product": Navigate(events[2], events[2]["arguments"]!["url"]!.ToString(), "B000000004"); break;
            case "missing_business": events.RemoveAt(7); break;
            case "close": events.Insert(3, new() { ["tool"] = "browser_close" }); break;
            case "failed_read": events[2]["error"] = true; break;
            case "incomplete_source": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void ExactNextReadAuthorityAndRepeatedIdentitiesSurviveColdReload()
    {
        var events = Events(sameIdentity: true);
        for (var i = 1; i <= 6; i++) Assert.Equal([events[i]["arguments"]!["url"]!.ToString()],
            LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(i).ToArray(), AmazonCardReviewPolicy.Id));
        var restored = JsonNode.Parse(Encode(events))!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(3, Verify(restored)["expected_product_count"]!.GetValue<int>());
        Assert.True(Verify(restored)["passed"]!.GetValue<bool>());
        restored.RemoveAt(2);
        Assert.False(Verify(restored)["passed"]!.GetValue<bool>());
    }

    internal static List<JsonObject> Events(bool sameIdentity = false)
    {
        var original = AmazonResolvedProductWorkflowTests.Events(); var source = original[0].DeepClone().AsObject();
        var records = Records(source); records.Clear(); List<JsonObject> proofs = []; List<JsonObject> visits = [];
        for (var card = 0; card < 3; card++)
        {
            var id = sameIdentity ? "B000000003" : "B00000000" + new[] { 3, 1, 2 }[card];
            var href = "https://sponsored-ads.amazon.fr/clk?card=" + card;
            records.Add(Link(card, "", href, true)); records.Add(Link(card, "", href, false));
            records.Add(Link(card, "", href + "#customerReviews", false));
            for (var i = 0; i < 2; i++) { var proof = original[18].DeepClone().AsObject(); Navigate(proof, href, id); proofs.Add(proof); }
            var visit = original[18].DeepClone().AsObject(); Navigate(visit, href, id);
            var text = Records(visit); text.Clear(); var number = id[^1] - '0';
            foreach (var value in new[] { "Product " + number, "Description " + number, "Price: " + (11 + number) + ",00 €" }) text.Add(new JsonObject { ["text"] = value });
            visit["result"]!["observationSnapshot"]!["recordCount"] = text.Count; visits.Add(visit);
        }
        Reindex(source); return [source, .. proofs, .. visits];
    }

    private static List<JsonObject> Known(bool heading)
    {
        var template = AmazonResolvedProductWorkflowTests.Events(); var source = template[0].DeepClone().AsObject(); var records = Records(source); records.Clear();
        const string href = "https://sponsored-ads.amazon.fr/clk?opaque=known";
        records.Add(Link(0, "B000000001", href, heading));
        records.Add(Link(0, "B000000001", "https://sponsored-ads.amazon.fr/clk?different=review#customerReviews", false));
        Reindex(source); var visit = template[^1].DeepClone().AsObject(); Navigate(visit, href, "B000000001"); return [source, visit];
    }

    private static JsonObject Link(int card, string id, string href, bool heading) => new()
    {
        ["kind"] = "link", ["tag"] = "a", ["selector"] = "#card-" + card + "-" + (heading ? "title" : Guid.NewGuid().ToString("N")),
        ["href"] = href, ["heading"] = heading ? "a > h2" : null,
        ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject { ["value"] = id, ["index"] = 90, ["tag"] = "div", ["role"] = null } },
        ["occurrence"] = new JsonObject { ["selector"] = "#card-" + card, ["index"] = card, ["tag"] = "article", ["role"] = null }
    };
    private static void Navigate(JsonObject read, string href, string id)
    {
        var final = "https://www.amazon.fr/dp/" + id;
        read["arguments"]!["url"] = href; read["result"]!["url"] = final;
        read["result"]!["acquisition"]!["navigation"]!["url"] = final;
        read["result"]!["navigationEvidence"] = new JsonObject { ["complete"] = true, ["requestedUrl"] = href, ["finalUrl"] = final,
            ["hops"] = new JsonArray(new JsonObject { ["url"] = href, ["method"] = "GET", ["statusCode"] = 302, ["location"] = final },
                new JsonObject { ["url"] = final, ["method"] = "GET", ["statusCode"] = 200 }) };
    }
    private static void Reindex(JsonObject source)
    {
        var records = Records(source); for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
    }
    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static string Encode(IEnumerable<JsonObject> events) => new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString();
    private static JsonObject Verify(IReadOnlyList<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonCardReviewSelection(events, 3, Query);
}
