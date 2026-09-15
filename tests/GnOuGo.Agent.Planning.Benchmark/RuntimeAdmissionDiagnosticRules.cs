using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class RuntimeAdmissionDiagnosticRules
{
    internal const string Identity = "schema5-runtime-occurrence-diagnostics-rerun-1";
    internal const int MaxCalls = 16;
    internal static readonly string[] Cases = ["local", "mixed"];
    internal static void RequireCase(string name, JsonObject? previous)
    {
        if (!Cases.Contains(name, StringComparer.Ordinal)) throw new InvalidOperationException("Only the two frozen diagnostic cases are authorized.");
        if (name == "mixed" && previous?["status"]?.ToString() != "passed") throw new InvalidOperationException("The first diagnostic must pass before the mixed case.");
    }
    internal static void RequireRequest(PlanningSnapshot state)
    {
        if (state.RequestAccounting.Any(c => c.Phase is not ("intent" or "intent_repair" or "intent_operations" or "intent_operations_repair" or "intent_relations" or "intent_relations_repair") || c.Reasoning != "low"))
            throw new InvalidOperationException("This diagnostic cannot dispatch other phases or reasoning profiles.");
        if (state.Graph is not null || state.BehaviorPlan is not null || state.ApprovedBehaviorHash is not null || state.ApprovedHash is not null)
            throw new InvalidOperationException("An admission diagnostic cannot construct or approve a workflow.");
    }

    internal static void RequirePreflight(int interpretationPages)
    {
        if (interpretationPages > MaxCalls) throw new InvalidOperationException("Packed interpretation exceeds the frozen diagnostic budget before dispatch.");
    }

    // Report only semantic enum domains. Source text, names, scoped references
    // and arbitrary literal/contract content stay in the encrypted manifest.
    internal static JsonObject Domains(JsonObject schema)
    {
        var values = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["properties"] is JsonObject fields)
                    foreach (var name in new[] { "role", "kind", "status", "evidence", "execution", "ownership", "resourceAction" })
                        if (fields[name]?["enum"] is JsonArray choices)
                        {
                            if (!values.TryGetValue(name, out var domain)) values[name] = domain = new(StringComparer.Ordinal);
                            foreach (var choice in choices) domain.Add(choice!.ToString());
                        }
                foreach (var child in obj) Visit(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(schema);
        return new(values.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonArray(p.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()))));
    }
}
