using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class TraceDebugServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_RemainsAvailable_WhenOpenTelemetryIsDisabled()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = false, ServiceName = "GnOuGo.Agent.Server" };
        var service = CreateService(host.Services, settings);

        var snapshot = await service.GetSnapshotAsync("corr-123", null, CancellationToken.None);

        Assert.True(snapshot.Availability.Enabled);
        Assert.Contains("disabled", snapshot.Availability.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(snapshot.Pending);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsLocalTrace_WhenOpenTelemetryExportIsDisabled()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = false, ServiceName = "GnOuGo.Agent.Server" };
        var localStore = new LocalTraceDebugStore(new StaticOptionsMonitor<OpenTelemetrySettings>(settings));

        using var chat = new Activity("chat.message")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        chat.SetTag(AgentOTelTelemetry.CorrelationIdTagName, "corr-local");
        localStore.Track(chat);

        using var workflow = new Activity("workflow")
            .SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(chat.TraceId, chat.SpanId, ActivityTraceFlags.Recorded)
            .Start();
        workflow.SetTag(AgentOTelTelemetry.CorrelationIdTagName, "corr-local");
        workflow.SetTag("gen_ai.request.model", "gpt-4o-mini");
        workflow.SetTag("gen_ai.usage.input_tokens", 12);
        workflow.SetTag("gen_ai.usage.output_tokens", 34);
        workflow.SetStatus(ActivityStatusCode.Ok);
        localStore.Track(workflow);
        localStore.Complete(workflow);
        localStore.Complete(chat);

        var service = CreateService(host.Services, settings, localStore);

        var snapshot = await service.GetSnapshotAsync("corr-local", null, CancellationToken.None);

        Assert.True(snapshot.Availability.Enabled);
        Assert.False(snapshot.Pending);
        Assert.Equal(chat.TraceId.ToHexString(), snapshot.TraceId);
        Assert.NotNull(snapshot.Trace);
        Assert.Equal(2, snapshot.Trace!.Spans.Count);
    }

    [Fact]
    public async Task GetSnapshotAsync_FallsBackToEmbeddedCollectorStorage_WhenLocalTraceIsMissing()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };
        var traceIdHex = "11111111111111111111111111111111";

        await host.AddSpansAsync(
            CreateSpan(
                traceIdHex,
                "2222222222222222",
                parentSpanIdHex: null,
                name: "chat.message",
                correlationId: "corr-db",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_000_000_000,
                endUnixNs: 1_710_000_000_500_000_000),
            CreateSpan(
                traceIdHex,
                "3333333333333333",
                parentSpanIdHex: "2222222222222222",
                name: "workflow",
                correlationId: "corr-db",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_100_000_000,
                endUnixNs: 1_710_000_000_400_000_000));

        var service = CreateService(host.Services, settings);

        var snapshot = await service.GetSnapshotAsync("corr-db", null, CancellationToken.None);

        Assert.True(snapshot.Availability.Enabled);
        Assert.False(snapshot.Pending);
        Assert.Equal(traceIdHex, snapshot.TraceId);
        Assert.NotNull(snapshot.Trace);
        Assert.Equal(2, snapshot.Trace!.Spans.Count);
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "chat.message");
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "workflow");
    }

    [Fact]
    public async Task GetSnapshotAsync_DeduplicatesSpansBySpanId_WhenDualWritePathProducesDuplicates()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };
        var traceIdHex = "44444444444444444444444444444444";

        // Simulate dual write: same spanId "5555555555555555" stored twice
        await host.AddSpansAsync(
            CreateSpan(
                traceIdHex,
                "5555555555555555",
                parentSpanIdHex: null,
                name: "chat.message",
                correlationId: "corr-dedup",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_000_000_000,
                endUnixNs: 1_710_000_000_500_000_000),
            CreateSpan(
                traceIdHex,
                "5555555555555555",
                parentSpanIdHex: null,
                name: "chat.message",
                correlationId: "corr-dedup",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_000_000_000,
                endUnixNs: 1_710_000_000_500_000_000),
            CreateSpan(
                traceIdHex,
                "6666666666666666",
                parentSpanIdHex: "5555555555555555",
                name: "workflow",
                correlationId: "corr-dedup",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_100_000_000,
                endUnixNs: 1_710_000_000_400_000_000));

        var service = CreateService(host.Services, settings);

        var snapshot = await service.GetSnapshotAsync(null, traceIdHex, CancellationToken.None);

        Assert.NotNull(snapshot.Trace);
        Assert.Equal(2, snapshot.Trace!.Spans.Count);
        Assert.Single(snapshot.Trace.Spans, span => span.SpanId == "5555555555555555");
        Assert.Single(snapshot.Trace.Spans, span => span.SpanId == "6666666666666666");
    }

    private static TraceDebugService CreateService(
        IServiceProvider services,
        OpenTelemetrySettings openTelemetrySettings,
        LocalTraceDebugStore? localStore = null)
    {
        localStore ??= new LocalTraceDebugStore(new StaticOptionsMonitor<OpenTelemetrySettings>(openTelemetrySettings));

        return new TraceDebugService(
            services.GetRequiredService<IServiceScopeFactory>(),
            localStore,
            new StaticOptionsMonitor<OpenTelemetrySettings>(openTelemetrySettings),
            NullLogger<TraceDebugService>.Instance);
    }

    private static SpanRecordEntity CreateSpan(
        string traceIdHex,
        string spanIdHex,
        string? parentSpanIdHex,
        string name,
        string correlationId,
        string serviceName,
        long startUnixNs,
        long endUnixNs)
    {
        var attributes = new Dictionary<string, object?>
        {
            [AgentOTelTelemetry.CorrelationIdTagName] = correlationId,
            ["gen_ai.request.model"] = "gpt-4o-mini",
            ["gen_ai.usage.input_tokens"] = 10,
            ["gen_ai.usage.output_tokens"] = 20
        };

        return new SpanRecordEntity
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            ReceivedUtc = DateTimeOffset.UtcNow,
            TraceId = Convert.FromHexString(traceIdHex),
            SpanId = Convert.FromHexString(spanIdHex),
            ParentSpanId = string.IsNullOrWhiteSpace(parentSpanIdHex) ? null : Convert.FromHexString(parentSpanIdHex),
            Name = name,
            Kind = 1,
            StartUnixNs = startUnixNs,
            EndUnixNs = endUnixNs,
            StatusCode = 1,
            AttributesJson = JsonSerializer.Serialize(attributes),
            EventsJson = "[]",
            ResourceJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["service.name"] = serviceName
            }),
            ScopeJson = "{}",
            ServiceName = serviceName
        };
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CollectorTestHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _dbPath;

        private CollectorTestHost(ServiceProvider provider, string dbPath)
        {
            _provider = provider;
            _dbPath = dbPath;
        }

        public IServiceProvider Services => _provider;

        public static async Task<CollectorTestHost> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-trace-debug-{Guid.NewGuid():N}.db");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<TelemetryDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath};Foreign Keys=False"));
            services.AddScoped<EfTelemetryStore>();

            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            await store.InitializeAsync(devMode: false);

            return new CollectorTestHost(provider, dbPath);
        }

        public async Task AddSpansAsync(params SpanRecordEntity[] spans)
        {
            using var scope = _provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            await store.AddSpansAsync(spans);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            try
            {
                if (File.Exists(_dbPath))
                    File.Delete(_dbPath);
            }
            catch
            {
            }
        }
    }
}
