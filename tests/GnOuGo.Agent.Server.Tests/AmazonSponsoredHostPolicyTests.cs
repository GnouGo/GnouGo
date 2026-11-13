using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonSponsoredHostPolicyTests
{
    internal const string Policy = "amazon-observed-sponsored-host-v2";
    internal const string Approval = "user-strict-amazon-sponsored-host-extension-20261109";
    internal const string Entry = "https://sponsored-ads.amazon.fr/clk/?opaque=observed&r=https%3A%2F%2Fwww.amazon.fr%2Fgp%2Faw%2Fd%2FB000000001";

    [Fact]
    public void ProposedObservedHostPolicyIsVersionedSeparatelyFromApprovedV1()
    {
        AmazonObservedRedirectPolicy.Validate(Policy, "amazon");
        Assert.False(AmazonObservedRedirectPolicy.Allows(Entry));
        Assert.False(AmazonObservedRedirectPolicy.IsEntry(Entry));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Validate(Policy, "code"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Allows(Entry, "unknown"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Facts("unknown"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.ApprovalFor("unknown"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Pattern("unknown"));
    }

    [Fact]
    public void CaptureRequiresSeparateExplicitGrantWithoutChangingTheOldDefault()
    {
        Assert.Null(AmazonObservedRedirectPolicy.Capture([], "amazon"));
        Assert.Equal(AmazonObservedRedirectPolicy.Id, AmazonObservedRedirectPolicy.Capture(["--amazon-observed-redirects"], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(["--amazon-observed-sponsored-host"], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(["--amazon-observed-sponsored-host", "--amazon-scope-approval", AmazonObservedRedirectPolicy.Approval], "amazon"));
        string[] args = ["--amazon-observed-sponsored-host", "--amazon-scope-approval", Approval];
        Assert.Equal(Policy, AmazonObservedRedirectPolicy.Capture(args, "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(args, "code"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture([..args, "--amazon-observed-redirects"], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(["--amazon-scope-approval", Approval], "amazon"));
    }

    [Theory]
    [InlineData(Entry, true)]
    [InlineData("https://www.amazon.fr/sspa/click?opaque=observed", true)]
    [InlineData("https://www.amazon.fr/gp/aw/d/B000000001", true)]
    [InlineData("http://sponsored-ads.amazon.fr/clk/?x=1", false)]
    [InlineData("https://user@sponsored-ads.amazon.fr/clk/?x=1", false)]
    [InlineData("https://sponsored-ads.amazon.fr:8443/clk/?x=1", false)]
    [InlineData("https://sponsored-ads.amazon.fr/clk/?x=1#fragment", false)]
    [InlineData("https://sponsored-ads.amazon.fr/clk/?", false)]
    [InlineData("https://sponsored-ads.amazon.fr/clk/", false)]
    [InlineData("https://sponsored-ads.amazon.fr/clk?x=1", false)]
    [InlineData("https://sponsored-ads.amazon.fr/other/?x=1", false)]
    [InlineData("https://sponsored-ads.amazon.fr.example.org/clk/?x=1", false)]
    [InlineData("https://other.amazon.fr/clk/?x=1", false)]
    public void OnlyTheExactAdditionalHostAndRouteAreEligible(string url, bool allowed)
        => Assert.Equal(allowed, AmazonObservedRedirectPolicy.Allows(url, Policy));

    [Fact]
    public void HostConfigurationIsCapturedWithoutMutatingSavedRestrictions()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var configured = new[] { "Browser", "Document" }.ToDictionary(name => "GnOuGo." + name + ".Mcp", _ => new McpServerOptions());
        var original = JsonSerializer.Serialize(configured);
        var old = LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, source, AmazonObservedRedirectPolicy.Id)["GnOuGo.Browser.Mcp"].EnvironmentVariables!;
        Assert.False(old.ContainsKey("Browser__AllowedHosts__2"));
        var current = LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, source, Policy)["GnOuGo.Browser.Mcp"].EnvironmentVariables!;
        Assert.Equal("sponsored-ads.amazon.fr", current["Browser__AllowedHosts__2"]);
        Assert.Equal(AmazonSponsoredHostPolicy.UrlPattern, current["Browser__AllowedNavigationUrlPatterns__0"]);
        Assert.Contains(AmazonSponsoredHostPolicy.UrlPattern, LiveWorkflowEvaluation.AmazonEvaluationPrompt("workflows/campaign/fresh", 3, Policy), StringComparison.Ordinal);
        Assert.Equal(original, JsonSerializer.Serialize(configured));
        configured["GnOuGo.Browser.Mcp"].EnvironmentVariables = new() { ["Browser__AllowedHosts__0"] = "stricter.example" };
        Assert.Throws<InvalidOperationException>(() => LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, source, Policy));
    }

    [Fact]
    public void V6RequiresTheActualObservedEntryAndRetainsThePreviousVerdicts()
    {
        var events = Events();
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.Equal("amazon-business-v6", Verify(events)["version"]!.ToString());
        Assert.False(LiveWorkflowOracles.VerifyAmazonObservedRedirectSelection(events, 2, Query)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonSelection(events, 2, Query)["passed"]!.GetValue<bool>());
        Assert.Equal([Entry], LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]], Policy));
        Assert.Empty(LiveWorkflowOracles.CompleteSearchRedirectEntries([events[0]]));
        Assert.True(LiveWorkflowOracles.VerifyAmazonObservedRedirectSelection(AmazonObservedRedirectOracleTests.Events(), 2, Query)["passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("missing_navigation")]
    [InlineData("missing_search_navigation")]
    [InlineData("external_hop")]
    [InlineData("forbidden_route")]
    [InlineData("post")]
    [InlineData("wrong_location")]
    [InlineData("loop")]
    [InlineData("invented_entry")]
    [InlineData("decoded_target")]
    [InlineData("wrong_order")]
    [InlineData("missing_occurrence")]
    [InlineData("truncated")]
    [InlineData("omitted_sponsored")]
    [InlineData("unknown_host_route")]
    public void IncompleteOrSubstitutedEvidenceCannotQualify(string change)
    {
        var events = Events();
        var navigation = events[1]["result"]!["navigationEvidence"]!;
        switch (change)
        {
            case "missing_navigation": events[1]["result"]!["navigationEvidence"] = null; break;
            case "missing_search_navigation": events[0]["result"]!["navigationEvidence"] = null; break;
            case "external_hop": navigation["hops"]![1]!["url"] = "https://example.org/dp/B000000001"; break;
            case "forbidden_route": navigation["hops"]![1]!["url"] = "https://sponsored-ads.amazon.fr/other/?x=1"; break;
            case "post": navigation["hops"]![0]!["method"] = "POST"; break;
            case "wrong_location": navigation["hops"]![0]!["location"] = "https://www.amazon.fr/dp/B000000002"; break;
            case "loop": navigation["hops"]![1]!["url"] = Entry; break;
            case "invented_entry": events[1]["arguments"]!["url"] = Entry + "&invented=1"; break;
            case "decoded_target": events[1]["arguments"]!["url"] = "https://www.amazon.fr/gp/aw/d/B000000001"; break;
            case "wrong_order": (events[1], events[2]) = (events[2], events[1]); break;
            case "missing_occurrence": Records(events)[0]!["occurrence"] = null; break;
            case "truncated": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "omitted_sponsored": events.RemoveAt(1); break;
            case "unknown_host_route": Records(events)[0]!["href"] = "https://sponsored-ads.amazon.fr/other/?x=1"; break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("amazon-business-v6", null, null)]
    [InlineData("amazon-business-v6", AmazonObservedRedirectPolicy.Id, AmazonObservedRedirectPolicy.Approval)]
    [InlineData("amazon-business-v6", Policy, AmazonObservedRedirectPolicy.Approval)]
    [InlineData("amazon-business-v7", Policy, Approval)]
    [InlineData("amazon-business-v7", AmazonProductContextPolicy.Id, AmazonObservedRedirectPolicy.Approval)]
    [InlineData("amazon-business-v6", AmazonProductContextPolicy.Id, Approval)]
    [InlineData("amazon-business-v5", Policy, Approval)]
    [InlineData("amazon-business-v5", AmazonObservedRedirectPolicy.Id, Approval)]
    public async Task VerifierRejectsMissingOrMismatchedCapturedAuthorityBeforeAnyTransport(string version, string? policy, string? approval)
    {
        var run = new JsonObject { ["workspace_relative"] = "workflows/test", ["execution"] = new JsonObject { ["success"] = true },
            ["events"] = new JsonArray(), ["oracle_version"] = version, ["browser_observation_policy"] = policy, ["browser_scope_approval"] = approval };
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowOracles.VerifyAsync("amazon", Path.GetTempPath(), run, null!));
    }

    private const string Query = "chaussure geox homme 45";
    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonSponsoredHostSelection(events, 2, Query);
    private static JsonArray Records(List<JsonObject> events) => events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    internal static List<JsonObject> Events() => AmazonObservedRedirectOracleTests.Events().Select(e =>
        JsonNode.Parse(e.ToJsonString().Replace("https://www.amazon.fr/sspa/click?opaque=observed", Entry, StringComparison.Ordinal))!.AsObject()).ToList();
}
