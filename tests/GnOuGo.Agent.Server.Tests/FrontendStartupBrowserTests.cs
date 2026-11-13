using System.Diagnostics;
using System.Text.Json;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class FrontendStartupBrowserTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("delayed")]
    [InlineData("missing_script")]
    [InlineData("missing_css")]
    public async Task ActualHostLoadsHelpersBeforeInteropAndShowsAssetFailures(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GnOuGo.Agent.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var module = Path.Combine(AppContext.BaseDirectory, "tools/GnOuGo.Browser.Mcp/.playwright/package/index.mjs");
        Assert.True(File.Exists(module), "Build the staged browser tool before the startup regression.");
        var options = DiagnosticPlanningTests.ModelOptions();
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:BackgroundProcessingEnabled=false", "--OtlpCollector:Enabled=false",
            "--ApplicationLlmBudget:Mode=limited", "--Logging:LogLevel:Default=Warning");
        await using var app = TelemetryTestHostArgs.Build(args, "http://127.0.0.1:0", Path.Combine(root.FullName, "src/GnOuGo.Agent.Server"), false, services =>
        {
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
            services.AddSingleton<IStartupFilter>(new AssetFault(scenario));
        });
        await app.StartAsync(ct);
        var contexts = app.Services.GetRequiredService<IDbContextFactory<PlanningDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var initialCount = await db.Sessions.CountAsync(ct);
        var url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!.TrimEnd('/');
        if (scenario.StartsWith("missing_", StringComparison.Ordinal))
            await Assert.ThrowsAsync<PlanningConflictException>(() => ServerLiveDiagnostics.VerifyFrontendAsync(app, root.FullName, url, "startup-" + scenario, ct));
        else await ServerLiveDiagnostics.VerifyFrontendAsync(app, root.FullName, url, "startup-" + scenario, ct);
        Assert.Equal(initialCount, await db.Sessions.CountAsync(ct));
        var service = app.Services.GetRequiredService<PlanningSessionService>();
        var state = await service.StartAsync("startup fixture", "Return the requested result", false, ct, thresholdMode: "strict");
        var count = await db.Sessions.CountAsync(ct);
        var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(new Uri(url));
        var output = Path.Combine(root.FullName, "artifacts/server-startup/browser", scenario);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root.FullName };
        start.ArgumentList.Add(Path.Combine(root.FullName, "scripts/smoke-client-startup.mjs"));
        start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { launch = launch.AbsoluteUri, url, session = state.Request.SessionId, scenario, output }));
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var log = (await stdout + await stderr).Replace(launch.AbsoluteUri, "[protected bootstrap]", StringComparison.Ordinal)
            .Replace(launch.Fragment[1..], "[redacted]", StringComparison.Ordinal);
        Assert.True(process.ExitCode == 0, log);
        Assert.Equal(count, await db.Sessions.CountAsync(ct));
        var retained = (await service.GetAsync(state.Request.SessionId, ct))!;
        Assert.Equal(0, retained.ModelCalls); Assert.Null(retained.PendingCall);
        await app.StopAsync(ct);
    }

    private sealed class AssetFault(string scenario) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var script = context.Request.Path == "/ui/app.js";
                if (scenario == "missing_script" && script || scenario == "missing_css" && context.Request.Path == "/ui/app.css")
                { context.Response.StatusCode = 404; return; }
                if (scenario == "delayed" && script) await Task.Delay(2000, context.RequestAborted);
                await continuation(context);
            });
            next(app);
        };
    }
}
