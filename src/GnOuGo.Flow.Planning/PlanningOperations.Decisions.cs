using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Complete source clauses, not interpretation labels, define the admission domain.</summary>
internal static partial class PlanningOperations
{
    internal sealed record Scope(PlanningIntentAssessment.IntentSource Source, PlanningReference Clause,
        JsonObject Words, JsonObject Boundaries, Func<string, string, PlanningReference> Select);

    internal static Scope[] Scopes(PlanningSnapshot state) => PlanningIntentAssessment.IntentSources(state)
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

    internal static string DecisionId(Scope scope) => "operation_" + scope.Clause.Id;

    internal static PlanningDecisionPages.Decision Decision(PlanningSnapshot state, Scope scope, IReadOnlyList<PlanningObligation> staged)
    {
        var variants = new JsonArray();
        JsonObject Action(IEnumerable<string> kinds, string? target, string? baseline, bool? required)
        {
            var schema = scope.Boundaries.DeepClone().AsObject();
            var properties = schema["properties"]!.AsObject();
            properties["kind"] = PlanningHoleRequests.Enum(kinds.ToArray());
            properties["target"] = target is null ? PlanningHoleRequests.Type("null") : PlanningHoleRequests.Enum([target]);
            properties["baseline"] = baseline is null ? PlanningHoleRequests.Type("null") : PlanningHoleRequests.Enum([baseline]);
            properties["required"] = required is null ? PlanningHoleRequests.Type("boolean") : new JsonObject { ["type"] = "boolean", ["const"] = required.Value };
            schema["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            return schema;
        }
        var baselineNodes = PlanningSourceGroundingRules.BaselineNodes(state);
        if (scope.Source.Authority == PlanningSourceAuthority.RequestedBehavior)
            variants.Add((JsonNode)Action(PlanningSourceGroundingRules.OperationKinds, null, null, null));
        else if (scope.Source.Authority == PlanningSourceAuthority.ExistingBehavior) foreach (var (id, node) in baselineNodes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var kinds = BaselineKinds(node.Node);
            if (kinds.Length > 0) variants.Add((JsonNode)Action(kinds, null, id, true));
        }
        foreach (var action in staged.Where(_ => scope.Source.Authority is PlanningSourceAuthority.RequestedBehavior or PlanningSourceAuthority.ExistingBehavior).OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var baseline = scope.Source.Authority == PlanningSourceAuthority.ExistingBehavior ? action.OperationAdmission!.BaselineReference : null;
            if (scope.Source.Authority == PlanningSourceAuthority.ExistingBehavior && baseline is null) continue;
            variants.Add((JsonNode)Action([action.Kind], action.Id, baseline, action.Required));
        }
        var outcomes = new JsonArray(
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["not_an_operation"]))),
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))));
        if (variants.Count > 0) outcomes.Add((JsonNode)PlanningHoleRequests.Object(
            ("status", PlanningHoleRequests.Enum(["operations"])),
            ("actions", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4,
                ["items"] = new JsonObject { ["anyOf"] = variants } })));
        var context = new JsonObject
        {
            ["task"] = "Identify every explicitly requested runtime action in this complete clause, independently of preliminary hints. local_processing is in-workflow parsing, validation, filtering, transformation, classification, aggregation or control flow. A policy mentioning an action does not request that action. Select action word boundaries; descriptions and identities are engine-owned. Reuse an issued target only when the clause governs that same action, preserving its kind, requiredness, occurrence and effects. Conditions and constraints may simultaneously govern an operation; reuse retains this clause without creating another action. not_an_operation retains its other obligations. If meaning, identity, baseline compatibility or complete coverage within four actions is unproven, use unresolved.",
            ["sourceAuthority"] = scope.Source.Authority.ToString(), ["clause"] = scope.Clause.Id,
            ["words"] = scope.Words.DeepClone(), ["questionContext"] = scope.Source.QuestionContext,
            ["hints"] = new JsonArray(state.Obligations.Where(o => o.OperationAdmission is null && o.Grounding?.ClauseReference == scope.Clause.Id)
                .Select(o => (JsonNode)new JsonObject { ["id"] = o.Id, ["kind"] = o.Kind }).ToArray()),
            ["targets"] = new JsonObject(staged.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => new KeyValuePair<string, JsonNode?>(o.Id,
                new JsonObject { ["kind"] = o.Kind, ["required"] = o.Required, ["evidence"] = Text(state, o), ["baseline"] = o.OperationAdmission!.BaselineReference }))),
            ["baselineNodes"] = scope.Source.Authority == PlanningSourceAuthority.ExistingBehavior ? new JsonObject(baselineNodes.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["workflow"] = p.Value.Workflow, ["node"] = p.Value.Node.Key,
                    ["type"] = p.Value.Node.Type, ["purpose"] = p.Value.Node.Purpose }))) : null
        };
        return new(DecisionId(scope), new JsonObject { ["anyOf"] = outcomes }, context, EvidenceFingerprint(state));
    }

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
