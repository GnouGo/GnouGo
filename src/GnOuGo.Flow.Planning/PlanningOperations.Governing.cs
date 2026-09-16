using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    internal static List<PlanningOperationAssignment> ReadRealizations(PlanningSnapshot state) =>
        CoverageAssignments(state).Where(a => a.Disposition == "supports").ToList();

    internal static Dictionary<string, PlanningOperationEffectAnchor> RealizedAnchors(PlanningSnapshot state, IReadOnlyList<PlanningOperationAssignment> realized) =>
        realized.Select(a => new KeyValuePair<string, PlanningOperationEffectAnchor>(a.EffectId!,
                a.Effect!.Candidates.Single(e => CanonicalId(state, e, a.Kind, a.BaselineReference) == a.EffectId)))
            .DistinctBy(p => p.Key, StringComparer.Ordinal).OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    internal static Dictionary<string, PlanningOperationEffectAnchor> RealizedDomain(PlanningSnapshot state, PlanningRuntimeEvidence evidence,
        IReadOnlyList<PlanningOperationAssignment> realized) => RealizedAnchors(state, realized.Where(a => evidence.BaselineReference is null || a.BaselineReference == evidence.BaselineReference).ToArray());

    internal static string GoverningFingerprint(PlanningSnapshot state, IReadOnlyList<PlanningOperationAssignment> realized) =>
        EffectFingerprint(state) + ":realized:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(
            realized.OrderBy(a => a.EffectId, StringComparer.Ordinal).ThenBy(a => a.RuntimeEvidenceId, StringComparer.Ordinal).ToList(),
            PlanningJsonContext.Default.ListPlanningOperationAssignment));

    internal static PlanningOperationEffectProof? DeterministicGoverning(PlanningSnapshot state, Scope scope,
        IReadOnlyList<PlanningOperationAssignment> realized)
    {
        var domain = RealizedDomain(state, scope.Evidence!, realized);
        if (domain.Count == 0) throw Failure(scope.Evidence!.Id, "Governing evidence requires a compatible realized effect.");
        if (domain.Count != 1) return null;
        var id = domain.Single().Key;
        // Only exact baseline ownership supplies this deterministic shortcut.
        // A shared clause and kind compatibility are insufficient.
        var governing = realized.Where(a => a.EffectId == id && (scope.Evidence!.BaselineReference is { } baseline && a.BaselineReference == baseline)).ToArray();
        if (governing.Length == 0) return null;
        return new(7, EffectDecisionId(scope.Evidence!, true), "governs", [domain[id]],
            governing.SelectMany(a => a.Effect!.Inputs).Distinct().Order(StringComparer.Ordinal).ToList(),
            governing.SelectMany(a => a.Effect!.Outputs).Distinct().Order(StringComparer.Ordinal).ToList(),
            [],
            [scope.Clause.Id], "deterministic", GoverningFingerprint(state, realized));
    }

}
