using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Flow.Core.Runtime;

internal static class LiveWorkflowOracles
{
    internal static async Task<JsonObject> VerifyAsync(string scenario, string root, JsonObject run, IMcpClientFactory transport)
    {
        var findings = new List<string>();
        var folder = Path.GetFullPath(Path.Combine(root, run["workspace_relative"]!.ToString()));
        if (run["execution"]?["success"]?.GetValue<bool>() != true) findings.Add("workflow_execution_failed");
        var events = run["events"]!.AsArray().OfType<JsonObject>().ToArray();
        if (scenario == "amazon")
        {
            var browser = await transport.GetClientAsync("GnOuGo.Browser.Mcp", CancellationToken.None);
            var cleanup = await browser.CallToolAsync("browser_get_content", new JsonObject(), CancellationToken.None);
            if (!cleanup.IsError || cleanup.Content?.ToJsonString().Contains("No active page", StringComparison.Ordinal) != true)
                findings.Add("browser_not_closed_by_workflow");
            await browser.CallToolAsync("browser_close", new JsonObject(), CancellationToken.None);
            var path = Path.Combine(folder, "products.xlsx");
            if (!File.Exists(path)) findings.Add("workbook_missing");
            else
            {
                using var workbook = SpreadsheetDocument.Open(path, false);
                var part = workbook.WorkbookPart!;
                var rows = part.WorksheetParts.SelectMany(w => w.Worksheet!.Descendants<Row>())
                    .Select(row => row.Elements<Cell>().Select(c => CellText(c, part)).ToArray()).ToArray();
                if (rows.Length is < 2 or > 4 || rows.Any(r => r.Length < 3)) findings.Add("workbook_row_shape_or_product_bound");
                else
                {
                    var names = rows[0].Select(Normalize).ToArray();
                    var nameIndex = Array.FindIndex(names, n => n is "nom" or "name" or "nom du produit" or "product name");
                    var descriptionIndex = Array.FindIndex(names, n => n == "description");
                    var priceIndex = Array.FindIndex(names, n => n is "prix" or "price");
                    if (nameIndex < 0 || descriptionIndex < 0 || priceIndex < 0) findings.Add("workbook_columns_missing");
                    else
                    {
                        var observations = events.Where(e => e["tool"]?.ToString() == "browser_get_content" && e["error"]?.GetValue<bool>() == false)
                            .Select(e => e["result"]!).Where(r => Uri.TryCreate(r["url"]?.ToString(), UriKind.Absolute, out var url) &&
                                (url.Host == "amazon.fr" || url.Host.EndsWith(".amazon.fr", StringComparison.Ordinal)) &&
                                (url.AbsolutePath.Contains("/dp/", StringComparison.Ordinal) || url.AbsolutePath.Contains("/gp/product/", StringComparison.Ordinal))).ToArray();
                        var pages = observations.GroupBy(r => r["url"]!.ToString()).ToArray();
                        if (pages.Length != rows.Length - 1 || pages.Length > 3) findings.Add("product_visits_do_not_match_rows");
                        foreach (var row in rows.Skip(1))
                        {
                            var name = Normalize(row[nameIndex]); var description = Normalize(row[descriptionIndex]); var price = Normalize(row[priceIndex]);
                            if (name.Length == 0 || description.Length == 0 || price.Length == 0 || !observations.Any(r =>
                            {
                                var text = Normalize(r["content"]?.ToString() ?? "");
                                return text.Contains(name, StringComparison.Ordinal) && text.Contains(description, StringComparison.Ordinal) && PriceObserved(price, text);
                            })) findings.Add("workbook_value_not_supported_by_captured_product_page");
                        }
                    }
                }
                run["workbook_rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)JsonValue.Create(v)).ToArray())).ToArray());
            }
        }
        else
        {
            LiveCodeEvidence.Verify(run, Path.Combine(folder, "repository"));
            if (Directory.Exists(Path.Combine(folder, "repository"))) findings.Add("repository_not_cleaned_by_workflow");
            if (!File.Exists(Path.Combine(folder, "review.json"))) findings.Add("local_review_missing");
            var commands = events.SelectMany(e => (e["result"]?["toolExecutions"] as JsonArray ?? []).OfType<JsonObject>())
                .Where(e => e["completionObserved"]?.GetValue<bool>() == true && e["conflictingCompletion"]?.GetValue<bool>() == false)
                .SelectMany(e => (e["terminals"] as JsonArray ?? []).OfType<JsonObject>().Select(t => (Call: e, Terminal: t))).ToArray();
            if (commands.Length == 0) findings.Add("no_independent_command_completion_evidence");
            if (commands.Any(c => c.Terminal["exitCode"] is null)) findings.Add("command_exit_code_unknown");
            if (commands.Any(c => c.Terminal["workingDirectory"]?.ToString() is not { } cwd ||
                !Path.GetFullPath(cwd).StartsWith(Path.Combine(folder, "repository") + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Path.GetFullPath(cwd) != Path.Combine(folder, "repository"))) findings.Add("command_confinement_not_established");
            // A complete repository-specific manifest and independent pinned-head
            // observations are required; assistant text never certifies checks.
            if (run["checkout_head"]?.ToString() != LiveWorkflowEvaluation.Head || run["checkout_base"]?.ToString() != LiveWorkflowEvaluation.Base)
                findings.Add("pinned_checkout_not_independently_verified");
            if (run["required_checks_verified"]?.GetValue<bool>() != true) findings.Add("repository_required_checks_not_independently_verified");
            if (File.Exists(Path.Combine(folder, "review.json")))
            {
                var review = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "review.json")));
                var failedChecks = run["observed_commands"]!.AsArray().Any(c => c!["exit_code"]!.GetValue<long>() != 0);
                if (review is not JsonObject || review["decision"]?.ToString() is not ("approve" or "request_changes") || review["findings"] is not JsonArray ||
                    failedChecks && review["decision"]?.ToString() == "approve") findings.Add("review_missing_or_false_success");
            }
        }
        return new JsonObject { ["passed"] = findings.Count == 0, ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }

    private static string CellText(Cell cell, WorkbookPart workbook) => cell.DataType?.Value == CellValues.SharedString
        ? workbook.SharedStringTablePart!.SharedStringTable!.Elements<SharedStringItem>().ElementAt(int.Parse(cell.InnerText, CultureInfo.InvariantCulture)).InnerText : cell.InnerText;
    private static string Normalize(string value) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))), "\\s+", " ").Trim().ToLowerInvariant();
    private static bool PriceObserved(string price, string text) => text.Contains(price, StringComparison.Ordinal) ||
        text.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal).Contains(price.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
}
