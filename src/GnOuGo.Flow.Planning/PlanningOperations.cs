using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Canonical action authority, committed once after bounded complete-clause adjudication.</summary>
internal static partial class PlanningOperations
{
    internal static string EvidenceFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint("operation-evidence-v7:" +
        state.Request.TenantId + ":" + state.Request.SessionId + ":" + new JsonArray(PlanningIntentAssessment.IntentSources(state)
            .Where(s => s.Authority is PlanningSourceAuthority.RequestedBehavior or PlanningSourceAuthority.ExistingBehavior)
            .Select(s => (JsonNode)new JsonArray(s.Id, s.Authority.ToString(), s.Text, s.QuestionContext)).ToArray()).ToJsonString() + ":" +
        (state.Request.Baseline is { } baseline ? PlanningGraphCompiler.Fingerprint(baseline) : "") + ":" + state.BehaviorRevision?.Text + ":" + state.RuntimeEvidenceFingerprint + ":" + state.DeclarationFingerprint);

    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        if (state.OperationAdmissionFingerprint is not null) { RequireCurrent(state); return; }
        RequireRuntimeEvidence(state);
        PlanningDeclarations.RequireCurrent(state);
        var staged = new List<PlanningObligation>();
        var scopes = Scopes(state);
        var mappings = await GroundRealizations(state, runtime, scopes, ct);
        // Realizations and identity selections precede every governing target domain.
        foreach (var scope in scopes.Where(s => mappings.GetValueOrDefault(s.Evidence!.Id)?.Contribution == "realizes"))
        {
            var proof = mappings[scope.Evidence!.Id];
            var ids = proof.Candidates.Select(a => CanonicalId(state, a, scope.Evidence.Kind!, scope.Evidence.BaselineReference)).ToArray();
            var selected = await SelectIdentity(state, runtime, scope, proof, ids, ct);
            var existing = staged.SingleOrDefault(o => o.Id == selected);
            var assignment = Assignment(scope, proof, selected, existing?.Id, existing is null ? "distinct" : "same_as", ids.Length == 1 ? "deterministic" : "model");
            if (existing is null) staged.Add(Create(state, assignment));
            else Replace(staged, Extend(state, existing, assignment));
        }
        var realized = ReadRealizations(state);
        var governing = await GroundGoverning(state, runtime, GoverningScopes(scopes, mappings), realized, ct);
        foreach (var scope in scopes.Where(s => governing.GetValueOrDefault(s.Evidence!.Id)?.Contribution is "governs" or "shared_rule"))
        {
            var proof = governing[scope.Evidence!.Id];
            var candidates = proof.Candidates.Select(a => CanonicalId(state, a, scope.Evidence.Kind!, scope.Evidence.BaselineReference)).ToHashSet(StringComparer.Ordinal);
            var targets = staged.Where(o => candidates.Contains(o.Id) && Compatible(state, scope.Evidence, o)).Select(o => o.Id).Order(StringComparer.Ordinal).ToArray();
            if (targets.Length != candidates.Count) throw Failure(scope.Clause.Id, "Governing effect evidence requires established compatible realizations.");
            var selected = proof.Contribution == "shared_rule" ? targets : [targets.Single()];
            foreach (var id in selected)
                Replace(staged, Extend(state, staged.Single(o => o.Id == id), Assignment(scope, proof, id, id, "attach",
                    "deterministic")));
        }
        staged = await PlanningSourceDecisions.ApplyRevisionAsync(state, runtime, staged, ct);
        await ResolveDependenciesAsync(state, runtime, staged, ct);
        ValidateEffectDependencies(state, staged);
        Commit(state, staged);
        await runtime.CheckpointAsync(state, ct);
    }

    private static async Task<string> SelectIdentity(PlanningSnapshot state, IPlanningRuntime runtime, Scope scope,
        PlanningOperationEffectProof proof, string[] identities, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (identities.Length == 0) throw Failure(scope.Clause.Id, "No proven effect identity is available.");
        if (identities.Length == 1) return identities[0];
        var decision = IdentityDecision(state, scope, proof, identities);
        var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", [decision], ct);
        return response[decision.Id]!.ToString();
    }

    private static void Replace(List<PlanningObligation> staged, PlanningObligation operation)
    { staged.RemoveAll(o => o.Id == operation.Id); staged.Add(operation); }

    internal static PlanningOperationAssignment Assignment(Scope scope, PlanningOperationEffectProof proof, string effectId, string? target, string disposition, string origin) => new(DecisionId(scope), scope.Clause.Id,
        scope.Evidence!.ActionReference!, scope.Evidence.Kind!, scope.Evidence.Necessity, target, scope.Evidence.BaselineReference)
        { RuntimeEvidenceId = scope.Evidence.Id, Disposition = disposition, ResolutionOrigin = origin, Effect = proof, EffectId = effectId };

    internal static PlanningObligation Create(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        ValidateAssignment(state, assignment);
        if (assignment.TargetId is not null) throw Failure(assignment.ClauseReference, "A new operation cannot borrow another action identity.");
        var anchor = state.References.Single(r => r.Id == assignment.ActionReference);
        var id = assignment.EffectId!;
        var operation = new PlanningObligation(id, [anchor.Id], assignment.Kind == "local_processing" ? "workflow" : "capability_contract", assignment.Kind, ResolveRequiredness(state, [assignment]));
        operation = operation with { Grounding = PlanningSourceGroundingRules.Create(state, operation, assignment.BaselineReference), Disposition = "admitted" };
        return Prove(state, operation, new(7, id, anchor.Id, assignment.BaselineReference, [assignment], EvidenceFingerprint(state), ""));
    }

    private static PlanningObligation Extend(PlanningSnapshot state, PlanningObligation operation, PlanningOperationAssignment assignment)
    {
        Validate(state, operation); ValidateAssignment(state, assignment);
        if (!Compatible(state, state.RuntimeEvidence.Single(e => e.Id == assignment.RuntimeEvidenceId), operation) ||
            assignment.TargetId != operation.Id || assignment.Kind != operation.Kind ||
            assignment.BaselineReference is { } baseline && baseline != operation.OperationAdmission!.BaselineReference)
            throw Failure(assignment.ClauseReference, "The reuse evidence contradicts the established kind or baseline ownership.");
        if (operation.OperationAdmission!.Assignments.Any(a => a.RuntimeEvidenceId == assignment.RuntimeEvidenceId))
            throw Failure(assignment.ClauseReference, "Repeated evidence cannot authorize another attachment.");
        return Prove(state, operation, operation.OperationAdmission! with { Assignments = [.. operation.OperationAdmission!.Assignments, assignment] });
    }

    internal static string Text(PlanningSnapshot state, PlanningObligation operation) => string.Join(" ",
        operation.OperationAdmission!.Assignments.Select(a => a.ClauseReference).Distinct(StringComparer.Ordinal).Select(id => PlanningChoiceEvidence.Text(state, id)));

    internal static PlanningObligation Prove(PlanningSnapshot state, PlanningObligation operation, PlanningOperationAdmission admission)
    {
        operation = operation with { Required = ResolveRequiredness(state, admission.Assignments) };
        operation = operation with { Grounding = PlanningSourceGroundingRules.Create(state, operation, admission.BaselineReference) };
        return operation with { OperationAdmission = admission with { ProofFingerprint = Proof(operation, admission) } };
    }

    private static string Proof(PlanningObligation operation, PlanningOperationAdmission admission) => PlanningGraphCompiler.Fingerprint(
        new JsonArray(operation.Id, operation.Kind, operation.Required, operation.Grounding?.Fingerprint).ToJsonString() + ":" +
        JsonSerializer.Serialize(admission with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningOperationAdmission));

    internal static void Commit(PlanningSnapshot state, List<PlanningObligation> operations)
    {
        if (operations.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != operations.Count) throw Failure("$plan", "Canonical operations have duplicate identities.");
        foreach (var operation in operations) Validate(state, operation);
        ValidateEffectDependencies(state, operations);
        var initial = state.Obligations.Where(o => o.OperationAdmission is null).ToList();
        if (initial.Any(o => operations.Any(a => a.Id == o.Id))) throw Failure("$plan", "An evidence identity cannot replace a canonical action.");
        state.Obligations = initial.Concat(operations.OrderBy(o => o.Id, StringComparer.Ordinal)).ToList();
        var fingerprint = Fingerprint(state);
        if (fingerprint != state.OperationAdmissionFingerprint)
        {
            var effects = operations.SelectMany(o => o.OperationAdmission!.Assignments).Select(a => a.Effect!).DistinctBy(e => e.DecisionId).ToArray();
            state.Events.Add(new("operation_effect_grounding_model", "intent_operations", DateTimeOffset.UtcNow, effects.Count(e => e.Origin == "model")));
            state.Events.Add(new("operation_effect_grounding_baseline", "intent_operations", DateTimeOffset.UtcNow, effects.Count(e => e.Origin == "baseline")));
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
            state.Events.Add(new("operation_action_candidates", "intent_operations", DateTimeOffset.UtcNow, state.RuntimeEvidence.Count(e => e.EvidenceRole == "action" && !excluded.ContainsKey(e.Id))));
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
    }

    private static string Fingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(EvidenceFingerprint(state) + ":" +
        string.Join('|', state.Obligations.Where(o => o.OperationAdmission is not null).OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.OperationAdmission!.ProofFingerprint)));

    internal static void RequireCurrent(PlanningSnapshot state)
    {
        RequireRuntimeEvidence(state);
        PlanningDeclarations.RequireCurrent(state);
        foreach (var operation in state.Obligations.Where(o => o.OperationAdmission is not null)) Validate(state, operation);
        ValidateEffectDependencies(state, state.Obligations.Where(o => o.OperationAdmission is not null).ToArray());
        if (state.OperationAdmissionFingerprint is null || state.OperationAdmissionFingerprint != Fingerprint(state))
            throw Failure("$plan", "Canonical operation admission requires explicit reassessment with current evidence.", "INTENT_OPERATION_PROOF_MISSING");
    }

    internal static void Validate(PlanningSnapshot state, PlanningObligation operation)
    {
        var proof = operation.OperationAdmission;
        if (proof is not { Version: 7 } || proof.CanonicalId != operation.Id || operation.Disposition != "admitted" ||
            operation.EvidenceReferences.Count != 1 || operation.EvidenceReferences[0] != proof.AnchorReference ||
            proof.EvidenceFingerprint != EvidenceFingerprint(state) || proof.Assignments.Count == 0 || proof.ProofFingerprint != Proof(operation, proof))
            throw Failure(operation.Id, "Current canonical admission proof is missing or stale.", "INTENT_OPERATION_PROOF_MISSING");
        if (operation.Grounding != PlanningSourceGroundingRules.Create(state, operation, proof.BaselineReference))
            throw Failure(operation.Id, "Canonical operation source authority changed.");
        if (operation.Required != ResolveRequiredness(state, proof.Assignments))
            throw Failure(operation.Id, "Canonical requiredness differs from its current necessity evidence.");
        var root = proof.Assignments[0];
        if (root.ActionReference != proof.AnchorReference || root.TargetId is not null || root.BaselineReference != proof.BaselineReference ||
            root.EffectId != operation.Id)
            throw Failure(operation.Id, "Canonical operation identity is not established by its root evidence.");
        foreach (var assignment in proof.Assignments)
        {
            ValidateAssignment(state, assignment);
            if (!Compatible(state, state.RuntimeEvidence.Single(e => e.Id == assignment.RuntimeEvidenceId), operation) ||
                assignment.Kind != operation.Kind || assignment.EffectId != operation.Id ||
                assignment != root && assignment.TargetId != operation.Id || assignment.BaselineReference is { } baseline && baseline != proof.BaselineReference)
                throw Failure(operation.Id, "Operation evidence contradicts its established contract.");
        }
    }

    private static void ValidateAssignment(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        if (!PlanningSourceGroundingRules.OperationKinds.Contains(assignment.Kind, StringComparer.Ordinal) ||
            !PlanningChoiceEvidence.Current(state, assignment.ClauseReference) || !PlanningChoiceEvidence.Current(state, assignment.ActionReference) ||
            PlanningChoiceEvidence.Parent(state, assignment.ActionReference).Id != assignment.ClauseReference || assignment.DecisionId != "operation_" + assignment.RuntimeEvidenceId)
            throw Failure(assignment.ClauseReference, "An operation requires exact current clause and action references.");
        var evidence = state.RuntimeEvidence.SingleOrDefault(e => e.Id == assignment.RuntimeEvidenceId)
            ?? throw Failure(assignment.ClauseReference, "Admission requires current runtime evidence, not an interpretation label.");
        ValidateRuntime(state, evidence);
        if (DeriveDeclarationExclusions(state).ContainsKey(evidence.Id))
            throw Failure(evidence.Id, "Canonical declaration evidence cannot authorize a standalone local occurrence.");
        if (evidence.ActionReference != assignment.ActionReference || evidence.ClauseReference != assignment.ClauseReference ||
            evidence.Kind != assignment.Kind || evidence.Necessity != assignment.Necessity || evidence.BaselineReference != assignment.BaselineReference ||
            (assignment.Disposition == "distinct" ? assignment.TargetId is not null || assignment.Effect?.Contribution != "realizes" :
                assignment.Disposition == "same_as" ? assignment.TargetId is null || assignment.Effect?.Contribution != "realizes" :
                assignment.Disposition == "attach" ? assignment.TargetId is null || assignment.Effect?.Contribution is not ("governs" or "shared_rule") : true) ||
            assignment.ResolutionOrigin is not ("deterministic" or "model"))
            throw Failure(assignment.ClauseReference, "The assignment changed its proven execution boundary or occurrence.");
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
