using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Only established runtime evidence can expose bounded action-identity choices.</summary>
internal static partial class PlanningOperations
{
    internal sealed record Scope(PlanningIntentAssessment.IntentSource Source, PlanningReference Clause,
        JsonObject Words, JsonObject Boundaries, Func<string, string, PlanningReference> Select, PlanningRuntimeEvidence? Evidence = null);

    internal static Scope[] SourceScopes(PlanningSnapshot state) => PlanningIntentAssessment.IntentSources(state)
        .Where(s => s.Authority is PlanningSourceAuthority.RequestedBehavior or PlanningSourceAuthority.ExistingBehavior)
        .OrderBy(s => s.Authority == PlanningSourceAuthority.ExistingBehavior ? 0 : 1)
        .SelectMany(source => PlanningReferences.Register(state, source.Id, source.Kind, source.Text)
            .Where(r => !string.IsNullOrWhiteSpace(source.Text.Substring(r.Start, r.Length)))
            .Select(r => PlanningReferences.ContainingClause(state, r, source.Text)).DistinctBy(r => r.Id, StringComparer.Ordinal)
            .OrderBy(r => r.Start).Select(clause =>
            {
                var boundaries = PlanningReferences.Boundaries(clause, source.Text);
                return new Scope(source, clause, boundaries.Context, boundaries.Schema, boundaries.Select);
            })).ToArray();

    internal static Scope[] Scopes(PlanningSnapshot state)
    {
        RequireRuntimeEvidence(state);
        PlanningDeclarations.RequireCurrent(state);
        return DeriveScopes(state);
    }

    private static Scope[] DeriveScopes(PlanningSnapshot state)
    {
        var sources = SourceScopes(state);
        var excluded = DeriveDeclarationExclusions(state);
        var scopes = state.RuntimeEvidence.Where(e => (e.Role is "local_behavior" or "runtime_action") && !excluded.ContainsKey(e.Id))
            .OrderBy(e => e.BaselineReference is null ? 1 : 0)
            .ThenBy(e => state.References.Single(r => r.Id == e.SourceReference).SourceId, StringComparer.Ordinal)
            .ThenBy(e => state.References.Single(r => r.Id == e.ActionReference).Start).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => sources.Single(s => s.Clause.Id == e.ClauseReference) with { Evidence = e }).ToArray();
        foreach (var group in scopes.Where(s => s.Evidence!.EvidenceRole == "action")
            .GroupBy(s => EffectCoordinate(state, s.Evidence!.ActionReference!), StringComparer.Ordinal))
        {
            var first = group.First().Evidence!;
            if (group.Any(s => !CompatibleFacts(first, s.Evidence!) || first.ResourceReference != s.Evidence!.ResourceReference))
                throw Failure(first.ActionReference!, "The same owned action has contradictory execution facts.");
        }
        return scopes;
    }

    internal static string DecisionId(Scope scope) => "operation_" + scope.Evidence!.Id;

    internal static PlanningDecisionPages.Decision IdentityDecision(PlanningSnapshot state, Scope scope, PlanningOperationEffectProof proof, IReadOnlyList<string> identities)
    {
        if (identities.Count < 2) throw Failure(scope.Clause.Id, "A canonical identity request requires multiple proven identities.");
        return new(DecisionId(scope), PlanningHoleRequests.Enum(identities.Order(StringComparer.Ordinal).ToArray()), new JsonObject
        {
            ["stage"] = "occurrence_identity", ["evidence"] = PlanningChoiceEvidence.Text(state, scope.Evidence!.ClauseReference),
            ["effects"] = new JsonObject(proof.Candidates.Select(a => new KeyValuePair<string, JsonNode?>(
                CanonicalId(state, a, scope.Evidence.Kind!, scope.Evidence.BaselineReference),
                new JsonObject { ["owner"] = a.OwnerReference, ["scope"] = a.WorkflowScope, ["boundary"] = a.BoundaryKind, ["boundaryReference"] = a.BoundaryReference }))),
            ["task"] = "Select the established effect identity governed by this contribution."
        }, EffectFingerprint(state) + ":" + string.Join('|', identities.Order(StringComparer.Ordinal)));
    }

    private static bool Compatible(PlanningSnapshot state, PlanningRuntimeEvidence evidence, PlanningObligation operation)
        => CompatibleFacts(evidence, state.RuntimeEvidence.Single(e => e.Id == operation.OperationAdmission!.Assignments[0].RuntimeEvidenceId));

    // Stable Flow executor semantics, never provider/tool naming. Unknown executor
    // boundaries cannot acquire a kind from their descriptive purpose.
    internal static string[] BaselineKinds(PlanningNode node) => node.Type switch
    {
        "set" or "emit" or "assert.non_null" or "template.render" or "decision.evaluate" or "sequence" or "parallel" or
        "switch" or "loop.sequential" or "loop.parallel" or "workflow.call" => ["local_processing"],
        "human.input" => ["human_interaction"],
        "llm.call" => ["external_execute"],
        "mcp.call" => ["external_read", "external_write", "external_execute", "resource_lifecycle", "cleanup"],
        _ => []
    };
}
