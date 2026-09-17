using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    private const int MaxContributionUnits = 6;
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
        if (scope.Evidence is not null) RequireEligibleContribution(state, scope.Evidence);
        var members = ContributionMembers(state, scope);
        foreach (var member in members) RequireEligibleContribution(state, member.Evidence!);
        var definitions = new JsonObject { ["owned"] = ContributionSourceSchema(state, scope, members) };
        var owned = new JsonObject { ["$ref"] = "#/$defs/owned" };
        var alternatives = new JsonArray(
            PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["excluded"])),
                ("scope", PlanningHoleRequests.Enum([scope.Clause.Id])), ("basis", PlanningHoleRequests.Enum(["no_operation_relevance"])), ("evidence", owned.DeepClone().AsObject())),
            PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["governing_property"])),
                ("scope", PlanningHoleRequests.Enum([scope.Clause.Id])), ("governingKind", PlanningHoleRequests.Enum(GoverningKinds)),
                ("evidence", owned.DeepClone().AsObject())));
        var domains = members.ToDictionary(s => s.Evidence!.Id, s => EffectDomain(state, s.Evidence!), StringComparer.Ordinal);
        var domain = domains.Values.SelectMany(d => d).GroupBy(p => p.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var requests = new JsonArray();
        var domainIndex = 0;
        foreach (var (id, anchor) in domain)
        {
            if (anchor.BoundaryKind != "result_realization" && anchor.OccurrenceProof is null) continue;
            var eligible = members.Where(s => domains[s.Evidence!.Id].ContainsKey(id)).ToArray();
            var supportName = "e" + domainIndex++;
            definitions[supportName] = ContributionOwnedSchema(state, eligible);
            var support = new JsonObject { ["$ref"] = "#/$defs/" + supportName };
            requests.Add((JsonNode)PlanningHoleRequests.Object(
                ("predicate", support.DeepClone().AsObject()),
                ("evidence", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4, ["items"] = support }),
                ("effect", PlanningHoleRequests.Enum([id])),
                ("basis", PlanningHoleRequests.Enum([anchor.BoundaryKind == "result_realization" ? "requested_result_production" : "requested_owned_occurrence"])),
                ("owner", PlanningHoleRequests.Enum([anchor.OwnerReference])), ("boundary", PlanningHoleRequests.Enum([anchor.BoundaryReference]))));
        }
        if (requests.Count != 0)
        {
            definitions["request"] = new JsonObject { ["anyOf"] = requests };
            alternatives.Add((JsonNode)PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["requested_execution"])),
                ("request", new JsonObject { ["$ref"] = "#/$defs/request" }),
                ("qualifiers", new JsonObject { ["type"] = "array", ["minItems"] = 0, ["maxItems"] = MaxContributionUnits - 1,
                    ["items"] = PlanningHoleRequests.Object(("evidence", owned.DeepClone().AsObject()),
                        ("governingKind", PlanningHoleRequests.Enum(GoverningKinds))) })));
        }
        // Bound each cardinality alternative without multiplying six parents by five
        // children. The parser additionally enforces the shared six-unit allowance.
        definitions["excluded"] = alternatives[0]!.DeepClone();
        definitions["property"] = alternatives[1]!.DeepClone();
        alternatives[0] = new JsonObject { ["$ref"] = "#/$defs/excluded" };
        alternatives[1] = new JsonObject { ["$ref"] = "#/$defs/property" };
        var shapes = new JsonArray();
        for (var count = domain.Count == 0 ? MaxContributionUnits : 1; count <= MaxContributionUnits; count++)
        {
            var choices = alternatives.DeepClone().AsArray();
            foreach (var choice in choices.OfType<JsonObject>())
                if (choice["properties"]?["qualifiers"] is JsonObject qualifiers)
                    qualifiers["maxItems"] = MaxContributionUnits - count;
            definitions["units_" + count] = new JsonObject { ["anyOf"] = choices };
            shapes.Add((JsonNode)new JsonObject { ["type"] = "array", ["minItems"] = domain.Count == 0 ? 1 : count, ["maxItems"] = count,
                ["items"] = new JsonObject { ["$ref"] = "#/$defs/units_" + count } });
        }
        var schema = new JsonObject { ["$defs"] = definitions, ["anyOf"] = new JsonArray(
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))),
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["qualified"])),
                ("units", new JsonObject { ["anyOf"] = shapes }))) };
        var context = new JsonObject
        {
            ["stage"] = "execution_contribution", ["scope"] = scope.Clause.Id,
            ["task"] = "Qualify the complete clause jointly. Requested execution must bind its owned request/predicate to its execution and subordinate result-selection evidence, effect and positive basis. Fragments cannot independently request execution. Retain the relationship between the complete statement and its parts. Governing properties and irrelevant units have no execution authority. These units can coexist; account for all issued evidence without contradictory overlaps. Semantic labels are nonexclusive context. Execution facts, necessity, an output or a candidate boundary alone do not prove requested performance. Governing qualification is unbound; applicability is resolved after realization. Unknown meaning or incomplete accounting requires unresolved.",
            ["composition"] = "A requested execution may retain its complete evidence and contain governing qualifiers. List contained properties in its qualifiers, not as overlapping peer units. Each qualifier must be properly contained in an owned support reference; their union cannot exhaust the request predicate or execution evidence. Qualifiers supply no executable support. Nesting records composition only, not applicability. Standalone properties remain unbound. At most six units total, including qualifiers.",
            ["clause"] = PlanningChoiceEvidence.Text(state, scope.Clause.Id), ["boundaries"] = scope.Words.DeepClone(),
            ["sourceOwnership"] = "Account for every non-whitespace part of the owned clause through qualified units or the issued canonical contracts. Additional clause references can qualify governing rules, conditions, runtime fallbacks, properties, or no operation relevance only. They cannot supply requested execution, effect identity, necessity or applicability. Runtime fallback is executable behavior evidence, not a declaration omission default. Preserve existing owned condition/fallback kinds. Unaccounted or unknown meaning requires unresolved.",
            ["semanticEvidence"] = new JsonArray(ContributionObligations(state, scope).Select(o => (JsonNode)new JsonObject
            { ["id"] = o.Id, ["kind"] = o.Kind, ["references"] = CoverageStrings(o.EvidenceReferences), ["grounding"] = o.Grounding!.Fingerprint }).ToArray()),
            ["contracts"] = CoverageStrings(ContractCoverage(state).Where(c => Overlaps(scope.Clause, c.Span)).Select(c => c.Span.Id)),
            ["provenance"] = new JsonObject(members.Select(s => new KeyValuePair<string, JsonNode?>(s.Evidence!.Id, new JsonObject
            { ["reference"] = s.Evidence.ActionReference, ["span"] = PlanningChoiceEvidence.Text(state, s.Evidence.ActionReference!),
                ["kind"] = s.Evidence.Kind, ["necessity"] = s.Evidence.Necessity.ToString(),
                ["effects"] = CoverageStrings(domains[s.Evidence.Id].Keys) }))),
            ["effects"] = new JsonObject(domain.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
            { ["scope"] = p.Value.WorkflowScope, ["owner"] = p.Value.OwnerReference, ["boundary"] = p.Value.BoundaryKind,
                ["boundaryReference"] = p.Value.BoundaryReference, ["ownershipProof"] = p.Value.OccurrenceProof?.Fingerprint,
                ["result"] = state.Declarations.SingleOrDefault(d => d.Id == p.Value.OwnerReference) is { } declaration ? PlanningDeclarations.Name(state, declaration) : null })))
        };
        return new(ContributionDecisionId(scope), schema, context,
            PlanningGraphCompiler.Fingerprint("execution-contribution-v6:" + EvidenceFingerprint(state) + ":" +
                string.Join('|', members.Select(s => s.Evidence!.ProofFingerprint)) + ":" + schema.ToJsonString() + ":" + context.ToJsonString()),
            SourceDecisionIds: members.Select(s => "contribution_" + s.Evidence!.Id).Append(ContributionDecisionId(scope))
                .Concat(ContributionObligations(state, scope).Where(o => o.Kind is "runtime_condition" or "runtime_fallback").Select(o => "contribution_" + o.Id))
                .Distinct().Order(StringComparer.Ordinal).ToArray());
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
        if (answer["units"]!.AsArray().Sum(u => 1 + (u!["qualifiers"]?.AsArray().Count ?? 0)) > MaxContributionUnits)
            throw Failure(scope.Clause.Id, "Clause qualification exceeds its complete semantic-unit allowance.");
        var members = ContributionMembers(state, scope);
        var selected = new Dictionary<string, PlanningReference>(StringComparer.Ordinal);
        // Current for this single parse only. Reuse the validated immutable inputs
        // while projecting units; no cached proof or authority survives this call.
        var obligations = ContributionObligations(state, scope);
        var contracts = ContractCoverage(state);
        PlanningReference Select(JsonNode value, bool execution = false)
        {
            var span = value is JsonObject range ? scope.Select(range["start"]!.ToString(), range["end"]!.ToString())
                : state.References.Single(r => r.Id == value.ToString());
            var owners = members.Where(s => ContainsSpan(state.References.Single(r => r.Id == s.Evidence!.ActionReference), span)).ToArray();
            if (!ContainsSpan(scope.Clause, span) || !SameSource(scope.Clause, span) ||
                contracts.Any(c => Overlaps(c.Span, span)))
                throw Failure(scope.Clause.Id, "Contribution source evidence is foreign or overlaps canonical contract ownership.");
            if (execution && owners.Length == 0) throw Failure(scope.Clause.Id, "Request qualification selected evidence outside the eligible owned domain.");
            if (execution) foreach (var owner in owners) RequireEligibleContribution(state, owner.Evidence!, span);
            // Normalize by owned coordinates, independent of notation and runtime enumeration.
            var exact = members.Select(s => state.References.Single(r => r.Id == s.Evidence!.ActionReference))
                .Concat(obligations.SelectMany(o => o.EvidenceReferences).Distinct().Select(id => state.References.Single(r => r.Id == id)))
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
            var facts = execution ? value["request"]! : value;
            var spans = execution ? facts["evidence"]!.AsArray().Select(v => Select(v!, true)).DistinctBy(r => r.Id).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray() : [Select(facts["evidence"]!)];
            var predicate = execution ? Select(facts["predicate"]!, true) : null;
            if (predicate is not null && !Enumerable.Range(predicate.Start, predicate.Length).All(i => char.IsWhiteSpace(scope.Source.Text[i]) || spans.Any(r => r.Start <= i && i < r.Start + r.Length)))
                throw Failure(scope.Clause.Id, "Requested execution must account for its predicate in its support projections.");
            var parents = members.Where(s => spans.Any(r => execution ? Overlaps(state.References.Single(p => p.Id == s.Evidence!.ActionReference), r)
                : ContainsSpan(state.References.Single(p => p.Id == s.Evidence!.ActionReference), r))).ToArray();
            if (execution)
            {
                // A separately owned occurrence may overlap the same source clause.
                // Bind every compatible record for this effect; other occurrences must
                // still receive their own complete accounting below.
                parents = parents.Where(p => EffectDomain(state, p.Evidence!).ContainsKey(facts["effect"]!.ToString())).ToArray();
                if (!spans.Append(predicate!).All(r => parents.Any(p => ContainsSpan(state.References.Single(x => x.Id == p.Evidence!.ActionReference), r))))
                    throw Failure(scope.Clause.Id, "Requested execution has no owned provenance for its selected effect.");
                RequireCompatibleContributionFacts(parents.Select(s => s.Evidence!).ToArray(), scope.Clause.Id);
            }
            var bindings = parents.Select(s => s.Evidence!.Id).Order(StringComparer.Ordinal).ToList();
            var unit = new PlanningContributionUnit("", value["role"]!.ToString(), scope.Clause.Id, predicate?.Id,
                spans.Select(r => r.Id).ToList(), bindings, facts["effect"]?.ToString(),
                facts["basis"]?.ToString() ?? "governing_property", facts["owner"]?.ToString(), facts["boundary"]?.ToString())
            { SourceBindings = SourceBindings(state, scope, spans, obligations), GoverningKind = value["role"]!.ToString() == "governing_property"
                ? GoverningKind(state, obligations, spans.Single(), value["governingKind"]!.ToString()) : null };
            if (unit.Role == "excluded" && obligations.Any(o => o.Kind is "runtime_condition" or "runtime_fallback" &&
                o.EvidenceReferences.Any(id => spans.Any(r => Overlaps(state.References.Single(x => x.Id == id), r)))))
                throw Failure(scope.Clause.Id, "Current runtime semantics cannot disappear through no operation relevance.");
            unit = SealContributionUnit(unit);
            units.Add(unit);
            foreach (var span in spans)
                contributions.Add(SealContribution(scope.Clause.Id, new("", span.Id, execution ? "supports" : unit.Role,
                    unit.EffectId, unit.Basis, unit.OwnerReference, unit.BoundaryReference, PlanningContributionOrigin.ModelQualification)
                { UnitId = unit.Id, RuntimeEvidenceIds = bindings, GoverningKind = unit.GoverningKind,
                    SourceBindings = SourceBindings(state, scope, [span], obligations) }));
            if (!execution) continue;
            var qualifierAnswers = value["qualifiers"]!.AsArray().Select(q => (Span: Select(q!["evidence"]!), Kind: q["governingKind"]!.ToString())).ToArray();
            if (qualifierAnswers.GroupBy(q => q.Span.Id).Any(g => g.Select(q => q.Kind).Distinct().Count() > 1))
                throw Failure(scope.Clause.Id, "The same governing qualifier has conflicting semantic kinds.");
            var qualifiers = qualifierAnswers.Select(q => q.Span).DistinctBy(r => r.Id).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            foreach (var a in qualifiers)
            foreach (var b in qualifiers)
                if (Overlaps(a, b) && !ContainsSpan(a, b) && !ContainsSpan(b, a))
                    throw Failure(scope.Clause.Id, "Governing qualifier composition cannot contain crossing evidence.");
            foreach (var qualifier in qualifiers)
            {
                if (!spans.Any(r => ContainsSpan(r, qualifier) && r.Length > qualifier.Length))
                    throw Failure(scope.Clause.Id, "A governing qualifier must be properly contained in its parent request evidence.");
                // Property provenance is exact containment, independent of preliminary kind.
                // The parent still has to own the child; overlapping unrelated records cannot supply ownership.
                var provenance = members.Where(p => ContainsSpan(state.References.Single(r => r.Id == p.Evidence!.ActionReference), qualifier))
                    .Select(p => p.Evidence!.Id).Order(StringComparer.Ordinal).ToList();
                if (provenance.Count == 0) throw Failure(scope.Clause.Id, "A governing qualifier has no owned parent provenance.");
                var child = SealContributionUnit(new("", "governing_property", scope.Clause.Id, null, [qualifier.Id], provenance,
                    null, "governing_property", null, null) { ParentRequestUnitId = unit.Id,
                    SourceBindings = SourceBindings(state, scope, [qualifier], obligations),
                    GoverningKind = GoverningKind(state, obligations, qualifier, qualifierAnswers.First(q => q.Span.Id == qualifier.Id).Kind) });
                units.Add(child);
                contributions.Add(SealContribution(scope.Clause.Id, new("", qualifier.Id, "governing_property", null,
                    "governing_property", null, null, PlanningContributionOrigin.ModelQualification)
                { UnitId = child.Id, RuntimeEvidenceIds = provenance, GoverningKind = child.GoverningKind, SourceBindings = child.SourceBindings }));
            }
            bool Exhausts(PlanningReference reference) => Enumerable.Range(reference.Start, reference.Length)
                .Where(i => !char.IsWhiteSpace(scope.Source.Text[i])).All(i => qualifiers.Any(q => q.Start <= i && i < q.Start + q.Length));
            if (qualifiers.Length != 0 && (Exhausts(predicate!) || spans.All(Exhausts)))
                throw Failure(scope.Clause.Id, "Governing qualifiers cannot exhaust or replace their parent execution request.");
        }
        foreach (var member in members)
        {
            var parent = state.References.Single(r => r.Id == member.Evidence!.ActionReference);
            if (!Covered(state, parent, units.Where(u => u.RuntimeEvidenceIds.Contains(member.Evidence!.Id)).SelectMany(u => u.EvidenceReferences).Select(id => selected[id]).ToArray()))
                throw Failure(scope.Clause.Id, "Clause qualification did not cover all of its owned runtime evidence.");
        }
        if (!Covered(state, scope.Clause, selected.Values.Concat(contracts.Select(c => c.Span))))
            throw Failure(scope.Clause.Id, "Clause qualification did not account for all owned source evidence.");
        foreach (var a in contributions)
        foreach (var b in contributions.Where(b => b.Role != a.Role))
            if (Overlaps(selected[a.EvidenceReference], selected[b.EvidenceReference]) &&
                !NestedProperty(a, b) && !NestedProperty(b, a))
                throw Failure(scope.Clause.Id, "Clause semantic units give contradictory authority to overlapping evidence.");
        bool NestedProperty(PlanningExecutionContribution property, PlanningExecutionContribution support) =>
            property.Role == "governing_property" && support.Role == "supports" &&
            units.First(u => u.Id == property.UnitId).ParentRequestUnitId == support.UnitId;
        foreach (var span in selected.Values)
            if (!state.References.Any(r => r.Id == span.Id)) state.References.Add(span);
        return SealContributionProof(new(6, members.FirstOrDefault()?.Evidence!.Id, decision.Id, decision.EvidenceFingerprint,
            contributions.DistinctBy(c => c.Id).OrderBy(c => c.Id, StringComparer.Ordinal).ToList(), "")
        { ClauseReference = scope.Clause.Id, RuntimeEvidenceIds = members.Select(s => s.Evidence!.Id).ToList(),
            Units = units.DistinctBy(u => u.Id).OrderBy(u => u.Id, StringComparer.Ordinal).ToList(),
            SourceBindings = SourceBindings(state, scope, selected.Values, obligations) });
    }

    // The parent identity does not depend on child enumeration or child identities.
    private static PlanningContributionUnit SealContributionUnit(PlanningContributionUnit unit) => unit with
    { Id = "unit_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(unit with { Id = "" }, PlanningJsonContext.Default.PlanningContributionUnit))[..24] };

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
        // Engine exclusions already retain their runtime/declaration source proof;
        // they neither acquire a qualified source unit nor need property provenance.
        var sourceScope = excluded ? null : SourceScopes(state).SingleOrDefault(s => s.Clause.Id == evidence.ClauseReference);
        var sourceBindings = sourceScope is null ? new List<PlanningContributionSourceBinding>() :
            SourceBindings(state, sourceScope, [state.References.Single(r => r.Id == reference)]);
        var unit = new PlanningContributionUnit("unit_" + evidence.Id, structural ? "requested_execution" : excluded ? "excluded" : "governing_property",
            evidence.ClauseReference, structural ? reference : null, [reference], [evidence.Id], structural ? anchor?.Key : null, basis, anchor?.Value.OwnerReference, anchor?.Value.BoundaryReference)
        { SourceBindings = sourceBindings, GoverningKind = structural || excluded ? null : "descriptive_property" };
        var item = SealContribution(evidence.Id, new("", reference, excluded ? "excluded" : structural ? "supports" : "governing_property",
            structural ? anchor?.Key : null, basis, anchor?.Value.OwnerReference, anchor?.Value.BoundaryReference,
            excluded ? PlanningContributionOrigin.DeterministicExclusion : PlanningContributionOrigin.DeterministicBaseline)
        { UnitId = unit.Id, RuntimeEvidenceIds = [evidence.Id], SourceBindings = sourceBindings, GoverningKind = unit.GoverningKind });
        return SealContributionProof(new(6, evidence.Id, null, PlanningGraphCompiler.Fingerprint("execution-contribution-v6:" +
            EvidenceFingerprint(state) + ":" + evidence.ProofFingerprint + ":" + state.DeclarationFingerprint), [item], "")
        { ClauseReference = evidence.ClauseReference, RuntimeEvidenceIds = [evidence.Id], Units = [unit], SourceBindings = sourceBindings });
    }

    internal static PlanningDecisionPages.Decision[] ContributionDecisions(PlanningSnapshot state) => ContributionScopes(state)
        .Select(s => ContributionDecision(state, s)).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();

    internal static PlanningExecutionContributionProof[] ReadContributions(PlanningSnapshot state)
    {
        var scopes = DeriveScopes(state).ToDictionary(s => s.Evidence!.Id, StringComparer.Ordinal);
        var decisions = ContributionDecisions(state);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions); }
        catch (PlanningConflictException) { throw Failure("$plan", "Current canonical contribution qualification is missing.", "INTENT_OPERATION_PROOF_MISSING"); }
        var proofs = state.RuntimeEvidence.Where(e => !scopes.ContainsKey(e.Id) || e.BaselineReference is not null)
            .Select(e => DeterministicContribution(state, e, !scopes.ContainsKey(e.Id))).ToList();
        proofs.AddRange(ContributionScopes(state).Select(s => ParseContributions(state, s, values[ContributionDecisionId(s)]!.AsObject())));
        return proofs.OrderBy(p => p.ClauseReference, StringComparer.Ordinal).ThenBy(p => p.RuntimeEvidenceId, StringComparer.Ordinal).ToArray();
    }

    private static async Task GroundContributions(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", ContributionDecisions(state), ct);
        _ = ReadContributions(state);
    }

    internal static Scope[] QualifiedScopes(PlanningSnapshot state)
    {
        var scopes = SourceScopes(state).ToDictionary(s => s.Clause.Id, StringComparer.Ordinal);
        return ReadContributions(state).SelectMany(p => p.Contributions.Where(c => c.Role != "excluded")
            .Select(c => scopes[p.ClauseReference!] with { Evidence = c.RuntimeEvidenceIds.Count == 0 ? null : state.RuntimeEvidence.Single(e => e.Id == c.RuntimeEvidenceIds[0]),
                Contribution = c, Unit = p.Units.Single(u => u.Id == c.UnitId) }))
            .OrderBy(s => s.Contribution!.Id, StringComparer.Ordinal).ToArray();
    }

    private static PlanningRuntimeEvidence[] AssignmentEvidence(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        if (assignment.RuntimeEvidenceIds.Count == 0 && assignment.Disposition != "attach") throw Failure(assignment.ClauseReference, "Current runtime provenance is missing.");
        return assignment.RuntimeEvidenceIds.Select(id => state.RuntimeEvidence.SingleOrDefault(e => e.Id == id)
            ?? throw Failure(assignment.ClauseReference, "Foreign runtime provenance.")).ToArray();
    }

    private static PlanningRuntimeEvidence[] ContributionEvidence(PlanningSnapshot state, Scope scope) =>
        (scope.Contribution?.RuntimeEvidenceIds ?? (scope.Evidence is null ? [] : [scope.Evidence.Id])).Select(id => state.RuntimeEvidence.Single(e => e.Id == id)).ToArray();
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
