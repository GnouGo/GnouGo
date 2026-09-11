using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Before review, locate editable scalar fields and explicitly authorized structural coordinates.</summary>
internal static class PlanningBehaviorPatches
{
    internal static void RestrictCapabilities(JsonObject candidate, List<PlanningExactPatches.Target> targets, PlanningPreparation preparation)
    {
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (target.Add && target.Schema["properties"] is JsonObject inserted &&
                inserted["capabilityId"]?["enum"] is JsonArray { Count: 1 } capabilityIds &&
                preparation.Capabilities.SingleOrDefault(c => c.Id == capabilityIds[0]?.ToString()) is { } capability)
            {
                // This coordinate repairs one missing implementation. Its ownership
                // is already locked; only the business-input dependence is undecided.
                inserted["operationIds"] = new JsonObject { ["type"] = "array",
                    ["items"] = capability.OperationIds.Count == 0 ? PlanningHoleRequests.Type("string") : PlanningHoleRequests.Enum(capability.OperationIds.ToArray()),
                    ["enum"] = new JsonArray(new JsonArray(capability.OperationIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())) };
                var workflow = string.Join('/', target.Path.Split('/').Take(3));
                var inputs = (Read(candidate, workflow + "/inputs") as JsonArray ?? []).Select(p => p!["name"]!.ToString()).ToArray();
                inserted["inputDependencies"] = new JsonObject { ["type"] = "array", ["items"] = inputs.Length == 0 ? PlanningHoleRequests.Type("string") : PlanningHoleRequests.Enum(inputs), ["maxItems"] = inputs.Length };
            }
            if (!target.Path.EndsWith("/capabilityId", StringComparison.Ordinal)) continue;
            var parent = target.Path[..target.Path.LastIndexOf('/')];
            // These neighboring assignments govern eligibility. Keep their joint
            // domain until their contracts are established in a subsequent pass.
            if (targets.Any(t => t.Path == parent + "/kind" || t.Path.StartsWith(parent + "/operationIds/", StringComparison.Ordinal))) continue;
            if (Read(candidate, parent) is not JsonObject node || node["operationIds"] is not JsonArray { Count: > 0 } operations) continue;
            var owned = operations.Select(o => o!.ToString()).ToHashSet(StringComparer.Ordinal);
            var values = new JsonArray(preparation.Capabilities.Where(c => c.OperationIds.All(owned.Contains) &&
                PlanningCapabilityBindings.SupportsBehavior(c, node["kind"]!.ToString()) && PlanningContractValidation.ValidateInstance(JsonValue.Create(c.Id), target.Schema).Count == 0)
                .Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray());
            // Preserve an already permitted unbound native implementation. Missing
            // required external implementations still fail the complete behavior gate.
            if (PlanningContractValidation.ValidateInstance(null, target.Schema).Count == 0) values.Add((JsonNode?)null);
            if (values.Count == 0) throw new PlanningHoleUnavailableException(PlanningFieldPaths.Canonical(candidate, target.Path), "No declared capability satisfies this behavior field's ownership and executor contract.");
            JsonNode type = values.All(v => v is null) ? JsonValue.Create("null")! : values.Any(v => v is null) ? new JsonArray("string", "null") : JsonValue.Create("string")!;
            targets[index] = target with { Schema = new JsonObject { ["type"] = type, ["enum"] = values } };
        }
        // A missing implementation may be supplied by an existing node once its
        // invalid reference is corrected. Resolve that prerequisite before offering
        // an insertion, which could otherwise duplicate the required operation.
        var bindings = targets.Where(t => !t.Add && t.Path.EndsWith("/capabilityId", StringComparison.Ordinal)).ToArray();
        targets.RemoveAll(t => t.Add && t.Schema["properties"]?["capabilityId"]?["enum"] is JsonArray { Count: 1 } values &&
            bindings.Any(binding => PlanningContractValidation.ValidateInstance(values[0], binding.Schema).Count == 0));
    }

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
