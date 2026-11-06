using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Telemetry;

public sealed record LlmDocumentReference(string Id, string Title, string Version, int Characters, int Bytes);
public sealed record LlmDocumentSearch(int? Match, int NextOffset, bool Complete, string Version);

public sealed partial class LlmTraceContentStore
{
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string SourceKey(string collection, string key) => "source:" + Digest(collection + "\n" + key);
    private static readonly SemaphoreSlim[] PreparationGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SemaphoreSlim PreparationGate(string collection, string key)
        => PreparationGates[(int)((uint)StringComparer.Ordinal.GetHashCode(Tenant + "\n" + collection + "\n" + key) % PreparationGates.Length)];

    public async Task ObserveAsync(string collection, string tenant, string session, string key, string value, long version, CancellationToken ct)
    {
        var gate = PreparationGate(collection, key);
        await gate.WaitAsync(ct);
        try { await ObserveCoreAsync(collection, tenant, session, key, value, version, ct); }
        finally { gate.Release(); }
    }

    private async Task ObserveCoreAsync(string collection, string tenant, string session, string key, string value, long version, CancellationToken ct)
    {
        if (!Enabled || tenant != Tenant) return;
        var owner = collection.StartsWith("flow-", StringComparison.Ordinal) ? "flow" : "agent";
        var requestId = owner == "agent" && !collection.Contains("-http-", StringComparison.Ordinal) ? key[(session.Length + 1)..] : key;
        if (!requestId.StartsWith(session + ":", StringComparison.Ordinal) || key != (owner == "agent" && !collection.Contains("-http-", StringComparison.Ordinal) ? session + ":" + requestId : requestId)) return;
        var source = JsonNode.Parse(value)!.AsObject();
        var docs = new JsonArray();
        var metadata = new JsonObject { ["tenant"] = tenant, ["session"] = session, ["requestId"] = requestId,
            ["collection"] = collection, ["key"] = key, ["sourceVersion"] = version, ["documents"] = docs };
        if (collection.Contains("-requests-", StringComparison.Ordinal))
        {
            if (source["clientRequestId"]?.ToString() != requestId) return;
            metadata["provider"] = source["provider"]?.DeepClone(); metadata["model"] = source["model"]?.DeepClone();
            metadata["settings"] = new JsonObject();
            foreach (var name in new[] { "model", "provider", "maxTokens", "reasoning", "temperature", "useBackgroundMode", "requestTimeoutSeconds" })
                metadata["settings"]![name] = source[name]?.DeepClone();
            metadata["requestHash"] = Digest(value);
            await AddDocument("Logical request and response schema", value);
        }
        else if (collection.Contains("-receipts-", StringComparison.Ordinal))
        {
            metadata["usage"] = source["usage"]?.DeepClone(); metadata["completionStatus"] = source["completionStatus"]?.DeepClone();
            await AddDocument("Logical response and reported usage", ResponseDocument(JsonSerializer.Deserialize(value, PlanningJsonContext.Default.LLMResponse)!));
        }
        else if (collection.Contains("-http-", StringComparison.Ordinal))
        {
            if (source["tenant"]?.ToString() != tenant || source["session"]?.ToString() != session) return;
            metadata["requestHash"] = source["request_hash"]?.DeepClone();
            metadata["policy"] = source["policy"]?.DeepClone();
            metadata["deadlineUtc"] = source["deadline_utc"]?.DeepClone();
            metadata["outputAllocation"] = source["output_ceiling"]?.DeepClone();
            metadata["reservationInputEstimate"] = source["input_ceiling"]?.DeepClone();
            var transport = source["transport"]?.DeepClone();
            if (transport is not null)
            {
                if (transport["RequestBody"] is { } body) { await AddDocument("Actual serialized HTTP request", body.ToString()); transport["RequestBody"] = null; }
                foreach (var attempt in transport["Attempts"]?.AsArray() ?? [])
                    foreach (var name in new[] { "Body", "PartialBody" })
                        if (attempt?[name] is { } bodyPart)
                        { await AddDocument("HTTP attempt " + attempt["Id"] + " — " + name, bodyPart.ToString()); attempt[name] = null; }
                metadata["transport"] = transport;
            }
        }
        else return;
        // Publish the small descriptor last. Readers either see the previous complete version or this complete version.
        await records.UpsertAsync(DocumentCollection, tenant, SourceKey(collection, key), Redact(metadata.ToJsonString()), Author, ct);

        async Task AddDocument(string title, string text)
        {
            text = Redact(text); // Redact the whole logical value before chunking, including escaped secrets and Unicode boundaries.
            var hash = Digest(text);
            var id = "document:" + Digest(collection + "\n" + key + "\n" + title + "\n" + hash);
            await records.UpsertAsync(DocumentCollection, tenant, id, text, Author, ct);
            docs.Add(new JsonObject { ["id"] = id, ["title"] = title, ["version"] = hash, ["characters"] = text.Length, ["bytes"] = Encoding.UTF8.GetByteCount(text) });
        }
    }

