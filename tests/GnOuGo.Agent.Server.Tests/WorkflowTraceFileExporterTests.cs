using System.Diagnostics;
using System.Text.Json;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowTraceFileExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesCompleteTraceAsLlmReadableJson()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 20, 14, 15, 16, TimeSpan.Zero));
            using var exporter = CreateExporter(root, enabled: true, clock);
            using var source = new ActivitySource($"GnOuGo.Tests.{Guid.NewGuid():N}");
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            exporter.BeginCapture(traceId);

            using var activity = StartTraceActivity(source, traceId, "workflow.root");
            Assert.NotNull(activity);
            activity.SetTag("gnougo-flow.workflow.name", "diagnostic-workflow");
            activity.AddBaggage("gnougo.agent.chat.correlation_id", "corr-123");

            using (var child = source.StartActivity("llm.call", ActivityKind.Client))
            {
                Assert.NotNull(child);
                child.SetTag("gen_ai.request.model", "test-model");
                child.AddEvent(new ActivityEvent(
                    "gen_ai.content.completion",
                    tags: new ActivityTagsCollection
                    {
                        ["gen_ai.completion"] = "LLM output"
                    }));
                child.SetStatus(ActivityStatusCode.Ok);
            }

            activity.SetStatus(ActivityStatusCode.Ok);
            activity.Dispose();

            await exporter.ExportAsync(traceId, "corr-123", CancellationToken.None);

            var path = Assert.Single(Directory.GetFiles(root, "*.json"));
            Assert.Equal("20-06-26-14-15-16.json", Path.GetFileName(path));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var jsonRoot = document.RootElement;
            Assert.Equal("gnougo.workflow-trace/v1", jsonRoot.GetProperty("schemaVersion").GetString());
            Assert.Equal(traceId, jsonRoot.GetProperty("traceId").GetString());
            Assert.Equal("corr-123", jsonRoot.GetProperty("correlationId").GetString());
            Assert.Equal("GnOuGo.Agent.Server.Tests", jsonRoot.GetProperty("service").GetProperty("name").GetString());
            Assert.Equal(2, jsonRoot.GetProperty("summary").GetProperty("spanCount").GetInt32());

            var spans = jsonRoot.GetProperty("spans").EnumerateArray().ToArray();
            Assert.Contains(spans, span => span.GetProperty("name").GetString() == "workflow.root");
            var llmSpan = Assert.Single(spans, span => span.GetProperty("name").GetString() == "llm.call");
            Assert.Equal(
                "test-model",
                llmSpan.GetProperty("attributes").GetProperty("gen_ai.request.model").GetString());
            Assert.Equal(
                "LLM output",
                llmSpan.GetProperty("events")[0].GetProperty("attributes").GetProperty("gen_ai.completion").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WhenTwoTracesFinishInSameSecond_KeepsSeparateFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 20, 14, 15, 16, TimeSpan.Zero));
            using var exporter = CreateExporter(root, enabled: true, clock);
            using var source = new ActivitySource($"GnOuGo.Tests.{Guid.NewGuid():N}");

            var firstTraceId = CaptureSingleSpanTrace(exporter, source, "first");
            var secondTraceId = CaptureSingleSpanTrace(exporter, source, "second");

            await exporter.ExportAsync(firstTraceId, "corr-first", CancellationToken.None);
            await exporter.ExportAsync(secondTraceId, "corr-second", CancellationToken.None);

            var files = Directory.GetFiles(root, "*.json");
            Assert.Equal(2, files.Length);
            Assert.Contains(files, path => Path.GetFileName(path) == "20-06-26-14-15-16.json");
            Assert.Contains(files, path => Path.GetFileName(path) == $"20-06-26-14-15-16-{secondTraceId[..8]}.json");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_SuccessfulWorkflowSeparatesRecoveredAndExpectedProbeErrors()
    {
        var root = CreateTempDirectory();
        try
        {
            using var exporter = CreateExporter(root, enabled: true, TimeProvider.System);
            using var source = new ActivitySource($"GnOuGo.Tests.{Guid.NewGuid():N}");
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            exporter.BeginCapture(traceId);

            using var workflow = StartTraceActivity(source, traceId, "workflow");
            Assert.NotNull(workflow);

            using (var retry = source.StartActivity("workflow.plan.pipeline.generate_leaf", ActivityKind.Internal))
            {
                Assert.NotNull(retry);
                retry.SetTag("gnougo-flow.plan.attempt", 1);
                retry.SetTag("gnougo-flow.plan.pipeline.leaf_status", "retrying");
                retry.SetStatus(ActivityStatusCode.Error, "invalid generated YAML");
            }

            using (var discovery = source.StartActivity("workflow.plan.mcp_discovery", ActivityKind.Internal))
            {
                Assert.NotNull(discovery);
                using var probe = source.StartActivity("GET", ActivityKind.Client);
                Assert.NotNull(probe);
                probe.SetTag("error.type", "404");
                probe.SetStatus(ActivityStatusCode.Error, "expected capability probe");
                discovery.SetStatus(ActivityStatusCode.Ok);
            }

            workflow.SetStatus(ActivityStatusCode.Ok);
            workflow.Dispose();

            await exporter.ExportAsync(traceId, "corr-recovered", CancellationToken.None);

            var path = Assert.Single(Directory.GetFiles(root, "*.json"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var summary = document.RootElement.GetProperty("summary");
            Assert.Equal("ok_with_recovered_errors", summary.GetProperty("status").GetString());
            Assert.Equal(2, summary.GetProperty("errorSpanCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("recoveredErrorSpanCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("expectedProbeErrorSpanCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("terminalErrorSpanCount").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capture_IgnoresHighVolumeUnrelatedProcessActivities()
    {
        var root = CreateTempDirectory();
        try
        {
            using var exporter = CreateExporter(root, enabled: true, TimeProvider.System);
            using var unrelatedSource = new ActivitySource($"GnOuGo.Unrelated.{Guid.NewGuid():N}");
            using var unrelatedListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == unrelatedSource.Name,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(unrelatedListener);

            for (var index = 0; index < 1_000; index++)
                unrelatedSource.StartActivity($"socket-{index}")?.Dispose();

            using var workflowSource = new ActivitySource($"GnOuGo.Workflow.{Guid.NewGuid():N}");
            var traceId = CaptureSingleSpanTrace(exporter, workflowSource, "workflow");
            await exporter.ExportAsync(traceId, "corr-workflow", CancellationToken.None);

            var path = Assert.Single(Directory.GetFiles(root, "*.json"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("spanCount").GetInt32());
            Assert.Equal("workflow", document.RootElement.GetProperty("spans")[0].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BeginCapture_AfterSmartFlowRootStarts_CapturesRootWhenItStops()
    {
        var root = CreateTempDirectory();
        try
        {
            using var exporter = CreateExporter(root, enabled: true, TimeProvider.System);
            using var source = new ActivitySource(AgentOTelTelemetry.ActivitySourceName);
            using var activity = source.StartActivity("chat.message", ActivityKind.Internal);
            Assert.NotNull(activity);

            var traceId = activity.TraceId.ToHexString();
            exporter.BeginCapture(traceId);
            activity.SetStatus(ActivityStatusCode.Ok);
            activity.Dispose();

            await exporter.ExportAsync(traceId, "corr-root", CancellationToken.None);

            var path = Assert.Single(Directory.GetFiles(root, "*.json"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("chat.message", document.RootElement.GetProperty("spans")[0].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WhenDisabled_DoesNotCreateTraceDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "GnOuGo.Agent.Server.Tests", Guid.NewGuid().ToString("N"));
        using var exporter = CreateExporter(root, enabled: false, TimeProvider.System);

        exporter.BeginCapture(ActivityTraceId.CreateRandom().ToHexString());
        await exporter.ExportAsync(ActivityTraceId.CreateRandom().ToHexString(), "corr-disabled", CancellationToken.None);

        Assert.False(Directory.Exists(root));
    }

    private static WorkflowTraceFileExporter CreateExporter(string traceDirectory, bool enabled, TimeProvider clock)
        => new(
            new TestOptionsMonitor<WorkflowTraceExportSettings>(new WorkflowTraceExportSettings { Enabled = enabled }),
            new TestOptionsMonitor<OpenTelemetrySettings>(new OpenTelemetrySettings
            {
                Enabled = false,
                ServiceName = "GnOuGo.Agent.Server.Tests",
                TenantId = "tenant-test"
            }),
            traceDirectory,
            clock,
            NullLogger<WorkflowTraceFileExporter>.Instance);

    private static string CaptureSingleSpanTrace(
        WorkflowTraceFileExporter exporter,
        ActivitySource source,
        string name)
    {
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        exporter.BeginCapture(traceId);
        using var activity = StartTraceActivity(source, traceId, name);
        Assert.NotNull(activity);
        activity.SetStatus(ActivityStatusCode.Ok);
        return traceId;
    }

    private static Activity? StartTraceActivity(ActivitySource source, string traceId, string name)
    {
        var parentContext = new ActivityContext(
            ActivityTraceId.CreateFromString(traceId.AsSpan()),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);
        return source.StartActivity(name, ActivityKind.Internal, parentContext);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Agent.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
