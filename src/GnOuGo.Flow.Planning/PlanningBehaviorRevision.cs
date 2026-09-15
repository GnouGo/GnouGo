using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Flow.Planning;

/// <summary>Locate a human revision against retained identities before authorizing exact behavior patches.</summary>
internal static class PlanningBehaviorRevision
{
    internal static async Task LocateAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var revision = state.BehaviorRevision!;
        var candidate = state.BehaviorAssessment.Candidate!;
        var catalog = Catalog(candidate);
        var references = PlanningReferences.Register(state, "revision", "user_revision", revision.Text);
        var decisions = new List<PlanningDecisionPages.Decision>();
        var issued = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var (id, target) in catalog)
        foreach (var chunk in references.Chunk(8))
        {
            var decisionId = "revision_" + id + "_" + chunk[0].Id;
            issued[decisionId] = target;
            decisions.Add(new(decisionId, PlanningHoleRequests.Enum(["unchanged", .. chunk.Select(r => r.Id)]), new JsonObject
            {
                ["coordinate"] = target.Path, ["operation"] = target.Operation,
                ["current"] = PlanningFieldPaths.ReadOptional(candidate, target.Path)?.DeepClone(),
                ["revision"] = PlanningReferences.Context(chunk, new Dictionary<string, string> { ["revision"] = revision.Text }),
                ["task"] = "Select the revision evidence that requires this exact change, or unchanged. Unrelated identities and content remain locked."
            }, PlanningGraphCompiler.Fingerprint(revision.Text + ":" + PlanningFieldPaths.Canonical(candidate, target.Path))));
        }
        var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior_revision_scope", "$plan", decisions, ct);
        var fields = new List<PlanningBehaviorRevisionField>();
        foreach (var group in response.Where(p => p.Value?.ToString() != "unchanged").GroupBy(p => issued[p.Key]))
        {
            var target = group.Key;
            var evidence = string.Join(" ", group.Select(p => p.Value!.ToString()).Distinct(StringComparer.Ordinal)
                .Select(id => PlanningReferences.Resolve(state, id, new Dictionary<string, string> { ["revision"] = revision.Text })));
            fields.Add(new(target.Path, PlanningFieldPaths.Canonical(candidate, target.Path), target.Operation,
                Fingerprint(PlanningFieldPaths.ReadOptional(candidate, target.Path)), evidence));
        }
        revision.Fields = fields; revision.Located = true;
        await runtime.CheckpointAsync(state, ct);
    }

    internal static IEnumerable<PlanningDiagnostic> Findings(PlanningSnapshot state, JsonObject candidate)
    {
        if (state.BehaviorRevision is not { Located: true } revision) yield break;
        var catalog = Catalog(candidate);
        foreach (var field in revision.Fields)
        {
            var located = catalog.Values.Where(t => PlanningFieldPaths.Canonical(candidate, t.Path) == field.CanonicalLocation).ToArray();
            var target = located.FirstOrDefault(t => t.Operation == field.Operation);
            if (target is null || Fingerprint(PlanningFieldPaths.ReadOptional(candidate, target.Path)) != field.OriginalFingerprint) continue;
            yield return new("BEHAVIOR_REVISION_REQUIRED", target.Path, "Human revision: " + field.Evidence, Rule: "revision_" + field.Operation);
        }
    }

    internal static JsonObject ApplyRemovals(PlanningSnapshot state, JsonObject candidate, JsonObject schema)
    {
        var findings = Findings(state, candidate).Where(d => d.Rule == "revision_remove").ToArray();
        if (findings.Length == 0) return candidate;
        var targets = PlanningBehaviorPatches.Scope(candidate, schema, findings);
        var retired = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                if (obj["operationIds"] is JsonArray operations)
                    retired.UnionWith(operations.Select(o => o!.ToString()));
                foreach (var child in obj) Collect(child.Value);
            }
            else if (value is JsonArray children) foreach (var child in children) Collect(child);
        }
        foreach (var target in targets) Collect(PlanningFieldPaths.Read(candidate, target.Path));
        retired.ExceptWith(state.Preparation!.Capabilities.SelectMany(c => c.OperationIds));
        // Removal of an operation also retires its derived ownership entries.
        // Issue exact companion coordinates only for those retired identities;
        // unrelated invalid content must remain staged for its own diagnosis.
        void Companions(JsonNode? value, string path)
        {
            if (targets.Any(t => path == t.Path || path.StartsWith(t.Path + "/", StringComparison.Ordinal))) return;
            if (value is JsonObject obj)
                foreach (var child in obj) Companions(child.Value, path + "/" + PlanningFieldPaths.Escape(child.Key));
            else if (value is JsonArray children)
                for (var i = 0; i < children.Count; i++)
                {
                    var coordinate = path + "/" + i;
                    if (path.EndsWith("/operationIds", StringComparison.Ordinal) && retired.Contains(children[i]!.ToString()))
                        targets.Add(new("retire_" + PlanningGraphCompiler.Fingerprint(PlanningFieldPaths.Canonical(candidate, coordinate))[..16], coordinate,
                            new JsonObject { ["type"] = "null" }, Remove: true));
                    else Companions(children[i], coordinate);
                }
        }
        Companions(candidate, "");
        return PlanningExactPatches.Apply(candidate, new JsonObject { ["patches"] = new JsonArray(targets.Select(t =>
            (JsonNode?)new JsonObject { ["target"] = t.Id, ["value"] = null }).ToArray()) }, targets, PlanningExactPatches.Schema(targets, schema));
    }

    private sealed record Target(string Path, string Operation);
    internal static JsonObject Context(JsonObject candidate) => Context(candidate, Catalog(candidate));
    private static JsonObject Context(JsonObject candidate, Dictionary<string, Target> catalog)
    {
        var anchors = new JsonObject();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        string Address(string path)
        {
            var parent = paths.Keys.Where(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal)).OrderByDescending(p => p.Length).FirstOrDefault();
            return parent is null ? path : paths[parent] + path[parent.Length..];
        }
        void Visit(JsonNode? node, string path)
        {
            if (node is JsonObject obj)
            {
                if ((obj["key"] ?? obj["name"]) is { } identity)
                {
                    var alias = "a" + anchors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    anchors[alias] = new JsonArray(Address(path), identity.DeepClone()); paths[path] = alias;
                }
                foreach (var (name, value) in obj) Visit(value, path + "/" + PlanningFieldPaths.Escape(name));
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Visit(array[i], path + "/" + i);
        }
        Visit(candidate, "");
        return new()
        {
            ["anchors"] = anchors,
            ["fields"] = new JsonObject(catalog.GroupBy(p => p.Value.Operation, StringComparer.Ordinal).Select(group =>
                new KeyValuePair<string, JsonNode?>(group.Key, new JsonObject(group.Select(p =>
                {
                var value = PlanningFieldPaths.ReadOptional(candidate, p.Value.Path);
                var current = value is JsonObject obj ? obj["key"] ?? obj["name"] : value;
                return new KeyValuePair<string, JsonNode?>(p.Key, new JsonArray(Address(p.Value.Path), current?.DeepClone()));
                })))))
        };
    }
    private static Dictionary<string, Target> Catalog(JsonObject candidate)
    {
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        void Add(string path, string operation) => targets.Add("r" + targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), new(path, operation));
        void Visit(JsonNode? node, string path)
        {
            if (node is JsonObject obj)
            {
                foreach (var (name, value) in obj.Where(p => p.Key is not ("key" or "name"))) Visit(value, path + "/" + PlanningFieldPaths.Escape(name));
            }
            else if (node is JsonArray array)
            {
                if (path.Split('/')[^1] is "workflows" or "inputs" or "outputs" or "steps" or "finally" or "outcomes")
                    Add(path + "/" + array.Count, "add");
                for (var index = 0; index < array.Count; index++)
                {
                    var itemPath = path + "/" + index;
                    // Removing a single identified element never replaces its containing collection.
                    Add(itemPath, "remove"); Visit(array[index], itemPath);
                }
            }
            else Add(path, "replace");
        }
        Visit(candidate, ""); return targets;
    }
    private static string Fingerprint(JsonNode? value) => PlanningGraphCompiler.Fingerprint(value?.ToJsonString() ?? "null");
}
