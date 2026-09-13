using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Bounded adjudication; source spans are candidates, never ports.</summary>
internal static partial class PlanningDeclarations
{
    internal static PlanningObligation[] Candidates(PlanningSnapshot state)
    {
        var declarations = state.Obligations.Where(IsCandidate).ToArray();
        // Standalone defaults may precede their declaration. Their target is a
        // semantic decision, not a nearest-name or nearest-clause heuristic.
        return declarations.Concat(state.Obligations.Where(o => o.Kind == "omission_default"))
            .DistinctBy(o => o.Id).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
    }
    private static bool IsCandidate(PlanningObligation o) => o.Kind is "business_input" or "business_output";

    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        var fingerprint = EvidenceFingerprint(state);
        if (state.DeclarationFingerprint is not null)
        { RequireCurrent(state); return; }
        var decisions = Decisions(state);
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", decisions, ct);
        var assignments = new List<PlanningDeclarationAssignment>();
        foreach (var decision in decisions)
        foreach (var pair in values[decision.Id]!.AsObject())
        {
            var value = pair.Value!;
            assignments.Add(new(pair.Key, value["disposition"]!.ToString(), value["target"]?.ToString(),
                value["name"]?.ToString(), value["scope"]?.ToString(), value["presence"]?.ToString() ?? "unspecified", value["default"]?.ToString()));
        }
        try { Commit(state, assignments, fingerprint); }
        catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException error) when (error.Code == "DECLARATION_GROUNDING_UNRESOLVED")
        {
            // The durable page remains the candidate. Unresolved evidence does
            // not grant a correction loop or partial declaration authority.
            state.Events.Add(new("declaration_unresolved", PlanningPhase.Capabilities, DateTimeOffset.UtcNow, 1));
            System.Diagnostics.Activity.Current?.AddEvent(new("planning.declaration_unresolved", tags: new() { ["location"] = error.Details?["location"]?.ToString() }));
            throw;
        }
        await runtime.CheckpointAsync(state, ct);
    }

    internal static PlanningDecisionPages.Decision[] Decisions(PlanningSnapshot state)
    {
        var candidates = Candidates(state);
        var sources = PlanningSourceDecisions.Sources(state);
        var baseline = Baselines(state);
        var scopes = new[] { "main" }.Concat(state.Obligations.Where(o => o.Kind == "workflow_boundary").Select(o => o.Id))
            .Concat(baseline.Values.Select(p => p.Scope)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var evidence = EvidenceFingerprint(state);
        return candidates.GroupBy(o => o.Grounding!.ClauseReference, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var clause = state.References.Single(r => r.Id == group.Key);
            var lexical = PlanningReferences.Lexical(state, clause, sources[clause.SourceId]);
            var names = lexical.Where(r => NameToken(PlanningReferences.Resolve(state, r.Id, sources)) is not null).Select(r => r.Id).ToArray();
            var schema = PlanningHoleRequests.Object(group.Select(o =>
            {
                var direction = o.Kind == "business_output" ? "output" : "input";
                var targets = candidates.Where(IsCandidate).Where(t => t.Id != o.Id && t.Kind == (direction == "input" ? "business_input" : "business_output"))
                    .Select(t => t.Id).Concat(baseline.Where(p => p.Value.Direction == direction).Select(p => p.Key)).Order(StringComparer.Ordinal).ToArray();
                // Clause context explains the decision; only the selected omission
                // evidence grants value authority. Neighboring condition literals do not.
                var spans = o.EvidenceReferences.Select(id => state.References.Single(r => r.Id == id)).ToArray();
                var literals = o.Kind == "omission_default" ? lexical.Where(r => spans.Any(span =>
                    r.SourceId == span.SourceId && r.Start >= span.Start && r.Start + r.Length <= span.Start + span.Length) &&
                    TryLiteral(PlanningReferences.Resolve(state, r.Id, sources), out _)).Select(r => r.Id).ToArray() : [];
                return (o.Id, AssignmentSchema(state, o, names, literals, targets, scopes, baseline));
            }).ToArray());
            var context = new JsonObject
            {
                ["task"] = "Adjudicate public declarations jointly from the complete clause. Distinct establishes exactly one subject; same_as denotes the same declaration; modifier_of attaches optionality, default, type or preservation evidence without adding a port; not_a_declaration retains descriptive/governing evidence without port authority. Runtime result descriptions are not additional public outputs. Multiple distinct subjects can share a clause. A broad candidate without one unambiguous subject must remain unresolved; do not silently select one of its subjects. Use unresolved when the subject or modifiers cannot be established. Presence refers to the port, not its object members; unspecified contributes no presence constraint. A default is an explicitly declared JSON literal applied only on omission; null is a distinct literal. Names select exact source tokens; never rename or infer an undeclared name.",
                ["clause"] = new JsonObject { [clause.Id] = PlanningReferences.Resolve(state, clause.Id, sources) },
                ["candidates"] = new JsonObject(group.Select(o => new KeyValuePair<string, JsonNode?>(o.Id, new JsonObject
                    { ["direction"] = o.Kind, ["evidence"] = PlanningSourceDecisions.Text(state, o) }))),
                ["tokens"] = new JsonObject(lexical.Select(r => new KeyValuePair<string, JsonNode?>(r.Id, JsonValue.Create(PlanningReferences.Resolve(state, r.Id, sources))))),
                ["targets"] = new JsonObject(candidates.Where(IsCandidate).Where(o => !group.Any(g => g.Id == o.Id)).Select(o =>
                    new KeyValuePair<string, JsonNode?>(o.Id, new JsonObject { ["direction"] = o.Kind, ["clause"] = PlanningChoiceEvidence.Text(state, o.Grounding!.ClauseReference) }))),
                ["baselinePorts"] = new JsonObject(baseline.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                    new JsonObject { ["name"] = p.Value.Name, ["direction"] = p.Value.Direction, ["scope"] = p.Value.Scope }))),
                ["scopes"] = new JsonObject(state.Obligations.Where(o => o.Kind == "workflow_boundary").Select(o =>
                    new KeyValuePair<string, JsonNode?>(o.Id, JsonValue.Create(PlanningSourceDecisions.Text(state, o)))))
            };
            return new PlanningDecisionPages.Decision("declarations_" + group.Key, schema, context, evidence);
        }).ToArray();
    }

    private static JsonObject AssignmentSchema(PlanningSnapshot state, PlanningObligation candidate, string[] names, string[] literals,
        string[] targets, string[] scopes, Dictionary<string, BaselinePort> baseline)
    {
        JsonObject Rule(string kind, params (string, JsonObject)[] fields) => PlanningHoleRequests.Object(
            new[] { ("disposition", PlanningHoleRequests.Enum([kind])) }.Concat(fields.Select(f => (f.Item1, f.Item2.DeepClone().AsObject()))).ToArray());
        var variants = new JsonArray(Rule("unresolved"), Rule("not_a_declaration"));
        if (candidate.Kind == "omission_default")
        {
            // A default makes an input optional. Baseline contracts are already
            // established: only an identical declared default can be reaffirmed.
            var unestablished = targets.Where(id => !baseline.ContainsKey(id)).ToArray();
            if (unestablished.Length > 0 && literals.Length > 0)
                variants.Add((JsonNode)Rule("modifier_of", ("target", PlanningHoleRequests.Enum(unestablished)),
                    ("presence", PlanningHoleRequests.Enum(["optional"])), ("default", PlanningHoleRequests.Enum(literals))));
            foreach (var target in targets.Where(baseline.ContainsKey))
            {
                var port = baseline[target];
                if (port.Required || port.Input?.Default is not { } value || !PlanningGraphValidation.IsLiteral(value)) continue;
                var matching = literals.Where(id => JsonNode.DeepEquals(Literal(state, id), PlanningGraphValidation.Literal(value))).ToArray();
                if (matching.Length > 0) variants.Add((JsonNode)Rule("modifier_of", ("target", PlanningHoleRequests.Enum([target])),
                    ("presence", PlanningHoleRequests.Enum(["optional"])), ("default", PlanningHoleRequests.Enum(matching))));
            }
            return new() { ["anyOf"] = variants };
        }
        var modifiers = new[] { ("presence", PlanningHoleRequests.Enum(["unspecified", "required", "optional"])),
            ("default", PlanningHoleRequests.Type("null")) };
        if (IsCandidate(candidate) && candidate.Grounding!.Authority == PlanningSourceAuthority.RequestedBehavior && names.Length > 0)
            variants.Add((JsonNode)Rule("distinct", [ ("name", PlanningHoleRequests.Enum(names)), ("scope", PlanningHoleRequests.Enum(scopes)), .. modifiers ]));
        var eligibleTargets = targets.Where(id => id != candidate.Id).ToArray();
        if (eligibleTargets.Length > 0)
        {
            if (IsCandidate(candidate)) variants.Add((JsonNode)Rule("same_as", [("target", PlanningHoleRequests.Enum(eligibleTargets)), .. modifiers]));
            variants.Add((JsonNode)Rule("modifier_of", [("target", PlanningHoleRequests.Enum(eligibleTargets)), .. modifiers]));
        }
        return new() { ["anyOf"] = variants };
    }
}
