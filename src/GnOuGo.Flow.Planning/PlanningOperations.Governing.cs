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
        IReadOnlyList<PlanningOperationAssignment> realized) => RealizedAnchors(state, realized.Where(a =>
            CompatibleFacts(evidence, state.RuntimeEvidence.Single(e => e.Id == a.RuntimeEvidenceId))).ToArray());

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
        // Applicability is established by the same owned governing clause or
        // exact baseline node. Kind compatibility alone is insufficient.
        var governing = realized.Where(a => a.EffectId == id && (a.ClauseReference == scope.Clause.Id ||
            scope.Evidence!.BaselineReference is { } baseline && a.BaselineReference == baseline)).ToArray();
        if (governing.Length == 0) return null;
        return new(5, EffectDecisionId(scope.Evidence!, true), "governs", [domain[id]],
            governing.SelectMany(a => a.Effect!.Inputs).Distinct().Order(StringComparer.Ordinal).ToList(),
            governing.SelectMany(a => a.Effect!.Outputs).Distinct().Order(StringComparer.Ordinal).ToList(),
            [],
            [scope.Clause.Id], "deterministic", GoverningFingerprint(state, realized));
    }

    private static async Task<Dictionary<string, PlanningOperationEffectProof>> GroundGoverning(PlanningSnapshot state, IPlanningRuntime runtime,
        Scope[] scopes, IReadOnlyList<PlanningOperationAssignment> realized, CancellationToken ct)
    {
        var deterministic = scopes.ToDictionary(s => s.Evidence!.Id, s => DeterministicGoverning(state, s, realized), StringComparer.Ordinal);
        var decisions = scopes.Where(s => deterministic[s.Evidence!.Id] is null).Select(s => EffectDecision(state, s, realized)).ToArray();
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", decisions, ct);
        return scopes.ToDictionary(s => s.Evidence!.Id, s => deterministic[s.Evidence!.Id] ??
            ParseEffect(state, s, values[EffectDecisionId(s.Evidence, true)]!.AsObject(), realized), StringComparer.Ordinal);
    }

    private static PlanningOperationEffectProof ReadGoverning(PlanningSnapshot state, Scope scope, IReadOnlyList<PlanningOperationAssignment> realized)
    {
        var covered = ReadCoverage(state).SelectMany(p => p.Contributions).Select(c => c.RuntimeEvidenceId).ToHashSet(StringComparer.Ordinal);
        var scopes = DeriveScopes(state).Where(s => s.Evidence!.EvidenceRole == "governing" && !covered.Contains(s.Evidence.Id)).ToArray();
        if (!scopes.Any(s => s.Evidence!.Id == scope.Evidence!.Id)) throw Failure(scope.Clause.Id, "This evidence was not deferred for governing assessment.");
        var deterministic = DeterministicGoverning(state, scope, realized);
        if (deterministic is not null) return deterministic;
        var decisions = scopes.Where(s => DeterministicGoverning(state, s, realized) is null).Select(s => EffectDecision(state, s, realized)).ToArray();
        var values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions);
        return ParseEffect(state, scope, values[EffectDecisionId(scope.Evidence!, true)]!.AsObject(), realized);
    }
}
