using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    // These indexes are derived request domains. Only completed decision pages and
    // the proofs attached to admitted obligations are persisted.
    internal static string EffectDecisionId(PlanningRuntimeEvidence evidence, bool governing = false) => (governing ? "effect_governing_" : "effect_") + evidence.Id;
    internal static string EffectFingerprint(PlanningSnapshot state) => "effect-proof-v2:realizations-first:" + EvidenceFingerprint(state);

    internal static string CanonicalId(PlanningSnapshot state, PlanningOperationEffectAnchor effect, string kind, string? baseline)
    {
        if (baseline is not null)
        {
            if (!PlanningSourceGroundingRules.BaselineNodes(state).TryGetValue(baseline, out var node))
                throw Failure(baseline, "The baseline effect owner is stale or foreign.");
            return "operation_" + PlanningGraphCompiler.Fingerprint(new JsonArray("operation-v1", "existing", node.Workflow, node.Node.Key).ToJsonString())[..24];
        }
        return "operation_" + PlanningGraphCompiler.Fingerprint(new JsonArray("operation-effect-v1", kind,
            effect.WorkflowScope, EffectCoordinate(state, effect.OwnerReference), effect.BoundaryKind,
            EffectCoordinate(state, effect.BoundaryReference), effect.IterationReference).ToJsonString())[..24];
    }

    private static string EffectCoordinate(PlanningSnapshot state, string reference)
    {
        if (state.Declarations.Any(d => d.Id == reference)) return reference;
        if (!PlanningChoiceEvidence.Current(state, reference)) throw Failure(reference, "Effect ownership requires a current issued reference.");
        var span = state.References.Single(r => r.Id == reference);
        return new JsonArray(span.SourceId, span.Start, span.Length).ToJsonString();
    }

    private static string[] WorkflowScopes(PlanningSnapshot state) => new[] { "main" }
        .Concat(state.Obligations.Where(o => o.Kind == "workflow_boundary").Select(o => o.Id))
        .Concat(state.Declarations.Select(d => d.WorkflowScope)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static Dictionary<string, PlanningOperationEffectAnchor> EffectDomain(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        var result = new Dictionary<string, PlanningOperationEffectAnchor>(StringComparer.Ordinal);
        void Add(PlanningOperationEffectAnchor effect, string? baseline = null) => result.TryAdd(CanonicalId(state, effect, evidence.Kind!, baseline), effect);
        if (evidence.BaselineReference is { } baseline)
        {
            var node = PlanningSourceGroundingRules.BaselineNodes(state)[baseline];
            Add(new(node.Workflow, baseline, "baseline", baseline), baseline);
            return result;
        }
        // A result slot is a possible single realization, not proof that every
        // action mentioning that output belongs to it. Grounding must establish
        // the result ownership AND the single-realization boundary.
        if (evidence.Kind == "local_processing")
            foreach (var declaration in state.Declarations.Where(d => d.Direction == "output"))
                Add(new(declaration.WorkflowScope, declaration.Id, "result_realization", declaration.Id));
        foreach (var action in DeriveScopes(state).Select(s => s.Evidence!).Where(e => e.EvidenceRole == "action" &&
            e.BaselineReference is null && CompatibleFacts(evidence, e)))
        foreach (var workflow in WorkflowScopes(state))
        {
            var boundary = action.Kind is "resource_lifecycle" or "cleanup" ? "resource_" + action.ResourceAction : "invocation";
            var owner = action.ResourceReference ?? action.ActionReference!;
            Add(new(workflow, owner, boundary, action.ActionReference!));
            foreach (var iteration in state.Obligations.Where(o => o.Kind == "iteration"))
                Add(new(workflow, owner, boundary, action.ActionReference!, iteration.Id));
        }
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private static bool CompatibleFacts(PlanningRuntimeEvidence left, PlanningRuntimeEvidence right) =>
        left.Kind == right.Kind && left.Role == right.Role && left.ExecutionScope == right.ExecutionScope &&
        left.BaselineReference == right.BaselineReference && left.ResourceAction == right.ResourceAction &&
        left.ResourceOwnership == right.ResourceOwnership;

    private static Dictionary<string, PlanningOperationEffectAnchor> AllEffects(PlanningSnapshot state) =>
        DeriveScopes(state).SelectMany(s => EffectDomain(state, s.Evidence!)).DistinctBy(p => p.Key, StringComparer.Ordinal)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static JsonObject ReferencesArray(IEnumerable<string> references, int minimum = 0)
    {
        var ids = references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new() { ["type"] = "array", ["minItems"] = minimum, ["maxItems"] = ids.Length,
            ["items"] = ids.Length == 0 ? PlanningHoleRequests.Type("null") : PlanningHoleRequests.Enum(ids) };
    }

    internal static PlanningDecisionPages.Decision EffectDecision(PlanningSnapshot state, Scope scope,
        IReadOnlyList<PlanningOperationAssignment>? realized = null)
    {
        var evidence = scope.Evidence!;
        var governing = realized is not null;
        if (!governing && evidence.EvidenceRole != "action") throw Failure(evidence.Id, "Governing evidence cannot establish a realization.");
        var domain = governing ? RealizedDomain(state, evidence, realized!) : EffectDomain(state, evidence);
        if (governing && domain.Count == 0) throw Failure(evidence.Id, "Governing evidence requires a compatible realized effect.");
        var producers = governing ? RealizedAnchors(state, realized!) : AllEffects(state);
        var sources = PlanningSourceDecisions.Sources(state);
        var references = state.References.Where(r => sources.ContainsKey(r.SourceId) && PlanningChoiceEvidence.Current(state, r.Id) &&
            (r.Id == evidence.ActionReference || r.Id == evidence.ClauseReference ||
                state.Declarations.Any(d => d.ClauseReferences.Contains(r.Id)) ||
                state.Obligations.Any(o => o.Kind is "workflow_boundary" or "iteration" && o.EvidenceReferences.Contains(r.Id))))
            .DistinctBy(r => r.Id).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var variants = new JsonArray(PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved", "not_an_effect"]))));
        if (!governing) variants.Add((JsonNode)PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["governing"])),
            ("evidence", ReferencesArray(references.Select(r => r.Id), 1))));
        // Each alternative is one execution scope. Foreign values must cross an
        // established interface (for example a caller-side workflow.call effect),
        // never become directly visible because another workflow was mentioned.
        foreach (var group in domain.GroupBy(p => p.Value.WorkflowScope, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        foreach (var contribution in governing && group.Count() > 1 ? new[] { "governs", "shared_rule" } : governing ? ["governs"] : ["realizes"])
        {
            var effects = ReferencesArray(group.Select(p => p.Key), contribution == "shared_rule" ? 2 : 1);
            if (governing && contribution == "governs") effects["maxItems"] = 1;
            variants.Add((JsonNode)PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["mapped"])),
                ("contribution", PlanningHoleRequests.Enum([contribution])),
                ("effects", effects),
                ("inputs", ReferencesArray(state.Declarations.Where(d => d.Direction == "input" && d.WorkflowScope == group.Key).Select(d => d.Id))),
                ("outputs", ReferencesArray(state.Declarations.Where(d => d.Direction == "output" && d.WorkflowScope == group.Key).Select(d => d.Id))),
                ("producers", ReferencesArray(producers.Where(p => p.Value.WorkflowScope == group.Key).Select(p => p.Key))),
                ("evidence", ReferencesArray(references.Select(r => r.Id), 1))));
        }
        return new(EffectDecisionId(evidence, governing), new() { ["anyOf"] = variants }, new()
        {
            ["stage"] = governing ? "effect_governing" : "effect_realizations",
            ["task"] = governing
                ? "Map this governing contribution to established realized effects. Select one applicable effect, or shared_rule only when the evidence governs every selected effect. Return this contribution's input/output and producer dependencies."
                : "Determine whether this evidence establishes a runtime effect realization. A result_realization is one construction of the declared result; sharing an output alone does not establish it. Invocation anchors require independently requested execution, an intermediate stage or repeated effect. Rules or descriptive evidence that do not establish a realization return governing without selecting targets. Targets are resolved after realizations exist. For a realization return its complete business-input/output and producer dependencies. Select multiple effect identities only when evidence cannot distinguish them.",
            ["action"] = PlanningChoiceEvidence.Text(state, evidence.ActionReference!),
            ["clause"] = PlanningChoiceEvidence.Text(state, evidence.ClauseReference),
            ["kind"] = evidence.Kind,
            ["effects"] = new JsonObject(domain.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
            { ["scope"] = p.Value.WorkflowScope, ["owner"] = p.Value.OwnerReference,
                ["boundary"] = p.Value.BoundaryKind, ["boundaryEvidence"] = p.Value.BoundaryReference,
                ["iteration"] = p.Value.IterationReference }))),
            ["declarations"] = new JsonObject(state.Declarations.Select(d => new KeyValuePair<string, JsonNode?>(d.Id,
                new JsonObject { ["name"] = PlanningDeclarations.Name(state, d), ["direction"] = d.Direction, ["scope"] = d.WorkflowScope,
                    ["evidence"] = string.Join(" ", d.ClauseReferences.Distinct().Select(r => PlanningChoiceEvidence.Text(state, r))) }))),
            ["references"] = new JsonObject(references.Select(r => new KeyValuePair<string, JsonNode?>(r.Id, JsonValue.Create(PlanningChoiceEvidence.Text(state, r.Id))))),
            ["producerEffects"] = new JsonObject(producers.Where(p => domain.Values.Any(e => e.WorkflowScope == p.Value.WorkflowScope))
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["scope"] = p.Value.WorkflowScope, ["evidence"] =
                state.Declarations.Any(d => d.Id == p.Value.OwnerReference) ? PlanningDeclarations.Name(state, state.Declarations.Single(d => d.Id == p.Value.OwnerReference)) :
                PlanningChoiceEvidence.Current(state, p.Value.BoundaryReference) ? PlanningChoiceEvidence.Text(state, p.Value.BoundaryReference) : p.Value.BoundaryKind })))
        }, governing ? GoverningFingerprint(state, realized!) : EffectFingerprint(state));
    }

    internal static PlanningOperationEffectProof ParseEffect(PlanningSnapshot state, Scope scope, JsonObject answer,
        IReadOnlyList<PlanningOperationAssignment>? realized = null)
    {
        var decision = EffectDecision(state, scope, realized);
        if (PlanningContractValidation.ValidateInstance(answer, decision.Schema).Count != 0)
            throw Failure(scope.Evidence!.Id, "Effect grounding changed issued references or execution facts.");
        var status = answer["status"]!.ToString();
        if (status == "unresolved") throw Failure(scope.Evidence!.Id, "No proven runtime effect identity is available.");
        var domain = realized is null ? EffectDomain(state, scope.Evidence!) : RealizedDomain(state, scope.Evidence!, realized);
        List<string> Values(string field)
        {
            var values = answer[field]?.AsArray().Select(v => v!.ToString()).ToList() ?? [];
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Count) throw Failure(scope.Evidence!.Id, "Duplicate effect references cannot establish additional evidence.");
            return values.Order(StringComparer.Ordinal).ToList();
        }
        var candidates = Values("effects").Select(id => domain[id]).ToList();
        var inputs = Values("inputs"); var outputs = Values("outputs"); var refs = Values("evidence");
        var producerIds = Values("producers");
        var producers = realized is null ? AllEffects(state) : RealizedAnchors(state, realized);
        if (candidates.Select(c => c.WorkflowScope).Distinct(StringComparer.Ordinal).Count() > 1)
            throw Failure(scope.Evidence!.Id, "An effect assignment cannot combine incompatible workflow scopes.");
        if (status is "mapped" or "governing" && !refs.Contains(scope.Evidence!.ClauseReference) && !refs.Contains(scope.Evidence!.ActionReference!))
            throw Failure(scope.Evidence.Id, "An effect assignment needs its own governing action or clause evidence.");
        foreach (var candidate in candidates)
        {
            if (inputs.Concat(outputs).Any(id => state.Declarations.Single(d => d.Id == id).WorkflowScope != candidate.WorkflowScope))
                throw Failure(scope.Evidence!.Id, "Effect dataflow crosses an unestablished workflow boundary.");
            if (producerIds.Any(id => !producers.TryGetValue(id, out var producer) || producer.WorkflowScope != candidate.WorkflowScope))
                throw Failure(scope.Evidence!.Id, "A foreign producer requires an established effect in the consuming workflow scope.");
            if (candidate.BoundaryKind == "result_realization" && answer["contribution"]?.ToString() == "realizes" && !outputs.Contains(candidate.OwnerReference))
                throw Failure(scope.Evidence!.Id, "A result realization must establish production of its exact owned result.");
        }
        return new(2, decision.Id, status == "not_an_effect" ? "none" : status == "governing" ? "pending_governing" : answer["contribution"]!.ToString(), candidates,
            inputs, outputs, producerIds, refs, "model", decision.EvidenceFingerprint);
    }

    internal static PlanningOperationEffectProof BaselineEffect(PlanningSnapshot state, Scope scope)
    {
        var evidence = scope.Evidence!;
        var anchor = EffectDomain(state, evidence).Single().Value;
        var node = PlanningSourceGroundingRules.BaselineNodes(state)[evidence.BaselineReference!];
        var graph = state.Request.Baseline!;
        var workflow = graph.Workflows.Single(w => w.Key == node.Workflow);
        var inputs = PlanningDataflow.BusinessInputs(workflow, node.Node);
        return new(2, EffectDecisionId(evidence), evidence.EvidenceRole == "action" ? "realizes" : "governs", [anchor],
            state.Declarations.Where(d => d.Direction == "input" && d.WorkflowScope == node.Workflow && inputs.Contains(PlanningDeclarations.Name(state, d))).Select(d => d.Id).Order(StringComparer.Ordinal).ToList(),
            [], [], [evidence.ClauseReference], "baseline", EffectFingerprint(state));
    }

    private static async Task<Dictionary<string, PlanningOperationEffectProof>> GroundRealizations(PlanningSnapshot state, IPlanningRuntime runtime, Scope[] scopes, CancellationToken ct)
    {
        scopes = scopes.Where(s => s.Evidence!.EvidenceRole == "action").ToArray();
        var decisions = scopes.Where(s => s.Evidence!.BaselineReference is null).Select(s => EffectDecision(state, s)).ToArray();
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", decisions, ct);
        return scopes.ToDictionary(s => s.Evidence!.Id, s => s.Evidence!.BaselineReference is null
            ? ParseEffect(state, s, values[EffectDecisionId(s.Evidence)]!.AsObject()) : BaselineEffect(state, s), StringComparer.Ordinal);
    }

    private static void ValidateEffect(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        var effect = assignment.Effect ?? throw Failure(assignment.ClauseReference, "Current effect ownership proof is required.", "INTENT_OPERATION_PROOF_MISSING");
        if (effect.Version != 2)
            throw Failure(assignment.ClauseReference, "Effect ownership proof is stale.", "INTENT_OPERATION_PROOF_MISSING");
        var scope = DeriveScopes(state).Single(s => s.Evidence!.Id == assignment.RuntimeEvidenceId);
        PlanningOperationEffectProof expected;
        if (effect.Contribution is "governs" or "shared_rule")
        {
            var realized = ReadRealizations(state);
            expected = ReadGoverning(state, scope, realized);
        }
        else if (effect.Origin == "baseline") expected = BaselineEffect(state, scope);
        else
        {
            var decisions = DeriveScopes(state).Where(s => s.Evidence!.EvidenceRole == "action" && s.Evidence.BaselineReference is null).Select(s => EffectDecision(state, s)).ToArray();
            JsonObject values;
            try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions); }
            catch (PlanningConflictException) { throw Failure(assignment.ClauseReference, "Effect grounding requires its exact completed request scope.", "INTENT_OPERATION_PROOF_MISSING"); }
            expected = ParseEffect(state, scope, values[effect.DecisionId]!.AsObject());
        }
        if (JsonSerializer.Serialize(expected, PlanningJsonContext.Default.PlanningOperationEffectProof) !=
            JsonSerializer.Serialize(effect, PlanningJsonContext.Default.PlanningOperationEffectProof))
            throw Failure(assignment.ClauseReference, "Effect mappings changed after grounding.");
    }

    internal static PlanningOperationEffectAnchor EffectAnchor(PlanningSnapshot state, PlanningObligation operation)
    {
        var root = operation.OperationAdmission!.Assignments[0];
        return root.Effect!.Candidates.Single(a => CanonicalId(state, a, root.Kind, root.BaselineReference) == operation.Id);
    }

    internal static IEnumerable<PlanningObligationRelation> EffectRelations(IEnumerable<PlanningObligation> operations) =>
        operations.SelectMany(o => o.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs.Concat(a.Effect.Producers))
            .Distinct(StringComparer.Ordinal).Select(p => new PlanningObligationRelation(p, o.Id, "data")));

    private static void ValidateEffectDependencies(PlanningSnapshot state, IReadOnlyList<PlanningObligation> operations)
    {
        var ids = operations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var relations = EffectRelations(operations).ToArray();
        foreach (var relation in relations)
            if (!ids.Contains(relation.Producer) && !state.Declarations.Any(d => d.Id == relation.Producer && d.Direction == "input"))
                throw Failure(relation.Consumer, "A producer effect has no admitted realization.");
        var visited = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw Failure(id, "Grounded effects contain a dependency cycle.");
            foreach (var relation in relations.Where(r => r.Consumer == id && ids.Contains(r.Producer))) Visit(relation.Producer);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in ids) Visit(id);
    }
}
