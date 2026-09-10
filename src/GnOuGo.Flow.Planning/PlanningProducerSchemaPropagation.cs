using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Stage an exact pure-producer schema fragment when a consumer reveals an open result contract.</summary>
internal static class PlanningProducerSchemaPropagation
{
    internal static bool Resolve(PlanningSnapshot state, PlanningStagedAssignments staged, out PlanningGraph? graph)
    {
        graph = null;
        var findings = staged.Diagnostics.Where(d => d.Code == "PRODUCER_CONTRACT_INCOMPLETE").ToArray();
        if (findings.Length == 0) return false;
        var targets = PlanningExactPatches.Scope(staged.Payload, staged.ResponseSchema, findings);
        var patches = new JsonArray();
        foreach (var target in targets)
        {
            var hole = staged.Targets.SingleOrDefault(h => target.Path == "/assignments/" + h.Id + "/properties");
            if (hole?.ExpectedSchema is not { } expected || !PlanningSchemaPropagation.Established(expected)) return false;
            // A selected direct output binding constrains its pure producer. These are
            // already declared member contracts, so no model inference is necessary.
            var declared = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(PlanningGraphImporter.Schema(expected), PlanningJsonContext.Default.PlanningSchema))!;
            patches.Add((JsonNode)new JsonObject { ["target"] = target.Id, ["value"] = declared["properties"]!.DeepClone() });
        }
        if (patches.Count == 0) return false;
        var previous = staged.Payload;
        staged.Payload = PlanningExactPatches.Apply(previous, new JsonObject { ["patches"] = patches }, targets, PlanningExactPatches.Schema(targets, staged.ResponseSchema));
        var evaluated = PlanningHoleAssignments.Evaluate(state, staged);
        if (evaluated.Diagnostics.Count == 0)
        { graph = evaluated.Graph; staged.Diagnostics.Clear(); return true; }
        staged.Payload = previous; return false;
    }

    internal static bool Stage(PlanningSnapshot state, PlanningStagedAssignments staged)
    {
        var workflow = state.Graph!.Workflows.Single(w => w.Key == staged.WorkflowKey);
        var root = "/workflows/" + state.Graph.Workflows.IndexOf(workflow);
        foreach (var diagnostic in staged.Diagnostics.Where(d => d.Code == "OUTPUT_TYPE_MISMATCH").ToArray())
        {
            var output = workflow.Outputs.FirstOrDefault(p => diagnostic.Location == root + "/outputs/" + workflow.Outputs.IndexOf(p) + "/schema");
            if (output is null) continue;
            var valuePath = diagnostic.Location[..^"schema".Length] + "value";
            var bindingHole = staged.Targets.SingleOrDefault(h => h.Path == valuePath);
            if (bindingHole is null || staged.Payload["assignments"]?[bindingHole.Id] is not JsonObject assignment) continue;
            var binding = PlanningHoleAssignments.Value(assignment, staged.Bindings);
            if (binding is not { Kind: "output", Source: not null, Path.Count: 0 }) continue;
            var producer = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")).SingleOrDefault(n => n.Node.Key == binding.Source);
            if (producer.Node is not { Type: "set", InternalRole: null, OutputSchema: not null } node) continue;
            if (node.CapabilityId is { } capability && state.Preparation!.Capabilities.Single(c => c.Id == capability).Resolution != "local") continue;
            var current = PlanningGraphCompiler.ToJsonSchema(node.OutputSchema, state.Preparation!);
            if (PlanningSchemaPropagation.Established(current) || current["type"]?.ToString() != "object" || output.Schema.Type != "object" ||
                current["properties"] is JsonObject { Count: > 0 }) continue;
            var path = producer.Path + "/outputSchema";
            var hole = staged.Targets.SingleOrDefault(h => h.Path == path);
            if (hole is not null && staged.Payload["assignments"]?[hole.Id] is not null) continue;
            var canonical = PlanningFieldPaths.Canonical(PlanningFieldPaths.Json(state.Graph), path);
            hole ??= new PlanningHole { Id = "h_" + PlanningGraphCompiler.Fingerprint(canonical)[..16], WorkflowKey = workflow.Key, NodeKey = node.Key,
                Path = path, CanonicalLocation = canonical, Kind = "schema", Purpose = node.Purpose + "; establish the result required by output " + output.Name,
                ExpectedSchema = PlanningGraphCompiler.ToJsonSchema(output.Schema, state.Preparation!) };
            var transport = PlanningHoleRequests.Create(state, workflow, [hole]);
            var partial = current.DeepClone().AsObject();
            // An open native envelope is an upper bound, not an established business shape.
            // The pure producer's declared members will be validated before this refinement commits.
            if (partial["additionalProperties"]?.ToString() == "true") partial["additionalProperties"] = false;
            var inline = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(PlanningGraphImporter.Schema(partial), PlanningJsonContext.Default.PlanningSchema));
            if (!staged.Targets.Any(h => h.Id == hole.Id)) staged.Targets.Add(hole);
            if (!state.Construction.Holes.Any(h => h.Id == hole.Id)) state.Construction.Holes.Add(hole);
            staged.Payload["assignments"]![hole.Id] = inline;
            staged.ResponseSchema["properties"]!["assignments"]!["properties"]![hole.Id] = transport.Schema["properties"]!["assignments"]!["properties"]![hole.Id]!.DeepClone();
            staged.ResponseSchema["properties"]!["assignments"]!["required"]!.AsArray().Add((JsonNode)JsonValue.Create(hole.Id)!);
            staged.ResponseSchema["$defs"] ??= new JsonObject();
            foreach (var (name, schema) in transport.Schema["$defs"]!.AsObject()) staged.ResponseSchema["$defs"]![name] = schema?.DeepClone();
            staged.Diagnostics.Remove(diagnostic);
            staged.Diagnostics.Add(new("PRODUCER_CONTRACT_INCOMPLETE", "/assignments/" + hole.Id + "/properties", "Establish the producer's required result members before publishing its consumer boundary.", Rule: "output:" + output.Name));
            staged.ScopeFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(staged.Targets, PlanningJsonContext.Default.ListPlanningHole));
            var progress = state.Construction.Workflows.Single(w => w.WorkflowKey == workflow.Key);
            progress.UnresolvedHoles = state.Construction.Holes.Count(h => h.WorkflowKey == workflow.Key && !h.Resolved);
            return true;
        }
        return false;
    }
}
