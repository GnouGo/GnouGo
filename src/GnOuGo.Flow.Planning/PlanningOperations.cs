using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Canonical action authority, committed once after bounded complete-clause adjudication.</summary>
internal static partial class PlanningOperations
{
    internal static string EvidenceFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint("operation-evidence-v4:" +
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
        foreach (var scope in scopes.Where(s => s.Evidence!.EvidenceRole == "action"))
        {
            ct.ThrowIfCancellationRequested();
            var targets = EligibleTargets(state, scope.Evidence!, staged).ToArray();
            var exact = targets.SingleOrDefault(o => ExactIdentity(state, scope.Evidence!, o));
            if (exact is not null)
                Replace(staged, Extend(state, exact, Assignment(scope, exact.Id, "same_as", "deterministic")));
            else if (targets.Length == 0)
            {
                var root = Create(state, Assignment(scope, null, "distinct", "deterministic"));
                if (staged.Any(o => o.Id == root.Id)) throw Failure(scope.Clause.Id, "The same action anchor has contradictory execution facts.");
                staged.Add(root);
            }
            else
            {
                var decision = Decision(state, scope, staged);
                var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", [decision], ct);
                Apply(state, scope, decision, response[decision.Id]!.AsObject(), staged);
            }
        }
        // Governing evidence cannot influence root identity or create an occurrence.
        var roots = staged.ToArray();
        foreach (var scope in scopes.Where(s => s.Evidence!.EvidenceRole == "governing"))
        {
            ct.ThrowIfCancellationRequested();
            var targets = EligibleTargets(state, scope.Evidence!, roots).ToArray();
            if (targets.Length == 0) throw Failure(scope.Clause.Id, "Governing runtime evidence has no established compatible occurrence.");
            if (targets.Length == 1)
            {
                var operation = staged.Single(o => o.Id == targets[0].Id);
                Replace(staged, Extend(state, operation, Assignment(scope, operation.Id, "attach", "deterministic")));
            }
            else
            {
                var decision = Decision(state, scope, roots);
                var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", [decision], ct);
                Apply(state, scope, decision, response[decision.Id]!.AsObject(), staged);
            }
        }
        staged = await PlanningSourceDecisions.ApplyRevisionAsync(state, runtime, staged, ct);
        Commit(state, staged);
        await runtime.CheckpointAsync(state, ct);
    }

