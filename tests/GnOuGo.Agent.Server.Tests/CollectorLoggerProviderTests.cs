using System.Diagnostics;
using Microsoft.Extensions.Logging;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Telemetry;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CollectorLoggerProviderTests
{
    [Fact]
    public async Task Log_PersistsWorkflowLog_WithCurrentTraceAndSpanIds()
    {
        var queue = new TelemetryIngestQueue(new AppOptions(
            DbPath: "ignored.db",
            BatchSize: 10,
            FlushSeconds: 1,
            ChannelCapacity: 16,
            RetentionSweepSeconds: 60,
            DevModeEnabled: true));

        using var provider = new CollectorLoggerProvider(
            queue,
            new TestOptionsMonitor<OpenTelemetrySettings>(new OpenTelemetrySettings
            {
                ServiceName = "GnOuGo.Agent.Server"
            }));

        var logger = provider.CreateLogger("GnOuGo.Agent.Server.Workflow");

        var traceId = ActivityTraceId.CreateFromString("0123456789abcdef0123456789abcdef".AsSpan());
        using var activity = new Activity("workflow")
            .SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
            .Start();

        logger.LogInformation("Workflow step completed");

        var row = await queue.Channel.Reader.ReadAsync(CancellationToken.None);
        var logRow = Assert.IsType<LogRow>(row);
        Assert.Equal(activity.TraceId.ToHexString(), Convert.ToHexString(logRow.TraceId!).ToLowerInvariant());
        Assert.Equal(activity.SpanId.ToHexString(), Convert.ToHexString(logRow.SpanId!).ToLowerInvariant());
        Assert.Equal("Information", logRow.SeverityText);
        Assert.Contains("Workflow step completed", logRow.Body, StringComparison.Ordinal);
    }
}