    private async Task<JsonObject?> SourceAsync(string collection, string session, string key, bool convert, CancellationToken ct)
    {
        if (records is not IKeyVaultRecordReader reader) return null;
        var source = await reader.GetMetadataAsync(collection, Tenant, key, Author, ct);
        if (source is null) return null;
        var index = await records.GetAsync(DocumentCollection, Tenant, SourceKey(collection, key), Author, ct);
        var manifest = index is null ? null : JsonNode.Parse(index.Value)!.AsObject();
        if (manifest?["sourceVersion"]?.GetValue<long>() != source.UpdatedAt.UtcTicks)
        {
            if (!convert) return null;
            // Approved historical policy: one explicit hydration per source version, never during polling.
            var gate = PreparationGate(collection, key);
            await gate.WaitAsync(ct);
            try
            {
                source = await reader.GetMetadataAsync(collection, Tenant, key, Author, ct);
                if (source is null) return null;
                index = await records.GetAsync(DocumentCollection, Tenant, SourceKey(collection, key), Author, ct);
                manifest = index is null ? null : JsonNode.Parse(index.Value)!.AsObject();
                if (manifest?["sourceVersion"]?.GetValue<long>() != source.UpdatedAt.UtcTicks)
                {
                    var old = await records.GetAsync(collection, Tenant, key, Author, ct);
                    if (old is null) return null;
                    await ObserveCoreAsync(collection, Tenant, session, key, old.Value, old.UpdatedAt.UtcTicks, ct);
                    index = await records.GetAsync(DocumentCollection, Tenant, SourceKey(collection, key), Author, ct);
                    manifest = index is null ? null : JsonNode.Parse(index.Value)!.AsObject();
                }
            }
            finally { gate.Release(); }
        }
        if (manifest?["tenant"]?.ToString() != Tenant || manifest["session"]?.ToString() != session || manifest["collection"]?.ToString() != collection || manifest["key"]?.ToString() != key) return null;
        return manifest;
    }

