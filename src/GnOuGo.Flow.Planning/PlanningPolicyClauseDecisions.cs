using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>One bounded adjudication per complete clause; preliminary labels never fix its semantics.</summary>
internal static class PlanningPolicyClauseDecisions
{
    internal static PlanningDecisionPages.Decision Build(PlanningSnapshot state, string clauseId,
        PlanningObligation[] group, PlanningObligation[] candidates, PlanningObligation[] operations)
    {
        var scopes = new JsonArray(
            PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("effect")), ("target", PlanningHoleRequests.Enum("read", "write", "execute", "lifecycle"))),
            PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("unknown")), ("target", PlanningHoleRequests.Enum("unknown"))));
        if (operations.Length > 0) scopes.Add((JsonNode)PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("operation")),
            ("target", PlanningHoleRequests.Enum(operations.Select(o => o.Id).ToArray()))));
        var humans = operations.Where(o => o.Kind == "human_interaction").Select(o => o.Id).ToArray();
        var alternatives = new JsonArray(
            PlanningHoleRequests.Object(("rule", PlanningHoleRequests.Enum("require_confirmation", "forbid_confirmation")),
                ("scope", new JsonObject { ["anyOf"] = scopes }), ("applicability", PlanningHoleRequests.Enum("always", "unless_explicit", "unknown"))),
            PlanningHoleRequests.Object(("rule", PlanningHoleRequests.Enum("forbid_interaction")),
                ("scope", PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("interaction")), ("target", PlanningHoleRequests.Enum(clauseId)))),
                ("interactionTargets", new JsonObject { ["type"] = "array", ["maxItems"] = humans.Length,
                    ["items"] = humans.Length == 0 ? PlanningHoleRequests.Type("string") : PlanningHoleRequests.Enum(humans) }),
                ("applicability", PlanningHoleRequests.Enum("always", "unless_explicit", "unknown"))),
            PlanningHoleRequests.Object(("rule", PlanningHoleRequests.Enum("not_confirmation_policy")),
                ("reason", PlanningHoleRequests.Enum("other_workflow_constraint", "unsupported_preliminary_classification"))),
            PlanningHoleRequests.Object(("rule", PlanningHoleRequests.Enum("rejection_condition")),
                ("permissionRule", PlanningHoleRequests.Enum(candidates.Select(o => o.Id).ToArray()))),
            PlanningHoleRequests.Object(("rule", PlanningHoleRequests.Enum("unknown"))));
        var schema = PlanningHoleRequests.Object(group.Select(o => (o.Id, new JsonObject { ["$ref"] = "#/$defs/policy" })).ToArray());
        schema["$defs"] = new JsonObject { ["policy"] = new JsonObject { ["anyOf"] = alternatives } };
        var context = new JsonObject
        {
            ["clauseReference"] = clauseId, ["clause"] = PlanningChoiceEvidence.Text(state, clauseId),
            ["preliminary"] = new JsonObject(group.Select(o => new KeyValuePair<string, JsonNode?>(o.Id,
                new JsonObject { ["kind"] = o.Kind, ["evidence"] = new JsonArray(o.EvidenceReferences.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }))),
            ["operations"] = new JsonObject(operations.Select(o => new KeyValuePair<string, JsonNode?>(o.Id,
                new JsonObject { ["kind"] = o.Kind, ["clause"] = PlanningChoiceEvidence.Text(state, o.Grounding!.ClauseReference) }))),
            ["permissionClauses"] = new JsonObject(candidates.GroupBy(o => o.Grounding!.ClauseReference).Select(g =>
                new KeyValuePair<string, JsonNode?>(g.Key, new JsonObject { ["clause"] = PlanningChoiceEvidence.Text(state, g.Key),
                    ["ruleIds"] = new JsonArray(g.Select(o => (JsonNode?)JsonValue.Create(o.Id)).ToArray()) }))),
            ["task"] = "Adjudicate this complete clause together. The supplied classifications are preliminary. Assign each issued field its supported policy meaning; duplicates of the same permission are normalized. require_confirmation already prevents the governed action on rejection or unavailable permission. A statement of that consequence is rejection_condition, referencing its require_confirmation field, never forbid_confirmation. forbid_confirmation requires an explicit prohibition on requesting confirmation; forbid_interaction prohibits the specific interaction. Retire unsupported preliminary confirmation labels with not_confirmation_policy without deleting other governing constraints. An effect mentioned by a policy is only a scope over admitted operations. Use unless_explicit only for a declared explicit-user exception. Unknown subjects, applicability or unproven semantics are unknown."
        };
        return new("policy_clause_" + clauseId, schema, context, Fingerprint(state, clauseId, candidates, operations));
    }

    internal static string Fingerprint(PlanningSnapshot state, string clauseId, PlanningObligation[] candidates, PlanningObligation[] operations)
        => PlanningGraphCompiler.Fingerprint("policy-semantics-v2:" + clauseId + ":" + string.Join('|', candidates.Concat(operations)
            .OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.Grounding!.Fingerprint)));
}
