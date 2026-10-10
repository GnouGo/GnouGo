using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.AI.Core;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
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
    public async Task BusinessCompositionRemovesForwardersWithoutChangingContractsOrLowering()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new DocumentServerSettings(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken);
        catalog.Policy.RequireExternalConfirmation = false;
        var baseline = BusinessProposal("baseline", catalog); var compact = BusinessProposal("compact", catalog);
        Assert.Equal(JsonSerializer.Serialize(baseline.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(compact.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(RetainedOperations(baseline.Plan!), RetainedOperations(compact.Plan!));
        var before = BusinessCompilation(baseline.Plan!, catalog); var after = BusinessCompilation(compact.Plan!, catalog);
        Assert.Equal(23, RetainedTasks(baseline.Plan!.Root).Count());
        Assert.InRange(RetainedTasks(compact.Plan!.Root).Count(), 1, 16);
        Assert.DoesNotContain(RetainedTasks(compact.Plan.Root), t => t.Kind == "value");
        Assert.Equal(2, RetainedTasks(compact.Plan.Root).Count(t => t.Kind == "foreach" && t.Body!.Tasks.Count == 0));
        Assert.True(after.Steps < before.Steps, $"steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; bytes={before.Yaml.Length}→{after.Yaml.Length}");
        Assert.True(after.Sets < before.Sets); Assert.True(after.Yaml.Length < before.Yaml.Length);
        Assert.Equal(before.Yaml, BusinessCompilation(baseline.Plan, catalog).Yaml);
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"COMPOSITION tasks=23→{RetainedTasks(compact.Plan.Root).Count()}; steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; yamlBytes={Encoding.UTF8.GetByteCount(before.Yaml)}→{Encoding.UTF8.GetByteCount(after.Yaml)}; profile=compact-bindings-v5");
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("producer_revision")]
    [InlineData("repeated")]
    [InlineData("empty")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    [InlineData("fabricated")]
    public async Task BusinessCompositionExecutesRealVisitsAndWorkbookWithLocalAssertions(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-business-tasks-").FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var visits = new List<string>(); var queries = new List<string>();
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        await using var site = builder.Build();
        site.MapGet("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value!; visits.Add(path);
            string body;
            if (path == "/search")
            {
                queries.Add(context.Request.Query["q"].ToString());
                body = "<h1>Observed catalogue</h1>" + string.Concat(Enumerable.Range(0, 24).Select(i => "<p>noise-" + i + new string('z', 120) + "</p>"));
                if (variant != "empty") body += string.Concat(Enumerable.Range(0, 12).Select(i => "<a href='http://" + context.Request.Host + "/entry/" + i + "?source=" + new string('u', 300) + "'>" + (i == 1 ? "Sponsored " : "") + "Observed choice " + i + "</a>"));
            }
            else if (path.StartsWith("/entry/", StringComparison.Ordinal))
            {
                var i = int.Parse(path[7..], System.Globalization.CultureInfo.InvariantCulture);
                body = "<h1>" + ProductName(i) + "</h1><p>" + ProductDescription(i) + "</p>" + (i == 2 ? "" : "<price>" + ProductPrice(i) + "</price>");
            }
            else { context.Response.StatusCode = 404; body = "Unavailable"; }
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<html><head><title>Observed fixture</title></head><body>" + body + "</body></html>", ct);
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
                    ["Browser__MaxObservationRecords"] = "2", ["Browser__MaxObservationPages"] = variant == "incomplete" ? "1" : "100", ["Browser__SlowMoMs"] = "0", ["Browser__HoldOpenMs"] = "0", ["OpenTelemetry__Enabled"] = "false" }),
                ["document"] = Server("Document", new() { ["Document__DefaultWorkingDirectory"] = workspace, ["OpenTelemetry__Enabled"] = "false" })
            });
            var model = new BusinessModel(variant, site.Urls.Single());
            var store = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners"));
            var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = model, RunStore = store,
                LLMUsageBudget = new(new() { MaxElapsed = TimeSpan.FromMinutes(2) }), LlmDefaults = new() { Model = "deterministic" },
                Limits = new() { TenantId = "local", RunId = "business-" + variant } };
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await RetainedCatalog(runtime, ct);
            catalog.Policy.RequireExternalConfirmation = false;
            var proposal = BusinessProposal("compact", catalog);
            var expectedPlan = JsonSerializer.Serialize(proposal.Plan, PlanningJsonContext.Default.TaskPlan);
            var corrections = new Dictionary<string, JsonNode?>();
            if (variant == "producer_revision")
            {
                var extraction = proposal.Plan!.Root.Tasks.Single(t => t.Id == "extract_choices");
                corrections["/tasks/extract_choices/each"] = JsonNode.Parse("""{"input":"pages","output":"pageViews"}""");
                corrections["/tasks/extract_choices/resultType"] = JsonSerializer.SerializeToNode(extraction.ResultType, PlanningJsonContext.Default.TaskType);
                corrections["/tasks/extract_choices/requires"] = JsonSerializer.SerializeToNode(extraction.Requires, PlanningJsonContext.Default.TaskValue);
                extraction.Each = new("page", "pageViews");
                extraction.ResultType!.Fields[0].Type.Items = extraction.ResultType.Fields[0].Type.Items!.Items;
                extraction.Requires = new() { Kind = "choice", Source = "acquire" };
            }
            if (variant == "denied") proposal.Plan!.Root.Tasks.Single(t => t.Id == "write").Inputs.Single(i => i.Name == "filePath").Value.Text = "../outside.xlsx";
            var adapter = new ProposalRuntime(runtime, proposal);
            var session = await new HybridWorkflowPlanner().AdvanceAsync(new() { Catalog = catalog, Request = new() {
                TenantId = "local", Prompt = proposal.Requirements!.Summary, Policy = new() { RequireExternalConfirmation = false },
                Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 } } }, new(), adapter, ct);
            if (variant == "producer_revision")
            {
                Assert.Equal(PlanningStatus.Stopped, session.Status); Assert.Equal(1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
                Assert.Contains(session.Diagnostics, d => d.Code == "TASK_TRANSFORM_EACH");
                Assert.Contains(session.Diagnostics, d => d.Code == "TASK_FLATTEN_INVALID");
                Assert.Contains(session.Diagnostics, d => d.Code == "CHOICE_UNKNOWN");
                Assert.Empty(visits); Assert.Empty(model.Requests);
                adapter.Patch = request =>
                {
                    var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!["repair"]!;
                    var edits = context["slots"]!.AsArray().Select(slot => (JsonNode)new JsonObject {
                        ["slot"] = slot!["id"]!.ToString(), ["action"] = "replace", ["value"] = corrections[slot["location"]!.ToString()]!.DeepClone() }).ToArray();
                    Assert.Equal(3, edits.Length);
                    return new() { ["discoveryRequests"] = null, ["clarifications"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray(edits) } };
                };
                session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { Kind = "revise", PreserveRequirements = true,
                    ExpectedRevision = session.Revision, ArtifactHash = session.ComputeArtifactHash(), EditablePaths = corrections.Keys.ToList(),
                    Text = "Correct only the extraction declaration and its assertion. Preserve every business operation and accepted output." }, adapter, ct);
                session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, adapter, ct);
                Assert.Equal(expectedPlan, JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan));
            }
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join('\n', session.Diagnostics));
            Assert.Equal(variant == "producer_revision" ? 2 : 1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts); Assert.Empty(visits); Assert.Empty(model.Requests);
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { Kind = "approve", ExpectedRevision = session.Revision,
                ArtifactHash = session.ComputeArtifactHash(), ReviewedRequirementIds = ["search", "select_first", "visit_extract", "write_workbook", "release"] }, new ProposalRuntime(runtime, proposal), ct);
            Assert.Equal(PlanningStatus.Approved, session.Status);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!)); var timer = Stopwatch.StartNew();
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["query"] = "observed lamps" }, ct);
            var journal = (await store.ReadAsync("local", engine.Limits.RunId, ct))!;
            Assert.True(journal.FinalizationCompleted, $"{result.Error?.Code}: {result.Error?.Message}; {result.Error?.Details}");
            Assert.DoesNotContain(journal.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
            Assert.Equal("completed", Assert.Single(journal.Invocations.Values, i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "browser_close").Status);
            var closed = await (await transport.GetClientAsync("browser", ct)).CallToolAsync("browser_get_content", new JsonObject(), ct);
            Assert.True(closed.IsError); Assert.Contains("No active page", closed.Content!["error_message"]!.ToString());
            var file = Path.Combine(workspace, "workflows/business-composition/items.xlsx");
            output.WriteLine($"BUSINESS {variant}: success={result.Success}; calls={session.ModelCalls}; repairs={session.ReplanAttempts}; elapsedMs={timer.ElapsedMilliseconds}; tasks={RetainedTasks(session.Plan!.Root).Count()}; yamlBytes={Encoding.UTF8.GetByteCount(session.Yaml!)}; inference={model.Requests.Count}; mappings={model.MappingCalls}; invocations={journal.Invocations.Count}; steps={journal.StepsStarted}; cleanup={journal.FinalizationStepsStarted}; journalBytes={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(journal, WorkflowRunJsonContext.Default.WorkflowRun))}; error={result.Error?.Code} {result.Error?.Message}");
            if (variant is "unoffered" or "incomplete" or "denied")
            {
                Assert.False(result.Success); Assert.False(File.Exists(file)); Assert.False(File.Exists(Path.Combine(root, "outside.xlsx")));
                if (variant is "unoffered" or "incomplete") Assert.DoesNotContain(visits, p => p.StartsWith("/entry/", StringComparison.Ordinal));
                if (variant == "incomplete") Assert.Equal(0, model.MappingCalls);
                Assert.DoesNotContain(journal.Invocations.Values, i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "document_write" && i.Status == "completed");
                return;
            }
            Assert.True(result.Success, result.Error?.Message + " " + string.Join("; ", model.MappingFailures)); Assert.Equal(["observed lamps"], queries);
            var selected = variant == "empty" ? [] : variant == "repeated" ? Enumerable.Range(0, 9).Append(0).ToArray() : Enumerable.Range(0, 10).ToArray();
            Assert.Equal(selected.Select(i => "/entry/" + i), visits.Where(p => p.StartsWith("/entry/", StringComparison.Ordinal)));
            Assert.Equal(file, result.Outputs!["filePath"]!.ToString());
            var records = result.Outputs["rows"]!.AsArray(); Assert.Equal(selected.Length, records.Count);
            var expected = new List<string[]> { new[] { "name", "description", "price", "error" } };
            for (var i = 0; i < selected.Length; i++)
            {
                var n = selected[i]; Assert.NotNull(records[i]); Assert.Equal(ProductName(n), records[i]!["name"]!.ToString());
                Assert.Equal(ProductDescription(n), records[i]!["description"]!.ToString()); Assert.Equal(n == 2 ? null : ProductPrice(n), records[i]!["price"]?.ToString());
                expected.Add([ProductName(n), ProductDescription(n), n == 2 ? "" : ProductPrice(n), n == 2 ? "missing_price" : ""]);
            }
            using (var workbook = SpreadsheetDocument.Open(file, false))
            {
                var worksheet = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet;
                Assert.NotNull(worksheet);
                var actual = worksheet.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
                if (variant == "fabricated") Assert.False(actual.Select(r => string.Join('\t', r)).SequenceEqual(expected.Select(r => string.Join('\t', r))));
                else { Assert.Equal(expected.Count, actual.Length); for (var i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i]); }
            }
            var mappings = journal.Invocations.Values.Where(i => i.StepType == "mapping.dynamic").ToArray(); Assert.Equal(1 + selected.Length, mappings.Length);
            foreach (var mapping in mappings)
                Assert.Equal(mapping.ResolvedInput!["sources"]!["pages"]!.AsArray().Count, Assert.Single(mapping.Output!["value"]!.AsObject()).Value!.AsArray().Count);
            if (variant != "empty") Assert.Contains(model.Offered!, c => !model.SampledReferences.Contains(c!["candidateKey"]!.ToString()));
            Assert.Equal(variant == "empty" ? 0 : 12, model.Offered!.Count);
            Assert.All(model.Requests, r => Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)), 1, 12000));
            output.WriteLine($"ORACLE {variant}: cellsMatch={variant != "fabricated"}; visited={selected.Length}; pages={mappings.Sum(m => m.ResolvedInput!["sources"]!["pages"]!.AsArray().Count)}; offered={model.Offered.Count}; decisionBytes={model.DecisionBytes}; cleanup=true; typedInference=0");
            var calls = model.Requests.Count; var pagesVisited = visits.Count;
            var recovered = await new WorkflowEngine { RunStore = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners")) }
                .ResumeAsync("local", engine.Limits.RunId, journal.Revision, document.Workflows[document.Entrypoint!], ct);
            Assert.True(recovered.Success, recovered.Error?.Message); Assert.True(JsonNode.DeepEquals(result.Outputs, recovered.Outputs));
            Assert.Equal(calls, model.Requests.Count); Assert.Equal(pagesVisited, visits.Count);
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }

    private sealed class BusinessModel(string variant, string origin) : ILLMClient, ILLMCapabilityResolver
    {
        internal List<LLMRequest> Requests { get; } = [];
        internal List<string> MappingFailures { get; } = [];
        internal HashSet<string> SampledReferences { get; } = new(StringComparer.Ordinal);
        internal JsonArray? Offered;
        internal int MappingCalls, DecisionBytes;
        private string? _unoffered;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            JsonObject result;
            if (request.StructuredOutputSchema?["properties"]?["script"] is not null)
            {
                MappingCalls++;
                var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
                if (data["failure"] is not null) MappingFailures.Add(data["failure"]!.ToString());
                foreach (var example in data["examples"]!.AsArray())
                    foreach (var record in example!["item"]!["records"]!.AsArray())
                    { SampledReferences.Add(record!["reference"]!.ToString()); if (record["kind"]!.ToString() != "link") _unoffered ??= record["reference"]!.ToString(); }
                var target = data["item_target"]!;
                const string candidates = "item.records.filter(r=>m.test(r.kind,'^link$')).map(r=>({candidateKey:r.reference,observedText:r.text}))";
                var script = target["type"]?.ToString() == "array" ? candidates :
                    "({nameFacts:item.records.filter(r=>m.test(r.tag,'^h1$')).map(r=>r.text),descriptionFacts:item.records.filter(r=>m.test(r.tag,'^p$')).map(r=>r.text),priceFacts:item.records.filter(r=>m.test(r.tag,'^price$')).map(r=>r.text)})";
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = script } });
            }
            var input = RetainedModel.BusinessData(request)!;
            Assert.DoesNotContain("observationSnapshot", input.ToJsonString()); Assert.DoesNotContain("noise-", input.ToJsonString());
            if (request.StructuredOutputSchema?["properties"]?["url"] is not null)
                result = new() { ["url"] = origin + "/search?q=" + Uri.EscapeDataString(input["query"]!.ToString()) };
            else if (input["offeredCandidates"] is JsonArray offered)
            {
                Offered = offered.DeepClone().AsArray(); DecisionBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                Assert.All(offered, c => Assert.Equal(new[] { "candidateKey", "observedText" }, c!.AsObject().Select(p => p.Key)));
                Assert.DoesNotContain("?source=", request.Prompt); Assert.DoesNotContain("selector", request.Prompt);
                var chosen = variant == "repeated" ? offered.Take(9).Append(offered[0]) : offered.Take(10);
                result = new() { ["productKeys"] = variant == "unoffered" ? new JsonArray(_unoffered ?? throw new InvalidOperationException("Fixture needs an unoffered original record.")) : new JsonArray(chosen.Select(c => c!["candidateKey"]!.DeepClone()).ToArray()), ["searchError"] = null };
            }
            else if (input["compactFacts"] is JsonArray facts)
            {
                Assert.All(facts, p => Assert.Equal(new[] { "nameFacts", "descriptionFacts", "priceFacts" }, p!.AsObject().Select(f => f.Key)));
                Assert.DoesNotContain("?source=", request.Prompt);
                JsonNode? Fact(string name) => facts.SelectMany(p => p![name]!.AsArray()).SingleOrDefault()?.DeepClone();
                result = new() { ["name"] = Fact("nameFacts"), ["description"] = Fact("descriptionFacts"), ["price"] = Fact("priceFacts"), ["error"] = Fact("priceFacts") is null ? "missing_price" : null };
            }
            else
            {
                var records = input["rows"]!.AsArray(); Assert.InRange(records.Count, 0, 10);
                var lines = new List<string> { "name\tdescription\tprice\terror" };
                lines.AddRange(records.Select(r => string.Join('\t', new[] { "name", "description", "price", "error" }.Select(f => r![f]?.ToString() ?? ""))));
                result = new() { ["content"] = variant == "fabricated" ? "name\nPlausible unobserved item" : string.Join('\n', lines), ["success"] = true };
            }
            return Task.FromResult(new LLMResponse { Json = result });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private static PlanningProposal BusinessProposal(string name, PlanningCatalog catalog)
    {
        var proposal = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BusinessTaskComposition", name + ".json")), PlanningJsonContext.Default.PlanningProposal)!;
        foreach (var task in RetainedTasks(proposal.Plan!.Root).Where(t => t.Kind == "operation"))
        {
            var method = task.Operation switch { "acquire" => "browser_get_content", "persist" => "document_write", "release" => "browser_close", _ => throw new InvalidOperationException() };
            task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id;
        }
        return proposal;
    }

    private static (string Yaml, int Steps, int Sets) BusinessCompilation(TaskPlan plan, PlanningCatalog catalog)
    {
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v5" } });
        Assert.True(compiled.Graph is not null, string.Join('\n', compiled.Diagnostics));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "business", true, true, true);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        static IEnumerable<CompiledStep> All(IEnumerable<CompiledStep> steps) => steps.SelectMany(s => new[] { s }
            .Concat(All(s.Steps ?? [])).Concat(All(s.Default ?? [])).Concat((s.Cases ?? []).SelectMany(c => All(c.Steps)))
            .Concat((s.Branches ?? []).SelectMany(All)));
        var all = document.Workflows.Values.SelectMany(w => All(w.Steps.Concat(w.Finally))).ToArray();
        return (yaml, all.Length, all.Count(s => s.Type == "set"));
    }
}
