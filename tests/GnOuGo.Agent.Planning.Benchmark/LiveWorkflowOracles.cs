using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Flow.Core.Runtime;

internal static class LiveWorkflowOracles
{
    internal const string ObservedProductsVersion = "observed-products-v2";
    internal const string AmazonBusinessVersion = "amazon-business-v3";
    internal const string CodeBusinessVersion = "code-business-v2";
    internal static async Task<JsonObject> VerifyAsync(string scenario, string root, JsonObject run, IMcpClientFactory transport,
        Func<Task<McpCallResult>>? hostClosureProbe = null)
    {
        var findings = new List<string>();
        var folder = Path.GetFullPath(Path.Combine(root, run["workspace_relative"]!.ToString()));
        if (run["execution"]?["success"]?.GetValue<bool>() != true) findings.Add("workflow_execution_failed");
        var events = run["events"]!.AsArray().OfType<JsonObject>().ToArray();
        if (scenario == "amazon")
        {
            if (run["oracle_version"]?.ToString() is { } version && version is not ("real-workflows-v1" or ObservedProductsVersion or AmazonBusinessVersion))
                throw new InvalidOperationException("Unknown retained execution oracle version.");
            var productLimit = run["max_products"]?.GetValue<int>() ?? 3;
            if (productLimit is < 1 or > 10) throw new InvalidOperationException("Invalid retained product bound.");
            var browser = await transport.GetClientAsync("GnOuGo.Browser.Mcp", CancellationToken.None);
            var cleanup = hostClosureProbe is null
                ? await browser.CallToolAsync("browser_get_content", new JsonObject(), CancellationToken.None)
                : await hostClosureProbe();
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
                    else if (run["oracle_version"]?.ToString() is ObservedProductsVersion or AmazonBusinessVersion)
                    {
                        var evidence = VerifyObservedProducts(events, rows, productLimit);
                        run["product_evidence"] = evidence;
                        findings.AddRange(evidence["findings"]!.AsArray().Select(f => f!.ToString()));
                        if (run["oracle_version"]?.ToString() == AmazonBusinessVersion)
                        {
                            var selection = VerifyAmazonSelection(events, productLimit, run["inputs"]?["query"]?.ToString());
                            run["search_evidence"] = selection;
                            findings.AddRange(selection["findings"]!.AsArray().Select(f => f!.ToString()));
                        }
                        if (!events.Any(e => e["tool"]?.ToString() == "document_write" && e["error"]?.GetValue<bool>() == false &&
                            e["result"]?["success"]?.GetValue<bool>() == true &&
                            e["result"]?["filePath"]?.ToString() is { } written && Path.GetFullPath(written) == path))
                            findings.Add("workbook_writer_receipt_missing");
                    }
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
            if (run["oracle_version"]?.ToString() is { } version && version is not ("real-workflows-v1" or CodeBusinessVersion))
                throw new InvalidOperationException("Unknown retained execution oracle version.");
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
                if (run["oracle_version"]?.ToString() == CodeBusinessVersion)
                {
                    var reportEvidence = LiveCodeEvidence.VerifyReport(run, review, Path.Combine(folder, "repository"), Path.Combine(folder, "review.json"));
                    run["report_evidence"] = reportEvidence;
                    findings.AddRange(reportEvidence["findings"]!.AsArray().Select(f => f!.ToString()));
                }
            }
        }
        return new JsonObject { ["passed"] = findings.Count == 0, ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }

    // Adapt producer formats, not the oracle: only complete observed snapshots
    // supply text. Never read generated rows, mapping examples or assistant claims.
    internal static IEnumerable<JsonObject> CompleteBrowserObservations(IReadOnlyList<JsonObject> events)
        => CompleteSnapshots(events).Select(s => new JsonObject { ["url"] = s.Url, ["content"] = s.Content });

    private sealed record Snapshot(JsonObject Read, string? Url, IReadOnlyList<JsonNode?> Records, string? LegacyText = null)
    {
        internal string Content => LegacyText ?? string.Join("\n", Records.Select(r => r?["text"]?.ToString() ?? ""));
    }

