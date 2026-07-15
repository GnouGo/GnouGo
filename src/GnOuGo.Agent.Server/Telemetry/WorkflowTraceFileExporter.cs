using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Captures completed activities and writes one LLM-readable JSON document for each
/// SmartFlow trace. Capture is independent of the OTLP exporter so local files also
/// work when remote OpenTelemetry export is disabled.
/// </summary>
public sealed class WorkflowTraceFileExporter : IWorkflowTraceFileExporter, IDisposable
{
    private readonly ConcurrentDictionary<string, CapturedTrace> _traces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeTraceIds = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<WorkflowTraceExportSettings> _settings;
    private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowTraceFileExporter> _logger;
    private readonly string _traceDirectory;
    private readonly ActivityListener _listener;

    public WorkflowTraceFileExporter(
        IOptionsMonitor<WorkflowTraceExportSettings> settings,
        IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings,
        IHostEnvironment environment,
        ILogger<WorkflowTraceFileExporter> logger)
        : this(
            settings,
            openTelemetrySettings,
            Path.Combine(GnOuGoWorkspace.ResolveWorkspaceDataDirectory(environment.ContentRootPath), "traces"),
            TimeProvider.System,
            logger)
    {
    }

    internal WorkflowTraceFileExporter(
        IOptionsMonitor<WorkflowTraceExportSettings> settings,
        IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings,
        string traceDirectory,
        TimeProvider timeProvider,
        ILogger<WorkflowTraceFileExporter> logger)
    {
        _settings = settings;
        _openTelemetrySettings = openTelemetrySettings;
        _timeProvider = timeProvider;
        _logger = logger;
        _traceDirectory = Path.GetFullPath(traceDirectory);

        _listener = new ActivityListener
        {
            // Keep the listener attached so an appsettings reload can enable capture
            // without recreating ActivitySource instances.
            ShouldListenTo = static _ => true,
            Sample = Sample,
            SampleUsingParentId = SampleUsingParentId,
            ActivityStopped = CaptureSafely
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void BeginCapture(string traceId)
    {
        if (!_settings.CurrentValue.Enabled || string.IsNullOrWhiteSpace(traceId))
            return;

        _activeTraceIds.TryAdd(traceId, 0);
        _traces.TryAdd(traceId, new CapturedTrace());
    }

    public async Task ExportAsync(string traceId, string correlationId, CancellationToken cancellationToken)
    {
        _activeTraceIds.TryRemove(traceId, out _);

        if (!_traces.TryRemove(traceId, out var trace))
            return;

        if (!_settings.CurrentValue.Enabled)
            return;

        try
        {
            CapturedSpan[] spans;
            lock (trace.SyncRoot)
            {
                spans = trace.Spans.Values
                    .OrderBy(static span => span.StartUtc)
                    .ThenBy(static span => span.SpanId, StringComparer.Ordinal)
                    .ToArray();
            }

            if (spans.Length == 0)
                return;

            Directory.CreateDirectory(_traceDirectory);
            var completionTime = _timeProvider.GetLocalNow();
            var baseName = completionTime.ToString("dd-MM-yy-HH-mm-ss", CultureInfo.InvariantCulture);
            var targetPath = Path.Combine(_traceDirectory, $"{baseName}.json");
            var tempPath = Path.Combine(_traceDirectory, $".{baseName}-{Guid.NewGuid():N}.tmp");

            try
            {
                await WriteDocumentAsync(tempPath, traceId, correlationId, spans, cancellationToken);
                targetPath = MoveToUniqueTarget(tempPath, targetPath, traceId);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            _logger.LogInformation(
                "Saved workflow OpenTelemetry trace {TraceId} to {TraceFilePath}.",
                traceId,
                targetPath);
        }
        catch (Exception ex)
        {
            // Trace persistence must never change the result of the workflow itself.
            _logger.LogWarning(ex, "Could not save workflow OpenTelemetry trace {TraceId}.", traceId);
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        _activeTraceIds.Clear();
        _traces.Clear();
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        try
        {
            if (!_settings.CurrentValue.Enabled)
                return ActivitySamplingResult.None;

            if (string.Equals(options.Source.Name, AgentOTelTelemetry.ActivitySourceName, StringComparison.Ordinal))
                return ActivitySamplingResult.AllDataAndRecorded;

            var parentTraceId = options.Parent.TraceId.ToHexString();
            return _activeTraceIds.ContainsKey(parentTraceId)
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None;
        }
        catch
        {
            return ActivitySamplingResult.None;
        }
    }

    private ActivitySamplingResult SampleUsingParentId(ref ActivityCreationOptions<string> options)
    {
        try
        {
            if (!_settings.CurrentValue.Enabled)
                return ActivitySamplingResult.None;

            if (string.Equals(options.Source.Name, AgentOTelTelemetry.ActivitySourceName, StringComparison.Ordinal))
                return ActivitySamplingResult.AllDataAndRecorded;

            var parentId = options.Parent;
            if (parentId is { Length: >= 35 } && parentId[2] == '-')
            {
                var parentTraceId = parentId.Substring(3, 32);
                if (_activeTraceIds.ContainsKey(parentTraceId))
                    return ActivitySamplingResult.AllDataAndRecorded;
            }

            return ActivitySamplingResult.None;
        }
        catch
        {
            return ActivitySamplingResult.None;
        }
    }

    private void CaptureSafely(Activity activity)
    {
        try
        {
            Capture(activity);
        }
        catch (Exception ex)
        {
            // ActivityListener callbacks execute inline with Activity.Stop(). They must
            // never leak an exception into application or thread-pool infrastructure.
            try
            {
                _logger.LogWarning(
                    ex,
                    "Could not capture OpenTelemetry activity '{ActivityName}' for a workflow trace.",
                    activity.DisplayName);
            }
            catch
            {
                // Logging is also best-effort inside a diagnostics callback.
            }
        }
    }

    private void Capture(Activity activity)
    {
        if (!_settings.CurrentValue.Enabled)
            return;

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(spanId))
            return;

        if (!_activeTraceIds.ContainsKey(traceId)
            || !_traces.TryGetValue(traceId, out var trace)
            || trace is null)
        {
            return;
        }

        var capturedSpan = CaptureSpan(activity);
        lock (trace.SyncRoot)
        {
            trace.Spans[spanId] = capturedSpan;
        }
    }

    private static CapturedSpan CaptureSpan(Activity activity)
    {
        var attributes = activity.TagObjects
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Key))
            .Select(static tag => new CapturedAttribute(tag.Key, tag.Value))
            .ToArray();
        var baggage = activity.Baggage
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(static item => new CapturedAttribute(item.Key, item.Value))
            .ToArray();
        var events = activity.Events
            .Select(static evt => new CapturedEvent(
                evt.Name,
                evt.Timestamp,
                evt.Tags.Select(static tag => new CapturedAttribute(tag.Key, tag.Value)).ToArray()))
            .ToArray();
        var links = activity.Links
            .Select(static link => new CapturedLink(
                link.Context.TraceId.ToHexString(),
                link.Context.SpanId.ToHexString(),
                link.Context.TraceFlags.ToString(),
                link.Tags?.Select(static tag => new CapturedAttribute(tag.Key, tag.Value)).ToArray() ?? []))
            .ToArray();

        return new CapturedSpan(
            activity.SpanId.ToHexString(),
            activity.ParentSpanId != default ? activity.ParentSpanId.ToHexString() : null,
            activity.OperationName,
            activity.DisplayName,
            activity.Kind.ToString(),
            new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            activity.Duration,
            activity.Status.ToString(),
            activity.StatusDescription,
            activity.ActivityTraceFlags.ToString(),
            activity.TraceStateString,
            activity.Source.Name,
            activity.Source.Version,
            attributes,
            baggage,
            events,
            links);
    }

    private async Task WriteDocumentAsync(
        string path,
        string traceId,
        string correlationId,
        IReadOnlyList<CapturedSpan> spans,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        var startUtc = spans.Min(static span => span.StartUtc);
        var endUtc = spans.Max(static span => span.StartUtc + span.Duration);
        var errorCount = spans.Count(static span => string.Equals(span.Status, "Error", StringComparison.Ordinal));
        var expectedProbeErrorCount = CountExpectedProbeErrors(spans);
        var workflowOutcome = ResolveWorkflowOutcome(spans);
        var recoveredErrorCount = string.Equals(workflowOutcome, "Ok", StringComparison.Ordinal)
            ? Math.Max(0, errorCount - expectedProbeErrorCount)
            : 0;
        var terminalErrorCount = string.Equals(workflowOutcome, "Ok", StringComparison.Ordinal)
            ? 0
            : Math.Max(0, errorCount - expectedProbeErrorCount);
        var summaryStatus = workflowOutcome switch
        {
            "Ok" when recoveredErrorCount > 0 || expectedProbeErrorCount > 0 => "ok_with_recovered_errors",
            "Ok" => "ok",
            "Error" => "error",
            _ => terminalErrorCount == 0 ? "ok" : "error"
        };
        var otelSettings = _openTelemetrySettings.CurrentValue;

        writer.WriteStartObject();
        writer.WriteString("schemaVersion", "gnougo.workflow-trace/v1");
        writer.WriteString("generatedAtUtc", _timeProvider.GetUtcNow());
        writer.WriteString("traceId", traceId);
        writer.WriteString("correlationId", correlationId);
        if (string.IsNullOrWhiteSpace(otelSettings.TenantId))
            writer.WriteNull("tenantId");
        else
            writer.WriteString("tenantId", otelSettings.TenantId);

        writer.WriteStartObject("service");
        writer.WriteString("name", otelSettings.ServiceName);
        writer.WriteEndObject();

        writer.WriteStartObject("summary");
        writer.WriteNumber("spanCount", spans.Count);
        writer.WriteNumber("errorSpanCount", errorCount);
        writer.WriteNumber("recoveredErrorSpanCount", recoveredErrorCount);
        writer.WriteNumber("expectedProbeErrorSpanCount", expectedProbeErrorCount);
        writer.WriteNumber("terminalErrorSpanCount", terminalErrorCount);
        writer.WriteString("startUtc", startUtc);
        writer.WriteString("endUtc", endUtc);
        writer.WriteNumber("durationMs", Math.Max(0d, (endUtc - startUtc).TotalMilliseconds));
        writer.WriteString("status", summaryStatus);
        writer.WriteEndObject();

        writer.WriteStartArray("spans");
        foreach (var span in spans)
            WriteSpan(writer, span);
        writer.WriteEndArray();
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string? ResolveWorkflowOutcome(IReadOnlyList<CapturedSpan> spans)
    {
        var workflowSpans = spans.Where(static span =>
                string.Equals(span.DisplayName, "workflow", StringComparison.Ordinal)
                || string.Equals(span.OperationName, "workflow", StringComparison.Ordinal)
                || string.Equals(span.DisplayName, "chat.command", StringComparison.Ordinal)
                || string.Equals(span.OperationName, "chat.command", StringComparison.Ordinal))
            .ToArray();

        if (workflowSpans.Any(static span => string.Equals(span.Status, "Error", StringComparison.Ordinal)))
            return "Error";
        if (workflowSpans.Any(static span => string.Equals(span.Status, "Ok", StringComparison.Ordinal)))
            return "Ok";
        return null;
    }

    private static int CountExpectedProbeErrors(IReadOnlyList<CapturedSpan> spans)
    {
        var spansById = spans.ToDictionary(static span => span.SpanId, StringComparer.Ordinal);
        return spans.Count(span =>
            string.Equals(span.Status, "Error", StringComparison.Ordinal)
            && IsExpectedHttpProbeStatus(span)
            && HasMcpDiscoveryAncestor(span, spansById));
    }

    private static bool IsExpectedHttpProbeStatus(CapturedSpan span)
    {
        if (!string.Equals(span.DisplayName, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(span.DisplayName, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var errorType = span.Attributes.FirstOrDefault(static attribute =>
            string.Equals(attribute.Key, "error.type", StringComparison.Ordinal))?.Value;
        var value = Convert.ToString(errorType, CultureInfo.InvariantCulture);
        return value is "404" or "405";
    }

    private static bool HasMcpDiscoveryAncestor(
        CapturedSpan span,
        IReadOnlyDictionary<string, CapturedSpan> spansById)
    {
        var parentId = span.ParentSpanId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentId)
               && visited.Add(parentId)
               && spansById.TryGetValue(parentId, out var parent))
        {
            if (parent.DisplayName.Contains("mcp_discovery", StringComparison.Ordinal)
                || parent.OperationName.Contains("mcp_discovery", StringComparison.Ordinal))
            {
                return true;
            }

            parentId = parent.ParentSpanId;
        }

        return false;
    }

    private static void WriteSpan(Utf8JsonWriter writer, CapturedSpan span)
    {
        writer.WriteStartObject();
        writer.WriteString("spanId", span.SpanId);
        if (span.ParentSpanId is null)
            writer.WriteNull("parentSpanId");
        else
            writer.WriteString("parentSpanId", span.ParentSpanId);
        writer.WriteString("name", span.DisplayName);
        writer.WriteString("operationName", span.OperationName);
        writer.WriteString("kind", span.Kind);
        writer.WriteString("startUtc", span.StartUtc);
        writer.WriteString("endUtc", span.StartUtc + span.Duration);
        writer.WriteNumber("durationMs", span.Duration.TotalMilliseconds);
        writer.WriteString("status", span.Status);
        if (span.StatusDescription is null)
            writer.WriteNull("statusDescription");
        else
            writer.WriteString("statusDescription", span.StatusDescription);
        writer.WriteString("traceFlags", span.TraceFlags);
        if (span.TraceState is null)
            writer.WriteNull("traceState");
        else
            writer.WriteString("traceState", span.TraceState);

        writer.WriteStartObject("instrumentationScope");
        writer.WriteString("name", span.SourceName);
        if (span.SourceVersion is null)
            writer.WriteNull("version");
        else
            writer.WriteString("version", span.SourceVersion);
        writer.WriteEndObject();

        WriteAttributes(writer, "attributes", span.Attributes);
        WriteAttributes(writer, "baggage", span.Baggage);

        writer.WriteStartArray("events");
        foreach (var evt in span.Events)
        {
            writer.WriteStartObject();
            writer.WriteString("name", evt.Name);
            writer.WriteString("timestampUtc", evt.Timestamp);
            WriteAttributes(writer, "attributes", evt.Attributes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("links");
        foreach (var link in span.Links)
        {
            writer.WriteStartObject();
            writer.WriteString("traceId", link.TraceId);
            writer.WriteString("spanId", link.SpanId);
            writer.WriteString("traceFlags", link.TraceFlags);
            WriteAttributes(writer, "attributes", link.Attributes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAttributes(Utf8JsonWriter writer, string propertyName, IReadOnlyList<CapturedAttribute> attributes)
    {
        writer.WriteStartObject(propertyName);
        foreach (var attribute in attributes)
        {
            writer.WritePropertyName(attribute.Key);
            WriteValue(writer, attribute.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case JsonElement json:
                json.WriteTo(writer);
                break;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string MoveToUniqueTarget(string tempPath, string requestedTargetPath, string traceId)
    {
        try
        {
            File.Move(tempPath, requestedTargetPath);
            return requestedTargetPath;
        }
        catch (IOException) when (File.Exists(requestedTargetPath))
        {
            var directory = Path.GetDirectoryName(requestedTargetPath)!;
            var timestamp = Path.GetFileNameWithoutExtension(requestedTargetPath);
            var suffix = traceId.Length >= 8 ? traceId[..8] : traceId;
            var collisionIndex = 1;

            while (true)
            {
                var disambiguator = collisionIndex == 1 ? suffix : $"{suffix}-{collisionIndex}";
                var collisionTarget = Path.Combine(directory, $"{timestamp}-{disambiguator}.json");
                try
                {
                    File.Move(tempPath, collisionTarget, overwrite: false);
                    return collisionTarget;
                }
                catch (IOException) when (File.Exists(collisionTarget))
                {
                    collisionIndex++;
                }
            }
        }
    }

    private sealed class CapturedTrace
    {
        public object SyncRoot { get; } = new();
        public Dictionary<string, CapturedSpan> Spans { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CapturedSpan(
        string SpanId,
        string? ParentSpanId,
        string OperationName,
        string DisplayName,
        string Kind,
        DateTimeOffset StartUtc,
        TimeSpan Duration,
        string Status,
        string? StatusDescription,
        string TraceFlags,
        string? TraceState,
        string SourceName,
        string? SourceVersion,
        CapturedAttribute[] Attributes,
        CapturedAttribute[] Baggage,
        CapturedEvent[] Events,
        CapturedLink[] Links);

    private sealed record CapturedEvent(
        string Name,
        DateTimeOffset Timestamp,
        CapturedAttribute[] Attributes);

    private sealed record CapturedLink(
        string TraceId,
        string SpanId,
        string TraceFlags,
        CapturedAttribute[] Attributes);

    private sealed record CapturedAttribute(string Key, object? Value);
}
