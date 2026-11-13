using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonAaxProductProposalTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void EveryOwnOrderedReadAndExactAliasSupportOnlyAnInactiveRelationship()
    {
        var events = Events(); var bytes = new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString();
        var report = LiveWorkflowOracles.AnalyzeAmazonAaxProductContextProposal(events, Query);
        Assert.True(report["relationship_evidence_complete"]!.GetValue<bool>(), report.ToJsonString());
        var context = Assert.Single(report["contexts"]!.AsArray())!;
        Assert.Equal(4, context["original_member_count"]!.GetValue<int>());
        Assert.Equal(3, context["own_product_reads"]!.GetValue<int>());
        Assert.False(Assert.Single(context["exact_review_aliases"]!.AsArray())!["http_receipt_claimed"]!.GetValue<bool>());
        foreach (var field in new[] { "active", "source_prerequisite_qualified", "business_oracle_passed", "navigation_authorized" })
            Assert.False(report[field]!.GetValue<bool>());
        Assert.False(AmazonObservedRedirectPolicy.IsObserved(report["version"]!.ToString()));
        Assert.Equal(bytes, new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString());
        var cold = JsonNode.Parse(bytes)!.AsArray().OfType<JsonObject>().ToArray();
        Assert.True(JsonNode.DeepEquals(report, LiveWorkflowOracles.AnalyzeAmazonAaxProductContextProposal(cold, Query)));
    }

    [Fact]
    public void NoAaxEntriesDoNotEstablishAProductRelationship()
    {
        var source = AmazonStoreListWorkflowTests.Events()[0];
        source["result"]!["acquisition"] = new JsonObject { ["navigation"] = new JsonObject { ["url"] = source["result"]!["url"]!.DeepClone() } };
        var report = LiveWorkflowOracles.AnalyzeAmazonAaxProductContextProposal([source], Query);
        Assert.False(report["relationship_evidence_complete"]!.GetValue<bool>());
        Assert.Empty(report["contexts"]!.AsArray());
        Assert.DoesNotContain(report["findings"]!.AsArray(), finding => finding!.ToString() == "complete_owned_search_required");
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
    }

    [Fact]
    public void TwoContextsRetainIndependentIdentitiesAndEveryRepeatedOccurrence()
    {
        var events = Events(); var source = events[0]; var records = Records(source);
        var second = records.Skip(records.Count - 4).Select(record => record!.DeepClone().AsObject()).ToArray();
        foreach (var record in second)
        {
            record["selector"] = record["selector"] + "-second";
            record["ancestorAttributes"]!["data-asin"]!["index"] = 902;
            record["href"] = record["href"]!.ToString().Replace("/opaque-", "/second-opaque-", StringComparison.Ordinal);
            records.Add(record);
        }
        for (var i = 0; i < records.Count; i++) records[i]!["reference"] = "search:record:" + i;
        source["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        foreach (var (entry, index) in second.Where(record => AmazonAaxSourceDiagnostic.IsEntry(record["href"]!.ToString())).Select((entry, index) => (entry, index)))
        {
            var read = AmazonAaxSourceDiagnosticTests.Resolution(entry["href"]!.ToString(), "second-" + index);
            // The advertised target stays unchanged: only the actual own redirect
            // and document response establish the second context's identity.
            var result = read["result"]!; var destination = result["url"]!.ToString().Replace("B000000001", "B000000002", StringComparison.Ordinal);
            result["url"] = destination; result["navigationEvidence"]!["finalUrl"] = destination;
            result["navigationEvidence"]!["hops"]![0]!["location"] = destination;
            result["navigationEvidence"]!["hops"]![1]!["url"] = destination;
            result["acquisition"]!["navigation"]!["url"] = destination;
            events.Add(read);
        }
        var report = LiveWorkflowOracles.AnalyzeAmazonAaxProductContextProposal(events, Query);
        Assert.True(report["relationship_evidence_complete"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Equal(new[] { "B000000001", "B000000002" }, report["contexts"]!.AsArray().Select(context => context!["candidate_identity"]!.ToString()));
        Assert.All(report["contexts"]!.AsArray(), context => Assert.Equal(3, context!["own_product_reads"]!.GetValue<int>()));
        Assert.False(report["business_oracle_passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("missing_repeat")][InlineData("reused_capture")][InlineData("wrong_order")]
    [InlineData("unobserved_redirect")][InlineData("failed_read")][InlineData("truncated")]
    [InlineData("conflicting_identity")][InlineData("unproved_review")][InlineData("extra_heading")]
    [InlineData("wrong_query")][InlineData("wrong_source_reference")]
    public void MissingOrConflictingEvidenceNeverResolvesTheContext(string change)
    {
        var events = Events();
        switch (change)
        {
            case "missing_repeat": events.RemoveAt(3); break;
            case "reused_capture": events[3] = events[2].DeepClone().AsObject(); break;
            case "wrong_order": (events[1], events[2]) = (events[2], events[1]); break;
            case "unobserved_redirect": events[1]["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = AmazonAaxSourceDiagnosticTests.Heading + "&unobserved=1"; break;
            case "failed_read": events[1]["error"] = true; break;
            case "truncated": events[1]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "conflicting_identity":
                var text = events[3].ToJsonString().Replace("B000000001", "B000000002", StringComparison.Ordinal);
                events[3] = JsonNode.Parse(text)!.AsObject(); break;
            case "unproved_review": Records(events[0])[^2]!["href"] = AmazonAaxSourceDiagnosticTests.Heading + "&unobserved=1#customerReviews"; break;
            case "extra_heading": Records(events[0])[^1]!["heading"] = "h2"; break;
            case "wrong_source_reference": Records(events[0])[^1]!["reference"] = "other:record:0"; break;
        }
        var report = LiveWorkflowOracles.AnalyzeAmazonAaxProductContextProposal(events, change == "wrong_query" ? "other" : Query);
        Assert.False(report["relationship_evidence_complete"]!.GetValue<bool>(), report.ToJsonString());
        Assert.False(report["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(report["navigation_authorized"]!.GetValue<bool>());
    }

    private static List<JsonObject> Events() => [AmazonAaxSourceDiagnosticTests.Source(),
        AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.First, "image"),
        AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.Heading, "heading"),
        AmazonAaxSourceDiagnosticTests.Resolution(AmazonAaxSourceDiagnosticTests.Heading, "repeat")];
    private static JsonArray Records(JsonObject source) => source["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
}
