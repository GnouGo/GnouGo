using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private static JsonObject BuildTypedCoverageRematchSchema(CapabilityInventory inventory, CapabilityCatalog catalog, IReadOnlySet<string> affected)
        => ScopeTypedCoverageRematchSchema(BuildTypedCapabilityMatchingSchema(inventory, catalog), affected);

    private static JsonObject ScopeTypedCoverageRematchSchema(JsonObject schema, IReadOnlySet<string> affected)
    {
        schema = schema.DeepClone().AsObject();
        var operations = schema["properties"]!["operation_matches"]!.AsObject();
        var entries = operations["properties"]!.AsObject();
        if (affected.Count == 0 || affected.Any(id => !entries.ContainsKey(id)))
            throw new InvalidOperationException("Coverage rematch requires known affected operation identifiers.");
        foreach (var id in entries.Select(p => p.Key).Except(affected).ToArray()) entries.Remove(id);
        operations["required"] = new JsonArray(entries.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        schema["properties"]!.AsObject().Remove("constraint_matches");
        schema["required"] = new JsonArray("operation_matches");
        return schema;
    }

    private static JsonObject MergeTypedCoverageRematch(JsonObject patch, JsonObject baseline, IReadOnlySet<string> affected)
    {
        if (patch.Count != 1 || patch["operation_matches"] is not JsonObject operations ||
            affected.Count == 0 || !operations.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(affected))
            throw new InvalidOperationException("Coverage rematch must contain only operation_matches with exactly the affected operation identifiers.");
        var result = baseline.DeepClone().AsObject(); var retained = result["operation_matches"]!.AsObject();
        foreach (var (id, value) in operations)
        {
            if (!retained.ContainsKey(id) || value is not JsonObject match ||
                !match.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "status", "reason", "catalog_ids", "candidate_catalog_ids", "decision_operation_id", "conditional_mode" }))
                throw new InvalidOperationException("Coverage rematch operation '" + id + "' contains missing, unknown or unsupported fields.");
            retained[id] = match.DeepClone();
        }
        return result; // Reparse this exact merged contract; discarded model fields cannot taint validity.
    }
}
