using System.Diagnostics;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task Log_PersistsApplicationLog_WithoutActivity_WhenOpenTelemetryIsEnabled()
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
                Enabled = true,
                ServiceName = "GnOuGo.Agent.Server"
            }));

        Activity.Current = null;
        var logger = provider.CreateLogger("GnOuGo.Agent.Server.Workflow");

        logger.LogInformation("Startup log without activity should still be captured");

        var row = await queue.Channel.Reader.ReadAsync(CancellationToken.None);
        var logRow = Assert.IsType<LogRow>(row);
        Assert.Null(logRow.TraceId);
        Assert.Null(logRow.SpanId);
        Assert.Equal("Information", logRow.SeverityText);
        Assert.Contains("Startup log without activity should still be captured", logRow.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_SerializesScopesAndException_WithNestedValues_WithoutNullProperties()
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
                Enabled = true,
                ServiceName = "GnOuGo.Agent.Server"
            }));

        var logger = provider.CreateLogger("GnOuGo.Agent.Server.Workflow");
        var payload = JsonNode.Parse("""
            {
              "count": 2,
              "items": [1, null, true]
            }
            """)!;

        using (logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenant"] = "acme",
            ["attempt"] = 3,
            ["nullable"] = null,
            ["payload"] = payload,
            ["tags"] = new[] { "one", "two" },
            ["details"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["duration"] = TimeSpan.FromSeconds(5)
            }
        }))
        {
            logger.LogError(new EventId(42, "workflow.failed"), new InvalidOperationException("Boom"), string.Empty);
        }

        var row = await queue.Channel.Reader.ReadAsync(CancellationToken.None);
        var logRow = Assert.IsType<LogRow>(row);

        var attributes = JsonNode.Parse(logRow.AttributesJson!)!.AsObject();
        Assert.Equal("GnOuGo.Agent.Server.Workflow", attributes["log.category"]?.GetValue<string>());
        Assert.Equal(42, attributes["log.event_id"]?.GetValue<int>());
        Assert.Equal("workflow.failed", attributes["log.event_name"]?.GetValue<string>());
        Assert.Equal("System.InvalidOperationException", attributes["exception.type"]?.GetValue<string>());
        Assert.Equal("Boom", attributes["exception.message"]?.GetValue<string>());
        Assert.Equal("acme", attributes["scope.tenant"]?.GetValue<string>());
        Assert.Equal(3, attributes["scope.attempt"]?.GetValue<int>());
        Assert.False(attributes.ContainsKey("scope.nullable"));

        var scopePayload = Assert.IsType<JsonObject>(attributes["scope.payload"]);
        Assert.Equal(2, scopePayload["count"]?.GetValue<int>());
        var scopeItems = Assert.IsType<JsonArray>(scopePayload["items"]);
        Assert.Equal(3, scopeItems.Count);
        Assert.True(scopeItems[1] is null);
        Assert.True(scopeItems[2]!.GetValue<bool>());

        var scopeDetails = Assert.IsType<JsonObject>(attributes["scope.details"]);
        Assert.True(scopeDetails["success"]!.GetValue<bool>());
        Assert.Equal("00:00:05", scopeDetails["duration"]?.GetValue<string>());

        var scopeTags = Assert.IsType<JsonArray>(attributes["scope.tags"]);
        Assert.Equal(["one", "two"], scopeTags.Select(item => item!.GetValue<string>()).ToArray());

        var resource = JsonNode.Parse(logRow.ResourceJson!)!.AsObject();
        Assert.Equal("GnOuGo.Agent.Server", resource["service.name"]?.GetValue<string>());
        Assert.Equal("dotnet", resource["telemetry.sdk.language"]?.GetValue<string>());

        var scope = JsonNode.Parse(logRow.ScopeJson!)!.AsObject();
        Assert.Equal("GnOuGo.Agent.Server.Workflow", scope["name"]?.GetValue<string>());

        Assert.Contains("System.InvalidOperationException: Boom", logRow.Body, StringComparison.Ordinal);
    }
}


