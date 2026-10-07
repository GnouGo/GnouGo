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
            var productLimit = run["max_products"]?.GetValue<int>() ?? 3;
            if (productLimit is < 1 or > 10) throw new InvalidOperationException("Invalid retained product bound.");
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
                if (rows.Length < 2 || rows.Length > productLimit + 1 || rows.Any(r => r.Length < 3)) findings.Add("workbook_row_shape_or_product_bound");
                else
                {
                    var names = rows[0].Select(Normalize).ToArray();
                    var nameIndex = Array.FindIndex(names, n => n is "nom" or "name" or "nom du produit" or "product name");
                    var descriptionIndex = Array.FindIndex(names, n => n == "description");
                    var priceIndex = Array.FindIndex(names, n => n is "prix" or "price");
                    if (nameIndex < 0 || descriptionIndex < 0 || priceIndex < 0) findings.Add("workbook_columns_missing");
                    else
                    {
                        var observations = CompleteBrowserObservations(events).Where(r => Uri.TryCreate(r["url"]?.ToString(), UriKind.Absolute, out var url) &&
                                (url.Host == "amazon.fr" || url.Host.EndsWith(".amazon.fr", StringComparison.Ordinal)) &&
                                (url.AbsolutePath.Contains("/dp/", StringComparison.Ordinal) || url.AbsolutePath.Contains("/gp/product/", StringComparison.Ordinal))).ToArray();
                        var pages = observations.GroupBy(r => r["url"]!.ToString()).ToArray();
                        if (pages.Length != rows.Length - 1 || pages.Length > productLimit) findings.Add("product_visits_do_not_match_rows");
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

    // Adapt producer formats, not the oracle: only complete observed snapshots
    // supply text. Never read generated rows, mapping examples or assistant claims.
    internal static IEnumerable<JsonObject> CompleteBrowserObservations(IReadOnlyList<JsonObject> events)
    {
        var reads = events.Where(e => e["tool"]?.ToString() == "browser_get_content" && e["error"]?.GetValue<bool>() == false && e["result"] is JsonObject).ToArray();
        foreach (var read in reads)
        {
            var result = read["result"]!.AsObject();
            var url = result["url"]?.ToString();
            if (result["observationSnapshot"] is JsonObject snapshot)
            {
                if (result["truncated"]?.GetValue<bool>() != false || snapshot["captureTruncated"]?.GetValue<bool>() != false ||
                    snapshot["manifestTruncated"]?.GetValue<bool>() != false || snapshot["id"]?.ToString() is not { Length: > 0 } id ||
                    snapshot["pages"] is not JsonArray pages || pages.Count > 100) continue;
                var records = new List<JsonNode?>(); var valid = true;
                foreach (var page in pages)
                {
                    if (page?["id"]?.ToString() != id || page["captureTruncated"]?.GetValue<bool>() != false || page["nextCursor"] is not null ||
                        page["records"] is not JsonArray values || values.Count is < 1 or > 200) { valid = false; break; }
                    records.AddRange(values);
                }
                if (valid && records.Count == snapshot["recordCount"]?.GetValue<int>())
                    yield return new JsonObject { ["url"] = url, ["content"] = string.Join("\n", records.Select(r => r?["text"]?.ToString() ?? "")) };
            }
            else if (result["observationManifest"] is JsonObject manifest)
            {
                if (manifest["captureTruncated"]?.GetValue<bool>() != false || manifest["manifestTruncated"]?.GetValue<bool>() != false ||
                    manifest["pages"] is not JsonArray pages || pages.Count > 100 || manifest["id"]?.ToString() is not { Length: > 0 } id) continue;
                var records = new List<JsonNode?>(); var valid = true; var cursors = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < pages.Count; i++)
                {
                    var cursor = pages[i]?["cursor"]?.ToString();
                    if (cursor is null || !cursors.Add(cursor)) { valid = false; break; }
                    var matches = reads.SkipWhile(r => !ReferenceEquals(r, read)).Skip(1)
                        .Where(r => r["arguments"]?["cursor"]?.ToString() == cursor).ToArray();
                    if (matches.Length == 0 || matches.Any(r => !JsonNode.DeepEquals(r["result"], matches[0]["result"]))) { valid = false; break; }
                    var page = matches[0]["result"]!;
                    if (page["url"]?.ToString() != url || page["observation"] is not JsonObject observed || observed["id"]?.ToString() != id ||
                        observed["captureTruncated"]?.GetValue<bool>() != false || observed["records"] is not JsonArray values ||
                        values.Count != pages[i]?["recordCount"]?.GetValue<int>() || observed["nextCursor"]?.ToString() != (i + 1 < pages.Count ? pages[i + 1]?["cursor"]?.ToString() : null))
                    { valid = false; break; }
                    records.AddRange(values);
                }
                if (valid && records.Count == manifest["recordCount"]?.GetValue<int>())
                    yield return new JsonObject { ["url"] = url, ["content"] = string.Join("\n", records.Select(r => r?["text"]?.ToString() ?? "")) };
            }
            else if (result["observation"] is JsonObject observation)
            {
                if (read["arguments"]?["cursor"] is not null || observation["captureTruncated"]?.GetValue<bool>() != false) continue;
                var id = observation["id"]?.ToString(); var current = observation;
                var text = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var valid = true;
                while (true)
                {
                    if (current["records"] is not JsonArray records) { valid = false; break; }
                    text.AddRange(records.Select(r => r?["text"]?.ToString() ?? ""));
                    if (current["nextCursor"]?.ToString() is not { } cursor) break;
                    if (!seen.Add(cursor) || seen.Count > 100) { valid = false; break; }
                    var next = reads.SkipWhile(r => !ReferenceEquals(r, read)).Skip(1)
                        .FirstOrDefault(r => r["arguments"]?["cursor"]?.ToString() == cursor)?["result"];
                    if (next?["url"]?.ToString() != url || next?["observation"] is not JsonObject following ||
                        following["id"]?.ToString() != id || following["captureTruncated"]?.GetValue<bool>() != false) { valid = false; break; }
                    current = following;
                }
                if (valid) yield return new JsonObject { ["url"] = url, ["content"] = string.Join("\n", text) };
            }
            else if (result["truncated"]?.GetValue<bool>() == false && result["nextCursor"] is null) yield return result;
        }
    }

    private static string CellText(Cell cell, WorkbookPart workbook) => cell.DataType?.Value == CellValues.SharedString
        ? workbook.SharedStringTablePart!.SharedStringTable!.Elements<SharedStringItem>().ElementAt(int.Parse(cell.InnerText, CultureInfo.InvariantCulture)).InnerText : cell.InnerText;
    private static string Normalize(string value) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))), "\\s+", " ").Trim().ToLowerInvariant();
    private static bool PriceObserved(string price, string text) => text.Contains(price, StringComparison.Ordinal) ||
        text.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal).Contains(price.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
}
