using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Versioned review wire annotations, translated into existing placement and effect fields.</summary>
internal static class PlanningOutcomeAnnotations
{
    internal static IEnumerable<PlanningCapability> Contracts(PlanningSession state, IReadOnlySet<string>? admitted = null) =>
        (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version))
        .Where(c => state.Catalog!.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id))
        .GroupBy(c => TaskOperations.Describe(c).Id, StringComparer.Ordinal)
        .Where(g => g.Count() == 1 && TaskOperations.Validate(g.First()).Count == 0 && (admitted is null || admitted.Contains(g.Key)))
        .Select(g => g.First());

    internal static void Schema(PlanningSession state, JsonObject root, JsonObject definitions, IReadOnlySet<string>? admitted)
    {
        var contracts = Contracts(state, admitted).Where(c => c.EffectKind is "read" or "write" or "execute" or "lifecycle")
            .Select(c => TaskOperations.Describe(c).Id).Order(StringComparer.Ordinal).ToArray();
        var data = PlanningSchemas.Object(("id", PlanningSchemas.Nonblank()), ("description", PlanningSchemas.Nonblank()),
            ("operation", PlanningSchemas.Type("null")), ("placement", PlanningSchemas.Enum("normal")),
            ("conditional", new JsonObject { ["type"] = "boolean", ["const"] = false }), ("coverage", PlanningSchemas.Enum("once")));
        var alternatives = new JsonArray(data);
        if (contracts.Length > 0)
            alternatives.Add((JsonNode)PlanningSchemas.Object(("id", PlanningSchemas.Nonblank()), ("description", PlanningSchemas.Nonblank()),
                ("operation", PlanningSchemas.Enum(contracts)), ("placement", PlanningSchemas.Enum("normal", "cleanup")),
                ("conditional", PlanningSchemas.Type("boolean")), ("coverage", PlanningSchemas.Enum("once", "each_item"))));
        definitions["requirements"]!["properties"]!["outcomes"]!["items"] = new JsonObject { ["anyOf"] = alternatives };
        var binding = root["properties"]!["outcomeBindings"]!["anyOf"]![0]!["items"]!;
        binding["properties"]!["inputs"] = PlanningSchemas.Array(PlanningSchemas.Nonblank());
        binding["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("inputs"));
    }

    internal static JsonNode Normalize(PlanningSession state, JsonNode response)
    {
        if (state.OutcomeVersion != 3) return response;
        var normalized = response.DeepClone();
        if (normalized["requirements"]?["outcomes"] is not JsonArray outcomes) return normalized;
        var contracts = Contracts(state).ToDictionary(c => TaskOperations.Describe(c).Id, StringComparer.Ordinal);
        foreach (var outcome in outcomes.OfType<JsonObject>())
        {
            var operation = outcome["operation"]?.GetValue<string>();
            outcome["execution"] = operation is null ? "data" : contracts.TryGetValue(operation, out var contract) ? contract.EffectKind : "unknown";
            outcome["always"] = outcome["placement"]?.GetValue<string>() == "cleanup";
            outcome.Remove("placement");
        }
        return normalized;
    }
}
