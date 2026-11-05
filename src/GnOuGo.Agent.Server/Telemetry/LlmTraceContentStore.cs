using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Telemetry;

public sealed partial class LlmTraceContentStore(IKeyVaultRecordStore records, IOptionsMonitor<TraceDebugSettings> settings,
    IOptions<OpenTelemetrySettings> telemetry, IPlanningSessionStore sessions, LLMRuntimeOptionsStore? runtimeOptions = null) : IPlanningJournalObserver
{
    internal const string Collection = "agent-llm-diagnostic-content-v1";
    internal const string DocumentCollection = Collection + "-documents";
    private const string Author = "GnOuGo.Agent.Server.TraceDebug";
    internal string Tenant => WorkflowExecutionTenant.Resolve(telemetry);
    internal bool Enabled => settings.CurrentValue.Enabled;
    internal int Limit => Math.Max(1, settings.CurrentValue.MaxDocumentBytes);
    private DateTimeOffset Cutoff => DateTimeOffset.UtcNow.AddDays(-Math.Max(1, settings.CurrentValue.ContentRetentionDays));

    internal async Task SaveAsync(string id, LlmTraceContent content, CancellationToken ct)
    {
        if (!Enabled || content.TenantId != Tenant) return;
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(content, LlmTraceJsonContext.Default.LlmTraceContent), LlmTraceJsonContext.Default.LlmTraceContent)!;
        copy.Documents = await PrepareTraceDocumentsAsync(id, copy, ct);
        if (copy.Input?.Length > 32768) copy.Input = null;
        if (copy.Output?.Length > 32768) copy.Output = null;
        await records.UpsertAsync(Collection, Tenant, id, JsonSerializer.Serialize(copy, LlmTraceJsonContext.Default.LlmTraceContent), Author, ct);
    }

    public async Task<LlmTraceContent?> LoadAsync(string id, string trace, string span, string? session, CancellationToken ct)
    {
        if (!Enabled) return null;
        var record = await records.GetAsync(Collection, Tenant, id, Author, ct);
        if (record is null || record.CreatedAt < Cutoff) return null;
        var content = JsonSerializer.Deserialize(record.Value, LlmTraceJsonContext.Default.LlmTraceContent);
        if (content is null || content.TenantId != Tenant || content.TraceId != trace || content.SpanId != span
            || session is not null && content.SessionId != session) return null;
        foreach (var doc in content.Documents ?? [])
            if (doc.Title == "Request and response schema" && content.Input is null) content.Input = (await records.GetAsync(DocumentCollection, Tenant, doc.Id, Author, ct))?.Value;
            else if (doc.Title == "Response and tool calls" && content.Output is null) content.Output = (await records.GetAsync(DocumentCollection, Tenant, doc.Id, Author, ct))?.Value;
        if (content.Journal is not null && content.RequestKey is not null && content.SessionId is not null)
        {
            var journal = await LoadJournalAsync(content.SessionId, content.Journal == "flow", content.RequestKey, ct);
            if (journal is null) { content.InputStatus = "journal unavailable"; content.OutputStatus = "journal unavailable"; }
            else
            {
                content.Input = journal.Input; content.Output = journal.Output;
                content.InputStatus = journal.InputStatus;
                // A missing completion does not erase a captured provider rejection.
                if (!content.OutputStatus.StartsWith("rejected", StringComparison.Ordinal) || journal.OutputBytes is not null)
                    content.OutputStatus = journal.OutputStatus;
                content.InputBytes = journal.InputBytes; content.OutputBytes = journal.OutputBytes;
            }
        }
        return content;
    }

    internal async Task<(string Journal, string Key)?> FindJournalAsync(string? session, string? requestId, CancellationToken ct)
    {
        if (session is null || requestId is null || !requestId.StartsWith(session + ":", StringComparison.Ordinal)) return null;
        foreach (var journal in new[] { "flow", "agent" })
        {
            var key = journal == "flow" ? requestId : session + ":" + requestId;
            if (records is IKeyVaultRecordReader reader && await reader.GetMetadataAsync(journal + "-planning-model-requests-v11", Tenant, key, Author, ct) is not null)
                return (journal, key);
        }
        return null;
    }

    private async Task<PlanningSession?> OwnedSessionAsync(string id, bool workflow, CancellationToken ct)
    {
        var session = workflow
            ? (await records.GetAsync("flow-planning-sessions-v11", Tenant, id, Author, ct) is { } value
                ? JsonSerializer.Deserialize(value.Value, PlanningJsonContext.Default.PlanningSession) : null)
            : await sessions.LoadAsync(Tenant, id, ct);
        return session?.Request.SessionId == id && session.Request.TenantId == Tenant && session.SchemaVersion == 11 ? session : null;
    }

    private async Task<bool> OwnsSession(string id, bool workflow, CancellationToken ct) => await OwnedSessionAsync(id, workflow, ct) is not null;

    public async Task<IReadOnlyList<LlmJournalCall>> HistoryAsync(string session, bool workflow, CancellationToken ct)
    {
        var result = new List<LlmJournalCall>();
        LlmCallCursor? cursor = null;
        while (true)
        {
            var page = await ReadCallsAsync(session, workflow, cursor, ct);
            result.AddRange(page.Calls.Select(c => new LlmJournalCall(c.JournalKey!, c.CreatedAt ?? DateTimeOffset.MinValue,
                c.Provider ?? "unavailable", c.Model ?? "unavailable", c.ResponseStatus, c.InputTokens, c.OutputTokens)));
            cursor = page.Cursor;
            if (!page.HasMore) break;
        }
        return result.DistinctBy(c => c.Key).ToArray();
    }

    public async Task<LlmTraceContent?> LoadJournalAsync(string session, bool workflow, string key, CancellationToken ct)
    {
        if (!Enabled || !key.StartsWith(session + ":", StringComparison.Ordinal) || !await OwnsSession(session, workflow, ct)) return null;
        var prefix = workflow ? "flow" : "agent";
        var record = await records.GetAsync(prefix + "-planning-model-requests-v11", Tenant, key, Author, ct);
        if (record is null) return null;
        var request = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.LLMRequest);
        if (request?.ClientRequestId?.StartsWith(session + ":", StringComparison.Ordinal) != true) return null;
        if (key != (workflow ? request.ClientRequestId : session + ":" + request.ClientRequestId)) return null;
        var receipt = await records.GetAsync(prefix + "-planning-model-receipts-v11", Tenant, key, Author, ct);
        var result = new LlmTraceContent { TenantId = Tenant, SessionId = session, CreatedAt = record.CreatedAt };
        SetInput(result, RequestDocument(request));
        if (receipt is not null)
            SetOutput(result, ResponseDocument(JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)!));
        else result.OutputStatus = "unavailable — no completion receipt; usage unknown";
        return result;
    }

    internal void SetInput(LlmTraceContent content, string text)
    {
        text = Redact(text);
        content.InputBytes = Encoding.UTF8.GetByteCount(text);
        content.InputStatus = "available";
        content.Input = text;
    }
    internal void SetOutput(LlmTraceContent content, string text)
    {
        text = Redact(text);
        content.OutputBytes = Encoding.UTF8.GetByteCount(text);
        content.OutputStatus = "available";
        content.Output = text;
    }

    internal string Redact(string value)
    {
        value = DiagnosticRedaction.Redact(value);
        if (runtimeOptions is null) return value;
        foreach (var provider in runtimeOptions.Current.Models.Values)
            foreach (var secret in new[] { provider.ApiKey, provider.ClientSecret, provider.PrivateKeyPem })
                if (!string.IsNullOrEmpty(secret))
                {
                    value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
                    value = value.Replace(JsonValue.Create(secret)!.ToJsonString()[1..^1], "[redacted]", StringComparison.Ordinal);
                }
        return value;
    }

    // Whitelist logical content. Provider options, headers and raw provider reasoning are never serialized.
    internal static string RequestDocument(LLMRequest request)
        => JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
    internal static string ResponseDocument(LLMResponse response)
        => JsonSerializer.Serialize(new LLMResponse { Text = response.Text, Json = response.Json, Usage = response.Usage,
            ToolCalls = response.ToolCalls, CompletionStatus = response.CompletionStatus }, PlanningJsonContext.Default.LLMResponse);

    public async Task PurgeAsync(CancellationToken ct)
    {
        // Only diagnostic content and its parts. Never hydrate bodies for retention or touch replay journals.
        if (records is not IKeyVaultRecordReader reader) return;
        foreach (var collection in new[] { Collection, DocumentCollection })
        {
            KeyVaultRecordCursor? cursor = null;
            while (true)
            {
                var page = await reader.QueryMetadataAsync(collection, Tenant, "", Author, cursor, ct: ct);
                foreach (var record in page.Records)
                    if (record.CreatedAt < Cutoff) await records.DeleteAsync(collection, Tenant, record.Key, Author, ct);
                cursor = page.Next;
                if (!page.HasMore) break;
            }
        }
    }
}

internal sealed class LlmTraceRetentionWorker(LlmTraceContentStore store, ILogger<LlmTraceRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try { await store.PurgeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Diagnostic retention unavailable ({ErrorType}).", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
