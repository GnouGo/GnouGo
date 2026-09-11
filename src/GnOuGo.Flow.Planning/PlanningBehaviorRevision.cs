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
        var schema = PlanningHoleRequests.Object(("fields", new JsonObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = catalog.Count,
            ["items"] = PlanningHoleRequests.Object(("target", PlanningHoleRequests.Enum(catalog.Keys.ToArray())), ("evidence", PlanningHoleRequests.Type("string")))
        }));
        // Replay the issued domain against the retained candidate. Target identities
        // are request-local; canonical coordinates and original fingerprints remain
        // the authority for applying the eventual patches.
        schema = state.Construction.PendingCalls.SingleOrDefault(c => c.Phase == "behavior_revision_scope" && c.WorkflowKey == "")?.Request.StructuredOutputSchema as JsonObject ?? schema;
        var issued = schema["properties"]!["fields"]!["items"]!["properties"]!["target"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
        if (issued.Length != catalog.Count || issued.Distinct(StringComparer.Ordinal).Count() != issued.Length)
            throw new WorkflowRuntimeException("BEHAVIOR_REVISION_SCOPE_INVALID", "The issued revision scope no longer matches the retained candidate.");
        var targets = issued.Zip(catalog.Values).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var context = Context(candidate, catalog);
        var prompt = "Locate only fields affected by the human's requested behavior revision. Select necessary companion changes together. " +
            "Select each target once, citing one exact nonempty revision excerpt. Unrelated behavior and identities remain locked.\nRevision:\n" + revision.Text +
            "\nFields are grouped by operation; target IDs map to [path, current scalar value or element identity]. " +
            "An anchor maps its alias to [parent-relative path, retained identity]; field paths use those aliases.\nCoordinates:\n" + PlanningPromptContext.Json(context);
        var response = await PlanningModelCalls.StructuredAsync(state, runtime, "behavior_revision_scope", prompt, schema, ct);
        var fields = new List<PlanningBehaviorRevisionField>();
        foreach (var selected in response["fields"]!.AsArray())
        {
            var target = targets[selected!["target"]!.ToString()]; var evidence = selected["evidence"]!.ToString();
            if (string.IsNullOrWhiteSpace(evidence) || !revision.Text.Contains(evidence, StringComparison.Ordinal))
                throw new WorkflowRuntimeException("BEHAVIOR_REVISION_EVIDENCE_INVALID", "The revision scope needs exact human evidence before any edit.");
            if (fields.Any(f => f.Path == target.Path)) throw new WorkflowRuntimeException("BEHAVIOR_REVISION_SCOPE_INVALID", "Duplicate revision coordinates are forbidden.");
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
