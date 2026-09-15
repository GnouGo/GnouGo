using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningDeclarations
{
    private sealed record RootConflict(string Identity, List<PlanningBusinessDeclaration> Claims)
    {
        internal string[] Candidates => Claims.SelectMany(d => d.Candidates).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static List<RootConflict> RootConflicts(List<PlanningBusinessDeclaration> roots)
    {
        var conflicts = roots.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => new RootConflict(g.Key, g.ToList())).OrderBy(g => g.Identity, StringComparer.Ordinal).ToList();
        foreach (var conflict in conflicts)
            if (conflict.Claims.Select(d => (d.Direction, d.WorkflowScope, d.Required)).Distinct().Count() != 1)
                throw RootFailure(conflict, "incompatible_root_contracts", "Duplicate declaration claims have contradictory established contracts.");
        return conflicts;
    }

    private static WorkflowRuntimeException RootFailure(RootConflict conflict, string rule, string message)
        => new("DECLARATION_GROUNDING_UNRESOLVED", message, details: new JsonObject
        {
            ["location"] = "/declarations/" + PlanningFieldPaths.Escape("@" + (conflict.Candidates.FirstOrDefault() ?? conflict.Identity)),
            ["rule"] = rule, ["canonicalIdentity"] = conflict.Identity,
            ["candidates"] = new JsonArray(conflict.Candidates.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        });

    internal static PlanningDecisionPages.Decision[] RootCorrections(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments)
    {
        var roots = AssessRoots(state, assignments);
        var conflicts = RootConflicts(roots);
        if (conflicts.Count == 0) return [];
        var candidates = Candidates(state).ToDictionary(o => o.Id, StringComparer.Ordinal);
        var original = Decisions(state).ToDictionary(d => d.Id, StringComparer.Ordinal);
        string Owner(string id) => "declarations_roots_" + candidates[id].Grounding!.ClauseReference;
        // Conflicts sharing an original clause decision share its single correction.
        // Keep their coupled assignments in one indivisible semantic unit.
        var groups = new List<HashSet<string>>();
        foreach (var conflict in conflicts)
        {
            var members = conflict.Candidates.ToHashSet(StringComparer.Ordinal);
            var owners = members.Select(Owner).ToHashSet(StringComparer.Ordinal);
            var overlaps = groups.Where(g => g.Select(Owner).Any(owners.Contains)).ToArray();
            foreach (var group in overlaps) { members.UnionWith(group); groups.Remove(group); }
            groups.Add(members);
        }
        return groups.OrderBy(g => g.Order(StringComparer.Ordinal).First(), StringComparer.Ordinal).Select(group =>
        {
            var members = assignments.Where(a => group.Contains(a.CandidateId)).OrderBy(a => a.CandidateId, StringComparer.Ordinal).ToArray();
            var owners = group.Select(Owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var frozen = roots.Where(d => !d.Candidates.Any(group.Contains)).DistinctBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var occupied = frozen.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var schema = PlanningHoleRequests.Object(members.Select(a =>
            {
                var source = original[Owner(a.CandidateId)].Schema["properties"]![a.CandidateId]!["anyOf"]!.AsArray();
                var variant = source.OfType<JsonObject>().Single(v => v["properties"]!["disposition"]!["enum"]![0]!.ToString() == a.Disposition).DeepClone().AsObject();
                var fields = variant["properties"]!.AsObject();
                var direction = a.Disposition == "distinct_input" ? "input" : "output";
                var names = fields["name"]!["enum"]!.AsArray().Select(n => n!.ToString())
                    .Where(id => !occupied.Contains(CanonicalId(SourceName(state, id), a.WorkflowScope!, direction))).ToArray();
                var alternatives = new JsonArray(Rule("deferred_attachment"), Rule("not_a_declaration"), Rule("unresolved"));
                if (names.Length > 0)
                {
                    fields["name"] = PlanningHoleRequests.Enum(names);
                    fields["scope"] = PlanningHoleRequests.Enum([a.WorkflowScope!]);
                    fields["presence"] = PlanningHoleRequests.Enum([a.Presence]);
                    fields["declaration"] = PlanningHoleRequests.Enum([a.DeclarationReference!]);
                    fields["presenceEvidence"] = PlanningHoleRequests.Enum([a.PresenceReference!]);
                    alternatives.Add((JsonNode)variant);
                }
                return (a.CandidateId, new JsonObject { ["anyOf"] = alternatives });
            }).ToArray());
            var clauses = members.SelectMany(a => new[] { candidates[a.CandidateId].Grounding!.ClauseReference, a.DeclarationReference!, a.PresenceReference! })
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var lexical = clauses.SelectMany(id => Lexical(state, id)).DistinctBy(r => r.Id, StringComparer.Ordinal).OrderBy(r => r.Id, StringComparer.Ordinal);
            var context = new JsonObject
            {
                ["task"] = "Correct only these duplicate root claims from their complete governing evidence. Select the exact declared public name. Establish one root per public identity and defer overlapping evidence to attachment, or retire evidence that is not a declaration. Another distinct root requires evidence of another public name. Existing direction, scope and presence remain fixed.",
                ["candidates"] = new JsonObject(members.Select(a => new KeyValuePair<string, JsonNode?>(a.CandidateId, Assignment(a)))),
                ["evidence"] = new JsonObject(members.Select(a => new KeyValuePair<string, JsonNode?>(a.CandidateId, JsonValue.Create(PlanningSourceDecisions.Text(state, candidates[a.CandidateId]))))),
                ["clauses"] = new JsonObject(clauses.Select(id => new KeyValuePair<string, JsonNode?>(id, JsonValue.Create(PlanningChoiceEvidence.Text(state, id))))),
                ["tokens"] = new JsonObject(lexical.Select(r => new KeyValuePair<string, JsonNode?>(r.Id, JsonValue.Create(PlanningChoiceEvidence.Text(state, r.Id))))),
                ["frozenRoots"] = new JsonObject(frozen.Select(d => new KeyValuePair<string, JsonNode?>(d.Id, new JsonObject
                    { ["name"] = Name(state, d), ["direction"] = d.Direction, ["scope"] = d.WorkflowScope, ["presence"] = d.Required ? "required" : "optional" }))),
                ["conflicts"] = new JsonObject(conflicts.Where(c => c.Candidates.Any(group.Contains)).Select(c => new KeyValuePair<string, JsonNode?>(c.Identity,
                    new JsonArray(c.Candidates.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()))))
            };
            var id = "declarations_root_conflict_" + PlanningGraphCompiler.Fingerprint(new JsonArray(members.Select(a => (JsonNode?)JsonValue.Create(a.CandidateId)).ToArray()).ToJsonString())[..24];
            return new PlanningDecisionPages.Decision(id, schema, context, EvidenceFingerprint(state), SourceDecisionIds: owners);
        }).ToArray();
    }

    private static async Task<List<PlanningDeclarationAssignment>> CorrectDuplicateRootsAsync(PlanningSnapshot state, IPlanningRuntime runtime,
        List<PlanningDeclarationAssignment> initial, CancellationToken ct)
    {
        var decisions = RootCorrections(state, initial);
        if (decisions.Length == 0) return initial;
        var editable = decisions.SelectMany(d => d.Schema["properties"]!.AsObject().Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);
        var findings = editable.Order(StringComparer.Ordinal).Select(id => new PlanningDiagnostic("DECLARATION_PUBLIC_IDENTITY_CONFLICT",
            "/declarations/" + PlanningFieldPaths.Escape("@" + id), "This root claim conflicts with another public declaration identity.",
            ValidationStage: PlanningGates.Response, Rule: "duplicate_public_identity")).ToArray();
        var evaluation = "declaration-roots:" + PlanningGraphCompiler.Fingerprint(new JsonObject(initial.OrderBy(a => a.CandidateId, StringComparer.Ordinal)
            .Select(a => new KeyValuePair<string, JsonNode?>(a.CandidateId, Assignment(a)))).ToJsonString());
        PlanningConvergence.Failure(state, "$plan", PlanningGates.Response, evaluation, findings);
        var values = await PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "intent_declarations", "$plan", PlanningGates.Response, decisions, ct);
        var corrected = Parse(values);
        Coverage(editable, corrected);
        ValidateAssignments(decisions, corrected);
        var replacements = corrected.ToDictionary(a => a.CandidateId, StringComparer.Ordinal);
        var merged = initial.Select(a => replacements.GetValueOrDefault(a.CandidateId) ?? a).ToList();
        if (initial.SequenceEqual(merged)) throw Failure(editable.Order(StringComparer.Ordinal).First(), "The only duplicate-root correction made no progress.");
        // Never mutate completed initial pages. The correction receipt is a staged
        // delta; restart repeats this exact merge before attachment can begin.
        _ = ValidateRoots(state, merged);
        return merged;
    }
}
