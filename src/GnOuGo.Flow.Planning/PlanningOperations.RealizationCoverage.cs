using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // Derived bounded domains, not a persisted graph. All authority is replayed
    // from the existing pages and attached to the existing admission proof.
    internal sealed record CoverageGroup(string Id, Scope[] Scopes,
        Dictionary<string, PlanningOperationEffectAnchor> Effects,
        Dictionary<string, PlanningRealizationContribution[]> Plans, PlanningDecisionPages.Decision Decision);

    internal static CoverageGroup[] CoverageGroups(PlanningSnapshot state)
    {
        var scopes = QualifiedScopes(state).Where(s => s.Contribution!.Role == "supports" && s.Evidence!.BaselineReference is null)
            .OrderBy(s => s.Contribution!.Id, StringComparer.Ordinal).ToArray();
        var domains = scopes.ToDictionary(s => s.Contribution!.Id, s => EffectDomain(state, s.Evidence!).Where(p => p.Key == s.Contribution!.EffectId).ToDictionary(), StringComparer.Ordinal);
        var remaining = scopes.ToList();
        foreach (var scope in remaining)
            if (domains[scope.Contribution!.Id].Count == 0)
                throw Failure(scope.Contribution!.Id, "Required realization coverage has no proven effect domain.");
        var groups = new List<CoverageGroup>();
        while (remaining.Count != 0)
        {
            var members = new HashSet<string>(StringComparer.Ordinal) { remaining[0].Contribution!.Id };
            var effects = new HashSet<string>(domains[remaining[0].Contribution!.Id].Keys, StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var scope in scopes.Where(s => domains[s.Contribution!.Id].Keys.Any(effects.Contains)))
                {
                    if (!members.Add(scope.Contribution!.Id)) continue;
                    // Governing evidence couples possible targets but never creates authority.
                    effects.UnionWith(domains[scope.Contribution!.Id].Keys); changed = true;
                }
            } while (changed);
            var selected = scopes.Where(s => members.Contains(s.Contribution!.Id)).ToArray();
            remaining.RemoveAll(s => members.Contains(s.Contribution!.Id));
            groups.Add(BuildCoverageGroup(state, selected, domains));
        }
        return groups.OrderBy(g => g.Id, StringComparer.Ordinal).ToArray();
    }

    private static CoverageGroup BuildCoverageGroup(PlanningSnapshot state, Scope[] scopes,
        Dictionary<string, Dictionary<string, PlanningOperationEffectAnchor>> domains)
    {
        var actions = scopes.Where(s => s.Contribution!.Role == "supports").ToArray();
        var domain = actions.SelectMany(s => domains[s.Contribution!.Id]).GroupBy(p => p.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var id = "coverage_" + PlanningGraphCompiler.Fingerprint(string.Join('|', scopes.Select(s => s.Contribution!.Id)))[..24];
        var context = new JsonObject
        {
            ["stage"] = "realization_coverage",
            ["task"] = "Select a complete issued coverage mapping using already qualified contribution authority. Every selected effect needs qualified executable support. Properties are context only; this decision cannot bind their applicability, retire them or use them as support. Distinct owned boundaries remain distinct. Omission requires explicit optional necessity. Return scoped public dataflow for each selected effect, or unresolved.",
            ["contributions"] = new JsonObject(scopes.Select(s => new KeyValuePair<string, JsonNode?>(s.Contribution!.Id, new JsonObject
            {
                ["action"] = PlanningChoiceEvidence.Text(state, s.Contribution!.EvidenceReference),
                ["clause"] = PlanningChoiceEvidence.Text(state, s.Clause.Id), ["role"] = s.Contribution!.Role,
                ["kind"] = s.Evidence!.Kind, ["necessity"] = s.Evidence.Necessity.ToString(),
                ["necessityEvidence"] = s.Evidence.NecessityReference,
                ["eligibleEffects"] = CoverageStrings(domains[s.Contribution!.Id].Keys.Where(domain.ContainsKey))
            }))),
            ["propertyContext"] = new JsonArray(QualifiedScopes(state).Where(s => s.Contribution!.Role == "governing_property")
                .Select(s => (JsonNode)new JsonObject { ["evidence"] = s.Contribution!.EvidenceReference,
                    ["property"] = PlanningChoiceEvidence.Text(state, s.Contribution.EvidenceReference),
                    ["clause"] = PlanningChoiceEvidence.Text(state, s.Clause.Id) }).ToArray()),
            ["effects"] = new JsonObject(domain.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
            { ["scope"] = p.Value.WorkflowScope, ["owner"] = p.Value.OwnerReference, ["boundary"] = p.Value.BoundaryKind,
                ["boundaryEvidence"] = p.Value.BoundaryReference }))),
            ["declarations"] = new JsonObject(state.Declarations.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d =>
                new KeyValuePair<string, JsonNode?>(d.Id, new JsonObject { ["name"] = PlanningDeclarations.Name(state, d),
                    ["direction"] = d.Direction, ["scope"] = d.WorkflowScope,
                    ["evidence"] = string.Join(" ", d.ClauseReferences.Distinct().Order(StringComparer.Ordinal).Select(r => PlanningChoiceEvidence.Text(state, r))) })))
        };
        var plans = new Dictionary<string, PlanningRealizationContribution[]>(StringComparer.Ordinal);
        var alternatives = new JsonArray(PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))));
        var schema = new JsonObject { ["anyOf"] = alternatives };
        var planContext = new JsonObject(); context["mappings"] = planContext;
        var fingerprint = EffectFingerprint(state) + ":" + string.Join('|', ReadContributions(state).Select(p => p.ProofFingerprint)) + ":coverage-v3:" + PlanningGraphCompiler.Fingerprint(context.ToJsonString());
        var decision = new PlanningDecisionPages.Decision(id, schema, context, fingerprint,
            SourceDecisionIds: scopes.Select(s => s.Contribution!.Id).ToArray());
        var byEffects = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        void Add(PlanningRealizationContribution[] mappings)
        {
            var selected = mappings.Where(m => m.Disposition == "supports").SelectMany(m => m.Effects).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (mappings.Where(m => m.Disposition == "governs").SelectMany(m => m.Effects).Any(e => !selected.Contains(e, StringComparer.Ordinal))) return;
            // Validate explicit necessity collectively before permitting any omission or completion.
            foreach (var effect in selected)
            {
                var facts = mappings.Where(m => m.Effects.Contains(effect)).Select(m => scopes.Single(s => s.Contribution!.Id == m.ContributionId).Evidence!.Necessity).ToArray();
                if (facts.Contains(PlanningOperationNecessity.Required) && facts.Contains(PlanningOperationNecessity.Optional)) return;
            }
            var key = string.Join('|', selected);
            var planId = "mapping_" + PlanningGraphCompiler.Fingerprint(new JsonArray(mappings.Select(m => (JsonNode)new JsonArray(m.ContributionId, m.Disposition, CoverageStrings(m.Effects))).ToArray()).ToJsonString())[..24];
            if (!plans.TryAdd(planId, mappings)) return;
            planContext[planId] = new JsonObject(mappings.Select(m => new KeyValuePair<string, JsonNode?>(m.ContributionId!,
                new JsonObject { ["role"] = m.Disposition, ["effects"] = CoverageStrings(m.Effects) })));
            if (!byEffects.TryGetValue(key, out var names))
            {
                names = []; byEffects.Add(key, names);
                var fields = selected.Select(effect =>
                {
                    var anchor = domain[effect];
                    var inputs = ReferencesArray(state.Declarations.Where(d => d.Direction == "input" && d.WorkflowScope == anchor.WorkflowScope).Select(d => d.Id));
                    var outputs = anchor.BoundaryKind == "result_realization" ? ReferencesArray([anchor.OwnerReference], 1) :
                        ReferencesArray(state.Declarations.Where(d => d.Direction == "output" && d.WorkflowScope == anchor.WorkflowScope).Select(d => d.Id));
                    return (effect, PlanningHoleRequests.Object(("inputs", inputs), ("outputs", outputs)));
                }).ToArray();
                alternatives.Add((JsonNode)PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["complete"])),
                    ("mapping", new JsonObject { ["type"] = "string", ["enum"] = names }), ("effects", PlanningHoleRequests.Object(fields))));
            }
            names.Add((JsonNode)JsonValue.Create(planId)!);
            // The same existing indivisible-decision sizing rule bounds domain generation.
            // No fallback drops coverage or creates independently acceptable fragments.
            PlanningDecisionPages.PackedPageCount(state, [decision]);
        }
        void Expand(int index, List<PlanningRealizationContribution> mappings)
        {
            if (index == scopes.Length) { Add(mappings.ToArray()); return; }
            var scope = scopes[index]; var evidence = scope.Evidence!; var qualification = scope.Contribution!;
            var candidates = domains[qualification.Id].Keys.Where(domain.ContainsKey).Order(StringComparer.Ordinal).ToArray();
            foreach (var target in candidates)
            foreach (var disposition in new[] { qualification.Role })
            {
                if (disposition == "governs" && !actions.Any(a => a.Contribution!.Id != qualification.Id && domains[a.Contribution.Id].ContainsKey(target))) continue;
                mappings.Add(new(evidence.Id, disposition, [target], ContributionReferences(scope)) { ContributionId = qualification.Id });
                Expand(index + 1, mappings); mappings.RemoveAt(mappings.Count - 1);
            }
            if (qualification.Role == "supports" && evidence.Necessity == PlanningOperationNecessity.Optional &&
                evidence.OccurrenceBoundary is not null)
            {
                mappings.Add(new(evidence.Id, "omitted", [], ContributionReferences(scope)) { ContributionId = qualification.Id });
                Expand(index + 1, mappings); mappings.RemoveAt(mappings.Count - 1);
            }
        }
        PlanningDecisionPages.PackedPageCount(state, [decision]);
        var forcedNecessityConflict = scopes.Where(s => domains[s.Contribution!.Id].Count == 1)
            .GroupBy(s => domains[s.Contribution!.Id].Single().Key, StringComparer.Ordinal)
            .Any(g => g.Any(s => s.Evidence!.Necessity == PlanningOperationNecessity.Required) &&
                g.Any(s => s.Evidence!.Necessity == PlanningOperationNecessity.Optional));
        if (!forcedNecessityConflict) Expand(0, []);
        // Only an entirely optional executable cohort can be omitted. This cannot
        // hide a conflicting required contribution, including an ambiguous one.
        if (actions.Any(s => s.Evidence!.Necessity == PlanningOperationNecessity.Optional) &&
            actions.All(s => s.Evidence!.Necessity is PlanningOperationNecessity.Optional or PlanningOperationNecessity.Unspecified) &&
            scopes.All(s => s.Evidence!.Necessity != PlanningOperationNecessity.Required))
            Add(scopes.Select(s => new PlanningRealizationContribution(s.Evidence!.Id, "omitted", [], ContributionReferences(s)) { ContributionId = s.Contribution!.Id }).ToArray());
        return new(id, scopes, domain, plans, decision with
        { EvidenceFingerprint = fingerprint + ":" + PlanningGraphCompiler.Fingerprint(schema.ToJsonString() + planContext.ToJsonString()) });
    }

    private static JsonArray CoverageStrings(IEnumerable<string> values) => new(values.Distinct().Order(StringComparer.Ordinal).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    private static List<string> ContributionReferences(Scope scope) => new[] { scope.Contribution!.EvidenceReference, scope.Clause.Id, scope.Evidence!.NecessityReference }
        .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

    internal static PlanningRealizationCoverageProof ParseCoverage(PlanningSnapshot state, CoverageGroup group, JsonObject answer)
    {
        if (PlanningContractValidation.ValidateInstance(answer, group.Decision.Schema).Count != 0)
            throw Failure(group.Id, "Incomplete or foreign realization coverage cannot authorize execution.");
        if (answer["status"]!.ToString() != "complete") throw Failure(group.Id, "Executable realization coverage is unresolved.");
        var contributions = group.Plans[answer["mapping"]!.ToString()].ToList();
        var effects = answer["effects"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            List<string> Values(string field)
            {
                var values = p.Value![field]!.AsArray().Select(v => v!.ToString()).ToArray();
                if (values.Distinct().Count() != values.Length) throw Failure(group.Id, "Duplicate dataflow references are not proof.");
                return values.Order(StringComparer.Ordinal).ToList();
            }
            return new PlanningRealizedEffect(p.Key, group.Effects[p.Key], contributions.Where(c => c.Disposition == "supports" && c.Effects.Contains(p.Key))
                .Select(c => c.ContributionId!).Order(StringComparer.Ordinal).ToList(), Values("inputs"), Values("outputs"));
        }).ToList();
        var proof = new PlanningRealizationCoverageProof(3, group.Id, group.Decision.EvidenceFingerprint, effects.Select(e => e.Id).ToList(), contributions, effects, "");
        return proof with { ProofFingerprint = CoverageFingerprint(proof) };
    }
    private static string CoverageFingerprint(PlanningRealizationCoverageProof proof) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(proof with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningRealizationCoverageProof));

    internal static PlanningRealizationCoverageProof[] ReadCoverage(PlanningSnapshot state)
    {
        var groups = CoverageGroups(state);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", groups.Select(g => g.Decision).ToArray()); }
        catch (PlanningConflictException) { throw Failure("$plan", "Coverage requires its exact completed decision scope.", "INTENT_OPERATION_PROOF_MISSING"); }
        return groups.Select(g => ParseCoverage(state, g, values[g.Id]!.AsObject())).Concat(
            QualifiedScopes(state).Where(s => s.Contribution!.Role == "supports" && s.Evidence!.BaselineReference is not null).GroupBy(s => s.Contribution!.EffectId, StringComparer.Ordinal).Select(group =>
            {
                var support = group.Where(s => s.Contribution!.Role == "supports").OrderBy(s => s.Contribution!.Id, StringComparer.Ordinal).ToArray();
                if (support.Length == 0) throw Failure(group.Key!, "Baseline annotations cannot establish execution.");
                var mapping = BaselineEffect(state, support[0]); var anchor = mapping.Candidates.Single(); var id = group.Key!;
                var proof = new PlanningRealizationCoverageProof(3, "coverage_baseline_" + id, EffectFingerprint(state), [id],
                    group.OrderBy(s => s.Contribution!.Id, StringComparer.Ordinal).Select(s => new PlanningRealizationContribution(s.Evidence!.Id, s.Contribution!.Role, [id], ContributionReferences(s))
                    { ContributionId = s.Contribution.Id }).ToList(),
                    [new(id, anchor, support.Select(s => s.Contribution!.Id).ToList(), mapping.Inputs, mapping.Outputs)], "");
                return proof with { ProofFingerprint = CoverageFingerprint(proof) };
            })).OrderBy(p => p.DecisionId, StringComparer.Ordinal).ToArray();
    }
    private static async Task GroundCoverage(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var groups = CoverageGroups(state);
        if (groups.FirstOrDefault(g => g.Plans.Count == 0) is { } impossible)
            throw Failure(impossible.Id, "Owned executable authority and necessity cannot establish complete realization coverage.");
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", groups.Select(g => g.Decision).ToArray(), ct);
        foreach (var group in groups) ParseCoverage(state, group, values[group.Id]!.AsObject());
    }
    internal static List<PlanningOperationAssignment> CoverageAssignments(PlanningSnapshot state)
    {
        var scopes = QualifiedScopes(state).ToDictionary(s => s.Contribution!.Id, StringComparer.Ordinal);
        var result = new List<PlanningOperationAssignment>();
        foreach (var proof in ReadCoverage(state))
        foreach (var contribution in proof.Contributions.Where(c => c.Disposition != "omitted"))
        foreach (var id in contribution.Effects)
        {
            var effect = proof.Effects.Single(e => e.Id == id); var scope = scopes[contribution.ContributionId!];
            var mapping = scope.Evidence!.BaselineReference is not null ? BaselineEffect(state, scope) with { Contribution = contribution.Disposition == "supports" ? "realizes" : "governs" } : new PlanningOperationEffectProof(7, proof.DecisionId, contribution.Disposition == "supports" ? "realizes" : "governs",
                [effect.Anchor], effect.Inputs, effect.Outputs, [], contribution.EvidenceReferences, "model", proof.DomainFingerprint);
            result.Add(Assignment(scope, mapping, id, id, contribution.Disposition == "supports" ? "supports" : "attach", "deterministic"));
        }
        return result.OrderBy(a => a.EffectId, StringComparer.Ordinal).ThenBy(a => a.RuntimeEvidenceId, StringComparer.Ordinal).ToList();
    }

    private static List<PlanningObligation> ReadCompleteCoverage(PlanningSnapshot state) => AttachApplicability(state, MaterializeCoverage(state), ReadApplicability(state));

    internal static List<PlanningObligation> MaterializeCoverage(PlanningSnapshot state)
    {
        var proofs = ReadCoverage(state);
        return CoverageAssignments(state).GroupBy(a => a.EffectId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var assignments = g.OrderBy(a => a.RuntimeEvidenceId, StringComparer.Ordinal).ToList();
            var supports = assignments.Where(a => a.Disposition == "supports").ToArray();
            if (supports.Length == 0) throw Failure(g.Key!, "A realized effect requires aggregate executable authority.");
            var diagnostic = supports.OrderBy(a => a.ActionReference, StringComparer.Ordinal).First();
            var operation = new PlanningObligation(g.Key!, [diagnostic.ActionReference], diagnostic.Kind == "local_processing" ? "workflow" : "capability_contract", diagnostic.Kind,
                ResolveRequiredness(state, assignments)) { Disposition = "admitted" };
            return Prove(state, operation, new(12, operation.Id, diagnostic.ActionReference, diagnostic.BaselineReference, assignments, EvidenceFingerprint(state), "")
            { ExecutionContributions = ReadContributions(state).ToList(), RealizationCoverage = proofs.SingleOrDefault(p => p.SelectedEffects.Contains(operation.Id)) });
        }).ToList();
    }
}
