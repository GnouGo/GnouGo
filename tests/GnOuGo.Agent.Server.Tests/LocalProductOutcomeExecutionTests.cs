using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
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

public sealed class LocalProductOutcomeExecutionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("nominal")]
    [InlineData("pages")]
    [InlineData("pages-parallel")]
    [InlineData("pages-compact")]
    [InlineData("pages-compact-consent")]
    [InlineData("pages-compact-decision")]
    [InlineData("pages-compact-decision-consent")]
    [InlineData("pages-compact-decision-complete")]
    [InlineData("pages-compact-decision-complete-consent")]
    [InlineData("pages-compact-decision-complete-flat")]
    [InlineData("pages-compact-decision-complete-flat-consent")]
    [InlineData("pages-compact-decision-complete-flat-lookup")]
    [InlineData("pages-compact-decision-complete-flat-lookup-consent")]
    [InlineData("pages-compact-decision-complete-flat-lookup-indexed")]
    [InlineData("pages-compact-decision-complete-flat-lookup-indexed-consent")]
    [InlineData("pages-compact-scoped")]
    [InlineData("extract")]
    [InlineData("each")]
    [InlineData("each-parallel")]
    [InlineData("observation")]
    [InlineData("changed")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("navigation")]
    [InlineData("denied")]
    [InlineData("limit")]
    [InlineData("consent")]
    [InlineData("no-consent")]
    [InlineData("delayed-consent")]
    [InlineData("unrelated-modal")]
    [InlineData("incomplete-observation")]
    [InlineData("required-consent")]
    [InlineData("required-incomplete-observation")]
    public async Task RealStdioContractsAndLocalBrowserExecutionProduceIndependentlyVerifiedWorkbook(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-local-products-").FullName;
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0)); builder.Logging.ClearProviders();
        await using var site = builder.Build();
        var visits = new List<string>();
        var flattened = variant.Contains("-flat", StringComparison.Ordinal);
        var lookup = variant.Contains("-lookup", StringComparison.Ordinal);
        var indexed = variant.Contains("-indexed", StringComparison.Ordinal);
        var compact = variant.StartsWith("pages-compact", StringComparison.Ordinal);
        var independentDecision = variant.StartsWith("pages-compact-decision", StringComparison.Ordinal);
        var consentScenario = independentDecision || variant is "pages-compact-consent" or "required-consent" or "required-incomplete-observation" or "consent" or "no-consent" or "delayed-consent" or "unrelated-modal" or "incomplete-observation";
        var consentReceipts = new List<bool>();
        site.MapGet("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value!; visits.Add(path);
            if (path is "/one" or "/two") consentReceipts.Add(context.Request.Cookies["fixtureConsent"] == "accepted");
            if (path == "/broken") { context.Abort(); return; }
            context.Response.ContentType = "text/html; charset=utf-8";
            var origin = "http://" + context.Request.Host;
            var links = variant switch { "empty" => Array.Empty<string>(), "missing" => ["missing"], "navigation" => ["broken"], "limit" => ["one", "two", "three", "four"], _ => ["one", "two"] };
            var body = path == "/search" ? string.Join("", links.Select(p => "<a href='" + origin + "/" + p + "'>Product</a>")) : path == "/missing" ? "<h1>Only a name</h1>" :
                path == "/one" ? variant == "changed" ? "<h1>Changed lamp</h1><p>New description</p><price>9,90 €</price>" : "<h1>Lampe été, &quot;A&quot;</h1><p>Bright and small {{values}}</p><price>19,99 €</price>" :
                variant == "changed" ? "<h1>Second</h1><p>Updated</p><price>12.00</price>" : "<h1>東京 lamp</h1><p>Line one\nLine two</p><price>25.00</price>";
            if (path == "/search" && variant.StartsWith("pages", StringComparison.Ordinal)) body = "<p>Catalogue</p><p>Observed introduction</p>" + body + "<p>Footer</p><p>End</p>";
            if (path == "/search" && compact) body = string.Concat(Enumerable.Range(0, 120).Select(i => "<p>irrelevant-observation-" + i + new string('x', 600) + "</p>")) + body;
            var banner = path == "/search" && variant is "pages-compact-consent" or "pages-compact-decision-consent" or "pages-compact-decision-complete-consent" or "pages-compact-decision-complete-flat-consent" or "pages-compact-decision-complete-flat-lookup-consent" or "pages-compact-decision-complete-flat-lookup-indexed-consent" or "required-consent" or "consent" or "delayed-consent" or "unrelated-modal";
            var dialog = "<section role='dialog' aria-modal='true' id='notice'><p>" + (variant == "unrelated-modal" ? "Account verification" : "Cookie preferences") +
                "</p><div><span role='button' tabindex='0' id='accept' onclick=\"document.cookie='fixtureConsent=accepted;path=/';document.querySelector('main').hidden=false;document.getElementById('notice').remove()\">" +
                (variant == "unrelated-modal" ? "Continue" : "Accept cookies") + "</span></div></section>";
            var html = "<html><body><main" + (banner && !independentDecision ? " hidden" : "") + ">" + body + "</main>";
            html += banner ? variant == "delayed-consent" ? "<script>setTimeout(()=>document.body.insertAdjacentHTML('beforeend'," + JsonValue.Create(dialog)!.ToJsonString() + "),100)</script>" : dialog : "";
            await context.Response.WriteAsync(html + "</body></html>", ct);
        });
        await site.StartAsync(ct);
        try
        {
            McpServerOptions Server(string name, Dictionary<string, string?> environment) => new()
            {
                Type = "stdio", Command = Environment.GetEnvironmentVariable("GNOU_GO_" + name.ToUpperInvariant() + "_MCP_TEST_EXECUTABLE") ?? Path.Combine(AppContext.BaseDirectory, "GnOuGo." + name + ".Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                EnvironmentVariables = environment
            };
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>
            {
                ["browser"] = Server("Browser", new() { ["Browser__AllowedHosts__0"] = "127.0.0.1", ["Browser__Headless"] = "true", ["Browser__KeepBrowserOpen"] = "false",
                    ["Browser__MaxObservationRecords"] = compact ? "8" : variant is "incomplete-observation" or "required-incomplete-observation" or "pages" or "pages-parallel" ? "1" : "80",
                    ["Browser__SlowMoMs"] = "0", ["Browser__HoldOpenMs"] = "0", ["Browser__NavigationTimeoutMs"] = "3000", ["OpenTelemetry__Enabled"] = "false" }),
                ["document"] = Server("Document", new() { ["Document__DefaultWorkingDirectory"] = workspace, ["OpenTelemetry__Enabled"] = "false" })
            });
            var model = new ProductTransformationFixture(variant) { ReadMethod = "browser_get_content", WriteMethod = "document_write", CloseMethod = "browser_close" };
            var consentModel = new ConsentModel(model);
            var extractionModel = new ExtractModel(model);
            var compactModel = new CompactModel(new ConsentModel(new PagedModel(model, compact: true, flattened: flattened, lookup: lookup), flattened: flattened));
            var engine = new WorkflowEngine { LLMUsageBudget = new(new() { MaxElapsed = TimeSpan.FromMinutes(2) }), McpClientFactory = transport, LLMClient = compact ? compactModel : variant.StartsWith("pages", StringComparison.Ordinal) ? new PagedModel(model) : consentScenario ? consentModel : variant is "extract" or "observation" or "each" or "each-parallel" ? extractionModel : model, HumanInputProvider = new PlanningCorpus.Human(true), LlmDefaults = new() { Model = "deterministic" } };
            if (compact)
            {
                engine.RunStore = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(
                    new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "execution-vault.db")),
                    Path.Combine(root, "execution-index.db"), Path.Combine(root, "execution-owners"));
                engine.Limits.TenantId = "local"; engine.Limits.RunId = "product-fixture";
            }
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await runtime.DiscoverAsync(new(), ct); var reads = 0;
            foreach (var source in await runtime.Capabilities.ListSourcesAsync(ct))
            {
                string? cursor = null;
                do
                {
                    var page = await runtime.Capabilities.ListAsync(source.Id, cursor, ct); reads++;
                    foreach (var capability in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(capability, ct));
                    cursor = page.NextCursor;
                } while (cursor is not null);
            }
            Assert.Equal("write", catalog.Capabilities.Single(c => c.Method == "document_write").EffectKind);
            Assert.Equal("read", catalog.Capabilities.Single(c => c.Method == "browser_get_content").EffectKind);
            Assert.Equal("execute", catalog.Capabilities.Single(c => c.Method == "browser_fill").EffectKind);
            Assert.Equal("lifecycle", catalog.Capabilities.Single(c => c.Method == "browser_close").EffectKind);
            var plan = ProductTransformationPlan.Create(catalog, model);
            if (variant is "each" or "each-parallel")
            {
                var products = plan.Root.Tasks.Single(t => t.Id == "products");
                products.Parallel = variant == "each-parallel"; products.MaxConcurrency = 2;
                var extraction = products.Body!.Tasks.Single(t => t.Id == "extract");
                products.Body.Tasks.Remove(extraction);
                products.Body.Outputs = [new("pages", ProductTransformationPlan.Ref("page", "content"))];
                plan.Root.Tasks.Insert(3, new() { Id = "extract_collection", Kind = "transform", Mode = "extract", Each = new("html", "records"),
                    Objective = "Extract name, description and price independently from each observed product page in order",
                    Inputs = [new("html", ProductTransformationPlan.Ref("products", "pages"))],
                    ResultType = ProductTransformationPlan.Obj(("records", new() { Kind = "array", Items = extraction.ResultType })) });
                plan.Root.Tasks.Single(t => t.Id == "table").Inputs = [new("records", ProductTransformationPlan.Ref("extract_collection", "records"))];
            }
            if (variant.StartsWith("pages", StringComparison.Ordinal))
            {
                var search = plan.Root.Tasks[0]; search.Inputs.Add(new("format", ProductTransformationPlan.Text("observation_pages")));
                TaskValue Field(TaskValue source, string name) => new() { Kind = "field", Items = [source], Port = name };
                var manifest = ProductTransformationPlan.Ref("search", "observationManifest");
                var pages = new PlanTask { Id = "consume_pages", Kind = "foreach", Objective = "Read every frozen snapshot page before visiting products",
                    Items = Field(manifest, "pages"), MaxItems = 100, Parallel = variant == "pages-parallel", MaxConcurrency = 2,
                    Requires = new() { Kind = "predicate", Predicate = "equal", Items = [ProductTransformationPlan.Ref("search", "truncated"), new() { Kind = "boolean", Boolean = false }] },
                    Body = new() { Tasks = [new() { Id = "read_page", Kind = "operation", Objective = "Read the listed snapshot page", Operation = search.Operation,
                        Inputs = [new("format", ProductTransformationPlan.Text("observation")), new("cursor", Field(new() { Kind = "item" }, "cursor"))] }],
                        Outputs = [new("observations", ProductTransformationPlan.Ref("read_page", "observation"))] } };
                plan.Root.Tasks.Insert(1, pages);
                plan.Root.Tasks.Single(t => t.Id == "urls").Inputs = [new("pages", ProductTransformationPlan.Ref("consume_pages", "observations"))];
                if (compact)
                {
                    pages.Body.Tasks.Add(new() { Id = "compact_records", Kind = "transform", Mode = "extract", Each = new("records", "candidates"),
                        Objective = "From each observed record, retain its text, href and DOM group only when it is a link. Return an ordered candidate array per record, empty for other record kinds. Never rank or infer missing links.",
                        Inputs = [new("records", Field(ProductTransformationPlan.Ref("read_page", "observation"), "records"))],
                        ResultType = ProductTransformationPlan.Obj(("candidates", new() { Kind = "array", Items = new() { Kind = "array", Items = ProductTransformationPlan.Obj(("text", new()), ("href", new()), ("group", new())) } })) });
                    pages.Body.Outputs = [new("observations", ProductTransformationPlan.Ref("compact_records", "candidates"))];
                }
            }
            if (consentScenario) AddConsentSteps(plan, catalog, independentDecision, variant.StartsWith("pages-compact-decision-complete", StringComparison.Ordinal));
            if (variant == "pages-compact-consent" || independentDecision) plan.Root.Tasks.Single(t => t.Id == "search").Inputs.Add(new("format", ProductTransformationPlan.Text("observation_pages")));
            if (variant.StartsWith("pages-compact-decision-complete", StringComparison.Ordinal))
            {
                // Producer-owned atomic acquisition; the existing extraction loop now
                // consumes delivered pages without a cursor call per page.
                TaskValue Field(TaskValue value, string name) => new() { Kind = "field", Port = name, Items = [value] };
                TaskValue Not(TaskValue value) => new() { Kind = "predicate", Predicate = "not", Items = [value] };
                foreach (var (captureId, loopId) in new[] { ("search", "consume_pages"), ("inspect", "decision_pages") })
                {
                    var capture = plan.Root.Tasks.Single(t => t.Id == captureId);
                    capture.Inputs.RemoveAll(i => i.Name == "format");
                    capture.Inputs.Add(new("format", ProductTransformationPlan.Text("observation_complete")));
                    var snapshot = ProductTransformationPlan.Ref(captureId, "observationSnapshot");
                    var loop = plan.Root.Tasks.Single(t => t.Id == loopId);
                    loop.Items = Field(snapshot, "pages");
                    loop.Requires = new() { Kind = "predicate", Predicate = "and", Items = [Not(Field(snapshot, "captureTruncated")), Not(Field(snapshot, "manifestTruncated"))] };
                    loop.Body!.Tasks.RemoveAt(0);
                    loop.Body.Tasks.Single().Inputs = [new("records", Field(new() { Kind = "item" }, "records"))];
                }
            }
            if (flattened)
            {
                TaskValue Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
                foreach (var consumer in new[] { "consent", "urls" })
                {
                    var task = plan.Root.Tasks.Single(t => t.Id == consumer);
                    var input = task.Inputs[0]; task.Inputs[0] = new(input.Name, Flatten(Flatten(input.Value)));
                }
            }
            if (lookup)
            {
                TaskValue Field(TaskValue value, string name) => new() { Kind = "field", Port = name, Items = [value] };
                TaskValue Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
                TaskValue Lookup(TaskValue records, TaskValue ids, string field) => new() { Kind = "lookup", Port = field, Items = [records, ids] };
                var pages = plan.Root.Tasks.Single(t => t.Id == "consume_pages");
                var extraction = pages.Body!.Tasks.Single();
                extraction.Objective = "Retain only observed candidate identity and text for the immediate decision; action arguments remain in the original records.";
                extraction.ResultType = ProductTransformationPlan.Obj(("candidates", new() { Kind = "array", Items = new() { Kind = "array",
                    Items = ProductTransformationPlan.Obj(("candidateId", new()), ("text", new())) } }));
                var select = plan.Root.Tasks.Single(t => t.Id == "urls");
                select.Objective = "Select observed candidate IDs in page order, without returning action arguments.";
                select.ResultType = ProductTransformationPlan.Obj(("ids", new() { Kind = "array", Items = new() }));
                var originalPages = new PlanTask { Id = "original_records", Kind = "foreach", MaxItems = 100,
                    Objective = "Retain original observed records separately", Items = pages.Items,
                    Body = new() { Outputs = [new("records", Field(new() { Kind = "item" }, "records"))] } };
                var offered = new PlanTask { Id = "offered_selection", Kind = "value", Objective = "Verify every selected identity was offered",
                    Outputs = [new("records", Lookup(select.Inputs[0].Value, ProductTransformationPlan.Ref("urls", "ids"), "candidateId"))] };
                var ids = new PlanTask { Id = "verified_identities", Kind = "foreach", MaxItems = 3, Objective = "Retain verified selected identities",
                    Items = ProductTransformationPlan.Ref("offered_selection", "records"), Body = new() { Outputs = [new("ids", Field(new() { Kind = "item" }, "candidateId"))] } };
                var resolve = new PlanTask { Id = "action_records", Kind = "value", Objective = "Recover original action arguments",
                    Outputs = [new("records", Lookup(Flatten(ProductTransformationPlan.Ref("original_records", "records")), ProductTransformationPlan.Ref("verified_identities", "ids"), "reference"))] };
                plan.Root.Tasks.InsertRange(plan.Root.Tasks.IndexOf(select) + 1, [originalPages, offered, ids, resolve]);
                if (indexed)
                {
                    var attach = new PlanTask { Id = "original_indices", Kind = "foreach", MaxItems = 2000,
                        Objective = "Attach observation positions", Items = Flatten(ProductTransformationPlan.Ref("original_records", "records")),
                        Body = new() { Outputs = [new("rows", new() { Kind = "object", Members = [new("position", new() { Kind = "index" }), new("original", new() { Kind = "item" })] })] } };
                    var retain = new PlanTask { Id = "indexed_originals", Kind = "foreach", MaxItems = 2000,
                        Objective = "Retain exact indexed observations", Items = ProductTransformationPlan.Ref("original_indices", "rows"),
                        Body = new() { Outputs = [new("rows", Field(new() { Kind = "item" }, "original"))] } };
                    plan.Root.Tasks.InsertRange(plan.Root.Tasks.IndexOf(resolve), [attach, retain]);
                    resolve.Outputs[0].Value.Items[0] = ProductTransformationPlan.Ref("indexed_originals", "rows");
                }
                var products = plan.Root.Tasks.Single(t => t.Id == "products"); products.Items = ProductTransformationPlan.Ref("action_records", "records");
                var visit = products.Body!.Tasks.Single(t => t.Id == "page"); var url = Field(new() { Kind = "item" }, "href");
                visit.Requires = new() { Kind = "predicate", Predicate = "not_equal", Items = [url, new() { Kind = "null" }] };
                visit.Inputs = [new("url", url)];
            }
            if (variant.StartsWith("required-", StringComparison.Ordinal))
            {
                TaskValue Field(TaskValue value, string name) => new() { Kind = "field", Port = name, Items = [value] };
                TaskValue Not(TaskValue value) => new() { Kind = "predicate", Predicate = "not", Items = [value] };
                TaskValue And(TaskValue left, TaskValue right) => new() { Kind = "predicate", Predicate = "and", Items = [left, right] };
                var observation = ProductTransformationPlan.Ref("inspect", "observation");
                plan.Root.Tasks.Single(t => t.Id == "consent").Requires = And(Not(ProductTransformationPlan.Ref("inspect", "truncated")),
                    And(Not(Field(observation, "captureTruncated")), new() { Kind = "predicate", Predicate = "equal", Items = [Field(observation, "nextCursor"), new() { Kind = "null" }] }));
            }
            if (variant == "observation")
            {
                foreach (var task in new[] { plan.Root.Tasks.Single(t => t.Id == "search"), plan.Root.Tasks.Single(t => t.Id == "products").Body!.Tasks.Single(t => t.Id == "page") })
                    task.Inputs.Add(new("format", ProductTransformationPlan.Text("observation")));
                plan.Root.Tasks.Single(t => t.Id == "urls").Inputs = [new("page", ProductTransformationPlan.Ref("search", "observation"))];
                plan.Root.Tasks.Single(t => t.Id == "products").Body!.Tasks.Single(t => t.Id == "extract").Inputs = [new("page", ProductTransformationPlan.Ref("page", "observation"))];
                Assert.NotEmpty(catalog.Capabilities.Single(c => c.Method == "document_write").ArtifactContract!.Locations!);
            }
            if (variant is "extract" or "observation")
            {
                plan.Root.Tasks.Single(t => t.Id == "urls").Mode = "extract";
                plan.Root.Tasks.Single(t => t.Id == "products").Body!.Tasks.Single(t => t.Id == "extract").Mode = "extract";
            }
            var write = plan.Root.Tasks.Single(t => t.Id == "write"); write.Inputs[0] = new("filePath", ProductTransformationPlan.Text(variant == "denied" ? "../outside.xlsx" : ProductTransformationFixture.OutputPath));
            plan.Root.Outputs[0] = new("file", ProductTransformationPlan.Ref("write", "filePath"));
            plan.Root.Tasks.Single(t => t.Id == "products").MaxItems = 3;
            var proposal = new PlanningProposal { Plan = plan, Requirements = new() { Summary = "Read products and save an XLSX document, always close the browser", Inputs = plan.Inputs,
                Outputs = [new() { Name = "file", Type = new() { Kind = "string" } }],
                Outcomes = [new("search", "Read search results"), new("products", "Read each product"),
                    new("save", "Save workbook"), new("close", "Close browser")] } };
            var planning = new ProposalRuntime(runtime, proposal);
            var session = new PlanningSession { Catalog = catalog, Request = new() { Prompt = proposal.Requirements.Summary, TenantId = "local", Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 } } };
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 5 && !PlanningStatus.IsWaiting(session.Status) && !PlanningStatus.IsTerminal(session.Status); i++)
                session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, planning, ct);
            var planningMs = timer.Elapsed.TotalMilliseconds;
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join("; ", session.Diagnostics.Select(d => d.Code + " " + d.Location + " " + d.Message)));
            PlanningArtifactApproval.Verify(session); Assert.Equal(1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
            if (variant == "pages-compact-scoped")
            {
                var before = JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan);
                var paths = new List<string> { "/tasks/write/inputs", "/tasks/write/requires" };
                session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { Kind = "revise", PreserveRequirements = true,
                    ExpectedRevision = session.Revision, ArtifactHash = session.ComputeArtifactHash(), EditablePaths = paths,
                    Text = "Explicitly disable append and require the formatted table before writing; preserve all observations, product visits and cleanup." }, planning, ct);
                Assert.Equal(before, JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan));
                write.Inputs.Add(new("append", new() { Kind = "boolean", Boolean = false }));
                planning.Patch = request =>
                {
                    var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
                    var edits = context["repair"]!["slots"]!.AsArray().Select(slot => (JsonNode)new JsonObject
                    {
                        ["slot"] = slot!["id"]!.ToString(), ["action"] = "replace",
                        ["value"] = slot["location"]!.ToString().EndsWith("/inputs", StringComparison.Ordinal)
                            ? JsonSerializer.SerializeToNode(write.Inputs, PlanningJsonContext.Default.ListTaskOutput)
                            : new JsonObject { ["kind"] = "present", ["source"] = "table" }
                    }).ToArray();
                    return new() { ["discoveryRequests"] = null, ["clarifications"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray(edits) } };
                };
                session = JsonSerializer.SerializeToNode(session, PlanningJsonContext.Default.PlanningSession)!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
                session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, planning, ct);
                Assert.True(session.Status == PlanningStatus.FinalReview, string.Join(';', session.Diagnostics));
                Assert.Equal(2, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
                Assert.Equal(plan.Root.Tasks.Select(t => t.Id), session.Plan!.Root.Tasks.Select(t => t.Id));
                Assert.Equal(plan.Root.Always.Select(t => t.Id), session.Plan.Root.Always.Select(t => t.Id));
                Assert.Equal("write", session.Plan.Root.Outputs[0].Value.Source);
                PlanningArtifactApproval.Verify(session);
            }
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new() { Kind = "approve", ExpectedRevision = session.Revision,
                ArtifactHash = session.ComputeArtifactHash(), ReviewedRequirementIds = proposal.Requirements.Outcomes.Select(r => r.Id).ToList() }, planning, ct);
            Assert.Equal(PlanningStatus.Approved, session.Status);
            var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!)); timer.Restart();
            var result = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["search"] = site.Urls.Single() + "/search" }, ct);
            var executionMs = timer.Elapsed.TotalMilliseconds;
            output.WriteLine($"LOCAL {variant}: success={result.Success}, calls={session.ModelCalls}, discovery={reads}, repairs={session.ReplanAttempts}, inputEstimate={planning.InputEstimate}, executionAdapterCalls={model.Calls.Count}, planningMs={planningMs:F1}, executionMs={executionMs:F1}; error={result.Error?.Code} {result.Error?.Message}");
            Assert.Equal(compact || variant is "required-consent" or "nominal" or "changed" or "empty" or "missing" or "extract" or "observation" or "each" or "each-parallel" or "consent" or "no-consent" or "delayed-consent" or "pages" or "pages-parallel", result.Success);
            if (compact)
            {
                Assert.NotEmpty(compactModel.Requests);
                Assert.All(compactModel.Requests, r => Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)), 1, 96000));
                output.WriteLine($"COMPACT {variant}: mapping requests={compactModel.Requests.Count}; complete request estimates=[{string.Join(',', compactModel.Requests.Select(r => KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest))))}]");
            }
            if (compact)
            {
                var checkpoint = await engine.RunStore!.ReadAsync("local", "product-fixture", ct);
                Assert.Equal(WorkflowRunStatus.Completed, checkpoint!.Status);
                Assert.True(checkpoint.FinalizationCompleted);
                Assert.DoesNotContain(checkpoint.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
                if (indexed)
                {
                    var origin = Assert.Single(session.Graph!.Workflows.SelectMany(w => w.Steps), n => n.Purpose == "Attach observation positions").Key;
                    var indexing = Assert.Single(WorkflowParser.Parse(session.Yaml!).Workflows.Values.SelectMany(w => w.Steps),
                        s => s.Input?.ToJsonString().Contains(origin, StringComparison.Ordinal) == true);
                    Assert.Equal("set", indexing.Type);
                    Assert.Equal("set", Assert.Single(checkpoint.Invocations.Values, i => i.Id.EndsWith("/step/" + indexing.Id, StringComparison.Ordinal)).StepType);
                }
            }
            if (variant is "each" or "each-parallel") Assert.Equal(1, extractionModel.Calls);
            var browser = await transport.GetClientAsync("browser", ct);
            var afterCleanup = await browser.CallToolAsync("browser_get_content", new JsonObject(), ct);
            Assert.True(afterCleanup.IsError);
            Assert.Contains("No active page", afterCleanup.Content!["error_message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
            await browser.CallToolAsync("browser_close", new JsonObject(), ct);
            var file = Path.Combine(workspace, ProductTransformationFixture.OutputPath);
            if (!result.Success)
            {
                Assert.False(File.Exists(file)); Assert.False(File.Exists(Path.Combine(root, "outside.xlsx")));
                if (variant == "required-incomplete-observation")
                {
                    Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.Equal(0, consentModel.Calls); Assert.Empty(model.Calls);
                    Assert.Empty(consentReceipts); Assert.Equal(["/search"], visits.Where(v => v != "/favicon.ico")); return;
                }
                if (variant is "unrelated-modal" or "incomplete-observation")
                {
                    Assert.Contains("CONTRACT_UNSATISFIED", result.Error!.Code + result.Error.Message);
                    Assert.Empty(consentReceipts); Assert.Equal(["/search"], visits.Where(v => v != "/favicon.ico")); return;
                }
                Assert.Equal(variant == "limit" ? "INPUT_VALIDATION" : "MCP_CALL_ERROR", result.Error!.Code);
                if (variant == "limit") { Assert.Single(model.Calls); Assert.Equal(new[] { "/search" }, visits.Where(v => v != "/favicon.ico")); }
                else
                {
                    Assert.Equal(variant == "denied" ? "document_write" : "browser_get_content", result.Error.Details!["method"]!.ToString());
                    if (variant == "denied") Assert.Contains("POLICY_VIOLATION", result.Error.Details.ToJsonString(), StringComparison.Ordinal);
                    else Assert.Contains("/broken", visits);
                }
                return;
            }
            if (consentScenario) Assert.All(consentReceipts, accepted => Assert.Equal(variant is not ("no-consent" or "pages-compact-decision" or "pages-compact-decision-complete" or "pages-compact-decision-complete-flat" or "pages-compact-decision-complete-flat-lookup" or "pages-compact-decision-complete-flat-lookup-indexed"), accepted));
            Assert.Equal(file, result.Outputs!["file"]!.ToString());
            using var workbook = SpreadsheetDocument.Open(file, false);
            var rows = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            Assert.Equal(model.ExpectedRows.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++) Assert.Equal(model.ExpectedRows[i], rows[i]);
            var observedVisits = visits.Where(v => v != "/favicon.ico").ToArray();
            if (variant == "each-parallel")
            {
                Assert.Equal("/search", observedVisits[0]);
                Assert.Equal(new[] { "/one", "/two" }, observedVisits.Skip(1).Order(StringComparer.Ordinal));
                // Requests may arrive concurrently; workbook rows above must still
                // match source order exactly, regardless of completion order.
            }
            else Assert.Equal(variant == "empty" ? ["/search"] : variant == "missing" ? ["/search", "/missing"] : new[] { "/search", "/one", "/two" }, observedVisits);
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }

    private static void AddConsentSteps(TaskPlan plan, PlanningCatalog catalog, bool independent = false, bool complete = false)
    {
        string Operation(string method) { var contract = catalog.Capabilities.Single(c => c.Method == method); return contract.Operation?.Id ?? contract.Id; }
        var open = new PlanTask { Id = "open", Kind = "operation", Objective = "Open the requested page and observe visible blockers", Operation = Operation("browser_get_content"),
            Inputs = [new("url", new() { Kind = "input", Source = "search" }), new("format", ProductTransformationPlan.Text("observation"))] };
        var wait = new PlanTask { Id = "settle", Kind = "operation", Objective = "Allow a bounded interval for delayed visible controls", Operation = Operation("browser_wait"), DependsOn = ["open"],
            Inputs = [new("delayMs", new() { Kind = "number", Number = 200 })] };
        var observe = new PlanTask { Id = "inspect", Kind = "operation", Objective = "Inspect current controls after the bounded wait", Operation = Operation("browser_get_content"), DependsOn = ["settle"],
            Inputs = [new("format", ProductTransformationPlan.Text("observation"))] };
        var decision = new PlanTask { Id = "consent", Kind = "transform", Objective = "Inspect the complete observation. Identify only an unambiguous visible cookie-acceptance control. Return accept=false when absent. Reject unrelated or ambiguous blockers and incomplete observations; do not guess a selector.",
            Inputs = [new("page", ProductTransformationPlan.Ref("inspect"))], ResultType = ProductTransformationPlan.Obj(("accept", new() { Kind = "boolean" }), ("reference", new() { Nullable = true })) };
        var gate = new PlanTask { Id = "consent_gate", Kind = "conditional", Objective = "Accept cookies only when that observed control is present", Condition = new() { Kind = "predicate", Predicate = "and", Items = [ProductTransformationPlan.Ref("consent", "accept"),
                new() { Kind = "predicate", Predicate = "not_equal", Items = [ProductTransformationPlan.Ref("consent", "reference"), new() { Kind = "null" }] }] },
            Body = new() { Tasks = [new() { Id = "accept_observed", Kind = "operation", Objective = "Click the observed cookie control", Operation = Operation("browser_click"), Inputs = [new("reference", ProductTransformationPlan.Ref("consent", "reference")), new("requestedAction", ProductTransformationPlan.Text("activate"))] }] }, Otherwise = new() };
        var search = plan.Root.Tasks.Single(t => t.Id == "search"); search.DependsOn = ["consent_gate"];
        search.Inputs = [new("selector", ProductTransformationPlan.Text("main"))];
        search.Objective = "Read a fresh targeted search observation after any consent action";
        if (complete)
        {
            gate.Body!.Tasks.Add(new() { Id = "observe_after_accept", Kind = "operation", Objective = "Acquire complete observations after the interaction",
                Operation = open.Operation, DependsOn = ["accept_observed"], Inputs = [new("format", ProductTransformationPlan.Text("observation_complete"))] });
            foreach (var (branch, source) in new[] { (gate.Body!, "observe_after_accept"), (gate.Otherwise!, "inspect") })
            {
                TaskValue Flag(string name) => new() { Kind = "field", Port = name, Items = [ProductTransformationPlan.Ref(source, "observationSnapshot")] };
                TaskValue Not(TaskValue value) => new() { Kind = "predicate", Predicate = "not", Items = [value] };
                branch.Outputs = [new("ready", new() { Kind = "predicate", Predicate = "and", Items = [Not(Flag("captureTruncated")), Not(Flag("manifestTruncated"))] }),
                    new("url", ProductTransformationPlan.Ref(source, "url")), new("title", ProductTransformationPlan.Ref(source, "title")),
                    new("observedControl", ProductTransformationPlan.Ref("consent", "reference"))];
            }
            // Consume direct boolean and string ports after the merge; nullable
            // controls stay nullable and are not used as a successful empty value.
            search.Requires = new() { Kind = "predicate", Predicate = "and", Items = [ProductTransformationPlan.Ref("consent_gate", "ready"),
                new() { Kind = "predicate", Predicate = "not_equal", Items = [ProductTransformationPlan.Ref("consent_gate", "url"), ProductTransformationPlan.Text("")] }] };
        }
        plan.Root.Tasks.InsertRange(0, [open, wait, observe, decision, gate]);
        if (independent)
        {
            TaskValue Field(TaskValue value, string name) => new() { Kind = "field", Port = name, Items = [value] };
            TaskValue Not(TaskValue value) => new() { Kind = "predicate", Predicate = "not", Items = [value] };
            var manifest = ProductTransformationPlan.Ref("inspect", "observationManifest");
            observe.Inputs = [new("format", ProductTransformationPlan.Text("observation_pages")), new("maxRecords", new() { Kind = "number", Number = 2 })];
            var pages = new PlanTask { Id = "decision_pages", Kind = "foreach", Objective = "Consume every complete manifest page before deciding or interacting", MaxItems = 100,
                Items = Field(manifest, "pages"), Requires = new() { Kind = "predicate", Predicate = "and", Items = [Not(Field(manifest, "captureTruncated")), Not(Field(manifest, "manifestTruncated"))] },
                Body = new() { Tasks = [new() { Id = "decision_page", Kind = "operation", Operation = observe.Operation, Objective = "Read the original complete page",
                    Inputs = [new("format", ProductTransformationPlan.Text("observation")), new("cursor", Field(new() { Kind = "item" }, "cursor"))] }],
                    Outputs = [new("observation", ProductTransformationPlan.Ref("decision_page", "observation"))] } };
            var extract = new PlanTask { Id = "decision_candidates", Kind = "transform", Mode = "extract", Each = new("records", "candidates"),
                Objective = "From each observed record, extract a control's exact text, observed reference and group when present. Return one candidate array per record, empty for other observed kinds. Do not decide which action is authorized.",
                Inputs = [new("records", Field(ProductTransformationPlan.Ref("decision_page", "observation"), "records"))],
                ResultType = ProductTransformationPlan.Obj(("candidates", new() { Kind = "array", Items = new() { Kind = "array",
                    Items = ProductTransformationPlan.Obj(("text", new()), ("reference", new()), ("group", new())) } })) };
            pages.Body.Tasks.Add(extract);
            pages.Body.Outputs = [new("candidates", ProductTransformationPlan.Ref("decision_candidates", "candidates"))];
            decision.Inputs = [new("candidates", ProductTransformationPlan.Ref("decision_pages", "candidates"))];
            plan.Root.Tasks.Insert(plan.Root.Tasks.IndexOf(decision), pages);
        }
    }

    private sealed class ConsentModel(ILLMClient next, bool flattened = false) : ILLMClient
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.StructuredOutputSchema?["properties"]?["accept"] is null) return next.CallAsync(request, ct);
            Calls++;
            var start = request.Prompt.IndexOf('{', request.Prompt.LastIndexOf("Business data", StringComparison.Ordinal));
            var data = JsonNode.Parse(request.Prompt[start..])!; var page = data["page"];
            if (page is not null && (page["truncated"]!.GetValue<bool>() || page["observation"]?["nextCursor"] is not null || page["observation"]!["captureTruncated"]!.GetValue<bool>()))
                throw new WorkflowRuntimeException("CONTRACT_UNSATISFIED", "The observation is incomplete; narrow or continue it before deciding.");
            var controls = page is null ? (flattened ? data["candidates"]!.AsArray().ToArray() : data["candidates"]!.AsArray().SelectMany(p => p!.AsArray()).SelectMany(r => r!.AsArray()).ToArray())
                : page["observation"]!["records"]!.AsArray().Where(r => r!["kind"]!.ToString() == "control").ToArray();
            if (page is null)
            {
                if (flattened) Assert.All(data["candidates"]!.AsArray(), c => Assert.Equal(new[] { "text", "reference", "group" }, c!.AsObject().Select(p => p.Key)));
                else Assert.True(data["candidates"]!.AsArray().Count > 3);
                Assert.DoesNotContain("irrelevant-observation", request.Prompt);
                Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)), 1, 12000);
            }
            if (controls.Any(r => r!["text"]!.ToString() != "Accept cookies"))
                throw new WorkflowRuntimeException("CONTRACT_UNSATISFIED", "The observed blocker is not authorized cookie consent.");
            Assert.InRange(controls.Length, 0, 1);
            if (controls.Length == 1) Assert.Equal("#notice", controls[0]!["group"]!.ToString());
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["accept"] = controls.Length == 1, ["reference"] = controls.SingleOrDefault()?["reference"]?.ToString() } });
        }
    }

    internal sealed class ProposalRuntime(WorkflowPlanningRuntime actual, PlanningProposal proposal) : IPlanningRuntime
    {
        public int InputEstimate;
        public Func<LLMRequest, JsonObject>? Patch;
        public ICapabilityCatalog Capabilities => actual.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => actual.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            InputEstimate = (System.Text.Encoding.UTF8.GetByteCount(request.Prompt) + System.Text.Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256;
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(Patch?.Invoke(request) ?? JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()) });
        }
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => actual.ValidateCatalogAsync(catalog, ct);
    }
    private sealed class PagedModel(ILLMClient next, bool compact = false, bool flattened = false, bool lookup = false) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.StructuredOutputSchema?["properties"]?[lookup ? "ids" : "urls"] is null) return next.CallAsync(request, ct);
            var start = request.Prompt.IndexOf('{', request.Prompt.LastIndexOf("Business data", StringComparison.Ordinal));
            var pages = JsonNode.Parse(request.Prompt[start..])!["pages"]!.AsArray();
            if (flattened) Assert.Equal(2, pages.Count); else Assert.True(pages.Count > 3);
            if (!compact)
            {
                Assert.All(pages, p => Assert.False(p!["captureTruncated"]!.GetValue<bool>()));
                Assert.Null(pages[^1]!["nextCursor"]);
            }
            else
            {
                Assert.DoesNotContain("irrelevant-observation", request.Prompt);
                if (flattened) Assert.All(pages, p => Assert.Equal(lookup ? new[] { "candidateId", "text" } : new[] { "text", "href", "group" }, p!.AsObject().Select(f => f.Key)));
                else Assert.True(pages.Sum(p => p!.AsArray().Count) > 120);
                Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)), 1, 12000);
            }
            var records = flattened ? pages : compact ? pages.SelectMany(p => p!.AsArray()).SelectMany(r => r!.AsArray()) : pages.SelectMany(p => p!["records"]!.AsArray()).Where(r => r!["kind"]!.ToString() == "link");
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [lookup ? "ids" : "urls"] = new JsonArray(records.Select(r => r![lookup ? "candidateId" : "href"]!.DeepClone()).ToArray()) } });
        }
    }

    private sealed class CompactModel(ILLMClient next) : ILLMClient, ILLMCapabilityResolver
    {
        internal List<LLMRequest> Requests { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.StructuredOutputSchema?["properties"]?["script"] is null) return next.CallAsync(request, ct);
            Requests.Add(request);
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = context["item_target"]?["items"]?["properties"]?["candidateId"] is not null
                ? "[item].filter(r=>m.test(r.kind,'^link$')).map(r=>({candidateId:r.reference,text:r.text}))"
                : context["item_target"]?["items"]?["properties"]?["reference"] is not null
                ? "[item].filter(r=>m.test(r.kind,'^control$')).map(r=>({text:r.text,reference:r.reference,group:r.group}))"
                : "[item].filter(r=>m.test(r.kind,'^link$')).map(r=>({text:r.text,href:r.href,group:r.group}))" } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private sealed class ExtractModel(ILLMClient interpretation) : ILLMClient, ILLMCapabilityResolver
    {
        public int Calls;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.StructuredOutputSchema?["properties"]?["script"] is null) return interpretation.CallAsync(request, ct);
            Calls++;
            var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            var observation = data["source"]?["page"] is not null;
            if (observation) Assert.NotEmpty(data["source"]!["page"]!["records"]!.AsArray());
            var script = observation ? data["target"]?["properties"]?["urls"] is not null
                ? "({urls:source.page.records.filter(r=>m.test(r.kind,'^link$')).map(r=>r.href)})"
                : "({name:source.page.records.filter(r=>m.test(r.tag,'^h1$'))[0].text,description:source.page.records.filter(r=>m.test(r.tag,'^p$'))[0].text,price:source.page.records.filter(r=>m.test(r.tag,'^price$'))[0].text})"
                : data["target"]?["properties"]?["urls"] is not null
                ? "({urls:m.texts(source.html," + JsonValue.Create("href=[\"']([^\"']+)")!.ToJsonString() + ")})"
                : "({name:m.decode(m.text(source.html,'<h1>([^<]+)</h1>')),description:m.decode(m.text(source.html,'<p>(.*?)</p>')),price:m.text(source.html,'<price>(.*?)</price>')})";
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = script } });
        }
    }
}
