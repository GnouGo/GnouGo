using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonObservedRedirectOracleTests
{
    private const string Query = "chaussure geox homme 45";
    private const string Search = "https://www.amazon.fr/s?k=chaussure+geox+homme+45";
    private const string Sponsored = "https://www.amazon.fr/sspa/click?opaque=observed";
    private const string First = "https://www.amazon.fr/dp/B000000001";
    private const string Second = "https://www.amazon.fr/dp/B000000002";

    [Fact]
    public void NewVersionRequiresActualRedirectAndOccurrenceEvidenceWhileV4KeepsItsVerdict()
    {
        var events = Events();
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonSelection(events, 2, Query)["passed"]!.GetValue<bool>());
        Assert.Equal([Sponsored], LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]]));
    }

    [Theory]
    [InlineData("missing_navigation", "product_navigation_provenance_incomplete")]
    [InlineData("missing_search_navigation", "search_navigation_provenance_incomplete")]
    [InlineData("external_hop", "product_navigation_provenance_incomplete")]
    [InlineData("forbidden_route", "product_navigation_provenance_incomplete")]
    [InlineData("loop", "product_navigation_provenance_incomplete")]
    [InlineData("wrong_location", "product_navigation_provenance_incomplete")]
    [InlineData("post", "product_navigation_provenance_incomplete")]
    [InlineData("incomplete", "product_navigation_provenance_incomplete")]
    [InlineData("invented_entry", "first_product_identity_or_order_mismatch")]
    [InlineData("wrong_order", "first_product_identity_or_order_mismatch")]
    [InlineData("wrong_asin", "first_product_identity_or_order_mismatch")]
    [InlineData("missing_occurrence", "search_product_occurrences_ambiguous")]
    [InlineData("broad_group", "search_product_occurrences_ambiguous")]
    [InlineData("wrong_reference", "search_record_provenance_inconsistent")]
    [InlineData("inconsistent_context", "search_occurrence_context_inconsistent")]
    [InlineData("reused_index", "search_occurrence_order_inconsistent")]
    [InlineData("ambiguous_heading_alias", "search_product_aliases_unverified")]
    [InlineData("mixed_unresolved_heading_alias", "search_product_aliases_unverified")]
    [InlineData("wrong_query", "search_query_does_not_match_input")]
    [InlineData("truncated", "requested_search_not_observed")]
    [InlineData("narrow_selector", "search_observation_scope_not_independently_complete")]
    [InlineData("fallback", "search_observation_scope_not_independently_complete")]
    [InlineData("unknown_redirect", "search_product_redirect_route_unapproved")]
    [InlineData("omitted_sponsored", "first_product_visits_incomplete")]
    public void MissingOrInconsistentEvidenceCannotQualify(string change, string finding)
    {
        var events = Events(); var search = Records(events[0]);
        var navigation = events[1]["result"]!["navigationEvidence"]!;
        switch (change)
        {
            case "missing_navigation": events[1]["result"]!["navigationEvidence"] = null; break;
            case "missing_search_navigation": events[0]["result"]!["navigationEvidence"] = null; break;
            case "external_hop": navigation["hops"]![1]!["url"] = "https://example.org/dp/B000000001"; break;
            case "forbidden_route": navigation["hops"]![1]!["url"] = "https://www.amazon.fr/ap/signin"; break;
            case "loop": navigation["hops"]![1]!["url"] = Sponsored; break;
            case "wrong_location": navigation["hops"]![0]!["location"] = Second; break;
            case "post": navigation["hops"]![0]!["method"] = "POST"; break;
            case "incomplete": navigation["complete"] = false; break;
            case "invented_entry": events[1]["arguments"]!["url"] = Sponsored + "&invented=1"; break;
            case "wrong_order": (events[1], events[2]) = (events[2], events[1]); break;
            case "wrong_asin": events[2]["result"]!["url"] = First; break;
            case "missing_occurrence": search[0]!["occurrence"] = null; break;
            case "broad_group": search[0]!["occurrence"]!["role"] = "main"; break;
            case "wrong_reference": search[1]!["reference"] = "another:record:1"; break;
            case "inconsistent_context": search[1]!["occurrence"]!["tag"] = "article"; break;
            case "reused_index": search[2]!["occurrence"]!["index"] = 0; break;
            case "ambiguous_heading_alias": search[1]!["href"] = Sponsored + "&other=1"; break;
            case "mixed_unresolved_heading_alias": search[1]!["href"] = First; break;
            case "wrong_query": events[0]["arguments"]!["url"] = Search + "&k=other"; break;
            case "truncated": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "narrow_selector": events[0]["arguments"]!["selector"] = "#products"; break;
            case "fallback": events[0]["result"]!["fallbackApplied"] = true; break;
            case "unknown_redirect": search[0]!["href"] = "https://www.amazon.fr/slredirect?opaque=1"; break;
            case "omitted_sponsored": events.RemoveAt(1); break;
        }
        Assert.Contains(finding, Verify(events)["findings"]!.AsArray().Select(f => f!.ToString()));
    }

    [Fact]
    public void OuterOccurrenceKeepsVariantLinksAndRepeatedProductsWithoutDiscardingCards()
    {
        var events = Events();
        // A nested variant link is retained but the card's explicit heading identifies its primary link.
        var variant = Record("search", 0, Second, 0, false);
        Records(events[0]).Insert(0, variant); Reindex(events[0]);
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Records(events[0])[3]!["href"] = Sponsored;
        events[2] = Visit(Sponsored, First);
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        events.RemoveAt(2);
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void AssociatedReviewFragmentsRemainSourceLinksWithoutBecomingNavigableEntriesOrExtraResults()
    {
        var events = Events();
        var link = Record("search", 3, Sponsored + "#customerReviews", 0, false);
        Records(events[0]).Insert(2, link); Reindex(events[0]);
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.False(AmazonObservedRedirectPolicy.Allows(Sponsored + "#customerReviews"));
        Assert.Equal([Sponsored], LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]]));
    }

    [Fact]
    public void HistoryAddressIsDistinctFromTheObservedHttpResponse()
    {
        var events = Events(); var evidence = events[0]["result"]!["navigationEvidence"]!;
        var wire = Search.Replace("+", "%20", StringComparison.Ordinal);
        evidence["requestedUrl"] = wire; evidence["finalUrl"] = wire;
        evidence["hops"]![0]!["url"] = wire; evidence["documentUrl"] = Search;
        events[0]["arguments"]!["url"] = wire;
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
    }

    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonObservedRedirectSelection(events, 2, Query);
    internal static List<JsonObject> Events() => [Snapshot(Search, "search", [Record("search", 0, Sponsored, 0, true),
        Record("search", 1, Sponsored, 0, true), Record("search", 2, Second, 1, true)]), Visit(Sponsored, First), Visit(Second, Second)];

    private static JsonObject Record(string id, int at, string href, int occurrence, bool heading) => new()
    {
        ["reference"] = id + ":record:" + at, ["kind"] = "link", ["href"] = href, ["text"] = "Observed product",
        ["group"] = "#card-" + occurrence, ["groupContext"] = new JsonObject { ["tag"] = "div", ["role"] = "listitem", ["index"] = occurrence },
        ["occurrence"] = new JsonObject { ["selector"] = "#card-" + occurrence, ["tag"] = "div", ["role"] = "listitem", ["index"] = occurrence },
        ["heading"] = heading ? "#heading-" + occurrence : null
    };

    private static JsonObject Visit(string requested, string final)
    {
        var result = Snapshot(final, "product", [new JsonObject { ["reference"] = "product:record:0", ["text"] = "Observed fields" }]);
        result["arguments"]!["url"] = requested;
        result["result"]!["navigationEvidence"] = Navigation(requested, final);
        return result;
    }

    private static JsonObject Snapshot(string url, string id, JsonArray records) => new()
    {
        ["tool"] = "browser_get_content", ["error"] = false,
        ["arguments"] = new JsonObject { ["url"] = url, ["selector"] = "body", ["format"] = "observation_complete" },
        ["result"] = new JsonObject { ["success"] = true, ["url"] = url, ["truncated"] = false, ["selector"] = "body",
            ["resolvedSelector"] = "body", ["fallbackApplied"] = false, ["navigationEvidence"] = Navigation(url, url),
            ["observationSnapshot"] = new JsonObject { ["id"] = id, ["captureTruncated"] = false, ["manifestTruncated"] = false,
                ["recordCount"] = records.Count, ["pages"] = new JsonArray(new JsonObject { ["id"] = id, ["captureTruncated"] = false, ["records"] = records }) } }
    };

    private static JsonObject Navigation(string requested, string final)
    {
        var hops = new JsonArray();
        if (requested != final) hops.Add(new JsonObject { ["url"] = requested, ["method"] = "GET", ["statusCode"] = 302, ["location"] = final });
        hops.Add(new JsonObject { ["url"] = final, ["method"] = "GET", ["statusCode"] = 200 });
        return new() { ["requestedUrl"] = requested, ["finalUrl"] = final, ["complete"] = true, ["hops"] = hops };
    }

    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static void Reindex(JsonObject read)
    {
        var records = Records(read);
        for (var index = 0; index < records.Count; index++) records[index]!["reference"] = "search:record:" + index;
        read["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
    }
}
