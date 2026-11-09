using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonProductContextOracleTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void ExplicitNonProductAdvertisingContextDoesNotHideTheFirstSponsoredProduct()
    {
        var events = Events();
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.Equal("amazon-business-v7", Verify(events)["version"]!.ToString());
        Assert.False(LiveWorkflowOracles.VerifyAmazonSponsoredHostSelection(events, 2, Query)["passed"]!.GetValue<bool>());
        Assert.Equal([AmazonSponsoredHostPolicyTests.Entry], LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]], AmazonProductContextPolicy.Id));
        Assert.Empty(LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]], AmazonSponsoredHostPolicy.Id));
    }

    [Theory]
    [InlineData("missing_attribute")]
    [InlineData("unknown_attribute")]
    [InlineData("empty_product")]
    [InlineData("malformed_product")]
    [InlineData("contradictory_direct_link")]
    [InlineData("reused_context")]
    [InlineData("inconsistent_context")]
    [InlineData("reversed_context")]
    [InlineData("invented_link")]
    [InlineData("wrong_destination")]
    [InlineData("omitted_sponsored")]
    [InlineData("wrong_order")]
    [InlineData("missing_navigation")]
    [InlineData("post")]
    [InlineData("external_hop")]
    [InlineData("truncated")]
    [InlineData("narrow_source")]
    [InlineData("bad_reference")]
    [InlineData("unclassified_advertising")]
    public void MissingContradictoryOrSubstitutedEvidenceCannotQualify(string change)
    {
        var events = Events(); var records = Records(events); var first = records[1]!;
        switch (change)
        {
            case "missing_attribute": first["ancestorAttributes"] = null; break;
            case "unknown_attribute": first["ancestorAttributes"]!["data-asin"] = null; break;
            case "empty_product": first["ancestorAttributes"]!["data-asin"]!["value"] = ""; break;
            case "malformed_product": first["ancestorAttributes"]!["data-asin"]!["value"] = "short"; break;
            case "contradictory_direct_link": records[3]!["ancestorAttributes"]!["data-asin"]!["value"] = "B000000003"; break;
            case "reused_context": records[3]!["ancestorAttributes"]!["data-asin"]!["index"] = 1; break;
            case "inconsistent_context": records[2]!["ancestorAttributes"]!["data-asin"]!["tag"] = "section"; break;
            case "reversed_context": records[3]!["ancestorAttributes"]!["data-asin"]!["index"] = 0; break;
            case "invented_link": events[1]["arguments"]!["url"] = AmazonSponsoredHostPolicyTests.Entry + "&invented=1"; break;
            case "wrong_destination": events[1]["result"]!["url"] = "https://www.amazon.fr/dp/B000000003"; break;
            case "omitted_sponsored": events.RemoveAt(1); break;
            case "wrong_order": (events[1], events[2]) = (events[2], events[1]); break;
            case "missing_navigation": events[1]["result"]!["navigationEvidence"] = null; break;
            case "post": events[1]["result"]!["navigationEvidence"]!["hops"]![0]!["method"] = "POST"; break;
            case "external_hop": events[1]["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = "https://example.org/dp/B000000001"; break;
            case "truncated": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "narrow_source": events[0]["arguments"]!["selector"] = "#products"; break;
            case "bad_reference": first["reference"] = "invented:record:1"; break;
            case "unclassified_advertising": records[0]!["ancestorAttributes"] = null; break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void SourceAncestorIdentityProvidesAnOccurrenceWithoutManufacturingAHeading()
    {
        var events = Events();
        foreach (var record in Records(events))
        {
            record!["heading"] = null;
            if (record["ancestorAttributes"]!["data-asin"]!["value"]!.ToString().Length > 0) record["occurrence"] = null;
        }
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.True(LiveWorkflowOracles.VerifyAmazonSponsoredHostSelection(AmazonSponsoredHostPolicyTests.Events(), 2, Query)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void NewCaptureUsesTheSameGrantAndRoutesWithExplicitProductContextOptIn()
    {
        string[] args = ["--amazon-observed-sponsored-host", "--amazon-scope-approval", AmazonSponsoredHostPolicy.Approval, "--amazon-product-context"];
        Assert.Equal(AmazonProductContextPolicy.Id, AmazonObservedRedirectPolicy.Capture(args, "amazon"));
        Assert.Equal(AmazonSponsoredHostPolicy.Approval, AmazonObservedRedirectPolicy.ApprovalFor(AmazonProductContextPolicy.Id));
        Assert.Equal(AmazonSponsoredHostPolicy.UrlPattern, AmazonObservedRedirectPolicy.Pattern(AmazonProductContextPolicy.Id));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(["--amazon-product-context"], "amazon"));
    }

    [Fact]
    public void SecondaryVariantLinksDoNotReplaceTheObservedHeadingProduct()
    {
        var events = Events(); var records = Records(events);
        var variant = records[1]!.DeepClone();
        variant["href"] = "https://www.amazon.fr/dp/B000000003"; variant["heading"] = null;
        records.Insert(1, variant);
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        foreach (var record in records.Skip(1).Take(3)) record!["heading"] = null;
        Assert.False(Verify(events)["passed"]!.GetValue<bool>()); // Without the primary association, the differing identities stay unverified.
    }

    [Fact]
    public void RepeatedProductIdentifiersRemainSeparateOriginalOccurrences()
    {
        var events = Events().Select(e => JsonNode.Parse(e.ToJsonString().Replace("B000000002", "B000000001", StringComparison.Ordinal))!.AsObject()).ToList();
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.Equal(2, Verify(events)["expected_product_count"]!.GetValue<int>());
        events.RemoveAt(2);
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void ProductContextHostConfigurationPreservesSavedAttributeRequests()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var settings = new[] { "Browser", "Document" }.ToDictionary(name => "GnOuGo." + name + ".Mcp", _ => new GnOuGo.AI.Core.McpServerOptions());
        settings["GnOuGo.Browser.Mcp"].EnvironmentVariables = new() { ["Browser__ObservationAncestorAttributes__0"] = "data-code" };
        var configured = LiveWorkflowEvaluation.Configuration(settings, "amazon", null, build, root, AmazonProductContextPolicy.Id)["GnOuGo.Browser.Mcp"].EnvironmentVariables!;
        Assert.Equal("data-code", configured["Browser__ObservationAncestorAttributes__0"]);
        Assert.Equal("data-asin", configured["Browser__ObservationAncestorAttributes__1"]);
        Assert.Equal("sponsored-ads.amazon.fr", configured["Browser__AllowedHosts__2"]);
        Assert.False(settings["GnOuGo.Browser.Mcp"].EnvironmentVariables!.ContainsKey("Browser__ObservationAncestorAttributes__1"));
    }

    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonProductContextSelection(events, 2, Query);
    internal static List<JsonObject> Events()
    {
        var events = AmazonSponsoredHostPolicyTests.Events(); var records = Records(events);
        for (var i = 0; i < records.Count; i++) records[i]!["ancestorAttributes"] = Context(i < 2 ? "B000000001" : "B000000002", i < 2 ? 1 : 2);
        records.Insert(0, new JsonObject { ["kind"] = "link", ["href"] = "https://sponsored-ads.amazon.fr/clk/?opaque=brand-navigation",
            ["text"] = "About this brand", ["ancestorAttributes"] = Context("", 0) });
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        return events;
    }
    private static JsonObject Context(string value, int index) => new() { ["data-asin"] = new JsonObject
        { ["value"] = value, ["tag"] = "div", ["role"] = null, ["index"] = index } };
    private static JsonArray Records(List<JsonObject> events) => events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
}
