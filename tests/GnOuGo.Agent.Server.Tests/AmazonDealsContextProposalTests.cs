using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonDealsContextProposalTests
{
    private const string Query = "chaussure geox homme 45";
    private const string Deals = "https://www.amazon.fr/deals";

    [Fact]
    public void OwnAuxiliaryProofSupportsOnlyTheInactiveRelationshipAndPreservesEveryRecord()
    {
        var events = Events(); var bytes = Serialize(events);
        var old = LiveWorkflowOracles.VerifyAmazonAaxProductSelection(events, 3, Query);
        Assert.False(old["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(AmazonAaxProductPolicy.Allows(Deals));
        var result = Analyze(events);
        Assert.True(result["relationship_evidence_complete"]!.GetValue<bool>(), result.ToJsonString());
        Assert.False(result["active"]!.GetValue<bool>());
        foreach (var key in new[] { "source_prerequisite_qualified", "business_oracle_passed", "navigation_authorized" })
            Assert.False(result[key]!.GetValue<bool>());
        Assert.Equal(4, result["ordered_own_read_count"]!.GetValue<int>());
        Assert.Equal(1, result["own_deals_read_count"]!.GetValue<int>());
        var context = Assert.Single(result["contexts"]!.AsArray())!;
        Assert.Equal(5, context["original_member_count"]!.GetValue<int>());
        Assert.Equal(3, context["own_product_reads"]!.GetValue<int>());
        Assert.Single(context["exact_review_aliases"]!.AsArray());
        Assert.Equal(bytes, Serialize(events));
        Assert.True(JsonNode.DeepEquals(result, Analyze(JsonNode.Parse(bytes)!.AsArray().OfType<JsonObject>().ToArray())));
        Assert.True(JsonNode.DeepEquals(old, LiveWorkflowOracles.VerifyAmazonAaxProductSelection(events, 3, Query)));
    }

    [Fact]
    public void RepeatedAuxiliaryOccurrencesEachRequireTheirOwnReadInSourceOrder()
    {
        var events = Events(); var records = Records(events);
        var repeat = records[3]!.DeepClone().AsObject(); repeat["selector"] = "#deal-repeat"; records.Insert(4, repeat);
        Reindex(events); events.Insert(4, DealRead("own-deal-repeat"));
        var result = Analyze(events);
        Assert.True(result["relationship_evidence_complete"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(2, result["own_deals_read_count"]!.GetValue<int>());
        events.RemoveAt(4);
        Assert.False(Analyze(events)["relationship_evidence_complete"]!.GetValue<bool>());
    }

    [Fact]
    public void LegacySponsoredRoutesRetainTheSameContextRelationship()
    {
        var events = JsonNode.Parse(Serialize(Events()).Replace("https://aax-eu-zaz.amazon.fr/x/c/",
            "https://sponsored-ads.amazon.fr/clk?opaque=", StringComparison.Ordinal))!.AsArray().OfType<JsonObject>().ToArray();
        var result = Analyze(events);
        Assert.True(result["relationship_evidence_complete"]!.GetValue<bool>(), result.ToJsonString());
        Assert.False(LiveWorkflowOracles.VerifyAmazonAaxProductSelection(events, 3, Query)["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    [Fact]
    public void AnAlreadyIdentifiedProductBeforeTheEmptyCardIsRetainedWithoutProposingItsReclassification()
    {
        var events = Events(); var records = Records(events);
        var heading = records[1]!.DeepClone().AsObject(); var offer = records[3]!.DeepClone().AsObject();
        foreach (var record in new[] { heading, offer })
        {
            record["ancestorAttributes"]!["data-asin"]!["value"] = "B000000002";
            record["ancestorAttributes"]!["data-asin"]!["index"] = 900;
            record["selector"] = "#known-" + record["selector"]!.ToString();
        }
        records.Insert(0, heading); records.Insert(1, offer); Reindex(events);
        events.Insert(1, DealRead("known-card-own-deal"));
        var bytes = Serialize(events); var result = Analyze(events);
        Assert.True(result["relationship_evidence_complete"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Single(result["contexts"]!.AsArray());
        Assert.Equal(5, result["ordered_own_read_count"]!.GetValue<int>());
        Assert.Equal(bytes, Serialize(events));
    }

    [Theory]
    [InlineData("missing_deals_proof")][InlineData("missing_product_repeat")][InlineData("wrong_order")]
    [InlineData("reused_capture")][InlineData("truncated")][InlineData("failed_read")]
    [InlineData("missing_navigation")][InlineData("wrong_source_reference")][InlineData("wrong_receipt_reference")]
    [InlineData("cross_card")][InlineData("extra_heading")][InlineData("semantic_occurrence")]
    [InlineData("missing_context")][InlineData("context_not_empty")][InlineData("conflicting_product")]
    [InlineData("unproved_alias")][InlineData("unknown_sibling")][InlineData("source_incomplete")]
    [InlineData("wrong_query")][InlineData("closed_early")][InlineData("new_search")]
    [InlineData("deals_query")][InlineData("deals_slash")][InlineData("deals_fragment")]
    [InlineData("deals_host")][InlineData("product_destination")][InlineData("different_document")]
    [InlineData("unobserved_hop")][InlineData("normalized_hop")][InlineData("not_200")]
    public void MissingConflictingUnorderedOrBroaderEvidenceRemainsUnverified(string change)
    {
        var events = Events(); var records = Records(events); var read = events[3];
        switch (change)
        {
            case "missing_deals_proof": events.RemoveAt(3); break;
            case "missing_product_repeat": events.RemoveAt(4); break;
            case "wrong_order": (events[1], events[2]) = (events[2], events[1]); break;
            case "reused_capture": read["result"]!["observationSnapshot"]!["id"] = "image"; break;
            case "truncated": read["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "failed_read": read["error"] = true; break;
            case "missing_navigation": read["result"]!["navigationEvidence"] = null; break;
            case "wrong_source_reference": records[3]!["reference"] = "other:record:3"; break;
            case "wrong_receipt_reference": read["diagnosticSourceReference"] = "other:record:3"; break;
            case "cross_card": records[3]!["ancestorAttributes"]!["data-asin"]!["index"] = 902; break;
            case "extra_heading": records[3]!["heading"] = "h3"; break;
            case "semantic_occurrence": records[3]!["occurrence"] = new JsonObject { ["tag"] = "li", ["role"] = null, ["index"] = 902, ["selector"] = "#separate" }; break;
            case "missing_context": records[3]!["ancestorAttributes"]!["data-asin"] = null; break;
            case "context_not_empty": records[3]!["ancestorAttributes"]!["data-asin"]!["value"] = "B000000002"; break;
            case "conflicting_product": events[4] = JsonNode.Parse(events[4].ToJsonString().Replace("B000000001", "B000000002", StringComparison.Ordinal))!.AsObject(); break;
            case "unproved_alias": records[2]!["href"] = AmazonAaxSourceDiagnosticTests.Heading + "&unseen=1#customerReviews"; break;
            case "unknown_sibling": records[3]!["href"] = "https://www.amazon.fr/unknown"; break;
            case "source_incomplete": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "closed_early": events.Insert(2, new JsonObject { ["tool"] = "browser_close" }); break;
            case "new_search": events.Insert(2, events[0].DeepClone().AsObject()); break;
            case "deals_query": records[3]!["href"] = Deals + "?ref=seen"; break;
            case "deals_slash": records[3]!["href"] = Deals + "/"; break;
            case "deals_fragment": records[3]!["href"] = Deals + "#seen"; break;
            case "deals_host": records[3]!["href"] = "https://amazon.fr/deals"; break;
            case "product_destination": events[3] = AmazonAaxSourceDiagnosticTests.Resolution(Deals, "deal-product"); break;
            case "different_document": read["result"]!["url"] = Deals + "/"; break;
            case "unobserved_hop": read["result"]!["navigationEvidence"]!["hops"]![0]!["url"] = Deals + "?unseen=1"; break;
            case "normalized_hop": events[1]["result"]!["navigationEvidence"]!["hops"]![0]!["location"] = "https://aax-eu-zaz.amazon.fr/x/c/unseen/../opaque-heading/https://www.amazon.fr/dp/B000000001?observed=2"; break;
            case "not_200": read["result"]!["navigationEvidence"]!["hops"]![0]!["statusCode"] = 204; break;
        }
        var bytes = Serialize(events);
        var result = LiveWorkflowOracles.AnalyzeAmazonDealsContextProposal(events, change == "wrong_query" ? "different" : Query);
        Assert.False(result["relationship_evidence_complete"]!.GetValue<bool>(), result.ToJsonString());
        Assert.False(result["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.Equal(bytes, Serialize(events));
    }

    private static List<JsonObject> Events()
    {
        var events = AmazonAaxWorkflowTests.SourceProofs();
        var records = Records(events); var offer = records[0]!.DeepClone().AsObject();
        offer["href"] = Deals; offer["selector"] = "#deal"; offer["heading"] = null;
        records.Insert(3, offer); Reindex(events); events.Insert(3, DealRead("own-deal"));
        return events;
    }
    private static JsonObject DealRead(string id)
    {
        var read = AmazonAaxSourceDiagnosticTests.Resolution(Deals, id); var result = read["result"]!;
        result["url"] = Deals; result["acquisition"]!["navigation"]!["url"] = Deals;
        result["navigationEvidence"]!["finalUrl"] = Deals;
        result["navigationEvidence"]!["hops"] = new JsonArray(new JsonObject { ["url"] = Deals, ["method"] = "GET", ["statusCode"] = 200, ["location"] = null });
        return read;
    }
    private static void Reindex(List<JsonObject> events)
    {
        var records = Records(events);
        for (var index = 0; index < records.Count; index++) records[index]!["reference"] = "search:record:" + index;
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
    }
    private static JsonArray Records(IReadOnlyList<JsonObject> events) => events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static JsonObject Analyze(IReadOnlyList<JsonObject> events) => LiveWorkflowOracles.AnalyzeAmazonDealsContextProposal(events, Query);
    private static string Serialize(IEnumerable<JsonObject> events) => new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString();
}
