using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Planning.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class DiscoveryRestartHostTests
{
    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("unchanged", true)]
    [InlineData("json_order", false)]
    [InlineData("json_order", true)]
    [InlineData("semantic", false)]
    [InlineData("semantic", true)]
    [InlineData("transport", false)]
    [InlineData("transport", true)]
    public async Task ActualHostRestartContinuesPageFiveFromEncryptedSnapshot(string variation, bool diagnostic)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var args = TelemetryTestHostArgs.Create("--TypedWorkflowPlanning:BackgroundProcessingEnabled=false", "--OtlpCollector:Enabled=false",
            "--ApplicationLlmBudget:Mode=limited", "--Logging:LogLevel:Default=Warning");
        var options = DiagnosticPlanningTests.ModelOptions(); var model = new NeverInference();
        void Configure(IServiceCollection services, Source source)
        {
            services.AddSingleton<IKeyVaultRuntimeConfigStore>(new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
            services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
            services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
                sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: source, llmClientOverride: model, defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
        }
        PlanningSession state; CapabilitySnapshotReference reference; string cursor; var first = new Source();
        await using (var app = TelemetryTestHostArgs.Build(args, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false, sp => Configure(sp, first)))
        {
            await app.StartAsync(ct);
            var service = app.Services.GetRequiredService<PlanningSessionService>();
            var store = app.Services.GetRequiredService<IPlanningSessionStore>();
            state = await service.StartAsync("restart-continuity", "Read the exact requested record", false, ct,
                thresholdMode: diagnostic ? "diagnostic" : "strict", diagnosticGrant: diagnostic ? new("controlled-operator") : null);
            DiscoveryRecoveryTests.ConfigureBlocked(state);
            state.ModelCalls = 0; state.Status = PlanningStatus.Generating; state.Diagnostics.Clear(); state.Discovery.Pages.Clear();
            state.Discovery.Sources = [new(first.Id, "Record operations")];
            var step = state.Construction!.Compact!.CurrentStep!; step.DiscoveryProtocol = 2;
            var focus = PlanningDiscoveryFocus.Choices(state)[0];
            var selection = new PlanningDiscoveryRequest(first.Id) { FocusId = focus.Id, Justification = "Read the described record contracts." };
            step.PendingDiscovery = new() { Id = Guid.NewGuid().ToString("N"), Requests = [selection],
                ActionHash = HybridWorkflowPlanner.DiscoveryActionHash(step, [selection]), Pending = [selection with { Query = focus.Query }] };
            state.Discovery.ActiveSourceActions = new() { [PlanningDiscoveryFocus.SourceKey(first.Id, focus.Query, focus.Id)] = step.PendingDiscovery.Id };
            var previous = state.Revision++; Assert.True(await store.TrySaveAsync(state, previous, ct));
            state = await service.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, ct);
            Assert.Equal(4, state.Discovery.Pages.Count); Assert.Equal(1, first.Reads);
            reference = Assert.Single(state.Construction!.Compact!.CurrentStep!.PendingDiscovery!.Snapshots!).Value;
            cursor = state.Construction.Compact.CurrentStep.PendingDiscovery.Pending[0].Cursor!;
            Assert.NotNull(cursor); Assert.Null(state.PendingCall); Assert.Equal(0, model.Calls);
            await app.StopAsync(ct);
        }
        var replacement = new Source { Unavailable = variation == "transport" };
        if (variation == "semantic") replacement.Tools[0].OutputSchema!["type"] = "integer";
        if (variation == "json_order") replacement.Tools[0].InputSchema = JsonNode.Parse("{ \"properties\": {}, \"type\": \"object\" }");
        // A new host, service provider, runtime factory and source instance share only durable storage.
        await using (var app = TelemetryTestHostArgs.Build(args, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false, sp => Configure(sp, replacement)))
        {
            await app.StartAsync(ct);
            var store = app.Services.GetRequiredService<IPlanningSessionStore>();
            var loaded = await store.LoadAsync(state.Request.TenantId, state.Request.SessionId, ct);
            Assert.NotNull(loaded);
            Assert.Equal(reference, Assert.Single(loaded.Construction!.Compact!.CurrentStep!.PendingDiscovery!.Snapshots!).Value);
            Assert.Equal(cursor, loaded.Construction.Compact.CurrentStep.PendingDiscovery.Pending[0].Cursor);
            var service = app.Services.GetRequiredService<PlanningSessionService>();
            var resumed = await service.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = loaded.Revision }, ct);
            Assert.Empty(resumed.Diagnostics); Assert.Equal(6, resumed.Discovery.Pages.Count);
            Assert.Null(resumed.Construction!.Compact!.CurrentStep!.PendingDiscovery);
            Assert.Equal(48, resumed.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Id).Distinct().Count());
            Assert.All(resumed.Discovery.Pages, page => { Assert.Equal(reference.Version, page.SnapshotVersion); Assert.Equal(reference.ActionId, page.ActionId); });
            Assert.Equal(0, replacement.Reads); Assert.Equal(0, model.Calls); Assert.Equal(0, resumed.ModelCalls);
            Assert.Equal(2, resumed.Construction.Checkpoints.Count); Assert.Equal("block", resumed.Construction.Compact.CurrentBlockId);
            var output = Path.Combine(root, "artifacts/discovery-convergence-20261106/host"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, variation + "-" + diagnostic + ".json"), JsonSerializer.Serialize(new
            {
                variation, diagnostic, session = resumed.Request.SessionId, snapshot = reference.Version,
                configuration = reference.ConfigurationFingerprint, action = reference.ActionId,
                continuation = cursor, pages = resumed.Discovery.Pages.Count, distinctCapabilities = 48,
                beforeRestartMetadataReads = first.Reads, afterRestartMetadataReads = replacement.Reads, llmCalls = model.Calls,
                hostServiceProviderRestarted = true, operatingSystemProcessRestarted = false
            }), ct);
            await app.StopAsync(ct);
        }
    }

    private sealed class NeverInference : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Metadata continuation must not infer."); }
    }

    private sealed class Source : IMcpClientFactory
    {
        internal string Id => CapabilityDiscovery.SourceId("record-source");
        internal int Reads;
        internal bool Unavailable;
        internal List<McpToolInfo> Tools = Enumerable.Range(0, 48).Select(i => new McpToolInfo { Name = "record_" + i, Description = "Read an exact record",
            EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, OutputSchema = new JsonObject { ["type"] = "string" } }).ToList();
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "record-source", Description = "Exact record operations" }];
        public Task<IMcpSession> GetClientAsync(string name, CancellationToken ct)
        {
            if (name != "record-source") return Task.FromResult<IMcpSession>(PlanningSessionLifecycleTests.AgentCatalog());
            Reads++; if (Unavailable) throw new IOException("PRIVATE_TRANSPORT_DETAIL");
            var factory = new InMemoryMcpClientFactory(); factory.RegisterServer(name, new() { Tools = Tools }); return factory.GetClientAsync(name, ct);
        }
    }
}
