using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Browser.Mcp;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedProductPlanningTests(ITestOutputHelper output)
{
    private static JsonObject Recording(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProductPlanning", name + ".json")))!.AsObject();

    [Fact]
    public async Task ExactPromptOutputExhaustionRetainsAllThreeResponsesWithoutAnotherCall()
    {
        var recording = Recording("exact-prompt-output-limit");
        Assert.Equal(RealProductContracts.Prompt, recording["prompt"]!.ToString());
        var root = Path.GetTempPath();
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root)));
        var replay = new Replay(recording, Recording("exact-prompt-output-limit-schemas"));
        var session = await Plan(factory, replay);
        Assert.Equal(PlanningStatus.Stopped, session.Status);
        Assert.Contains(session.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Equal(3, session.ModelCalls); Assert.Equal(3, replay.Calls); Assert.Equal(0, session.ReplanAttempts);
        Assert.Null(session.Plan); Assert.Null(session.Yaml); Assert.Equal(0, factory.InvocationAttempts);
    }

    [Fact]
    public void SyntheticCompactPlanHasSerializationHeadroomWithoutDroppingBusinessBehavior()
    {
        var recording = Recording("synthetic-compact");
        Assert.Equal(RealProductContracts.Prompt, recording["prompt"]!.ToString());
        Assert.Contains("Synthetic", recording["provenance"]!.ToString());
        var compact = recording["responses"]![1]!;
        var retained = Recording("retained-complete")["responses"]![1]!["json"]!;
        static int Bytes(JsonNode value) => System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString(new()
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.True(Bytes(compact["plan"]!) <= Bytes(retained["plan"]!) * 0.6);
        Assert.True((Bytes(compact) + 2) / 3 + 256 <= 32768 / 4);
        output.WriteLine($"UTF-8 minified bytes: retained plan={Bytes(retained["plan"]!)}, compact plan={Bytes(compact["plan"]!)}, retained response={Bytes(retained)}, compact response={Bytes(compact)}; conservative response tokens={(Bytes(compact) + 2) / 3 + 256}");
        var plan = compact["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var tasks = plan.Root.Tasks.Concat(plan.Root.Tasks.Single(t => t.Kind == "foreach").Body!.Tasks).Concat(plan.Root.Always).ToArray();
        Assert.Equal(11, tasks.Length); Assert.Equal(4, tasks.Count(t => t.Kind == "transform"));
        Assert.True(Assert.Single(plan.Inputs).Required); Assert.Equal(2, plan.Root.Outputs.Count);
        Assert.Equal("input", tasks.Single(t => t.Id == "search").Inputs.Single(i => i.Name == "value").Value.Kind);
        Assert.Equal("item", tasks.Single(t => t.Id == "page").Inputs.Single(i => i.Name == "url").Value.Kind);
        Assert.DoesNotContain(tasks, t => t.Kind == "value");
    }

    [Theory]
    [InlineData("nominal", "chaussure geox homme 45")]
    [InlineData("changed", "baskets été homme 42")]
    [InlineData("empty", "chaussure geox homme 45")]
    [InlineData("missing", "chaussure geox homme 45")]
    [InlineData("denied", "chaussure geox homme 45")]
    [InlineData("invalid", "chaussure geox homme 45")]
    [InlineData("cancelled", "chaussure geox homme 45")]
    [InlineData("write_failed", "chaussure geox homme 45")]
    [InlineData("nominal", "chaussure geox homme 45", true)]
    [InlineData("changed", "baskets été homme 42", true)]
    [InlineData("empty", "chaussure geox homme 45", true)]
    [InlineData("missing", "chaussure geox homme 45", true)]
    [InlineData("denied", "chaussure geox homme 45", true)]
    [InlineData("invalid", "chaussure geox homme 45", true)]
    [InlineData("cancelled", "chaussure geox homme 45", true)]
    [InlineData("write_failed", "chaussure geox homme 45", true)]
    public async Task CapturedOrSyntheticPlanBindsProductQueriesAndWritesActualXlsx(string variant, string query, bool compact = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-recorded-product-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root);
            var metadata = RealProductContracts.Capture(policy);
            if (compact) { metadata["archive"] = new JsonArray(); metadata["calendar"] = new JsonArray(); }
            // Retain the configured source names from the captured conversation.
            var factory = new RealProductContracts.Factory(compact ? metadata : new() { ["GnOuGo.Browser.Mcp"] = metadata["browser"]!.DeepClone(), ["GnOuGo.Document.Mcp"] = metadata["document"]!.DeepClone() });
            var replay = compact ? new Replay(Recording("synthetic-compact")) : new Replay(Recording("retained-complete"), Recording("retained-complete-schemas"));
            var session = await Plan(factory, replay);
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join("; ", session.Diagnostics.Select(d => d.Code + ": " + d.Location + ": " + d.Message)));
            Assert.Equal(2, replay.Calls); Assert.Equal(2, session.Discovery.Pages.Count);
            if (compact) Assert.Equal(new[] { "browser", "document" }, factory.DiscoveryReads.Order(StringComparer.Ordinal));
            Assert.Equal(0, factory.InvocationAttempts);
            PlanningArtifactApproval.Verify(session);
            var changed = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            changed.Plan!.Root.Outputs.Single(o => o.Name == (compact ? "file" : "fichierExcel")).Value.Port = "filePath";
            Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(changed));
            foreach (var request in replay.Requests)
            {
                var promptBytes = System.Text.Encoding.UTF8.GetByteCount(request.Prompt);
                var schemaBytes = System.Text.Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString());
                var estimate = (promptBytes + schemaBytes + 2) / 3 + 256;
                Assert.InRange(estimate, 1, 24000);
                if (compact) output.WriteLine($"Complete request: prompt bytes={promptBytes}, schema bytes={schemaBytes}, conservative input tokens={estimate}");
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var model = new Observations(variant, cancellation);
            var writer = new DocumentTools(new DocumentOperationHost(policy, NullLogger<DocumentOperationHost>.Instance), NullLogger<DocumentTools>.Instance);
            var effects = new List<string>(); var visited = new List<string>(); string? searched = null;
            var relativeFile = compact ? "workflows/product-search/products.xlsx" : Observations.RelativeFile;
            factory.Handler = (_, tool, args, ct) =>
            {
                effects.Add(tool);
                JsonNode? value;
                switch (tool)
                {
                    case "document_get_policy": value = JsonSerializer.SerializeToNode(writer.GetPolicy(), RealProductContracts.Json); break;
                    case "browser_fill":
                        Assert.True(compact); Assert.Equal("#search", args!["selector"]!.ToString());
                        searched = args["value"]!.ToString(); Assert.Equal(query, searched); Assert.True(args["submit"]!.GetValue<bool>());
                        value = JsonSerializer.SerializeToNode(new BrowserActionResult("fill", "https://www.amazon.fr/s?k=" + Uri.EscapeDataString(searched), "Search", "#search", true, true, "navigation"), RealProductContracts.Json);
                        break;
                    case "browser_get_content":
                        var url = args!["url"]?.ToString() ?? "https://www.amazon.fr/s?k=" + Uri.EscapeDataString(searched ?? throw new InvalidOperationException("Search was not submitted.")); visited.Add(url);
                        Assert.Equal("html", args["format"]!.ToString());
                        var html = compact && visited.Count == 1 ? "<html><input id='search' name='query'/></html>" : visited.Count == (compact ? 2 : 1) ? model.SearchHtml : model.Page(url);
                        value = JsonSerializer.SerializeToNode(new BrowserContentResult(url, "fixture", 200, null, "body", false, null, "html", html, false, html.Length), RealProductContracts.Json);
                        break;
                    case "document_write":
                        var result = writer.Write(variant == "write_failed" ? "../forbidden.xlsx" : args!["filePath"]!.ToString(), args!["content"]!.ToString());
                        return Task.FromResult(new McpCallResult { IsError = !result.Success, Content = JsonSerializer.SerializeToNode(result, RealProductContracts.Json) });
                    case "browser_close": value = new JsonObject { ["closed"] = true, ["success"] = true, ["ok"] = true, ["error_code"] = null, ["error_message"] = null }; break;
                    default: throw new InvalidOperationException("Unexpected capability: " + tool);
                }
                return Task.FromResult(new McpCallResult { Content = value });
            };
            var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = model, LlmDefaults = new() { Model = "mock" }, HumanInputProvider = new PlanningCorpus.Human(variant != "denied") };
            var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!));
            var resultRun = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { [compact ? "product" : "articleARechercher"] = query }, cancellation.Token);
            if (variant == "denied") { Assert.False(resultRun.Success); Assert.Empty(effects); Assert.Equal(0, model.Calls); return; }
            Assert.Equal("https://www.amazon.fr/s?k=" + Uri.EscapeDataString(query), visited[compact ? 1 : 0]);
            Assert.Equal("browser_close", effects[^1]);
            if (variant is "invalid" or "cancelled" or "write_failed") { Assert.False(resultRun.Success); Assert.False(File.Exists(Path.Combine(root, relativeFile))); return; }
            Assert.True(resultRun.Success, resultRun.Error?.Message);
            Assert.Equal(relativeFile, resultRun.Outputs![compact ? "file" : "fichierExcel"]!.ToString());
            Assert.Equal(model.ExpectedProductUrls, visited.Skip(compact ? 2 : 1));
            using var workbook = SpreadsheetDocument.Open(Path.Combine(root, relativeFile), false);
            var rows = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            var expected = variant switch
            {
                "empty" => new[] { new[] { "nom", "description", "prix" } },
                "changed" => [["nom", "description", "prix"], ["Changed shoe", "Changed description", "49,90 €"]],
                "missing" => [["nom", "description", "prix"], ["Only a name", "", ""]],
                _ => [["nom", "description", "prix"], ["Chaussure été, \"A\"", "Light and comfortable", "89,99 €"], ["東京", "Line one Line two", "95.00"]]
            };
            if (compact) expected[0] = ["name", "description", "price"];
            Assert.Equal(expected.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++) Assert.Equal(expected[i], rows[i]);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<PlanningSession> Plan(RealProductContracts.Factory factory, Replay replay)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory, LLMClient = replay }, (_, _) => Task.CompletedTask);
        var session = new PlanningSession { Request = new() { TenantId = "test", Prompt = RealProductContracts.Prompt, Mode = PlanningMode.Auto, Generation = new() { MaxInputTokensPerRequest = 24000, MaxOutputTokens = 32768 } } };
        string? accepted = null;
        for (var i = 0; i < 10 && !PlanningStatus.IsTerminal(session.Status) && !PlanningStatus.IsWaiting(session.Status); i++)
        {
            // Simulate recovery of an already dispatched historical request. Replay
            // its original schema and response; never project old data onto today's schema.
            if (replay.Schemas is { } schemas)
            {
                var id = "historical-replay:" + ++session.ModelCalls;
                session.PendingCall = new() { Id = id, Purpose = "tasks", Request = new() { ClientRequestId = id,
                    StructuredOutputSchema = schemas[replay.Calls]!.DeepClone() } };
            }
            session = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, runtime, TestContext.Current.CancellationToken);
            var requirements = JsonSerializer.Serialize(session.Requirements, PlanningJsonContext.Default.PlanningRequirements);
            if (accepted is not null) Assert.Equal(accepted, requirements);
            accepted = requirements;
        }
        return session;
    }
    private sealed class Replay(JsonObject recording, JsonObject? schemas = null) : ILLMClient
    {
        public JsonArray? Schemas { get; } = schemas?["schemas"]!.AsArray();
        public int Calls { get; private set; }
        public List<LLMRequest> Requests { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var responses = recording["responses"]!.AsArray();
            if (Calls >= responses.Count) throw new InvalidOperationException("No replacement or synthesized model response is allowed.");
            if (Schemas is null)
            {
                Assert.Equal(RealProductContracts.Prompt, recording["prompt"]!.ToString());
                Assert.Equal(32768, request.MaxTokens);
                Assert.Equal(Calls == 0, request.StructuredOutputSchema!["properties"]!.AsObject().ContainsKey("requirements"));
                return Task.FromResult(new LLMResponse { Json = responses[Calls++]!.DeepClone() });
            }
            Assert.True(JsonNode.DeepEquals(Schemas[Calls], request.StructuredOutputSchema));
            return Task.FromResult(JsonSerializer.Deserialize(responses[Calls++], PlanningJsonContext.Default.LLMResponse)!);
        }
    }

    private sealed class Observations(string variant, CancellationTokenSource cancellation) : ILLMClient
    {
        public const string RelativeFile = "workflows/amazon-recherche-produits/workflow-created/resultats_amazon.xlsx";
        public int Calls { get; private set; }
        public string[] ExpectedProductUrls => variant == "empty" ? [] : variant is "missing" or "changed" ? ["https://www.amazon.fr/dp/one"] : ["https://www.amazon.fr/dp/one", "https://www.amazon.fr/dp/two"];
        public string SearchHtml => "<html>" + string.Concat(ExpectedProductUrls.Select(u => "<a href='" + u + "'>Product</a>")) + "</html>";
        public string Page(string url) => variant == "missing" ? "<html><h1>Only a name</h1></html>" : variant == "changed" ? "<html><h1>Changed shoe</h1><p>Changed description</p><price>49,90 €</price></html>" : url.EndsWith("/one", StringComparison.Ordinal) ? "<html><h1>Chaussure été, &quot;A&quot;</h1><p>Light\tand comfortable</p><price>89,99 €</price></html>" : "<html><h1>東京</h1><p>Line one\nLine two</p><price>95.00</price></html>";
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("Business data (JSON):\n", StringComparison.Ordinal) + "Business data (JSON):\n".Length)..])!;
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            JsonObject output;
            if (fields.ContainsKey("selector"))
            {
                var html = XDocument.Parse(data["html"]!.ToString());
                output = new() { ["selector"] = "#" + html.Descendants("input").Single().Attribute("id")!.Value };
            }
            else if (fields.ContainsKey("urls"))
            {
                if (variant == "invalid") return Task.FromResult(new LLMResponse { Json = new JsonObject { ["urls"] = "invalid" } });
                var html = XDocument.Parse(data["html"]!.ToString());
                output = new() { ["urls"] = new JsonArray(html.Descendants("a").Select(a => (JsonNode?)JsonValue.Create(a.Attribute("href")!.Value)).ToArray()) };
            }
            else if (fields.ContainsKey("name"))
            {
                if (variant == "cancelled") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                var html = XDocument.Parse(data["html"]!.ToString());
                output = new() { ["name"] = html.Descendants("h1").FirstOrDefault()?.Value, ["description"] = html.Descendants("p").FirstOrDefault()?.Value, ["price"] = html.Descendants("price").FirstOrDefault()?.Value };
            }
            else if (fields.ContainsKey("tsv"))
                output = new() { ["tsv"] = "name\tdescription\tprice\n" + string.Join('\n', data["products"]!.AsArray().Select(r => string.Join('\t', new[] { "name", "description", "price" }.Select(k => Regex.Replace(r?[k]?.ToString() ?? "", @"\s+", " "))))) };
            else if (fields.ContainsKey("urlRechercheAmazon")) output = new() { ["requete"] = data["articleARechercher"]!.ToString(), ["urlRechercheAmazon"] = "https://www.amazon.fr/s?k=" + Uri.EscapeDataString(data["articleARechercher"]!.ToString()), ["cheminFichierExcel"] = RelativeFile, ["plafondArticles"] = 20 };
            else if (fields.ContainsKey("listeLisible"))
            {
                if (variant == "invalid") return Task.FromResult(new LLMResponse { Json = new JsonObject { ["articles"] = "invalid" } });
                var html = XDocument.Parse(data["htmlResultats"]!.ToString());
                output = new() { ["articles"] = new JsonArray(html.Descendants("a").Select(a => (JsonNode)new JsonObject { ["urlProduit"] = a.Attribute("href")!.Value, ["nomListe"] = null, ["descriptionListe"] = null, ["prixListe"] = null }).ToArray()), ["listeLisible"] = string.Join('\n', html.Descendants("a").Select(a => a.Value)) };
            }
            else if (fields.ContainsKey("nomSecours")) output = new() { ["urlProduit"] = data["articleCourant"]!["urlProduit"]!.ToString(), ["nomSecours"] = null, ["descriptionSecours"] = null, ["prixSecours"] = null };
            else if (fields.ContainsKey("nom"))
            {
                if (variant == "cancelled") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                var html = XDocument.Parse(data["htmlPageProduit"]!.ToString());
                output = new() { ["nom"] = html.Descendants("h1").FirstOrDefault()?.Value, ["description"] = html.Descendants("p").FirstOrDefault()?.Value, ["prix"] = html.Descendants("price").FirstOrDefault()?.Value, ["urlProduit"] = data["urlProduit"]!.ToString() };
            }
            else if (fields.ContainsKey("contenuXlsxTabule"))
            {
                var records = data["articlesExtraits"]!.AsObject().Single().Value!.AsArray();
                output = new() { ["contenuXlsxTabule"] = "nom\tdescription\tprix\n" + string.Join('\n', records.Select(r => string.Join('\t', new[] { "nom", "description", "prix" }.Select(k => Regex.Replace(r?[k]?.ToString() ?? "", @"\s+", " "))))), ["cheminFichierExcel"] = data["cheminFichierExcel"]!.ToString(), ["nombreArticlesExtraits"] = records.Count };
            }
            else throw new InvalidOperationException("Unexpected transformation contract.");
            return Task.FromResult(new LLMResponse { Json = output, Text = output.ToJsonString() });
        }
    }
}
