using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // Derived domains only: exact canonical evidence owns coverage, never a
    // preliminary runtime role/kind or the containing clause. Nothing is persisted
    // beside existing declaration assignments and contribution/admission proofs.
    private sealed record ContractSpan(PlanningReference Span, string Declaration);
    private sealed record ContractExclusion(string[] Declarations, string[] SeparateEvidence);

    internal static Dictionary<string, string[]> DeclarationExclusions(PlanningSnapshot state)
    {
        PlanningDeclarations.RequireCurrent(state);
        return DeriveDeclarationExclusions(state);
    }

    private static ContractSpan[] ContractCoverage(PlanningSnapshot state)
    {
        var candidates = PlanningDeclarations.Candidates(state).ToDictionary(o => o.Id, StringComparer.Ordinal);
        return state.DeclarationAssignments.Where(a => a.Disposition is "distinct_input" or "distinct_output" or "same_as" or "modifier_of")
            .SelectMany(a => (candidates.TryGetValue(a.CandidateId, out var candidate) ? candidate.EvidenceReferences
                : throw Failure(a.CandidateId, "Declaration coverage has a stale candidate.")).Select(id =>
            {
                if (!PlanningChoiceEvidence.Current(state, id)) throw Failure(id, "Declaration coverage requires current owned evidence.");
                var declarations = state.Declarations.Where(d => d.Candidates.Contains(a.CandidateId, StringComparer.Ordinal) || d.Id == a.TargetId).Take(2).ToArray();
                if (declarations.Length != 1) throw Failure(a.CandidateId, "Declaration coverage requires one established canonical target.");
                return new ContractSpan(state.References.Single(r => r.Id == id), declarations[0].Id);
            })).Distinct().OrderBy(c => c.Span.Owner, StringComparer.Ordinal).ThenBy(c => c.Span.SourceId, StringComparer.Ordinal)
            .ThenBy(c => c.Span.SourceFingerprint, StringComparer.Ordinal).ThenBy(c => c.Span.Start).ThenBy(c => c.Span.Length)
            .ThenBy(c => c.Span.Id, StringComparer.Ordinal).ThenBy(c => c.Declaration, StringComparer.Ordinal).ToArray();
    }

    private static string ContractCoverageFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(
        "contract-eligibility-v1:" + state.DeclarationFingerprint + ":" + new JsonArray(ContractCoverage(state).Select(c => (JsonNode)new JsonArray(
            c.Span.Owner, c.Span.SourceId, c.Span.SourceFingerprint, c.Span.Start, c.Span.Length, c.Span.Id, c.Declaration)).ToArray()).ToJsonString());

    private static bool SameSource(PlanningReference left, PlanningReference right) => left.Owner == right.Owner &&
        left.SourceId == right.SourceId && left.SourceFingerprint == right.SourceFingerprint;
    private static bool Overlaps(PlanningReference left, PlanningReference right) => SameSource(left, right) &&
        left.Start < right.Start + right.Length && right.Start < left.Start + left.Length;
    private static bool ContainsSpan(PlanningReference outer, PlanningReference inner) => SameSource(outer, inner) &&
        outer.Start <= inner.Start && inner.Start + inner.Length <= outer.Start + outer.Length;

    private static bool Covered(PlanningSnapshot state, PlanningReference action, IEnumerable<PlanningReference> spans)
    {
        var text = PlanningChoiceEvidence.Text(state, action.Id);
        var owned = spans.Where(s => SameSource(s, action)).ToArray();
        return Enumerable.Range(0, text.Length).All(i => char.IsWhiteSpace(text[i]) ||
            owned.Any(s => s.Start <= action.Start + i && action.Start + i < s.Start + s.Length));
    }

    private static Dictionary<string, ContractExclusion> ContractExclusions(PlanningSnapshot state)
    {
        var coverage = ContractCoverage(state);
        var actions = state.RuntimeEvidence.Where(e => e.ActionReference is not null).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        foreach (var evidence in actions) ValidateRuntime(state, evidence);
        var spans = actions.ToDictionary(e => e.Id, e => state.References.Single(r => r.Id == e.ActionReference), StringComparer.Ordinal);
        var result = new Dictionary<string, ContractExclusion>(StringComparer.Ordinal);
        foreach (var evidence in actions)
        {
            var action = spans[evidence.Id];
            var overlaps = coverage.Where(c => Overlaps(c.Span, action)).ToArray();
            if (overlaps.Length == 0) continue;
            var declarations = overlaps.Select(c => c.Declaration).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (Covered(state, action, overlaps.Select(c => c.Span)))
            {
                result.Add(evidence.Id, new(declarations, []));
                continue;
            }
            // A partial parent is accounted for only by already owned, disjoint
            // runtime evidence with unchanged execution/necessity/boundary facts.
            // No complement substring, word-boundary catalog or sibling clause
            // manufactures action evidence. Children remain qualification candidates.
            var separate = actions.Where(e => e.Id != evidence.Id && ContainsSpan(action, spans[e.Id]) &&
                !coverage.Any(c => Overlaps(c.Span, spans[e.Id])) && CompatibleFacts(evidence, e) &&
                evidence.ResourceReference == e.ResourceReference && evidence.Necessity == e.Necessity &&
                evidence.NecessityReference == e.NecessityReference &&
                (evidence.OccurrenceBoundary is null || evidence.OccurrenceBoundary == e.OccurrenceBoundary)).ToArray();
            if (!Covered(state, action, overlaps.Select(c => c.Span).Concat(separate.Select(e => spans[e.Id]))))
                throw Failure(evidence.Id, "The runtime action partially overlaps canonical contract evidence; complete separate owned action evidence is required.");
            result.Add(evidence.Id, new(declarations, separate.Select(e => e.Id).ToArray()));
        }
        return result;
    }

    private static Dictionary<string, string[]> DeriveDeclarationExclusions(PlanningSnapshot state) => ContractExclusions(state)
        .ToDictionary(p => p.Key, p => p.Value.Declarations, StringComparer.Ordinal);

    private static void RequireEligibleContribution(PlanningSnapshot state, PlanningRuntimeEvidence evidence, PlanningReference? selected = null)
    {
        if (ContractExclusions(state).ContainsKey(evidence.Id))
            throw Failure(evidence.Id, "Canonical contract-covered or separately accounted evidence cannot authorize execution or an operation contribution.");
        if (selected is not null && ContractCoverage(state).Any(c => Overlaps(c.Span, selected)))
            throw Failure(selected.Id, "Executable contribution evidence overlaps canonical contract ownership.");
    }
}
