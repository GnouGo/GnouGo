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

    internal static string EvidenceFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint("declarations-v5:" +
        state.Request.TenantId + ":" + state.Request.SessionId + ":" +
        string.Join('|', state.Obligations.Where(o => o.OperationAdmission is null).OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.Grounding?.Fingerprint)) + ":" +
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
        if (state.Obligations.Any(o => o.Kind is "business_input" or "business_output"))
            throw Failure("$plan", "Historical directional interpretations require explicit reassessment with current grounding proof.");
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

    internal static string CanonicalId(string name, string scope, string direction)
        => "decl_" + PlanningGraphCompiler.Fingerprint("public-declaration-v1:" + new JsonArray(direction, scope, name).ToJsonString())[..24];

    private static void ValidateAssignments(IEnumerable<PlanningDecisionPages.Decision> pages, List<PlanningDeclarationAssignment> assignments)
    {
        var chosen = assignments.ToDictionary(a => a.CandidateId, StringComparer.Ordinal);
        foreach (var page in pages)
        foreach (var field in page.Schema["properties"]!.AsObject())
            if (!chosen.TryGetValue(field.Key, out var value) || PlanningContractValidation.ValidateInstance(Assignment(value), field.Value!.AsObject()).Count != 0)
                throw Failure(field.Key, "Declaration assignments contain incomplete proof, unknown or out-of-scope references.");
        foreach (var assignment in assignments)
            if (assignment.Disposition == "unresolved") throw Failure(assignment.CandidateId, "The complete governing evidence does not establish one declaration subject or its modifiers.");
    }

    private static void Coverage(IEnumerable<string> candidates, List<PlanningDeclarationAssignment> assignments)
    {
        if (assignments.Select(a => a.CandidateId).Distinct(StringComparer.Ordinal).Count() != assignments.Count ||
            !assignments.Select(a => a.CandidateId).ToHashSet(StringComparer.Ordinal).SetEquals(candidates))
            throw Failure("$plan", "Every issued declaration candidate requires exactly one assignment.");
    }

    private static List<PlanningBusinessDeclaration> ValidateRoots(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments)
    {
        var result = AssessRoots(state, assignments);
        var conflicts = RootConflicts(result);
        if (conflicts.Count > 0) throw RootFailure(conflicts[0], "duplicate_public_identity",
            "Distinct declarations claim the same public name and scope. Identity must be adjudicated explicitly.");
        return result;
    }

    private static List<PlanningBusinessDeclaration> AssessRoots(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        var candidates = Candidates(state).Where(IsCandidate).ToDictionary(o => o.Id, StringComparer.Ordinal);
        Coverage(candidates.Keys, assignments);
        ValidateAssignments(Decisions(state), assignments);
        var result = new List<PlanningBusinessDeclaration>();
        foreach (var (id, port) in Baselines(state))
            result.Add(new(CanonicalId(port.Name, port.Scope, port.Direction), port.Direction, port.Scope, id, id, port.Required,
                null, [], [], [], [], PlanningGraphCompiler.Fingerprint(EvidenceFingerprint(state) + ":" + id)));
        foreach (var root in assignments.Where(IsDistinct))
        {
            var direction = root.Disposition == "distinct_input" ? "input" : "output";
            var scope = root.WorkflowScope!;
            var clauses = new[] { candidates[root.CandidateId].Grounding!.ClauseReference, root.DeclarationReference!, root.PresenceReference! }
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            result.Add(new(CanonicalId(SourceName(state, root.NameReference!), scope, direction), direction, scope, root.NameReference!, null,
                root.Presence == "required", null, [root.CandidateId], [], [], clauses,
                PlanningGraphCompiler.Fingerprint(EvidenceFingerprint(state) + ":" + JsonSerializer.Serialize(root, PlanningJsonContext.Default.PlanningDeclarationAssignment))));
        }
        return result.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    private static List<PlanningBusinessDeclaration> Materialize(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        var candidates = Candidates(state).ToDictionary(o => o.Id, StringComparer.Ordinal);
        Coverage(candidates.Keys, assignments);
        // Reject hidden companion fields before deriving the staging projection.
        foreach (var assignment in assignments) _ = Assignment(assignment);
        if (assignments.Any(a => a.Disposition == "deferred_attachment"))
            throw Failure("$plan", "Deferred attachments are staging decisions and cannot grant declaration authority.");
        var rootAssignments = assignments.Where(a => IsCandidate(candidates[a.CandidateId])).Select(RootAssignment).ToList();
        var roots = ValidateRoots(state, rootAssignments);
        var linked = assignments.Where(a => a.Disposition is "same_as" or "modifier_of" || candidates[a.CandidateId].Kind is "omission_default" or "declaration_constraint").ToList();
        ValidateAssignments(AttachmentDecisions(state, rootAssignments), linked);
        var result = new List<PlanningBusinessDeclaration>();
        foreach (var root in roots)
        {
            var members = assignments.Where(a => root.Candidates.Contains(a.CandidateId) || a.TargetId == root.Id)
                .OrderBy(a => a.CandidateId, StringComparer.Ordinal).ToArray();
            var defaults = members.Select(a => a.DefaultReference).OfType<string>().ToArray();
            var literalValues = defaults.Select(id => Literal(state, id)).ToArray();
            if (literalValues.Skip(1).Any(v => !JsonNode.DeepEquals(literalValues[0], v)) || defaults.Length > 0 && (root.Required || root.Direction != "input"))
                throw Failure(root.Id, "Declaration defaults conflict with each other or with required presence.");
            var clauses = root.ClauseReferences.Concat(members.Select(a => candidates[a.CandidateId].Grounding!.ClauseReference))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            result.Add(root with
            {
                DefaultReference = defaults.FirstOrDefault(),
                Candidates = members.Where(a => IsDistinct(a) || a.Disposition == "same_as").Select(a => a.CandidateId).ToList(),
                Aliases = members.Where(a => a.Disposition == "same_as").Select(a => a.CandidateId).ToList(),
                ModifierReferences = members.Where(a => a.Disposition == "modifier_of").SelectMany(a => candidates[a.CandidateId].EvidenceReferences)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                ClauseReferences = clauses,
                ProofFingerprint = PlanningGraphCompiler.Fingerprint(root.ProofFingerprint + ":" + JsonSerializer.Serialize(members.ToList(), PlanningJsonContext.Default.ListPlanningDeclarationAssignment))
            });
        }
        return result;
    }

    internal static JsonObject Assignment(PlanningDeclarationAssignment a)
    {
        var distinct = IsDistinct(a);
        var link = a.Disposition is "same_as" or "modifier_of";
        var result = new JsonObject { ["disposition"] = a.Disposition };
        if (distinct)
        {
            result["name"] = a.NameReference; result["scope"] = a.WorkflowScope;
            result["declaration"] = a.DeclarationReference; result["presenceEvidence"] = a.PresenceReference;
        }
        if (link) result["target"] = a.TargetId;
        if (distinct || link) { result["presence"] = a.Presence; result["default"] = a.DefaultReference; }
        if (!distinct && (a.NameReference is not null || a.WorkflowScope is not null || a.DeclarationReference is not null || a.PresenceReference is not null) ||
            !link && a.TargetId is not null || !distinct && !link && (a.Presence != "unspecified" || a.DefaultReference is not null))
            throw Failure(a.CandidateId, "The disposition contains unsupported companion fields.");
        return result;
    }

    internal static string Name(PlanningSnapshot state, PlanningBusinessDeclaration declaration) => declaration.BaselineReference is { } baseline
        ? Baselines(state).GetValueOrDefault(baseline)?.Name ?? throw Failure(declaration.Id, "The baseline name reference is stale.")
        : SourceName(state, declaration.NameReference);
    internal static string SourceName(PlanningSnapshot state, string reference)
        => NameToken(PlanningChoiceEvidence.Text(state, reference)) ?? throw Failure(reference, "The public name is not a supported lexical token.");
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
