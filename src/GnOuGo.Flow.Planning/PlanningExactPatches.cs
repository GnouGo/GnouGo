using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>One exact-field patch transport for staged executable and behavior assignments.</summary>
internal static class PlanningExactPatches
{
    internal sealed record Target(string Id, string Path, JsonObject Schema, bool Add = false, bool Remove = false, string? Destination = null);

    // Only this reference-keyed domain crosses the model boundary. The patch
    // array below is coordinator-owned mutation IR, never a response contract.
    internal static async Task<JsonObject> ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, string phase, string owner,
        string gate, IReadOnlyList<Target> targets, JsonObject source, JsonObject context, string evidence, CancellationToken ct)
    {
        var decisions = targets.Select(t =>
        {
            var schema = t.Schema.DeepClone().AsObject();
            if (source["$defs"] is { } definitions) schema["$defs"] = definitions.DeepClone();
            PlanningHoleRequests.PruneDefinitions(schema);
            var scoped = context.DeepClone().AsObject();
            if (scoped["fields"] is JsonObject fields)
                scoped["fields"] = new JsonObject(fields.Where(p => p.Key == t.Id).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
            var hole = state.Construction.Holes.FirstOrDefault(h => !h.Superseded && h.WorkflowKey == owner &&
                (t.Path == h.Path || t.Path.StartsWith(h.Path + "/", StringComparison.Ordinal) || t.Path.StartsWith("/assignments/" + h.Id + "/", StringComparison.Ordinal)));
            var governingEvidence = hole is null ? evidence : PlanningGraphCompiler.Fingerprint(state.ApprovedBehaviorHash + ":" + PlanningContext.Contracts(state) + ":" + hole.CanonicalLocation);
            return new PlanningDecisionPages.Decision(t.Id, PlanningDecisionPages.BoundDomain(schema), new JsonObject
            {
                ["field"] = t.Path, ["context"] = scoped,
                ["operation"] = t.Remove ? "remove" : t.Destination is not null ? "move" : t.Add ? "insert" : "replace"
            }, governingEvidence, hole?.Id, hole?.Id);
        }).ToArray();
        var assignments = await PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, phase, owner, gate, decisions, ct);
        return new JsonObject { ["patches"] = new JsonArray(assignments.Select(p => (JsonNode?)new JsonObject
        { ["target"] = p.Key, ["value"] = p.Value?.DeepClone() }).ToArray()) };
    }
    internal static List<Target> Scope(JsonObject payload, JsonObject schema, IEnumerable<PlanningDiagnostic> findings)
    {
        var targets = new List<Target>();
        foreach (var diagnostic in findings.Where(d => d.Required && d.Location.StartsWith('/')))
        {
            JsonNode? current;
            try { current = PlanningFieldPaths.Read(payload, diagnostic.Location); }
            catch (InvalidOperationException) { continue; }
            foreach (var path in Leaves(current, diagnostic.Location, diagnostic.Location.Split('/').Contains("json", StringComparer.Ordinal)))
            {
                try
                {
                    var fieldSchema = SchemaAt(schema, payload, path);
                    var parent = PlanningFieldPaths.Read(payload, path[..path.LastIndexOf('/')]);
                    var token = path[(path.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    var add = parent is JsonObject obj && !obj.ContainsKey(token);
                    targets.Add(new("f_" + PlanningGraphCompiler.Fingerprint(PlanningFieldPaths.Canonical(payload, path))[..16], path, fieldSchema, add));
                }
                catch (InvalidOperationException) { /* A finding cannot authorize an unknown field. */ }
            }
        }
        return targets.DistinctBy(t => t.Path).OrderBy(t => t.Path, StringComparer.Ordinal).ToList();
    }
    private static IEnumerable<string> Leaves(JsonNode? value, string path, bool rawJson = false)
    {
        if (value is JsonObject obj)
        {
            if (!rawJson && obj["kind"]?.ToString() == "literal")
            {
                var field = obj.ContainsKey("json") ? "json" : "value";
                foreach (var leaf in Leaves(obj[field], path + "/" + field, field == "json")) yield return leaf;
                yield break;
            }
            if (!rawJson && obj["kind"]?.ToString() is "null" or "string" or "number" or "boolean" or "array" or "object") { yield return path; yield break; }
            if (!rawJson && obj["kind"]?.ToString() == "compute")
            { yield return path + "/expression"; yield return path + "/bindings"; yield break; }
            foreach (var (name, child) in obj)
                foreach (var field in Leaves(child, path + "/" + PlanningFieldPaths.Escape(name), rawJson)) yield return field;
        }
        else if (value is JsonArray array && array.Count > 0)
            for (var i = 0; i < array.Count; i++) foreach (var field in Leaves(array[i], path + "/" + i, rawJson)) yield return field;
        else yield return path;
    }
    internal static JsonObject Schema(IReadOnlyList<Target> targets, JsonObject source)
    {
        if (targets.Count == 0) throw new InvalidOperationException("No exact editable fields were located.");
        var definitions = source["$defs"]?.DeepClone().AsObject() ?? new JsonObject();
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in targets.GroupBy(t => t.Schema.ToJsonString(), StringComparer.Ordinal).Where(g => g.Count() > 1 && g.Key.Length >= 96))
        {
            var index = shared.Count;
            string name;
            do { name = "patchField" + index++.ToString(System.Globalization.CultureInfo.InvariantCulture); } while (definitions.ContainsKey(name));
            definitions[name] = group.First().Schema.DeepClone(); shared[group.Key] = name;
        }
        var schema = PlanningHoleRequests.Object(("patches", new JsonObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = targets.Count,
            ["items"] = new JsonObject { ["anyOf"] = new JsonArray(targets.Select(t => (JsonNode?)PlanningHoleRequests.Object(
                ("target", PlanningHoleRequests.Enum(t.Id)), ("value", shared.TryGetValue(t.Schema.ToJsonString(), out var name)
                    ? new JsonObject { ["$ref"] = "#/$defs/" + name } : t.Schema.DeepClone().AsObject()))).ToArray()) }
        }));
        if (definitions.Count > 0) schema["$defs"] = definitions;
        PlanningHoleRequests.PruneDefinitions(schema); return schema;
    }
    internal static JsonObject Apply(JsonObject payload, JsonObject patches, IReadOnlyList<Target> targets, JsonObject schema)
    {
        var errors = PlanningContractValidation.ValidateInstance(patches, schema);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid exact-field patch response: " + string.Join("; ", errors));
        var candidate = payload.DeepClone().AsObject(); var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in patches["patches"]!.AsArray().OrderByDescending(p => targets.Single(t => t.Id == p!["target"]!.GetValue<string>()).Path, CoordinateOrder))
        {
            var target = targets.Single(t => t.Id == patch!["target"]!.GetValue<string>());
            if (!changed.Add(target.Path) || changed.Any(p => p != target.Path && (p.StartsWith(target.Path + "/", StringComparison.Ordinal) || target.Path.StartsWith(p + "/", StringComparison.Ordinal))))
                throw new InvalidOperationException("Duplicate or overlapping field patches are forbidden.");
            if (target.Remove || target.Destination is not null)
            {
                var split = target.Path.LastIndexOf('/');
                var parent = PlanningFieldPaths.Read(candidate, target.Path[..split])!.AsArray();
                var index = int.Parse(target.Path[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                var moving = parent[index]?.DeepClone(); parent.RemoveAt(index);
                if (target.Destination is { } destination)
                {
                    if (!changed.Add(destination) || changed.Any(p => p != destination && (p.StartsWith(destination + "/", StringComparison.Ordinal) || destination.StartsWith(p + "/", StringComparison.Ordinal))))
                        throw new InvalidOperationException("Overlapping move targets are forbidden.");
                    var end = destination.LastIndexOf('/'); var array = PlanningFieldPaths.Read(candidate, destination[..end])!.AsArray();
                    var offset = int.Parse(destination[(end + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                    array.Insert(offset, moving);
                }
            }
            else if (target.Add)
            {
                var split = target.Path.LastIndexOf('/'); var parent = PlanningFieldPaths.Read(candidate, target.Path[..split])!;
                var token = target.Path[(split + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (parent is JsonArray array)
                    array.Insert(int.Parse(token, System.Globalization.CultureInfo.InvariantCulture), patch!["value"]?.DeepClone());
                else
                {
                    if (parent.AsObject().ContainsKey(token)) throw new PlanningConflictException("The insertion field already exists.");
                    parent[token] = patch!["value"]?.DeepClone();
                }
            }
            else PlanningFieldPaths.Replace(candidate, target.Path, patch!["value"]);
        }
        return candidate;
    }
    private static readonly IComparer<string> CoordinateOrder = Comparer<string>.Create((left, right) =>
    {
        var a = left.Split('/'); var b = right.Split('/');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var comparison = int.TryParse(a[i], out var x) && int.TryParse(b[i], out var y) ? x.CompareTo(y) : StringComparer.Ordinal.Compare(a[i], b[i]);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    });

    internal static JsonObject SchemaAt(JsonObject root, JsonObject payload, string path)
    {
        JsonObject schema = root; JsonNode? value = payload;
        foreach (var escaped in path.Split('/').Skip(1))
        {
            var token = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            schema = Resolve(schema, value, token);
            if (schema["properties"] is JsonObject properties && properties[token] is JsonObject field)
            { schema = field; value = value is JsonObject obj ? obj[token] : null; }
            else if (schema["prefixItems"] is JsonArray prefix && int.TryParse(token, out var offset) && offset >= 0 && offset < prefix.Count && prefix[offset] is JsonObject tuple)
            { schema = tuple; value = value is JsonArray tupleValues && offset < tupleValues.Count ? tupleValues[offset] : null; }
            else if (schema["items"] is JsonObject items && int.TryParse(token, out var index))
            { schema = items; value = value is JsonArray array && index >= 0 && index < array.Count ? array[index] : null; }
            else throw new InvalidOperationException("The response schema has no exact field at this pointer.");
        }
        return schema.DeepClone().AsObject();
        JsonObject Resolve(JsonObject current, JsonNode? instance, string token)
        {
            for (var depth = 0; depth < 64; depth++)
            {
                if (current["$ref"] is JsonValue reference)
                { current = PlanningFieldPaths.Read(root, reference.GetValue<string>()[1..])?.AsObject() ?? throw new InvalidOperationException("Unknown schema reference."); continue; }
                if (current["anyOf"] is JsonArray variants)
                {
                    var objects = variants.OfType<JsonObject>().ToArray();
                    current = objects.FirstOrDefault(v => v["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(k => k?.ToString() == (instance as JsonObject)?["kind"]?.ToString()))
                        ?? objects.FirstOrDefault(v => v["properties"]?[token] is not null) ?? objects.FirstOrDefault() ?? throw new InvalidOperationException("Empty schema union.");
                    continue;
                }
                return current;
            }
            throw new InvalidOperationException("Unbounded response schema reference.");
        }
    }
}
