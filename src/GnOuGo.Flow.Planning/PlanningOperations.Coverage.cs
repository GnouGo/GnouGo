using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // Derived from current canonical assignments, never from broad context clauses
    // or preliminary contract labels. No separate mutable evidence collection.
    internal static Dictionary<string, string[]> DeclarationExclusions(PlanningSnapshot state)
    {
        PlanningDeclarations.RequireCurrent(state);
        return DeriveDeclarationExclusions(state);
    }

    // Declaration validation also validates operation proofs. The per-assignment
    // defense uses the same derivation without recursively re-entering that gate.
    private static Dictionary<string, string[]> DeriveDeclarationExclusions(PlanningSnapshot state)
    {
        var candidates = PlanningDeclarations.Candidates(state).ToDictionary(o => o.Id, StringComparer.Ordinal);
        var coverage = state.DeclarationAssignments.Where(a => a.Disposition is "distinct_input" or "distinct_output" or "same_as" or "modifier_of")
            .SelectMany(a => (candidates.TryGetValue(a.CandidateId, out var candidate) ? candidate.EvidenceReferences
                : throw Failure(a.CandidateId, "Declaration coverage has a stale candidate.")).Select(id =>
            {
                if (!PlanningChoiceEvidence.Current(state, id)) throw Failure(id, "Declaration coverage requires current owned evidence.");
                var declarations = state.Declarations.Where(d => d.Candidates.Contains(a.CandidateId, StringComparer.Ordinal) || d.Id == a.TargetId).Take(2).ToArray();
                if (declarations.Length != 1) throw Failure(a.CandidateId, "Declaration coverage requires one established canonical target.");
                return (Span: state.References.Single(r => r.Id == id), Declaration: declarations[0].Id);
            })).Distinct().ToArray();
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var evidence in state.RuntimeEvidence.Where(e => e.Role == "local_behavior"))
        {
            ValidateRuntime(state, evidence);
            var action = state.References.Single(r => r.Id == evidence.ActionReference);
            var overlaps = coverage.Where(c => c.Span.Owner == action.Owner && c.Span.SourceId == action.SourceId &&
                c.Span.SourceFingerprint == action.SourceFingerprint && c.Span.Start < action.Start + action.Length &&
                c.Span.Start + c.Span.Length > action.Start).OrderBy(c => c.Span.Start).ToArray();
            if (overlaps.Length == 0) continue;
            var text = PlanningChoiceEvidence.Text(state, action.Id);
            var cursor = action.Start;
            foreach (var (span, _) in overlaps)
            {
                if (span.Start > cursor && !string.IsNullOrWhiteSpace(text.Substring(cursor - action.Start, span.Start - cursor))) break;
                cursor = Math.Max(cursor, Math.Min(span.Start + span.Length, action.Start + action.Length));
            }
            if (cursor < action.Start + action.Length && !string.IsNullOrWhiteSpace(text[(cursor - action.Start)..]))
                throw Failure(evidence.Id, "The runtime action partially overlaps canonical contract evidence; a separate owned action span is required.");
            result.Add(evidence.Id, overlaps.Select(c => c.Declaration).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }
}
