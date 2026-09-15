using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicit synthetic effect facts. Never imports or replaces historical receipts.</summary>
internal static class OperationEffectFixtures
{
    internal static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope,
        IEnumerable<string>? effects = null, string? contribution = null, IEnumerable<string>? inputs = null,
        IEnumerable<string>? outputs = null, IEnumerable<string>? producers = null)
    {
        var domain = PlanningOperations.EffectDomain(state, scope.Evidence!);
        var ids = effects?.ToArray() ?? [domain.First(p => p.Value.BoundaryReference == scope.Evidence!.ActionReference).Key];
        return new()
        {
            ["status"] = "mapped", ["contribution"] = contribution ?? (scope.Evidence!.EvidenceRole == "action" ? "realizes" : "governs"),
            ["effects"] = Strings(ids), ["inputs"] = Strings(inputs ?? []),
            ["outputs"] = Strings(outputs ?? ids.Select(id => domain[id]).Where(a => a.BoundaryKind == "result_realization").Select(a => a.OwnerReference)),
            ["producers"] = Strings(producers ?? []), ["evidence"] = Strings([scope.Evidence!.ClauseReference])
        };
    }

    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    internal static void Seed(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null)
    {
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var answers = new JsonObject();
        var decisions = scopes.Select(s => PlanningOperations.EffectDecision(state, s)).ToArray();
        foreach (var scope in scopes)
        {
            var domain = PlanningOperations.EffectDomain(state, scope.Evidence!);
            var value = answer is not null ? answer(scope) : scope.Evidence!.EvidenceRole == "action" ? Answer(state, scope) :
                domain.Count == 0 ? new JsonObject { ["status"] = "unresolved" } : Answer(state, scope, domain.Where(p => p.Value.BoundaryKind != "result_realization").Select(p => p.Key));
            answers[PlanningOperations.EffectDecisionId(scope.Evidence!)] = value;
        }
        // Synthetic fixture precondition, deliberately no dispatch/receipt claim.
        while (PlanningDecisionPages.Completed(state, "intent_operations", "$plan", decisions) is null)
        {
            var page = state.DecisionPages.Last(p => p.Phase == "intent_operations" && p.Status == "pending" && p.ParentId is null);
            page.Status = "completed"; page.Candidate = new(page.Decisions.Select(id => new KeyValuePair<string, JsonNode?>(id, answers[id]!.DeepClone())));
        }
    }

    internal static JsonObject Response(PlanningSnapshot state, LLMRequest request) => new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
    {
        if (p.Key.StartsWith("operation_", StringComparison.Ordinal)) return new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![0]!.DeepClone());
        var scope = PlanningOperations.Scopes(state).Single(s => PlanningOperations.EffectDecisionId(s.Evidence!) == p.Key);
        var domain = PlanningOperations.EffectDomain(state, scope.Evidence!);
        var result = domain.FirstOrDefault(d => d.Value.BoundaryKind == "result_realization");
        var effects = result.Key is not null ? new[] { result.Key } : scope.Evidence!.EvidenceRole == "governing" ? domain.Keys.ToArray() : null;
        return new KeyValuePair<string, JsonNode?>(p.Key, Answer(state, scope, effects));
    }));
}
