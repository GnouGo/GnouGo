using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.AI.Core;
using GnOuGo.Planning.Examples;
using Microsoft.Extensions.Logging;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

// Explicit live harness: actual Agent.Server, actual configured provider, existing campaign ledger.
// No credentials or bootstrap links are exported. The campaign lease is held by the caller.
internal static class ServerLiveDiagnostics
{
    internal static async Task RunAsync(string[] args, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, string workspace)
    {
        var run = SchemaPortabilityCampaign.RequiredOption(args, "--run");
        var planning = args.ElementAtOrDefault(1) == "server-live-plan";
        if (run.Length > 70 || !run.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid fresh run identity.");
        if (await campaign.LoadAsync("planning-evaluation-runs", run) is not null) throw new PlanningConflictException("This live run already exists; start a fresh independent identity.");
        var root = Directory.GetCurrentDirectory();
        var stateRoot = Path.Combine(workspace, ".GnOuGo", "data", "live-server", run); Directory.CreateDirectory(stateRoot);
        var hostArgs = new[]
        {
            "--DevMode:Enabled=true", "--ApplicationLlmBudget:Mode=limited",
            "--TypedWorkflowPlanning:DiagnosticEnabled=true", "--TypedWorkflowPlanning:DefaultThresholdMode=diagnostic",
            "--TypedWorkflowPlanning:DiagnosticModelMaximum=false", "--TypedWorkflowPlanning:OpenLocalOperatorBrowser=false",
            "--TypedWorkflowPlanning:BackgroundProcessingEnabled=" + planning.ToString(), "--OpenTelemetry:Enabled=true", "--OpenTelemetry:TenantId=benchmark",
            "--OtlpCollector:Enabled=false", "--Logging:LogLevel:Default=Warning",
            "--KeyVault:DatabasePath=" + KeyVaultBenchmarkModel.WorkspaceDatabasePath(workspace, null, KeyVaultDatabasePathResolver.DefaultRelativePath),
            "--Agent:DatabasePath=" + KeyVaultBenchmarkModel.WorkspaceDatabasePath(workspace, null, ".GnOuGo/data/gnougo-agent.db"),
            "--Database:Path=" + Path.Combine(stateRoot, "telemetry.db"),
            "--TypedWorkflowPlanning:DatabasePath=" + Path.Combine(stateRoot, "planning.db"),
            "--DocsIngestorMcp:DatabasePath=" + Path.Combine(stateRoot, "docs.db"),
            "--DocsIngestorMcp:VectorDatabasePath=" + Path.Combine(stateRoot, "vectors.sqlite"),
            "--DocsIngestorMcp:OriginalsDirectory=" + Path.Combine(stateRoot, "originals")
        };
        await using var app = GnOuGoAgentWebHost.Build(hostArgs, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false,
            services => {
                if (!planning) return;
                services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(sp.GetRequiredService<LLMRuntimeOptionsStore>(),
                    sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                    llmClientOverride: new CampaignClient(model, sp.GetRequiredService<IPlanningSessionStore>(),
                        sp.GetRequiredService<LlmTraceCapture>(), sp.GetRequiredService<LLMRuntimeOptionsStore>()),
                    defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
            });
        var options = await app.Services.GetRequiredService<LlmDefaultsResolver>().ResolveAsync();
        if (options.DefaultProvider != model.Provider || options.DefaultModel != model.Model ||
            options.ResolveProvider(model.Provider)!.Url != model.CampaignConfiguration()["endpoint"]!.ToString() ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(options.ResolveProvider(model.Provider)!.RequestPolicy), model.CampaignConfiguration()["request_policy"]))
            throw new PlanningConflictException("Server and campaign effective provider/model/endpoint/request settings differ; no inference started.");
        var report = new JsonObject { ["source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"), ["run"] = run,
            ["campaign"] = campaign.Id, ["campaign_configuration"] = campaign.ConfigurationId,
            ["provider"] = options.DefaultProvider, ["model"] = options.DefaultModel, ["host"] = "GnOuGo.Agent.Server",
            ["budget_mode"] = "limited", ["started_utc"] = DateTimeOffset.UtcNow, ["phase"] = planning ? "server_planning" : "provider_probe" };
        await campaign.SaveAsync("planning-evaluation-runs", run, report);
        var invoked = 0;
        app.MapPost("/api/planning/live-provider-probe", async (HttpContext context, LocalOperatorAccess access, IAntiforgery antiforgery, LlmTraceCapture capture) =>
        {
            if (!await access.CanInspectAsync()) return Results.Forbid();
            await antiforgery.ValidateRequestAsync(context);
            if (Interlocked.CompareExchange(ref invoked, 1, 0) != 0) return Results.Conflict("A fresh independent run is required; this probe cannot replay.");
            var nonce = Guid.NewGuid().ToString("N");
            var request = new LLMRequest { Provider = model.Provider, Model = model.Model, Reasoning = "medium", MaxTokens = 1024,
                UseBackgroundMode = false, DisableTransportRetries = true,
                Prompt = "Return the requested JSON object. Set ok to true and nonce to " + nonce + ". No tools or external actions.",
                StructuredOutputSchema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" },
                        ["nonce"] = new JsonObject { ["type"] = "string", ["const"] = nonce } },
                    ["required"] = new JsonArray("ok", "nonce") } };
            request.ClientRequestId = run + ":probe:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            report["request_id"] = request.ClientRequestId; report["requested_output_tokens"] = request.MaxTokens;
            await campaign.SaveAsync("planning-evaluation-runs", run, report);
            using var activity = new Activity("live.provider.structured_output_probe").Start();
            try
            {
                var result = await capture.CallAsync(request, options, (r, ct) => model.CallAsync(r, ct), context.RequestAborted);
                var received = result.Json ?? (result.Text is null ? null : JsonNode.Parse(result.Text));
                report["passed"] = result.CompletionStatus != "output_limit" && received?["ok"]?.GetValue<bool>() == true && received?["nonce"]?.ToString() == nonce;
                report["completion_status"] = result.CompletionStatus; report["usage"] = result.Usage?.DeepClone();
            }
            catch (Exception error)
            {
                report["passed"] = false; report["exception_type"] = error.GetType().Name;
                if (error is LLMClientException failure)
                {
                    report["failure_kind"] = failure.Kind.ToString(); report["http_status"] = failure.StatusCode;
                    report["safe_provider_code"] = failure.SafeProviderCode; report["provider_stage"] = failure.Stage.ToString();
                }
                // Exception messages and provider bodies remain in protected encrypted evidence.
            }
            report["finished_utc"] = DateTimeOffset.UtcNow; report["trace_id"] = activity.TraceId.ToHexString();
            await campaign.SaveAsync("planning-evaluation-runs", run, report, CancellationToken.None);
            return Results.Json(report);
        });
        await app.StartAsync();
        try
        {
            var url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!;
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(12) };
            var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(client.BaseAddress);
            using var auth = await client.PostAsync("/local-operator/session", new StringContent(launch.Fragment[1..])); auth.EnsureSuccessStatusCode();
            var csrf = await client.GetFromJsonAsync<JsonObject>("/api/planning/antiforgery");
            client.DefaultRequestHeaders.Add(csrf!["headerName"]!.ToString(), csrf["requestToken"]!.ToString());
            using var limited = await client.PostAsJsonAsync("/api/application-llm-budget", new JsonObject { ["mode"] = "limited" }); limited.EnsureSuccessStatusCode();
            if (planning)
            {
                var scenario = SchemaPortabilityCampaign.Option(args, "--case") ?? "local";
                var prompt = scenario switch { "local" => PlanningBenchmarkCases.Prompt("local"),
                    "collections" => PlanningBenchmarkCases.Prompt("collections"),
                    "amazon" => LiveWorkflowEvaluation.AmazonEvaluationPrompt("workflows/" + run, 3),
                    "code" => LiveWorkflowEvaluation.CodePrompt, _ => throw new ArgumentException("Unknown live scenario.") };
                report["scenario"] = scenario;
                using var started = await client.PostAsJsonAsync("/api/planning", new JsonObject { ["name"] = run, ["prompt"] = prompt,
                    ["mode"] = "auto", ["thresholdMode"] = "diagnostic" });
                started.EnsureSuccessStatusCode();
                var dto = await started.Content.ReadFromJsonAsync<JsonObject>(); var id = dto!["id"]!.ToString();
                report["session_id"] = id; report["operator_url"] = url.TrimEnd('/') + "/planning/" + id;
                Console.WriteLine(new JsonObject { ["run"] = run, ["session_id"] = id, ["operator_url"] = report["operator_url"]!.DeepClone() }.ToJsonString());
                using var observer = StartObserver(app, root, url, id, run);
                long revision = -1;
                while (true)
                {
                    var state = await app.Services.GetRequiredService<PlanningSessionService>().GetAsync(id, CancellationToken.None)
                        ?? throw new PlanningConflictException("The live Server session disappeared.");
                    if (state.Revision != revision)
                    {
                        revision = state.Revision; report["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
                        report["result"] = new JsonObject { ["status"] = state.Status, ["calls"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts,
                            ["execution_status"] = "not_started", ["execution_correct"] = false };
                        await campaign.SaveAsync("planning-evaluation-runs", run, report);
                        Console.WriteLine(new JsonObject { ["session_id"] = id, ["status"] = state.Status, ["phase"] = state.Phase,
                            ["revision"] = revision, ["calls"] = state.ModelCalls,
                            ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => (JsonNode)JsonValue.Create(d.Code)).ToArray()) }.ToJsonString());
                    }
                    if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status))
                    {
                        report["passed"] = state.Status == PlanningStatus.FinalReview; report["finished_utc"] = DateTimeOffset.UtcNow;
                        await campaign.SaveAsync("planning-evaluation-runs", run, report);
                        if (report["passed"]!.GetValue<bool>() != true) Environment.ExitCode = 1;
                        if (observer is not null)
                        {
                            try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                            finally { if (!observer.HasExited) observer.Kill(entireProcessTree: true); }
                            report["browser_observation_exit_code"] = observer.ExitCode;
                            await campaign.SaveAsync("planning-evaluation-runs", run, report);
                        }
                        break;
                    }
                    await Task.Delay(2000);
                }
            }
            else
            {
                using var response = await client.PostAsync("/api/planning/live-provider-probe", new StringContent("{}")); response.EnsureSuccessStatusCode();
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                if (report["passed"]?.GetValue<bool>() != true) Environment.ExitCode = 1;
            }
        }
        finally { await app.StopAsync(); }
    }
    private static Process? StartObserver(WebApplication app, string root, string url, string id, string run)
    {
        var module = Path.Combine(root, "src/GnOuGo.Browser.Mcp/bin/Release/net10.0/.playwright/package/index.mjs");
        if (!File.Exists(module)) return null;
        var output = Path.Combine(root, "artifacts/live-server-20261106", run); Directory.CreateDirectory(output);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/observe-live-planning.mjs")); start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        var process = Process.Start(start)!;
        // Drain diagnostics into private artifacts; do not print authority-bearing browser input.
        _ = Task.Run(async () => await File.WriteAllTextAsync(Path.Combine(output, "observer.stdout.log"), await process.StandardOutput.ReadToEndAsync()));
        _ = Task.Run(async () => await File.WriteAllTextAsync(Path.Combine(output, "observer.stderr.log"), await process.StandardError.ReadToEndAsync()));
        var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(new Uri(url));
        process.StandardInput.Write(JsonSerializer.Serialize(new { launch = launch.AbsoluteUri, url = url.TrimEnd('/'), session = id, output }));
        process.StandardInput.Close(); return process;
    }
    private sealed class CampaignClient(KeyVaultBenchmarkModel model, IPlanningSessionStore sessions,
        LlmTraceCapture capture, LLMRuntimeOptionsStore options) : ILLMClient
    {
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var id = request.ClientRequestId?.Split(':')[0] ?? throw new PlanningConflictException("A captured Server request identity is required.");
            var state = await sessions.LoadAsync("benchmark", id, ct) ?? throw new PlanningConflictException("No owned live Server session.");
            return await capture.CallAsync(request, options.Current, (r, token) => model.CallServerPlanningAsync(r, state, token), ct);
        }
    }
}
