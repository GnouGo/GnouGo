using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AmazonResolvedStoreWorkflowTests
{
    private const string Query = "chaussure geox homme 45";

    [Fact]
    public void SourceResolutionsDoNotConstituteProductVisitsOrChangeV7()
    {
        var events = AmazonSourceResolutionProposalTests.Events();
        var sourceOnly = Verify(events);
        Assert.True(sourceOnly["source_prerequisite_qualified"]!.GetValue<bool>());
        Assert.False(sourceOnly["passed"]!.GetValue<bool>());
        Assert.Equal(0, sourceOnly["visited_product_count"]!.GetValue<int>());
        Assert.Equal(17, sourceOnly["source_resolution_count"]!.GetValue<int>());
        Assert.True(Verify(Events())["passed"]!.GetValue<bool>());
        Assert.False(LiveWorkflowOracles.VerifyAmazonProductContextSelection(Events(), 2, Query)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void RepeatedProductIdentitiesStillRequireEveryOccurrence()
    {
        var events = Events().Select(e => JsonNode.Parse(e.ToJsonString().Replace("B000000002", "B000000001", StringComparison.Ordinal))!.AsObject()).ToList();
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.Equal(2, Verify(events)["expected_product_count"]!.GetValue<int>());
        events.RemoveAt(19);
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void AlreadyProvedProductSourceNeedsNoStoreResolution()
    {
        var events = AmazonProductContextOracleTests.Events(); var records = Records(events[0]); records.RemoveAt(0);
        for (var index = 0; index < records.Count; index++) records[index]!["reference"] = "search:record:" + index;
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
        Assert.True(Verify(events)["passed"]!.GetValue<bool>());
        Assert.Equal(0, Verify(events)["source_resolution_count"]!.GetValue<int>());
        Assert.Contains(events[1]["arguments"]!["url"]!.ToString(), LiveWorkflowOracles.ResolvedStoreAllowedEntries([events[0]]));
    }

    [Fact]
    public void EachResolutionIsIndividuallyRequiredBeforeSourceProductAuthority()
    {
        var events = Events();
        Assert.Empty(LiveWorkflowOracles.ResolvedStoreAllowedEntries([]));
        for (var count = 1; count <= 17; count++)
            Assert.Equal([events[count]["arguments"]!["url"]!.ToString()], LiveWorkflowOracles.ResolvedStoreAllowedEntries(events.Take(count).ToArray()));
        var products = LiveWorkflowOracles.ResolvedStoreAllowedEntries(events.Take(18).ToArray()).ToArray();
        Assert.Contains(events[18]["arguments"]!["url"]!.ToString(), products);
        Assert.Contains(events[19]["arguments"]!["url"]!.ToString(), products);
        Assert.DoesNotContain(events[1]["arguments"]!["url"]!.ToString(), products);
    }

    [Theory]
    [InlineData("omit_resolution")]
    [InlineData("reorder_resolutions")]
    [InlineData("invented_resolution")]
    [InlineData("product_destination")]
    [InlineData("history_disguises_product")]
    [InlineData("heading_conflict")]
    [InlineData("occurrence_conflict")]
    [InlineData("truncated_source")]
    [InlineData("truncated_destination")]
    [InlineData("close")]
    [InlineData("new_search")]
    [InlineData("redirect_search")]
    [InlineData("missing_navigation")]
    [InlineData("failed_read")]
    [InlineData("omit_product")]
    [InlineData("reorder_products")]
    [InlineData("invented_product")]
    [InlineData("wrong_product")]
    public void IncompleteContradictoryOrExpiredEvidenceCannotQualify(string change)
    {
        var events = Events();
        switch (change)
        {
            case "omit_resolution": events.RemoveAt(1); break;
            case "reorder_resolutions": (events[1], events[2]) = (events[2], events[1]); break;
            case "invented_resolution": events[1]["arguments"]!["url"] += "&invented=1"; break;
            case "product_destination": SetProductDestination(events[1], false); break;
            case "history_disguises_product": SetProductDestination(events[1], true); break;
            case "heading_conflict": Records(events[0])[0]!["heading"] = "Product heading"; break;
            case "occurrence_conflict": Records(events[0])[0]!["occurrence"] = new JsonObject { ["selector"] = "#product", ["tag"] = "article", ["index"] = 0 }; break;
            case "truncated_source": events[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "truncated_destination": events[1]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "close": events.Insert(18, new JsonObject { ["tool"] = "browser_close" }); break;
            case "new_search": events.Insert(18, events[0].DeepClone().AsObject()); break;
            case "redirect_search": events[1]["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = events[0]["arguments"]!["url"]!.DeepClone(); break;
            case "missing_navigation": events[1]["result"]!["navigationEvidence"] = null; break;
            case "failed_read": events.Insert(18, new JsonObject { ["tool"] = "browser_get_content", ["arguments"] = new JsonObject(), ["error"] = true }); break;
            case "omit_product": events.RemoveAt(18); break;
            case "reorder_products": (events[18], events[19]) = (events[19], events[18]); break;
            case "invented_product": events[18]["arguments"]!["url"] += "&invented=1"; break;
            case "wrong_product": events[18]["result"]!["url"] = "https://www.amazon.fr/dp/B000000003"; break;
        }
        Assert.False(Verify(events)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void CaptureRequiresTheNewGrantAndPreservesOldProfiles()
    {
        Assert.Equal(AmazonResolvedStorePolicy.Id, AmazonObservedRedirectPolicy.Capture(
            ["--amazon-resolved-store-context", "--amazon-scope-approval", AmazonResolvedStorePolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-resolved-store-context", "--amazon-scope-approval", AmazonProductContextPolicy.Approval], "amazon"));
        Assert.Throws<InvalidOperationException>(() => AmazonObservedRedirectPolicy.Capture(["--amazon-resolved-store-context"], "amazon"));
        Assert.Throws<ArgumentException>(() => AmazonObservedRedirectPolicy.Capture(
            ["--amazon-resolved-store-context", "--amazon-product-context", "--amazon-scope-approval", AmazonResolvedStorePolicy.Approval], "amazon"));
        Assert.False(AmazonSponsoredHostPolicy.Allows("https://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc"));
        Assert.False(AmazonResolvedStorePolicy.Allows("https://sponsored-ads.amazon.fr/clk?opaque=x"));
        Assert.False(AmazonResolvedStorePolicy.Allows("https://www.amazon.fr/stores/Brand"));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("header_only", false)]
    [InlineData("swapped_rows", false)]
    [InlineData("invented_field", false)]
    [InlineData("execution_failed", false)]
    [InlineData("missing_writer", false)]
    [InlineData("not_closed", false)]
    [InlineData("omitted_resolution", false)]
    [InlineData("omitted_product", false)]
    public async Task FullBusinessOracleStillRequiresExactWorkbookExecutionReceiptsAndCleanup(string change, bool passed)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-resolved-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "products.xlsx"); var events = Events();
            string[][] rows = [["name", "description", "price"], ["Product 1", "Description 1", "12,00 €"], ["Product 2", "Description 2", "13,00 €"]];
            if (change == "header_only") rows = [rows[0]];
            if (change == "swapped_rows") (rows[1], rows[2]) = (rows[2], rows[1]);
            if (change == "invented_field") rows[1][1] = "Invented description";
            using (var workbook = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var part = workbook.AddWorkbookPart(); part.Workbook = new Workbook();
                var sheet = part.AddNewPart<WorksheetPart>();
                sheet.Worksheet = new Worksheet(new SheetData(rows.Select(row => new Row(row.Select(value => new Cell
                    { DataType = CellValues.String, CellValue = new CellValue(value) })))));
                part.Workbook.Append(new Sheets(new Sheet { Id = part.GetIdOfPart(sheet), SheetId = 1, Name = "Products" }));
            }
            if (change == "omitted_resolution") events.RemoveAt(1);
            if (change == "omitted_product") events.RemoveAt(18);
            if (change != "missing_writer") events.Add(new JsonObject { ["tool"] = "document_write", ["error"] = false,
                ["result"] = new JsonObject { ["success"] = true, ["filePath"] = path } });
            events.Add(new JsonObject { ["tool"] = "browser_close" });
            var run = new JsonObject { ["workspace_relative"] = ".", ["oracle_version"] = LiveWorkflowOracles.AmazonResolvedStoreVersion,
                ["browser_observation_policy"] = AmazonResolvedStorePolicy.Id, ["browser_scope_approval"] = AmazonResolvedStorePolicy.Approval,
                ["max_products"] = 2, ["inputs"] = new JsonObject { ["query"] = Query },
                ["execution"] = new JsonObject { ["success"] = change != "execution_failed" },
                ["events"] = new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()) };
            var transport = new InMemoryMcpClientFactory();
            transport.RegisterServer("GnOuGo.Browser.Mcp", new MockMcpServerConfig { ToolHandlers = new()
            {
                ["browser_get_content"] = _ => new() { IsError = change != "not_closed", Content = new JsonObject { ["message"] = change == "not_closed" ? "Still open" : "No active page" } },
                ["browser_close"] = _ => new() { Content = new JsonObject { ["success"] = true } }
            } });
            var report = await LiveWorkflowOracles.VerifyAsync("amazon", root, run, transport);
            Assert.Equal(passed, report["passed"]!.GetValue<bool>());
            if (passed) Assert.Equal(2, run["product_evidence"]!["visit_count"]!.GetValue<int>());
            run["browser_scope_approval"] = AmazonProductContextPolicy.Approval;
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowOracles.VerifyAsync("amazon", root, run, null!));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static List<JsonObject> Events()
    {
        var events = AmazonSourceResolutionProposalTests.Events();
        foreach (var (read, index) in AmazonProductContextOracleTests.Events().Skip(1).Select((read, index) => (read, index)))
        {
            var records = Records(read); records.Clear();
            foreach (var text in new[] { "Product " + (index + 1), "Description " + (index + 1), "Price: " + (12 + index) + ",00 €" }) records.Add(new JsonObject { ["text"] = text });
            read["result"]!["observationSnapshot"]!["recordCount"] = records.Count;
            read["result"]!["acquisition"] = new JsonObject { ["navigation"] = new JsonObject { ["url"] = read["result"]!["url"]!.DeepClone() } };
            events.Add(read);
        }
        return events;
    }

    private static void SetProductDestination(JsonObject read, bool history)
    {
        const string product = "https://www.amazon.fr/dp/B000000001";
        var navigation = read["result"]!["navigationEvidence"]!;
        if (history) navigation["documentUrl"] = read["result"]!["url"]!.DeepClone();
        else read["result"]!["url"] = product;
        navigation["finalUrl"] = product; navigation["hops"]![0]!["location"] = product; navigation["hops"]![1]!["url"] = product;
    }
    private static JsonArray Records(JsonObject read) => read["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
    private static JsonObject Verify(List<JsonObject> events) => LiveWorkflowOracles.VerifyAmazonResolvedStoreSelection(events, 2, Query);
}
