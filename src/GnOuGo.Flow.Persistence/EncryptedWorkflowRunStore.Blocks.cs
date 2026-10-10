using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
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
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> _loaded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _written = new(StringComparer.Ordinal);
        private readonly Dictionary<(WorkflowInvocation Invocation, bool After), JsonNode> _snapshots = new();
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

        public async Task<WorkflowRun> ReadAsync(CancellationToken ct, bool deferred = true)
        {
            var header = await DecodeAsync(Head["header"], ct) as JsonObject ?? throw Invalid();
            var result = WorkflowRunStorage.Read(header.ToJsonString(), tenant, runId);
            foreach (var pair in Head["invocations"]!.AsObject())
                result.Invocations.AddOrUpdate(pair.Key, await ReadInvocationAsync(pair.Value, ct, deferred), (_, _) => throw Invalid());
            foreach (var e in await EventsAsync(ct))
                result.Events.Add(new(e!["timestamp"]!.GetValue<DateTimeOffset>(), e["kind"]!.GetValue<string>(), e["invocationId"]?.GetValue<string>()));
            if (result.Revision != Revision || result.CancelRequested != CancelRequested) throw Invalid();
            foreach (var pair in result.Invocations) if (pair.Key != pair.Value.Id) throw Invalid();
            if (result.Events.Count != Head["eventCount"]?.GetValue<int>()) throw Invalid();
            return result;
        }

        private async Task<WorkflowInvocation> ReadInvocationAsync(JsonNode? reference, CancellationToken ct, bool deferred = true)
        {
            await LoadAsync(reference, new(StringComparer.Ordinal), ct);
            var fields = ObjectFields(reference);
            var metadata = new JsonObject();
            foreach (var (key, value) in fields)
                metadata[key] = key is "dataBefore" or "dataAfter" ? key == "dataBefore" ? new JsonObject() : null : Decode(value);
            var invocation = metadata.Deserialize(WorkflowRunJsonContext.Default.WorkflowInvocation) ?? throw Invalid();
            foreach (var after in new[] { false, true })
            {
                var key = after ? "dataAfter" : "dataBefore";
                if (!fields.TryGetValue(key, out var value)) throw Invalid();
                Defer(invocation, after, value);
            }
            if (!deferred) { _ = invocation.DataBefore; _ = invocation.DataAfter; }
            return invocation;
        }

        private void Defer(WorkflowInvocation invocation, bool after, JsonNode reference)
        {
            if (!after || !IsNull(reference)) _ = ObjectFields(reference);
            _snapshots[(invocation, after)] = reference;
            invocation.DeferSnapshot(after, () => Decode(reference) switch
            { JsonObject obj => obj, null when after => null, _ => throw Invalid() });
        }

        public async Task CaptureAsync(WorkflowInvocation invocation, JsonObject data, bool after, CancellationToken ct)
        {
            var reference = await EncodeAsync(data, ct, force: true);
            Defer(invocation, after, reference);
        }

        public async Task RestoreAsync(WorkflowInvocation invocation, JsonObject data, bool after, CancellationToken ct)
        {
            var reference = await SnapshotAsync(invocation, after, ct);
            if (IsNull(reference)) reference = await SnapshotAsync(invocation, false, ct);
            RestoreObject(data, reference, ct);
        }

        private async Task<JsonNode> SnapshotAsync(WorkflowInvocation invocation, bool after, CancellationToken ct)
        {
            if (!invocation.TryGetMaterializedSnapshot(after, out var value))
                return _snapshots.TryGetValue((invocation, after), out var reference) ? reference.DeepClone() : throw Invalid();
            // Public getters permit arbitrary edits. Never trust mutable node identity.
            return await EncodeAsync(value, ct);
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
                        var invocation = await ReadInvocationAsync(pair.Value, ct);
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
                Inputs = null, Limits = run.Limits, ModelBudget = run.ModelBudget, ModelUsage = run.ModelUsage,
                StepsStarted = run.StepsStarted, FinalizationStepsStarted = run.FinalizationStepsStarted,
                Status = run.Status, CancelRequested = run.CancelRequested, FinalizationStarted = run.FinalizationStarted,
                FinalizationCompleted = run.FinalizationCompleted, Result = null, CreatedAt = run.CreatedAt, UpdatedAt = run.UpdatedAt
            };
            var headerNode = JsonSerializer.SerializeToNode(header, WorkflowRunJsonContext.Default.WorkflowRun)!.AsObject();
            var headerFields = new JsonArray();
            foreach (var pair in headerNode)
                headerFields.Add((JsonNode)new JsonArray(pair.Key, pair.Key switch
                {
                    "inputs" => await EncodeAsync(run.Inputs, ct),
                    "result" => await EncodeResultAsync(run.Result, ct),
                    _ => await EncodeAsync(pair.Value, ct)
                }));
            next["header"] = await WriteBlockAsync(new() { ["kind"] = "object", ["values"] = headerFields }, ct);
            var invocations = changed is null ? new JsonObject() : next["invocations"]!.AsObject();
            IEnumerable<string> ids = changed is null ? run.Invocations.Keys : changed;
            foreach (var id in ids)
            {
                if (!run.Invocations.TryGetValue(id, out var invocation)) throw Invalid();
                invocations[id] = await EncodeInvocationAsync(invocation, ct);
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
            // Cache verified immutable block definitions, never expanded snapshots.
        }

        private async Task<JsonNode> EncodeInvocationAsync(WorkflowInvocation invocation, CancellationToken ct)
        {
            var metadata = new WorkflowInvocation
            {
                Id = invocation.Id, ParentInvocationId = invocation.ParentInvocationId, StepType = invocation.StepType,
                Description = invocation.Description, Recovery = invocation.Recovery, IsFinalization = invocation.IsFinalization,
                Status = invocation.Status, ExternalCompletionObserved = invocation.ExternalCompletionObserved,
                PreparedAt = invocation.PreparedAt, DispatchedAt = invocation.DispatchedAt, CompletedAt = invocation.CompletedAt
            };
            var fields = new JsonArray();
            foreach (var pair in JsonSerializer.SerializeToNode(metadata, WorkflowRunJsonContext.Default.WorkflowInvocation)!.AsObject())
                fields.Add((JsonNode)new JsonArray(pair.Key, pair.Key switch
                {
                    "dataBefore" => await SnapshotAsync(invocation, false, ct),
                    "dataAfter" => await SnapshotAsync(invocation, true, ct),
                    "resolvedInput" => await EncodeAsync(invocation.ResolvedInput, ct),
                    "output" => await EncodeAsync(invocation.Output, ct),
                    "observation" => await EncodeAsync(invocation.Observation, ct),
                    "control" => await EncodeFieldsAsync(invocation.Control, ct),
                    "error" => await EncodeErrorAsync(invocation.Error, ct),
                    _ => await EncodeAsync(pair.Value, ct)
                }));
            return await WriteBlockAsync(new() { ["kind"] = "object", ["values"] = fields }, ct);
        }

        private async Task<JsonNode> EncodeResultAsync(RunResult? result, CancellationToken ct)
        {
            if (result is null) return new JsonArray(0, null);
            var steps = new JsonArray();
            foreach (var step in result.StepResults)
                steps.Add(await EncodeFieldsAsync(new Dictionary<string, JsonNode?>
                {
                    ["stepId"] = JsonValue.Create(step.StepId), ["stepType"] = JsonValue.Create(step.StepType),
                    ["status"] = JsonValue.Create((int)step.Status), ["output"] = step.Output,
                    ["duration"] = JsonValue.Create(step.Duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture))
                }, ct, "error", await EncodeErrorAsync(step.Error, ct)));
            return await WriteBlockAsync(new() { ["kind"] = "object", ["values"] = new JsonArray(
                new JsonArray("success", new JsonArray(0, result.Success)),
                new JsonArray("outputs", await EncodeAsync(result.Outputs, ct)),
                new JsonArray("stepResults", await WriteBlockAsync(new() { ["kind"] = "array", ["values"] = steps }, ct)),
                new JsonArray("error", await EncodeErrorAsync(result.Error, ct))) }, ct);
        }

        private Task<JsonNode> EncodeErrorAsync(WorkflowError? error, CancellationToken ct) => error is null
            ? Task.FromResult<JsonNode>(new JsonArray(0, null))
            : EncodeFieldsAsync(new Dictionary<string, JsonNode?>
            { ["code"] = JsonValue.Create(error.Code), ["type"] = JsonValue.Create(error.Type), ["message"] = JsonValue.Create(error.Message),
              ["retryable"] = JsonValue.Create(error.Retryable), ["details"] = error.Details }, ct);

        private async Task<JsonNode> EncodeFieldsAsync(IEnumerable<KeyValuePair<string, JsonNode?>> source, CancellationToken ct,
            string? extraName = null, JsonNode? extraReference = null)
        {
            var fields = new JsonArray();
            foreach (var pair in source) fields.Add((JsonNode)new JsonArray(pair.Key, await EncodeAsync(pair.Value, ct)));
            if (extraName is not null) fields.Add((JsonNode)new JsonArray(extraName, extraReference));
            return await WriteBlockAsync(new() { ["kind"] = "object", ["values"] = fields }, ct);
        }

        // Bounded traversal: never serialize a container to decide whether to split it.
        private static bool FitsInline(JsonNode? value, ref int remaining)
        {
            if (remaining < 0) return false;
            switch (value)
            {
                case JsonObject obj:
                    remaining -= 2;
                    foreach (var pair in obj)
                    {
                        if (pair.Key.Length > remaining) return false;
                        remaining -= JsonEncodedText.Encode(pair.Key).EncodedUtf8Bytes.Length + 4;
                        if (!FitsInline(pair.Value, ref remaining)) return false;
                    }
                    break;
                case JsonArray array:
                    remaining -= 2;
                    foreach (var item in array) { remaining--; if (!FitsInline(item, ref remaining)) return false; }
                    break;
                default:
                    if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > remaining) return false;
                    remaining -= Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null");
                    break;
            }
            return remaining >= 0;
        }

        private async Task<JsonNode> EncodeAsync(JsonNode? value, CancellationToken ct, bool force = false)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = InlineBytes;
            if (!force && FitsInline(value, ref remaining)) return new JsonArray(0, value?.DeepClone());
            if (value is JsonObject obj) return await EncodeFieldsAsync(obj, ct);
            if (value is JsonArray array) return await EncodeArrayAsync(array, 0, array.Count, ct);
            return await WriteBlockAsync(new() { ["kind"] = "value", ["value"] = value?.DeepClone() }, ct);
        }

        private async Task<JsonNode> EncodeArrayAsync(JsonArray array, int start, int count, CancellationToken ct)
        {
            var chunks = count > ChunkSize;
            var items = new JsonArray();
            if (chunks)
                for (var i = start; i < start + count; i += ChunkSize)
                    items.Add(await EncodeArrayAsync(array, i, Math.Min(ChunkSize, start + count - i), ct));
            else
                for (var i = start; i < start + count; i++) items.Add(await EncodeAsync(array[i], ct));
            return await WriteBlockAsync(new() { ["kind"] = chunks ? "chunks" : "array", ["values"] = items }, ct);
        }

        private async Task<JsonNode> WriteBlockAsync(JsonObject block, CancellationToken ct)
        {
            // Normalize typed scalar writers before canonical hashing. Only one block is serialized.
            block = JsonNode.Parse(block.ToJsonString())!.AsObject();
            var body = block.ToJsonString(); var hash = Hash(body);
            if (!_written.Contains(hash))
            {
                var envelope = new JsonObject { ["tenantId"] = tenant, ["runId"] = runId, ["body"] = block.DeepClone() };
                await records.UpsertAsync(Blocks, tenant, _prefix + hash, envelope.ToJsonString(), Author, ct);
                _written.Add(hash);
            }
            _loaded.TryAdd(hash, block);
            return new JsonArray(1, hash);
        }

        private async Task<JsonNode?> DecodeAsync(JsonNode? reference, CancellationToken ct)
        {
            await LoadAsync(reference, new(StringComparer.Ordinal), ct);
            return Decode(reference!);
        }

        private async Task LoadAsync(JsonNode? reference, HashSet<string> active, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reference is not JsonArray { Count: 2 } tuple) throw Invalid();
            var kind = tuple[0]?.GetValue<int>();
            if (kind == 0) return;
            if (kind != 1 || tuple[1] is not JsonValue) throw Invalid();
            var hash = tuple[1]!.GetValue<string>();
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || !active.Add(hash)) throw Invalid();
            try
            {
                if (_loaded.ContainsKey(hash)) return;
                var record = await records.GetAsync(Blocks, tenant, _prefix + hash, Author, ct) ?? throw Invalid();
                var envelope = JsonNode.Parse(record.Value) as JsonObject ?? throw Invalid();
                if (envelope["tenantId"]?.ToString() != tenant || envelope["runId"]?.ToString() != runId ||
                    envelope["body"] is not JsonObject block || Hash(block.ToJsonString()) != hash) throw Invalid();
                switch (block["kind"]?.ToString())
                {
                    case "object":
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var field in block["values"]!.AsArray())
                        {
                            if (field is not JsonArray { Count: 2 } pair || pair[0] is null || !names.Add(pair[0]!.GetValue<string>())) throw Invalid();
                            await LoadAsync(pair[1], active, ct);
                        }
                        break;
                    case "array": case "chunks":
                        foreach (var item in block["values"]!.AsArray()) await LoadAsync(item, active, ct);
                        break;
                    case "value": break;
                    default: throw Invalid();
                }
                _loaded.TryAdd(hash, block); _written.Add(hash);
            }
            finally { active.Remove(hash); }
        }

        private JsonObject Block(JsonNode reference) => _loaded[reference[1]!.GetValue<string>()];
        private bool IsNull(JsonNode reference) => reference[0]!.GetValue<int>() == 0 ? reference[1] is null
            : Block(reference)["kind"]?.ToString() == "value" && Block(reference)["value"] is null;

        private Dictionary<string, JsonNode> ObjectFields(JsonNode? reference)
        {
            if (reference is null) throw Invalid();
            if (reference[0]!.GetValue<int>() == 0)
                return (reference[1] as JsonObject ?? throw Invalid()).ToDictionary(p => p.Key, p => (JsonNode)new JsonArray(0, p.Value?.DeepClone()), StringComparer.Ordinal);
            var block = Block(reference);
            if (block["kind"]?.ToString() != "object") throw Invalid();
            return block["values"]!.AsArray().ToDictionary(v => v![0]!.GetValue<string>(), v => v![1]!, StringComparer.Ordinal);
        }

        private IEnumerable<JsonNode> ArrayItems(JsonNode reference)
        {
            var block = Block(reference);
            if (block["kind"]?.ToString() == "array")
                foreach (var item in block["values"]!.AsArray()) yield return item!;
            else if (block["kind"]?.ToString() == "chunks")
                foreach (var chunk in block["values"]!.AsArray())
                {
                    if (chunk![0]!.GetValue<int>() == 0)
                        foreach (var item in chunk[1] as JsonArray ?? throw Invalid()) yield return new JsonArray(0, item?.DeepClone());
                    else foreach (var item in ArrayItems(chunk)) yield return item;
                }
            else throw Invalid();
        }

        private JsonNode? Decode(JsonNode reference)
        {
            if (reference[0]!.GetValue<int>() == 0) return reference[1]?.DeepClone();
            var block = Block(reference);
            return block["kind"]?.ToString() switch
            {
                "value" => block["value"]?.DeepClone(),
                "object" => new JsonObject(ObjectFields(reference).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Decode(p.Value)))),
                "array" or "chunks" => new JsonArray(ArrayItems(reference).Select(Decode).ToArray()),
                _ => throw Invalid()
            };
        }

        private void RestoreObject(JsonObject target, JsonNode reference, CancellationToken ct)
        {
            var fields = ObjectFields(reference);
            foreach (var key in target.Select(p => p.Key).Where(k => !fields.ContainsKey(k)).ToArray()) target.Remove(key);
            foreach (var pair in fields)
            {
                var current = target[pair.Key]; var value = RestoreValue(current, pair.Value, ct);
                if (!target.ContainsKey(pair.Key) || !ReferenceEquals(current, value)) target[pair.Key] = value;
            }
        }

        private JsonNode? RestoreValue(JsonNode? current, JsonNode reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var block = reference[0]!.GetValue<int>() == 1 ? Block(reference) : null;
            if (current is JsonObject obj && block?["kind"]?.ToString() == "object")
            { RestoreObject(obj, reference, ct); return obj; }
            if (current is JsonArray array && block?["kind"]?.ToString() is "array" or "chunks")
            {
                var index = 0;
                foreach (var item in ArrayItems(reference))
                {
                    if (index == array.Count) array.Add(Decode(item));
                    else { var value = RestoreValue(array[index], item, ct); if (!ReferenceEquals(value, array[index])) array[index] = value; }
                    index++;
                }
                while (array.Count > index) array.RemoveAt(array.Count - 1);
                return array;
            }
            var desired = reference[0]!.GetValue<int>() == 0 ? reference[1] : block?["kind"]?.ToString() == "value" ? block["value"] : null;
            // Bound comparisons too: a changed large container must not be serialized
            // just because the frozen value is inline. Preserve numeric token spelling.
            var remaining = InlineBytes;
            if ((block is null || block["kind"]?.ToString() == "value") &&
                (current is null or JsonValue || FitsInline(current, ref remaining)) &&
                string.Equals(current?.ToJsonString(), desired?.ToJsonString(), StringComparison.Ordinal)) return current;
            return Decode(reference);
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        private static WorkflowRunConflictException Invalid() => new("The encrypted execution checkpoint or a referenced block is invalid or missing. Stop and inspect the durable run.");
    }
}
