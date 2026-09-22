using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class ScenarioFixtureGeneration
{
    internal static async Task ApplyAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var definitions = PlanningSchemas.Definitions();
        var schema = PlanningSchemas.Object(("inputs", PlanningSchemas.Nullable(PlanningSchemas.Ref("literal_object"))),
            ("observations", PlanningSchemas.Array(PlanningSchemas.Object(("workflow", PlanningSchemas.String()), ("node", PlanningSchemas.String()),
                ("responses", PlanningSchemas.Array(PlanningSchemas.Ref("literal")))))));
        schema["$defs"] = definitions; PlanningJsonTransport.PruneDefinitions(schema);
        var domains = PlanningFixtureSamples.Domains(state).ToArray();
        var prompt = """
            Supply literal simulated scenario data for the validated workflow. These are test samples, not real observations or producer contracts.
            Use recursive literal value objects only. For opaque tool results provide a representative whole value appropriate for the explicit adapters in this plan.
            The capability remains opaque. Do not change the plan, infer authoritative fields, claim checks ran, or provide executable expressions.
            Provide one response per invocation in the nominal scenario. Inputs and responses must satisfy all declared contracts.
            """ + "\n" + new JsonObject { ["groundedPlan"] = PlanningJsonTransport.Grounded(state.GroundedPlan!),
                ["domains"] = new JsonArray(domains.Select(d => (JsonNode)new JsonObject { ["location"] = d.Path, ["schema"] = d.Schema.DeepClone(), ["sample"] = d.Sample?.DeepClone() }).ToArray()),
                ["externalSteps"] = new JsonArray(state.Graph!.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)).Where(n => n.Type is "mcp.call" or "llm.call")
                    .Select(n => (JsonNode)new JsonObject { ["workflow"] = w.Key, ["node"] = n.Key })).ToArray()) }.ToJsonString();
        var response = await PlanningModelCalls.CallAsync(state, runtime, "fixtures", prompt, schema, ct);
        state.Fixtures = new()
        {
            Inputs = response["inputs"] is null ? null : Literal(response["inputs"]!)!.AsObject(),
            Observations = response["observations"]!.AsArray().Select(o => new PlanningObservation(o!["workflow"]!.GetValue<string>(), o["node"]!.GetValue<string>(), o["responses"]!.AsArray().Select(r => Literal(r!)).ToList())).ToList()
        };
        state.Diagnostics.Clear(); state.Phase = PlanningPhase.Scenarios;
    }
    private static JsonNode? Literal(JsonNode node)
    {
        var value = JsonSerializer.Deserialize(node, PlanningJsonContext.Default.GroundedValue)!;
        return Read(value);
        static JsonNode? Read(GroundedValue value) => value.Kind switch
        {
            "null" => null, "string" => JsonValue.Create(value.Text), "number" => JsonValue.Create(value.Number), "boolean" => JsonValue.Create(value.Boolean),
            "object" => new JsonObject(value.Members.Select(m => new KeyValuePair<string, JsonNode?>(m.Name, Read(m.Value)))),
            "array" => new JsonArray(value.Items.Select(Read).ToArray()), _ => throw new InvalidOperationException("A fixture must be literal data.")
        };
    }
}
