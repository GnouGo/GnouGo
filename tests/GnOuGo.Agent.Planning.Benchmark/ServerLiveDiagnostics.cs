using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Hosting;
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
        if (run.Length > 70 || !run.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid fresh run identity.");
        if (await campaign.LoadAsync("planning-evaluation-runs", run) is not null) throw new PlanningConflictException("This live run already exists; start a fresh independent identity.");
        var root = Directory.GetCurrentDirectory();
        var stateRoot = Path.Combine(workspace, ".GnOuGo", "data", "live-server", run); Directory.CreateDirectory(stateRoot);
        var hostArgs = new[]
        {
            "--DevMode:Enabled=true", "--ApplicationLlmBudget:Mode=limited",
            "--TypedWorkflowPlanning:DiagnosticEnabled=true", "--TypedWorkflowPlanning:DefaultThresholdMode=diagnostic",
            "--TypedWorkflowPlanning:DiagnosticModelMaximum=false", "--TypedWorkflowPlanning:OpenLocalOperatorBrowser=false",
            "--TypedWorkflowPlanning:BackgroundProcessingEnabled=false", "--OpenTelemetry:Enabled=true", "--OpenTelemetry:TenantId=benchmark",
            "--OtlpCollector:Enabled=false", "--Logging:LogLevel:Default=Warning",
            "--KeyVault:DatabasePath=" + KeyVaultBenchmarkModel.WorkspaceDatabasePath(workspace, null, KeyVaultDatabasePathResolver.DefaultRelativePath),
            "--Agent:DatabasePath=" + KeyVaultBenchmarkModel.WorkspaceDatabasePath(workspace, null, ".GnOuGo/data/gnougo-agent.db"),
            "--Database:Path=" + Path.Combine(stateRoot, "telemetry.db"),
            "--TypedWorkflowPlanning:DatabasePath=" + Path.Combine(stateRoot, "planning.db"),
            "--DocsIngestorMcp:DatabasePath=" + Path.Combine(stateRoot, "docs.db"),
            "--DocsIngestorMcp:VectorDatabasePath=" + Path.Combine(stateRoot, "vectors.sqlite"),
            "--DocsIngestorMcp:OriginalsDirectory=" + Path.Combine(stateRoot, "originals")
        };
        await using var app = GnOuGoAgentWebHost.Build(hostArgs, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false);
        var options = await app.Services.GetRequiredService<LlmDefaultsResolver>().ResolveAsync();
        if (options.DefaultProvider != model.Provider || options.DefaultModel != model.Model ||
            options.ResolveProvider(model.Provider)!.Url != model.CampaignConfiguration()["endpoint"]!.ToString() ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(options.ResolveProvider(model.Provider)!.RequestPolicy), model.CampaignConfiguration()["request_policy"]))
            throw new PlanningConflictException("Server and campaign effective provider/model/endpoint/request settings differ; no inference started.");
        var report = new JsonObject { ["source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"), ["run"] = run,
            ["campaign"] = campaign.Id, ["campaign_configuration"] = campaign.ConfigurationId,
            ["provider"] = options.DefaultProvider, ["model"] = options.DefaultModel, ["host"] = "GnOuGo.Agent.Server",
            ["budget_mode"] = "limited", ["started_utc"] = DateTimeOffset.UtcNow, ["phase"] = "provider_probe" };
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
            using var response = await client.PostAsync("/api/planning/live-provider-probe", new StringContent("{}")); response.EnsureSuccessStatusCode();
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            if (report["passed"]?.GetValue<bool>() != true) Environment.ExitCode = 1;
        }
        finally { await app.StopAsync(); }
    }
}
