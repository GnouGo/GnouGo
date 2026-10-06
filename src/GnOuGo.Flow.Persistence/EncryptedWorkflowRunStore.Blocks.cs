using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Persistence;

public sealed partial class EncryptedWorkflowRunStore
{
    // Private physical layout. Public WorkflowRun serialization remains schema 9.
    // A root is published only after all immutable blocks exist. Unreferenced
    // blocks from an interrupted write are never completion evidence.
    private sealed class SplitJournal(IKeyVaultRecordStore records, string tenant, string runId, JsonObject head)
    {
        private const string Blocks = "flow-execution-blocks-v1";
        private const int InlineBytes = 4096, ChunkSize = 32;
        private readonly string _prefix = Hash(new JsonArray(tenant, runId).ToJsonString()) + "/";
        private readonly Dictionary<string, JsonNode> _loaded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _written = new(StringComparer.Ordinal);
        private int _eventPrefixCount = head["eventCount"]?.GetValue<int>() ?? 0;
        public JsonObject Head { get; private set; } = head;
        public long Revision => Head["revision"]!.GetValue<long>();
        public bool CancelRequested => Head["cancelRequested"]!.GetValue<bool>();

        public static JsonObject NewHead(string tenant, string id) => new()
        {
            ["storageVersion"] = 1, ["tenantId"] = tenant, ["runId"] = id, ["revision"] = 0,
            ["cancelRequested"] = false, ["invocations"] = new JsonObject(), ["events"] = new JsonArray()
        };

        public static JsonObject? Parse(string json, string tenant, string id)
        {
            var node = JsonNode.Parse(json) as JsonObject ?? throw Invalid();
            if (!node.ContainsKey("storageVersion")) return null;
            if (node["storageVersion"]?.GetValue<int>() != 1) throw new WorkflowRunConflictException(WorkflowRunStorage.IncompatibleMessage);
            if (node["tenantId"]?.ToString() != tenant || node["runId"]?.ToString() != id ||
                node["invocations"] is not JsonObject || node["events"] is not JsonArray ||
                node["revision"] is not JsonValue revision || !revision.TryGetValue<long>(out var revisionNumber) || revisionNumber < 0 ||
                node["cancelRequested"] is not JsonValue cancellation || !cancellation.TryGetValue<bool>(out _) ||
                node["eventCount"] is not JsonValue count || !count.TryGetValue<int>(out var eventCount) || eventCount < 0 || node["header"] is null)
                throw Invalid();
            return node;
        }

        public async Task RefreshAsync(CancellationToken ct)
        {
            var record = await records.GetAsync(Collection, tenant, runId, Author, ct) ?? throw Invalid();
            Head = Parse(record.Value, tenant, runId) ?? throw Invalid();
        }

        public async Task<WorkflowRun> ReadAsync(CancellationToken ct)
        {
            var header = await DecodeAsync(Head["header"], ct) as JsonObject ?? throw Invalid();
            var invocations = new JsonObject();
            foreach (var pair in Head["invocations"]!.AsObject())
                invocations.Add(pair.Key, await DecodeAsync(pair.Value, ct));
            header["invocations"] = invocations;
            header["events"] = await EventsAsync(ct);
            var result = WorkflowRunStorage.Read(header.ToJsonString(), tenant, runId);
            if (result.Revision != Revision || result.CancelRequested != CancelRequested) throw Invalid();
            foreach (var pair in result.Invocations) if (pair.Key != pair.Value.Id) throw Invalid();
            if (result.Events.Count != Head["eventCount"]?.GetValue<int>()) throw Invalid();
            _loaded.Clear();
            return result;
        }

