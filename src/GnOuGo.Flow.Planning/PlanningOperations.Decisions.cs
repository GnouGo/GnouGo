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
        var sources = SourceScopes(state);
        return state.RuntimeEvidence.Where(e => e.Role is "local_behavior" or "runtime_action")
            .OrderBy(e => state.References.Single(r => r.Id == e.SourceReference).SourceId, StringComparer.Ordinal)
            .ThenBy(e => state.References.Single(r => r.Id == e.ActionReference).Start).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => sources.Single(s => s.Clause.Id == e.ClauseReference) with { Evidence = e }).ToArray();
    }

    internal static string DecisionId(Scope scope) => "operation_" + scope.Evidence!.Id;

    internal static PlanningDecisionPages.Decision Decision(PlanningSnapshot state, Scope scope, IReadOnlyList<PlanningObligation> staged)
    {
        var evidence = scope.Evidence ?? throw Failure(scope.Clause.Id, "A decision requires eligible runtime evidence.");
        ValidateRuntime(state, evidence);
        var targets = EligibleTargets(state, evidence, staged).ToArray();
        var outcomes = new JsonArray(PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))));
        if (targets.Length > 0) outcomes.Add((JsonNode)PlanningHoleRequests.Object(
            ("status", PlanningHoleRequests.Enum(["reuse"])), ("target", PlanningHoleRequests.Enum(targets.Select(o => o.Id).ToArray()))));
        return new(DecisionId(scope), new JsonObject { ["anyOf"] = outcomes }, new JsonObject
        {
            ["task"] = "Attach governing evidence to the same established runtime occurrence. Kind, effect, requiredness and source boundaries are locked. Descriptions alone cannot prove identity. Select an issued target, or unresolved.",
            ["evidence"] = PlanningChoiceEvidence.Text(state, evidence.ClauseReference),
            ["subject"] = PlanningChoiceEvidence.Text(state, evidence.SubjectReference!),
            ["kind"] = evidence.Kind,
            ["targets"] = new JsonObject(targets.Select(o => new KeyValuePair<string, JsonNode?>(o.Id, JsonValue.Create(Text(state, o)))))
        }, EvidenceFingerprint(state));
    }

    private static IEnumerable<PlanningObligation> EligibleTargets(PlanningSnapshot state, PlanningRuntimeEvidence evidence, IReadOnlyList<PlanningObligation> staged)
        => staged.Where(o => o.Kind == evidence.Kind && o.Required == evidence.Required &&
            (evidence.BaselineReference is null || evidence.BaselineReference == o.OperationAdmission!.BaselineReference) &&
            o.OperationAdmission!.Assignments.Any(a => state.RuntimeEvidence.Single(e => e.Id == a.RuntimeEvidenceId).SubjectReference == evidence.SubjectReference));

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
