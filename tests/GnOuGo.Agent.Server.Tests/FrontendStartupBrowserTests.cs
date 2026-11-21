using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerSubmissionIdentifiesTheSavedWorkflowAndItsExecutedInputs(bool promptOnly)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GnOuGo.Agent.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var run = "form-" + Guid.NewGuid().ToString("N");
        var args = TelemetryTestHostArgs.Create("--OtlpCollector:Enabled=false", "--Logging:LogLevel:Default=Warning",
            "--TypedWorkflowPlanning:BackgroundProcessingEnabled=false");
        await using var app = TelemetryTestHostArgs.Build(args, "http://127.0.0.1:0", Path.Combine(root.FullName, "src/GnOuGo.Agent.Server"), false);
        await app.StartAsync(ct);
        var yaml = promptOnly ? SmartFlowServiceTests.RawResultWorkflow : await File.ReadAllTextAsync(Path.Combine(root.FullName,
            "docs/evidence/compact-bindings-v12-2026-10-12/checked-outputs-v12.yaml"), ct);
        var repository = app.Services.GetRequiredService<GnOuGo.Agent.Mcp.Services.IAgentRepository>();
        var saved = await repository.AddAgentAsync(run, yaml, ct: ct);
        var input = promptOnly ? new JsonObject { ["task"] = "Inspect the measured result" } : new JsonObject { ["observationPages"] = """[[{"key":"x","text":"typed form"}]]""", ["selectedKeys"] = """["x"]""" };
        var url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!;
        using var http = new HttpClient();
        var firstPaint = await http.GetStringAsync(url, ct);
        Assert.Matches("""<button\b[^>]*title="Select a GnOuGo agent"[^>]*\bdisabled\b[^>]*>""", firstPaint);
        using var browser = ServerLiveDiagnostics.StartObserver(app, root.FullName, url, null, run, run, input);
        try { await browser.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(100), ct); }
        finally { if (!browser.HasExited) browser.Kill(entireProcessTree: true); }
        Assert.Equal(0, browser.ExitCode);
        var output = Path.Combine(root.FullName, "artifacts/live-server-20261106", run);
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "form-invocation.json"), ct))!;
        Assert.Equal(run, receipt["agent"]!.ToString());
        var journal = await app.Services.GetRequiredService<WorkflowRunService>().ReadAsync(receipt["run"]!.ToString(), ct);
        Assert.NotNull(journal);
        Assert.Equal(saved.Id, Guid.Parse(journal.Limits.AgentId!));
        Assert.Equal(yaml, journal.WorkflowYaml);
        Assert.Equal(WorkflowRunStatus.Completed, journal.Status);
        foreach (var value in input)
            Assert.True(JsonNode.DeepEquals(promptOnly ? value.Value : JsonNode.Parse(value.Value!.ToString()), journal.Inputs![value.Key]));
        var returned = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "form-response.json"), ct))!;
        Assert.Equal(receipt["run"]!.ToString(), returned["run"]!.ToString());
        Assert.Equal(run, returned["agent"]!.ToString());
        if (promptOnly) Assert.Equal(journal.Result!.Outputs!["answer"]!.ToString(), returned["text"]!.ToString());
        else Assert.True(JsonNode.DeepEquals(journal.Result!.Outputs, JsonNode.Parse(returned["text"]!.ToString())));
        await app.StopAsync(ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedObserverRetriesOnlyAStatusTimeoutAndKeepsHeadersPrivate(bool timeout)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GnOuGo.Agent.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var fault = new PlanningStatusFault(timeout);
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:BackgroundProcessingEnabled=false", "--OtlpCollector:Enabled=false");
        await using var app = TelemetryTestHostArgs.Build(args, "http://127.0.0.1:0", Path.Combine(root.FullName, "src/GnOuGo.Agent.Server"), false,
            services => services.AddSingleton<IStartupFilter>(fault));
        await app.StartAsync(ct);
        var state = await app.Services.GetRequiredService<PlanningSessionService>().StartAsync("observer fixture", "Return a string", false, ct);
        var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(new Uri(GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!));
        var output = Path.Combine(root.FullName, "artifacts/server-startup/browser", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, WorkingDirectory = root.FullName };
        start.ArgumentList.Add(Path.Combine(root.FullName, "scripts/observe-live-planning.mjs"));
        start.Environment["PLAYWRIGHT_MODULE_PATH"] = Path.Combine(AppContext.BaseDirectory, "tools/GnOuGo.Browser.Mcp/.playwright/package/index.mjs");
        using var browser = Process.Start(start)!;
        var stderr = browser.StandardError.ReadToEndAsync(ct);
        await browser.StandardInput.WriteAsync(JsonSerializer.Serialize(new { launch = launch.AbsoluteUri, url = launch.GetLeftPart(UriPartial.Authority),
            session = state.Request.SessionId, output, mode = "observe" }));
        browser.StandardInput.Close();
        try { await browser.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(90), ct); }
        finally { if (!browser.HasExited) browser.Kill(entireProcessTree: true); }
        var log = await stderr;
        Assert.True(fault.Authenticated, "The status read must carry the operator session cookie.");
        Assert.False(log.Contains("cookie:", StringComparison.OrdinalIgnoreCase), "The observer emitted authenticated request headers.");
        Assert.False(log.Contains(launch.Fragment[1..], StringComparison.Ordinal), "The observer emitted the bootstrap fragment.");
        Assert.Equal(timeout ? 2 : 1, fault.Reads);
        Assert.Equal(timeout ? 0 : 1, browser.ExitCode);
        Assert.Contains("planning status", log, StringComparison.OrdinalIgnoreCase);
        if (timeout)
            Assert.Equal("stopped", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "live-browser-result.json"), ct))!["status"]!.ToString());
        await app.StopAsync(ct);
    }

    private sealed class PlanningStatusFault(bool timeout) : IStartupFilter
    {
        internal int Reads;
        internal bool Authenticated;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (!context.Request.Path.StartsWithSegments("/api/planning") || !HttpMethods.IsGet(context.Request.Method))
                { await continuation(context); return; }
                Authenticated |= context.Request.Cookies.Count > 0;
                if (Interlocked.Increment(ref Reads) == 1)
                {
                    if (timeout) await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    else context.Abort();
                    return;
                }
                await context.Response.WriteAsJsonAsync(new { status = "stopped" }, context.RequestAborted);
            });
            next(app);
        };
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
