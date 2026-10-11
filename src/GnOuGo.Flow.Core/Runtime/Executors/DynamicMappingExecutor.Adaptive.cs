using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class DynamicMappingExecutor
{
    private static bool ContentDependent(Acornima.Ast.Node node) => InterpretsText(node) ||
        node is Acornima.Ast.CallExpression { Callee: Acornima.Ast.MemberExpression { Object: Acornima.Ast.Identifier { Name: "m" }, Property: Acornima.Ast.Identifier { Name: "test" } } } || node.ChildNodes.Any(ContentDependent);

    private async Task<JsonNode?> ExecuteAdaptiveAsync(StepExecutionContext ctx, JsonObject sources, JsonArray items,
        string input, string output, JsonObject target, JsonObject itemTarget, JsonObject identity, JintSandbox sandbox,
        Func<int, IReadOnlyList<int>?, string?, string?, int?, Task<JsonObject>> generate, CancellationToken ct)
    {
        var tenant = ctx.Limits.TenantId;
        var store = string.IsNullOrWhiteSpace(tenant) ? null : ctx.Engine.MappingArtifacts;
        identity = identity.DeepClone().AsObject();
        identity.Remove("sources");
        identity["collection_profile"] = 3;
        identity["context"] = new JsonObject(sources.Where(p => p.Key != input).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
        var baseKey = Hash(identity, ct);
        var sourceHash = Hash(sources, ct);
        var shapes = items.Select(v => Hash(Shape(v), ct)).ToArray();
        var shapeKeys = shapes.Select(s => Hash(JsonValue.Create(baseKey + ":" + s), ct)).ToArray();
        var contentKeys = items.Select((v, i) => Hash(JsonValue.Create(shapeKeys[i] + ":" + Hash(v, ct)), ct)).ToArray();
        var results = new JsonNode?[items.Count];
        var scripts = new string?[items.Count];
        var previous = new string?[items.Count];
        var failures = new Dictionary<int, WorkflowRuntimeException>();
        var allowance = sandbox.CreateMappingAllowance();
        var unsuccessful = new HashSet<string>(StringComparer.Ordinal);
        var all = Enumerable.Range(0, items.Count).ToArray();
        var processed = new HashSet<int>();
        ctx.SetTelemetryAttribute("gnougo.mapping.model_attempts", 0);
        ctx.SetTelemetryAttribute("gnougo.mapping.specializations", 0);
        var attempts = 0;
        var repairs = 0;
        var specializations = 0;
        int[]? repairIndices = null;
        var hits = 0;
        // Pin cache assignments before doing work: a restart must not consult a changed cache.
        var pinned = ctx.ReadRecordedControl("mapping_adaptive_cache") as JsonObject;
        if (pinned is null)
        {
            var entries = new JsonObject();
            var reads = new Dictionary<string, MappingArtifact?>();
            foreach (var index in all)
            {
                foreach (var key in new[] { contentKeys[index], shapeKeys[index] })
                {
                    if (!reads.TryGetValue(key, out var artifact))
                    {
                        artifact = store is null ? ctx.Engine.MappingMemory.GetValueOrDefault(key) : await store.ReadAsync(tenant!, key, ct);
                        reads[key] = artifact;
                    }
                    if (artifact is null || artifact.Key != key || artifact.ProfileVersion != JintSandbox.MappingProfileVersion ||
                        artifact.ContentHash is not null && artifact.ContentHash != Hash(items[index], ct)) continue;
                    entries[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["key"] = key, ["script"] = artifact.Script };
                    break;
                }
            }
            pinned = (await ctx.RecordControlAsync("mapping_adaptive_cache", () => new JsonObject
            { ["source"] = sourceHash, ["binding"] = baseKey, ["entries"] = entries, ["adaptation_profile"] = 2 }, ct))!.AsObject();
        }
        if (pinned["source"]?.ToString() != sourceHash || pinned["binding"]?.ToString() != baseKey)
            throw JintSandbox.Unsatisfied("Recorded adaptive assignments do not match the approved mapping input.");
        if (pinned["adaptation_profile"]?.GetValue<int>() != 2)
            throw JintSandbox.Unsatisfied("The pending mapping uses historical adaptation assignments; explicit revision is required. Issued requests and receipts remain retained.");
        try
        {
            foreach (var group in pinned["entries"]!.AsObject().GroupBy(p => p.Value!["script"]!.GetValue<string>()))
            {
                var indices = group.Select(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
                try { Evaluate(group.Key, indices); }
                catch (WorkflowRuntimeException ex) when (JintSandbox.IsInvalidProgram(ex)) { Reject(indices, ex); }
                foreach (var index in indices)
                    if (scripts[index] is not null) hits++;
                    else
                    {
                        var key = pinned["entries"]![index.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["key"]!.GetValue<string>();
                        ctx.Engine.MappingMemory.TryRemove(key, out _);
                        if (store is not null) await store.RemoveAsync(tenant!, key, ct);
                    }
            }
            ctx.SetTelemetryAttribute("gnougo.mapping.cache_hits", hits);
            ctx.SetTelemetryAttribute("gnougo.mapping.cache_hit", hits == items.Count);
            // One generic candidate first, then only unresolved shape/cause groups, in source order.
            while (scripts.Any(s => s is null))
            {
                ct.ThrowIfCancellationRequested();
                var unresolved = all.Where(i => scripts[i] is null).ToArray();
                var groups = unresolved.GroupBy(i => shapes[i] + ":" + (failures.GetValueOrDefault(i)?.Message ?? "unmapped")).ToArray();
                ctx.SetTelemetryAttribute("gnougo.mapping.failure_groups", repairIndices is null ? groups.Length : 1);
                var phase = repairIndices is not null ? "program_repair" : attempts == 0 ? "generation" : "specialization";
                var indices = repairIndices ?? (attempts == 0 ? unresolved : groups.OrderBy(g => g.Min()).First().ToArray());
                repairIndices = null;
                var priorError = failures.GetValueOrDefault(indices[0]);
                var failed = priorError is null ? (int?)null : JintSandbox.IsInvalidProgram(priorError)
                    ? priorError.Details?["source_index"]?.GetValue<int>() : indices[0];
                var error = priorError?.Message;
                var assigned = new JsonObject { ["indices"] = new JsonArray(indices.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                    ["failure"] = error, ["source"] = sourceHash, ["phase"] = phase };
                var recorded = await ctx.RecordControlAsync("mapping_adaptive_" + attempts, () => assigned, ct);
                if (!JsonNode.DeepEquals(assigned, recorded)) throw JintSandbox.Unsatisfied("Adaptive specialization assignments changed during recovery.");
                var receipt = await generate(attempts, indices, priorError is null ? null : previous[indices[0]], error, failed);
                attempts++;
                ctx.SetTelemetryAttribute("gnougo.mapping.model_attempts", attempts);
                if (phase == "program_repair") repairs++;
                if (phase == "specialization") specializations++;
                ctx.SetTelemetryAttribute("gnougo.mapping.specializations", specializations);
                ctx.SetTelemetryAttribute("gnougo.mapping.program_repairs", repairs);
                string? script = null;
                try
                {
                    var payload = receipt["json"] as JsonObject ?? JsonNode.Parse(receipt["text"]?.GetValue<string>() ?? "null") as JsonObject;
                    script = payload?["script"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                    if (string.IsNullOrWhiteSpace(script)) throw JintSandbox.InvalidProgram("The model did not return a mapping expression.");
                    var successes = Evaluate(script, indices);
                    // A program that works nowhere provides no evidence for shape specialization.
                    if (successes == 0) repairIndices = indices;
                }
                catch (JsonException) { Reject(indices, JintSandbox.InvalidProgram("The model returned malformed mapping JSON.")); repairIndices = indices; }
                catch (WorkflowRuntimeException ex) when (ex.Code == "CONTRACT_UNSATISFIED" && ex.Details?["mapping_resource_limit"]?.GetValue<bool>() != true)
                { Reject(indices, ex); repairIndices = indices; }
                var remaining = indices.Where(i => scripts[i] is null).ToArray();
                if (remaining.Length != 0)
                {
                    foreach (var index in remaining)
                    {
                        var signature = Hash(new JsonObject { ["script"] = script, ["index"] = index, ["cause"] = failures[index].Message }, ct);
                        if (!unsuccessful.Add(signature)) throw JintSandbox.Unsatisfied("An identical mapping candidate repeated the same unresolved failures.");
                    }
                }
            }
            JsonNode complete = new JsonArray(results);
            if (output != "") complete = new JsonObject { [output] = complete };
            var errors = JsonSchemaContractValidator.ValidateInstance(complete, target);
            if (errors.Count != 0) throw JintSandbox.Unsatisfied("The complete mapped collection does not satisfy its target: " + string.Join("; ", errors));
            // No partial output or newly learned artifact is published before complete validation.
            var writes = new Dictionary<string, MappingArtifact>();
            foreach (var group in all.GroupBy(i => shapes[i]))
            {
                var generic = group.Select(i => scripts[i]).Distinct(StringComparer.Ordinal).Count() == 1;
                foreach (var index in group)
                {
                    var contentSpecific = !generic || items[index] is JsonValue scalar && scalar.TryGetValue<string>(out _) || ContentDependent(new Acornima.Parser().ParseExpression(scripts[index]!));
                    var key = contentSpecific ? contentKeys[index] : shapeKeys[index];
                    writes[key] = new(key, scripts[index]!, contentSpecific ? Hash(items[index], ct) : null, JintSandbox.MappingProfileVersion);
                }
            }
            foreach (var artifact in writes.Values)
                if (store is not null) await store.WriteAsync(tenant!, artifact, ct);
                else ctx.Engine.MappingMemory[artifact.Key] = artifact;
            ctx.SetTelemetryAttribute("gnougo.mapping.processed_items", items.Count);
            return new JsonObject { ["value"] = complete };
        }
        finally { ObserveSandbox(ctx, allowance); }

        void Reject(IEnumerable<int> indices, WorkflowRuntimeException error)
        { foreach (var index in indices) if (scripts[index] is null) failures[index] = error; }
        int Evaluate(string script, IReadOnlyList<int> indices)
        {
            foreach (var index in indices) previous[index] = script;
            JintSandbox.ValidateMapping(script);
            // Stage each candidate privately. A program defect discovered on a later
            // path invalidates that candidate, including its earlier apparent successes.
            var accepted = new Dictionary<int, JsonNode?>();
            var rejected = new Dictionary<int, WorkflowRuntimeException>();
            try
            {
                sandbox.ExecuteMappingItems(script, sources, input, itemTarget, indices, allowance,
                    (i, result) => { processed.Add(i); accepted[i] = result; },
                    (i, error) => { processed.Add(i); rejected[i] = error; }, ct);
                foreach (var (i, result) in accepted) { results[i] = result; scripts[i] = script; failures.Remove(i); }
                foreach (var (i, error) in rejected) failures[i] = error;
                return accepted.Count;
            }
            finally
            {
                ctx.SetTelemetryAttribute("gnougo.mapping.processed_items", processed.Count);
                ctx.SetTelemetryAttribute("gnougo.mapping.validated_items", scripts.Count(s => s is not null));
            }
        }
    }

    private static void ObserveSandbox(StepExecutionContext ctx, JintSandbox.MappingAllowance allowance)
    {
        if (allowance.ExhaustedResource is { } resource) ctx.SetTelemetryAttribute("gnougo.mapping.sandbox.exhausted_resource", resource);
        foreach (var (name, value) in allowance.Snapshot())
            if (value is JsonValue number)
            {
                object scalar = number.TryGetValue<long>(out var integer) ? integer : number.TryGetValue<int>(out var count) ? count : number.GetValue<double>();
                ctx.SetTelemetryAttribute("gnougo.mapping.sandbox." + name, scalar);
            }
    }
}
