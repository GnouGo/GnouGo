using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Document.Mcp;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task RetainedWholeProposalUsesOnlyExistingTypedBindingsAndPreservesRequirements()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new DocumentServerSettings(), AppContext.BaseDirectory)));
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, ct);
        var failed = RetainedProposal("failed", catalog);
        var corrected = RetainedProposal("corrected", catalog);
        Assert.Equal(JsonSerializer.Serialize(failed.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(corrected.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(RetainedOperations(failed.Plan!), RetainedOperations(corrected.Plan!));
        var invalid = new TaskPlanCompiler().Compile(failed.Plan!, catalog);
        Assert.Null(invalid.Graph);
        Assert.Equal(8, invalid.Diagnostics.Count(d => d.Code == "TASK_FIELD_TYPE"));
        var compiled = new TaskPlanCompiler().Compile(corrected.Plan!, catalog);
        Assert.True(compiled.Graph is not null, string.Join('\n', compiled.Diagnostics));
        Assert.Equal(0, factory.InvocationAttempts);
        Assert.Equal("value", corrected.Plan!.Root.Tasks.Single(t => t.Id == "normalize_products").Kind);
        var products = Assert.Single(corrected.Requirements!.Outputs!, o => o.Name == "products").Type;
        Assert.False(products.Items!.Nullable); Assert.Equal(10, products.MaxItems);
        Assert.All(products.Items.Fields.Where(f => f.Name is "name" or "description" or "price" or "url"), f => Assert.True(f.Type.Nullable));
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("absent")]
    [InlineData("unknown-id")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    [InlineData("fabricated")]
    [InlineData("repeated-selection")]
    [InlineData("typed-repair")]
    [InlineData("typed-lookup-repair")]
    public async Task RetainedCompositionVisitsProductsAndWritesIndependentlyCheckedExcel(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var typedRepair = variant is "typed-repair" or "typed-lookup-repair";
        var root = Directory.CreateTempSubdirectory("gnougo-retained-products-").FullName;
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var visits = new List<string>(); var submitted = new List<string>(); var consentSeen = new List<bool>();
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0)); builder.Logging.ClearProviders();
        await using var site = builder.Build();
        site.MapGet("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value!; visits.Add(path);
            var origin = "http://" + context.Request.Host;
            var noise = string.Concat(Enumerable.Range(0, 8).Select(i => "<p>irrelevant-observation-" + i + new string('x', 20) + "</p>"));
            string body;
            if (path == "/")
            {
                body = noise + "<form action='/search' method='get'><input aria-label='Search catalogue' name='q' /><button>Search</button></form>";
                if (variant == "consent" && context.Request.Cookies["fixtureConsent"] != "accepted")
                    body += "<section role='dialog'><p>Cookie preferences</p><button onclick=\"document.cookie='fixtureConsent=accepted;path=/';this.parentNode.remove()\">Accept cookies</button></section>";
            }
            else if (path == "/search")
            {
                submitted.Add(context.Request.Query["q"].ToString());
                consentSeen.Add(context.Request.Cookies["fixtureConsent"] == "accepted");
                // Repeated business values and a sponsored label must not delete or reorder rows.
                body = noise + string.Concat(Enumerable.Range(0, 12).Select(i => "<a href='" + origin + "/product/" + i + "?source=" + new string('z', 250) + "'>" + (i == 1 ? "Sponsored " : "") + "Observed product " + i + "</a>")) + noise;
            }
            else if (path.StartsWith("/product/", StringComparison.Ordinal))
            {
                var i = int.Parse(path[9..], System.Globalization.CultureInfo.InvariantCulture);
                body = "<h1>" + ProductName(i) + "</h1><p>" + ProductDescription(i) + "</p>" + (i == 2 ? "" : "<price>" + ProductPrice(i) + "</price>");
            }
            else { context.Response.StatusCode = 404; body = "Not found"; }
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<html><head><title>Local catalogue</title></head><body>" + body + "</body></html>", ct);
        });
        await site.StartAsync(ct);
        try
        {
            McpServerOptions Server(string name, Dictionary<string, string?> environment) => new()
            {
                Type = "stdio", Command = Environment.GetEnvironmentVariable("GNOU_GO_" + name.ToUpperInvariant() + "_MCP_TEST_EXECUTABLE") ??
                    Path.Combine(AppContext.BaseDirectory, "GnOuGo." + name + ".Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")), EnvironmentVariables = environment
            };
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>
            {
                ["browser"] = Server("Browser", new() { ["Browser__AllowedHosts__0"] = "127.0.0.1", ["Browser__Headless"] = "true", ["Browser__KeepBrowserOpen"] = "false",
                    ["Browser__MaxObservationRecords"] = "2", ["Browser__MaxObservationPages"] = variant == "incomplete" ? "1" : "100", ["Browser__SlowMoMs"] = "0", ["Browser__HoldOpenMs"] = "0", ["Browser__NavigationTimeoutMs"] = "3000", ["OpenTelemetry__Enabled"] = "false" }),
                ["document"] = Server("Document", new() { ["Document__DefaultWorkingDirectory"] = workspace, ["OpenTelemetry__Enabled"] = "false" })
            });
            var model = new RetainedModel(variant);
            var store = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(
                new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners"));
            var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = model, RunStore = store,
                LLMUsageBudget = new(new() { MaxElapsed = TimeSpan.FromMinutes(2) }), HumanInputProvider = new PlanningCorpus.Human(true), LlmDefaults = new() { Model = "deterministic" } };
            engine.Limits.TenantId = "local"; engine.Limits.RunId = "retained-" + variant;
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await RetainedCatalog(runtime, ct);
            var proposal = RetainedProposal("corrected", catalog, site.Urls.Single());
            var expectedPlan = JsonSerializer.Serialize(proposal.Plan, PlanningJsonContext.Default.TaskPlan);
            var corrections = new Dictionary<string, JsonNode?>();
            if (typedRepair)
            {
                // Reproduce the retained typed-binding failures inside the full executable composition.
                var bindings = RetainedTasks(proposal.Plan!.Root).SelectMany(t => t.Outputs.Select(o => (Task: t, Output: o))).ToArray();
                var binding = bindings.First(b => b.Output.Value.Kind == (variant == "typed-repair" ? "flatten" : "lookup"));
                corrections["/tasks/" + binding.Task.Id + "/outputs/" + binding.Output.Name] = JsonSerializer.SerializeToNode(binding.Output.Value, PlanningJsonContext.Default.TaskValue);
                // Test independently: an invalid upstream projection prevents downstream typing.
                if (variant == "typed-repair") binding.Output.Value.Items[0] = new() { Kind = "string", Text = "not an observed array" };
                else binding.Output.Value.Items.Reverse();
            }
            if (variant == "denied") proposal.Plan!.Root.Tasks.Single(t => t.Id == "write_products_xlsx").Inputs.Single(i => i.Name == "filePath").Value.Text = "../outside.xlsx";
            var adapter = new ProposalRuntime(runtime, proposal);
            if (typedRepair) adapter.Patch = request =>
            {
                if (request.StructuredOutputSchema?["properties"]?["patch"] is null)
                    return JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
                var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!["repair"]!;
                Assert.Equal(10, context["version"]!.GetValue<int>());
                var edits = context["slots"]!.AsArray().Select(slot => new JsonObject {
                    ["slot"] = slot!["id"]!.ToString(), ["action"] = "replace", ["value"] = corrections[slot["location"]!.ToString()]!.DeepClone() }).ToArray();
                Assert.Equal(corrections.Count, edits.Length);
                return new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
                    ["patch"] = new JsonObject { ["edits"] = new JsonArray(edits.Select(e => (JsonNode)e).ToArray()) } };
            };
            var session = new PlanningSession { Catalog = catalog, Request = new() { TenantId = "local", Prompt = proposal.Requirements!.Summary,
                Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 } } };
            var timer = Stopwatch.StartNew();
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new(), adapter, ct);
            if (typedRepair)
            {
                Assert.Equal(1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
                Assert.Contains(session.Diagnostics, d => d.Code == (variant == "typed-repair" ? "TASK_FLATTEN_INVALID" : "TASK_LOOKUP_INVALID"));
                session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, adapter, ct);
                Assert.Equal(expectedPlan, JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan));
            }
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join('\n', session.Diagnostics));
            Assert.Equal(typedRepair ? 2 : 1, session.ModelCalls); Assert.Equal(typedRepair ? 1 : 0, session.ReplanAttempts);
            Assert.Empty(visits); Assert.Empty(model.Requests);
            var reviewed = new List<string> { "search_catalogue", "collect_top_products", "visit_and_extract", "save_excel", "cleanup_browser" };
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { Kind = "approve", ExpectedRevision = session.Revision,
                ArtifactHash = session.ComputeArtifactHash(), ReviewedRequirementIds = reviewed }, adapter, ct);
            Assert.Equal(PlanningStatus.Approved, session.Status);
            var planningMs = timer.Elapsed.TotalMilliseconds;
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!)); timer.Restart();
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["query"] = "observed lamps" }, ct);
            var executionMs = timer.Elapsed.TotalMilliseconds;
            var journal = await store.ReadAsync("local", engine.Limits.RunId, ct);
            Assert.NotNull(journal); Assert.True(journal.FinalizationCompleted);
            Assert.DoesNotContain(journal.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
            var close = Assert.Single(journal.Invocations.Values, i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "browser_close");
            Assert.Equal("completed", close.Status);
            var browser = await transport.GetClientAsync("browser", ct);
            var after = await browser.CallToolAsync("browser_get_content", new JsonObject(), ct);
            Assert.True(after.IsError); Assert.Contains("No active page", after.Content!["error_message"]!.ToString());
            var file = Path.Combine(workspace, "workflows/retained-products/products.xlsx");
            output.WriteLine($"RETAINED {variant}: success={result.Success}; planningCalls={session.ModelCalls}; repairs={session.ReplanAttempts}; planningMs={planningMs:F1}; executionMs={executionMs:F1}; yamlBytes={Encoding.UTF8.GetByteCount(session.Yaml!)}; mappingCalls={model.MappingCalls}; unsampledPages={model.PagesOutsideExamples}; interpretationCalls={model.Requests.Count - model.MappingCalls}; requestEstimates=[{string.Join(',', model.Requests.Select(r => KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest))))}]; invocations={journal.Invocations.Count}; steps={journal.StepsStarted}; cleanupSteps={journal.FinalizationStepsStarted}; journalBytes={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(journal, WorkflowRunJsonContext.Default.WorkflowRun))}; error={result.Error?.Code} {result.Error?.Message}");
            if (variant is "unknown-id" or "incomplete" or "denied")
            {
                Assert.False(result.Success); Assert.False(File.Exists(file)); Assert.False(File.Exists(Path.Combine(root, "outside.xlsx")));
                if (variant == "unknown-id") Assert.DoesNotContain(visits, p => p.StartsWith("/product/", StringComparison.Ordinal));
                if (variant == "incomplete") { Assert.Empty(model.Requests); Assert.Empty(submitted); }
                Assert.DoesNotContain(journal.Invocations.Values, i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "document_write" && i.Status == "completed");
                return;
            }
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(["observed lamps"], submitted);
            Assert.Equal([variant == "consent"], consentSeen);
            var selectedIndices = variant == "repeated-selection" ? Enumerable.Range(0, 9).Append(0).ToArray() : Enumerable.Range(0, 10).ToArray();
            var visitedProducts = visits.Where(p => p.StartsWith("/product/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(selectedIndices.Select(i => "/product/" + i), visitedProducts);
            if (variant == "repeated-selection") Assert.False(visitedProducts.SequenceEqual(Enumerable.Range(0, 10).Select(i => "/product/" + i)));
            Assert.Equal(file, result.Outputs!["xlsxPath"]!.ToString());
            var products = result.Outputs["products"]!.AsArray(); Assert.Equal(10, products.Count);
            for (var i = 0; i < products.Count; i++)
            {
                Assert.NotNull(products[i]); Assert.Equal(i, products[i]!["index"]!.GetValue<int>());
                Assert.Equal(ProductName(selectedIndices[i]), products[i]!["name"]!.ToString()); Assert.Equal(ProductDescription(selectedIndices[i]), products[i]!["description"]!.ToString());
                Assert.Equal(selectedIndices[i] == 2 ? null : ProductPrice(selectedIndices[i]), products[i]!["price"]?.ToString());
            }
            Assert.True(model.PagesOutsideExamples > 0);
            var mappings = journal.Invocations.Values.Where(i => i.StepType == "mapping.dynamic").ToArray(); Assert.Equal(13, mappings.Length);
            foreach (var mapping in mappings)
            {
                var pages = mapping.ResolvedInput!["sources"]!["pages"]!.AsArray();
                var outputPages = Assert.Single(mapping.Output!["value"]!.AsObject()).Value!.AsArray();
                Assert.Equal(pages.Count, outputPages.Count);
                Assert.All(pages, page => Assert.NotEmpty(page!["records"]!.AsArray()));
            }
            var offered = model.Requests.Select(RetainedModel.BusinessData).FirstOrDefault(d => d?["productOffered"] is not null)!["productOffered"]!.AsArray();
            Assert.Equal(12, offered.Count); Assert.Contains(offered, c => c!["text"]!.ToString().StartsWith("Sponsored", StringComparison.Ordinal));
            Assert.Contains(offered, c => !model.SampledReferences.Contains(c!["candidateId"]!.ToString()));
            output.WriteLine($"COVERAGE {variant}: pages={mappings.Sum(m => m.ResolvedInput!["sources"]!["pages"]!.AsArray().Count)}; records={mappings.Sum(m => m.ResolvedInput!["sources"]!["pages"]!.AsArray().Sum(p => p!["records"]!.AsArray().Count))}; offered={offered.Count}; selected={products.Count}; typedProjectionCalls=0; normalizationCalls=0");
            var captures = journal.Invocations.Values.Where(i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "browser_get_content").ToArray();
            Assert.Equal(13, captures.Length);
            using var workbook = SpreadsheetDocument.Open(file, false);
            var rows = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            var expected = new List<string[]> { new[] { "Index", "Name", "Description", "Price", "URL", "Status" } };
            for (var i = 0; i < 10; i++)
            {
                var observed = selectedIndices[i];
                expected.Add([i.ToString(System.Globalization.CultureInfo.InvariantCulture), ProductName(observed), ProductDescription(observed), observed == 2 ? "" : ProductPrice(observed),
                    site.Urls.Single() + "/product/" + observed + "?source=" + new string('z', 250), observed == 2 ? "missing_price" : "observed"]);
            }
            if (variant == "fabricated") Assert.False(rows.Select(r => string.Join('\t', r)).SequenceEqual(expected.Select(r => string.Join('\t', r))));
            else
            {
                Assert.Equal(expected.Count, rows.Length);
                for (var i = 0; i < rows.Length; i++) Assert.Equal(expected[i], rows[i]);
            }
            output.WriteLine($"ORACLE {variant}: cellsMatch={variant != "fabricated"}; firstTenVisits={variant != "repeated-selection"}; workbookRows={rows.Length}; browserClosed=true; noUnresolvedReceipts=true");
            var before = model.Requests.Count; var beforeVisits = visits.Count;
            // A fresh store reconstructs committed completion without a second visit, inference or write.
            var restarted = new WorkflowEngine { RunStore = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(
                new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners")) };
            var recovered = await restarted.ResumeAsync("local", engine.Limits.RunId, journal.Revision, document.Workflows[document.Entrypoint!], ct);
            Assert.True(recovered.Success, recovered.Error?.Message); Assert.True(JsonNode.DeepEquals(result.Outputs, recovered.Outputs));
            Assert.Equal(before, model.Requests.Count); Assert.Equal(beforeVisits, visits.Count);
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }

    private static string ProductName(int i) => i is 0 or 1 ? "Same observed name" : "Observed item " + i;
    private static string ProductDescription(int i) => "Description observée " + i;
    private static string ProductPrice(int i) => (i + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",99 €";

    private sealed class RetainedModel(string variant) : ILLMClient, ILLMCapabilityResolver
    {
        internal List<LLMRequest> Requests { get; } = [];
        internal int MappingCalls;
        internal int PagesOutsideExamples;
        internal HashSet<string> SampledReferences { get; } = new(StringComparer.Ordinal);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)), 1, 12000);
            if (request.StructuredOutputSchema?["properties"]?["script"] is not null)
            {
                MappingCalls++;
                var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
                PagesOutsideExamples += data["source_count"]!.GetValue<int>() - data["examples"]!.AsArray().Count;
                foreach (var example in data["examples"]!.AsArray())
                    foreach (var record in example!["item"]!["records"]!.AsArray())
                        SampledReferences.Add(record!["reference"]!.ToString());
                var fields = data["item_target"]!["properties"]!.AsObject().Select(f => f.Key);
                if (fields.SequenceEqual(["facts"]))
                    return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({facts:item.records.filter(r=>m.test(r.tag,'^(h1|p|price)$')||m.test(r.text,'CAPTCHA')).map(r=>({evidenceId:r.reference,tag:r.tag,text:r.text}))})" } });
                var expressions = fields.Select(field => field + ":item.records.filter(r=>m.test(r." + (field == "captchaEvidence" ? "text,'CAPTCHA'" : field == "nameCandidates" ? "tag,'^h1$'" : field == "descriptionCandidates" ? "tag,'^p$'" : field == "priceCandidates" ? "tag,'^price$'" : field == "productCandidates" ? "kind,'^link$'" : "kind,'^control$'") + ")).map(r=>({" +
                    (field == "productCandidates" ? "candidateId:r.reference,text:r.text" : field is "consentControls" or "searchFillTargets" ? "candidateId:r.reference,text:r.text,tag:r.tag" : "evidenceId:r.reference,text:r.text") + "}))");
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({" + string.Join(',', expressions) + "})" } });
            }
            var input = BusinessData(request)!; JsonObject result;
            Assert.DoesNotContain("irrelevant-observation", input.ToJsonString());
            Assert.DoesNotContain("observationSnapshot", input.ToJsonString());
            if (input["consentOffered"] is JsonArray consent)
                result = new() { ["selectedConsentId"] = consent.SingleOrDefault(c => c!["text"]!.ToString() == "Accept cookies")?["candidateId"]?.DeepClone(), ["captchaEncountered"] = false, ["status"] = "observed" };
            else if (input["searchOffered"] is JsonArray search)
                result = new() { ["selectedSearchId"] = search.Single(c => c!["tag"]!.ToString() == "input")!["candidateId"]!.DeepClone(), ["captchaEncountered"] = false, ["status"] = "observed" };
            else if (input["productOffered"] is JsonArray candidates)
            {
                Assert.All(candidates, c => Assert.Equal(new[] { "candidateId", "text" }, c!.AsObject().Select(f => f.Key)));
                Assert.DoesNotContain("?source=", request.Prompt);
                result = new() { ["selectedProductIds"] = variant == "unknown-id" ? new JsonArray("not-offered") : new JsonArray((variant == "repeated-selection" ? candidates.Take(9).Append(candidates[0]) : candidates.Take(10)).Select(c => c!["candidateId"]!.DeepClone()).ToArray()), ["captchaEncountered"] = false, ["status"] = "observed" };
            }
            else if (input["facts"] is JsonArray facts)
            {
                JsonNode? Fact(string tag) => facts.SingleOrDefault(f => f!["tag"]!.ToString() == tag)?["text"]?.DeepClone();
                result = new() { ["name"] = Fact("h1"), ["description"] = Fact("p"), ["price"] = Fact("price"),
                    ["status"] = Fact("price") is null ? "missing_price" : "observed", ["captchaEncountered"] = false };
            }
            else if (input["products"] is JsonArray products)
            {
                Assert.InRange(products.Count, 0, 10);
                var rows = new List<string> { "Index\tName\tDescription\tPrice\tURL\tStatus" };
                rows.AddRange(products.Select(p => string.Join('\t', new[] { "index", "name", "description", "price", "url", "status" }.Select(f => p![f]?.ToString() ?? ""))));
                result = new() { ["xlsxTsv"] = variant == "fabricated" ? "Name\nPlausible but unobserved product" : string.Join('\n', rows) };
            }
            else
            {
                Assert.Contains("productCaptchaEncountered", input.AsObject().Select(p => p.Key));
                var flags = new[] { input["initialCaptchaEncountered"]!, input["readyCaptchaEncountered"]!, input["searchCaptchaEncountered"]! }.Concat(input["productCaptchaEncountered"]!.AsArray());
                result = new() { ["captchaEncountered"] = flags.Any(f => f!.GetValue<bool>()), ["status"] = "observed" };
            }
            return Task.FromResult(new LLMResponse { Json = result });
        }
        internal static JsonNode? BusinessData(LLMRequest request)
        {
            var marker = request.Prompt.LastIndexOf("Business data", StringComparison.Ordinal);
            return marker < 0 ? null : JsonNode.Parse(request.Prompt[request.Prompt.IndexOf('{', marker)..]);
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private static PlanningProposal RetainedProposal(string name, PlanningCatalog catalog, string? origin = null)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RetainedProductBindings", name + ".json")))!;
        var proposal = json.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        foreach (var task in RetainedTasks(proposal.Plan!.Root))
        {
            if (task.Kind == "operation") task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == task.Operation)).Id;
            if (origin is not null && task.Id == "open_catalogue") task.Inputs.Single(i => i.Name == "url").Value.Text = origin + "/";
        }
        return proposal;
    }

    private static IEnumerable<PlanTask> RetainedTasks(TaskScope scope)
    {
        foreach (var task in scope.Tasks.Concat(scope.Always))
        {
            yield return task;
            foreach (var nested in new[] { task.Body, task.Otherwise }.Where(s => s is not null).SelectMany(s => RetainedTasks(s!))) yield return nested;
        }
    }

    private static string[] RetainedOperations(TaskPlan plan) => RetainedTasks(plan.Root).Where(t => t.Kind == "operation")
        .Select(t => t.Id + ":" + t.Operation).ToArray();

    private static async Task<PlanningCatalog> RetainedCatalog(WorkflowPlanningRuntime runtime, CancellationToken ct)
    {
        var catalog = await runtime.DiscoverAsync(new(), ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(ct))
        {
            string? cursor = null;
            do
            {
                var page = await runtime.Capabilities.ListAsync(source.Id, cursor, ct);
                foreach (var capability in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(capability, ct));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        return catalog;
    }
}