    private static IEnumerable<Snapshot> CompleteSnapshots(IReadOnlyList<JsonObject> events)
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
                    yield return new Snapshot(read, url, records);
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
                    yield return new Snapshot(read, url, records);
            }
            else if (result["observation"] is JsonObject observation)
            {
                if (read["arguments"]?["cursor"] is not null || observation["captureTruncated"]?.GetValue<bool>() != false) continue;
                var id = observation["id"]?.ToString(); var current = observation;
                var allRecords = new List<JsonNode?>(); var seen = new HashSet<string>(StringComparer.Ordinal); var valid = true;
                while (true)
                {
                    if (current["records"] is not JsonArray records) { valid = false; break; }
                    allRecords.AddRange(records);
                    if (current["nextCursor"]?.ToString() is not { } cursor) break;
                    if (!seen.Add(cursor) || seen.Count > 100) { valid = false; break; }
                    var next = reads.SkipWhile(r => !ReferenceEquals(r, read)).Skip(1)
                        .FirstOrDefault(r => r["arguments"]?["cursor"]?.ToString() == cursor)?["result"];
                    if (next?["url"]?.ToString() != url || next?["observation"] is not JsonObject following ||
                        following["id"]?.ToString() != id || following["captureTruncated"]?.GetValue<bool>() != false) { valid = false; break; }
                    current = following;
                }
                if (valid) yield return new Snapshot(read, url, allRecords);
            }
            else if (result["truncated"]?.GetValue<bool>() == false && result["nextCursor"] is null) yield return new Snapshot(read, url, [], result["content"]?.ToString() ?? "");
        }
    }

    // Pure evidence replay: no Browser actions, inference or artifact mutation.
    internal static JsonObject VerifyObservedProducts(IReadOnlyList<JsonObject> events, string[][] rows, int maximum)
    {
        var findings = new List<string>();
        var complete = CompleteSnapshots(events).ToDictionary(s => s.Read);
        var observedUrls = new HashSet<string>(StringComparer.Ordinal);
        var visits = new List<Snapshot>();
        var visitEvents = new JsonArray();
        for (var index = 0; index < events.Count; index++)
        {
            var read = events[index];
            if (read["tool"]?.ToString() != "browser_get_content") continue;
            complete.TryGetValue(read, out var snapshot);
            var requested = read["arguments"]?["url"]?.ToString();
            if (requested is not null && observedUrls.Contains(requested))
            {
                if (snapshot is null || snapshot.Records.Count == 0 || !HttpUrl(snapshot.Url) || read["result"]?["success"]?.GetValue<bool>() == false)
                    findings.Add("visited_observation_incomplete");
                else if (read["result"]?["acquisition"]?["navigation"]?["url"] is { } navigation && navigation.ToString() != snapshot.Url ||
                    requested != snapshot.Url && read["result"]?["acquisition"]?["navigation"]?["url"]?.ToString() != snapshot.Url)
                    findings.Add("redirect_without_producer_evidence");
                else
                {
                    visits.Add(snapshot);
                    visitEvents.Add(new JsonObject { ["event_index"] = index, ["redirected"] = requested != snapshot.Url });
                }
            }
            else if (requested is not null && visits.Count > 0) findings.Add("navigation_not_offered_by_observations");
            if (snapshot is not null)
                foreach (var record in snapshot.Records)
                    if (record?["href"]?.ToString() is { } href && HttpUrl(href)) observedUrls.Add(href);
        }
        if (rows.Length < 2 || rows.Length > maximum + 1 || visits.Count != rows.Length - 1)
            findings.Add("product_visits_do_not_match_rows");
        var headers = rows.FirstOrDefault()?.Select(Normalize).ToArray() ?? [];
        var name = Array.FindIndex(headers, h => h is "nom" or "name" or "nom du produit" or "product name");
        var description = Array.FindIndex(headers, h => h == "description");
        var price = Array.FindIndex(headers, h => h is "prix" or "price");
        var error = Array.FindIndex(headers, h => h is "error" or "erreur");
        var rowEvidence = new JsonArray();
        if (name < 0 || description < 0 || price < 0) findings.Add("workbook_columns_missing");
        else for (var index = 0; index < Math.Min(visits.Count, rows.Length - 1); index++)
        {
            var row = rows[index + 1]; var visit = visits[index];
            if (row.Length <= new[] { name, description, price, error }.Max())
            { findings.Add("workbook_row_shape_or_product_bound"); continue; }
            var texts = visit.Records.Select(r => r?["text"]?.ToString()).OfType<string>().ToArray();
            var nameMatches = row[name].Length > 0 && texts.Contains(row[name], StringComparer.Ordinal);
            var descriptionMatches = row[description].Length > 0 && texts.Contains(row[description], StringComparer.Ordinal);
            // A displayed price may be an exact substring of a larger observed
            // label. No culture, whitespace or punctuation substitutions.
            var priceMatches = row[price].Length > 0 && texts.Any(t => ExactSegment(t, row[price]));
            if (!nameMatches) findings.Add("workbook_name_not_exact_observed_value");
            if (!descriptionMatches) findings.Add("workbook_description_not_one_observed_block");
            if (!priceMatches) findings.Add("workbook_price_not_exact_observed_value");
            if (new[] { name, description, price }.Any(i => row[i].Length == 0)) findings.Add("workbook_absence_not_independently_established");
            if (error >= 0 && row[error].Length > 0 &&
                visit.Read["result"]?["error_message"]?.ToString() != row[error]) findings.Add("workbook_error_not_producer_failure");
            rowEvidence.Add(new JsonObject { ["row"] = index + 1, ["name_exact"] = nameMatches,
                ["description_block_exact"] = descriptionMatches, ["price_exact"] = priceMatches,
                // Classification only: this never makes a non-exact value pass.
                ["description_in_combined_observation"] = row[description].Length > 0 && Normalize(visit.Content).Contains(Normalize(row[description]), StringComparison.Ordinal) });
        }
        return new JsonObject { ["version"] = ObservedProductsVersion, ["passed"] = findings.Count == 0,
            ["visit_count"] = visits.Count, ["visits"] = visitEvents, ["rows"] = rowEvidence,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }

    // The input and complete search snapshot define the expected products.
    // Generated workbook rows and selected visit order never define this oracle.
    internal static JsonObject VerifyAmazonSelection(IReadOnlyList<JsonObject> events, int maximum, string? query)
    {
        var findings = new List<string>();
        var complete = CompleteSnapshots(events).ToDictionary(s => s.Read);
        Snapshot? search = null;
        var searchIndex = -1;
        for (var index = 0; index < events.Count; index++)
        {
            var read = events[index];
            if (read["tool"]?.ToString() != "browser_get_content" || read["arguments"]?["url"]?.ToString() is not { } requested ||
                !AmazonAddress(requested, out var address) || address.AbsolutePath != "/s") continue;
            if (string.IsNullOrWhiteSpace(query) || SearchQuery(address) != query) findings.Add("search_query_does_not_match_input");
            if (!complete.TryGetValue(read, out var snapshot) || !AmazonAddress(snapshot.Url, out var actual) ||
                actual.AbsolutePath != "/s" || SearchQuery(actual) != query || snapshot.Records.Count == 0 ||
                read["result"]?["success"]?.GetValue<bool>() != true)
            { findings.Add("search_observation_not_independently_complete"); continue; }
            if (search is null) { search = snapshot; searchIndex = index; }
        }
        if (search is null) findings.Add("requested_search_not_observed");
        var expected = new List<(string Identity, string Group, List<string> Urls)>();
        if (search is not null)
        {
            var links = search.Records.OfType<JsonObject>()
                .Where(r => r["href"]?.ToString() is { } href && ProductIdentity(href) is not null)
                .Select(r => (Record: r, Identity: ProductIdentity(r["href"]!.ToString())!, Group: r["group"]?.ToString() ?? "")).ToArray();
            var groups = links.Where(l => l.Group.Length > 0).GroupBy(l => l.Group, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(l => l.Identity).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
            foreach (var link in links)
            {
                var same = expected.FindIndex(e => e.Identity == link.Identity && e.Group == link.Group);
                if (same >= 0)
                {
                    // Multiple anchors for one product are aliases only when
                    // original DOM grouping separates that single-product card.
                    // A broad/mixed group cannot establish result occurrences.
                    var semanticItem = Regex.IsMatch(link.Group, @"(?:^| > )(?:article|li|tr):nth-of-type\([0-9]+\)$",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (link.Group.Length == 0 || groups[link.Group] != 1 || !semanticItem ||
                        link.Group == search.Read["arguments"]?["selector"]?.ToString())
                        findings.Add("search_product_occurrences_ambiguous");
                    else if (!expected[same].Urls.Contains(link.Record["href"]!.ToString(), StringComparer.Ordinal))
                        expected[same].Urls.Add(link.Record["href"]!.ToString());
                    continue;
                }
                expected.Add((link.Identity, link.Group, [link.Record["href"]!.ToString()]));
            }
            if (search.Records.Any(r => r?["href"]?.ToString() is { } href && AmazonAddress(href, out var uri) &&
                (uri.AbsolutePath.Contains("sspa/click", StringComparison.Ordinal) || uri.AbsolutePath.Contains("slredirect", StringComparison.Ordinal))))
                findings.Add("search_product_redirect_identity_unverified");
            if (search.Records.Any(r => r?["href"]?.ToString() is { } href && AmazonAddress(href, out var uri) &&
                (uri.AbsolutePath.Contains("/dp/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/gp/product/", StringComparison.Ordinal)) &&
                ProductIdentity(href) is null))
                findings.Add("search_product_identity_unverified");
            if (expected.Count == 0) findings.Add("search_product_identities_unverified");
        }
        var selected = expected.Take(maximum).ToArray();
        if (events.Take(Math.Max(0, searchIndex)).Any(e => e["tool"]?.ToString() == "browser_get_content" &&
            ProductIdentity(e["arguments"]?["url"]?.ToString()) is not null)) findings.Add("product_visit_before_search");
        var visits = events.Select((e, i) => (Event: e, Index: i)).Where(p => p.Index > searchIndex &&
            p.Event["tool"]?.ToString() == "browser_get_content" && p.Event["arguments"]?["url"] is not null &&
            !(AmazonAddress(p.Event["arguments"]!["url"]!.ToString(), out var uri) && uri.AbsolutePath == "/s")).ToArray();
        if (visits.Length != selected.Length || selected.Length == 0) findings.Add("first_product_visits_incomplete");
        for (var index = 0; index < Math.Min(visits.Length, selected.Length); index++)
        {
            var visit = visits[index]; var expectedProduct = selected[index];
            if (visit.Index <= searchIndex || !expectedProduct.Urls.Contains(visit.Event["arguments"]!["url"]!.ToString(), StringComparer.Ordinal) ||
                !complete.TryGetValue(visit.Event, out var snapshot) || ProductIdentity(snapshot.Url) != expectedProduct.Identity)
                findings.Add("first_product_identity_or_order_mismatch");
        }
        return new JsonObject { ["version"] = AmazonBusinessVersion, ["passed"] = findings.Count == 0,
            ["expected_product_count"] = selected.Length, ["visited_product_count"] = visits.Length,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
    }

    private static bool AmazonAddress(string? value, out Uri address)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.UserInfo.Length == 0 &&
            parsed.Host is "amazon.fr" or "www.amazon.fr") { address = parsed; return true; }
        address = null!; return false;
    }

    private static string? SearchQuery(Uri address)
    {
        var values = address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .Where(p => WebUtility.UrlDecode(p[0]) == "k").ToArray();
        return values.Length == 1 && values[0].Length == 2 ? WebUtility.UrlDecode(values[0][1]) : null;
    }

    private static string? ProductIdentity(string? value)
    {
        if (!AmazonAddress(value, out var address)) return null;
        var parts = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = parts.Length > 1 && parts[0] == "dp" ? 0 : parts.Length > 2 && parts[1] == "dp" && parts[0] != "gp" ? 1 : -1;
        if (index < 0 && parts.Length > 2 && parts[0] == "gp" && parts[1] == "product") index = 1;
        if (index < 0 || index + 1 >= parts.Length) return null;
        var identity = parts[index + 1];
        return identity.Length == 10 && identity.All(char.IsAsciiLetterOrDigit) ? identity : null;
    }

    private static bool HttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool ExactSegment(string text, string value)
    {
        for (var start = 0; start <= text.Length - value.Length;)
        {
            var at = text.IndexOf(value, start, StringComparison.Ordinal);
            if (at < 0) break;
            var end = at + value.Length;
            if ((at == 0 || !Part(text[at - 1])) && (end == text.Length || !Part(text[end]))) return true;
            start = at + 1;
        }
        return false;
        static bool Part(char character) => char.IsLetterOrDigit(character) || character is ',' or '.';
    }

    private static string CellText(Cell cell, WorkbookPart workbook) => cell.DataType?.Value == CellValues.SharedString
        ? workbook.SharedStringTablePart!.SharedStringTable!.Elements<SharedStringItem>().ElementAt(int.Parse(cell.InnerText, CultureInfo.InvariantCulture)).InnerText : cell.InnerText;
    private static string Normalize(string value) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))), "\\s+", " ").Trim().ToLowerInvariant();
    private static bool PriceObserved(string price, string text) => text.Contains(price, StringComparison.Ordinal) ||
        text.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal).Contains(price.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
}
