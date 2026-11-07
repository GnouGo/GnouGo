using System.Diagnostics;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class DiscoveryRestartHostTests
{
    [Fact]
    public async Task EncryptedPaginationSurvivesActualProcessExitWithoutRefetch()
    {
        var ct = TestContext.Current.CancellationToken;
        const string phaseVariable = "GNOUGO_TEST_DISCOVERY_RESTART_PHASE";
        const string directoryVariable = "GNOUGO_TEST_DISCOVERY_RESTART_DIRECTORY";
        if (Environment.GetEnvironmentVariable(phaseVariable) is { } phase)
        {
            await RunChildAsync(phase, Environment.GetEnvironmentVariable(directoryVariable)!, ct);
            return;
        }
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var directory = Path.Combine(root, "artifacts/discovery-convergence-20261106/process", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var childPhase in new[] { "capture", "continue" })
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(DiscoveryRestartHostTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(DiscoveryRestartHostTests).FullName + "." + nameof(EncryptedPaginationSurvivesActualProcessExitWithoutRefetch));
            start.Environment[phaseVariable] = childPhase; start.Environment[directoryVariable] = directory;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(90), ct); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            var log = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, childPhase + ".log"), log, ct);
            Assert.True(process.ExitCode == 0, log);
        }
        var captured = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "capture.json"), ct))!;
        var continued = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "continue.json"), ct))!;
        Assert.NotEqual(captured["process"]!.GetValue<int>(), continued["process"]!.GetValue<int>());
        Assert.Equal(captured["snapshot"]!.ToString(), continued["snapshot"]!.ToString());
        Assert.Equal(0, continued["metadataReads"]!.GetValue<int>());
    }

    private static async Task RunChildAsync(string phase, string directory, CancellationToken ct)
    {
        Assert.Contains(phase, new[] { "capture", "continue" });
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:BackgroundProcessingEnabled=false", "--OtlpCollector:Enabled=false",
            "--ApplicationLlmBudget:Mode=limited", "--Logging:LogLevel:Default=Warning",
            "--KeyVault:DatabasePath=" + Path.Combine(directory, "vault.db"),
            "--TypedWorkflowPlanning:DatabasePath=" + Path.Combine(directory, "planning.db"));
        var source = new Source { Unavailable = phase == "continue" }; var model = new NeverInference();
        var options = DiagnosticPlanningTests.ModelOptions();
        await using var app = GnOuGoAgentWebHost.Build(args, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false, services =>
        {
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
            services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
                sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: source, llmClientOverride: model, defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
        });
        await app.StartAsync(ct);
        var service = app.Services.GetRequiredService<PlanningSessionService>(); var store = app.Services.GetRequiredService<IPlanningSessionStore>();
        PlanningSession state;
        if (phase == "capture")
        {
            state = await service.StartAsync("process-continuity", "Read the requested record", false, ct, thresholdMode: "diagnostic", diagnosticGrant: new("controlled-operator"));
            DiscoveryRecoveryTests.ConfigureBlocked(state);
            state.ModelCalls = 0; state.Status = PlanningStatus.Generating; state.Diagnostics.Clear(); state.Discovery.Pages.Clear();
            state.Discovery.Sources = [new(source.Id, "Record operations")];
            var step = state.Construction!.Compact!.CurrentStep!; step.DiscoveryProtocol = 2;
            var focus = PlanningDiscoveryFocus.Choices(state)[0];
            var selection = new PlanningDiscoveryRequest(source.Id) { FocusId = focus.Id, Justification = "Inspect the record contracts." };
            step.PendingDiscovery = new() { Id = Guid.NewGuid().ToString("N"), Requests = [selection],
                ActionHash = HybridWorkflowPlanner.DiscoveryActionHash(step, [selection]), Pending = [selection with { Query = focus.Query }] };
            state.Discovery.ActiveSourceActions = new() { [PlanningDiscoveryFocus.SourceKey(source.Id, focus.Query, focus.Id)] = step.PendingDiscovery.Id };
            var revision = state.Revision++; Assert.True(await store.TrySaveAsync(state, revision, ct));
        }
        else
        {
            var captured = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "capture.json"), ct))!;
            state = (await store.LoadAsync(captured["tenant"]!.ToString(), captured["session"]!.ToString(), ct))!;
            Assert.NotNull(state); Assert.Equal(4, state.Discovery.Pages.Count);
        }
        state = await service.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, ct);
        Assert.Empty(state.Diagnostics); Assert.Equal(phase == "capture" ? 4 : 6, state.Discovery.Pages.Count);
        Assert.Equal(phase == "capture" ? 1 : 0, source.Reads); Assert.Equal(0, model.Calls); Assert.Equal(0, state.ModelCalls);
        Assert.Equal(state.Discovery.Pages.Count * 8, state.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Id).Distinct().Count());
        var snapshot = Assert.Single(state.Discovery.Pages.Select(p => p.SnapshotVersion).Distinct());
        await File.WriteAllTextAsync(Path.Combine(directory, phase + ".json"), new JsonObject
        {
            ["process"] = Environment.ProcessId, ["session"] = state.Request.SessionId, ["tenant"] = state.Request.TenantId,
            ["snapshot"] = snapshot, ["metadataReads"] = source.Reads, ["pages"] = state.Discovery.Pages.Count, ["llmCalls"] = model.Calls
        }.ToJsonString(), ct);
        await app.StopAsync(ct);
    }
}
