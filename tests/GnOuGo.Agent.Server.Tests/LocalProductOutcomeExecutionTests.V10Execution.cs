using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.AI.Core;
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
    [Theory]
    [InlineData("complete")]
    [InlineData("repeated")]
    [InlineData("missing")]
    [InlineData("one")]
    [InlineData("empty")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    public Task V10CompositionExecutesOriginalObservationsAndExactWorkbook(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v10");

    private async Task ExecuteTypedComposition(string variant, string profile, bool designer = false, bool approval = false, bool viaHost = false, bool producerStatus = false, bool guards = false, string? automaticCorrection = null, bool nullableGuards = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-v10-execution-").FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var visits = new List<string>();
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        await using var site = builder.Build();
        site.MapGet("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value!; visits.Add(path);
            string body;
            if (designer && path == "/")
            {
                body = "<form action='/search' method='get'><input aria-label='Search catalogue' name='q' /><button>Search</button></form>";
            }
            else if (path == "/search")
            {
                Assert.Equal("observed lamps", context.Request.Query["q"].ToString());
                body = "<h1>Observed catalogue</h1>" + string.Concat(Enumerable.Range(0, 24).Select(i => "<p>noise-" + i + new string('z', 80) + "</p>"));
                if (variant != "empty") body += string.Concat(Enumerable.Range(0, 12).Select(i => "<a " + ((designer || approval) && variant == "null_href" && i == 2 ? "data-unused='http://" : "href='http://") + context.Request.Host +
                    (variant == "redirected" ? "/relay?record=" + i + "&amp;source=" : "/entry/" + i + "?source=") + new string('u', 300) + "'>Observed choice " + i + "</a>"));
            }
            else if (variant == "redirected" && path == "/relay")
            {
                context.Response.Redirect("/entry/" + context.Request.Query["record"] + "?resolved=true"); return;
            }
            else if (path.StartsWith("/entry/", StringComparison.Ordinal))
            {
                var i = int.Parse(path[7..], System.Globalization.CultureInfo.InvariantCulture);
                body = "<h1>" + V10Name(i) + "</h1>" + (variant == "missing" && i == 3 ? "" : "<p>" + V10Description(i) + "</p>") +
                    (variant == "missing" && i == 2 ? "" : "<price>" + V10Price(i) + "</price>") + "<button>Report a problem</button>";
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
            var events = new List<JsonObject>();
            var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, e => { events.Add(e); return Task.CompletedTask; });
            var model = new V10Model(variant, site.Urls.Single(), designer, approval);
            var store = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners"));
            var runMetrics = Environment.GetEnvironmentVariable("GNOU_GO_V13_METRICS") is { Length: > 0 } ? new RunMetrics(store) : null;
            var engine = new WorkflowEngine { McpClientFactory = observed, LLMClient = model, RunStore = (IWorkflowRunStore?)runMetrics ?? store,
                LLMUsageBudget = new(new() { MaxElapsed = TimeSpan.FromMinutes(2) }), LlmDefaults = new() { Model = "deterministic" },
                Limits = new() { TenantId = "local", RunId = "v10-" + variant } };
            var checkMetrics = Environment.GetEnvironmentVariable("GNOU_GO_V13_METRICS") is { Length: > 0 } ? new CheckMetrics() : null;
            if (checkMetrics is not null) engine.Telemetry = checkMetrics;
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await RetainedCatalog(runtime, ct); catalog.Policy.RequireExternalConfirmation = false;
            var proposal = nullableGuards ? NullableGuardComposition(catalog) : guards ? GuardCorrected(catalog) : approval ? ApprovalCorrected(catalog, site.Urls.Single()) : designer ? DesignerCorrected(catalog, site.Urls.Single()) : V10Proposal(catalog, 4);
            if (producerStatus) AddProducerStatus(proposal);
            var writerTask = proposal.Plan!.Root.Tasks.Single(t => t.Id == (designer || approval ? "write" : "save_products_xlsx"));
            if (variant == "denied") writerTask.Inputs.Single(i => i.Name == "filePath").Value.Text = "../outside.xlsx";
            var session = nullableGuards && variant != "denied" ? await NullableGuardRevision(catalog, runtime, proposal, ct) : automaticCorrection is not null ? await AutomaticCompositionSession(catalog, runtime, proposal, automaticCorrection, ct) : guards && variant != "denied" ? await GuardRevision(catalog, runtime, proposal, ct) : approval && variant == "denied" ? await V10PlanningSession(catalog, runtime, proposal, false, ct, profile) : approval ? await DesignerRevision(catalog, runtime, proposal, ct, site.Urls.Single(), approval: true) : designer ? await DesignerRevision(catalog, runtime, proposal, ct, site.Urls.Single()) : await V10PlanningSession(catalog, runtime, proposal, false, ct, profile);
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join('\n', session.Diagnostics));
            Assert.Equal(0, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
            Assert.Empty(visits); Assert.Empty(model.Requests); Assert.Null(session.ApprovedHash);
            Assert.Equal(profile, session.Request.Options["compilation_profile"]?.ToString());
            var document = WorkflowParser.Parse(session.Yaml!);
            var generatedSteps = document.Workflows.Values.SelectMany(w => V11Steps(w.Steps.Concat(w.Finally ?? []))).ToArray();
            if (designer || approval) { Assert.Contains("expression_contracts:", session.Yaml!); Assert.DoesNotContain("checkedMapping(", session.Yaml!); }
            else if (profile is "compact-bindings-v13" or "compact-bindings-v14" or "compact-bindings-v15")
            {
                Assert.True(generatedSteps.Length < 61);
                Assert.True(generatedSteps.Count(s => s.Type == "set") < 27);
            }
            else
            {
                Assert.Equal(profile == "compact-bindings-v10" ? 67 : 61, generatedSteps.Length);
                Assert.Equal(profile == "compact-bindings-v10" ? 33 : 27, generatedSteps.Count(s => s.Type == "set"));
            }
            var workflow = new WorkflowCompiler().Compile(document);
            var allocated = GC.GetTotalAllocatedBytes(); var timer = Stopwatch.StartNew();
            var inputs = new JsonObject { [nullableGuards ? "query" : designer || approval ? "article" : "query"] = "observed lamps" };
            GnOuGo.Flow.Core.Models.RunResult result;
            if (viaHost)
            {
                await using var host = BudgetHost.Create(session.Yaml!, model, observed,
                    ["--Flow:Execution:LlmBudget:MaxElapsed=00:02:00"], store);
                var execution = await host.ExecuteAsync(engine.Limits.RunId!, ct, inputs);
                Assert.DoesNotContain(execution, e => e.Type == "human_input_request");
                result = (await store.ReadAsync("local", engine.Limits.RunId!, ct))!.Result!;
            }
            else result = await engine.ExecuteAsync(workflow.Workflows[workflow.Entrypoint!], inputs, ct);
            allocated = GC.GetTotalAllocatedBytes() - allocated;
            var journal = (await store.ReadAsync("local", engine.Limits.RunId, ct))!;
            Assert.True(journal.FinalizationCompleted, $"{result.Error?.Code}: {result.Error?.Message}; {result.Error?.Details}");
            Assert.DoesNotContain(journal.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
            Assert.Equal("completed", Assert.Single(journal.Invocations.Values, i => i.StepType == "mcp.call" && i.ResolvedInput?["method"]?.ToString() == "browser_close").Status);
            var closed = await (await transport.GetClientAsync("browser", ct)).CallToolAsync("browser_get_content", new JsonObject(), ct);
            Assert.True(closed.IsError); Assert.Contains("No active page", closed.Content!["error_message"]!.ToString());
            var path = Path.Combine(workspace, writerTask.Inputs.Single(i => i.Name == "filePath").Value.Text!);
            output.WriteLine($"{profile} RUNTIME {variant}: success={result.Success}; elapsedMs={timer.ElapsedMilliseconds}; tasks={RetainedTasks(proposal.Plan.Root).Count()}; YAML={Encoding.UTF8.GetByteCount(session.Yaml!)}; " +
                $"inference={model.Requests.Count}; mappings={model.MappingCalls}; invocations={journal.Invocations.Count}; steps={journal.StepsStarted}; cleanup={journal.FinalizationStepsStarted}; allocatedBytes={allocated}; " +
                $"logicalJournalBytes={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(journal, WorkflowRunJsonContext.Default.WorkflowRun))}; physicalBytes={Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)}; error={result.Error?.Code} {result.Error?.Message}");
            if (Environment.GetEnvironmentVariable("GNOU_GO_V13_METRICS") is { Length: > 0 } metricsFolder)
            {
                Directory.CreateDirectory(metricsFolder);
                var metrics = new JsonObject { ["profile"] = profile, ["variant"] = variant, ["success"] = result.Success,
                    ["elapsed_ms"] = timer.ElapsedMilliseconds, ["allocated_bytes"] = allocated,
                    ["invocations"] = journal.Invocations.Count, ["steps_started"] = journal.StepsStarted, ["finalization_steps"] = journal.FinalizationStepsStarted,
                    ["inference_calls"] = model.Requests.Count, ["mapping_calls"] = model.MappingCalls,
                    ["logical_journal_bytes"] = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(journal, WorkflowRunJsonContext.Default.WorkflowRun)),
                    ["physical_store_bytes"] = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                    ["check_events"] = checkMetrics?.Events.Count, ["check_event_bytes"] = checkMetrics?.Bytes,
                    ["checkpoint_count"] = runMetrics?.Checkpoints, ["checkpoint_ms"] = runMetrics?.CheckpointMilliseconds,
                    ["snapshot_captures"] = runMetrics?.Captures, ["snapshot_capture_ms"] = runMetrics?.CaptureMilliseconds,
                    ["snapshot_restores"] = runMetrics?.Restores, ["snapshot_restore_ms"] = runMetrics?.RestoreMilliseconds,
                    ["decision_request_bytes"] = model.DecisionBytes,
                    ["yaml_bytes"] = Encoding.UTF8.GetByteCount(session.Yaml!) };
                File.WriteAllText(Path.Combine(metricsFolder, profile + "-" + variant + ".json"), metrics.ToJsonString(new() { WriteIndented = true }));
                File.WriteAllLines(Path.Combine(metricsFolder, profile + "-" + variant + "-checks.txt"), checkMetrics!.Events);
            }
            if (variant is "unoffered" or "unknown" or "incomplete" or "denied" || guards && variant is "null_href" or "empty_selection")
            {
                Assert.False(result.Success); Assert.False(File.Exists(path)); Assert.False(File.Exists(Path.Combine(root, "outside.xlsx")));
                if (guards && variant == "null_href") Assert.Equal(new[] { "/entry/0", "/entry/1" }, visits.Where(p => p.StartsWith("/entry/", StringComparison.Ordinal)));
                else if (variant != "denied") Assert.DoesNotContain(visits, p => p.StartsWith("/entry/", StringComparison.Ordinal));
                if (variant == "incomplete")
                {
                    // Designer first acquires a complete search-control page;
                    // only the subsequent search snapshot exceeds the page bound.
                    Assert.Equal(designer ? 1 : 0, model.MappingCalls);
                    Assert.Null(model.Offered);
                }
                Assert.DoesNotContain(events, e => e["tool"]?.ToString() == "document_write" && e["error"]?.GetValue<bool>() == false);
                if (automaticCorrection == "commitment") SaveCommitmentOutcome(variant, result.Success, visits, model.Requests, null, true, null);
                return;
            }
            Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message}; {result.Error?.Details}");
            var selected = variant == "empty" ? [] : variant == "one" ? [0] : variant == "repeated" ? Enumerable.Range(0, 9).Append(0).ToArray() : Enumerable.Range(0, 10).ToArray();
            Assert.Equal(selected.Where(i => !((designer || approval) && variant == "null_href" && i == 2)).Select(i => "/entry/" + i), visits.Where(p => p.StartsWith("/entry/", StringComparison.Ordinal)));
            if (variant == "redirected")
            {
                Assert.Equal(10, visits.Count(p => p == "/relay"));
                Assert.Equal(10, events.Count(e => e["tool"]?.ToString() == "browser_get_content" &&
                    e["arguments"]?["url"]?.ToString().Contains("/relay?record=", StringComparison.Ordinal) == true));
            }
            Assert.Equal(path, result.Outputs![designer || approval ? "filePath" : "productsFile"]!.ToString());
            var products = designer || approval ? null : result.Outputs["products"]!.AsArray(); if (products is not null) Assert.Equal(selected.Length, products.Count);
            if (designer)
            {
                Assert.Contains("/", visits); Assert.Contains("/search", visits); Assert.Equal("completed", result.Outputs["status"]!.ToString());
                var fillEvent = Assert.Single(events, e => e["tool"]?.ToString() == "browser_fill");
                Assert.Equal("observed lamps", fillEvent["arguments"]!["value"]!.ToString());
                Assert.True(fillEvent["arguments"]!["submit"]!.GetValue<bool>());
                var homeRecords = events[0]["result"]!["observationSnapshot"]!["pages"]!.AsArray().SelectMany(p => p!["records"]!.AsArray());
                Assert.Single(homeRecords, r => r!["reference"]?.ToString() == fillEvent["arguments"]!["reference"]!.ToString() && r["tag"]?.ToString() == "input");
                var searchEvent = Assert.Single(events, e => e["tool"]?.ToString() == "browser_get_content" && e["arguments"]?["url"] is null);
                var offeredOriginals = searchEvent["result"]!["observationSnapshot"]!["pages"]!.AsArray().SelectMany(p => p!["records"]!.AsArray()).Where(r => r!["tag"]?.ToString() == "a").ToArray();
                Assert.Equal(selected.Select(i => offeredOriginals[i]!["href"]?.ToString()).Where(url => url is not null),
                    events.Where(e => e["tool"]?.ToString() == "browser_get_content" && e["arguments"]?["url"]?.ToString().Contains("/entry/", StringComparison.Ordinal) == true).Select(e => e["arguments"]!["url"]!.ToString()));
            }
            if (approval)
            {
                if (nullableGuards)
                {
                    Assert.True(result.Outputs!["success"]!.GetValue<bool>());
                    Assert.Equal("completed", result.Outputs["status"]!.ToString()); Assert.Empty(result.Outputs["errors"]!.AsArray());
                }
                else if (guards) Assert.Equal(new[] { "filePath" }, result.Outputs!.AsObject().Select(p => p.Key));
                else
                {
                    Assert.True(result.Outputs!["status"]!["saved"]!.GetValue<bool>());
                    Assert.True(result.Outputs["status"]!["success"]!.GetValue<bool>());
                    Assert.Null(result.Outputs["status"]!["dataRowCount"]);
                }
                Assert.DoesNotContain(events, e => e["tool"]?.ToString() == "browser_fill");
                var originalLinks = events.First(e => e["tool"]?.ToString() == "browser_get_content")["result"]!["observationSnapshot"]!["pages"]!.AsArray()
                    .SelectMany(p => p!["records"]!.AsArray()).Where(r => r!["tag"]?.ToString() == "a").ToArray();
                Assert.Equal(selected.Select(i => originalLinks[i]!["href"]?.ToString()).Where(url => url is not null),
                    events.Where(e => e["tool"]?.ToString() == "browser_get_content" && e["arguments"]?["url"]?.ToString().Contains("/entry/", StringComparison.Ordinal) == true).Select(e => e["arguments"]!["url"]!.ToString()));
            }
            string[][] rows;
            using (var workbook = SpreadsheetDocument.Open(path, false))
            {
                var sheet = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!;
                rows = sheet.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
                Assert.Empty(sheet.Descendants<CellFormula>());
            }
            Assert.Equal(selected.Length + 1, rows.Length); Assert.Equal(approval ? ["Nom", "Description", "Prix"] : designer ? ["Nom", "Description", "Prix", "Statut"] : new[] { "name", "description", "price", "error" }, rows[0]);
            for (var row = 0; row < selected.Length; row++)
            {
                var i = selected[row]; var description = variant == "missing" && i == 3 ? null : V10Description(i); var price = variant == "missing" && i == 2 ? null : V10Price(i);
                var unavailable = (designer || approval) && variant == "null_href" && i == 2;
                Assert.Equal(approval ? (unavailable ? ["", "", ""] : new[] { V10Name(i), description ?? "", price ?? "" }) : unavailable ? new[] { "", "", "", "href null; page not visited" } :
                    new[] { V10Name(i), description ?? "", price ?? "", designer ? "observed" : "" }, rows[row + 1]);
                if (products is not null) {
                    Assert.Equal(V10Name(i), products[row]!["name"]!.ToString()); Assert.Equal(description, products[row]!["description"]?.ToString());
                    Assert.Equal(price, products[row]!["price"]?.ToString()); Assert.Null(products[row]!["error"]);
                }
            }
            var oracle = LiveWorkflowOracles.VerifyObservedProducts(events, rows, 10);
            if (variant is not ("missing" or "empty" or "null_href"))
            {
                Assert.True(oracle["passed"]!.GetValue<bool>(), oracle.ToJsonString());
                var fabricated = rows.Select(r => r.ToArray()).ToArray(); fabricated[1][1] += " rewritten";
                Assert.False(LiveWorkflowOracles.VerifyObservedProducts(events, fabricated, 10)["passed"]!.GetValue<bool>());
            }
            // The unchanged live oracle conservatively rejects unsupported absence
            // claims and a zero-product business outcome. Fixture ground truth above
            // independently proves exact nullable cells and header-only assembly.
            else Assert.False(oracle["passed"]!.GetValue<bool>());
            if (producerStatus)
            {
                var outcomes = result.Outputs!["acquisitions"]!.AsArray();
                Assert.Equal(selected.Length, outcomes.Count);
                foreach (var outcome in outcomes)
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"success":true,"availability":true,"completeness":true,"message":null}"""), outcome));
            }
            var mappings = journal.Invocations.Values.Where(i => i.StepType == "mapping.dynamic").ToArray();
            Assert.Equal(selected.Length + (designer ? 2 : 1) - ((designer || approval) && variant == "null_href" ? 1 : 0), mappings.Length);
            foreach (var mapping in mappings)
                Assert.Equal(mapping.ResolvedInput!["sources"]!["pages"]!.AsArray().Count, Assert.Single(mapping.Output!["value"]!.AsObject()).Value!.AsArray().Count);
            Assert.Equal(variant == "empty" ? 0 : 12, model.Offered!.Count);
            if (variant != "empty") Assert.Contains(model.Offered, c => !model.SampledReferences.Contains(c!["reference"]!.ToString()));
            Assert.All(model.Requests, r => Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)), 1, 12000));
            var writer = Assert.Single(events, e => e["tool"]?.ToString() == "document_write");
            Assert.Equal(path, writer["result"]!["filePath"]!.ToString());
            Assert.Equal(rows.Length, writer["arguments"]!["rows"]!.AsArray().Count);
            if (designer)
            {
                Assert.True(JsonNode.DeepEquals(result.Outputs!["rows"], writer["arguments"]!["rows"]));
                Assert.Equal(writer["result"]!["relativePath"]?.ToString(), result.Outputs!["relativePath"]?.ToString());
            }
            var calls = model.Requests.Count; var external = events.Count; var content = await File.ReadAllBytesAsync(path, ct);
            var recoveryStarted = Stopwatch.GetTimestamp(); var recoveryAllocated = GC.GetTotalAllocatedBytes();
            var restoredStore = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(new GnOuGo.KeyVault.Core.Services.KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners"));
            GnOuGo.Flow.Core.Models.RunResult restored;
            if (viaHost)
            {
                await using var restarted = BudgetHost.Create(session.Yaml!, model, observed,
                    ["--Flow:Execution:LlmBudget:MaxCalls=1"], restoredStore);
                var run = await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<GnOuGo.Agent.Server.SmartFlow.WorkflowRunService>(restarted.App.Services)
                    .CommandAsync(engine.Limits.RunId!, "resume", new(journal.Revision), ct);
                Assert.Equal(TimeSpan.FromMinutes(2), run.ModelBudget!.MaxElapsed);
                Assert.Equal(50, run.ModelBudget.MaxCalls); Assert.Equal(1_000_000, run.ModelBudget.MaxTotalTokens);
                Assert.Equal(model.Requests.Count * 30L, run.ModelUsage!.TotalTokens);
                restored = run.Result!;
            }
            else restored = await new WorkflowEngine { RunStore = restoredStore }
                .ResumeAsync("local", engine.Limits.RunId, journal.Revision, workflow.Workflows[workflow.Entrypoint!], ct);
            if (Environment.GetEnvironmentVariable("GNOU_GO_V13_METRICS") is { Length: > 0 } recoveryFolder)
                File.WriteAllText(Path.Combine(recoveryFolder, profile + "-" + variant + "-recovery.json"), new JsonObject
                {
                    ["elapsed_ms"] = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds,
                    ["allocated_bytes"] = GC.GetTotalAllocatedBytes() - recoveryAllocated,
                    ["repeated_inference_calls"] = model.Requests.Count - calls, ["repeated_external_calls"] = events.Count - external
                }.ToJsonString(new() { WriteIndented = true }));
            Assert.True(restored.Success, restored.Error?.Message); Assert.True(JsonNode.DeepEquals(result.Outputs, restored.Outputs));
            Assert.Equal(calls, model.Requests.Count); Assert.Equal(external, events.Count); Assert.Equal(content, await File.ReadAllBytesAsync(path, ct));
            if (automaticCorrection == "commitment") SaveCommitmentOutcome(variant, result.Success, visits, model.Requests, rows, true, oracle["passed"]!.GetValue<bool>());
            if (Environment.GetEnvironmentVariable("GNOU_GO_V10_EVIDENCE") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllBytesAsync(Path.Combine(evidence, "workbook-" + variant + ".xlsx"), content, ct);
                await File.WriteAllTextAsync(Path.Combine(evidence, "observations-" + variant + ".json"),
                    new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString(), ct);
            }
            output.WriteLine($"V10 ORACLE {variant}: exactCells=true; visits={visits.Count(p => p.StartsWith("/entry/", StringComparison.Ordinal))}; pages={mappings.Sum(m => m.ResolvedInput!["sources"]!["pages"]!.AsArray().Count)}; offered={model.Offered.Count}; " +
                $"decisionBytes={model.DecisionBytes}; inferenceForTypedCopies=0; cleanup=true; committedRecovery=true; oracle={oracle.ToJsonString()}");
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }

    private static string V10Name(int i) => "=Observed lamp " + i % 3;
    private static string V10Description(int i) => "Complete observed block " + i + " — café; all supplied words retained.";
    private static string V10Price(int i) => (10 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",00 €";

    // Measurement-only sink for existing lightweight events. It never retains
    // inputs, results, observations or schema payloads.
    private sealed class CheckMetrics : IWorkflowTelemetry, IWorkflowSpan, IStepSpan
    {
        internal List<string> Events { get; } = [];
        internal long Bytes;
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => this;
        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info) => this;
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public void StepEnd(IStepSpan span, StepResultInfo result) { }
        public void Dispose() { }
        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            if (!name.EndsWith(".check", StringComparison.Ordinal)) return;
            var text = name + " " + string.Join("; ", (attributes ?? []).Select(p => p.Key + "=" + p.Value));
            Events.Add(text); Bytes += Encoding.UTF8.GetByteCount(text);
        }
    }

    private sealed class V10Model(string variant, string origin, bool designer = false, bool approval = false) : ILLMClient, ILLMCapabilityResolver
    {
        internal List<LLMRequest> Requests { get; } = [];
        internal HashSet<string> SampledReferences { get; } = new(StringComparer.Ordinal);
        internal JsonArray? Offered;
        internal int MappingCalls, DecisionBytes;
        private string? _unoffered;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            if (request.StructuredOutputSchema?["properties"]?["script"] is not null)
            {
                MappingCalls++;
                var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
                Assert.Null(data["failure"]);
                foreach (var example in data["examples"]!.AsArray())
                    foreach (var record in example!["item"]!["records"]!.AsArray())
                    { SampledReferences.Add(record!["reference"]!.ToString()); if (record["kind"]!.ToString() != "link") _unoffered ??= record["reference"]!.ToString(); }
                var details = data["item_target"]!["items"]!["properties"]!["tag"] is not null;
                var controlMapping = designer && data["objective"]?.ToString().Contains("search-control", StringComparison.Ordinal) == true;
                // The deterministic fixture recognizes the actual observed input tag;
                // all other mappings keep their existing fixture behavior.
                controlMapping |= designer && data["examples"]!.AsArray().Any(e => e!["item"]!["records"]!.AsArray().Any(r => r!["tag"]?.ToString() == "input"));
                var script = controlMapping ? "item.records.filter(r=>m.test(r.tag,'^input$')).map(r=>({reference:r.reference,observedText:r.text}))" : details ? "item.records.filter(r=>m.test(r.tag,'^(h1|p|price)$')).map(r=>({reference:r.reference,observedText:r.text,tag:r.tag}))" :
                    (designer || approval ? "item.records.filter(r=>m.test(r.tag,'^a$')).map(r=>({reference:r.reference,observedText:r.text}))" : "item.records.filter(r=>m.test(r.kind,'^link$')).map(r=>({reference:r.reference,observedText:r.text}))");
                return Response(new JsonObject { ["script"] = script });
            }
            var input = RetainedModel.BusinessData(request)!;
            Assert.DoesNotContain("observationSnapshot", input.ToJsonString()); Assert.DoesNotContain("noise-", input.ToJsonString());
            JsonObject result;
            if (designer && input["controls"] is JsonArray controls)
                result = new() { ["reference"] = Assert.Single(controls)!["reference"]!.DeepClone() };
            else if (request.StructuredOutputSchema?["properties"]?["url"] is not null)
                result = new() { ["url"] = origin + "/search?q=" + Uri.EscapeDataString(input["query"]!.ToString()) };
            else if (input["candidates"] is JsonArray candidates)
            {
                Offered = candidates.DeepClone().AsArray(); DecisionBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                Assert.All(candidates, c => Assert.Equal(new[] { "reference", "observedText" }, c!.AsObject().Select(p => p.Key)));
                Assert.DoesNotContain("?source=", request.Prompt); Assert.DoesNotContain("selector", input.ToJsonString());
                var chosen = variant == "one" ? candidates.Take(1) : variant == "repeated" ? candidates.Take(9).Append(candidates[0]) : candidates.Take(10);
                result = new() { ["selectedProductReferenceIds"] = variant == "empty_selection" ? new JsonArray() : variant == "unknown" ? new JsonArray("unknown-original") : variant == "unoffered" ? new JsonArray(_unoffered ?? throw new InvalidOperationException("Missing unoffered fixture record.")) :
                    new JsonArray(chosen.Select(c => c!["reference"]!.DeepClone()).ToArray()) };
            }
            else if (input["detailCandidates"] is JsonArray blocks)
            {
                Assert.All(blocks, b => Assert.Equal(new[] { "reference", "observedText", "tag" }, b!.AsObject().Select(p => p.Key)));
                result = new();
                foreach (var (name, tag) in new[] { ("Name", "h1"), ("Description", "p"), ("Price", "price") })
                    result["selected" + name + "ReferenceId"] = blocks.SingleOrDefault(b => b!["tag"]!.ToString() == tag)?["reference"]?.DeepClone();
            }
            else throw new InvalidOperationException("Unexpected inference for typed copying, ID lookup or cell assembly.");
            return Response(result);
        }
        // Deterministic provider usage lets host token ceilings exercise settlement and recovery.
        private static Task<LLMResponse> Response(JsonObject result) => Task.FromResult(new LLMResponse
        {
            Json = result,
            Usage = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 10, ["total_tokens"] = 30 }
        });
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
