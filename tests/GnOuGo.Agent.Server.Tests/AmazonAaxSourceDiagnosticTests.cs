using System.Text.Json.Nodes;
using GnOuGo.Browser.Mcp;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonAaxSourceDiagnosticTests
{
    private const string Query = "chaussure geox homme 45";
    internal const string First = "https://aax-eu-zaz.amazon.fr/x/c/opaque-image/https://www.amazon.fr/dp/B000000001?observed=1";
    internal const string Heading = "https://aax-eu-zaz.amazon.fr/x/c/opaque-heading/https://www.amazon.fr/dp/B000000001?observed=2";

    [Fact]
    public void SameQueryHistoryAddressKeepsHttpEvidenceAndLiteralEntriesWithoutNormalization()
    {
        var settings = Settings(); var guard = new AmazonAaxSourceDiagnostic(settings, AmazonAaxSourceDiagnostic.Approval); var source = Source();
        const string httpUrl = "https://www.amazon.fr/s?k=chaussure%20geox%20homme%2045";
        const string documentUrl = "https://www.amazon.fr/s?k=chaussure+geox+homme+45";
        source["arguments"]!["url"] = httpUrl;
        source["result"]!["navigationEvidence"]!["requestedUrl"] = httpUrl;
        source["result"]!["navigationEvidence"]!["finalUrl"] = httpUrl;
        source["result"]!["navigationEvidence"]!["hops"] = new JsonArray(new JsonObject
            { ["url"] = httpUrl, ["method"] = "GET", ["statusCode"] = 200, ["location"] = null });
        source["result"]!["url"] = documentUrl;
        source["result"]!["acquisition"]!["navigation"]!["url"] = documentUrl;
        source["result"]!["navigationEvidence"]!["documentUrl"] = documentUrl;
        var bytes = source.ToJsonString();
        guard.ObserveSearch(source, Query);
        Assert.Equal([First, Heading, Heading], guard.OriginalOccurrences.Select(r => r["href"]!.ToString()));
        Assert.Equal(bytes, source.ToJsonString());
        Assert.Equal(httpUrl, source["result"]!["navigationEvidence"]!["finalUrl"]!.ToString());
    }

    [Fact]
    public void MatchingDocumentQueryCannotReplaceTheActualHttpSearchQuery()
    {
        var settings = Settings(); var guard = new AmazonAaxSourceDiagnostic(settings, AmazonAaxSourceDiagnostic.Approval); var source = Source();
        var navigation = source["result"]!["navigationEvidence"]!; var original = navigation["finalUrl"]!.ToString();
        const string other = "https://www.amazon.fr/s?k=different";
        navigation["finalUrl"] = other; navigation["documentUrl"] = original;
        navigation["hops"]![0]!["statusCode"] = 302; navigation["hops"]![0]!["location"] = other;
        navigation["hops"]!.AsArray().Add(new JsonObject { ["url"] = other, ["method"] = "GET", ["statusCode"] = 200, ["location"] = null });
        Assert.Throws<InvalidOperationException>(() => guard.ObserveSearch(source, Query));
        Assert.Null(guard.Next);
    }

    [Fact]
    public void OwnOriginalOccurrencesPreserveOrderDuplicatesAndFragmentWithoutWorkflowAuthority()
    {
        var settings = Settings(); var guard = new AmazonAaxSourceDiagnostic(settings, AmazonAaxSourceDiagnostic.Approval);
        Assert.Throws<InvalidOperationException>(() => guard.RequireNext(First));
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(First, settings));
        var source = Source(); var bytes = source.ToJsonString(); guard.ObserveSearch(source, Query);
        Assert.Equal([First, Heading, Heading], guard.OriginalOccurrences.Select(r => r["href"]!.ToString()));
        Assert.Throws<InvalidOperationException>(() => guard.RequireNext(Heading));
        Assert.False(guard.RedirectAllowed(Heading + "&unobserved=1"));
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(Heading + "&unobserved=1", settings));
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(Heading + "#customerReviews", settings));
        for (var i = 0; i < 3; i++)
        {
            var href = guard.Next!; guard.RequireNext(href);
            Assert.Equal(href, BrowserNavigationPolicy.ValidateNavigationTarget(href, settings).AbsoluteUri);
            guard.ObserveResolution(Resolution(href, "proof" + i));
        }
        Assert.Null(guard.Next); Assert.Throws<InvalidOperationException>(() => guard.RequireNext(Heading));
        Assert.False(AmazonObservedRedirectPolicy.IsObserved("amazon-aax-source-only"));
        Assert.False(AmazonStoreListPolicy.Allows(Heading));
        Assert.Equal(bytes, source.ToJsonString());
        guard.Expire(); Assert.False(guard.RedirectAllowed(First));
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(First, settings));
    }

    [Theory]
    [InlineData("failed_read")][InlineData("truncated")][InlineData("wrong_query")]
    [InlineData("wrong_reference")][InlineData("inconsistent_context")][InlineData("missing_navigation")]
    public void IncompleteOrConflictingSearchCannotGrantSourceAuthority(string change)
    {
        var settings = Settings(); var guard = new AmazonAaxSourceDiagnostic(settings, AmazonAaxSourceDiagnostic.Approval); var source = Source();
        switch (change)
        {
            case "failed_read": source["error"] = true; break;
            case "truncated": source["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "wrong_reference": Records(source)[0]!["reference"] = "other:record:0"; break;
            case "inconsistent_context": Records(source)[^1]!["ancestorAttributes"]!["data-asin"]!["tag"] = "span"; break;
            case "missing_navigation": source["result"]!["navigationEvidence"] = null; break;
        }
        Assert.Throws<InvalidOperationException>(() => guard.ObserveSearch(source, change == "wrong_query" ? "different" : Query));
        Assert.Null(guard.Next); Assert.False(guard.RedirectAllowed(First));
    }

    [Theory]
    [InlineData("failure")][InlineData("missing_proof")][InlineData("unobserved_hop")]
    [InlineData("wrong_entry")][InlineData("reused_capture")][InlineData("new_search")]
    public void FailedReadsNewSearchesAndReusedProofExpireAuthority(string change)
    {
        var settings = Settings(); var guard = new AmazonAaxSourceDiagnostic(settings, AmazonAaxSourceDiagnostic.Approval); guard.ObserveSearch(Source(), Query);
        var read = Resolution(First, "proof");
        switch (change)
        {
            case "failure": read["error"] = true; break;
            case "missing_proof": read["result"]!["navigationEvidence"] = null; break;
            case "unobserved_hop": read["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = Heading + "&unobserved=1"; break;
            case "wrong_entry": read["arguments"]!["url"] = Heading; break;
            case "reused_capture": guard.ObserveResolution(read); read = Resolution(Heading, "proof"); break;
            case "new_search": guard.BeginSearch(Source()["arguments"]!["url"]!.ToString()); break;
        }
        Assert.Throws<InvalidOperationException>(() => guard.ObserveResolution(read));
        Assert.Null(guard.Next); Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(First, settings));
    }

    [Theory]
    [InlineData("http://aax-eu-zaz.amazon.fr/x/c/opaque")]
    [InlineData("https://aax-eu-zaz.amazon.fr:443/x/c/opaque")]
    [InlineData("https://user@aax-eu-zaz.amazon.fr/x/c/opaque")]
    [InlineData("https://aax-eu-zaz.amazon.fr.evil.example/x/c/opaque")]
    [InlineData("https://aax-eu-zaz.amazon.fr/x/other/opaque")]
    [InlineData("https://aax-eu-zaz.amazon.fr/x/c/opaque#customerReviews")]
    public void GrantDoesNotAuthorizeOtherLiteralRoutes(string value) => Assert.False(AmazonAaxSourceDiagnostic.IsEntry(value));

    [Fact]
    public void ExactApprovalAndOriginalProfileIntersectionRemainMandatory()
    {
        Assert.Throws<InvalidOperationException>(() => new AmazonAaxSourceDiagnostic(Settings(), AmazonStoreListPolicy.Approval));
        var settings = Settings(); settings.AllowedNavigationUrlPatterns.Add("https://saved-restriction.invalid/");
        Assert.Throws<InvalidOperationException>(() => new AmazonAaxSourceDiagnostic(settings, AmazonAaxSourceDiagnostic.Approval));
        Assert.False(AmazonAaxSourceDiagnostic.IsDestination("https://www.amazon.fr/sspa/click?opaque=1"));
        Assert.False(AmazonAaxSourceDiagnostic.IsDestination("https://sponsored-ads.amazon.fr/clk?opaque=1"));
        Assert.True(AmazonAaxSourceDiagnostic.IsDestination("https://www.amazon.fr/dp/B000000001"));
    }

    internal static BrowserServerSettings Settings() => new() { AllowedHosts = ["www.amazon.fr", "amazon.fr", "sponsored-ads.amazon.fr"],
        AllowedNavigationUrlPatterns = [AmazonStoreListPolicy.UrlPattern] };

    internal static JsonObject Source()
    {
        var source = AmazonStoreListWorkflowTests.Events()[0]; var records = Records(source);
        foreach (var (href, index) in new[] { First, Heading, Heading + "#customerReviews", Heading }.Select((href, i) => (href, i)))
            records.Add(new JsonObject { ["kind"] = "link", ["tag"] = "a", ["selector"] = "#aax-" + index, ["href"] = href,
                ["heading"] = index == 1 ? "h2" : null, ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject
                    { ["value"] = "", ["tag"] = "div", ["role"] = null, ["index"] = 901 } } });
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        source["result"]!["acquisition"] = new JsonObject { ["navigation"] = new JsonObject { ["url"] = source["result"]!["url"]!.DeepClone() } };
        return source;
    }

    private static JsonObject Resolution(string href, string snapshot)
    {
        var read = AmazonProductContextOracleTests.Events()[1]; var result = read["result"]!;
        read["arguments"]!["url"] = href; result["navigationEvidence"]!["requestedUrl"] = href;
        result["navigationEvidence"]!["hops"]![0]!["url"] = href;
        result["acquisition"] = new JsonObject { ["navigation"] = new JsonObject { ["url"] = result["url"]!.DeepClone() } };
        result["observationSnapshot"]!["id"] = snapshot;
        foreach (var page in result["observationSnapshot"]!["pages"]!.AsArray()) page!["id"] = snapshot;
        return read;
    }
    private static JsonArray Records(JsonObject source) => source["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
}
