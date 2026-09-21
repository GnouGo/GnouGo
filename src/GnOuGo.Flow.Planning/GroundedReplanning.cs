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
        var owners = state.Diagnostics.Where(d => d.Required && d.Location.StartsWith("/scopes/", StringComparison.Ordinal))
            .Select(d => d.Location.Split('/')[2]).Distinct().ToArray();
        var block = owners.Length == 1 ? GroundedTraversal.Blocks(state.GroundedPlan!).FirstOrDefault(b => b.Workflow == owners[0]) : default;
        var subflow = owners.Length == 1 ? state.GroundedPlan!.Subflows.FindIndex(f => f.Name == owners[0]) : -1;
        var path = block.Block is not null ? block.Path : subflow >= 0 ? "/subflows/" + subflow : "";
        string[] fields = block.Block is not null ? ["operations", "result"] : ["inputs", "operations", "outputs"];
        var source = path.Length == 0 ? json : PlanningFieldPaths.Read(json, path)!.AsObject();
        var fragment = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f, source[f]?.DeepClone())));
        var ids = state.Grounding!.Selections!.SelectMany(s => s.CapabilityIds).Distinct();
        var schema = PlanningSchemas.Grounded(ids);
        var properties = schema["properties"]!.AsObject();
        if (block.Block is not null)
            schema["properties"] = new JsonObject { ["operations"] = properties["operations"]!.DeepClone(), ["result"] = PlanningSchemas.Ref("value") };
        else foreach (var key in properties.Select(p => p.Key).Where(k => !fields.Contains(k)).ToArray()) properties.Remove(key);
        schema["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
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
        var check = new PlanningSession { Request = state.Request, Catalog = state.Catalog, SemanticPlan = state.SemanticPlan, Grounding = state.Grounding, GroundedPlan = candidate };
        var boundaries = CapabilityGrounder.ValidateBindings(check);
        if (boundaries.Count > 0) throw new PlanningResponseException(boundaries);
        // The atomic proposal remains non-executable until the full validation pipeline accepts it.
        state.GroundedPlan = candidate; state.Graph = null; state.Fixtures = null; state.Scenarios.Clear();
        state.Yaml = null; state.ApprovedHash = null; state.Diagnostics.Clear(); state.Phase = PlanningPhase.Validation;
    }
}