    internal static void Apply(PlanningSnapshot state, Scope scope, PlanningDecisionPages.Decision decision, JsonObject response, List<PlanningObligation> staged)
    {
        if (PlanningContractValidation.ValidateInstance(response, decision.Schema).Count != 0)
            throw Failure(scope.Clause.Id, "Operation assignments contain unknown or out-of-scope fields.");
        var status = response["status"]!.ToString();
        if (status == "unresolved") throw Failure(scope.Clause.Id, "Complete action identity, kind or coverage remains unresolved.");
        if (status == "not_an_operation") return;
        if (status == "distinct")
        {
            var operation = Create(state, Assignment(scope, null, status, "model"));
            if (staged.Any(o => o.Id == operation.Id)) throw Failure(scope.Clause.Id, "Distinct action identity is already occupied.");
            staged.Add(operation); return;
        }
        var targets = status == "same_as" ? new[] { response["target"]!.ToString() }
            : response["targets"]!.AsArray().Select(v => v!.ToString()).ToArray();
        if (targets.Length == 0 || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw Failure(scope.Clause.Id, "Governing targets must be a nonempty unique canonical set.");
        foreach (var target in targets.Order(StringComparer.Ordinal))
        {
            var established = EligibleTargets(state, scope.Evidence!, staged).SingleOrDefault(o => o.Id == target)
                ?? throw Failure(scope.Clause.Id, "The target is not an eligible established action.");
            Replace(staged, Extend(state, established, Assignment(scope, target, status, "model")));
        }
    }

    private static void Replace(List<PlanningObligation> staged, PlanningObligation operation)
    { staged.RemoveAll(o => o.Id == operation.Id); staged.Add(operation); }

    private static PlanningOperationAssignment Assignment(Scope scope, string? target, string disposition, string origin) => new(DecisionId(scope), scope.Clause.Id,
        scope.Evidence!.ActionReference!, scope.Evidence.Kind!, scope.Evidence.Required, target, scope.Evidence.BaselineReference)
        { RuntimeEvidenceId = scope.Evidence.Id, Disposition = disposition, ResolutionOrigin = origin };

    internal static PlanningObligation Create(PlanningSnapshot state, PlanningOperationAssignment assignment)
    {
        ValidateAssignment(state, assignment);
        if (assignment.TargetId is not null) throw Failure(assignment.ClauseReference, "A new operation cannot borrow another action identity.");
        var anchor = state.References.Single(r => r.Id == assignment.ActionReference);
        var id = CanonicalId(state, anchor, assignment.BaselineReference);
        var operation = new PlanningObligation(id, [anchor.Id], assignment.Kind == "local_processing" ? "workflow" : "capability_contract", assignment.Kind, assignment.Required);
        operation = operation with { Grounding = PlanningSourceGroundingRules.Create(state, operation, assignment.BaselineReference), Disposition = "admitted" };
        return Prove(state, operation, new(4, id, anchor.Id, assignment.BaselineReference, [assignment], EvidenceFingerprint(state), ""));
    }

    private static PlanningObligation Extend(PlanningSnapshot state, PlanningObligation operation, PlanningOperationAssignment assignment)
    {
        Validate(state, operation); ValidateAssignment(state, assignment);
        if (!Compatible(state, state.RuntimeEvidence.Single(e => e.Id == assignment.RuntimeEvidenceId), operation) ||
            assignment.TargetId != operation.Id || assignment.Kind != operation.Kind || assignment.Required != operation.Required ||
            assignment.BaselineReference is { } baseline && baseline != operation.OperationAdmission!.BaselineReference)
            throw Failure(assignment.ClauseReference, "The reuse evidence contradicts the established kind, presence or baseline ownership.");
        if (operation.OperationAdmission!.Assignments.Any(a => a.RuntimeEvidenceId == assignment.RuntimeEvidenceId))
            throw Failure(assignment.ClauseReference, "Repeated evidence cannot authorize another attachment.");
        return Prove(state, operation, operation.OperationAdmission! with { Assignments = [.. operation.OperationAdmission!.Assignments, assignment] });
    }

    internal static string CanonicalId(PlanningSnapshot state, PlanningReference anchor, string? baseline)
    {
        JsonArray identity;
        if (baseline is not null)
        {
            if (!PlanningSourceGroundingRules.BaselineNodes(state).TryGetValue(baseline, out var node)) throw Failure(anchor.Id, "The baseline node is stale or foreign.");
            identity = new("operation-v1", "existing", node.Workflow, node.Node.Key);
        }
        else identity = new("operation-v1", "requested", anchor.SourceId, anchor.Start, anchor.Length);
        return "operation_" + PlanningGraphCompiler.Fingerprint(identity.ToJsonString())[..24];
    }

    internal static string Text(PlanningSnapshot state, PlanningObligation operation) => string.Join(" ",
        operation.OperationAdmission!.Assignments.Select(a => a.ClauseReference).Distinct(StringComparer.Ordinal).Select(id => PlanningChoiceEvidence.Text(state, id)));

    internal static PlanningObligation Prove(PlanningSnapshot state, PlanningObligation operation, PlanningOperationAdmission admission)
        => operation with { OperationAdmission = admission with { ProofFingerprint = Proof(operation, admission) } };

    private static string Proof(PlanningObligation operation, PlanningOperationAdmission admission) => PlanningGraphCompiler.Fingerprint(
        new JsonArray(operation.Id, operation.Kind, operation.Required, operation.Grounding?.Fingerprint).ToJsonString() + ":" +
        JsonSerializer.Serialize(admission with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningOperationAdmission));

    internal static void Commit(PlanningSnapshot state, List<PlanningObligation> operations)
    {
        if (operations.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != operations.Count) throw Failure("$plan", "Canonical operations have duplicate identities.");
        foreach (var operation in operations) Validate(state, operation);
        var initial = state.Obligations.Where(o => o.OperationAdmission is null).ToList();
        if (initial.Any(o => operations.Any(a => a.Id == o.Id))) throw Failure("$plan", "An evidence identity cannot replace a canonical action.");
        state.Obligations = initial.Concat(operations.OrderBy(o => o.Id, StringComparer.Ordinal)).ToList();
        var fingerprint = Fingerprint(state);
        if (fingerprint != state.OperationAdmissionFingerprint)
        {
            var excluded = DeriveDeclarationExclusions(state);
            state.Events.Add(new("runtime_policy_engine_resolved", "intent_operations", DateTimeOffset.UtcNow,
                state.RuntimeEvidence.Count(e => e.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority)));
            state.Events.Add(new("operation_excluded_declaration_coverage", "intent_operations", DateTimeOffset.UtcNow, excluded.Count));
            foreach (var (evidence, declarations) in excluded)
                System.Diagnostics.Activity.Current?.AddEvent(new("planning.operation_declaration_covered", tags: new()
                    { ["runtime_evidence_id"] = evidence, ["declaration_ids"] = string.Join(",", declarations) }));
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
        if (state.OperationAdmissionFingerprint is null || state.OperationAdmissionFingerprint != Fingerprint(state))
            throw Failure("$plan", "Canonical operation admission requires explicit reassessment with current evidence.", "INTENT_OPERATION_PROOF_MISSING");
    }

    internal static void Validate(PlanningSnapshot state, PlanningObligation operation)
    {
        var proof = operation.OperationAdmission;
        if (proof is not { Version: 4 } || proof.CanonicalId != operation.Id || operation.Disposition != "admitted" ||
            operation.EvidenceReferences.Count != 1 || operation.EvidenceReferences[0] != proof.AnchorReference ||
            proof.EvidenceFingerprint != EvidenceFingerprint(state) || proof.Assignments.Count == 0 || proof.ProofFingerprint != Proof(operation, proof))
            throw Failure(operation.Id, "Current canonical admission proof is missing or stale.", "INTENT_OPERATION_PROOF_MISSING");
        if (operation.Grounding != PlanningSourceGroundingRules.Create(state, operation, proof.BaselineReference))
            throw Failure(operation.Id, "Canonical operation source authority changed.");
        var root = proof.Assignments[0];
        if (root.ActionReference != proof.AnchorReference || root.TargetId is not null || root.BaselineReference != proof.BaselineReference ||
            CanonicalId(state, state.References.Single(r => r.Id == proof.AnchorReference), proof.BaselineReference) != operation.Id)
            throw Failure(operation.Id, "Canonical operation identity is not established by its root evidence.");
        foreach (var assignment in proof.Assignments)
        {
            ValidateAssignment(state, assignment);
            if (!Compatible(state, state.RuntimeEvidence.Single(e => e.Id == assignment.RuntimeEvidenceId), operation) ||
                assignment.Kind != operation.Kind || assignment.Required != operation.Required ||
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
            evidence.Kind != assignment.Kind || evidence.Required != assignment.Required || evidence.BaselineReference != assignment.BaselineReference ||
            (assignment.Disposition == "distinct" ? assignment.TargetId is not null || evidence.EvidenceRole != "action" :
                assignment.Disposition == "same_as" ? assignment.TargetId is null || evidence.EvidenceRole != "action" :
                assignment.Disposition == "attach" ? assignment.TargetId is null || evidence.EvidenceRole != "governing" : true) ||
            assignment.ResolutionOrigin is not ("deterministic" or "model"))
            throw Failure(assignment.ClauseReference, "The assignment changed its proven execution boundary or occurrence.");
        if (assignment.ResolutionOrigin == "model" && !state.DecisionPages.Any(p => p.Phase == "intent_operations" &&
            p.Status == "completed" && p.Candidate?[assignment.DecisionId] is JsonObject selected &&
            selected["status"]?.ToString() == assignment.Disposition && (assignment.Disposition == "distinct" ||
                assignment.Disposition == "same_as" && selected["target"]?.ToString() == assignment.TargetId ||
                assignment.Disposition == "attach" && selected["targets"] is JsonArray targets &&
                targets.Any(t => t?.ToString() == assignment.TargetId))))
            throw Failure(assignment.ClauseReference, "A model-owned identity or attachment requires its completed decision page.");
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
