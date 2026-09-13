using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningDeclarations
{
    internal sealed record BaselinePort(string Scope, string Direction, string Name, bool Required, PlanningPort? Input, PlanningOutput? Output);
    internal static Dictionary<string, BaselinePort> Baselines(PlanningSnapshot state)
    {
        var result = new Dictionary<string, BaselinePort>(StringComparer.Ordinal);
        if (state.Request.Baseline is not { } graph) return result;
        var hash = PlanningGraphCompiler.Fingerprint(graph);
        foreach (var workflow in graph.Workflows)
        {
            var scope = workflow.Key == graph.Entrypoint ? "main" : workflow.Key;
            foreach (var input in workflow.Inputs) Add(new(scope, "input", input.Name, input.Required, input, null));
            foreach (var output in workflow.Outputs) Add(new(scope, "output", output.Name, true, null, output));
        }
        return result;
        void Add(BaselinePort port)
        {
            var id = "bp_" + PlanningGraphCompiler.Fingerprint(state.Request.TenantId + ":" + state.Request.SessionId + ":" + hash + ":" + port.Scope + ":" + port.Direction + ":" + port.Name)[..24];
            if (!result.TryAdd(id, port)) throw Failure(id, "The baseline has duplicate public port identities.");
        }
    }

    internal static string EvidenceFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint("declarations-v2:" +
        state.Request.TenantId + ":" + state.Request.SessionId + ":" +
        string.Join('|', state.Obligations.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.Grounding?.Fingerprint)) + ":" +
        (state.Request.Baseline is { } graph ? PlanningGraphCompiler.Fingerprint(graph) : ""));

    internal static void Commit(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments, string evidenceFingerprint)
    {
        if (evidenceFingerprint != EvidenceFingerprint(state)) throw Failure("$plan", "The declaration evidence changed before adjudication.");
        var declarations = Materialize(state, assignments);
        var proof = Proof(evidenceFingerprint, assignments, declarations);
        state.DeclarationAssignments = assignments.OrderBy(a => a.CandidateId, StringComparer.Ordinal).ToList();
        state.Declarations = declarations;
        if (state.DeclarationFingerprint != proof)
        {
            foreach (var group in assignments.GroupBy(a => a.Disposition, StringComparer.Ordinal))
            {
                state.Events.Add(new("declaration_" + group.Key, PlanningPhase.Capabilities, DateTimeOffset.UtcNow, group.Count()));
                foreach (var item in group) System.Diagnostics.Activity.Current?.AddEvent(new("planning.declaration_adjudicated", tags: new()
                    { ["candidate_id"] = item.CandidateId, ["disposition"] = item.Disposition, ["target_id"] = item.TargetId }));
            }
        }
        state.DeclarationFingerprint = proof;
    }

    internal static void RequireCurrent(PlanningSnapshot state)
    {
        // With no source declarations and no baseline there is no public
        // declaration authority to validate (including engine-internal ports).
        if (Candidates(state).Length == 0 && state.Request.Baseline is null && state.DeclarationFingerprint is null) return;
        if (state.DeclarationFingerprint is null) throw Failure("$plan", "Public declarations require current adjudication before behavior review. Explicitly continue preparation to reassess them.");
        var derived = Materialize(state, state.DeclarationAssignments);
        if (state.DeclarationFingerprint != Proof(EvidenceFingerprint(state), state.DeclarationAssignments, derived) ||
            JsonSerializer.Serialize(derived, PlanningJsonContext.Default.ListPlanningBusinessDeclaration) != JsonSerializer.Serialize(state.Declarations, PlanningJsonContext.Default.ListPlanningBusinessDeclaration))
            throw Failure("$plan", "The declaration proof is stale or its canonical declarations changed.");
    }

    private static string Proof(string evidence, List<PlanningDeclarationAssignment> assignments, List<PlanningBusinessDeclaration> declarations)
        => PlanningGraphCompiler.Fingerprint(evidence + ":" + JsonSerializer.Serialize(assignments.OrderBy(a => a.CandidateId, StringComparer.Ordinal).ToList(), PlanningJsonContext.Default.ListPlanningDeclarationAssignment) + ":" +
            JsonSerializer.Serialize(declarations, PlanningJsonContext.Default.ListPlanningBusinessDeclaration));

    private static List<PlanningBusinessDeclaration> Materialize(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        var candidates = Candidates(state).ToDictionary(o => o.Id, StringComparer.Ordinal);
        var baseline = Baselines(state);
        if (assignments.Select(a => a.CandidateId).Distinct(StringComparer.Ordinal).Count() != assignments.Count ||
            !assignments.Select(a => a.CandidateId).ToHashSet(StringComparer.Ordinal).SetEquals(candidates.Keys))
            throw Failure("$plan", "Every issued declaration candidate requires exactly one assignment.");
        var chosen = assignments.ToDictionary(a => a.CandidateId, StringComparer.Ordinal);
        foreach (var page in Decisions(state))
        {
            var response = new JsonObject(page.Schema["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, Assignment(chosen[p.Key]))));
            if (PlanningContractValidation.ValidateInstance(response, page.Schema).Count != 0)
                throw Failure(chosen.Keys.Order(StringComparer.Ordinal).FirstOrDefault() ?? "$plan", "Declaration assignments contain unknown, foreign or out-of-scope references.");
        }
        foreach (var assignment in assignments)
            if (assignment.Disposition == "unresolved") throw Failure(assignment.CandidateId, "The complete governing evidence does not establish one declaration subject or its modifiers.");
        string? Root(string id, HashSet<string> path)
        {
            if (baseline.ContainsKey(id)) return id;
            if (!chosen.TryGetValue(id, out var assignment) || !path.Add(id)) throw Failure(id, "Declaration links contain a cycle or missing target.");
            if (assignment.Disposition == "not_a_declaration") return null;
            if (assignment.Disposition == "distinct") return id;
            var root = assignment.TargetId is { } target ? Root(target, path) : null;
            if (root is null) throw Failure(id, "An alias or modifier must resolve to a distinct declaration or baseline port.");
            return root;
        }
        var roots = assignments.ToDictionary(a => a.CandidateId, a => Root(a.CandidateId, new(StringComparer.Ordinal)), StringComparer.Ordinal);
        var result = new List<PlanningBusinessDeclaration>();
        foreach (var root in roots.Values.OfType<string>().Concat(baseline.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var inherited = baseline.GetValueOrDefault(root);
            var primary = inherited is null ? chosen[root] : null;
            var direction = inherited?.Direction ?? (candidates[root].Kind == "business_input" ? "input" : "output");
            var members = assignments.Where(a => roots[a.CandidateId] == root).OrderBy(a => a.CandidateId, StringComparer.Ordinal).ToArray();
            if (members.Any(a => a.Disposition is "same_as" or "modifier_of" &&
                (candidates[a.CandidateId].Kind == "omission_default" ? direction != "input" : candidates[a.CandidateId].Kind != (direction == "input" ? "business_input" : "business_output"))))
                throw Failure(root, "Declaration aliases and modifiers must preserve input/output direction.");
            if (members.Any(a => a.DefaultReference is not null && (candidates[a.CandidateId].Kind != "omission_default" || a.Disposition != "modifier_of" || a.Presence != "optional")))
                throw Failure(root, "Only an evidenced omission modifier can supply an optional input default.");
            var presence = members.Select(a => a.Presence).Where(p => p != "unspecified").Distinct(StringComparer.Ordinal).ToArray();
            if (presence.Length > 1 || inherited is null && presence.Length == 0)
                throw Failure(root, "Declaration requiredness is conflicting or unresolved.");
            var required = presence.Length == 0 ? inherited!.Required : presence[0] == "required";
            var defaults = members.Select(a => a.DefaultReference).OfType<string>().ToArray();
            var literalValues = defaults.Select(id => Literal(state, id)).ToArray();
            if (literalValues.Skip(1).Any(v => !JsonNode.DeepEquals(literalValues[0], v)) || defaults.Length > 0 && (required || direction != "input"))
                throw Failure(root, "Declaration defaults conflict with each other or with required presence.");
            if (inherited is not null && (required != inherited.Required || defaults.Length > 0 &&
                (inherited.Input?.Default is not { } previous || !PlanningGraphValidation.IsLiteral(previous) || !JsonNode.DeepEquals(PlanningGraphValidation.Literal(previous), literalValues[0]))))
                throw Failure(root, "A baseline port contract cannot be rewritten by a candidate alias.");
            var name = inherited?.Name ?? NameToken(PlanningChoiceEvidence.Text(state, primary!.NameReference!));
            if (string.IsNullOrWhiteSpace(name)) throw Failure(root, "The declared public name is unavailable or unsupported.");
            var scope = inherited?.Scope ?? primary!.WorkflowScope!;
            var clauses = members.Select(a => candidates[a.CandidateId].Grounding!.ClauseReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var id = "decl_" + PlanningGraphCompiler.Fingerprint(root + ":" + (primary?.NameReference ?? root) + ":" + scope + ":" + direction)[..24];
            var proof = PlanningGraphCompiler.Fingerprint(EvidenceFingerprint(state) + ":" + root + ":" + JsonSerializer.Serialize(members.ToList(), PlanningJsonContext.Default.ListPlanningDeclarationAssignment));
            result.Add(new(id, direction, scope, primary?.NameReference ?? root, inherited is null ? null : root, required,
                defaults.FirstOrDefault(), members.Where(a => a.Disposition is "distinct" or "same_as").Select(a => a.CandidateId).ToList(),
                members.Where(a => a.Disposition == "same_as").Select(a => a.CandidateId).ToList(),
                members.Where(a => a.Disposition == "modifier_of" || a.DefaultReference is not null || a.Presence != "unspecified")
                    .SelectMany(a => candidates[a.CandidateId].EvidenceReferences).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), clauses, proof));
        }
        if (result.GroupBy(d => (d.WorkflowScope, d.Direction, Name: Name(state, d))).Any(g => g.Count() > 1))
            throw Failure("$plan", "Distinct declarations claim the same public name and scope. Identity must be adjudicated explicitly.");
        return result;
    }

    internal static JsonObject Assignment(PlanningDeclarationAssignment a)
    {
        var result = new JsonObject { ["disposition"] = a.Disposition };
        if (a.Disposition == "distinct") { result["name"] = a.NameReference; result["scope"] = a.WorkflowScope; }
        if (a.Disposition is "same_as" or "modifier_of") result["target"] = a.TargetId;
        if (a.Disposition is "distinct" or "same_as" or "modifier_of") { result["presence"] = a.Presence; result["default"] = a.DefaultReference; }
        if (a.Disposition != "distinct" && (a.NameReference is not null || a.WorkflowScope is not null) ||
            a.Disposition is "distinct" or "not_a_declaration" or "unresolved" && a.TargetId is not null ||
            a.Disposition is "not_a_declaration" or "unresolved" && (a.Presence != "unspecified" || a.DefaultReference is not null))
            throw Failure(a.CandidateId, "The disposition contains unsupported companion fields.");
        return result;
    }

    internal static string Name(PlanningSnapshot state, PlanningBusinessDeclaration declaration) => declaration.BaselineReference is { } baseline
        ? Baselines(state).GetValueOrDefault(baseline)?.Name ?? throw Failure(declaration.Id, "The baseline name reference is stale.")
        : NameToken(PlanningChoiceEvidence.Text(state, declaration.NameReference)) ?? throw Failure(declaration.Id, "The public name is not a supported lexical token.");
    private static string? NameToken(string token)
    {
        if (token.StartsWith('"') && TryLiteral(token, out var value) && value is JsonValue text && text.TryGetValue<string>(out var name)) return string.IsNullOrWhiteSpace(name) ? null : name;
        return Regex.IsMatch(token, @"\A[\p{L}_][\p{L}\p{N}_-]*\z") ? token : null;
    }
    private static bool TryLiteral(string token, out JsonNode? value)
    {
        try { value = JsonNode.Parse(token); return true; }
        catch (JsonException) { value = null; return false; }
    }
    private static JsonNode? Literal(PlanningSnapshot state, string reference)
        => TryLiteral(PlanningChoiceEvidence.Text(state, reference), out var value) ? value : throw Failure(reference, "The selected default is not an exact JSON literal.");
    internal static PlanningValue? Default(PlanningSnapshot state, PlanningBusinessDeclaration declaration)
        => declaration.DefaultReference is { } reference ? PlanningJsonTransport.Literal(Literal(state, reference))
            : declaration.BaselineReference is { } baseline && Baselines(state)[baseline].Input?.Default is { } previous
                ? JsonSerializer.Deserialize(JsonSerializer.Serialize(previous, PlanningJsonContext.Default.PlanningValue), PlanningJsonContext.Default.PlanningValue) : null;
    internal static PlanningBehaviorPort Port(PlanningSnapshot state, PlanningBusinessDeclaration declaration) => new(Name(state, declaration),
        declaration.ClauseReferences.Count == 0 ? Name(state, declaration) : string.Join("\n", declaration.ClauseReferences.Select(id => PlanningChoiceEvidence.Text(state, id))), declaration.Required)
        { DeclarationId = declaration.Id };
    internal static string? CanonicalInput(PlanningSnapshot state, string candidate) => state.Declarations.SingleOrDefault(d => d.Direction == "input" && (d.Candidates.Contains(candidate) || d.Id == candidate))?.Id;
    internal static WorkflowRuntimeException Failure(string id, string message) => new("DECLARATION_GROUNDING_UNRESOLVED", message,
        details: new JsonObject { ["location"] = "/declarations/" + PlanningFieldPaths.Escape("@" + id) });
}
