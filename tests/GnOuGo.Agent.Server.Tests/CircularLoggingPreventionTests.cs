using System.Diagnostics;
using Microsoft.Extensions.Logging;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Telemetry;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

/// <summary>
/// Validates that the <see cref="CollectorLoggerProvider"/> does not re-ingest
/// logs produced by the embedded OTLP collector pipeline, which would otherwise
/// create an infinite INSERT → log → INSERT feedback loop.
/// </summary>
public sealed class CircularLoggingPreventionTests
{
    [Theory]
    [InlineData("OtlpTenantCollector.Services.TelemetryBatchWriter")]
    [InlineData("OtlpTenantCollector.Services.EfTelemetryStore")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Grpc.AspNetCore.Server.ServerCallHandler")]
    [InlineData("System.Net.Http.HttpClient.OtlpTraceExporter")]
    [InlineData("GnOuGo.Agent.Server.Telemetry.CollectorLoggerProvider")]
    public async Task CollectorLoggerProvider_DoesNotEnqueue_SuppressedCategoryLogs(string categoryName)
    {
        var queue = CreateQueue();
        using var provider = CreateProvider(queue);
        var logger = provider.CreateLogger(categoryName);

        using var activity = new Activity("test")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        logger.LogInformation("This should never be enqueued");

        // Give a brief moment then verify nothing was enqueued
        await Task.Delay(50);
        Assert.False(queue.Channel.Reader.TryRead(out _),
            $"Category '{categoryName}' should be suppressed but a row was enqueued.");
    }

    [Fact]
    public async Task CollectorLoggerProvider_DoesEnqueue_ApplicationCategoryLogs()
    {
        var queue = CreateQueue();
        using var provider = CreateProvider(queue);
        var logger = provider.CreateLogger("GnOuGo.Agent.Server.SmartFlow.SmartFlowService");

        using var activity = new Activity("test")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        logger.LogInformation("Application log that should be captured");

        var row = await queue.Channel.Reader.ReadAsync(CancellationToken.None);
        var logRow = Assert.IsType<LogRow>(row);
        Assert.Contains("Application log that should be captured", logRow.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectorLoggerProvider_DoesNotEnqueue_WhenNoActivity()
    {
        var queue = CreateQueue();
        using var provider = CreateProvider(queue);
        var logger = provider.CreateLogger("GnOuGo.Agent.Server.SmartFlow.SmartFlowService");

        // Ensure no Activity.Current
        Activity.Current = null;

        logger.LogInformation("No activity context — should not be enqueued");

        await Task.Delay(50);
        Assert.False(queue.Channel.Reader.TryRead(out _),
            "Logs without an active Activity should not be enqueued.");
    }

    private static TelemetryIngestQueue CreateQueue() => new(new AppOptions(
        DbPath: "ignored.db",
        BatchSize: 10,
        FlushSeconds: 1,
        ChannelCapacity: 64,
        RetentionSweepSeconds: 60,
        DevModeEnabled: true));

    private static CollectorLoggerProvider CreateProvider(TelemetryIngestQueue queue) => new(
        queue,
        new TestOptionsMonitor<OpenTelemetrySettings>(new OpenTelemetrySettings
        {
            ServiceName = "GnOuGo.Agent.Server"
        }));
}

