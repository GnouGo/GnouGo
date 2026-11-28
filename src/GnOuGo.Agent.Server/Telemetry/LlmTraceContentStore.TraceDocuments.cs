using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Telemetry;

public sealed partial class LlmTraceContentStore
{
    private async Task<List<LlmDocumentReference>> PrepareTraceDocumentsAsync(string id, LlmTraceContent content, CancellationToken ct)
    {
        var result = new List<LlmDocumentReference>();
        foreach (var (title, text) in new[] { ("Request and response schema", content.Input), ("Response and tool calls", content.Output) })
            if (text is not null)
            {
                var redacted = Redact(text); var hash = Digest(redacted);
                var key = "document:" + Digest(id + "\n" + title + "\n" + hash);
                await records.UpsertAsync(DocumentCollection, Tenant, key, redacted, Author, ct);
                result.Add(new(key, title, hash, redacted.Length, Encoding.UTF8.GetByteCount(redacted)));
            }
        return result;
    }

    internal async Task<JsonObject?> TraceDocumentsAsync(string id, string trace, string span, string? session, CancellationToken ct)
    {
        if (!Enabled) return null;
        if (records is not IKeyVaultRecordReader reader) throw new NotSupportedException("Authenticated document paging is unavailable.");
        LlmTraceContent? journal = null;
        var gate = PreparationGate(Collection, id);
        await gate.WaitAsync(ct);
        try
        {
            var source = await reader.GetMetadataAsync(Collection, Tenant, id, Author, ct);
            if (source is null || source.CreatedAt < Cutoff) return null;
            var cached = await records.GetAsync(DocumentCollection, Tenant, "trace-pages:" + id, Author, ct);
            var index = cached is null ? null : JsonNode.Parse(cached.Value)!.AsObject();
            if (index?["sourceVersion"]?.GetValue<long>() == source.UpdatedAt.UtcTicks)
                return index["tenant"]?.ToString() == Tenant && index["trace"]?.ToString() == trace && index["span"]?.ToString() == span
                    && (session is null || index["session"]?.ToString() == session) ? index : null;
            var record = await records.GetAsync(Collection, Tenant, id, Author, ct);
            if (record is null || record.CreatedAt < Cutoff) return null;
            var content = JsonSerializer.Deserialize(record.Value, LlmTraceJsonContext.Default.LlmTraceContent);
            if (content is null || content.TenantId != Tenant || content.TraceId != trace || content.SpanId != span || session is not null && content.SessionId != session) return null;
            if (content.Journal is not null && content.RequestKey is not null && content.SessionId is not null) journal = content;
            else
            {
                var docs = content.Documents ?? await PrepareTraceDocumentsAsync(id, content, ct);
                var detail = new JsonObject { ["tenant"] = Tenant, ["trace"] = trace, ["span"] = span, ["session"] = content.SessionId,
                    ["sourceVersion"] = record.UpdatedAt.UtcTicks, ["responseStatus"] = content.OutputStatus,
                    ["documents"] = new JsonArray(docs.Select(d => (JsonNode)new JsonObject { ["id"] = d.Id, ["title"] = d.Title,
                        ["version"] = d.Version, ["characters"] = d.Characters, ["bytes"] = d.Bytes }).ToArray()) };
                await records.UpsertAsync(DocumentCollection, Tenant, "trace-pages:" + id, detail.ToJsonString(), Author, ct);
                return detail;
            }
        }
        finally { gate.Release(); }
        // Do not nest striped preparation locks when resolving a journal-backed trace reference.
        return await JournalDocumentsAsync(journal.SessionId!, journal.Journal == "flow", journal.RequestKey!, ct);
    }
}
