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
        var excluded = DeclarationExclusions(state);
        return state.RuntimeEvidence.Where(e => (e.Role is "local_behavior" or "runtime_action") && !excluded.ContainsKey(e.Id))
            .OrderBy(e => e.BaselineReference is null ? 1 : 0)
            .ThenBy(e => state.References.Single(r => r.Id == e.SourceReference).SourceId, StringComparer.Ordinal)
            .ThenBy(e => state.References.Single(r => r.Id == e.ActionReference).Start).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => sources.Single(s => s.Clause.Id == e.ClauseReference) with { Evidence = e }).ToArray();
    }

    internal static string DecisionId(Scope scope) => "operation_" + scope.Evidence!.Id;

    internal static PlanningDecisionPages.Decision Decision(PlanningSnapshot state, Scope scope, IReadOnlyList<PlanningObligation> staged)
    {
        var evidence = scope.Evidence ?? throw Failure(scope.Clause.Id, "A decision requires eligible runtime evidence.");
        ValidateRuntime(state, evidence);
        var targets = EligibleTargets(state, evidence, staged).ToArray();
        var governing = evidence.EvidenceRole == "governing";
        var outcomes = new JsonArray(PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))));
        if (governing)
        {
            if (targets.Length > 0) outcomes.Add((JsonNode)PlanningHoleRequests.Object(
                ("status", PlanningHoleRequests.Enum(["attach"])),
                ("targets", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = targets.Length,
                    ["items"] = PlanningHoleRequests.Enum(targets.Select(o => o.Id).ToArray()) })));
        }
        else
        {
            outcomes.Add((JsonNode)PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["distinct", "not_an_operation"]))));
            if (targets.Length > 0) outcomes.Add((JsonNode)PlanningHoleRequests.Object(
                ("status", PlanningHoleRequests.Enum(["same_as"])), ("target", PlanningHoleRequests.Enum(targets.Select(o => o.Id).ToArray()))));
        }
        return new(DecisionId(scope), new JsonObject { ["anyOf"] = outcomes }, new JsonObject
        {
            ["stage"] = governing ? "governing" : "roots",
            ["task"] = governing
                ? "Attach this rule to the established actions it governs. Select several targets only when the rule applies to each, not to combine ambiguous alternatives."
                : "Decide only occurrence identity. Reuse an action when this is evidence for that same execution occurrence; keep genuinely independent executions distinct. Descriptions or equal kinds alone cannot establish identity.",
            ["clause"] = PlanningChoiceEvidence.Text(state, evidence.ClauseReference),
            ["action"] = PlanningChoiceEvidence.Text(state, evidence.ActionReference!),
            ["kind"] = evidence.Kind, ["required"] = evidence.Required,
            ["resourceAction"] = evidence.ResourceAction, ["resourceOwnership"] = evidence.ResourceOwnership,
            ["resource"] = evidence.ResourceReference is null ? null : PlanningChoiceEvidence.Text(state, evidence.ResourceReference),
            ["rootSetFingerprint"] = RootSetFingerprint(staged),
            ["targets"] = new JsonObject(targets.Select(o => new KeyValuePair<string, JsonNode?>(o.Id, new JsonObject
            { ["evidence"] = Text(state, o), ["kind"] = o.Kind, ["required"] = o.Required,
                ["baseline"] = o.OperationAdmission!.BaselineReference })))
        }, EvidenceFingerprint(state));
    }

    private static string RootSetFingerprint(IEnumerable<PlanningObligation> roots) => PlanningGraphCompiler.Fingerprint(
        string.Join('|', roots.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.OperationAdmission!.ProofFingerprint)));

    private static bool Compatible(PlanningSnapshot state, PlanningRuntimeEvidence evidence, PlanningObligation operation)
    {
        var root = state.RuntimeEvidence.Single(e => e.Id == operation.OperationAdmission!.Assignments[0].RuntimeEvidenceId);
        return operation.Kind == evidence.Kind && operation.Required == evidence.Required &&
            root.Role == evidence.Role && root.ExecutionScope == evidence.ExecutionScope &&
            root.BaselineReference == evidence.BaselineReference && root.ResourceAction == evidence.ResourceAction &&
            root.ResourceOwnership == evidence.ResourceOwnership;
    }

    private static IEnumerable<PlanningObligation> EligibleTargets(PlanningSnapshot state, PlanningRuntimeEvidence evidence, IReadOnlyList<PlanningObligation> staged)
        => staged.Where(o => Compatible(state, evidence, o)).OrderBy(o => o.Id, StringComparer.Ordinal);

    private static bool ExactIdentity(PlanningSnapshot state, PlanningRuntimeEvidence evidence, PlanningObligation operation)
        => evidence.BaselineReference is not null && evidence.BaselineReference == operation.OperationAdmission!.BaselineReference ||
            CanonicalId(state, state.References.Single(r => r.Id == evidence.ActionReference), evidence.BaselineReference) == operation.Id;

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
