using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonAaxWorkflowTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void EmptyAsinProductCardRetainsItsUnprovedDealsNavigationAsABlocker()
    {
        var events = JsonNode.Parse(JsonSerializerBytes(WorkflowEvents()).Replace(
            "https://aax-eu-zaz.amazon.fr/x/c/product-", "https://sponsored-ads.amazon.fr/clk?opaque=product-", StringComparison.Ordinal))!
            .AsArray().OfType<JsonObject>().ToList();
        var records = Records(events[0]);
        var entry = records[17]!.AsObject();
        var review = entry.DeepClone().AsObject();
        review["tag"] = "a"; review["selector"] = "#review-link"; review["heading"] = null;
        review["href"] = entry["href"]!.ToString() + "#customerReviews";
        review["reference"] = "search:record:" + records.Count;
        records.Add(review);
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        Assert.True(Verify(events)["source_prerequisite_qualified"]!.GetValue<bool>());

        // Sanitized source06 relationship: three own product proofs and an
        // exact review alias share one empty-ASIN card with an extra offer link.
        const string href = "https://www.amazon.fr/deals";
        var offer = entry.DeepClone().AsObject();
        offer["tag"] = "a"; offer["selector"] = "#offer-link"; offer["heading"] = null;
        offer["href"] = href; offer["reference"] = "search:record:" + records.Count;
        records.Add(offer);
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        var raw = JsonSerializerBytes(events);
        var result = Verify(events);
        Assert.False(AmazonAaxProductPolicy.Allows(href));
        Assert.False(result["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.Contains("source_product_resolution_context_unverified", result["findings"]!.AsArray().Select(f => f!.ToString()));
        Assert.Equal(3, result["source_product_reads"]!.GetValue<int>());
        Assert.Contains(result["source_proof_aliases"]!.AsArray(), row => row?["source_record"]?.ToString() == review["reference"]!.ToString());
        Assert.Empty(LiveWorkflowOracles.ResolvedSourceAllowedEntries(events, AmazonAaxProductPolicy.Id));
        var cold = JsonNode.Parse(raw)!.AsArray().OfType<JsonObject>().ToList();
        Assert.True(JsonNode.DeepEquals(result, Verify(cold)));
        Assert.Equal(raw, JsonSerializerBytes(events));
    }

    [Fact]
    public void ExactOptInCapturesOnlyTrustedObservedScopeAndPreservesSavedSettings()
    {
        string[] args = ["--amazon-aax-product-context", "--amazon-scope-approval", AmazonAaxProductPolicy.Approval];
        Assert.Equal(AmazonAaxProductPolicy.Id, AmazonObservedRedirectPolicy.Capture(args, "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(args, "code"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(["--amazon-aax-product-context", "--amazon-scope-approval", AmazonAaxSourceDiagnostic.Approval], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture([.. args, "--amazon-source-coverage"], "amazon"));
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var configured = new[] { "Browser", "Document" }.ToDictionary(name => "GnOuGo." + name + ".Mcp", _ => new McpServerOptions());
        var before = JsonSerializer.Serialize(configured);
        var profile = LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, root, AmazonAaxProductPolicy.Id)["GnOuGo.Browser.Mcp"].EnvironmentVariables!;
        Assert.Equal(AmazonSourceCoveragePolicy.UrlPattern, profile["Browser__AllowedNavigationUrlPatterns__0"]);
        Assert.Equal(AmazonAaxProductPolicy.SourcePattern, profile["Browser__ObservedNavigationSourceUrlPatterns__0"]);
        Assert.Equal(AmazonAaxProductPolicy.EntryPattern, profile["Browser__ObservedNavigationEntryUrlPatterns__0"]);
        Assert.DoesNotContain(profile.Values, value => value == AmazonAaxSourceDiagnostic.Host);
        Assert.Equal(before, JsonSerializer.Serialize(configured));
        var old = LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, root, AmazonSourceCoveragePolicy.Id)["GnOuGo.Browser.Mcp"].EnvironmentVariables!;
        Assert.DoesNotContain(old.Keys, key => key.StartsWith("Browser__ObservedNavigation", StringComparison.Ordinal));
        foreach (var key in new[] { "Browser__ObservedNavigationSourceUrlPatterns__0", "Browser__ObservedNavigationEntryUrlPatterns__0" })
        {
            configured["GnOuGo.Browser.Mcp"].EnvironmentVariables = new() { [key] = "stricter" };
            Assert.Throws<InvalidOperationException>(() => LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, root, AmazonAaxProductPolicy.Id));
        }
    }

    [Fact]
    public void LegacyAndAaxSourceReadsKeepTheirCombinedOrderBeforeEveryBusinessVisit()
    {
        var events = WorkflowEvents();
        for (var i = 1; i <= 20; i++) Assert.Equal([events[i]["arguments"]!["url"]!.ToString()],
            LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(i).ToArray(), AmazonAaxProductPolicy.Id));
        Assert.True(Verify(events)["passed"]!.GetValue<bool>(), Verify(events).ToJsonString());
        (events[17], events[18]) = (events[18], events[17]);
        Assert.False(Verify(events)["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("none", true)][InlineData("header_only", false)][InlineData("missing_header", false)]
    [InlineData("swapped_rows", false)][InlineData("invented_field", false)][InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)][InlineData("not_closed", false)][InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)][InlineData("source_only", false)][InlineData("early_fields", false)]
    public Task FullBusinessOracleStillRequiresAllFieldsRowsExecutionAndCleanup(string change, bool passed)
        => AmazonResolvedStoreWorkflowTests.CheckBusinessAsync(change, passed, productResolution: true, aax: true);

    [Fact]
    public void EveryOwnProofQualifiesSourceButCannotReplaceBusinessVisits()
    {
        var events = SourceProofs();
        var bytes = JsonSerializerBytes(events);
        var source = Verify(events);
        Assert.True(source["source_prerequisite_qualified"]!.GetValue<bool>(), source.ToJsonString());
        Assert.False(source["passed"]!.GetValue<bool>());
        Assert.Equal(3, source["source_product_reads"]!.GetValue<int>());
        Assert.Single(source["source_proof_aliases"]!.AsArray());
        events.Add(Visit());
        var report = Verify(events);
        Assert.True(report["passed"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Equal(LiveWorkflowOracles.AmazonAaxProductVersion, report["version"]!.ToString());
        Assert.False(LiveWorkflowOracles.VerifyAmazonSourceCoverageSelection(events, 3, Query)["passed"]!.GetValue<bool>());
        Assert.Equal(bytes, JsonSerializerBytes(events.Take(4)));
        var cold = JsonNode.Parse(JsonSerializerBytes(events))!.AsArray().OfType<JsonObject>().ToArray();
        Assert.True(JsonNode.DeepEquals(report, Verify(cold)));
    }

    [Fact]
    public void NextEntryAuthorityRetainsOrderAndRepetitions()
    {
        var events = SourceProofs();
        Assert.Empty(LiveWorkflowOracles.ResolvedSourceAllowedEntries([], AmazonAaxProductPolicy.Id));
        for (var count = 1; count < events.Count; count++)
            Assert.Equal([events[count]["arguments"]!["url"]!.ToString()], LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(count).ToArray(), AmazonAaxProductPolicy.Id));
        Assert.Equal([AmazonAaxSourceDiagnosticTests.Heading], LiveWorkflowOracles.ResolvedSourceAllowedEntries(events, AmazonAaxProductPolicy.Id));
    }

    [Theory]
    [InlineData("missing_repeat")][InlineData("reused_capture")][InlineData("wrong_order")]
    [InlineData("unobserved_redirect")][InlineData("failed_read")][InlineData("truncated")]
    [InlineData("conflicting_identity")][InlineData("unproved_review")][InlineData("extra_heading")]
    [InlineData("wrong_query")][InlineData("wrong_source_reference")][InlineData("closed")]
    [InlineData("new_search")][InlineData("early_visit")][InlineData("reused_visit")][InlineData("normalized_redirect")]
    public void MissingOrConflictingSourceOrBusinessEvidenceFails(string change)
    {
        var events = SourceProofs(); events.Add(Visit());
        switch (change)
        {
            case "missing_repeat": events.RemoveAt(3); break;
            case "reused_capture": events[3] = events[2].DeepClone().AsObject(); break;
            case "wrong_order": (events[1], events[2]) = (events[2], events[1]); break;
            case "unobserved_redirect": events[1]["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = AmazonAaxSourceDiagnosticTests.Heading + "&unobserved=1"; break;
            case "normalized_redirect":
                var navigation = events[1]["result"]!["navigationEvidence"]!;
                navigation["hops"]![0]!["location"] = "https://aax-eu-zaz.amazon.fr/x/c/unseen/../opaque-heading/https://www.amazon.fr/dp/B000000001?observed=2";
                navigation["hops"]!.AsArray().Insert(1, new JsonObject { ["url"] = AmazonAaxSourceDiagnosticTests.Heading, ["method"] = "GET", ["statusCode"] = 302,
                    ["location"] = navigation["finalUrl"]!.DeepClone() });
                break;
            case "failed_read": events[1]["error"] = true; break;
            case "truncated": events[1]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "conflicting_identity": events[3] = JsonNode.Parse(events[3].ToJsonString().Replace("B000000001", "B000000002", StringComparison.Ordinal))!.AsObject(); break;
            case "unproved_review": Records(events[0])[^2]!["href"] = AmazonAaxSourceDiagnosticTests.Heading + "&unobserved=1#customerReviews"; break;
            case "extra_heading": Records(events[0])[^1]!["heading"] = "h2"; break;
            case "wrong_source_reference": Records(events[0])[^1]!["reference"] = "other:record:0"; break;
            case "closed": events.Insert(2, new JsonObject { ["tool"] = "browser_close" }); break;
            case "new_search": events.Insert(3, events[0].DeepClone().AsObject()); break;
            case "early_visit": events.Insert(1, events[^1]); events.RemoveAt(events.Count - 1); break;
            case "reused_visit": events[^1] = events[2].DeepClone().AsObject(); break;
        }
        var report = LiveWorkflowOracles.VerifyAmazonAaxProductSelection(events, 3, change == "wrong_query" ? "other" : Query);
        Assert.False(report["passed"]!.GetValue<bool>(), report.ToJsonString());
    }

    internal static List<JsonObject> SourceProofs()
    {
        // Minimal synthetic source: the four retained AAX records form the whole
        // fixture document, rather than omitting any records of a live source.
        var source = AmazonAaxSourceDiagnosticTests.Source(); var records = Records(source);
        var card = records.TakeLast(4).Select(r => r!.DeepClone()).ToArray(); records.Clear();
        foreach (var record in card) records.Add(record);
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        return [source, AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.First, "image"),
            AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.Heading, "heading"),
            AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.Heading, "repeat")];
    }
    internal static List<JsonObject> WorkflowEvents()
    {
        // Fresh synthetic fixture with legacy store reads preceding the new
        // empty-ASIN card; complete original link order remains in one source.
        var events = JsonNode.Parse(JsonSerializerBytes(AmazonResolvedProductWorkflowTests.Events())
            .Replace("https://sponsored-ads.amazon.fr/clk?opaque=product-", "https://aax-eu-zaz.amazon.fr/x/c/product-", StringComparison.Ordinal))!
            .AsArray().OfType<JsonObject>().ToList();
        foreach (var (read, index) in events.Select((read, index) => (read, index)))
        {
            var id = index == 0 ? "search" : "own-capture-" + index;
            read["result"]!["observationSnapshot"]!["id"] = id;
            foreach (var page in read["result"]!["observationSnapshot"]!["pages"]!.AsArray())
            {
                page!["id"] = id;
                var records = page["records"]!.AsArray();
                for (var i = 0; i < records.Count; i++) records[i]!["reference"] = id + ":record:" + i;
            }
        }
        return events;
    }
    private static JsonObject Visit() => AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.Heading, "business");
    private static JsonArray Records(JsonObject source) => source["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static JsonObject Verify(IReadOnlyList<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonAaxProductSelection(events, 3, Query);
    private static string JsonSerializerBytes(IEnumerable<JsonObject> events) => new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString();
}
