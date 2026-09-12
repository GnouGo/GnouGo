using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Current, complete evidence scopes for business decisions. No provider or language heuristics.</summary>
internal static class PlanningChoiceEvidence
{
    internal static string Fingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(
        state.Request.Prompt + ":" + JsonSerializer.Serialize(state.Intent.Answers, PlanningJsonContext.Default.ListPlanningAnswer) + ":" +
        JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.PlanningGraph) + ":" + state.Request.Options["policy"]?.ToJsonString() + ":" + state.Preparation?.Fingerprint);

    internal static List<PlanningReference> Clauses(PlanningSnapshot state)
        => PlanningIntentAssessment.IntentSources(state).SelectMany(s => PlanningReferences.Register(state, s.Id, s.Kind, s.Text)).ToList();

    internal static PlanningReference Parent(PlanningSnapshot state, string id)
    {
        var reference = state.References.Single(r => r.Id == id);
        return PlanningReferences.ContainingClause(state, reference, PlanningSourceDecisions.Sources(state)[reference.SourceId]);
    }

    internal static string Text(PlanningSnapshot state, string reference)
        => PlanningReferences.Resolve(state, reference, PlanningSourceDecisions.Sources(state));

    internal static bool Current(PlanningSnapshot state, string reference)
    {
        try { _ = Text(state, reference); return true; }
        catch (PlanningConflictException) { return false; }
    }

    internal static string Origin(PlanningSnapshot state, string reference)
    {
        var source = state.References.Single(r => r.Id == reference).SourceId;
        return PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == source).Kind switch
        {
            "user_request" => "intent", "user_answer" => "answer", "existing_workflow" => "baseline",
            "host_constraint" => "policy", _ => "unknown"
        };
    }

    internal static IEnumerable<string> Governors(PlanningSnapshot state, PlanningBusinessDecision decision)
    {
        return state.Obligations.Where(o => o.Id != decision.ObligationId && o.Kind is
                "business_input" or "business_preference" or "explicit_value" or "default_value" or "runtime_condition" or "workflow_policy" or "confirmation_required" or "confirmation_forbidden")
            .SelectMany(o => o.EvidenceReferences).Concat(Clauses(state).Where(r => Origin(state, r.Id) is "answer" or "baseline" or "policy").Select(r => r.Id))
            .Distinct(StringComparer.Ordinal);
    }

    internal static void Event(PlanningSnapshot state, PlanningBusinessDecision decision, string kind)
    {
        var identity = kind + ":" + string.Join('|', decision.EvidenceReferences.Order(StringComparer.Ordinal)) + ":" + decision.SelectedChoiceId;
        if (decision.ReportedEvents.Contains(identity, StringComparer.Ordinal)) return;
        decision.ReportedEvents.Add(identity);
        state.Events.Add(new("business_decision_" + kind, state.CurrentPhase ?? PlanningPhase.Behavior, DateTimeOffset.UtcNow, decision.Alternatives.Count));
        System.Diagnostics.Activity.Current?.AddEvent(new("planning.business_decision", tags: new()
        {
            ["phase"] = state.CurrentPhase ?? PlanningPhase.Behavior, ["resolution"] = kind,
            ["decision_id"] = decision.Id, ["candidate_count"] = decision.Alternatives.Count
        }));
    }
}
