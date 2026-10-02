using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
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

public sealed class LocalProductOutcomeExecutionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("nominal")]
    [InlineData("changed")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("navigation")]
    [InlineData("denied")]
    [InlineData("limit")]
    public async Task RealStdioContractsAndLocalBrowserExecutionProduceIndependentlyVerifiedWorkbook(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-local-products-").FullName;
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0)); builder.Logging.ClearProviders();
        await using var site = builder.Build();
        var visits = new List<string>();
        site.MapGet("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value!; visits.Add(path);
            if (path == "/broken") { context.Abort(); return; }
            context.Response.ContentType = "text/html; charset=utf-8";
            var origin = "http://" + context.Request.Host;
            var links = variant switch { "empty" => Array.Empty<string>(), "missing" => ["missing"], "navigation" => ["broken"], "limit" => ["one", "two", "three", "four"], _ => ["one", "two"] };
            var body = path == "/search" ? string.Join("", links.Select(p => "<a href='" + origin + "/" + p + "'>Product</a>")) : path == "/missing" ? "<h1>Only a name</h1>" :
                path == "/one" ? variant == "changed" ? "<h1>Changed lamp</h1><p>New description</p><price>9,90 €</price>" : "<h1>Lampe été, &quot;A&quot;</h1><p>Bright and small {{values}}</p><price>19,99 €</price>" :
                variant == "changed" ? "<h1>Second</h1><p>Updated</p><price>12.00</price>" : "<h1>東京 lamp</h1><p>Line one\nLine two</p><price>25.00</price>";
            await context.Response.WriteAsync("<html><body><main>" + body + "</main></body></html>", ct);
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
                    ["Browser__SlowMoMs"] = "0", ["Browser__HoldOpenMs"] = "0", ["Browser__NavigationTimeoutMs"] = "3000", ["OpenTelemetry__Enabled"] = "false" }),
                ["document"] = Server("Document", new() { ["Document__DefaultWorkingDirectory"] = workspace, ["OpenTelemetry__Enabled"] = "false" })
            });
            var model = new ProductTransformationFixture(variant) { ReadMethod = "browser_get_content", WriteMethod = "document_write", CloseMethod = "browser_close" };
            var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = model, HumanInputProvider = new PlanningCorpus.Human(true), LlmDefaults = new() { Model = "deterministic" } };
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
            var write = plan.Root.Tasks.Single(t => t.Id == "write"); write.Inputs[0] = new("filePath", ProductTransformationPlan.Text(variant == "denied" ? "../outside.xlsx" : ProductTransformationFixture.OutputPath));
            plan.Root.Outputs[0] = new("file", ProductTransformationPlan.Ref("write", "filePath"));
            plan.Root.Tasks.Single(t => t.Kind == "foreach").MaxItems = 3;
            var proposal = new PlanningProposal { Plan = plan, Requirements = new() { Summary = "Read products and save an XLSX document, always close the browser", Inputs = plan.Inputs,
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
            var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!)); timer.Restart();
            var result = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["search"] = site.Urls.Single() + "/search" }, ct);
            var executionMs = timer.Elapsed.TotalMilliseconds;
            output.WriteLine($"LOCAL {variant}: success={result.Success}, calls={session.ModelCalls}, discovery={reads}, repairs={session.ReplanAttempts}, inputEstimate={planning.InputEstimate}, executionAdapterCalls={model.Calls.Count}, planningMs={planningMs:F1}, executionMs={executionMs:F1}; error={result.Error?.Code} {result.Error?.Message}");
            Assert.Equal(variant is "nominal" or "changed" or "empty" or "missing", result.Success);
            var browser = await transport.GetClientAsync("browser", ct);
            var afterCleanup = await browser.CallToolAsync("browser_get_content", new JsonObject(), ct);
            Assert.True(afterCleanup.IsError);
            Assert.Contains("No active page", afterCleanup.Content!["error_message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
            await browser.CallToolAsync("browser_close", new JsonObject(), ct);
            var file = Path.Combine(workspace, ProductTransformationFixture.OutputPath);
            if (!result.Success)
            {
                Assert.False(File.Exists(file)); Assert.False(File.Exists(Path.Combine(root, "outside.xlsx")));
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
            Assert.Equal(file, result.Outputs!["file"]!.ToString());
            using var workbook = SpreadsheetDocument.Open(file, false);
            var rows = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            Assert.Equal(model.ExpectedRows.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++) Assert.Equal(model.ExpectedRows[i], rows[i]);
            Assert.Equal(variant == "empty" ? ["/search"] : variant == "missing" ? ["/search", "/missing"] : new[] { "/search", "/one", "/two" }, visits.Where(v => v != "/favicon.ico"));
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }

    internal sealed class ProposalRuntime(WorkflowPlanningRuntime actual, PlanningProposal proposal) : IPlanningRuntime
    {
        public int InputEstimate;
        public ICapabilityCatalog Capabilities => actual.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => actual.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            InputEstimate = (System.Text.Encoding.UTF8.GetByteCount(request.Prompt) + System.Text.Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256;
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()) });
        }
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => actual.ValidateCatalogAsync(catalog, ct);
    }
}
