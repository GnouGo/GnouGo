using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Before review, locate editable scalar fields and explicitly authorized structural coordinates.</summary>
internal static class PlanningBehaviorPatches
{
    internal static List<PlanningExactPatches.Target> Scope(JsonObject candidate, JsonObject schema, IReadOnlyList<PlanningDiagnostic> findings)
    {
        var located = findings.Where(d => d.Rule is not ("revision_remove" or "revision_add") && d.Location != "/" && d.Location != "/workflows" &&
            Read(candidate, d.Location) is not JsonObject).ToArray();
        var targets = PlanningExactPatches.Scope(candidate, schema, located)
            .Where(t => Read(candidate, t.Path) is not JsonArray &&
                // Valid identities are immutable, even before review.
                (!t.Path.EndsWith("/key", StringComparison.Ordinal) && !t.Path.EndsWith("/name", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(Read(candidate, t.Path)?.ToString())))
            .ToList();
        foreach (var diagnostic in findings.Where(d => d.Rule == "revision_remove"))
            targets.Add(new(Id(candidate, diagnostic.Location, "remove"), diagnostic.Location, new JsonObject { ["type"] = "null" }, Remove: true));
        foreach (var diagnostic in findings.Where(d => d.Rule == "revision_add"))
        {
            var split = diagnostic.Location.LastIndexOf('/');
            if (Read(candidate, diagnostic.Location[..split]) is not JsonArray collection ||
                diagnostic.Location[(split + 1)..] != collection.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
            var field = PlanningExactPatches.SchemaAt(schema, candidate, diagnostic.Location);
            targets.Add(new(Id(candidate, diagnostic.Location, "insert"), diagnostic.Location, field, Add: true));
        }
        foreach (var diagnostic in findings.Where(d => d.Rule == "behavior_08"))
            targets.Add(new(Id(candidate, diagnostic.Location, "remove"), diagnostic.Location, new JsonObject { ["type"] = "null" }, Remove: true));
        foreach (var diagnostic in located.Where(d => d.Rule == "behavior_09"))
        {
            if (Read(candidate, diagnostic.Location) is not JsonArray dependencies) continue;
            for (var index = 0; index < dependencies.Count; index++)
            {
                var workflowPath = string.Join('/', diagnostic.Location.Split('/').Take(3));
                var names = Read(candidate, workflowPath + "/inputs")!.AsArray().Select(p => p!["name"]!.ToString()).ToHashSet(StringComparer.Ordinal);
                if (names.Contains(dependencies[index]!.ToString()) && !dependencies.Take(index).Any(d => JsonNode.DeepEquals(d, dependencies[index]))) continue;
                var path = diagnostic.Location + "/" + index;
                targets.Add(new(Id(candidate, path, "remove"), path, new JsonObject { ["type"] = "null" }, Remove: true));
            }
        }
        foreach (var diagnostic in findings.Where(d => d.Rule?.StartsWith("insert_behavior_node:", StringComparison.Ordinal) == true))
        {
            var capabilityId = diagnostic.Rule!["insert_behavior_node:".Length..];
            var nodes = schema["$defs"]!["behaviorNode"]!["anyOf"]!.AsArray();
            var variant = nodes.OfType<JsonObject>().First(n => ContainsEnum(n["properties"]!["capabilityId"], capabilityId));
            var field = variant.DeepClone().AsObject();
            field["properties"]!["capabilityId"] = PlanningHoleRequests.Enum(capabilityId);
            field["properties"]!["key"] = PlanningHoleRequests.Enum("node_" + PlanningGraphCompiler.Fingerprint(diagnostic.Location + capabilityId)[..16]);
            targets.Add(new(Id(candidate, diagnostic.Location, "insert"), diagnostic.Location, field, Add: true));
        }
        foreach (var diagnostic in findings.Where(d => d.Rule == "behavior_19"))
        {
            if (Read(candidate, diagnostic.Location) is not JsonArray nodes) continue;
            var outcomePath = diagnostic.Location[..diagnostic.Location.LastIndexOf('/')];
            var outcomesPath = outcomePath[..outcomePath.LastIndexOf('/')];
            if (Read(candidate, outcomesPath) is not JsonArray outcomes) continue;
            for (var i = 0; i < nodes.Count; i++)
            for (var outcome = 0; outcome < outcomes.Count; outcome++)
            {
                if (outcomes[outcome]?["isDefault"]?.GetValue<bool>() != false) continue;
                var destination = outcomesPath + "/" + outcome + "/steps/" + outcomes[outcome]!["steps"]!.AsArray().Count;
                var path = diagnostic.Location + "/" + i;
                targets.Add(new(Id(candidate, path, "move:" + destination), path, new JsonObject { ["type"] = "null" }, Destination: destination));
            }
        }
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.Remove || !target.Path.Contains("/inputDependencies/", StringComparison.Ordinal)) continue;
            var workflowPath = string.Join('/', target.Path.Split('/').Take(3));
            var names = Read(candidate, workflowPath + "/inputs")!.AsArray().Select(p => p!["name"]!.ToString()).ToArray();
            if (names.Length > 0) targets[i] = target with { Schema = PlanningHoleRequests.Enum(names) };
        }
        // An explicitly removed element already removes its descendants. Offering
        // those descendants as companion edits can only create overlapping patches.
        return targets.Where(t => !targets.Any(parent => parent.Remove && t.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal)))
            .DistinctBy(t => t.Id).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    internal static string Owner(JsonObject candidate, IReadOnlyList<PlanningExactPatches.Target> targets)
    {
        var owners = targets.Select(t => t.Path.Split('/')).Where(p => p.Length > 2 && p[1] == "workflows")
            .Select(p => Read(candidate, "/workflows/" + p[2] + "/key")?.ToString()).Distinct(StringComparer.Ordinal).ToArray();
        return owners.Length == 1 && owners[0] is { } owner ? owner : "$plan";
    }
    private static bool ContainsEnum(JsonNode? schema, string value) => schema is JsonObject obj &&
        (obj["enum"] is JsonArray items && items.Any(v => v?.ToString() == value) || obj["anyOf"] is JsonArray variants && variants.Any(v => ContainsEnum(v, value)));
    private static string Id(JsonObject candidate, string path, string operation) => "b_" + PlanningGraphCompiler.Fingerprint(operation + PlanningFieldPaths.Canonical(candidate, path))[..16];
    private static JsonNode? Read(JsonObject candidate, string path)
    { try { return PlanningFieldPaths.Read(candidate, path); } catch (InvalidOperationException) { return null; } }
}
