using System.Diagnostics;
using System.Text.Json;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Planning.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class DiagnosticPlanningBrowserTests
{
    [Fact]
    public async Task ActualHostRecoversDiscoveryWithoutInferenceAndPreservesProtectedInspectionAndFiniteAdmission()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var output = Path.Combine(root, "artifacts/focused-discovery/browser"); Directory.CreateDirectory(output);
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:BackgroundProcessingEnabled=false", "--ApplicationLlmBudget:Mode=limited", "--WorkflowPlanningBudget:Amount=0.000000001",
            "--Logging:LogLevel:Default=Warning")
            .Where(a => !a.StartsWith("--TypedWorkflowPlanning:OpenLocalOperatorBrowser=", StringComparison.Ordinal)).ToArray();
        var browser = new RecordingOperatorBrowser(); var model = new NeverInference(); var options = DiagnosticPlanningTests.ModelOptions();
        var resume = new PausedResume();
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<ILocalOperatorBrowserLauncher>(browser);
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
            services.AddSingleton<IPlanningSessionStore>(sp => new PausedResumeStore(new EfPlanningSessionStore(
                sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PlanningDbContext>>(),
                sp.GetRequiredService<GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore>()), resume));
            services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
                sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: new UnavailableMetadata(), llmClientOverride: model, defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
        }
        await using var app = GnOuGoAgentWebHost.Build(args, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false, Configure);
        var resumeReleasePath = "/testing/release-resume-" + Guid.NewGuid().ToString("N");
        app.MapPost(resumeReleasePath, () =>
        {
            Assert.True(resume.Persisted.Task.IsCompleted);
            resume.Release.TrySetResult();
            return Microsoft.AspNetCore.Http.Results.NoContent();
        });
        await app.StartAsync(ct);
        var url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!.TrimEnd('/');
        var service = app.Services.GetRequiredService<PlanningSessionService>(); var store = app.Services.GetRequiredService<IPlanningSessionStore>();
        async Task<PlanningSession> Seed(string name)
        {
            var state = await service.StartAsync(name, "Return the requested result", false, ct, thresholdMode: "diagnostic", diagnosticGrant: new("controlled-operator"));
            DiscoveryRecoveryTests.ConfigureBlocked(state);
            await DiscoveryRecoveryTests.SeedModelHistoryAsync(app.Services.GetRequiredService<GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore>(),
                app.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PlanningDbContext>>(), state);
            var source = CapabilityDiscovery.SourceId("exploratory");
            state.Discovery.Sources = [new(source, "Unrelated exploratory source")];
            state.Discovery.Pages = [new(source, "old", [], null, "Original exception unavailable", state.Request.Prompt)];
            state.Construction!.Compact!.CurrentStep!.PendingDiscovery = new()
                { Requests = [new(source)], Pending = [new(source, "old", state.Request.Prompt)] };
            if (name == "other-session")
            {
                var step = state.Construction.Compact.CurrentStep; step.DiscoveryProtocol = 2; step.PendingDiscovery = null;
                var focus = PlanningDiscoveryFocus.Choices(state)[0];
                var failure = new CapabilityDiscoveryFailure("required-" + state.Request.SessionId, source, focus.Id, focus.Query, "old",
                    "catalog_changed", "The selected contract changed; refresh or select another verified operation.", null, null, null, DateTimeOffset.UtcNow) { StepId = step.Id };
                state.Discovery.Failures = [failure];
                state.Discovery.Pages[0] = state.Discovery.Pages[0] with { FailureId = failure.Id, FocusId = focus.Id, FailureCategory = failure.Category };
                state.Diagnostics = [new("DISCOVERY_REQUIRED_CONTRACT_CHANGED", "/binding", failure.Message)];
            }
            var previous = state.Revision++; Assert.True(await store.TrySaveAsync(state, previous, ct)); return state;
        }
        var state = await Seed("recovery-browser"); var other = await Seed("other-session");
        resume.SessionId = state.Request.SessionId;
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/smoke-discovery-recovery.mjs")); start.Environment["PLAYWRIGHT_MODULE_PATH"] = PlaywrightModule();
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { url, launch = browser.Address!.ToString(), session = state.Request.SessionId,
            otherSession = other.Request.SessionId, output, resumeReleasePath })); process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct); }
        finally { resume.Release.TrySetResult(); if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var log = await stdout + "\n" + await stderr;
        await File.WriteAllTextAsync(Path.Combine(output, "browser-process.log"), log.Replace(browser.Address.Fragment, "[redacted]", StringComparison.Ordinal), ct);
        Assert.True(process.ExitCode == 0, log); Assert.Equal(0, model.Calls);
        await app.StopAsync(ct);
    }
    private sealed class PausedResume
    {
        internal string? SessionId;
        internal int Paused;
        internal TaskCompletionSource Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class PausedResumeStore(IPlanningSessionStore inner, PausedResume pause) : IPlanningSessionStore
    {
        public Task<PlanningSession?> LoadAsync(string tenantId, string sessionId, CancellationToken ct) => inner.LoadAsync(tenantId, sessionId, ct);
        public Task<IReadOnlyList<PlanningSession>> ListAsync(string tenantId, CancellationToken ct) => inner.ListAsync(tenantId, ct);
        public async Task<bool> TrySaveAsync(PlanningSession session, long? expectedRevision, CancellationToken ct)
        {
            var saved = await inner.TrySaveAsync(session, expectedRevision, ct);
            if (saved && session.Request.SessionId == pause.SessionId && session.Status == PlanningStatus.Generating &&
                Interlocked.Exchange(ref pause.Paused, 1) == 0)
            {
                pause.Persisted.TrySetResult();
                await pause.Release.Task.WaitAsync(ct);
            }
            return saved;
        }
    }
    private sealed class NeverInference : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Recovery must not bypass admission or dispatch a model request."); }
    }
    private sealed class UnavailableMetadata : IMcpClientFactory
    {
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "exploratory", Description = "Exploratory metadata source" }];
        public Task<IMcpSession> GetClientAsync(string name, CancellationToken ct) => name == "exploratory"
            ? throw new IOException("PRIVATE_MCP_CREDENTIAL") : Task.FromResult<IMcpSession>(PlanningSessionLifecycleTests.AgentCatalog());
    }
}
