using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    private static Dictionary<string, PlanningOperationEffectProof> ReadRealizationMappings(PlanningSnapshot state)
    {
        var scopes = DeriveScopes(state).Where(s => s.Evidence!.EvidenceRole == "action").ToArray();
        var decisions = scopes.Where(s => s.Evidence!.BaselineReference is null).Select(s => EffectDecision(state, s)).ToArray();
        var values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions);
        return scopes.ToDictionary(s => s.Evidence!.Id, s => s.Evidence!.BaselineReference is null
            ? ParseEffect(state, s, values[EffectDecisionId(s.Evidence)]!.AsObject()) : BaselineEffect(state, s), StringComparer.Ordinal);
    }

    // Reconstruct selected realizations from durable pages, never from possible
    // anchors or a second authoritative operation collection.
    internal static List<PlanningOperationAssignment> ReadRealizations(PlanningSnapshot state)
    {
        var mappings = ReadRealizationMappings(state);
        var result = new List<PlanningOperationAssignment>();
        foreach (var scope in DeriveScopes(state).Where(s => mappings.GetValueOrDefault(s.Evidence!.Id)?.Contribution == "realizes"))
        {
            var proof = mappings[scope.Evidence!.Id];
            var ids = proof.Candidates.Select(a => CanonicalId(state, a, scope.Evidence.Kind!, scope.Evidence.BaselineReference)).Order(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) throw Failure(scope.Clause.Id, "A realization requires a proven effect identity.");
            var selected = ids[0];
            if (ids.Length > 1)
            {
                var decision = IdentityDecision(state, scope, proof, ids);
                var values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", [decision]);
                selected = values[decision.Id]!.ToString();
            }
            var exists = result.Any(a => a.EffectId == selected);
            result.Add(Assignment(scope, proof, selected, exists ? selected : null, exists ? "same_as" : "distinct", ids.Length == 1 ? "deterministic" : "model"));
        }
        return result;
    }

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

    private static Scope[] GoverningScopes(Scope[] scopes, Dictionary<string, PlanningOperationEffectProof> mappings) =>
        scopes.Where(s => s.Evidence!.EvidenceRole == "governing" || mappings.GetValueOrDefault(s.Evidence.Id)?.Contribution == "pending_governing").ToArray();

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
        return new(3, EffectDecisionId(scope.Evidence!, true), "governs", [domain[id]],
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
        var scopes = GoverningScopes(DeriveScopes(state), ReadRealizationMappings(state));
        if (!scopes.Any(s => s.Evidence!.Id == scope.Evidence!.Id)) throw Failure(scope.Clause.Id, "This evidence was not deferred for governing assessment.");
        var deterministic = DeterministicGoverning(state, scope, realized);
        if (deterministic is not null) return deterministic;
        var decisions = scopes.Where(s => DeterministicGoverning(state, s, realized) is null).Select(s => EffectDecision(state, s, realized)).ToArray();
        var values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions);
        return ParseEffect(state, scope, values[EffectDecisionId(scope.Evidence!, true)]!.AsObject(), realized);
    }
}
