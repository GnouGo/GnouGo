using System.Diagnostics;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class DiagnosticPlanningBrowserTests
{
    [Fact]
    public async Task ActualHostDefaultsUnlimitedAndOffersVerifiedFreshStartAfterLimitedAdmission()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var output = Path.Combine(root, "artifacts/unlimited-budget/browser"); Directory.CreateDirectory(output);
        var module = PlaywrightModule();
        Assert.True(File.Exists(module));
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = builder.Build(); var physical = 0;
        var literal = new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel();
        provider.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct))!;
            Interlocked.Increment(ref physical);
            Assert.Equal(128000, body["max_completion_tokens"]!.GetValue<int>());
            await Task.Delay(1500, ct);
            var schema = body["response_format"]!["json_schema"]!["schema"]!.AsObject();
            var response = await literal.CallAsync(new() { Model = body["model"]!.ToString(), Prompt = body["messages"]![0]!["content"]!.ToString(), StructuredOutputSchema = schema }, ct);
            return Results.Json(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = response.Json!.ToJsonString() } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 } });
        });
        await provider.StartAsync(ct);
        var options = DiagnosticPlanningTests.ModelOptions(); options.DefaultModel = "private-sol-deployment";
        Assert.True(ModelMetadataCatalog.TryGetBuiltin("openai", "gpt-6.1-sol", out var metadata));
        var deployment = ModelMetadataCatalog.Clone(metadata); deployment.Id = options.DefaultModel;
        deployment.Pricing = new() { Currency = "USD", InputPer1MTokens = 30, OutputPer1MTokens = 180, CachedInputPer1MTokens = 3 };
        options.ModelOverrides["openai/" + options.DefaultModel] = deployment;
        options.Models["openai"].Url = provider.Urls.Single() + "/v1"; options.Models["openai"].ApiKey = "CONTROLLED_BUDGET_SECRET";
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:MaxModelCalls=1", "--TypedWorkflowPlanning:MaxTotalTokens=1",
            "--TypedWorkflowPlanning:MaxActiveMilliseconds=1", "--TypedWorkflowPlanning:DiagnosticModelMaximum=true", "--Logging:LogLevel:Default=Warning")
            .Where(arg => !arg.StartsWith("--TypedWorkflowPlanning:OpenLocalOperatorBrowser=", StringComparison.Ordinal)).ToArray();
        var browser = new RecordingOperatorBrowser();
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<ILocalOperatorBrowserLauncher>(browser);
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
            services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
                sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: new FakeMcpClientFactory(PlanningSessionLifecycleTests.AgentCatalog()), defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
        }
        var url = "http://127.0.0.1:0";
        await using var app = GnOuGoAgentWebHost.Build(args, url, Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        Assert.Equal("unlimited", app.Services.GetRequiredService<ApplicationLlmBudgetPolicy>().Mode);
        Assert.True(app.Services.GetRequiredService<ILLMUsageBudgetScopeFactory>().CreateScope()!.IsTrackingOnly);
        await app.StartAsync(ct); var token = browser.Address!.Fragment[1..];
        url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!.TrimEnd('/');
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/smoke-unlimited-budget.mjs")); start.ArgumentList.Add(url); start.ArgumentList.Add(token); start.ArgumentList.Add(output);
        start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(3), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var logs = ((await stdout) + "\n" + (await stderr)).Replace(token, "[redacted]", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(output, "browser-process.log"), logs, ct); Assert.True(process.ExitCode == 0, logs);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "result.json"), ct))!;
        var service = app.Services.GetRequiredService<PlanningSessionService>();
        var limited = (await service.GetAsync(report["limitedSessionId"]!.ToString(), ct))!;
        Assert.Equal(0, limited.ModelCalls); Assert.Equal("limited", limited.Request.Options["application_llm_budget"]!["mode"]!.ToString());
        var completed = (await service.GetAsync(report["unlimitedSessionId"]!.ToString(), ct))!;
        Assert.Equal(PlanningStatus.FinalReview, completed.Status); Assert.True(completed.ModelCalls > 1); Assert.True(completed.Usage!.CostEstimateAvailable);
        var before = physical; await app.StopAsync(ct);
        // Persisted preference takes precedence even over an explicitly different host default.
        await using var reopened = GnOuGoAgentWebHost.Build([.. args, "--ApplicationLlmBudget:Mode=limited"], "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        await reopened.StartAsync(ct); Assert.Equal("unlimited", reopened.Services.GetRequiredService<ApplicationLlmBudgetPolicy>().Mode);
        var restored = (await reopened.Services.GetRequiredService<PlanningSessionService>().GetAsync(completed.Request.SessionId, ct))!;
        Assert.Equal(completed.Usage, restored.Usage, new UsageComparer()); Assert.Equal(before, physical);
        await reopened.StopAsync(ct); await provider.StopAsync(ct);
    }
}
