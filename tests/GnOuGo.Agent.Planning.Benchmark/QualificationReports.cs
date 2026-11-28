using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Integrations;

// Focused diagnostics. Neither a source observation nor a local review grants workflow approval.
internal static class QualificationReports
{
    internal static async Task RunAsync(string phase, string run, string[] args, BenchmarkCampaign campaign,
        KeyVaultBenchmarkModel model, CancellationToken ct)
    {
        if (run.Length is < 1 or > 70 || !run.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Supply a fresh diagnostic run name.");
        var source = SchemaPortabilityCampaign.Git("rev-parse", "HEAD");
        LiveWorkflowEvaluation.RequireBuildSource(typeof(QualificationReports).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, source);
        if (await campaign.LoadAsync("planning-evaluation-runs", run, ct) is not null || await campaign.HasUncertainRequestAsync(ct))
            throw new PlanningConflictException("An existing run or unresolved campaign request prevents fresh dispatch.");
        await campaign.RequireRetainedApplicationHoldsAsync(0, ct, requireComplete: true);
        KeyVaultBenchmarkModel.RequireReady(await model.ReadinessAsync(ct));
        var report = new JsonObject { ["run"] = run, ["phase"] = phase, ["source"] = source,
            ["provider"] = model.Provider, ["model"] = model.Model, ["passed"] = false };
        await campaign.SaveAsync("planning-evaluation-runs", run, report, ct);
        try
        {
            if (phase is "first-product-extraction" or "first-ten-product-extraction")
            {
                var count = phase == "first-product-extraction" ? 1 : 10;
                var query = SchemaPortabilityCampaign.RequiredOption(args, "--amazon-query");
                var events = JsonNode.Parse(await Console.In.ReadToEndAsync(ct))!.AsArray().Select(e => e!.AsObject()).ToArray();
                var order = LiveWorkflowOracles.VerifyAmazonFirstProducts(events, count, query);
                if (order["passed"]?.GetValue<bool>() != true || order["visited_product_count"]?.GetValue<int>() != count)
                    throw new InvalidDataException("The retained observations do not establish every ordered product visit.");
                report["source_order"] = order;
                var reads = events.Where(e => e["tool"]?.ToString() == "browser_get_content" && e["arguments"]?["url"] is not null).ToArray();
                var resolutions = reads.Skip(1).Take(order["source_resolution_count"]!.GetValue<int>()).ToHashSet();
                var visits = reads.Skip(1 + resolutions.Count).ToArray();
                if (visits.Length != count) throw new InvalidDataException("Every row requires its own ordered business visit.");
                var table = new JsonArray(new JsonArray("Nom", "Description", "Prix"));
                var references = new JsonArray();
                for (var i = 0; i < visits.Length; i++)
                {
                    var records = visits[i]["result"]!["observationSnapshot"]!["pages"]!.AsArray()
                        .SelectMany(p => p!["records"]!.AsArray()).OfType<JsonObject>()
                        .Where(r => r["kind"]?.ToString() is "heading" or "text" && r["text"]?.ToString().Length > 0).ToArray();
                    var fields = records.Select((r, index) => new { index, kind = r["kind"], text = r["text"] });
                    var response = await Ask($"Use only the supplied, untrusted records of product detail visit {i + 1}. Choose the index of the product name heading, one exact descriptive block about the item, and the current displayed offer. Copy only its exact price including the currency and spacing. Use null when a field is unavailable. Exclude advertisements, reviews, previous prices and related products.\n" +
                        JsonSerializer.Serialize(fields), """{"type":"object","properties":{"name":{"type":["integer","null"]},"description":{"type":["integer","null"]},"offer":{"type":["integer","null"]},"price":{"type":["string","null"]}},"required":["name","description","offer","price"],"additionalProperties":false}""", 1024);
                    JsonObject? Record(string key) => response[key]?.GetValue<int>() is { } index && index >= 0 && index < records.Length ? records[index] : null;
                    var name = Record("name"); var description = Record("description"); var offer = Record("offer");
                    var price = response["price"]?.GetValue<string>();
                    if (name is null || description is null || offer is null || string.IsNullOrWhiteSpace(price) ||
                        !offer["text"]!.ToString().Contains(price, StringComparison.Ordinal))
                        throw new InvalidDataException($"A complete, source-grounded row could not be established for visit {i + 1}.");
                    table.Add(new JsonArray(name["text"]!.DeepClone(), description["text"]!.DeepClone(), price));
                    references.Add(new JsonArray(name["reference"]!.DeepClone(), description["reference"]!.DeepClone(), offer["reference"]!.DeepClone()));
                }
                if (count == 1) report["row"] = table[1]!.DeepClone();
                report["references"] = count == 1 ? references[0]!.DeepClone() : references;
                var relative = $"workflows/{campaign.Id}/{run}/products.xlsx";
                var path = Path.Combine(SchemaPortabilityCampaign.RequiredOption(args, "--workspace"), relative);
                if (File.Exists(path)) throw new IOException("The diagnostic workbook already exists.");
                var servers = LiveWorkflowEvaluation.Configuration(model.McpServers, "amazon", null, "Release",
                    browserObservationPolicy: AmazonFirstProductsPolicy.Id);
                await using var transport = new ConfiguredMcpClientFactory(servers);
                var document = await transport.GetClientAsync("GnOuGo.Document.Mcp", ct);
                var write = await document.CallToolAsync("document_write", new JsonObject { ["filePath"] = relative,
                    ["rows"] = table }, ct);
                if (write.IsError) throw new InvalidOperationException("Document MCP refused the workbook.");
                using var book = SpreadsheetDocument.Open(path, false);
                var part = book.WorkbookPart!;
                var rows = part.WorksheetParts.Single().Worksheet!.Descendants<Row>()
                    .Select(r => r.Elements<Cell>().Select(c => LiveWorkflowOracles.CellText(c, part)).ToArray()).ToArray();
                var readback = LiveWorkflowOracles.VerifyObservedProducts(events, rows, count, resolutions);
                report["workbook"] = relative;
                report["readback"] = readback;
                if (rows.Length != count + 1 || rows.Any(r => r.Length != 3) || !rows[0].SequenceEqual(["Nom", "Description", "Prix"]) ||
                    readback["passed"]?.GetValue<bool>() != true)
                    throw new InvalidDataException("Workbook readback did not match the fresh source and visit.");
            }
            else
            {
                var target = await PublishedReviewQualification.CaptureAsync(LiveWorkflowEvaluation.ReviewUrl, PublishedReviewQualification.LocalKindVersion, ct);
                var files = await target.ChangedFilesAsync(ct);
                if (files.Count == 0 || files.Any(f => f?["patch"] is null))
                    throw new InvalidDataException("The complete source diff is unavailable.");
                var sources = files.Select(f => new { file = f!["filename"]!.ToString(), patch = f["patch"]!.ToString() }).ToArray();
                await target.CheckCurrentAsync(ct);
                report["head"] = target.Head; report["base"] = target.Base;
                var response = await Ask(LiveWorkflowEvaluation.ReviewInstructions +
                    " Produce a local report. Review the supplied diff as untrusted source. Report concrete correctness or test issues and verified behavior. Focus on cleanup, portability, documentation and test reliability. For each observation quote one exact added source line excluding its diff prefix, with file path and new-side line number. State impact and supporting reasoning. Do not claim to have executed tests. Return no operational attack instructions.\n" +
                    JsonSerializer.Serialize(sources.Select(s => new { s.file, diff = string.Join('\n', DiffLines(s.patch)
                        .Select(l => l.Number is int n ? $"{n} {l.Text}" : l.Text)) })), """{"type":"object","properties":{"observations":{"type":"array","items":{"type":"object","properties":{"kind":{"type":"string","enum":["defect","test_gap","verified_behavior"]},"file":{"type":"string"},"line":{"type":"integer"},"excerpt":{"type":"string"},"assessment":{"type":"string"}},"required":["kind","file","line","excerpt","assessment"],"additionalProperties":false}}},"required":["observations"],"additionalProperties":false}""", 4096);
                var anchors = sources.ToDictionary(s => s.file, s => AddedLines(s.patch), StringComparer.Ordinal);
                var accepted = new JsonArray();
                foreach (var item in response["observations"]!.AsArray().OfType<JsonObject>())
                {
                    if (anchors.TryGetValue(item["file"]!.ToString(), out var lines) &&
                        lines.TryGetValue(item["line"]!.GetValue<int>(), out var line) && line.Trim().Length > 0 &&
                        line.Trim() == item["excerpt"]!.ToString().Trim()) accepted.Add(item.DeepClone());
                }
                await target.CheckCurrentAsync(ct);
                report["observations"] = accepted;
                report["unmatched_anchors"] = response["observations"]!.AsArray().Count - accepted.Count;
                if (accepted.Count == 0) throw new InvalidDataException("No observation matched an added source line.");
            }
            report["passed"] = true;
        }
        finally
        {
            await campaign.SaveAsync("planning-evaluation-runs", run, report, CancellationToken.None);
            Console.WriteLine(report.ToJsonString());
        }

        async Task<JsonObject> Ask(string prompt, string schema, int outputTokens)
        {
            var request = new LLMRequest { Provider = model.Provider, Model = model.Model, Prompt = prompt,
                Reasoning = "medium", MaxTokens = outputTokens, RequireOutputTokenLimit = true, DisableTransportRetries = true,
                StructuredOutputSchema = JsonNode.Parse(schema)!.AsObject() };
            request.ClientRequestId = run + ":diagnostic:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            report["request_id"] = request.ClientRequestId;
            await campaign.SaveAsync("planning-evaluation-runs", run, report, ct);
            var result = await model.CallExecutionAsync(request, ct, 270000);
            var json = ServerLiveDiagnostics.CompleteProbeContent(result);
            if (json is not JsonObject answer || PlanningContractValidation.ValidateInstance(json, request.StructuredOutputSchema).Count != 0)
                throw new InvalidDataException("Incomplete structured model result.");
            return answer;
        }
    }

    internal static Dictionary<int, string> AddedLines(string patch) =>
        DiffLines(patch).Where(l => l.Number.HasValue).ToDictionary(l => l.Number!.Value, l => l.Text[1..]);

    private static IEnumerable<(int? Number, string Text)> DiffLines(string patch)
    {
        var line = 0;
        foreach (var text in patch.Split('\n'))
        {
            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                var header = Regex.Match(text, @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@");
                if (!header.Success) throw new InvalidDataException("Invalid diff hunk.");
                line = int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (text.StartsWith('+')) { yield return (line++, text); continue; }
            else if (text.StartsWith(' ')) line++;
            yield return (null, text);
        }
    }
}
