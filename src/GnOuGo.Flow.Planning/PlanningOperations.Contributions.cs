using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // A clause is the indivisible semantic scope. Runtime records remain preliminary provenance.
    internal static string ContributionDecisionId(PlanningRuntimeEvidence evidence) => "contribution_clause_" +
        PlanningGraphCompiler.Fingerprint(evidence.ClauseReference)[..24];
    internal static Scope[] ContributionMembers(PlanningSnapshot state, Scope scope) => DeriveScopes(state)
        .Where(s => s.Evidence!.BaselineReference is null && s.Clause.Id == scope.Clause.Id)
        .OrderBy(s => s.Evidence!.Id, StringComparer.Ordinal).ToArray();

    private static JsonObject ContributionOwnedSchema(PlanningSnapshot state, Scope[] members)
    {
        var choices = new JsonArray();
        foreach (var member in members)
        {
            choices.Add((JsonNode)PlanningHoleRequests.Enum([member.Evidence!.ActionReference!]));
            choices.Add((JsonNode)ContributionBoundaries(state, member).Schema);
        }
        return new JsonObject { ["anyOf"] = choices };
    }

    internal static PlanningDecisionPages.Decision ContributionDecision(PlanningSnapshot state, Scope scope)
    {
        RequireEligibleContribution(state, scope.Evidence!);
        var members = ContributionMembers(state, scope);
        if (members.Length == 0) throw new InvalidOperationException("Exact baseline execution is engine qualified.");
        foreach (var member in members) RequireEligibleContribution(state, member.Evidence!);
        var owned = ContributionOwnedSchema(state, members);
        var alternatives = new JsonArray(
            PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["excluded"])),
                ("scope", PlanningHoleRequests.Enum([scope.Clause.Id])), ("basis", PlanningHoleRequests.Enum(["no_operation_relevance"])), ("evidence", owned.DeepClone().AsObject())),
            PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["governing_property"])),
                ("scope", PlanningHoleRequests.Enum([scope.Clause.Id])), ("evidence", owned.DeepClone().AsObject())));
        var domains = members.ToDictionary(s => s.Evidence!.Id, s => EffectDomain(state, s.Evidence!), StringComparer.Ordinal);
        var domain = domains.Values.SelectMany(d => d).GroupBy(p => p.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        foreach (var (id, anchor) in domain)
        {
            if (anchor.BoundaryKind != "result_realization" && anchor.OccurrenceProof is null) continue;
            var eligible = members.Where(s => domains[s.Evidence!.Id].ContainsKey(id)).ToArray();
            var support = ContributionOwnedSchema(state, eligible);
            alternatives.Add((JsonNode)PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["requested_execution"])),
                ("scope", PlanningHoleRequests.Enum([scope.Clause.Id])), ("predicate", support.DeepClone().AsObject()),
                ("evidence", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4, ["items"] = support }),
                ("effect", PlanningHoleRequests.Enum([id])),
                ("basis", PlanningHoleRequests.Enum([anchor.BoundaryKind == "result_realization" ? "requested_result_production" : "requested_owned_occurrence"])),
                ("owner", PlanningHoleRequests.Enum([anchor.OwnerReference])), ("boundary", PlanningHoleRequests.Enum([anchor.BoundaryReference]))));
        }
        var schema = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))),
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["qualified"])), ("units", new JsonObject
            { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 6, ["items"] = new JsonObject { ["anyOf"] = alternatives } }))) };
        var context = new JsonObject
        {
            ["stage"] = "execution_contribution", ["scope"] = scope.Clause.Id,
            ["task"] = "Qualify the complete clause jointly. Requested execution must bind its owned request/predicate to its execution and subordinate result-selection evidence, effect and positive basis. Fragments cannot independently request execution. Retain the relationship between the complete statement and its parts. Governing properties and irrelevant units have no execution authority. These units can coexist; account for all issued evidence without contradictory overlaps. Semantic labels are nonexclusive context. Execution facts, necessity, an output or a candidate boundary alone do not prove requested performance. Governing qualification is unbound; applicability is resolved after realization. Unknown meaning or incomplete accounting requires unresolved.",
            ["clause"] = PlanningChoiceEvidence.Text(state, scope.Clause.Id), ["boundaries"] = scope.Words.DeepClone(),
            ["provenance"] = new JsonObject(members.Select(s => new KeyValuePair<string, JsonNode?>(s.Evidence!.Id, new JsonObject
            { ["reference"] = s.Evidence.ActionReference, ["span"] = PlanningChoiceEvidence.Text(state, s.Evidence.ActionReference!),
                ["kind"] = s.Evidence.Kind, ["necessity"] = s.Evidence.Necessity.ToString(),
                ["effects"] = CoverageStrings(domains[s.Evidence.Id].Keys) }))),
            ["effects"] = new JsonObject(domain.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
            { ["scope"] = p.Value.WorkflowScope, ["owner"] = p.Value.OwnerReference, ["boundary"] = p.Value.BoundaryKind,
                ["boundaryReference"] = p.Value.BoundaryReference, ["ownershipProof"] = p.Value.OccurrenceProof?.Fingerprint,
                ["result"] = state.Declarations.SingleOrDefault(d => d.Id == p.Value.OwnerReference) is { } declaration ? PlanningDeclarations.Name(state, declaration) : null })))
        };
        return new(ContributionDecisionId(scope.Evidence!), schema, context,
            PlanningGraphCompiler.Fingerprint("execution-contribution-v4:" + EvidenceFingerprint(state) + ":" +
                string.Join('|', members.Select(s => s.Evidence!.ProofFingerprint)) + ":" + schema.ToJsonString() + ":" + context.ToJsonString()),
            SourceDecisionIds: members.Select(s => "contribution_" + s.Evidence!.Id).Append(ContributionDecisionId(scope.Evidence!)).Order(StringComparer.Ordinal).ToArray());
    }

    private static (JsonObject Context, JsonObject Schema, Func<string, string, PlanningReference> Select) ContributionBoundaries(PlanningSnapshot state, Scope scope)
    {
        var parent = state.References.Single(r => r.Id == scope.Evidence!.ActionReference);
        var starts = scope.Boundaries["properties"]!["start"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
        var ends = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
        var from = starts.Where(b => scope.Select(b, ends[^1]).Start is var start && start >= parent.Start && start < parent.Start + parent.Length).ToArray();
        var to = ends.Where(b => scope.Select(starts[0], b) is var span && span.Start + span.Length > parent.Start && span.Start + span.Length <= parent.Start + parent.Length).ToArray();
        PlanningReference Select(string start, string end)
        {
            var span = scope.Select(start, end);
            if (!from.Contains(start) || !to.Contains(end) || span.Start < parent.Start || span.Start + span.Length > parent.Start + parent.Length)
                throw Failure(scope.Evidence!.Id, "Contribution evidence is outside its exact runtime parent.");
            return span;
        }
        return (scope.Words, PlanningHoleRequests.Object(("start", PlanningHoleRequests.Enum(from)), ("end", PlanningHoleRequests.Enum(to))), Select);
    }

    internal static PlanningExecutionContributionProof ParseContributions(PlanningSnapshot state, Scope scope, JsonObject answer)
    {
        var decision = ContributionDecision(state, scope);
        if (PlanningContractValidation.ValidateInstance(answer, decision.Schema).Count != 0)
            throw Failure(scope.Clause.Id, "Clause qualification changed its request links, owned evidence, effect bindings or support bases.");
        if (answer["status"]!.ToString() != "qualified") throw Failure(scope.Clause.Id, "Executable contribution authority is unresolved.");
        var members = ContributionMembers(state, scope);
        var selected = new Dictionary<string, PlanningReference>(StringComparer.Ordinal);
        PlanningReference Select(JsonNode value)
        {
            var span = value is JsonObject range ? scope.Select(range["start"]!.ToString(), range["end"]!.ToString())
                : state.References.Single(r => r.Id == value.ToString());
            var owners = members.Where(s => ContainsSpan(state.References.Single(r => r.Id == s.Evidence!.ActionReference), span)).ToArray();
            if (owners.Length == 0) throw Failure(scope.Clause.Id, "Request qualification selected evidence outside the eligible owned domain.");
            foreach (var owner in owners) RequireEligibleContribution(state, owner.Evidence!, span);
            // Normalize by owned coordinates, independent of notation and runtime enumeration.
            var exact = members.Select(s => state.References.Single(r => r.Id == s.Evidence!.ActionReference))
                .Where(r => r.Start == span.Start && r.Length == span.Length).OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            if (exact is not null) span = exact;
            selected[span.Id] = span;
            return span;
        }
        var units = new List<PlanningContributionUnit>();
        var contributions = new List<PlanningExecutionContribution>();
        foreach (var node in answer["units"]!.AsArray())
        {
            var value = node!;
            var execution = value["role"]!.ToString() == "requested_execution";
            var spans = execution ? value["evidence"]!.AsArray().Select(v => Select(v!)).DistinctBy(r => r.Id).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray() : [Select(value["evidence"]!)];
            var predicate = execution ? Select(value["predicate"]!) : null;
            if (predicate is not null && !Enumerable.Range(predicate.Start, predicate.Length).All(i => char.IsWhiteSpace(scope.Source.Text[i]) || spans.Any(r => r.Start <= i && i < r.Start + r.Length)))
                throw Failure(scope.Clause.Id, "Requested execution must account for its predicate in its support projections.");
            var parents = members.Where(s => spans.Any(r => Overlaps(state.References.Single(p => p.Id == s.Evidence!.ActionReference), r))).ToArray();
            if (execution)
            {
                // A separately owned occurrence may overlap the same source clause.
                // Bind every compatible record for this effect; other occurrences must
                // still receive their own complete accounting below.
                parents = parents.Where(p => EffectDomain(state, p.Evidence!).ContainsKey(value["effect"]!.ToString())).ToArray();
                if (!spans.Append(predicate!).All(r => parents.Any(p => ContainsSpan(state.References.Single(x => x.Id == p.Evidence!.ActionReference), r))))
                    throw Failure(scope.Clause.Id, "Requested execution has no owned provenance for its selected effect.");
                RequireCompatibleContributionFacts(parents.Select(s => s.Evidence!).ToArray(), scope.Clause.Id);
            }
            var bindings = parents.Select(s => s.Evidence!.Id).Order(StringComparer.Ordinal).ToList();
            var unit = new PlanningContributionUnit("", value["role"]!.ToString(), scope.Clause.Id, predicate?.Id,
                spans.Select(r => r.Id).ToList(), bindings, value["effect"]?.ToString(),
                value["basis"]?.ToString() ?? "governing_property", value["owner"]?.ToString(), value["boundary"]?.ToString());
            unit = unit with { Id = "unit_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(unit, PlanningJsonContext.Default.PlanningContributionUnit))[..24] };
            units.Add(unit);
            foreach (var span in spans)
                contributions.Add(SealContribution(scope.Clause.Id, new("", span.Id, execution ? "supports" : unit.Role,
                    unit.EffectId, unit.Basis, unit.OwnerReference, unit.BoundaryReference, PlanningContributionOrigin.ModelQualification)
                { UnitId = unit.Id, RuntimeEvidenceIds = bindings }));
        }
        foreach (var member in members)
        {
            var parent = state.References.Single(r => r.Id == member.Evidence!.ActionReference);
            if (!Covered(state, parent, units.Where(u => u.RuntimeEvidenceIds.Contains(member.Evidence!.Id)).SelectMany(u => u.EvidenceReferences).Select(id => selected[id]).ToArray()))
                throw Failure(scope.Clause.Id, "Clause qualification did not cover all of its owned runtime evidence.");
        }
        foreach (var a in contributions)
        foreach (var b in contributions.Where(b => b.Role != a.Role))
            if (Overlaps(selected[a.EvidenceReference], selected[b.EvidenceReference]))
                throw Failure(scope.Clause.Id, "Clause semantic units give contradictory authority to overlapping evidence.");
        foreach (var span in selected.Values)
            if (!state.References.Any(r => r.Id == span.Id)) state.References.Add(span);
        return SealContributionProof(new(4, members[0].Evidence!.Id, decision.Id, decision.EvidenceFingerprint,
            contributions.DistinctBy(c => c.Id).OrderBy(c => c.Id, StringComparer.Ordinal).ToList(), "")
        { ClauseReference = scope.Clause.Id, RuntimeEvidenceIds = members.Select(s => s.Evidence!.Id).ToList(),
            Units = units.DistinctBy(u => u.Id).OrderBy(u => u.Id, StringComparer.Ordinal).ToList() });
    }

    private static PlanningExecutionContribution SealContribution(string parent, PlanningExecutionContribution value) => value with
    { Id = "contribution_" + PlanningGraphCompiler.Fingerprint(parent + ":" + JsonSerializer.Serialize(value with { Id = "" }, PlanningJsonContext.Default.PlanningExecutionContribution))[..24] };
    private static PlanningExecutionContributionProof SealContributionProof(PlanningExecutionContributionProof proof) => proof with
    { ProofFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(proof with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningExecutionContributionProof)) };

    private static PlanningExecutionContributionProof DeterministicContribution(PlanningSnapshot state, PlanningRuntimeEvidence evidence, bool excluded)
    {
        KeyValuePair<string, PlanningOperationEffectAnchor>? anchor = excluded ? null : EffectDomain(state, evidence).Single();
        var structural = !excluded && PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == state.References.Single(r => r.Id == evidence.SourceReference).SourceId).Structural;
        var exclusion = excluded ? ContractExclusions(state).GetValueOrDefault(evidence.Id) : null;
        var basis = excluded ? exclusion is { SeparateEvidence.Length: > 0 } ? "canonical_contract_separation" : "canonical_source_or_declaration_exclusion" : structural ? "existing_baseline_execution" : "baseline_owner_annotation";
        var reference = evidence.ActionReference ?? evidence.SourceReference;
        var unit = new PlanningContributionUnit("unit_" + evidence.Id, structural ? "requested_execution" : excluded ? "excluded" : "governing_property",
            evidence.ClauseReference, structural ? reference : null, [reference], [evidence.Id], structural ? anchor?.Key : null, basis, anchor?.Value.OwnerReference, anchor?.Value.BoundaryReference);
        var item = SealContribution(evidence.Id, new("", reference, excluded ? "excluded" : structural ? "supports" : "governing_property",
            structural ? anchor?.Key : null, basis, anchor?.Value.OwnerReference, anchor?.Value.BoundaryReference,
            excluded ? PlanningContributionOrigin.DeterministicExclusion : PlanningContributionOrigin.DeterministicBaseline)
        { UnitId = unit.Id, RuntimeEvidenceIds = [evidence.Id] });
        return SealContributionProof(new(4, evidence.Id, null, PlanningGraphCompiler.Fingerprint("execution-contribution-v4:" +
            EvidenceFingerprint(state) + ":" + evidence.ProofFingerprint + ":" + state.DeclarationFingerprint), [item], "")
        { ClauseReference = evidence.ClauseReference, RuntimeEvidenceIds = [evidence.Id], Units = [unit] });
    }

    internal static PlanningDecisionPages.Decision[] ContributionDecisions(PlanningSnapshot state) => DeriveScopes(state)
        .Where(s => s.Evidence!.BaselineReference is null).GroupBy(s => s.Clause.Id, StringComparer.Ordinal)
        .Select(g => ContributionDecision(state, g.First())).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();

    internal static PlanningExecutionContributionProof[] ReadContributions(PlanningSnapshot state)
    {
        var scopes = DeriveScopes(state).ToDictionary(s => s.Evidence!.Id, StringComparer.Ordinal);
        var decisions = ContributionDecisions(state);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions); }
        catch (PlanningConflictException) { throw Failure("$plan", "Current canonical contribution qualification is missing.", "INTENT_OPERATION_PROOF_MISSING"); }
        var proofs = state.RuntimeEvidence.Where(e => !scopes.ContainsKey(e.Id) || e.BaselineReference is not null)
            .Select(e => DeterministicContribution(state, e, !scopes.ContainsKey(e.Id))).ToList();
        proofs.AddRange(scopes.Values.Where(s => s.Evidence!.BaselineReference is null).GroupBy(s => s.Clause.Id, StringComparer.Ordinal)
            .Select(g => ParseContributions(state, g.First(), values[ContributionDecisionId(g.First().Evidence!)]!.AsObject())));
        return proofs.OrderBy(p => p.ClauseReference, StringComparer.Ordinal).ThenBy(p => p.RuntimeEvidenceId, StringComparer.Ordinal).ToArray();
    }

    private static async Task GroundContributions(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", ContributionDecisions(state), ct);
        _ = ReadContributions(state);
    }

    internal static Scope[] QualifiedScopes(PlanningSnapshot state)
    {
        var scopes = DeriveScopes(state).ToDictionary(s => s.Evidence!.Id, StringComparer.Ordinal);
        return ReadContributions(state).SelectMany(p => p.Contributions.Where(c => c.Role != "excluded")
            .Select(c => scopes[c.RuntimeEvidenceIds[0]] with { Contribution = c, Unit = p.Units.Single(u => u.Id == c.UnitId) }))
            .OrderBy(s => s.Contribution!.Id, StringComparer.Ordinal).ToArray();
    }

    private static PlanningRuntimeEvidence[] AssignmentEvidence(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        if (assignment.RuntimeEvidenceIds.Count == 0) throw Failure(assignment.ClauseReference, "Current runtime provenance is missing.");
        return assignment.RuntimeEvidenceIds.Select(id => state.RuntimeEvidence.SingleOrDefault(e => e.Id == id)
            ?? throw Failure(assignment.ClauseReference, "Foreign runtime provenance.")).ToArray();
    }

    private static PlanningRuntimeEvidence[] ContributionEvidence(PlanningSnapshot state, Scope scope) =>
        (scope.Contribution?.RuntimeEvidenceIds ?? [scope.Evidence!.Id]).Select(id => state.RuntimeEvidence.Single(e => e.Id == id)).ToArray();
    private static void RequireCompatibleContributionFacts(PlanningRuntimeEvidence[] evidence, string location)
    {
        if (evidence.Length == 0 || evidence.Any(e => !CompatibleFacts(evidence[0], e) || evidence[0].ResourceReference != e.ResourceReference))
            throw Failure(location, "The contribution has conflicting execution ownership or facts.");
        _ = ContributionNecessity(evidence, location);
    }
    private static PlanningOperationNecessity ContributionNecessity(IEnumerable<PlanningRuntimeEvidence> evidence, string location)
    {
        var facts = evidence.Select(e => e.Necessity).Where(n => n != PlanningOperationNecessity.Unspecified).Distinct().ToArray();
        if (facts.Length > 1) throw Failure(location, "The same runtime occurrence has conflicting explicit required and optional evidence.");
        return facts.Length == 0 ? PlanningOperationNecessity.Unspecified : facts[0];
    }
}
