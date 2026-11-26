using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonFirstProductsTests
{
    private const string Query = "chaussure geox homme 45";
    private const string First = "B000000001";
    private const string Second = "B000000002";
    private static string Product(string asin) => "https://www.amazon.fr/dp/" + asin;
    private static JsonObject Verify(List<JsonObject> events, int count) => LiveWorkflowOracles.VerifyAmazonFirstProducts(events, count, Query);
    private static JsonArray Records(JsonObject source) => source["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();

    [Fact]
    public void DetailTextProfileAddsMissingPrefixesAndKeepsExistingConfiguration()
    {
        const string key = "Browser__ObservationTextSelectorPrefixes__";
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            { [key + "0"] = "#user-selector", [key + "4"] = "#productDescription", ["Unrelated"] = "keep" };
        AmazonFirstProductsPolicy.ConfigureDetailText(environment);
        var captured = environment.OrderBy(pair => pair.Key).ToArray();
        AmazonFirstProductsPolicy.ConfigureDetailText(environment);
        Assert.Equal(captured, environment.OrderBy(pair => pair.Key));
        Assert.Equal("#user-selector", environment[key + "0"]);
        Assert.Equal("#productDescription", environment[key + "4"]);
        Assert.Equal("keep", environment["Unrelated"]);
        Assert.Contains("#title", environment.Values);
        Assert.Contains("#apex-pricetopay", environment.Values);
        Assert.Contains("observationSnapshot.textMatches.records", AmazonFirstProductsPolicy.PlanningFacts, StringComparison.Ordinal);
    }

    private static JsonObject Link(string href, string asin, int ordinal, bool heading = false)
        => new() { ["kind"] = "link", ["tag"] = "a", ["selector"] = "#link-" + ordinal + (heading ? "-title" : "-aux"),
            ["href"] = href, ["heading"] = heading ? "h2" : null,
            ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject { ["index"] = ordinal, ["tag"] = "div", ["value"] = asin } },
            ["occurrence"] = new JsonObject { ["index"] = ordinal, ["selector"] = "#card-" + ordinal, ["tag"] = "li" } };

    private static JsonObject Source(params JsonObject[] links)
    {
        var source = AmazonDealsContextProposalTests.Events()[0];
        var records = Records(source);
        records.Clear();
        foreach (var link in links)
        {
            link["reference"] = "search:record:" + records.Count;
            records.Add(link);
        }
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        return source;
    }

    [Fact]
    public void SponsoredReviewAndOfferBelongToTheirOwnCardWithoutNavigation()
    {
        var review = AmazonAaxSourceDiagnosticTests.Heading + "&other=observed#customerReviews";
        var source = Source(Link(AmazonAaxSourceDiagnosticTests.Heading, First, 1), Link(review, First, 1),
            Link(AmazonDealsPolicy.Address, First, 1), Link(Product(Second), Second, 2, true));
        var before = source.ToJsonString();
        var report = Verify([source], 2);
        Assert.True(report["source_prerequisite_qualified"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Equal(new[] { First, Second }, report["selected_identities"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal(new[] { "auxiliary_review", "auxiliary_promotion" }, report["source_card_membership"]!.AsArray().Select(x => x!["classification"]!.ToString()));
        Assert.All(report["source_card_membership"]!.AsArray(), x => {
            Assert.Equal(First, x!["product_identity"]!.ToString());
            Assert.Equal("search:record:0", Assert.Single(x["proof_source_records"]!.AsArray())!.ToString());
            Assert.False(x["http_call_created"]!.GetValue<bool>());
            Assert.Empty(x["proof_event_indices"]!.AsArray());
        });
        Assert.Empty(report["source_resolution_evidence"]!.AsArray());
        Assert.False(report["passed"]!.GetValue<bool>());
        Assert.Equal(before, source.ToJsonString());
    }

    [Fact]
    public void PrefixIncludesInterveningProductsAndRejectsOutOfOrderVisits()
    {
        var source = Source(Link(Product(Second), Second, 1, true), Link(Product(First), First, 2, true),
            Link("https://www.amazon.fr/unresolved", "", 3, true));
        var report = Verify([source], 2);
        Assert.True(report["source_prerequisite_qualified"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Equal(new[] { Second, First }, report["selected_identities"]!.AsArray().Select(x => x!.ToString()));
        Assert.False(Verify([source], 3)["source_prerequisite_qualified"]!.GetValue<bool>());
        var wrongVisit = AmazonAaxSourceDiagnosticTests.Resolution(Product(First), "wrong-order");
        Assert.Contains("first_product_identity_or_order_mismatch", Verify([source, wrongVisit], 2)["findings"]!.AsArray().Select(x => x!.ToString()));
    }

    [Theory]
    [InlineData("different_card")][InlineData("conflicting_heading")][InlineData("closed")][InlineData("new_search")]
    public void CannotBorrowIdentityOrExpiredAuthority(string change)
    {
        var source = Source(Link(Product(change == "conflicting_heading" ? Second : First), First, 1, true),
            Link(AmazonAaxSourceDiagnosticTests.Heading + "#customerReviews", First, change == "different_card" ? 2 : 1),
            Link(Product(Second), Second, 3, true));
        List<JsonObject> events = [source];
        if (change == "closed") events.Add(new JsonObject { ["tool"] = "browser_close" });
        if (change == "new_search") events.Add(source.DeepClone().AsObject());
        if (change == "closed") events.Add(AmazonAaxSourceDiagnosticTests.Resolution(Product(First), "expired"));
        var report = Verify(events, 2);
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.Equal(0, report["visited_product_count"]!.GetValue<int>());
        Assert.Empty(LiveWorkflowOracles.ResolvedSourceAllowedEntries(events, AmazonFirstProductsPolicy.Id));
    }

    [Theory]
    [InlineData("/blackfriday", true)][InlineData("/blackfriday?unseen=1", false)][InlineData("/other", false)]
    public void NecessaryListingRequiresAnOwnedChainToTheExactDestination(string path, bool allowed)
    {
        var source = Source(Link(AmazonDealsPolicy.Address, "", 1, true), Link(Product(First), First, 2, true));
        var read = AmazonDealsContextProposalTests.DealRead("owned-listing");
        var destination = "https://www.amazon.fr" + path;
        var result = read["result"]!;
        result["url"] = destination;
        result["acquisition"]!["navigation"]!["url"] = destination;
        result["navigationEvidence"]!["finalUrl"] = destination;
        var hops = result["navigationEvidence"]!["hops"]!.AsArray();
        hops[0]!["location"] = path;
        hops[0]!["statusCode"] = 302;
        hops.Add(new JsonObject { ["method"] = "GET", ["url"] = destination, ["statusCode"] = 200 });
        Assert.False(Verify([source], 1)["source_prerequisite_qualified"]!.GetValue<bool>());
        var report = Verify([source, read], 1);
        Assert.True(allowed == report["source_prerequisite_qualified"]!.GetValue<bool>(), report.ToJsonString());
    }

    [Fact]
    public void OnlyNestedVariationLinksCanDifferFromTheKnownHeadingIdentity()
    {
        var variant = Link(Product(Second), First, 1);
        variant["role"] = "link";
        variant["text"] = "Brown";
        variant["groupContext"] = new JsonObject { ["role"] = "listitem", ["index"] = 7 };
        variant["group"] = "#card-1 > div";
        var source = Source(variant, Link(Product(First), First, 1, true));
        Assert.True(Verify([source], 1)["source_prerequisite_qualified"]!.GetValue<bool>());
        // Browser may identify the nested group with a document-unique relative selector.
        variant["group"] = "div:nth-of-type(3) > div:nth-of-type(1)";
        Assert.True(Verify([source], 1)["source_prerequisite_qualified"]!.GetValue<bool>());
        variant["group"] = "#different-card > div";
        Assert.False(Verify([source], 1)["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("https://www.amazon.fr/blackfriday", true)]
    [InlineData("https://www.amazon.fr/blackfriday?unseen=1", false)]
    [InlineData("https://www.amazon.fr/unlisted", false)]
    [InlineData("https://example.org/blackfriday", false)]
    public void BrowserChecksTheSeasonalDestinationBeforeContact(string url, bool permitted)
    {
        var settings = new GnOuGo.Browser.Mcp.BrowserServerSettings { AllowedNavigationUrlPatterns = [AmazonFirstProductsPolicy.UrlPattern] };
        if (permitted) GnOuGo.Browser.Mcp.BrowserNavigationPolicy.ValidateNavigationTarget(url, settings);
        else Assert.Throws<InvalidOperationException>(() => GnOuGo.Browser.Mcp.BrowserNavigationPolicy.ValidateNavigationTarget(url, settings));
    }

    [Fact]
    public void ResolvedEmptyAsinUsesTheObservedReadsForMembership()
    {
        var events = AmazonDealsContextProposalTests.Events();
        events.RemoveAt(3); // The observed promotion is auxiliary once the card's product is proved.
        var report = Verify(events, 1);
        Assert.True(report["source_prerequisite_qualified"]!.GetValue<bool>(), report.ToJsonString());
        Assert.All(report["source_card_membership"]!.AsArray(), e =>
        {
            Assert.Equal("resolved_source_card_membership", e!["proof_kind"]!.ToString());
            Assert.Equal(3, e["proof_event_indices"]!.AsArray().Count);
        });
    }
}
