using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

namespace GnOuGo.Agent.Server.Tests;

public sealed class DiagnosticPlanningBrowserTests
{
    [Fact]
    public async Task ActualServerShowsActivePhasesAttemptsWarningsCancellationAndReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GnOuGo.Agent.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var root = repository.FullName;
        var module = Environment.GetEnvironmentVariable("PLAYWRIGHT_MODULE_PATH") ?? Path.Combine(root, "src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs");
        Assert.True(File.Exists(module), "Build the bundled browser MCP tool to make Playwright available.");
        var output = Path.Combine(root, "artifacts/diagnostic-planning/browser"); Directory.CreateDirectory(output);
        var providerBuilder = WebApplication.CreateBuilder(); providerBuilder.Configuration.Sources.Clear(); providerBuilder.Configuration.AddInMemoryCollection();
        providerBuilder.Logging.ClearProviders(); providerBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = providerBuilder.Build();
        var physicalCalls = 0;
        var invalidSelection = 0;
        var script = new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel();
        provider.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted))!;
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
            return Results.Json(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15,
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 2 },
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 2 } } });
        });
        await provider.StartAsync(ct);
        var options = DiagnosticPlanningTests.ModelOptions();
        options.Models["openai"].Url = provider.Urls.Single() + "/v1";
        options.Models["openai"].ApiKey = "CONTROLLED_PROVIDER_KEY";
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:DiagnosticEnabled=true", "--TypedWorkflowPlanning:MaxModelCalls=1",
            "--TypedWorkflowPlanning:MaxTotalTokens=1", "--TypedWorkflowPlanning:MaxActiveMilliseconds=1", "--Logging:LogLevel:Default=Warning",
            "--OpenTelemetry:Enabled=false", "--TypedWorkflowPlanning:OpenLocalOperatorBrowser=false");
        var url = "http://127.0.0.1:" + Port();
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
                sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: new FakeMcpClientFactory(PlanningSessionLifecycleTests.AgentCatalog()),
                capture: sp.GetRequiredService<GnOuGo.Agent.Server.Telemetry.LlmTraceCapture>()));
        }
        await using var app = GnOuGoAgentWebHost.Build(args, url, Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        await app.StartAsync(ct);
        var token = app.Services.GetRequiredService<LocalOperatorBootstrap>().Issue();
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/smoke-diagnostic-planning.mjs")); start.ArgumentList.Add(url); start.ArgumentList.Add(token); start.ArgumentList.Add(output);
        start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(3), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var result = await stdout; var errors = await stderr;
        await File.WriteAllTextAsync(Path.Combine(output, "browser-process.log"), result + "\n" + errors, ct);
        Assert.True(process.ExitCode == 0, result + "\n" + errors);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "result.json"), ct))!;
        var service = app.Services.GetRequiredService<PlanningSessionService>();
        var complete = (await service.GetAsync(report["sessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.FinalReview, complete.Status); Assert.Null(complete.ApprovedHash);
        Assert.Equal(5, complete.ModelCalls); Assert.Equal(6, complete.Usage!.Calls);
        Assert.Contains(complete.Usage.JournaledAttempts!.Values, attempt => attempt.VerifiedUsage is null);
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
        var reopenedUrl = "http://127.0.0.1:" + Port();
        await using var reopened = GnOuGoAgentWebHost.Build(args, reopenedUrl, Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        await reopened.StartAsync(ct);
        var restored = (await reopened.Services.GetRequiredService<PlanningSessionService>().GetAsync(complete.Request.SessionId, ct))!;
        Assert.Equal(complete.InspectionEvents, restored.InspectionEvents);
        Assert.Equal(complete.Usage, restored.Usage, new UsageComparer());
        using var authenticated = new HttpClient(new HttpClientHandler { CookieContainer = new() });
        var restartToken = reopened.Services.GetRequiredService<LocalOperatorBootstrap>().Issue();
        var signIn = await authenticated.PostAsync(reopenedUrl + "/local-operator/session", new StringContent(restartToken, System.Text.Encoding.UTF8, "text/plain"), ct);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var history = await authenticated.GetStringAsync(reopenedUrl + "/api/planning/" + complete.Request.SessionId + "/inspection", ct);
        Assert.Equal(5, JsonNode.Parse(history)!["calls"]!.AsArray().Count);
        Assert.Equal(before, physicalCalls);
        await reopened.StopAsync(ct);
        await provider.StopAsync(ct);
    }
    private sealed class UsageComparer : IEqualityComparer<LLMUsageBudgetSnapshot>
    {
        public bool Equals(LLMUsageBudgetSnapshot? a, LLMUsageBudgetSnapshot? b) =>
            System.Text.Json.JsonSerializer.Serialize(a, PlanningJsonContext.Default.LLMUsageBudgetSnapshot) == System.Text.Json.JsonSerializer.Serialize(b, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        public int GetHashCode(LLMUsageBudgetSnapshot value) => 0;
    }
    private static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
}
