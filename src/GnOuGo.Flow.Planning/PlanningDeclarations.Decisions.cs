using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Direction-neutral evidence acquires port authority only after canonical adjudication.</summary>
internal static partial class PlanningDeclarations
{
    internal static PlanningObligation[] Candidates(PlanningSnapshot state) => state.Obligations
        .Where(o => IsCandidate(o) || o.Kind is "omission_default" or "declaration_constraint").OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
    private static bool IsCandidate(PlanningObligation o) => o.Kind == "declaration_candidate";
    private static bool IsDistinct(PlanningDeclarationAssignment a) => a.Disposition is "distinct_input" or "distinct_output";

    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        var fingerprint = EvidenceFingerprint(state);
        if (state.DeclarationFingerprint is not null) { RequireCurrent(state); return; }
        try
        {
            // Durable pages are the staging area. Replaying this call revalidates
            // completed roots before constructing the exact dependent target domain.
            var rootValues = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", Decisions(state), ct);
            var roots = await CorrectDuplicateRootsAsync(state, runtime, Parse(rootValues), ct);
            var attachments = AttachmentDecisions(state, roots);
            var linked = Parse(await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", attachments, ct));
            Commit(state, roots.Where(a => a.Disposition != "deferred_attachment").Concat(linked).ToList(), fingerprint);
        }
        catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException error) when (error.Code == "DECLARATION_GROUNDING_UNRESOLVED")
        {
            state.Events.Add(new("declaration_unresolved", PlanningPhase.Capabilities, DateTimeOffset.UtcNow, 1));
            System.Diagnostics.Activity.Current?.AddEvent(new("planning.declaration_unresolved", tags: new() { ["location"] = error.Details?["location"]?.ToString() }));
            throw;
        }
        await runtime.CheckpointAsync(state, ct);
    }

    private static List<PlanningDeclarationAssignment> Parse(JsonObject values) => values.SelectMany(page => page.Value!.AsObject()).Select(pair =>
    {
        var value = pair.Value!;
        return new PlanningDeclarationAssignment(pair.Key, value["disposition"]!.ToString(), value["target"]?.ToString(),
            value["name"]?.ToString(), value["scope"]?.ToString(), value["presence"]?.ToString() ?? "unspecified", value["default"]?.ToString())
            { DeclarationReference = value["declaration"]?.ToString(), PresenceReference = value["presenceEvidence"]?.ToString() };
    }).ToList();

    internal static PlanningDeclarationAssignment RootAssignment(PlanningDeclarationAssignment assignment) => assignment.Disposition is "same_as" or "modifier_of"
        ? new(assignment.CandidateId, "deferred_attachment", null, null, null, "unspecified", null) : assignment;

    internal static PlanningDecisionPages.Decision[] Decisions(PlanningSnapshot state)
    {
        var candidates = Candidates(state);
        var baseline = Baselines(state);
        var scopes = new[] { "main" }.Concat(state.Obligations.Where(o => o.Kind == "workflow_boundary").Select(o => o.Id))
            .Concat(baseline.Values.Select(p => p.Scope)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var presenceReferences = candidates.Where(o => IsCandidate(o) || o.Kind == "omission_default")
            .Select(o => o.Grounding!.ClauseReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return candidates.Where(IsCandidate).GroupBy(o => o.Grounding!.ClauseReference, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var lexical = Lexical(state, group.Key);
            var names = lexical.Where(r => NameToken(PlanningChoiceEvidence.Text(state, r.Id)) is not null).Select(r => r.Id).ToArray();
            var schema = PlanningHoleRequests.Object(group.Select(o =>
            {
                var variants = new JsonArray(Rule("unresolved"), Rule("not_a_declaration"), Rule("deferred_attachment"));
                if (o.Grounding!.Authority == PlanningSourceAuthority.RequestedBehavior && names.Length > 0)
                    foreach (var direction in new[] { "input", "output" })
                        variants.Add((JsonNode)Rule("distinct_" + direction, ("name", PlanningHoleRequests.Enum(names)),
                            ("scope", PlanningHoleRequests.Enum(scopes)), ("presence", PlanningHoleRequests.Enum(["required", "optional"])),
                            ("default", PlanningHoleRequests.Type("null")), ("declaration", PlanningHoleRequests.Enum([group.Key])),
                            ("presenceEvidence", PlanningHoleRequests.Enum(presenceReferences))));
                return (o.Id, new JsonObject { ["anyOf"] = variants });
            }).ToArray());
            var context = Context(state, group.Key, group, lexical);
            context["task"] = "Establish public declarations from complete evidence. distinct_input/output requires an explicitly declared subject and established port presence, not member presence or obligation necessity. Select owned declaration and presence evidence. Defer aliases and type, enum, preservation or presence modifiers for attachment to canonical declarations. A broad candidate without one established subject is unresolved. Shared clauses or overlapping spans alone do not prove identity. Known baseline ports are already established; do not redeclare them.";
            context["presenceEvidence"] = new JsonObject(presenceReferences.Select(id => new KeyValuePair<string, JsonNode?>(id, JsonValue.Create(PlanningChoiceEvidence.Text(state, id)))));
            context["baselinePorts"] = new JsonObject(baseline.Select(p => new KeyValuePair<string, JsonNode?>(CanonicalId(p.Value.Name, p.Value.Scope, p.Value.Direction),
                new JsonObject { ["name"] = p.Value.Name, ["direction"] = p.Value.Direction, ["scope"] = p.Value.Scope, ["presence"] = p.Value.Required ? "required" : "optional" })));
            context["scopes"] = new JsonObject(state.Obligations.Where(o => o.Kind == "workflow_boundary").Select(o =>
                new KeyValuePair<string, JsonNode?>(o.Id, JsonValue.Create(PlanningSourceDecisions.Text(state, o)))));
            return new PlanningDecisionPages.Decision("declarations_roots_" + group.Key, schema, context, EvidenceFingerprint(state));
        }).ToArray();
    }

    internal static PlanningDecisionPages.Decision[] AttachmentDecisions(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments)
    {
        var roots = ValidateRoots(state, assignments);
        var fingerprint = Proof(EvidenceFingerprint(state), assignments, roots);
        var pending = assignments.Where(a => a.Disposition == "deferred_attachment").Select(a => a.CandidateId).ToHashSet(StringComparer.Ordinal);
        return Candidates(state).Where(o => pending.Contains(o.Id) || o.Kind is "omission_default" or "declaration_constraint")
            .GroupBy(o => o.Grounding!.ClauseReference, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var lexical = Lexical(state, group.Key);
            var schema = PlanningHoleRequests.Object(group.Select(o => (o.Id, AttachmentSchema(state, o, roots, lexical))).ToArray());
            var context = Context(state, group.Key, group, lexical);
            context["task"] = "Attach evidence to established public declarations. same_as denotes the same public declaration; modifier_of preserves member/type/preservation or presence evidence without adding a port. Canonical direction and presence cannot change. Omission defaults apply only when an optional input is absent. Unsupported identity remains unresolved; descriptive evidence can be retained without port authority.";
            context["targets"] = new JsonObject(roots.Select(d => new KeyValuePair<string, JsonNode?>(d.Id, new JsonObject
                { ["name"] = Name(state, d), ["direction"] = d.Direction, ["scope"] = d.WorkflowScope,
                    ["presence"] = d.Required ? "required" : "optional", ["clauses"] = new JsonObject(d.ClauseReferences.Select(id =>
                        new KeyValuePair<string, JsonNode?>(id, JsonValue.Create(PlanningChoiceEvidence.Text(state, id))))) })));
            return new PlanningDecisionPages.Decision("declarations_attachments_" + group.Key, schema, context, fingerprint);
        }).ToArray();
    }

    private static JsonObject AttachmentSchema(PlanningSnapshot state, PlanningObligation candidate, List<PlanningBusinessDeclaration> roots, PlanningReference[] lexical)
    {
        var variants = new JsonArray(Rule("unresolved"));
        if (candidate.Kind != "declaration_constraint") variants.Add((JsonNode)Rule("not_a_declaration"));
        var targets = roots.Where(d => (candidate.Grounding!.Authority != PlanningSourceAuthority.ExistingBehavior || d.BaselineReference is not null) && PlanningBaselineProjection.OwnsDeclaration(state, candidate, d)).ToArray();
        if (candidate.Kind == "omission_default")
        {
            var spans = candidate.EvidenceReferences.Select(id => state.References.Single(r => r.Id == id)).ToArray();
            var literals = lexical.Where(r => spans.Any(span => r.SourceId == span.SourceId && r.Start >= span.Start && r.Start + r.Length <= span.Start + span.Length) &&
                TryLiteral(PlanningChoiceEvidence.Text(state, r.Id), out _)).Select(r => r.Id).ToArray();
            foreach (var target in targets.Where(d => d.Direction == "input" && !d.Required))
            {
                var allowed = literals;
                if (target.BaselineReference is { } baseline)
                {
                    var previous = Baselines(state)[baseline].Input?.Default;
                    allowed = previous is not null && PlanningGraphValidation.IsLiteral(previous)
                        ? literals.Where(id => JsonNode.DeepEquals(Literal(state, id), PlanningGraphValidation.Literal(previous))).ToArray() : [];
                }
                if (allowed.Length > 0) variants.Add((JsonNode)Rule("modifier_of", ("target", PlanningHoleRequests.Enum([target.Id])),
                    ("presence", PlanningHoleRequests.Enum(["optional"])), ("default", PlanningHoleRequests.Enum(allowed))));
            }
        }
        else if (targets.Length > 0)
        {
            foreach (var kind in candidate.Kind == "declaration_constraint" ? new[] { "modifier_of" } : ["same_as", "modifier_of"])
                variants.Add((JsonNode)Rule(kind, ("target", PlanningHoleRequests.Enum(targets.Select(d => d.Id).ToArray())),
                    ("presence", PlanningHoleRequests.Enum(["unspecified"])), ("default", PlanningHoleRequests.Type("null"))));
        }
        return new() { ["anyOf"] = variants };
    }

    private static JsonObject Rule(string kind, params (string, JsonObject)[] fields) => PlanningHoleRequests.Object(
        new[] { ("disposition", PlanningHoleRequests.Enum([kind])) }.Concat(fields.Select(f => (f.Item1, f.Item2.DeepClone().AsObject()))).ToArray());
    private static PlanningReference[] Lexical(PlanningSnapshot state, string clauseId)
    {
        var clause = state.References.Single(r => r.Id == clauseId);
        return PlanningReferences.Lexical(state, clause, PlanningSourceDecisions.Sources(state)[clause.SourceId]).ToArray();
    }
    private static JsonObject Context(PlanningSnapshot state, string clauseId, IEnumerable<PlanningObligation> group, PlanningReference[] lexical) => new()
    {
        ["clause"] = new JsonObject { [clauseId] = PlanningChoiceEvidence.Text(state, clauseId) },
        ["candidates"] = new JsonObject(group.Select(o => new KeyValuePair<string, JsonNode?>(o.Id,
            new JsonObject { ["kind"] = o.Kind, ["evidence"] = PlanningSourceDecisions.Text(state, o) }))),
        ["tokens"] = new JsonObject(lexical.Select(r => new KeyValuePair<string, JsonNode?>(r.Id, JsonValue.Create(PlanningChoiceEvidence.Text(state, r.Id)))))
    };
}
