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
    public async Task GetSnapshotAsync_IncludesLogsForResolvedTrace()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };
        var traceIdHex = "77777777777777777777777777777777";
        var spanIdHex = "8888888888888888";

        await host.AddSpansAsync(CreateSpan(
            traceIdHex,
            spanIdHex,
            parentSpanIdHex: null,
            name: "chat.message",
            correlationId: "corr-logs",
            serviceName: settings.ServiceName,
            startUnixNs: 1_710_000_000_000_000_000,
            endUnixNs: 1_710_000_000_500_000_000));

        await host.AddLogsAsync(
            CreateLog(traceIdHex, spanIdHex, 9, "Information", "first correlated log", settings.ServiceName, 1),
            CreateLog(traceIdHex, spanIdHex, 17, "Error", "second correlated log", settings.ServiceName, 2));

        var service = CreateService(host.Services, settings);

        var snapshot = await service.GetSnapshotAsync("corr-logs", null, CancellationToken.None);

        Assert.NotNull(snapshot.Trace);
        Assert.Equal(traceIdHex, snapshot.TraceId);
        Assert.Equal(2, snapshot.Logs.Count);
        Assert.Equal("first correlated log", snapshot.Logs[0].Body);
        Assert.Equal("second correlated log", snapshot.Logs[1].Body);
        Assert.Equal(traceIdHex, snapshot.Logs[0].TraceId);
        Assert.Equal(spanIdHex, snapshot.Logs[0].SpanId);
        Assert.Equal("Information", snapshot.Logs[0].SeverityText);
        Assert.NotNull(snapshot.Logs[0].Id);
        Assert.NotNull(snapshot.Logs[1].Id);
        Assert.NotEqual(snapshot.Logs[0].Id, snapshot.Logs[1].Id);
        Assert.Equal(1L, Convert.ToInt64(snapshot.Logs[0].Attributes["sequence"]));
        Assert.Equal(settings.ServiceName, snapshot.Logs[0].Resource["service.name"]);
    }

    [Fact]
    public async Task GetSnapshotAsync_PreservesStoredSpanPayloads_ForDebugSidebar()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };
        var traceIdHex = "12121212121212121212121212121212";

        await host.AddSpansAsync(CreateSpan(
            traceIdHex,
            "3434343434343434",
            parentSpanIdHex: null,
            name: "chat.completions gpt-4o-mini",
            correlationId: "corr-payloads",
            serviceName: settings.ServiceName,
            startUnixNs: 1_710_000_000_000_000_000,
            endUnixNs: 1_710_000_000_500_000_000,
            additionalAttributes: new Dictionary<string, object?>
            {
                ["nested"] = new Dictionary<string, object?> { ["ok"] = true },
                ["temperature"] = 0.2d
            },
            eventsJson: """
                [{"name":"tokens.counted","timeUtc":"2024-03-09T16:00:00+00:00","attributes":{"tokens":42,"labels":["prompt","completion"]}}]
                """,
            resource: new Dictionary<string, object?>
            {
                ["service.name"] = settings.ServiceName,
                ["deployment.environment"] = "test"
            },
            scope: new Dictionary<string, object?>
            {
                ["name"] = "GnOuGo.Agent.Server.Tests",
                ["version"] = "1.0.0"
            }));

        var service = CreateService(host.Services, settings);

        var snapshot = await service.GetSnapshotAsync(null, traceIdHex, CancellationToken.None);

        Assert.NotNull(snapshot.Trace);
        Assert.Single(snapshot.Trace!.Spans);
        var span = snapshot.Trace.Spans[0];
        Assert.Equal("chat.completions gpt-4o-mini", span.Name);
        Assert.Equal("gpt-4o-mini", span.Attributes["gen_ai.request.model"]);
        Assert.Equal(0.2d, Convert.ToDouble(span.Attributes["temperature"]));
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(span.Attributes["nested"]);
        Assert.Equal(true, nested["ok"]);
        Assert.Equal(settings.ServiceName, span.Resource["service.name"]);
        Assert.Equal("test", span.Resource["deployment.environment"]);
        Assert.Equal("GnOuGo.Agent.Server.Tests", span.Scope["name"]);

        Assert.Single(span.Events);
        var traceEvent = span.Events[0];
        Assert.Equal("tokens.counted", traceEvent.Name);
        Assert.Equal(DateTimeOffset.Parse("2024-03-09T16:00:00+00:00"), traceEvent.TimeUtc);
        Assert.Equal(42L, Convert.ToInt64(traceEvent.Attributes["tokens"]));
        var labels = Assert.IsAssignableFrom<IReadOnlyList<object?>>(traceEvent.Attributes["labels"]);
        Assert.Equal("prompt", labels[0]);
        Assert.Equal("completion", labels[1]);
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

    [Fact]
    public async Task GetSnapshotAsync_MergesAllRelatedTraces_WhenCorrelationIdMatchesMultipleStoredTraces()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };

        await host.AddSpansAsync(
            CreateSpan(
                traceIdHex: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                spanIdHex: "1111111111111111",
                parentSpanIdHex: null,
                name: "chat.message",
                correlationId: "corr-merged",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_000_000_000,
                endUnixNs: 1_710_000_000_100_000_000),
            CreateSpan(
                traceIdHex: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                spanIdHex: "2222222222222222",
                parentSpanIdHex: null,
                name: "workflow",
                correlationId: "corr-merged",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_110_000_000,
                endUnixNs: 1_710_000_000_400_000_000),
            CreateSpan(
                traceIdHex: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                spanIdHex: "3333333333333333",
                parentSpanIdHex: "2222222222222222",
                name: "chat.completions gpt-4o-mini",
                correlationId: "corr-merged",
                serviceName: settings.ServiceName,
                startUnixNs: 1_710_000_000_200_000_000,
                endUnixNs: 1_710_000_000_350_000_000));

        var service = CreateService(host.Services, settings);

        var snapshot = await service.GetSnapshotAsync("corr-merged", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CancellationToken.None);

        Assert.NotNull(snapshot.Trace);
        Assert.False(snapshot.Pending);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", snapshot.TraceId);
        Assert.Equal(3, snapshot.Trace!.Spans.Count);
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "chat.message");
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "workflow");
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "chat.completions gpt-4o-mini");
        Assert.Contains("2 related traces", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_MergesLocalTracesForSameCorrelation_WhenRootTraceIdIsAlreadyKnown()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };
        var localStore = new LocalTraceDebugStore(new StaticOptionsMonitor<OpenTelemetrySettings>(settings));

        using var root = new Activity("chat.message")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        root.SetTag(AgentOTelTelemetry.CorrelationIdTagName, "corr-local-merged");
        localStore.Track(root);

        using var secondary = new Activity("workflow")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        secondary.SetTag(AgentOTelTelemetry.CorrelationIdTagName, "corr-local-merged");
        secondary.SetTag("gen_ai.request.model", "gpt-4o-mini");
        localStore.Track(secondary);
        localStore.Complete(secondary);
        localStore.Complete(root);

        var service = CreateService(host.Services, settings, localStore);

        var snapshot = await service.GetSnapshotAsync("corr-local-merged", root.TraceId.ToHexString(), CancellationToken.None);

        Assert.NotNull(snapshot.Trace);
        Assert.Equal(root.TraceId.ToHexString(), snapshot.TraceId);
        Assert.Equal(2, snapshot.Trace!.Spans.Count);
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "chat.message");
        Assert.Contains(snapshot.Trace.Spans, span => span.Name == "workflow");
    }

    [Fact]
    public async Task StreamSnapshotsAsync_EmitsInitialAndCollectorFlushSnapshots()
    {
        await using var host = await CollectorTestHost.CreateAsync();
        var settings = new OpenTelemetrySettings { Enabled = true, ServiceName = "GnOuGo.Agent.Server" };
        var service = CreateService(
            host.Services,
            settings,
            streamContent: "event: init\ndata: []\n\nevent: update\ndata: []\n\n");
        var traceIdHex = "99999999999999999999999999999999";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var snapshots = service.StreamSnapshotsAsync("corr-stream", null, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Assert.True(await snapshots.MoveNextAsync());
        Assert.True(snapshots.Current.Pending);

        await host.AddSpansAsync(CreateSpan(
            traceIdHex,
            "aaaaaaaaaaaaaaaa",
            parentSpanIdHex: null,
            name: "chat.message",
            correlationId: "corr-stream",
            serviceName: settings.ServiceName,
            startUnixNs: 1_710_000_000_000_000_000,
            endUnixNs: 1_710_000_000_500_000_000));

        Assert.True(await snapshots.MoveNextAsync());
        Assert.NotNull(snapshots.Current.Trace);
        Assert.Equal(traceIdHex, snapshots.Current.TraceId);
    }

    private static TraceDebugService CreateService(
        IServiceProvider services,
        OpenTelemetrySettings openTelemetrySettings,
        LocalTraceDebugStore? localStore = null,
        string streamContent = "event: init\ndata: []\n\n")
    {
        localStore ??= new LocalTraceDebugStore(new StaticOptionsMonitor<OpenTelemetrySettings>(openTelemetrySettings));

        return new TraceDebugService(
            new StaticHttpClientFactory(streamContent),
            services.GetRequiredService<IServiceScopeFactory>(),
            localStore,
            new StaticOptionsMonitor<TraceDebugSettings>(new TraceDebugSettings
            {
                BaseUrl = "http://localhost:4318",
                ServiceName = openTelemetrySettings.ServiceName
            }),
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
        long endUnixNs,
        Dictionary<string, object?>? additionalAttributes = null,
        string? eventsJson = null,
        Dictionary<string, object?>? resource = null,
        Dictionary<string, object?>? scope = null)
    {
        var attributes = new Dictionary<string, object?>
        {
            [AgentOTelTelemetry.CorrelationIdTagName] = correlationId,
            ["gen_ai.request.model"] = "gpt-4o-mini",
            ["gen_ai.usage.input_tokens"] = 10,
            ["gen_ai.usage.output_tokens"] = 20
        };

        if (additionalAttributes is not null)
        {
            foreach (var attribute in additionalAttributes)
                attributes[attribute.Key] = attribute.Value;
        }

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
            EventsJson = eventsJson ?? "[]",
            ResourceJson = JsonSerializer.Serialize(resource ?? new Dictionary<string, object?>
            {
                ["service.name"] = serviceName
            }),
            ScopeJson = JsonSerializer.Serialize(scope ?? new Dictionary<string, object?>()),
            ServiceName = serviceName
        };
    }

    private static LogRecordEntity CreateLog(
        string traceIdHex,
        string spanIdHex,
        int severityNumber,
        string severityText,
        string body,
        string serviceName,
        int sequence)
    {
        return new LogRecordEntity
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            ReceivedUtc = DateTimeOffset.Parse($"2024-03-09T16:00:0{sequence}+00:00"),
            TraceId = Convert.FromHexString(traceIdHex),
            SpanId = Convert.FromHexString(spanIdHex),
            SeverityNumber = severityNumber,
            SeverityText = severityText,
            Body = body,
            AttributesJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sequence"] = sequence
            }),
            ResourceJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["service.name"] = serviceName
            }),
            ScopeJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = "GnOuGo.Agent.Server.Tests"
            }),
            ServiceName = serviceName
        };
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StaticHttpClientFactory(string streamContent) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticSseHandler(streamContent))
            {
                BaseAddress = new Uri("http://localhost:4318"),
                Timeout = Timeout.InfiniteTimeSpan
            };
    }

    private sealed class StaticSseHandler(string streamContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(streamContent)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
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
            services.AddSingleton(new AppOptions(
                DbPath: dbPath,
                BatchSize: 100,
                FlushSeconds: 1,
                ChannelCapacity: 100,
                RetentionSweepSeconds: 60,
                DevModeEnabled: false));
            services.AddDbContext<TelemetryDbContext>(options =>
            {
                var dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                options.UseSqlite($"Data Source={dbPath}");
            });
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

        public async Task AddLogsAsync(params LogRecordEntity[] logs)
        {
            using var scope = _provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            await store.AddLogsAsync(logs);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