        // Only out-of-owner commands can change the committed head while the
        // execution lease is held. Load their changed invocations, not snapshots
        // of the owner's unchanged steps.
        public async Task<IReadOnlyCollection<string>> MergeCommandsAsync(WorkflowRun run, CancellationToken ct)
        {
            var previous = Head;
            await RefreshAsync(ct);
            if (Revision < previous["revision"]!.GetValue<long>()) throw Invalid();
            var commands = new WorkflowRun { CancelRequested = CancelRequested };
            if (Revision != previous["revision"]!.GetValue<long>())
            {
                foreach (var pair in Head["invocations"]!.AsObject())
                    if (!JsonNode.DeepEquals(pair.Value, previous["invocations"]![pair.Key]))
                    {
                        var json = await DecodeAsync(pair.Value, ct) ?? throw Invalid();
                        var invocation = json.Deserialize(WorkflowRunJsonContext.Default.WorkflowInvocation) ?? throw Invalid();
                        if (invocation.Id != pair.Key) throw Invalid();
                        commands.Invocations[pair.Key] = invocation;
                    }
                foreach (var e in await EventsAsync(ct))
                    commands.Events.Add(new(e!["timestamp"]!.GetValue<DateTimeOffset>(), e["kind"]!.GetValue<string>(), e["invocationId"]?.GetValue<string>()));
            }
            var changed = commands.Invocations.Keys.Concat(run.Invocations.Values
                .Where(i => i.ParentInvocationId is not null && i.CompletedAt is null &&
                    run.Invocations.TryGetValue(i.ParentInvocationId, out var parent) && parent.CompletedAt is not null)
                .Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToArray();
            WorkflowRunStorage.MergeCommands(run, commands);
            return changed;
        }

        private async Task<JsonArray> EventsAsync(CancellationToken ct)
        {
            var events = new JsonArray();
            foreach (var chunk in Head["events"]!.AsArray())
            {
                var values = await DecodeAsync(chunk, ct) as JsonArray ?? throw Invalid();
                foreach (var value in values) events.Add(value?.DeepClone());
            }
            return events;
        }

        public async Task SaveAsync(WorkflowRun run, IReadOnlyCollection<string>? changed, CancellationToken ct)
        {
            WorkflowRunStorage.ValidateOwnership(run, tenant, runId);
            var next = (JsonObject)Head.DeepClone();
            next["revision"] = run.Revision; next["cancelRequested"] = run.CancelRequested;
            // Serialize only the header, not the invocation dictionary. Payloads
            // remain full fidelity but identical subtrees are stored once.
            var header = new WorkflowRun
            {
                SchemaVersion = run.SchemaVersion, TenantId = run.TenantId, RunId = run.RunId, Revision = run.Revision,
                WorkflowName = run.WorkflowName, WorkflowYaml = run.WorkflowYaml, DefinitionHash = run.DefinitionHash,
                Inputs = run.Inputs, Limits = run.Limits, ModelBudget = run.ModelBudget, ModelUsage = run.ModelUsage,
                StepsStarted = run.StepsStarted, FinalizationStepsStarted = run.FinalizationStepsStarted,
                Status = run.Status, CancelRequested = run.CancelRequested, FinalizationStarted = run.FinalizationStarted,
                FinalizationCompleted = run.FinalizationCompleted, Result = run.Result, CreatedAt = run.CreatedAt, UpdatedAt = run.UpdatedAt
            };
            next["header"] = await EncodeAsync(JsonSerializer.SerializeToNode(header, WorkflowRunJsonContext.Default.WorkflowRun), ct, force: true);
            var invocations = changed is null ? new JsonObject() : next["invocations"]!.AsObject();
            IEnumerable<string> ids = changed is null ? run.Invocations.Keys : changed;
            foreach (var id in ids)
            {
                if (!run.Invocations.TryGetValue(id, out var invocation)) throw Invalid();
                invocations[id] = await EncodeAsync(JsonSerializer.SerializeToNode(invocation, WorkflowRunJsonContext.Default.WorkflowInvocation), ct, force: true);
            }
            if (changed is null) next["invocations"] = invocations;
            var events = new JsonArray();
            // Completed event chunks never change. Only the tail is serialized.
            var previousCount = Head["eventCount"]?.GetValue<int>() ?? 0;
            if (changed is not null && run.Events.Count < previousCount) throw Invalid();
            for (var i = 0; i < run.Events.Count; i += ChunkSize)
            {
                if (changed is not null && i + ChunkSize <= _eventPrefixCount)
                { events.Add(Head["events"]![i / ChunkSize]!.DeepClone()); continue; }
                var chunk = new JsonArray();
                foreach (var e in run.Events.Skip(i).Take(ChunkSize))
                    chunk.Add((JsonNode)new JsonObject { ["timestamp"] = e.Timestamp, ["kind"] = e.Kind, ["invocationId"] = e.InvocationId });
                events.Add(await EncodeAsync(chunk, ct, force: true));
            }
            next["events"] = events; next["eventCount"] = run.Events.Count;
            await records.UpsertAsync(Collection, tenant, runId, next.ToJsonString(), Author, ct);
            Head = next; _eventPrefixCount = run.Events.Count;
            // Read caching is for one reconstruction/command merge, not a second
            // unbounded in-memory copy of all execution observations.
            _loaded.Clear();
        }

        private async Task<JsonNode> EncodeAsync(JsonNode? value, CancellationToken ct, bool force = false)
        {
            var json = value?.ToJsonString() ?? "null";
            if (!force && Encoding.UTF8.GetByteCount(json) <= InlineBytes) return new JsonArray(0, value?.DeepClone());
            var block = new JsonObject();
            switch (value)
            {
                case JsonObject obj:
                    block["kind"] = "object";
                    var fields = new JsonArray();
                    foreach (var pair in obj) fields.Add((JsonNode)new JsonArray(pair.Key, await EncodeAsync(pair.Value, ct)));
                    block["values"] = fields; break;
                case JsonArray array:
                    var chunks = array.Count > ChunkSize;
                    block["kind"] = chunks ? "chunks" : "array";
                    var items = new JsonArray();
                    if (chunks)
                        for (var i = 0; i < array.Count; i += ChunkSize)
                            items.Add(await EncodeAsync(new JsonArray(array.Skip(i).Take(ChunkSize).Select(v => v?.DeepClone()).ToArray()), ct));
                    else foreach (var item in array) items.Add(await EncodeAsync(item, ct));
                    block["values"] = items; break;
                default: block["kind"] = "value"; block["value"] = value?.DeepClone(); break;
            }
            // Normalize typed JsonValue writers (for example DateTimeOffset) to
            // JSON tokens before hashing; parsing must reproduce identical bytes.
            block = JsonNode.Parse(block.ToJsonString())!.AsObject();
            var body = block.ToJsonString(); var hash = Hash(body);
            if (!_written.Contains(hash))
            {
                var envelope = new JsonObject { ["tenantId"] = tenant, ["runId"] = runId, ["body"] = block };
                await records.UpsertAsync(Blocks, tenant, _prefix + hash, envelope.ToJsonString(), Author, ct);
                _written.Add(hash);
            }
            return new JsonArray(1, hash);
        }

        private Task<JsonNode?> DecodeAsync(JsonNode? reference, CancellationToken ct) => DecodeAsync(reference, new HashSet<string>(StringComparer.Ordinal), ct);
        private async Task<JsonNode?> DecodeAsync(JsonNode? reference, HashSet<string> active, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reference is not JsonArray { Count: 2 } tuple) throw Invalid();
            var kind = tuple[0]?.GetValue<int>();
            if (kind == 0) return tuple[1]?.DeepClone();
            if (kind != 1 || tuple[1] is not JsonValue) throw Invalid();
            var hash = tuple[1]!.GetValue<string>();
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || !active.Add(hash)) throw Invalid();
            try
            {
                if (_loaded.TryGetValue(hash, out var cached)) return cached.DeepClone();
                var record = await records.GetAsync(Blocks, tenant, _prefix + hash, Author, ct) ?? throw Invalid();
                var envelope = JsonNode.Parse(record.Value) as JsonObject ?? throw Invalid();
                if (envelope["tenantId"]?.ToString() != tenant || envelope["runId"]?.ToString() != runId ||
                    envelope["body"] is not JsonObject block || Hash(block.ToJsonString()) != hash) throw Invalid();
                JsonNode result;
                switch (block["kind"]?.ToString())
                {
                    case "value": result = block["value"]?.DeepClone() ?? throw Invalid(); break;
                    case "object":
                        var obj = new JsonObject();
                        foreach (var field in block["values"]!.AsArray())
                        {
                            var pair = field as JsonArray ?? throw Invalid();
                            if (pair.Count != 2 || pair[0] is null || obj.ContainsKey(pair[0]!.GetValue<string>())) throw Invalid();
                            obj.Add(pair[0]!.GetValue<string>(), await DecodeAsync(pair[1], active, ct));
                        }
                        result = obj; break;
                    case "array": case "chunks":
                        var array = new JsonArray();
                        foreach (var item in block["values"]!.AsArray())
                        {
                            var decoded = await DecodeAsync(item, active, ct);
                            if (block["kind"]!.ToString() == "chunks")
                            {
                                if (decoded is not JsonArray chunk) throw Invalid();
                                foreach (var child in chunk) array.Add(child?.DeepClone());
                            }
                            else array.Add(decoded);
                        }
                        result = array; break;
                    default: throw Invalid();
                }
                _loaded[hash] = result; _written.Add(hash);
                return result.DeepClone();
            }
            finally { active.Remove(hash); }
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        private static WorkflowRunConflictException Invalid() => new("The encrypted execution checkpoint or a referenced block is invalid or missing. Stop and inspect the durable run.");
    }
}