    internal async Task<JsonObject> JournalDocumentsAsync(string session, bool workflow, string key, CancellationToken ct)
    {
        if (!Enabled || await OwnedSessionAsync(session, workflow, ct) is not { } state) throw new KeyNotFoundException("Planning session not found.");
        var prefix = workflow ? "flow" : "agent";
        var requestId = workflow ? key : key.StartsWith(session + ":", StringComparison.Ordinal) ? key[(session.Length + 1)..] : "";
        if (!requestId.StartsWith(session + ":", StringComparison.Ordinal) || key != (workflow ? requestId : session + ":" + requestId)) throw new KeyNotFoundException("Request not owned by this session.");
        var request = await SourceAsync(prefix + "-planning-model-requests-v11", session, key, true, ct) ?? throw new KeyNotFoundException("Request unavailable.");
        var response = await SourceAsync(prefix + "-planning-model-receipts-v11", session, key, true, ct);
        var http = await SourceAsync(prefix + "-planning-model-http-v11", session, requestId, true, ct);
        if (http is not null && !string.Equals(http["requestHash"]?.ToString(), request["requestHash"]?.ToString(), StringComparison.OrdinalIgnoreCase)) throw new KeyNotFoundException("HTTP evidence does not match this request.");
        return new JsonObject { ["sessionId"] = session, ["requestId"] = requestId, ["settings"] = request["settings"]?.DeepClone(),
            ["responseStatus"] = response is not null ? "Response received — validation and business review are separate" :
                state.RequestMeasurements?.SingleOrDefault(m => m.RequestId == requestId)?.Result == "financial_refusal_before_dispatch"
                    ? LlmInspectionLabels.FinancialRefusal : "No completion receipt — outcome or usage may be uncertain",
            ["completionStatus"] = response?["completionStatus"]?.DeepClone(),
            ["usage"] = response?["usage"]?.DeepClone(), ["transport"] = http?["transport"]?.DeepClone(),
            ["transportPolicy"] = http?["policy"]?.DeepClone(), ["deadlineUtc"] = http?["deadlineUtc"]?.DeepClone(),
            ["outputAllocation"] = http?["outputAllocation"]?.DeepClone(), ["reservationInputEstimate"] = http?["reservationInputEstimate"]?.DeepClone(),
            ["documents"] = new JsonArray(new[] { request, response, http }.Where(s => s is not null).SelectMany(s => s!["documents"]!.AsArray()).Select(d => d!.DeepClone()).ToArray()) };
    }

    internal static IReadOnlyList<LlmDocumentReference> Documents(JsonObject detail) => detail["documents"]?.AsArray().Select(d => new LlmDocumentReference(
        d!["id"]!.ToString(), d["title"]!.ToString(), d["version"]!.ToString(), d["characters"]!.GetValue<int>(), d["bytes"]!.GetValue<int>())).ToArray() ?? [];

    internal async Task<KeyVaultRecordRange> ReadDocumentAsync(JsonObject detail, string document, string? version, int offset, int count, CancellationToken ct)
    {
        var descriptor = Documents(detail).SingleOrDefault(d => d.Id == document) ?? throw new KeyNotFoundException("Document not owned by this request.");
        if (version is not null && descriptor.Version != version) throw new KeyVaultRecordVersionException();
        if (records is not IKeyVaultRecordReader reader) throw new NotSupportedException("The configured record store does not support authenticated pages.");
        return await reader.ReadRangeAsync(DocumentCollection, Tenant, document, Author, descriptor.Version, offset, count, ct) ?? throw new KeyNotFoundException("Document unavailable.");
    }

    internal async Task<LlmDocumentSearch> SearchDocumentAsync(JsonObject detail, string document, string version, string query, int offset, CancellationToken ct)
    {
        if (query.Length is < 1 or > 1024) throw new ArgumentException("Search must contain 1–1024 characters.");
        var previous = ""; var start = offset;
        // Bounded work per request with exact continuation; matches can straddle encrypted chunks/pages.
        for (var i = 0; i < 8; i++)
        {
            var page = await ReadDocumentAsync(detail, document, version, start, 32768, ct);
            var scan = previous + page.Text;
            var found = scan.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (found >= 0) return new(start - previous.Length + found, page.NextOffset, page.Complete, page.Version);
            if (page.Complete) return new(null, page.NextOffset, true, page.Version);
            var overlap = Math.Min(query.Length - 1, scan.Length);
            if (overlap > 0 && overlap < scan.Length && char.IsLowSurrogate(scan[scan.Length - overlap])) overlap++;
            previous = scan[^overlap..]; start = page.NextOffset;
        }
        // Re-read overlap on the next continuation so no boundary match is lost.
        return new(null, start - previous.Length, false, version);
    }
}
