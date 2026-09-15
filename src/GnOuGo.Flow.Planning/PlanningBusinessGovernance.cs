using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Reference-scoped interpretation, followed by deterministic precedence and applicability.</summary>
internal static class PlanningBusinessGovernance
{
    internal static async Task AssessAsync(PlanningSnapshot state, IPlanningRuntime runtime, PlanningBusinessDecision decision, CancellationToken ct)
    {
        var governors = PlanningChoiceEvidence.Governors(state, decision).Distinct(StringComparer.Ordinal).ToArray();
        var choices = new JsonObject(decision.Alternatives.Select(a => new KeyValuePair<string, JsonNode?>(a.Id,
            JsonValue.Create(PlanningChoiceEvidence.Text(state, a.EvidenceReference)))));
        var pages = governors.Select(reference => new PlanningDecisionPages.Decision("governs_" + decision.Id + "_" + reference,
            PlanningHoleRequests.Object(("choice", PlanningHoleRequests.Enum(["unrelated", .. decision.Alternatives.Select(a => a.Id)])),
                ("rule", PlanningHoleRequests.Enum(CanPrefer(state, reference) ? ["require", "deny", "prefer"] : ["require", "deny"])),
                ("applicability", PlanningHoleRequests.Enum(["always", "omitted", "explicit_null", "unknown"]))),
            new JsonObject
            {
                ["subject"] = PlanningChoiceEvidence.Text(state, decision.SubjectReference), ["choices"] = choices.DeepClone(),
                ["source"] = PlanningChoiceEvidence.Text(state, PlanningChoiceEvidence.Parent(state, reference).Id), ["origin"] = PlanningChoiceEvidence.Origin(state, reference),
                ["task"] = "Identify only an explicit governing declaration for this subject. Unrelated, ambiguous or absent declarations select unrelated. Binding instructions require/deny; an explicitly nonbinding preference prefers. A declared omission default applies only to omitted values, never explicit null. A runtime default is executable behavior, not permission to choose a planning outcome. Preserve conditions: unknown applicability cannot grant authority. Never infer a default or a preference."
            }, decision.DependencyFingerprint)).ToArray();
        var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "business_governance", "$plan", pages, ct);
        foreach (var reference in governors)
        {
            var result = response["governs_" + decision.Id + "_" + reference]!;
            var choice = result["choice"]!.ToString();
            if (choice == "unrelated") continue;
            var rule = new PlanningBusinessConstraint(result["rule"]!.ToString(), choice, reference,
                PlanningChoiceEvidence.Origin(state, reference), result["applicability"]!.ToString());
            if (!decision.EvidenceReferences.Contains(reference)) decision.EvidenceReferences.Add(reference);
            if (!decision.Constraints.Contains(rule)) decision.Constraints.Add(rule);
        }
    }

    internal static bool CanPrefer(PlanningSnapshot state, string reference) => PlanningChoiceEvidence.Origin(state, reference) == "baseline" ||
        state.Obligations.Any(o => o.Kind == "business_preference" && o.EvidenceReferences.Contains(reference));

    internal static bool Applies(PlanningBusinessDecision decision, PlanningBusinessConstraint rule)
        => rule.Applicability == "always" || rule.Applicability == decision.ValuePresence && decision.ValuePresence is "omitted" or "explicit_null";

    internal static IEnumerable<PlanningBusinessConstraint> Applicable(PlanningSnapshot state, PlanningBusinessDecision decision)
    {
        var rules = decision.Constraints.Where(r => Applies(decision, r)).ToArray();
        // Policy is never superseded by user intent. Current answers supersede their
        // earlier intent/baseline choice; an unchanged baseline still governs.
        var governing = rules.Where(r => r.Kind is "require" or "deny" && r.Origin != "policy").ToArray();
        int Priority(PlanningBusinessConstraint r) => r.Origin switch { "answer" => 3, "intent" => 2, "baseline" => 1, _ => 0 };
        var priority = governing.Length == 0 ? 0 : governing.Max(Priority);
        var sourceOrder = PlanningIntentAssessment.IntentSources(state).Select((s, index) => (s.Id, index)).ToDictionary(p => p.Id, p => p.index);
        var latestAnswer = governing.Where(r => r.Origin == "answer").Select(r => state.References.Single(x => x.Id == r.SourceReference).SourceId)
            .OrderBy(id => sourceOrder[id]).LastOrDefault();
        return rules.Where(r => r.Kind == "prefer" || r.Origin == "policy" || Priority(r) == priority &&
            (r.Origin != "answer" || state.References.Single(x => x.Id == r.SourceReference).SourceId == latestAnswer));
    }
}
