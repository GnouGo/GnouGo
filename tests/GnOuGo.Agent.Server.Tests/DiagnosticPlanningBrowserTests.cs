using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Telemetry;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class DiagnosticPlanningBrowserTests
{
    private static string PlaywrightModule() => Environment.GetEnvironmentVariable("PLAYWRIGHT_MODULE_PATH") ??
        Path.Combine(AppContext.BaseDirectory, "tools", "GnOuGo.Browser.Mcp", ".playwright", "package", "index.mjs");

    [Fact]
    public async Task ActualServerShowsActivePhasesAttemptsWarningsCancellationAndReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GnOuGo.Agent.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var root = repository.FullName;
        var module = PlaywrightModule();
        Assert.True(File.Exists(module), "Build the bundled browser MCP tool to make Playwright available.");
        var output = Path.Combine(root, "artifacts/one-inspector/browser"); Directory.CreateDirectory(output);
        var providerBuilder = WebApplication.CreateBuilder(); providerBuilder.Configuration.Sources.Clear(); providerBuilder.Configuration.AddInMemoryCollection();
        providerBuilder.Logging.ClearProviders(); providerBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = providerBuilder.Build();
        var physicalCalls = 0;
        var requestedModels = new System.Collections.Concurrent.ConcurrentBag<string>();
        var invalidSelection = 0;
        var script = new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel();
        provider.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted))!;
            requestedModels.Add(body["model"]!.ToString());
            var number = Interlocked.Increment(ref physicalCalls);
            var prompt = body["messages"]![0]!["content"]!.ToString();
            if (prompt.Contains("partial fixture", StringComparison.Ordinal))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"choices\":[", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(100, context.RequestAborted); context.Abort(); return Results.Empty;
            }
            await Task.Delay(body["messages"]![0]!["content"]!.ToString().Contains("cancelled test", StringComparison.Ordinal) ? 30000 : number == 1 ? 4000 : 1500, context.RequestAborted);
            if (number == 1) return Results.Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "upstream_error", ["api_key"] = "CONTROLLED_PROVIDER_KEY" } }, statusCode: 500);
            var request = new LLMRequest { Model = body["model"]!.ToString(), Prompt = body["messages"]![0]!["content"]!.ToString(),
                StructuredOutputSchema = body["response_format"]!["json_schema"]!["schema"]!.DeepClone() };
            var response = await script.CallAsync(request, context.RequestAborted);
            var content = prompt.Contains("empty fixture", StringComparison.Ordinal) ? "" :
                prompt.Contains("correction fixture", StringComparison.Ordinal) && request.StructuredOutputSchema["title"]!.ToString() == "construction_select" &&
                Interlocked.Increment(ref invalidSelection) == 1 ? "{}" : response.Json!.ToJsonString();
            return Results.Json(new JsonObject { ["diagnostic_padding"] = number == 2 ? new string('x', 100000) + "😀 Bearer CONTROLLED_PROVIDER_KEY" : "", ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15,
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 2 },
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 2 } } });
        });
        await provider.StartAsync(ct);
        var options = DiagnosticPlanningTests.ModelOptions();
        options.DefaultModel = LlmDefaultsResolverTests.ConfiguredModel;
        Assert.True(ModelMetadataCatalog.TryGetBuiltin("openai", "gpt-6.1-sol", out var metadata));
        var deployment = ModelMetadataCatalog.Clone(metadata); deployment.Id = options.DefaultModel;
        options.ModelOverrides["openai/" + options.DefaultModel] = deployment;
        options.Models["openai"].Url = provider.Urls.Single() + "/v1";
        options.Models["openai"].ApiKey = "CONTROLLED_PROVIDER_KEY";
        var args = TelemetryTestHostArgs.Create("--ApplicationLlmBudget:Mode=limited", "--ApplicationLlmBudget:FinancialForecastMode=experimental_warning", "--TypedWorkflowPlanning:MaxModelCalls=1",
            "--TypedWorkflowPlanning:MaxTotalTokens=1", "--TypedWorkflowPlanning:MaxActiveMilliseconds=1", "--Logging:LogLevel:Default=Warning")
            .Where(arg => !arg.StartsWith("--TypedWorkflowPlanning:OpenLocalOperatorBrowser=", StringComparison.Ordinal)).ToArray();
        var agentDatabase = args.Single(arg => arg.StartsWith("--Agent:DatabasePath=", StringComparison.Ordinal))["--Agent:DatabasePath=".Length..];
        await AgentMcpTestPersistence.SeedUserConfigAsync(agentDatabase, new UserConfigUpdate("openai", LlmDefaultsResolverTests.StaleModel, null), ct);
        var browser = new RecordingOperatorBrowser();
        var url = "http://127.0.0.1:0";
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<ILocalOperatorBrowserLauncher>(browser);
            services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options => options.DetailedErrors = true);
            services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
                sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: new FakeMcpClientFactory(PlanningSessionLifecycleTests.AgentCatalog()),
                capture: sp.GetRequiredService<GnOuGo.Agent.Server.Telemetry.LlmTraceCapture>(), defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
        }
        await using var app = TelemetryTestHostArgs.Build(args, url, Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        Assert.Equal(options.DefaultModel, app.Services.GetRequiredService<LLMRuntimeOptionsStore>().Current.DefaultModel);
        await app.StartAsync(ct);
        url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!.TrimEnd('/');
        Assert.NotNull(browser.Address); Assert.Equal(url, browser.Address.GetLeftPart(UriPartial.Authority));
        var token = browser.Address.Fragment[1..];
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/smoke-diagnostic-planning.mjs")); start.ArgumentList.Add(url); start.ArgumentList.Add(token); start.ArgumentList.Add(output);
        start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(3), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var result = (await stdout).Replace(token, "[redacted]", StringComparison.Ordinal);
        var errors = (await stderr).Replace(token, "[redacted]", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(output, "browser-process.log"), result + "\n" + errors, ct);
        if (process.ExitCode != 0)
        {
            var states = await app.Services.GetRequiredService<PlanningSessionService>().ListAsync(ct);
            result += "\nHost state: " + string.Join("; ", states.Select(s => s.Status + "/" + s.Phase + "/revision=" + s.Revision + "/calls=" + s.ModelCalls));
        }
        Assert.True(process.ExitCode == 0, result + "\n" + errors);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "result.json"), ct))!;
        var service = app.Services.GetRequiredService<PlanningSessionService>();
        var ordinary = (await service.GetAsync(report["ordinarySessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.Stopped, ordinary.Status);
        Assert.Equal("strict", ordinary.Request.Options["agent_host_policy"]!["thresholdMode"]!.ToString());
        Assert.Equal(0, ordinary.ModelCalls);
        var complete = (await service.GetAsync(report["sessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.FinalReview, complete.Status); Assert.Null(complete.ApprovedHash);
        Assert.Equal(5, complete.ModelCalls); Assert.Equal(6, complete.Usage!.Calls);
        Assert.Contains(complete.Usage.JournaledAttempts!.Values, attempt => attempt.VerifiedUsage is null);
        Assert.Equal(options.DefaultModel, complete.Request.Options["generator"]!["model"]!.ToString());
        Assert.All(requestedModels, model => Assert.Equal(options.DefaultModel, model));
        var trace = await app.Services.GetRequiredService<TraceDebugService>().GetPlanningSnapshotAsync(complete.Request.SessionId, false, null, ct);
        Assert.NotNull(trace.Trace);
        Assert.Contains(trace.Trace.Spans, span => span.Name == "gen_ai.generate" && span.Attributes.ContainsKey("gnougo.llm.content_ref"));
        Assert.All(trace.Trace.Spans.Where(span => span.Name == "gen_ai.generate"), span =>
        {
            var attributes = System.Text.Json.JsonSerializer.Serialize(span.Attributes);
            Assert.DoesNotContain("CONTROLLED_PROVIDER_KEY", attributes);
            Assert.DoesNotContain("Return the required public numeric result", attributes);
        });
        var cancelled = (await service.GetAsync(report["cancelledSessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.Cancelled, cancelled.Status);
        Assert.Contains(cancelled.Usage!.JournaledAttempts!.Values, attempt => attempt.VerifiedUsage is null);
        var corrected = (await service.GetAsync(report["correctedSessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.FinalReview, corrected.Status); Assert.True(corrected.ReplanAttempts > 0);
        Assert.Contains(corrected.InspectionEvents!, e => e.State == "correction"); Assert.Null(corrected.ApprovedHash);
        var partial = (await service.GetAsync(report["partialSessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.Stopped, partial.Status); Assert.NotNull(partial.PendingCall);
        Assert.Contains(partial.Usage!.JournaledAttempts!.Values, attempt => attempt.VerifiedUsage is null);
        Assert.Equal(PlanningStatus.Stopped, (await service.GetAsync(report["emptySessionId"]!.ToString(), ct))!.Status);
        Assert.Equal(PlanningStatus.Stopped, (await service.GetAsync(report["strictSessionId"]!.ToString(), ct))!.Status);
        var before = physicalCalls;
        await app.StopAsync(ct);
        var reopenedUrl = "http://127.0.0.1:0";
        // Keep the original journals/accounting, but deliberately omit telemetry to exercise truthful fallback.
        await using var reopened = TelemetryTestHostArgs.Build([.. args, "--Database:Path=" + Path.Combine(output, "empty-restart-telemetry-" + Guid.NewGuid().ToString("N") + ".db")], reopenedUrl, Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        await reopened.StartAsync(ct);
        reopenedUrl = GnOuGoAgentWebHost.ResolvePublishedEndpoints(reopened).AppBaseAddress!.TrimEnd('/');
        Assert.Equal(options.DefaultModel, reopened.Services.GetRequiredService<LLMRuntimeOptionsStore>().Current.DefaultModel);
        var restored = (await reopened.Services.GetRequiredService<PlanningSessionService>().GetAsync(complete.Request.SessionId, ct))!;
        Assert.Equal(complete.InspectionEvents, restored.InspectionEvents);
        Assert.Equal(complete.Usage, restored.Usage, new UsageComparer());
        using var authenticated = new HttpClient(new HttpClientHandler { CookieContainer = new() });
        var restartToken = reopened.Services.GetRequiredService<LocalOperatorBootstrap>().Issue();
        var signIn = await authenticated.PostAsync(reopenedUrl + "/local-operator/session", new StringContent(restartToken, System.Text.Encoding.UTF8, "text/plain"), ct);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var history = await authenticated.GetStringAsync(reopenedUrl + "/api/planning/" + complete.Request.SessionId + "/inspection", ct);
        Assert.Equal(5, JsonNode.Parse(history)!["calls"]!.AsArray().Count);
        var restartStart = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        var browserToken = reopened.Services.GetRequiredService<LocalOperatorBootstrap>().Issue();
        foreach (var arg in new[] { Path.Combine(root, "scripts/smoke-inspector-restart.mjs"), reopenedUrl, browserToken, complete.Request.SessionId, output }) restartStart.ArgumentList.Add(arg);
        restartStart.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        using var restartProcess = Process.Start(restartStart)!;
        var restartOutput = restartProcess.StandardOutput.ReadToEndAsync(ct); var restartErrors = restartProcess.StandardError.ReadToEndAsync(ct);
        try { await restartProcess.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(1), ct); }
        finally { if (!restartProcess.HasExited) restartProcess.Kill(entireProcessTree: true); }
        var restartLog = ((await restartOutput) + (await restartErrors)).Replace(browserToken, "[redacted]", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(output, "restart-browser-process.log"), restartLog, ct);
        Assert.True(restartProcess.ExitCode == 0, restartLog);
        Assert.Equal(before, physicalCalls);
        await reopened.StopAsync(ct);
        await provider.StopAsync(ct);
    }
    private sealed class RecordingOperatorBrowser : ILocalOperatorBrowserLauncher
    {
        public Uri? Address { get; private set; }
        public void Open(Uri address) => Address = address;
    }
    private sealed class UsageComparer : IEqualityComparer<LLMUsageBudgetSnapshot>
    {
        public bool Equals(LLMUsageBudgetSnapshot? a, LLMUsageBudgetSnapshot? b) =>
            System.Text.Json.JsonSerializer.Serialize(a, PlanningJsonContext.Default.LLMUsageBudgetSnapshot) == System.Text.Json.JsonSerializer.Serialize(b, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        public int GetHashCode(LLMUsageBudgetSnapshot value) => 0;
    }
}
