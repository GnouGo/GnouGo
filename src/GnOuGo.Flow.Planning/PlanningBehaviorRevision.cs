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
        var context = new JsonObject(catalog.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
        { ["path"] = p.Value.Path, ["operation"] = p.Value.Operation })));
        var prompt = "Locate only fields affected by the human's requested behavior revision. Select necessary companion changes together. " +
            "Cite an exact nonempty excerpt from the revision for every selected field. Unrelated behavior and identities remain locked.\nRevision:\n" + revision.Text +
            "\nRetained behavior:\n" + candidate.ToJsonString() + "\nCoordinates:\n" + context.ToJsonString();
        var response = await PlanningModelCalls.StructuredAsync(state, runtime, "behavior_revision_scope", prompt, schema, ct);
        var fields = new List<PlanningBehaviorRevisionField>();
        foreach (var selected in response["fields"]!.AsArray())
        {
            var target = catalog[selected!["target"]!.ToString()]; var evidence = selected["evidence"]!.ToString();
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
    private static Dictionary<string, Target> Catalog(JsonObject candidate)
    {
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        void Add(string path, string operation) => targets.Add("r_" + PlanningGraphCompiler.Fingerprint(operation + PlanningFieldPaths.Canonical(candidate, path))[..16], new(path, operation));
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
