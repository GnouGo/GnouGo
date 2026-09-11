using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Exact typed graph fields projected onto the shared atomic patch mechanism.</summary>
public static class PlanningPatches
{
    public static HashSet<string> Scope(PlanningGraph graph, IReadOnlyList<PlanningDiagnostic> diagnostics)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal); var json = PlanningFieldPaths.Json(graph);
        foreach (var diagnostic in diagnostics.Where(d => d.Required))
        {
            var located = PlanningDiagnosticLocations.TypedInput(diagnostic, graph);
            for (var wi = 0; wi < graph.Workflows.Count; wi++)
            {
                var workflow = graph.Workflows[wi]; var root = "/workflows/" + wi;
                foreach (var collection in new[] { "inputs", "outputs" })
                    for (var pi = 0; pi < (collection == "inputs" ? workflow.Inputs.Count : workflow.Outputs.Count); pi++)
                        foreach (var field in collection == "inputs" ? new[] { "schema", "default" } : new[] { "schema", "value" })
                            Visit(root + "/" + collection + "/" + pi + "/" + field, root, null);
                foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
                {
                    if (node.InternalRole is not null) continue;
                    foreach (var field in new[] { "input", "expr", "outputSchema", "structuredOutput/schema" }) Visit(path + "/" + field, path, node.Key);
                    for (var ci = 0; ci < node.Cases.Count; ci++) Visit(path + "/cases/" + ci + "/when", path, node.Key);
                }
                void Visit(string editable, string owner, string? node)
                {
                    if (located.Location != editable && !located.Location.StartsWith(editable + "/", StringComparison.Ordinal)) return;
                    JsonNode? value;
                    try { value = PlanningFieldPaths.Read(json, located.Location); }
                    catch (InvalidOperationException) { return; }
                    if (value is null)
                    {
                        var split = located.Location.LastIndexOf('/');
                        if (PlanningFieldPaths.ReadOptional(json, located.Location[..split]) is JsonObject parent && parent.ContainsKey(located.Location[(split + 1)..]))
                            allowed.Add(Coordinate(workflow.Key, node, located.Location[(owner.Length + 1)..]));
                        return;
                    }
                    foreach (var leaf in Editable(value, located.Location))
                    {
                        if (leaf.EndsWith("/input", StringComparison.Ordinal) || leaf.EndsWith("/inputs", StringComparison.Ordinal) || leaf.EndsWith("/outputs", StringComparison.Ordinal)) continue;
                        allowed.Add(Coordinate(workflow.Key, node, leaf[(owner.Length + 1)..]));
                    }
                }
            }
        }
        return allowed;
    }
    private static IEnumerable<string> Editable(JsonNode value, string path)
    {
        if (value is JsonObject obj)
        {
            if (obj["kind"]?.ToString() == "compute")
            {
                yield return path + "/text";
                if (obj["members"] is JsonArray parameters)
                    for (var i = 0; i < parameters.Count; i++) yield return path + "/members/" + i + "/value";
                yield break;
            }
            if (obj["kind"] is not null && !path.EndsWith("/input", StringComparison.Ordinal)) { yield return path; yield break; }
            foreach (var (name, child) in obj.Where(p => p.Key is not ("name" or "kind" or "capabilityId" or "schemaPointer" or "strict")))
                if (child is not null) foreach (var leaf in Editable(child, path + "/" + PlanningFieldPaths.Escape(name))) yield return leaf;
        }
        else if (value is JsonArray array)
        {
            if (path.EndsWith("/path", StringComparison.Ordinal) || path.EndsWith("/enum", StringComparison.Ordinal)) { yield return path; yield break; }
            for (var i = 0; i < array.Count; i++) if (array[i] is { } item) foreach (var leaf in Editable(item, path + "/" + i)) yield return leaf;
        }
        else yield return path;
    }
    public static string Coordinate(string? workflow, string? node, string field) => JsonSerializer.Serialize(new[] { workflow, node, field }, PlanningJsonContext.Default.StringArray);
    internal static List<PlanningExactPatches.Target> Targets(PlanningGraph? graph, IReadOnlySet<string> allowed)
    {
        return allowed.Order(StringComparer.Ordinal).Select(coordinate =>
        {
            var parts = JsonSerializer.Deserialize(coordinate, PlanningJsonContext.Default.StringArray)!;
            if (parts[0] is null || parts[2] is "input" or "inputs" or "outputs" or "functions" or "onError") throw new InvalidOperationException("Whole-container repair permissions are forbidden.");
            var path = coordinate;
            if (graph is not null)
            {
                var index = graph.Workflows.FindIndex(w => w.Key == parts[0]);
                if (index < 0) throw new InvalidOperationException("Unknown repair workflow.");
                var root = "/workflows/" + index;
                path = parts[1] is null ? root : PlanningGraphValidation.Located(graph.Workflows[index].Steps, root + "/steps")
                    .Concat(PlanningGraphValidation.Located(graph.Workflows[index].Finally, root + "/finally")).Single(p => p.Node.Key == parts[1]).Path;
                path += "/" + parts[2];
            }
            return new PlanningExactPatches.Target("f_" + PlanningGraphCompiler.Fingerprint(coordinate)[..16], path, Contract(parts[2]));
        }).ToList();
    }
    private static JsonObject Contract(string field)
    {
        var name = field.Split('/')[^1];
        return name switch
        {
            "value" or "default" or "expr" or "when" => new() { ["$ref"] = "#/$defs/value" },
            "schema" or "items" or "additionalProperties" => new() { ["$ref"] = "#/$defs/schema" },
            "nullable" or "required" or "boolean" => PlanningHoleRequests.Type("boolean"),
            "number" => PlanningHoleRequests.Type("number"),
            "type" => PlanningHoleRequests.Enum("string", "number", "integer", "boolean", "array", "object"),
            "path" or "enum" => new() { ["type"] = "array", ["items"] = PlanningHoleRequests.Type("string") },
            _ => PlanningHoleRequests.Type("string")
        };
    }
    internal sealed record Request(JsonObject Schema, Dictionary<string, PlanningValue> Bindings, JsonNode Context)
    { internal Dictionary<string, List<string>> ParameterScopes { get; init; } = new(StringComparer.Ordinal); }
    internal static Request CreateRequest(PlanningGraph graph, PlanningPreparation preparation, IReadOnlySet<string> allowed, PlanningDataflowContract dataflow)
    {
        var targets = Targets(graph, allowed); var bindings = new Dictionary<string, PlanningValue>(StringComparer.Ordinal);
        var definitions = PlanningSchemas.ValueDefinitions(); var contexts = new JsonArray();
        var parameterScopes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < targets.Count; i++) targets[i] = PlanningRepairDomains.Specialize(targets[i], graph, preparation, dataflow, bindings, contexts);
        foreach (var workflow in graph.Workflows)
        {
            var root = "/workflows/" + graph.Workflows.IndexOf(workflow) + "/";
            var values = targets.Where(t => t.Path.StartsWith(root, StringComparison.Ordinal) && t.Schema["$ref"]?.ToString() == "#/$defs/value").ToArray();
            if (values.Length == 0) continue;
            var nodes = PlanningGraphValidation.Located(workflow.Steps, root + "steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "finally")).ToArray();
            var holes = values.Select(t => new PlanningHole { Id = t.Id, Path = t.Path, WorkflowKey = workflow.Key,
                NodeKey = nodes.Where(n => t.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault().Node?.Key,
                Kind = "value", Purpose = nodes.Where(n => t.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault().Node?.Purpose ?? workflow.Purpose }).ToArray();
            var snapshot = new PlanningSnapshot { Graph = PlanningContext.Clone(graph), Preparation = preparation };
            snapshot.Construction.Dataflow = dataflow; snapshot.Construction.Holes = holes.ToList();
            var unresolved = PlanningFieldPaths.Json(snapshot.Graph);
            foreach (var hole in holes) PlanningFieldPaths.Replace(unresolved, hole.Path, JsonSerializer.SerializeToNode(new PlanningValue { Kind = PlanningGraphSkeleton.Unresolved }, PlanningJsonContext.Default.PlanningValue));
            snapshot.Graph = JsonSerializer.Deserialize(unresolved, PlanningJsonContext.Default.PlanningGraph)!;
            var scoped = PlanningHoleRequests.Create(snapshot, snapshot.Graph.Workflows.Single(w => w.Key == workflow.Key), holes);
            if (scoped.Schema["$defs"] is JsonObject defs) foreach (var (name, definition) in defs) definitions[name] = definition?.DeepClone();
            foreach (var pair in scoped.Bindings) bindings[pair.Key] = pair.Value;
            foreach (var pair in scoped.ParameterScopes) parameterScopes[pair.Key] = pair.Value;
            foreach (var target in values)
                targets[targets.IndexOf(target)] = target with { Schema = scoped.Schema["properties"]!["assignments"]!["properties"]![target.Id]!.DeepClone().AsObject() };
            contexts.Add(scoped.Context.DeepClone());
        }
        return new(PlanningExactPatches.Schema(targets, new JsonObject { ["$defs"] = definitions }), bindings, contexts) { ParameterScopes = parameterScopes };
    }

    public static PlanningGraph Apply(PlanningGraph original, JsonObject response, IReadOnlySet<string> allowed, PlanningPreparation preparation, PlanningDataflowContract dataflow)
    {
        var request = CreateRequest(original, preparation, allowed, dataflow);
        var candidate = ApplyVerified(original, response, allowed, request);
        var invalid = PlanningRepairDomains.Validate(candidate, preparation, dataflow, Targets(original, allowed));
        if (invalid.Count > 0) throw new InvalidOperationException("The exact repair does not satisfy current field eligibility.");
        return candidate;
    }

    internal static PlanningGraph ApplyVerified(PlanningGraph original, JsonObject response, IReadOnlySet<string> allowed, Request request)
    {
        var errors = PlanningContractValidation.ValidateInstance(response, request.Schema);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid exact-field repair: " + string.Join("; ", errors));
        var targets = Targets(original, allowed); var typed = response.DeepClone().AsObject();
        foreach (var patch in typed["patches"]!.AsArray())
        {
            var target = targets.Single(t => t.Id == patch!["target"]!.ToString());
            if (target.Schema["$ref"]?.ToString() == "#/$defs/value")
            {
                var value = PlanningHoleAssignments.Value(patch!["value"]!.AsObject(), request.Bindings, request.ParameterScopes.GetValueOrDefault(target.Id));
                patch["value"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.PlanningValue));
            }
        }
        var schema = PlanningExactPatches.Schema(targets, new JsonObject { ["$defs"] = PlanningSchemas.ValueDefinitions() });
        var json = PlanningExactPatches.Apply(PlanningFieldPaths.Json(original), typed, targets, schema);
        var graph = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningGraph)!;
        if (PlanningGraphSkeleton.Fingerprint(original) != PlanningGraphSkeleton.Fingerprint(graph)) throw new InvalidOperationException("A field repair changed frozen topology.");
        return graph;
    }
}
