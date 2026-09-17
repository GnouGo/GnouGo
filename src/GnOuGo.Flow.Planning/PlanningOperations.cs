using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Canonical action authority, committed once after bounded complete-clause adjudication.</summary>
internal static partial class PlanningOperations
{
    internal static string EvidenceFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint("operation-evidence-v17:" +
        state.Request.TenantId + ":" + state.Request.SessionId + ":" + new JsonArray(PlanningIntentAssessment.IntentSources(state).OrderBy(s => s.Id, StringComparer.Ordinal)
            .Where(s => s.Authority is PlanningSourceAuthority.RequestedBehavior or PlanningSourceAuthority.ExistingBehavior)
            .Select(s => (JsonNode)new JsonArray(s.Id, s.Authority.ToString(), s.Text, s.QuestionContext)).ToArray()).ToJsonString() + ":" +
        (state.Request.Baseline is { } baseline ? PlanningGraphCompiler.Fingerprint(baseline) : "") + ":" + state.BehaviorRevision?.Text + ":" + state.RuntimeEvidenceFingerprint + ":" + ContractCoverageFingerprint(state));

    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PlanningSourceGroundingRules.ValidateAll(state);
        if (state.OperationAdmissionFingerprint is not null) { RequireCurrent(state); return; }
        RequireRuntimeEvidence(state);
        PlanningDeclarations.RequireCurrent(state);
        PlanningBaselineProjection.RequireExecutableCoverage(state);
        _ = Scopes(state);
        await GroundOccurrenceBoundaries(state, runtime, ct);
        await GroundExecutionRequests(state, runtime, ct);
        await GroundContributions(state, runtime, ct);
        await GroundCoverage(state, runtime, ct);
        await GroundApplicability(state, runtime, ct);
        var staged = ReadCompleteCoverage(state);
        staged = await PlanningSourceDecisions.ApplyRevisionAsync(state, runtime, staged, ct);
        staged = AttachApplicability(state, staged, SurvivingApplicability(state, staged));
        await ResolveDependenciesAsync(state, runtime, staged, ct);
        ValidateEffectDependencies(state, staged);
        Commit(state, staged);
        await runtime.CheckpointAsync(state, ct);
    }

    private static void Replace(List<PlanningObligation> staged, PlanningObligation operation)
    { staged.RemoveAll(o => o.Id == operation.Id); staged.Add(operation); }

    internal static PlanningOperationAssignment Assignment(PlanningSnapshot state, Scope scope, PlanningOperationEffectProof proof, string effectId, string? target, string disposition, string origin) => new(DecisionId(scope), scope.Clause.Id,
        scope.Contribution?.EvidenceReference ?? scope.Evidence!.ActionReference!, scope.Evidence?.Kind ?? "", ContributionNecessity(ContributionEvidence(state, scope), scope.Clause.Id), target, scope.Evidence?.BaselineReference)
        { ContributionId = scope.Contribution?.Id, RuntimeEvidenceId = scope.Evidence?.Id, RuntimeEvidenceIds = ContributionEvidence(state, scope).Select(e => e.Id).Order(StringComparer.Ordinal).ToList(),
            SourceBindings = scope.Contribution?.SourceBindings ?? [], Disposition = disposition, ResolutionOrigin = origin, Effect = proof, EffectId = effectId };

    internal static string Text(PlanningSnapshot state, PlanningObligation operation) => string.Join(" ",
        operation.OperationAdmission!.Assignments.Select(a => a.ClauseReference).Distinct(StringComparer.Ordinal).Select(id => PlanningChoiceEvidence.Text(state, id)));

    internal static PlanningObligation Prove(PlanningSnapshot state, PlanningObligation operation, PlanningOperationAdmission admission)
    {
        admission = admission with { Assignments = admission.Assignments.OrderBy(a => a.ContributionId, StringComparer.Ordinal).ThenBy(a => a.EffectId, StringComparer.Ordinal).ToList() };
        operation = operation with { Required = ResolveRequiredness(state, admission.Assignments) };
        operation = operation with { Grounding = PlanningSourceGroundingRules.Create(state, operation, admission.BaselineReference) };
        return operation with { OperationAdmission = admission with { ProofFingerprint = Proof(operation, admission) } };
    }

    private static string Proof(PlanningObligation operation, PlanningOperationAdmission admission) => PlanningGraphCompiler.Fingerprint(
        new JsonArray(operation.Id, operation.Kind, operation.Required, operation.Grounding?.Fingerprint).ToJsonString() + ":" +
        JsonSerializer.Serialize(admission with { ProofFingerprint = "", Assignments = admission.Assignments.OrderBy(a => a.ContributionId, StringComparer.Ordinal).ThenBy(a => a.EffectId, StringComparer.Ordinal).ToList() }, PlanningJsonContext.Default.PlanningOperationAdmission));

    internal static void Commit(PlanningSnapshot state, List<PlanningObligation> operations)
    {
        if (operations.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != operations.Count) throw Failure("$plan", "Canonical operations have duplicate identities.");
        foreach (var operation in operations) Validate(state, operation);
        ValidateEffectDependencies(state, operations);
        ValidateCoverageSurvivors(state, operations);
        ValidateApplicability(state, operations);
        var initial = state.Obligations.Where(o => o.OperationAdmission is null).ToList();
        if (initial.Any(o => operations.Any(a => a.Id == o.Id))) throw Failure("$plan", "An evidence identity cannot replace a canonical action.");
        state.Obligations = initial.Concat(operations.OrderBy(o => o.Id, StringComparer.Ordinal)).ToList();
        var fingerprint = Fingerprint(state);
        if (fingerprint != state.OperationAdmissionFingerprint)
        {
            state.Events.Add(new("operation_execution_request_model", "intent_operations", DateTimeOffset.UtcNow, ReadExecutionRequests(state).Count(p => p.DecisionId is not null)));
            var qualifications = ReadContributions(state);
            state.Events.Add(new("operation_contribution_model", "intent_operations", DateTimeOffset.UtcNow,
                qualifications.Count(p => p.DecisionId is not null)));
            foreach (var group in qualifications.SelectMany(p => p.Contributions).GroupBy(c => (c.Role, c.Origin)))
                state.Events.Add(new("operation_contribution_" + group.Key.Role + "_" + group.Key.Origin,
                    "intent_operations", DateTimeOffset.UtcNow, group.Count()));
            state.Events.Add(new("operation_applicability_model", "intent_operations", DateTimeOffset.UtcNow,
                ReadApplicability(state).Count(p => p.Origin == PlanningApplicabilityOrigin.ModelApplicability)));
            state.Events.Add(new("operation_applicability_owner", "intent_operations", DateTimeOffset.UtcNow,
                ReadApplicability(state).Count(p => p.Origin == PlanningApplicabilityOrigin.DeterministicOwner)));
            state.Events.Add(new("operation_realization_coverage", "intent_operations", DateTimeOffset.UtcNow, CoverageGroups(state).Length));
            state.Events.Add(new("operation_optional_omissions", "intent_operations", DateTimeOffset.UtcNow,
                ReadCoverage(state).SelectMany(p => p.Contributions).Count(c => c.Disposition == "omitted")));
            var effects = operations.SelectMany(o => o.OperationAdmission!.Assignments).Select(a => a.Effect!).DistinctBy(e => e.DecisionId).ToArray();
            state.Events.Add(new("operation_effect_grounding_model", "intent_operations", DateTimeOffset.UtcNow, effects.Count(e => e.Origin == "model")));
            state.Events.Add(new("operation_effect_grounding_baseline", "intent_operations", DateTimeOffset.UtcNow, effects.Count(e => e.Origin == "baseline")));
            state.Events.Add(new("operation_boundary_evidence", "intent_operations", DateTimeOffset.UtcNow,
                scopesWithBoundary(state)));
            state.Events.Add(new("operation_boundary_scope_model", "intent_operations", DateTimeOffset.UtcNow,
                operations.SelectMany(o => o.OperationAdmission!.Assignments).SelectMany(a => a.Effect!.Candidates)
                    .Select(a => a.OccurrenceProof?.DecisionId).OfType<string>().Distinct(StringComparer.Ordinal).Count()));
            state.Events.Add(new("operation_identity_model", "intent_operations", DateTimeOffset.UtcNow,
                operations.SelectMany(o => o.OperationAdmission!.Assignments).Where(a => a.ResolutionOrigin == "model").Select(a => a.DecisionId).Distinct().Count()));
            var excluded = DeriveDeclarationExclusions(state);
            state.Events.Add(new("runtime_policy_engine_resolved", "intent_operations", DateTimeOffset.UtcNow,
                state.RuntimeEvidence.Count(e => e.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority)));
            state.Events.Add(new("operation_excluded_declaration_coverage", "intent_operations", DateTimeOffset.UtcNow, excluded.Count));
            foreach (var (evidence, declarations) in excluded)
                System.Diagnostics.Activity.Current?.AddEvent(new("planning.operation_declaration_covered", tags: new()
                    { ["runtime_evidence_id"] = evidence, ["declaration_ids"] = string.Join(",", declarations) }));
            var dependencies = operations.SelectMany(o => o.OperationAdmission!.Dependencies!.Assignments).ToArray();
            foreach (var group in dependencies.Where(a => a.Disposition == "data").GroupBy(a => a.Origin))
                state.Events.Add(new("operation_dependency_" + group.Key, "intent_operations", DateTimeOffset.UtcNow, group.Count()));
            state.Events.Add(new("operation_dependency_model", "intent_operations", DateTimeOffset.UtcNow,
                dependencies.Where(a => a.DecisionId is not null).Select(a => a.DecisionId).Distinct().Count()));
            state.Events.Add(new("operations_admitted", "intent_operations", DateTimeOffset.UtcNow, operations.Count));
            foreach (var group in state.RuntimeEvidence.Where(e => e.Role is "planning_directive" or "contract" or "policy").GroupBy(e => e.Role))
                state.Events.Add(new("operation_excluded_" + group.Key, "intent_operations", DateTimeOffset.UtcNow, group.Count()));
            state.Events.Add(new("operation_action_candidates", "intent_operations", DateTimeOffset.UtcNow, ReadContributions(state).SelectMany(p => p.Contributions).Count(c => c.Role == "supports")));
            foreach (var group in operations.SelectMany(o => o.OperationAdmission!.Assignments).DistinctBy(a => a.DecisionId)
                .GroupBy(a => (a.Disposition, a.ResolutionOrigin)))
                state.Events.Add(new("operation_" + group.Key.Disposition + "_" + group.Key.ResolutionOrigin, "intent_operations", DateTimeOffset.UtcNow, group.Count()));
            state.Events.Add(new("external_actions_admitted", "intent_operations", DateTimeOffset.UtcNow, operations.Count(o => o.Kind != "local_processing")));
            foreach (var operation in operations)
            {
                var proof = operation.OperationAdmission!;
                System.Diagnostics.Activity.Current?.AddEvent(new("planning.operation_admitted", tags: new()
                { ["operation_id"] = operation.Id, ["kind"] = operation.Kind, ["authority"] = operation.Grounding!.Authority.ToString(), ["governing_clauses"] = proof.Assignments.Count }));
                if (proof.Assignments.Count > 1) state.Events.Add(new("operation_evidence_reused", "intent_operations", DateTimeOffset.UtcNow, proof.Assignments.Count - 1));
            }
        }
        state.OperationAdmissionFingerprint = fingerprint;
        static int scopesWithBoundary(PlanningSnapshot snapshot) => snapshot.RuntimeEvidence.Count(e => e.OccurrenceBoundary is not null);
    }

    private static string Fingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(EvidenceFingerprint(state) + ":" +
        string.Join('|', ReadExecutionRequests(state).Select(p => p.ProofFingerprint)) + ":" +
        string.Join('|', ReadContributions(state).Select(p => p.ProofFingerprint)) + ":" +
        string.Join('|', ReadCoverage(state).OrderBy(p => p.DecisionId, StringComparer.Ordinal).Select(p => p.ProofFingerprint)) + ":" +
        string.Join('|', SurvivingApplicability(state, state.Obligations.Where(o => o.OperationAdmission is not null).ToArray()).Select(p => p.ProofFingerprint)) + ":" +
        string.Join('|', state.Obligations.Where(o => o.OperationAdmission is not null).OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.OperationAdmission!.ProofFingerprint)));

    internal static void RequireCurrent(PlanningSnapshot state)
    {
        RequireRuntimeEvidence(state);
        PlanningDeclarations.RequireCurrent(state);
        PlanningBaselineProjection.RequireExecutableCoverage(state);
        foreach (var operation in state.Obligations.Where(o => o.OperationAdmission is not null)) Validate(state, operation);
        ValidateEffectDependencies(state, state.Obligations.Where(o => o.OperationAdmission is not null).ToArray());
        ValidateCoverageSurvivors(state, state.Obligations.Where(o => o.OperationAdmission is not null).ToArray());
        ValidateApplicability(state, state.Obligations.Where(o => o.OperationAdmission is not null).ToArray());
        if (state.OperationAdmissionFingerprint is null || state.OperationAdmissionFingerprint != Fingerprint(state))
            throw Failure("$plan", "Canonical operation admission requires explicit reassessment with current evidence.", "INTENT_OPERATION_PROOF_MISSING");
    }

    private static void ValidateCoverageSurvivors(PlanningSnapshot state, IReadOnlyList<PlanningObligation> operations)
    {
        var expected = PlanningSourceDecisions.ReadRevisionProjection(state, ReadCompleteCoverage(state));
        if (!expected.Select(o => o.Id).Order(StringComparer.Ordinal).SequenceEqual(operations.Select(o => o.Id).Order(StringComparer.Ordinal)))
            throw Failure("$plan", "Realization coverage differs from surviving revision authority.");
    }

    internal static void Validate(PlanningSnapshot state, PlanningObligation operation)
    {
        var proof = operation.OperationAdmission;
        if (proof is not { Version: 17 } || proof.CanonicalId != operation.Id || operation.Disposition != "admitted" ||
            operation.EvidenceReferences.Count != 1 || operation.EvidenceReferences[0] != proof.AnchorReference ||
            proof.EvidenceFingerprint != EvidenceFingerprint(state) || proof.Assignments.Count == 0 ||
            proof.Assignments.Select(a => a.ContributionId).Distinct(StringComparer.Ordinal).Count() != proof.Assignments.Count || proof.ProofFingerprint != Proof(operation, proof))
            throw Failure(operation.Id, "Current canonical admission proof is missing or stale.", "INTENT_OPERATION_PROOF_MISSING");
        if (operation.Grounding != PlanningSourceGroundingRules.Create(state, operation, proof.BaselineReference))
            throw Failure(operation.Id, "Canonical operation source authority changed.");
        if (operation.Required != ResolveRequiredness(state, proof.Assignments))
            throw Failure(operation.Id, "Canonical requiredness differs from its current necessity evidence.");
        var support = proof.Assignments.Where(a => a.Disposition == "supports").ToArray();
        if (support.Length == 0 || support.Any(a => a.EffectId != operation.Id || a.BaselineReference != proof.BaselineReference) ||
            proof.AnchorReference != support.Select(a => a.ActionReference).Order(StringComparer.Ordinal).First())
            throw Failure(operation.Id, "Canonical operation authority requires aggregate executable coverage.");
        var coverage = ReadCoverage(state).SingleOrDefault(p => p.SelectedEffects.Contains(operation.Id));
        if (JsonSerializer.Serialize(coverage, PlanningJsonContext.Default.PlanningRealizationCoverageProof) !=
            JsonSerializer.Serialize(proof.RealizationCoverage, PlanningJsonContext.Default.PlanningRealizationCoverageProof))
            throw Failure(operation.Id, "Realization coverage is stale or incomplete.");
        var expected = CoverageAssignments(state).Where(a => a.EffectId == operation.Id).ToArray();
        if (expected.Any(a => !proof.Assignments.Any(b => b.ContributionId == a.ContributionId && b.Disposition == a.Disposition)))
            throw Failure(operation.Id, "An owned contribution disappeared from realization coverage.");
        if (JsonSerializer.Serialize(ReadContributions(state).ToList(), PlanningJsonContext.Default.ListPlanningExecutionContributionProof) !=
            JsonSerializer.Serialize(proof.ExecutionContributions, PlanningJsonContext.Default.ListPlanningExecutionContributionProof))
            throw Failure(operation.Id, "Canonical contribution qualification is stale or incomplete.");
        if (JsonSerializer.Serialize(ReadExecutionRequests(state).ToList(), PlanningJsonContext.Default.ListPlanningExecutionRequestProof) !=
            JsonSerializer.Serialize(proof.ExecutionRequests, PlanningJsonContext.Default.ListPlanningExecutionRequestProof))
            throw Failure(operation.Id, "Canonical execution request authority is stale or incomplete.");
        foreach (var assignment in proof.Assignments)
        {
            ValidateAssignment(state, assignment);
            if (assignment.Disposition == "supports" && !Compatible(state, state.RuntimeEvidence.Single(e => e.Id == assignment.RuntimeEvidenceId), operation) ||
                assignment.Kind != operation.Kind || assignment.EffectId != operation.Id ||
                assignment.TargetId != operation.Id || assignment.BaselineReference is { } baseline && baseline != proof.BaselineReference)
                throw Failure(operation.Id, "Operation evidence contradicts its established contract.");
        }
    }

    private static void ValidateAssignment(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        if (!PlanningSourceGroundingRules.OperationKinds.Contains(assignment.Kind, StringComparer.Ordinal) ||
            !PlanningChoiceEvidence.Current(state, assignment.ClauseReference) || !PlanningChoiceEvidence.Current(state, assignment.ActionReference) ||
            PlanningChoiceEvidence.Parent(state, assignment.ActionReference).Id != assignment.ClauseReference ||
            assignment.DecisionId != "operation_" + (assignment.RuntimeEvidenceId ?? assignment.ContributionId))
            throw Failure(assignment.ClauseReference, "An operation requires exact current clause and action references.");
        var evidence = state.RuntimeEvidence.SingleOrDefault(e => e.Id == assignment.RuntimeEvidenceId);
        if (evidence is null && assignment.Disposition != "attach")
            throw Failure(assignment.ClauseReference, "Admission requires current runtime evidence, not an interpretation label.");
        foreach (var binding in AssignmentEvidence(state, assignment))
        {
            ValidateRuntime(state, binding);
            if (assignment.Disposition == "supports") RequireEligibleContribution(state, binding, state.References.Single(r => r.Id == assignment.ActionReference));
        }
        var qualification = ReadContributions(state).SelectMany(p => p.Contributions)
            .SingleOrDefault(c => c.Id == assignment.ContributionId) ?? throw Failure(assignment.ClauseReference, "Current contribution authority is missing.");
        ValidateSourceBindings(state, qualification, assignment.ClauseReference);
        if (!qualification.RuntimeEvidenceIds.SequenceEqual(assignment.RuntimeEvidenceIds) || assignment.RuntimeEvidenceId != qualification.RuntimeEvidenceIds.FirstOrDefault() ||
            !qualification.SourceBindings.SequenceEqual(assignment.SourceBindings))
            throw Failure(assignment.ClauseReference, "Assignment provenance differs from its current clause qualification.");
        if (assignment.Disposition == "attach")
        {
            if (qualification.Role != "governing_property" || qualification.EffectId is not null ||
                qualification.EvidenceReference != assignment.ActionReference || ContributionNecessity(AssignmentEvidence(state, assignment), assignment.ClauseReference) != assignment.Necessity)
                throw Failure(assignment.ClauseReference, "Only current qualified properties may supply governing attachments.");
            var target = AttachApplicability(state, MaterializeCoverage(state), ReadApplicability(state))
                .SingleOrDefault(o => o.Id == assignment.TargetId);
            var expected = target?.OperationAdmission!.Assignments.SingleOrDefault(a => a.ContributionId == assignment.ContributionId);
            if (expected is null || JsonSerializer.Serialize(expected, PlanningJsonContext.Default.PlanningOperationAssignment) !=
                JsonSerializer.Serialize(assignment, PlanningJsonContext.Default.PlanningOperationAssignment))
                throw Failure(assignment.ClauseReference, "Governing assignment differs from its current applicability proof.");
            return;
        }
        if (qualification.EvidenceReference != assignment.ActionReference || qualification.EffectId != assignment.EffectId ||
            qualification.Role != (assignment.Disposition == "supports" ? "supports" : "governs") || evidence!.ClauseReference != assignment.ClauseReference ||
            evidence.Kind != assignment.Kind || ContributionNecessity(AssignmentEvidence(state, assignment), assignment.ClauseReference) != assignment.Necessity || evidence.BaselineReference != assignment.BaselineReference ||
            (assignment.Disposition == "supports" ? assignment.TargetId != assignment.EffectId || assignment.Effect?.Contribution != "realizes" :
                assignment.Disposition == "attach" ? assignment.TargetId != assignment.EffectId || assignment.Effect?.Contribution is not ("governs" or "shared_rule") : true) ||
            assignment.ResolutionOrigin is not ("deterministic" or "model"))
            throw Failure(assignment.ClauseReference, "The assignment changed its proven execution boundary or occurrence.");
        RequireCompatibleContributionFacts(AssignmentEvidence(state, assignment), assignment.ClauseReference);
        ValidateEffect(state, assignment);
        if (!assignment.Effect!.Candidates.Any(a => CanonicalId(state, a, assignment.Kind, assignment.BaselineReference) == assignment.EffectId))
            throw Failure(assignment.ClauseReference, "Operation identity is outside its grounded effect domain.");
        if (assignment.ResolutionOrigin == "model")
        {
            var scope = DeriveScopes(state).Single(s => s.Evidence!.Id == evidence.Id);
            var identities = assignment.Effect.Candidates.Select(a => CanonicalId(state, a, assignment.Kind, assignment.BaselineReference)).Order(StringComparer.Ordinal).ToArray();
            var decision = IdentityDecision(state, scope, assignment.Effect, identities);
            var values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", [decision]);
            if (values[decision.Id]?.ToString() != assignment.EffectId)
                throw Failure(assignment.ClauseReference, "The selected occurrence differs from its completed canonical-ID decision.");
        }
        else if (assignment.Effect.Candidates.Count != 1 && assignment.Effect.Contribution != "shared_rule")
            throw Failure(assignment.ClauseReference, "Multiple proven identities require a bounded selection.");
        var reference = state.References.Single(r => r.Id == assignment.ActionReference);
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == reference.SourceId);
        if (source.Authority == PlanningSourceAuthority.RequestedBehavior && assignment.BaselineReference is null) return;
        if (source.Authority != PlanningSourceAuthority.ExistingBehavior || assignment.BaselineReference is null ||
            !PlanningSourceGroundingRules.BaselineNodes(state).TryGetValue(assignment.BaselineReference, out var node) ||
            !BaselineKinds(node.Node).Contains(assignment.Kind, StringComparer.Ordinal))
            throw Failure(assignment.ClauseReference, "The source cannot introduce this action or prove its baseline execution boundary.");
    }

    private static WorkflowRuntimeException Failure(string reference, string message, string code = "INTENT_OPERATION_UNRESOLVED")
    {
        var location = "/operations/@" + PlanningFieldPaths.Escape(reference);
        System.Diagnostics.Activity.Current?.AddEvent(new("planning.operation_unresolved", tags: new() { ["code"] = code, ["location"] = location }));
        return new(code, message, details: new JsonObject { ["location"] = location });
    }
}
