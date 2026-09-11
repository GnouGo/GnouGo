using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Projects the same field domain onto exact scalar and computation-parameter repairs.</summary>
internal static class PlanningRepairDomains
{
    internal static string? ValueOwner(JsonObject graph, string path, bool computation = false)
    {
        for (var cursor = path; cursor.LastIndexOf('/') > 0; cursor = cursor[..cursor.LastIndexOf('/')])
            if (PlanningFieldPaths.ReadOptional(graph, cursor) is JsonObject value && value["kind"] is not null && (!computation || value["kind"]!.ToString() == "compute")) return cursor;
        return null;
    }
    internal static (PlanningSnapshot State, PlanningWorkflow Workflow, PlanningHole Hole) View(PlanningGraph graph, PlanningPreparation preparation, PlanningDataflowContract dataflow, string path)
    {
        var index = int.Parse(path.Split('/')[2], System.Globalization.CultureInfo.InvariantCulture);
        var workflow = graph.Workflows[index]; var root = "/workflows/" + index;
        var owner = PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally"))
            .Where(n => path.StartsWith(n.Path + "/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault().Node;
        var state = new PlanningSnapshot { Graph = PlanningContext.Clone(graph), Preparation = preparation };
        state.Construction.Dataflow = dataflow;
        PlanningGraphSkeleton.Add(state, state.Graph.Workflows[index], owner, path, "value", owner?.Purpose ?? workflow.Purpose);
        var hole = state.Construction.Holes.Single(); var json = PlanningFieldPaths.Json(state.Graph);
        PlanningFieldPaths.Replace(json, path, JsonSerializer.SerializeToNode(new PlanningValue { Kind = PlanningGraphSkeleton.Unresolved }, PlanningJsonContext.Default.PlanningValue));
        state.Graph = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningGraph)!;
        return (state, state.Graph.Workflows[index], hole);
    }
    internal static PlanningExactPatches.Target Specialize(PlanningExactPatches.Target target, PlanningGraph graph, PlanningPreparation preparation, PlanningDataflowContract dataflow,
        Dictionary<string, PlanningValue> bindings, JsonArray contexts)
    {
        var json = PlanningFieldPaths.Json(graph);
        var computation = ValueOwner(json, target.Path, true);
        if (computation is not null && target.Path.StartsWith(computation + "/members/", StringComparison.Ordinal) && target.Path.EndsWith("/value", StringComparison.Ordinal))
        {
            var view = View(graph, preparation, dataflow, computation);
            var domain = PlanningHoleEligibility.Analyze(view.State, view.Workflow, view.Hole);
            var ids = domain.Parameters.Select(b => "p_" + b.Id[2..14]).ToArray();
            if (ids.Length == 0) throw new PlanningHoleUnavailableException(view.Hole.CanonicalLocation, "No admissible computation parameter remains at this repair field.");
            foreach (var binding in domain.Parameters) bindings["p_" + binding.Id[2..14]] = binding.Value;
            contexts.Add((JsonNode)new JsonObject { ["parameters"] = new JsonObject(domain.Parameters.Select(b => new KeyValuePair<string, JsonNode?>("p_" + b.Id[2..14], b.Schema.DeepClone()))) });
            return target with { Schema = PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("binding")), ("binding", PlanningHoleRequests.Enum(ids))) };
        }
        var parent = ValueOwner(json, target.Path);
        if (parent is null || parent == target.Path) return target;
        var current = JsonSerializer.Deserialize(PlanningFieldPaths.Read(json, parent)!, PlanningJsonContext.Default.PlanningValue)!;
        var field = target.Path[(parent.Length + 1)..];
        if (current.Kind == "compute" && field == "text") return target with { Schema = new JsonObject { ["type"] = "string", ["minLength"] = 1 } };
        var scoped = View(graph, preparation, dataflow, parent);
        var eligibility = PlanningHoleEligibility.Analyze(scoped.State, scoped.Workflow, scoped.Hole);
        if (PlanningGraphValidation.IsLiteral(current) && field is "text" or "number" or "boolean" && eligibility.Contract is { } expected)
        {
            if (!eligibility.Literal) throw new PlanningHoleUnavailableException(scoped.Hole.CanonicalLocation, "A literal field cannot satisfy the outstanding dynamic obligations.");
            var contract = expected.DeepClone().AsObject();
            // The typed scalar kind is outside this repair scope: a nullable destination
            // does not authorize writing null into a retained number/string storage field.
            var types = PlanningContractCompatibility.Types(contract).Where(t => t != "null").ToArray();
            if (types.Length == 1) contract["type"] = types[0];
            if (contract["enum"] is JsonArray values)
                foreach (var value in values.Where(v => v is null).ToArray()) values.Remove(value);
            return target with { Schema = PlanningLiteralSchemas.Create(contract, PlanningFieldPaths.Canonical(json, target.Path)) };
        }
        if (field is "source" or "path" || field.StartsWith("path/", StringComparison.Ordinal))
        {
            var choices = new JsonArray();
            foreach (var binding in eligibility.Direct)
            {
                var candidate = JsonSerializer.SerializeToNode(binding.Value, PlanningJsonContext.Default.PlanningValue)!.AsObject();
                var value = PlanningFieldPaths.ReadOptional(candidate, "/" + field)?.DeepClone();
                if (value is null) continue;
                var original = JsonSerializer.SerializeToNode(current, PlanningJsonContext.Default.PlanningValue)!.AsObject();
                PlanningFieldPaths.Replace(original, "/" + field, value);
                if (JsonNode.DeepEquals(original, candidate) && !choices.Any(v => JsonNode.DeepEquals(v, value))) choices.Add(value);
            }
            if (choices.Count == 0) throw new PlanningHoleUnavailableException(scoped.Hole.CanonicalLocation, "No admissible binding can be reached through this exact repair field.");
            var schema = target.Schema.DeepClone().AsObject(); schema["enum"] = choices;
            return target with { Schema = schema };
        }
        return target;
    }
    internal static List<PlanningDiagnostic> Validate(PlanningGraph candidate, PlanningPreparation preparation, PlanningDataflowContract dataflow, IEnumerable<PlanningExactPatches.Target> targets)
    {
        var json = PlanningFieldPaths.Json(candidate); var findings = new List<PlanningDiagnostic>();
        var paths = targets.Select(t => ValueOwner(json, t.Path, true) ?? ValueOwner(json, t.Path)).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var value = JsonSerializer.Deserialize(PlanningFieldPaths.Read(json, path)!, PlanningJsonContext.Default.PlanningValue);
            var view = View(candidate, preparation, dataflow, path);
            if (PlanningHoleEligibility.Validate(view.State, view.Workflow, view.Hole, value) is { } finding) findings.Add(finding);
        }
        return findings;
    }
}
