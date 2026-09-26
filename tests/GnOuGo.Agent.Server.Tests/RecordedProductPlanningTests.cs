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

public sealed class RecordedProductPlanningTests
{
    private static JsonObject Recording(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProductPlanning", name + ".json")))!.AsObject();

    [Fact]
    public async Task ExactPromptOutputExhaustionRetainsAllThreeResponsesWithoutAnotherCall()
    {
        var recording = Recording("exact-prompt-output-limit");
        Assert.Equal(RealProductContracts.Prompt, recording["prompt"]!.ToString());
        var root = Path.GetTempPath();
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root)));
        var replay = new Replay(recording);
        var session = await Plan(factory, replay);
        Assert.Equal(PlanningStatus.Stopped, session.Status);
        Assert.Contains(session.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Equal(3, session.ModelCalls); Assert.Equal(3, replay.Calls); Assert.Equal(0, session.ReplanAttempts);
        Assert.Null(session.Plan); Assert.Null(session.Yaml); Assert.Equal(0, factory.InvocationAttempts);
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
    public async Task CapturedModelPlanBindsProductQueriesAndWritesActualXlsx(string variant, string query)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-recorded-product-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root);
            var metadata = RealProductContracts.Capture(policy);
            // Retain the configured source names from the captured conversation.
            var factory = new RealProductContracts.Factory(new() { ["GnOuGo.Browser.Mcp"] = metadata["browser"]!.DeepClone(), ["GnOuGo.Document.Mcp"] = metadata["document"]!.DeepClone() });
            var replay = new Replay(Recording("retained-complete"));
            var session = await Plan(factory, replay);
            Assert.Equal(PlanningStatus.FinalReview, session.Status);
            Assert.Equal(2, replay.Calls); Assert.Equal(2, session.Discovery.Pages.Count);
            Assert.Equal(0, factory.InvocationAttempts);
            PlanningArtifactApproval.Verify(session);
            var changed = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            changed.Plan!.Root.Outputs.Single(o => o.Name == "fichierExcel").Value.Port = "filePath";
            Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(changed));
            Assert.All(replay.Requests, r => Assert.True((System.Text.Encoding.UTF8.GetByteCount(r.Prompt) + System.Text.Encoding.UTF8.GetByteCount(r.StructuredOutputSchema!.ToJsonString())) / 3d + 1024 < 24000));

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var model = new Observations(variant, cancellation);
            var writer = new DocumentTools(new DocumentOperationHost(policy, NullLogger<DocumentOperationHost>.Instance), NullLogger<DocumentTools>.Instance);
            var effects = new List<string>(); var visited = new List<string>();
            factory.Handler = (_, tool, args, ct) =>
            {
                effects.Add(tool);
                JsonNode? value;
                switch (tool)
                {
                    case "document_get_policy": value = JsonSerializer.SerializeToNode(writer.GetPolicy(), RealProductContracts.Json); break;
                    case "browser_get_content":
                        var url = args!["url"]!.ToString(); visited.Add(url);
                        Assert.Equal("html", args["format"]!.ToString());
                        var html = visited.Count == 1 ? model.SearchHtml : model.Page(url);
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
            var resultRun = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["articleARechercher"] = query }, cancellation.Token);
            if (variant == "denied") { Assert.False(resultRun.Success); Assert.Empty(effects); Assert.Equal(0, model.Calls); return; }
            Assert.Equal("https://www.amazon.fr/s?k=" + Uri.EscapeDataString(query), visited[0]);
            Assert.Equal("browser_close", effects[^1]);
            if (variant is "invalid" or "cancelled" or "write_failed") { Assert.False(resultRun.Success); Assert.False(File.Exists(Path.Combine(root, Observations.RelativeFile))); return; }
            Assert.True(resultRun.Success, resultRun.Error?.Message);
            Assert.Equal(Observations.RelativeFile, resultRun.Outputs!["fichierExcel"]!.ToString());
            Assert.Equal(model.ExpectedProductUrls, visited.Skip(1));
            using var workbook = SpreadsheetDocument.Open(Path.Combine(root, Observations.RelativeFile), false);
            var rows = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            var expected = variant switch
            {
                "empty" => new[] { new[] { "nom", "description", "prix" } },
                "changed" => [["nom", "description", "prix"], ["Changed shoe", "Changed description", "49,90 €"]],
                "missing" => [["nom", "description", "prix"], ["Only a name", "", ""]],
                _ => [["nom", "description", "prix"], ["Chaussure été, \"A\"", "Light and comfortable", "89,99 €"], ["東京", "Line one Line two", "95.00"]]
            };
            Assert.Equal(expected.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++) Assert.Equal(expected[i], rows[i]);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<PlanningSession> Plan(RealProductContracts.Factory factory, Replay replay)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory, LLMClient = replay }, (_, _) => Task.CompletedTask);
        var session = new PlanningSession { Request = new() { TenantId = "test", Prompt = RealProductContracts.Prompt, Mode = PlanningMode.Auto, Generation = new() { MaxInputTokensPerRequest = 24000 } } };
        for (var i = 0; i < 10 && !PlanningStatus.IsTerminal(session.Status) && !PlanningStatus.IsWaiting(session.Status); i++)
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, runtime, TestContext.Current.CancellationToken);
        return session;
    }
    private sealed class Replay(JsonObject recording) : ILLMClient
    {
        public int Calls { get; private set; }
        public List<LLMRequest> Requests { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var responses = recording["responses"]!.AsArray();
            if (Calls >= responses.Count) throw new InvalidOperationException("No replacement or synthesized model response is allowed.");
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
            if (fields.ContainsKey("urlRechercheAmazon")) output = new() { ["requete"] = data["articleARechercher"]!.ToString(), ["urlRechercheAmazon"] = "https://www.amazon.fr/s?k=" + Uri.EscapeDataString(data["articleARechercher"]!.ToString()), ["cheminFichierExcel"] = RelativeFile, ["plafondArticles"] = 20 };
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
