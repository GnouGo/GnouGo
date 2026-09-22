using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Replace a complete bound business scope, including its exported values, in one transaction.</summary>
internal static class GroundedReplanning
{
    internal static async Task ApplyAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var diagnostics = PlanningDiagnosticLocations.ForIntent(state);
        if (diagnostics.Any(d => d.Required && d.Code == "PLANNING_HOST_CONTRACT"))
        { state.Diagnostics = diagnostics; state.Status = PlanningStatus.Stopped; return; }
        var json = PlanningJsonTransport.Grounded(state.GroundedPlan!);
        var owners = AffectedOwners(state.GroundedPlan!, diagnostics);
        var block = owners.Length == 1 ? GroundedTraversal.Blocks(state.GroundedPlan!).FirstOrDefault(b => b.Workflow == owners[0]) : default;
        var subflow = owners.Length == 1 ? state.GroundedPlan!.Subflows.FindIndex(f => f.Name == owners[0]) : -1;
        var path = block.Block is not null ? block.Path : subflow >= 0 ? "/subflows/" + subflow : "";
        string[] fields = block.Block is not null ? ["operations", "result"] : owners.Length > 1 ? ["inputs", "operations", "outputs", "subflows"] : ["inputs", "operations", "outputs"];
        var source = path.Length == 0 ? json : PlanningFieldPaths.Read(json, path)!.AsObject();
        var fragment = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f, source[f]?.DeepClone())));
        var ids = state.Grounding!.Selections!.SelectMany(s => s.CapabilityIds).Distinct();
        var schema = PlanningSchemas.Grounded(ids);
        var properties = schema["properties"]!.AsObject();
        if (block.Block is not null)
            schema["properties"] = new JsonObject { ["operations"] = properties["operations"]!.DeepClone(), ["result"] = PlanningSchemas.Ref("value") };
        else foreach (var key in properties.Select(p => p.Key).Where(k => !fields.Contains(k) && k != "blockedActions").ToArray()) properties.Remove(key);
        if (block.Block is not null) schema["properties"]!["blockedActions"] = properties["blockedActions"]!.DeepClone();
        schema["required"] = new JsonArray(fields.Append("blockedActions").Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        PlanningJsonTransport.PruneDefinitions(schema);
        var prompt = CapabilityGrounder.BindingPrompt(state) + "\n" + """
            Replan only the complete affected scope below. Return its replacement operations and exported values together.
            Preserve every semantic action and the scope's required input/output boundary. Add explicit validation adapters where needed.
            Correct the underlying producer or topology, not successive field-name guesses. Other scopes remain unchanged.
            """ + "\n" + new JsonObject { ["scope"] = path, ["fragment"] = fragment,
                ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic) }.ToJsonString();
        var response = await PlanningModelCalls.CallAsync(state, runtime, "replan", prompt, schema, ct);
        if (JsonNode.DeepEquals(fragment, response))
        { state.Diagnostics.Add(new("REPLAN_NO_PROGRESS", path, "The complete replacement is unchanged.")); state.Status = PlanningStatus.Stopped; return; }
        foreach (var field in fields) source[field] = response[field]?.DeepClone();
        var candidate = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GroundedPlan)!;
        if (owners.Length > 1)
            foreach (var unaffected in state.GroundedPlan!.Subflows.Where(f => !owners.Contains(f.Name, StringComparer.Ordinal)))
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(unaffected, PlanningJsonContext.Default.GroundedSubflow),
                    candidate.Subflows.FirstOrDefault(f => f.Name == unaffected.Name) is { } retained ? JsonSerializer.SerializeToNode(retained, PlanningJsonContext.Default.GroundedSubflow) : null))
                    throw new PlanningResponseException([new("REPLAN_BOUNDARY_INVALID", "/subflows/" + unaffected.Name, "Unrelated subflows must remain unchanged.")]);
        var check = new PlanningSession { Request = state.Request, Catalog = state.Catalog, SemanticPlan = state.SemanticPlan, Grounding = state.Grounding, GroundedPlan = candidate };
        var boundaries = CapabilityGrounder.ValidateBindings(check);
        if (boundaries.Count > 0) throw new PlanningResponseException(boundaries);
        // The atomic proposal remains non-executable until the full validation pipeline accepts it.
        state.GroundedPlan = candidate; state.Graph = null; state.Fixtures = null; state.Scenarios.Clear();
        state.Yaml = null; state.ApprovedHash = null; state.Diagnostics.Clear(); state.Phase = PlanningPhase.Validation;
    }

    internal static string[] AffectedOwners(GroundedPlan plan, IReadOnlyList<PlanningDiagnostic> diagnostics)
    {
        var operations = GroundedTraversal.Located(plan).Select(p => (p.Operation, p.Path, Owner: GroundedTraversal.GraphOwner(plan, p.Path))).ToArray();
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in GroundedTraversal.Blocks(plan))
        {
            var start = block.Path.LastIndexOf("/operations/", StringComparison.Ordinal) + "/operations/".Length;
            var end = block.Path.IndexOf('/', start);
            parents[block.Workflow] = GroundedTraversal.GraphOwner(plan, block.Path[..end]);
        }
        IEnumerable<string> Ancestors(string owner)
        {
            yield return owner;
            while (parents.TryGetValue(owner, out var parent)) { owner = parent; yield return owner; }
        }
        string? Producer(string owner, string id) => Ancestors(owner).Select(scope => operations.FirstOrDefault(o => o.Owner == scope && o.Operation.Id == id).Path).FirstOrDefault(p => p is not null);
        var dependencies = operations.ToDictionary(o => o.Path, o => GroundedTraversal.OwnValues(o.Operation).SelectMany(GroundedTraversal.Values)
            .Where(v => v.Kind == "result" && v.Source is not null).Select(v => Producer(o.Owner, v.Source!))
            .Concat(o.Operation.After.Select(id => Producer(o.Owner, id))).Where(p => p is not null).Cast<string>().Distinct().ToArray());
        var affected = new HashSet<string>(StringComparer.Ordinal); var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(d => d.Required))
        {
            var exact = operations.Where(o => diagnostic.Location == o.Path || diagnostic.Location.StartsWith(o.Path + "/", StringComparison.Ordinal) ||
                diagnostic.Location == "/scopes/" + o.Owner + "/operations/" + o.Operation.Id).OrderByDescending(o => o.Path.Length).FirstOrDefault();
            if (exact.Operation is not null) affected.Add(exact.Path);
            else owners.Add(diagnostic.Location.StartsWith("/scopes/", StringComparison.Ordinal) ? diagnostic.Location.Split('/')[2] :
                diagnostic.Location.StartsWith("/operations/", StringComparison.Ordinal) || diagnostic.Location.StartsWith("/subflows/", StringComparison.Ordinal)
                    ? GroundedTraversal.GraphOwner(plan, diagnostic.Location) : "main");
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var operation in operations)
            {
                if (affected.Contains(operation.Path)) foreach (var dependency in dependencies[operation.Path]) changed |= affected.Add(dependency);
                else if (dependencies[operation.Path].Any(affected.Contains) || operation.Operation is CallGroundedOperation call &&
                    operations.Any(p => affected.Contains(p.Path) && Ancestors(p.Owner).Last() == call.Flow)) changed |= affected.Add(operation.Path);
            }
        }
        owners.UnionWith(operations.Where(o => affected.Contains(o.Path)).Select(o => o.Owner));
        if (owners.Count == 0) return ["main"];
        var common = Ancestors(owners.First()).FirstOrDefault(a => owners.All(o => Ancestors(o).Contains(a, StringComparer.Ordinal)));
        return common is null ? owners.ToArray() : [common];
    }
}
