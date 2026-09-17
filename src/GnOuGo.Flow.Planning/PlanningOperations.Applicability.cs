using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    internal static string ApplicabilityDecisionId(Scope scope) => "applicability_" + scope.Contribution!.Id;
    private static Scope[] PropertyScopes(PlanningSnapshot state) => QualifiedScopes(state)
        .Where(s => s.Contribution!.Role == "governing_property").ToArray();

    // Ephemeral evaluation cache for one proof read, never persisted or authoritative.
    private sealed record ApplicabilityContext(Scope[] Properties, PlanningRealizationCoverageProof[] Coverage,
        CoverageGroup[] Groups, List<PlanningObligation> Realizations, PlanningExecutionContributionProof[] Qualifications,
        string RealizedFingerprint);
    private static ApplicabilityContext ApplicabilityContextFor(PlanningSnapshot state)
    {
        var coverage = ReadCoverage(state);
        return new(PropertyScopes(state), coverage, CoverageGroups(state), MaterializeCoverage(state), ReadContributions(state),
            PlanningGraphCompiler.Fingerprint(string.Join('|', coverage.Select(p => p.ProofFingerprint))));
    }

    // A consideration domain is not an attachment proof. In particular, preliminary
    // execution kind, role, necessity, clause identity and cardinality confer no applicability.
    internal static Dictionary<string, PlanningOperationEffectAnchor> ApplicabilityDomain(PlanningSnapshot state, Scope scope)
        => ApplicabilityDomain(state, scope, MaterializeCoverage(state));
    private static Dictionary<string, PlanningOperationEffectAnchor> ApplicabilityDomain(PlanningSnapshot state, Scope scope,
        IReadOnlyList<PlanningObligation> operations)
    {
        var realized = operations.ToDictionary(o => o.Id, o => EffectAnchor(state, o), StringComparer.Ordinal);
        var owner = scope.Source.Baseline;
        if (owner?.OwnerKind == "node")
            return realized.Where(p => p.Value.BoundaryKind == "baseline" && p.Value.OwnerReference ==
                PlanningBaselineProjection.NodeReference(state, owner)).ToDictionary();
        var workflow = owner?.Workflow;
        var workflows = ContributionEvidence(state, scope).Where(e => e.OccurrenceBoundary is not null)
            .Select(e => ReadOccurrenceBoundary(state, e).WorkflowScope).Append(workflow).OfType<string>().Distinct().ToArray();
        if (workflows.Length > 1) throw Failure(scope.Clause.Id, "Governing provenance has conflicting workflow owners.");
        workflow = workflows.SingleOrDefault();
        return realized.Where(p => workflow is null || p.Value.WorkflowScope == workflow).ToDictionary();
    }

    private static List<string> ExactPropertyOwner(PlanningSnapshot state, Scope scope, PlanningOperationEffectAnchor anchor)
    {
        if (scope.Source.Baseline is { OwnerKind: "node" } owner && anchor.BoundaryKind == "baseline" &&
            anchor.OwnerReference == PlanningBaselineProjection.NodeReference(state, owner))
            return [scope.Contribution!.EvidenceReference, anchor.OwnerReference];
        var owned = ContributionEvidence(state, scope).Where(e => e.OccurrenceBoundary is not null)
            .Select(e => ReadOccurrenceBoundary(state, e)).ToArray();
        if (owned.Length != 0 && anchor.OccurrenceProof is { } occurrence)
        {
            if (owned.Any(p => p.WorkflowScope != occurrence.WorkflowScope || p.Evidence != occurrence.Evidence)) return [];
            var proof = owned[0];
            if (proof.WorkflowScope == occurrence.WorkflowScope && proof.Evidence == occurrence.Evidence)
                return new[] { proof.Evidence.OwnerReference, proof.Evidence.BoundaryReference }
                    .Distinct().Order(StringComparer.Ordinal).ToList();
        }
        return [];
    }

    private static string ApplicabilityDomainFingerprint(PlanningSnapshot state, Scope scope, ApplicabilityContext context) => PlanningGraphCompiler.Fingerprint(
        "governing-applicability-v2:" + context.RealizedFingerprint + ":" +
        context.Qualifications.Single(p => p.Contributions.Any(c => c.Id == scope.Contribution!.Id)).ProofFingerprint + ":" +
        CoverageStrings(ApplicabilityDomain(state, scope, context.Realizations).Keys).ToJsonString());

    private static PlanningGoverningApplicabilityProof SealApplicability(PlanningGoverningApplicabilityProof proof)
    {
        proof = proof with
        {
            Targets = proof.Targets.Distinct().Order(StringComparer.Ordinal).ToList(),
            TargetEvidence = proof.TargetEvidence.OrderBy(e => e.Target, StringComparer.Ordinal).Select(e => e with
                { EvidenceReferences = e.EvidenceReferences.Distinct().Order(StringComparer.Ordinal).ToList() }).ToList(),
            OwnerReferences = proof.OwnerReferences.Distinct().Order(StringComparer.Ordinal).ToList()
        };
        return proof with { ProofFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(
            proof with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningGoverningApplicabilityProof)) };
    }

    private static PlanningGoverningApplicabilityProof PropertyProof(PlanningSnapshot state, Scope scope, ApplicabilityContext context, string outcome,
        List<string> targets, List<PlanningGoverningTargetEvidence> evidence, List<string> owners,
        PlanningApplicabilityOrigin origin, string? decision = null, string? workflow = null, string? inactive = null) => SealApplicability(new(
            2, scope.Contribution!.Id, scope.Evidence?.Id, scope.Contribution.EvidenceReference, outcome, targets, workflow,
            evidence, owners, inactive, origin, decision, context.RealizedFingerprint, ApplicabilityDomainFingerprint(state, scope, context), "")
            { SourceBindings = scope.Contribution.SourceBindings });

    private static PlanningGoverningApplicabilityProof? DeterministicApplicability(PlanningSnapshot state, Scope scope, ApplicabilityContext context)
    {
        // Workflow annotations retain their exact canonical owner; this is not a
        // broadcast to all operations, nor a model escape from missing applicability.
        if (scope.Source.Baseline is { OwnerKind: "workflow", Workflow: { } workflow })
            return PropertyProof(state, scope, context, "workflow", [], [], [scope.Contribution!.EvidenceReference],
                PlanningApplicabilityOrigin.DeterministicOwner, workflow: workflow);
        var owned = ApplicabilityDomain(state, scope, context.Realizations).Select(p => (p.Key, Owner: ExactPropertyOwner(state, scope, p.Value)))
            .Where(p => p.Owner.Count != 0).ToArray();
        if (owned.Length == 1)
            return PropertyProof(state, scope, context, "active", [owned[0].Key], [new(owned[0].Key, [scope.Contribution!.EvidenceReference])],
                owned[0].Owner, PlanningApplicabilityOrigin.DeterministicOwner);
        // Omission may deactivate an exactly owned property, never retarget it.
        foreach (var group in context.Groups)
        foreach (var effect in group.Effects)
        {
            var owner = ExactPropertyOwner(state, scope, effect.Value);
            if (owner.Count == 0) continue;
            var coverage = context.Coverage.Single(p => p.DecisionId == group.Id);
            var contributions = group.Scopes.Where(s => s.Contribution!.EffectId == effect.Key).Select(s => s.Contribution!.Id).ToArray();
            if (contributions.Length != 0 && contributions.All(id => coverage.Contributions.Any(c => c.ContributionId == id && c.Disposition == "omitted")))
                return PropertyProof(state, scope, context, "inactive", [], [], owner, PlanningApplicabilityOrigin.DeterministicInactive,
                    inactive: coverage.ProofFingerprint);
        }
        return null;
    }

    internal static PlanningDecisionPages.Decision ApplicabilityDecision(PlanningSnapshot state, Scope scope)
        => ApplicabilityDecision(state, scope, ApplicabilityContextFor(state));
    private static PlanningDecisionPages.Decision ApplicabilityDecision(PlanningSnapshot state, Scope scope, ApplicabilityContext context)
    {
        var domain = ApplicabilityDomain(state, scope, context.Realizations);
        if (domain.Count == 0) throw Failure(scope.Contribution!.Id, "Qualified governing evidence has no realized applicability target or proven inactive owner.");
        var refs = new[] { scope.Contribution!.EvidenceReference, scope.Clause.Id }.Distinct().Order(StringComparer.Ordinal).ToArray();
        var binding = PlanningHoleRequests.Object(("target", PlanningHoleRequests.Enum(domain.Keys.ToArray())),
            ("evidence", ReferencesArray(refs, 1)));
        var schema = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))),
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["active"])), ("bindings", new JsonObject
            { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = domain.Count, ["items"] = binding }))) };
        return new(ApplicabilityDecisionId(scope), schema, new()
        {
            ["stage"] = "governing_applicability",
            ["task"] = "Prove which realized executions this qualified property governs. Every selected target needs owned supporting applicability evidence; explicitly shared rules may bind several targets. The consideration domain, same clause, shared workflow and a single available target do not prove applicability. Return unresolved if applicability cannot be established. Do not change execution, ownership, necessity, dataflow or other policy responsibilities.",
            ["property"] = PlanningChoiceEvidence.Text(state, scope.Contribution.EvidenceReference),
            ["clause"] = PlanningChoiceEvidence.Text(state, scope.Clause.Id),
            ["references"] = new JsonObject(refs.Select(id => new KeyValuePair<string, JsonNode?>(id, JsonValue.Create(PlanningChoiceEvidence.Text(state, id))))),
            ["realized"] = new JsonObject(context.Realizations.Where(o => domain.ContainsKey(o.Id)).Select(o =>
                new KeyValuePair<string, JsonNode?>(o.Id, new JsonObject { ["kind"] = o.Kind, ["scope"] = domain[o.Id].WorkflowScope,
                    ["owner"] = domain[o.Id].OwnerReference, ["executionEvidence"] = Text(state, o) })))
        }, ApplicabilityDomainFingerprint(state, scope, context), SourceDecisionIds: [scope.Contribution.Id]);
    }

    internal static PlanningDecisionPages.Decision[] ApplicabilityDecisions(PlanningSnapshot state) => ApplicabilityDecisions(state, ApplicabilityContextFor(state));
    private static PlanningDecisionPages.Decision[] ApplicabilityDecisions(PlanningSnapshot state, ApplicabilityContext context) => context.Properties
        .Where(s => DeterministicApplicability(state, s, context) is null).Select(s => ApplicabilityDecision(state, s, context)).ToArray();

    internal static PlanningGoverningApplicabilityProof[] ReadApplicability(PlanningSnapshot state)
    {
        var context = ApplicabilityContextFor(state);
        var decisions = ApplicabilityDecisions(state, context);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", decisions); }
        catch (PlanningConflictException) { throw Failure("$plan", "Current governing applicability is missing.", "INTENT_OPERATION_PROOF_MISSING"); }
        return context.Properties.Select(scope =>
        {
            if (DeterministicApplicability(state, scope, context) is { } deterministic) return deterministic;
            var decision = decisions.Single(d => d.Id == ApplicabilityDecisionId(scope));
            var answer = values[decision.Id] as JsonObject;
            if (answer is null || PlanningContractValidation.ValidateInstance(answer, decision.Schema).Count != 0)
                throw Failure(scope.Contribution!.Id, "Governing applicability changed its issued targets or owned references.");
            if (answer["status"]!.ToString() != "active") throw Failure(scope.Contribution!.Id, "Governing applicability is unresolved.");
            var bindings = answer["bindings"]!.AsArray().Select(b => new PlanningGoverningTargetEvidence(b!["target"]!.ToString(),
                b["evidence"]!.AsArray().Select(e => e!.ToString()).ToList())).ToList();
            if (bindings.Select(b => b.Target).Distinct().Count() != bindings.Count ||
                bindings.Any(b => b.EvidenceReferences.Distinct().Count() != b.EvidenceReferences.Count))
                throw Failure(scope.Contribution!.Id, "Duplicate applicability selections cannot establish additional proof.");
            return PropertyProof(state, scope, context, "active", bindings.Select(b => b.Target).ToList(), bindings, [],
                PlanningApplicabilityOrigin.ModelApplicability, decision.Id);
        }).OrderBy(p => p.ContributionId, StringComparer.Ordinal).ToArray();
    }

    private static async Task GroundApplicability(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", ApplicabilityDecisions(state), ct);
        _ = ReadApplicability(state);
    }

    internal static List<PlanningObligation> AttachApplicability(PlanningSnapshot state, List<PlanningObligation> operations,
        IReadOnlyList<PlanningGoverningApplicabilityProof> proofs)
    {
        var scopes = PropertyScopes(state).ToDictionary(s => s.Contribution!.Id, StringComparer.Ordinal);
        foreach (var operation in operations.ToArray())
        {
            var assignments = operation.OperationAdmission!.Assignments.Where(a => a.Disposition == "supports").ToList();
            foreach (var proof in proofs.Where(p => p.Outcome == "active" && p.Targets.Contains(operation.Id)))
            {
                var scope = scopes[proof.ContributionId];
                var mapping = new PlanningOperationEffectProof(7, proof.DecisionId ?? "applicability_owner_" + proof.ContributionId,
                    proof.Targets.Count > 1 ? "shared_rule" : "governs", [EffectAnchor(state, operation)], [], [], [],
                    proof.TargetEvidence.Single(e => e.Target == operation.Id).EvidenceReferences,
                    proof.Origin == PlanningApplicabilityOrigin.ModelApplicability ? "applicability_model" : "applicability_owner", proof.ProofFingerprint);
                assignments.Add(Assignment(state, scope, mapping, operation.Id, operation.Id, "attach", "deterministic") with
                { Kind = operation.Kind, BaselineReference = operation.OperationAdmission.BaselineReference });
            }
            Replace(operations, Prove(state, operation, operation.OperationAdmission! with
            { Assignments = assignments, GoverningApplicability = proofs.ToList() }));
        }
        return operations;
    }

    private static PlanningGoverningApplicabilityProof[] SurvivingApplicability(PlanningSnapshot state, IReadOnlyList<PlanningObligation> survivors)
    {
        var original = MaterializeCoverage(state);
        return ReadApplicability(state).Select(proof =>
        {
            var retired = proof.Targets.Where(id => survivors.All(o => o.Id != id)).ToArray();
            if (retired.Length == 0) return proof;
            var scope = PropertyScopes(state).Single(s => s.Contribution!.Id == proof.ContributionId);
            if (retired.Length != proof.Targets.Count || retired.Length != 1 ||
                ExactPropertyOwner(state, scope, EffectAnchor(state, original.Single(o => o.Id == retired[0]))).Count == 0)
                throw Failure(proof.ContributionId, "Retired governing targets require exact owner and revision proof; automatic retargeting is forbidden.");
            var revision = PlanningSourceDecisions.OperationRevisionEvidence(state, ReadCompleteCoverage(state).Single(o => o.Id == retired[0]));
            return SealApplicability(proof with { Outcome = "inactive", Targets = [], TargetEvidence = [],
                OwnerReferences = proof.OwnerReferences.Concat(revision.References).ToList(),
                InactiveProofFingerprint = revision.Fingerprint, Origin = PlanningApplicabilityOrigin.DeterministicInactive });
        }).ToArray();
    }

    private static void ValidateApplicability(PlanningSnapshot state, IReadOnlyList<PlanningObligation> operations)
    {
        var complete = ReadCompleteCoverage(state);
        var survivors = PlanningSourceDecisions.ReadRevisionProjection(state, complete);
        var proofs = SurvivingApplicability(state, survivors);
        var expected = AttachApplicability(state, survivors, proofs);
        foreach (var operation in operations)
        {
            var match = expected.Single(o => o.Id == operation.Id).OperationAdmission!;
            if (JsonSerializer.Serialize(operation.OperationAdmission!.GoverningApplicability, PlanningJsonContext.Default.ListPlanningGoverningApplicabilityProof) !=
                JsonSerializer.Serialize(proofs.ToList(), PlanningJsonContext.Default.ListPlanningGoverningApplicabilityProof) ||
                JsonSerializer.Serialize(operation.OperationAdmission.Assignments, PlanningJsonContext.Default.ListPlanningOperationAssignment) !=
                JsonSerializer.Serialize(match.Assignments, PlanningJsonContext.Default.ListPlanningOperationAssignment))
                throw Failure(operation.Id, "Governing applicability is incomplete, foreign or stale.", "INTENT_OPERATION_PROOF_MISSING");
        }
    }

    internal static bool EstablishedPolicyApplicability(PlanningSnapshot state, PlanningObligation policy, PlanningObligation operation)
    {
        // Residual projection can split evidence or trim surrounding whitespace.
        // Reuse only complete coverage bound to this exact policy grounding and
        // current target, never overlap or membership in the same clause alone.
        var admission = operation.OperationAdmission!;
        var evidence = admission.ExecutionContributions.SelectMany(p => p.Contributions)
            .Where(c => c.Role == "governing_property" && c.SourceBindings.Any(b => b.SemanticObligationId == policy.Id &&
                b.GroundingFingerprint == policy.Grounding?.Fingerprint) && admission.GoverningApplicability.Any(p =>
                p.Outcome == "active" && p.Targets.Contains(operation.Id) && p.ContributionId == c.Id && p.EvidenceReference == c.EvidenceReference))
            .Select(c => state.References.Single(r => r.Id == c.EvidenceReference)).ToArray();
        return policy.EvidenceReferences.Count != 0 && policy.EvidenceReferences.All(id =>
            state.References.Single(r => r.Id == id) is var reference && Covered(state, reference, evidence.Where(r => ContainsSpan(reference, r))));
    }
}
