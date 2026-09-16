using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Consumer-authored policy meaning with complete owned text coverage. No host-specific semantics.</summary>
internal static class PlanningDeclaredPolicyProjection
{
    internal static PlanningDeclaredPolicyEvidence? Read(PlanningSnapshot state)
    {
        if (state.Request.Options["policy"]?["declared_evidence"] is not { } json) return null;
        PlanningDeclaredPolicyEvidence evidence;
        try { evidence = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningDeclaredPolicyEvidence)!; }
        catch (JsonException) { throw Failure(); }
        var text = state.Request.Options["policy"]?["instructions"]?.GetValue<string>() ?? "";
        if (evidence is not { Version: 1, Clauses: not null } || evidence.SourceFingerprint != PlanningGraphCompiler.Fingerprint(text) ||
            evidence.Clauses.Count == 0 || evidence.Clauses.Any(c => c is null)) throw Failure();
        var end = 0;
        foreach (var clause in evidence.Clauses.OrderBy(c => c.Start))
        {
            if (clause.Start < end || clause.Length < 1 || clause.Start > text.Length - clause.Length ||
                !string.IsNullOrWhiteSpace(text[end..clause.Start]) || clause.Meanings is not { Count: > 0 } ||
                clause.Meanings.Any(m => m is null || !PlanningSourceGroundingRules.PolicyKinds.Contains(m.Kind, StringComparer.Ordinal)) ||
                clause.Meanings.Select(m => m.Kind).Distinct(StringComparer.Ordinal).Count() != clause.Meanings.Count) throw Failure();
            end = clause.Start + clause.Length;
        }
        if (!string.IsNullOrWhiteSpace(text[end..])) throw Failure();
        return evidence;
    }

    internal static string Fingerprint(PlanningSnapshot state) => Read(state) is { } evidence
        ? PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(evidence with { Clauses = evidence.Clauses.OrderBy(c => c.Start)
            .Select(c => c with { Meanings = c.Meanings.OrderBy(m => m.Kind, StringComparer.Ordinal).ToList() }).ToList() }, PlanningJsonContext.Default.PlanningDeclaredPolicyEvidence)) : "";

    internal static bool Owns(PlanningSnapshot state, PlanningIntentAssessment.IntentSource source) =>
        source.Id == "host" && source.Authority == PlanningSourceAuthority.ConstraintsOnly && Clauses(state).Count != 0;

    internal static List<(PlanningReference Reference, PlanningDeclaredPolicyClause Clause)> Clauses(PlanningSnapshot state)
    {
        var evidence = Read(state);
        if (evidence is null) return [];
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == "host");
        var template = PlanningReferences.Register(state, source.Id, source.Kind, source.Text)[0];
        var result = new List<(PlanningReference, PlanningDeclaredPolicyClause)>();
        foreach (var clause in evidence.Clauses.OrderBy(c => c.Start))
        {
            var span = template with { Start = clause.Start, Length = clause.Length, Id = "r_" + PlanningGraphCompiler.Fingerprint(
                template.Owner + ":declared-policy-v1:" + template.SourceFingerprint + ":" + clause.Start + ":" + clause.Length)[..24] };
            if (!state.References.Any(r => r.Id == span.Id)) state.References.Add(span);
            var containing = PlanningReferences.ContainingClause(state, span, source.Text);
            if (!string.IsNullOrWhiteSpace(source.Text[containing.Start..span.Start]) ||
                !string.IsNullOrWhiteSpace(source.Text[(span.Start + span.Length)..(containing.Start + containing.Length)])) throw Failure();
            result.Add((containing, clause));
        }
        return result;
    }

    internal static List<PlanningObligation> Obligations(PlanningSnapshot state) => Clauses(state).SelectMany(c => c.Clause.Meanings.Select(m =>
    {
        var value = new PlanningObligation("ob_" + PlanningGraphCompiler.Fingerprint("declared-policy-v1:" + c.Reference.Id + ":" + m.Kind)[..16],
            [c.Reference.Id], "workflow", m.Kind, m.Required);
        return value with { Grounding = PlanningSourceGroundingRules.Create(state, value) };
    })).ToList();

    private static WorkflowRuntimeException Failure() => new("INTENT_SOURCE_AUTHORITY_UNPROVEN",
        "Declared policy evidence must match its complete current source, allowed semantics and exact coverage.",
        details: new JsonObject { ["location"] = "/options/policy/declared_evidence" });
}
