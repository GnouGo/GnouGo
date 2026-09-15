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

    internal static PlanningOperations.Scope Scope(PlanningSnapshot state, string id) => PlanningOperations.Scopes(state).Single(s =>
        PlanningOperations.EffectDecisionId(s.Evidence!) == id || PlanningOperations.EffectDecisionId(s.Evidence!, true) == id);

    internal static JsonObject Defer(PlanningOperations.Scope scope) => new() { ["status"] = "governing", ["evidence"] = Strings([scope.Clause.Id]) };

    internal static void Seed(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null, bool rootsOnly = false)
    {
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var answers = scopes.ToDictionary(s => s.Evidence!.Id, s => answer?.Invoke(s) ??
            (s.Evidence!.EvidenceRole == "action" ? Answer(state, s) : Defer(s)));
        var actions = scopes.Where(s => s.Evidence!.EvidenceRole == "action").ToArray();
        var values = new JsonObject(actions.Select(s => new KeyValuePair<string, JsonNode?>(PlanningOperations.EffectDecisionId(s.Evidence!),
            answers[s.Evidence!.Id]["contribution"]?.ToString() is "governs" or "shared_rule" ? Defer(s) : answers[s.Evidence.Id].DeepClone())));
        SeedPages(state, actions.Select(s => PlanningOperations.EffectDecision(state, s)).ToArray(), values);
        if (rootsOnly) return;
        var realized = PlanningOperations.ReadRealizations(state);
        var pending = scopes.Where(s => s.Evidence!.EvidenceRole == "governing" ||
            answers[s.Evidence.Id]["contribution"]?.ToString() is "governs" or "shared_rule" || answers[s.Evidence.Id]["status"]?.ToString() == "governing")
            .Where(s => PlanningOperations.RealizedDomain(state, s.Evidence!, realized).Count != 0 && PlanningOperations.DeterministicGoverning(state, s, realized) is null).ToArray();
        values = new JsonObject(pending.Select(s => new KeyValuePair<string, JsonNode?>(PlanningOperations.EffectDecisionId(s.Evidence!, true),
            answer is not null ? answers[s.Evidence!.Id].DeepClone() : Answer(state, s, [PlanningOperations.RealizedDomain(state, s.Evidence!, realized).First().Key], "governs"))));
        SeedPages(state, pending.Select(s => PlanningOperations.EffectDecision(state, s, realized)).ToArray(), values);
    }

    internal static void SeedPages(PlanningSnapshot state, PlanningDecisionPages.Decision[] decisions, JsonObject answers)
    {
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
        var scope = Scope(state, p.Key);
        var governing = p.Key == PlanningOperations.EffectDecisionId(scope.Evidence!, true);
        var domain = governing ? PlanningOperations.RealizedDomain(state, scope.Evidence!, PlanningOperations.ReadRealizations(state)) : PlanningOperations.EffectDomain(state, scope.Evidence!);
        var result = domain.FirstOrDefault(d => d.Value.BoundaryKind == "result_realization");
        var effects = result.Key is not null ? new[] { result.Key } : governing ? [domain.First().Key] : null;
        return new KeyValuePair<string, JsonNode?>(p.Key, Answer(state, scope, effects, governing ? "governs" : "realizes"));
    }));
}
