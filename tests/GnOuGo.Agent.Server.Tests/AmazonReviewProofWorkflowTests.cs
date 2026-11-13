using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonReviewProofWorkflowTests
{
    private const string Policy = "amazon-observed-review-proof-context-v6";

    [Fact]
    public void SourceResolutionFactsDoNotRequireEarlyReadsOfAlreadyIdentifiedProducts()
    {
        var events = Events(); var records = Records(events[0]);
        var known = records.OfType<JsonObject>().First(r =>
            r["ancestorAttributes"]?["data-asin"]?["value"]?.ToString() is { Length: 10 });
        var knownHref = known["href"]!.ToString();
        records.Remove(known); records.Insert(0, known);
        for (var index = 0; index < records.Count; index++) records[index]!["reference"] = "search:record:" + index;
        Assert.True(AmazonReviewProofPolicy.Allows(knownHref));
        var next = LiveWorkflowOracles.ResolvedSourceAllowedEntries([events[0]], Policy).ToArray();
        Assert.Equal([events[1]["arguments"]!["url"]!.ToString()], next);
        Assert.DoesNotContain(knownHref, next);
        Assert.Contains(knownHref, LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(21).ToArray(), Policy));

        const string resolution = "Before business product selection, resolve every sponsored source-link occurrence with unverified identity by GET of its exact literal href in source order, including repeated hrefs.";
        Assert.Contains(resolution, AmazonResolvedProductPolicy.PlanningFacts, StringComparison.Ordinal);
        Assert.Contains(resolution, AmazonReviewProofPolicy.PlanningFacts, StringComparison.Ordinal);
        Assert.DoesNotContain("Resolve every navigable source-link occurrence", AmazonReviewProofPolicy.PlanningFacts, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactReviewAliasRequiresEveryNavigableOccurrenceBeforeBusinessAuthority()
    {
        var events = Events();
        for (var count = 1; count <= 20; count++)
            Assert.Equal([events[count]["arguments"]!["url"]!.ToString()],
                LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(count).ToArray(), Policy));
        var allowed = LiveWorkflowOracles.ResolvedSourceAllowedEntries(events.Take(21).ToArray(), Policy).ToArray();
        Assert.Contains(events[21]["arguments"]!["url"]!.ToString(), allowed);
        Assert.DoesNotContain(Records(events[0])[^1]!["href"]!.ToString(), allowed);
    }

    [Fact]
    public void ProofReferencesRetainRawRecordsWithoutInventingReadsOrBusinessVisits()
    {
        var events = Events(); var raw = new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString();
        var source = Verify(events.Take(21).ToList());
        Assert.True(source["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(source["passed"]!.GetValue<bool>());
        Assert.Equal(3, source["source_product_reads"]!.GetValue<int>());
        Assert.Equal(20, source["source_resolution_count"]!.GetValue<int>());
        Assert.Equal(0, source["visited_product_count"]!.GetValue<int>());
        var alias = Assert.Single(source["source_proof_aliases"]!.AsArray())!;
        Assert.Equal("search:record:18", Assert.Single(alias["proof_source_records"]!.AsArray())!.ToString());
        Assert.Equal(19, Assert.Single(alias["proof_event_indices"]!.AsArray())!.GetValue<int>());
        Assert.False(alias["http_call_created"]!.GetValue<bool>());
        Assert.False(alias["business_visit"]!.GetValue<bool>());
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonResolvedProductSelection(events, 3, "chaussure geox homme 45")["passed"]!.GetValue<bool>());
        Assert.Equal(raw, new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString());
    }

    [Fact]
    public void RepeatedHrefOccurrencesEachNeedTheirOwnProofAfterColdReload()
    {
        var events = Events(); var records = Records(events[0]);
        records[19]!["href"] = records[18]!["href"]!.DeepClone();
        events[20] = events[19].DeepClone().AsObject();
        var encoded = new JsonArray(events.Select(e => e.DeepClone()).ToArray()).ToJsonString();
        var restored = JsonNode.Parse(encoded)!.AsArray().OfType<JsonObject>().ToList();
        var result = Verify(restored);
        Assert.True(result["passed"]!.GetValue<bool>());
        var alias = Assert.Single(result["source_proof_aliases"]!.AsArray())!;
        Assert.Equal(2, alias["proof_source_records"]!.AsArray().Count);
        Assert.Equal(3, result["source_product_reads"]!.GetValue<int>());
        restored.RemoveAt(20);
        Assert.False(Verify(restored)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void SameIdentityAcrossContextsStillRequiresTwoOrderedBusinessOccurrences()
    {
        var events = AmazonResolvedProductWorkflowTests.Events(2); var records = Records(events[0]);
        foreach (var at in new[] { 17, 20 })
        {
            var alias = records[at]!.DeepClone(); alias["href"] = records[at + 1]!["href"] + "#customerReviews";
            alias["reference"] = "search:record:" + records.Count; records.Add(alias);
        }
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        var report = LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 4, "chaussure geox homme 45");
        Assert.True(report["passed"]!.GetValue<bool>());
        Assert.Equal(4, report["expected_product_count"]!.GetValue<int>());
        Assert.Equal(6, report["source_product_reads"]!.GetValue<int>());
        Assert.Equal(2, report["source_proof_aliases"]!.AsArray().Count);
        events.RemoveAt(24);
        Assert.False(LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 4, "chaussure geox homme 45")["passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("query")]
    [InlineData("context")]
    [InlineData("heading")]
    [InlineData("occurrence")]
    [InlineData("fragment")]
    [InlineData("encoded")]
    [InlineData("known_asin")]
    [InlineData("missing_base")]
    [InlineData("missing_proof")]
    [InlineData("store_proof")]
    [InlineData("conflicting_product")]
    [InlineData("navigate_fragment")]
    [InlineData("missing_business")]
    public void AnAliasCannotWaiveSourceEvidenceOrBusinessVisits(string change)
    {
        var events = Events(); var records = Records(events[0]); var alias = records[^1]!;
        switch (change)
        {
            case "query": alias["href"] = alias["href"]!.ToString().Replace("?opaque=", "?different=", StringComparison.Ordinal); break;
            case "context": alias["ancestorAttributes"]!["data-asin"]!["index"] = 91; break;
            case "heading": alias["heading"] = "h2"; break;
            case "occurrence": alias["occurrence"] = new JsonObject { ["index"] = 99 }; break;
            case "fragment": alias["href"] = alias["href"]!.ToString().Replace("#customerReviews", "#other", StringComparison.Ordinal); break;
            case "encoded": alias["href"] = alias["href"]!.ToString().Replace("#customerReviews", "%23customerReviews", StringComparison.Ordinal); break;
            case "known_asin": alias["ancestorAttributes"]!["data-asin"]!["value"] = "B000000003"; break;
            case "missing_base": records[18]!["href"] += "&different=1"; break;
            case "missing_proof": events.RemoveAt(19); break;
            case "store_proof":
                var store = events[1].DeepClone().AsObject();
                var request = events[19]["arguments"]!["url"]!.ToString();
                store["arguments"]!["url"] = request; store["result"]!["navigationEvidence"]!["requestedUrl"] = request;
                store["result"]!["navigationEvidence"]!["hops"]![0]!["url"] = request; events[19] = store; break;
            case "conflicting_product": events[19]["result"]!["url"] = "https://www.amazon.fr/dp/B000000004"; break;
            case "navigate_fragment": events[19]["arguments"]!["url"] = alias["href"]!.DeepClone(); break;
            case "missing_business": events.RemoveAt(21); break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void GrantAndNavigationRemainSeparateFromProofReferences()
    {
        Assert.Equal(Policy, AmazonObservedRedirectPolicy.Capture(
            ["--amazon-review-proof-context", "--amazon-scope-approval", AmazonReviewProofPolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-review-proof-context", "--amazon-scope-approval", AmazonResolvedProductPolicy.Approval], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-review-proof-context", "--amazon-resolved-product-context", "--amazon-scope-approval", AmazonReviewProofPolicy.Approval], "amazon"));
        Assert.Equal(AmazonResolvedProductPolicy.UrlPattern, AmazonReviewProofPolicy.UrlPattern);
        Assert.False(AmazonReviewProofPolicy.Allows(Records(Events()[0])[^1]!["href"]!.ToString()));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("header_only", false)]
    [InlineData("missing_header", false)]
    [InlineData("swapped_rows", false)]
    [InlineData("invented_field", false)]
    [InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)]
    [InlineData("not_closed", false)]
    [InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)]
    [InlineData("source_only", false)]
    [InlineData("early_fields", false)]
    public Task FullOracleKeepsEveryBusinessRequirement(string change, bool passed)
        => AmazonResolvedStoreWorkflowTests.CheckBusinessAsync(change, passed, productResolution: true, reviewProof: true);

    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonReviewProofSelection(events, 3, "chaussure geox homme 45");

    internal static List<JsonObject> Events()
    {
        var events = AmazonResolvedProductWorkflowTests.Events(); var records = Records(events[0]);
        var alias = records[17]!.DeepClone(); alias["href"] = records[18]!["href"] + "#customerReviews";
        alias["reference"] = "search:record:" + records.Count; records.Add(alias);
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        return events;
    }

    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
}
