using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningScenarioFixtures
{
    // The engine constructs known layouts. The model supplies only new sample
    // scalars or selects an issued alternative; fixture data never grants authority.
    private static async Task<JsonNode?> FixtureAsync(PlanningSnapshot state, IPlanningRuntime runtime, string phase, string workflow,
        string coordinate, JsonObject contract, JsonObject context, CancellationToken ct, int depth = 0)
    {
        if (depth > 24) throw new WorkflowRuntimeException("SCENARIO_CONTRACT_DEPTH", "The fixture contract exceeds its representable depth at " + coordinate);
        var projected = contract.DeepClone().AsObject();
        if (projected["type"]?.ToString() == "object" && projected["properties"] is JsonObject members)
        {
            // A sample may choose the declared object members even when the runtime
            // also permits additional members. The governing contract is unchanged.
            projected["additionalProperties"] = false;
            projected["required"] = new JsonArray(members.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        }
        var schema = projected["type"]?.ToString() is "object" or "array" || projected["anyOf"] is JsonArray ? projected : PlanningLiteralSchemas.Create(projected, coordinate);
        if (schema["const"] is { } constant) return constant.DeepClone();
        if (schema["enum"] is JsonArray values)
        {
            if (values.Count == 1) return values[0]?.DeepClone();
            var choices = values.Select((value, index) => (Id: "alternative_" + index, Value: value)).ToArray();
            var choice = await Decide(PlanningHoleRequests.Enum(choices.Select(v => v.Id).ToArray()), new JsonObject
            { ["alternatives"] = new JsonObject(choices.Select(v => new KeyValuePair<string, JsonNode?>(v.Id, v.Value?.DeepClone()))) });
            return choices.Single(v => v.Id == choice!.ToString()).Value?.DeepClone();
        }
        if (schema["anyOf"] is JsonArray variants)
        {
            var choices = variants.Select((value, index) => (Id: "contract_" + index, Value: value!.AsObject())).ToArray();
            var choice = choices.Length == 1 ? choices[0].Id : (await Decide(PlanningHoleRequests.Enum(choices.Select(v => v.Id).ToArray()), new JsonObject
            { ["alternatives"] = new JsonObject(choices.Select(v => new KeyValuePair<string, JsonNode?>(v.Id, v.Value.DeepClone()))) }))!.ToString();
            return await FixtureAsync(state, runtime, phase, workflow, coordinate + ":" + choice, choices.Single(v => v.Id == choice).Value, context, ct, depth + 1);
        }
        var types = PlanningContractCompatibility.Types(schema).ToArray();
        if (types.Length == 1 && types.Contains("object") && schema["properties"] is JsonObject properties)
        {
            var result = new JsonObject();
            foreach (var (name, member) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                result[name] = await FixtureAsync(state, runtime, phase, workflow, coordinate + "/" + PlanningFieldPaths.Escape(name), member!.AsObject(), context, ct, depth + 1);
            if (PlanningContractValidation.ValidateInstance(result, contract).Count != 0)
                throw new WorkflowRuntimeException("SCENARIO_SAMPLE_CONFLICT", "The fixture members violate their governing object contract at " + coordinate);
            return result;
        }
        if (types.Length == 1 && types.Contains("array") && schema["items"] is JsonObject item)
        {
            var minimum = schema["minItems"]?.GetValue<int>() ?? 0;
            var maximum = Math.Min(schema["maxItems"]?.GetValue<int>() ?? minimum + 2, minimum + 2);
            if (minimum > maximum || minimum > 64) throw new WorkflowRuntimeException("SCENARIO_CARDINALITY_LIMIT", "The fixture cardinality exceeds its bounded representation at " + coordinate);
            var selected = minimum == maximum ? minimum : int.Parse((await Decide(PlanningHoleRequests.Enum(Enumerable.Range(minimum, maximum - minimum + 1)
                .Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()), new JsonObject { ["choice"] = "sample cardinality" }))!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var result = new JsonArray();
            for (var index = 0; index < selected; index++) result.Add(await FixtureAsync(state, runtime, phase, workflow, coordinate + "/" + index, item, context, ct, depth + 1));
            if (PlanningContractValidation.ValidateInstance(result, schema).Count != 0)
                throw new WorkflowRuntimeException("SCENARIO_SAMPLE_CONFLICT", "The independently constructed fixture members violate their governing collection contract at " + coordinate);
            return result;
        }
        if (types.Length == 1 && types.Contains("null")) return null;
        if (types.Contains("object") || types.Contains("array") || types.Length == 0)
            throw new WorkflowRuntimeException("SCENARIO_CONTRACT_UNRESOLVED", "A fixture needs an established member contract at " + coordinate);
        if (types.Contains("string")) schema["maxLength"] = Math.Min(schema["maxLength"]?.GetValue<int>() ?? 512, 512);
        return await Decide(schema, new());

        async Task<JsonNode?> Decide(JsonObject domain, JsonObject details)
        {
            var id = "sample_" + PlanningGraphCompiler.Fingerprint(coordinate)[..24];
            details["context"] = context.DeepClone(); details["member"] = coordinate;
            var values = await PlanningDecisionPages.ResolveAsync(state, runtime, phase, workflow,
                [new(id, domain, details, PlanningGraphCompiler.Fingerprint(contract.ToJsonString() + context.ToJsonString()))], ct);
            return values[id]?.DeepClone();
        }
    }
}
