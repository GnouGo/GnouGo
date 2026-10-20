using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonBusinessOracleTests
{
    private const string Query = "chaussure geox homme 45";
    private const string Search = "https://www.amazon.fr/s?k=chaussure+geox+homme+45";

    [Fact]
    public void OriginalSearchDefinesFirstProductsBeforeWorkbookOrVisits()
    {
        var events = Events();
        AssertPass(events);
        (events[1], events[2]) = (events[2], events[1]);
        AssertFinding(events, "first_product_identity_or_order_mismatch");
    }

    [Theory]
    [InlineData("wrong_query", "search_query_does_not_match_input")]
    [InlineData("duplicate_query", "search_query_does_not_match_input")]
    [InlineData("changed_search_result", "search_observation_not_independently_complete")]
    [InlineData("omitted", "first_product_visits_incomplete")]
    [InlineData("duplicated_visit", "first_product_identity_or_order_mismatch")]
    [InlineData("later_product", "first_product_identity_or_order_mismatch")]
    [InlineData("invented_url", "first_product_identity_or_order_mismatch")]
    [InlineData("different_product_redirect", "first_product_identity_or_order_mismatch")]
    [InlineData("non_product_visit", "first_product_identity_or_order_mismatch")]
    [InlineData("truncated_search", "requested_search_not_observed")]
    [InlineData("opaque_sponsored", "search_product_redirect_identity_unverified")]
    [InlineData("malformed_product", "search_product_identity_unverified")]
    public void RejectsUnsupportedSelectionWithoutUsingGeneratedRows(string variant, string finding)
    {
        var events = Events();
        switch (variant)
        {
            case "wrong_query": events[0]["arguments"]!["url"] = Search.Replace("geox", "other", StringComparison.Ordinal); break;
            case "duplicate_query": events[0]["arguments"]!["url"] = Search + "&k=another"; break;
            case "changed_search_result": events[0]["result"]!["url"] = Search.Replace("geox", "other", StringComparison.Ordinal); break;
            case "omitted": events.RemoveAt(2); break;
            case "duplicated_visit": events[2] = (JsonObject)events[1].DeepClone(); break;
            case "later_product": events[2] = Product(3); break;
            case "invented_url": events[1]["arguments"]!["url"] = Url(1) + "?invented=1"; break;
            case "different_product_redirect": events[1]["result"]!["url"] = Url(4); break;
            case "non_product_visit": events[1] = Snapshot("https://www.amazon.fr/help", [new JsonObject { ["text"] = "Help" }]); break;
            case "truncated_search": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "opaque_sponsored":
                Records(events[0]).Insert(0, new JsonObject { ["href"] = "https://www.amazon.fr/sspa/click?opaque=123", ["text"] = "Sponsored" });
                Count(events[0]); break;
            case "malformed_product":
                Records(events[0]).Insert(0, new JsonObject { ["href"] = "https://www.amazon.fr/dp/unknown", ["text"] = "Unresolved product" });
                Count(events[0]); break;
        }
        AssertFinding(events, finding);
    }

    [Fact]
    public void ExactUrlAliasesInOneObservedProductGroupDoNotCreateExtraProducts()
    {
        var events = Events();
        Records(events[0]).Insert(1, new JsonObject { ["href"] = Url(1) + "?ref=image", ["group"] = "main > article:nth-of-type(1)", ["text"] = "" });
        Count(events[0]);
        events[1]["arguments"]!["url"] = Url(1) + "?ref=image";
        AssertPass(events);
    }

    [Fact]
    public void SeparateObservedProductOccurrencesPreserveRepeatedIdentities()
    {
        var events = Events();
        Records(events[0])[1]!["href"] = Url(1);
        events[2] = Product(1);
        AssertPass(events);
        events.RemoveAt(2);
        AssertFinding(events, "first_product_visits_incomplete");
    }

    [Fact]
    public void BroadDuplicateAnchorGroupingCannotSilentlyDeduplicateProducts()
    {
        var events = Events();
        foreach (var record in Records(events[0])) record!["group"] = "#search";
        Records(events[0]).Insert(1, new JsonObject { ["href"] = Url(1), ["group"] = "#search", ["text"] = "Repeated anchor" });
        Count(events[0]);
        AssertFinding(events, "search_product_occurrences_ambiguous");
    }

    [Fact]
    public void AnotherGroupDoesNotMakeRepeatedIdentitiesInOneBroadGroupUnambiguous()
    {
        var events = Events();
        Records(events[0])[0]!["group"] = "main";
        Records(events[0]).Insert(1, new JsonObject { ["href"] = Url(1), ["group"] = "main", ["text"] = "Another occurrence" });
        Count(events[0]);
        AssertFinding(events, "search_product_occurrences_ambiguous");
    }

    [Fact]
    public void FewerCompleteObservedProductsCanBeVerifiedWithoutInventingRows()
    {
        var events = Events();
        Records(events[0]).RemoveAt(3); Records(events[0]).RemoveAt(2); Records(events[0]).RemoveAt(1);
        Count(events[0]); events.RemoveAt(2);
        var result = LiveWorkflowOracles.VerifyAmazonSelection(events, 3, Query);
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(1, result["expected_product_count"]!.GetValue<int>());
    }

    [Fact]
    public void PreSearchLandingReadIsNotMisclassifiedAsAProductVisit()
    {
        var events = Events();
        events.Insert(0, Snapshot("https://www.amazon.fr/", [new JsonObject { ["text"] = "Search" }]));
        AssertPass(events);
        events[0] = Product(1);
        AssertFinding(events, "product_visit_before_search");
    }

    [Fact]
    public void HistoricalObservedProductOracleRetainsItsCapturedBehavior()
    {
        var events = ObservedProductOracleTests.Events("https://fixture.invalid/item", "https://fixture.invalid/item");
        Assert.True(LiveWorkflowOracles.VerifyObservedProducts(events, ObservedProductOracleTests.Rows(), 10)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonSelection(events, 10, Query)["passed"]!.GetValue<bool>());
    }

    private static List<JsonObject> Events() =>
    [
        Snapshot(Search, Enumerable.Range(1, 4).Select(i => new JsonObject
        { ["href"] = Url(i), ["text"] = "Product " + i, ["group"] = "main > article:nth-of-type(" + i + ")" }).ToArray()),
        Product(1), Product(2)
    ];
    private static JsonObject Product(int index) => Snapshot(Url(index), [new JsonObject { ["text"] = "Product " + index }]);
    private static string Url(int index) => "https://www.amazon.fr/dp/B00000000" + index;
    private static JsonObject Snapshot(string url, JsonObject[] records) => new()
    {
        ["tool"] = "browser_get_content", ["error"] = false, ["arguments"] = new JsonObject { ["url"] = url },
        ["result"] = new JsonObject { ["url"] = url, ["success"] = true, ["truncated"] = false,
            ["observationSnapshot"] = new JsonObject { ["id"] = "snapshot-" + url, ["captureTruncated"] = false, ["manifestTruncated"] = false,
                ["recordCount"] = records.Length, ["pages"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "snapshot-" + url,
                    ["captureTruncated"] = false, ["nextCursor"] = null, ["records"] = new JsonArray(records.Cast<JsonNode>().ToArray()) }) } }
    };
    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static void Count(JsonObject read) => read["result"]!["observationSnapshot"]!["recordCount"] = Records(read).Count;
    private static void AssertPass(List<JsonObject> events)
    {
        var result = LiveWorkflowOracles.VerifyAmazonSelection(events, 2, Query);
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
    }
    private static void AssertFinding(List<JsonObject> events, string expected)
    {
        var result = LiveWorkflowOracles.VerifyAmazonSelection(events, 2, Query);
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Contains(expected, result["findings"]!.AsArray().Select(f => f!.ToString()));
    }
}
