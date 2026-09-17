using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GnOuGo.Agent.Server.Planning;

internal static class PlanningPersistenceSmoke
{
    // Exercises the actual published EF model and encrypted KeyVault boundary without starting services.
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var database = GnOuGoWorkspace.ResolveDatabasePath(Path.Combine(directory, ".GnOuGo/data/planning-smoke.db"), directory, ".GnOuGo/data/planning-smoke.db");
        var vault = GnOuGoWorkspace.ResolveDatabasePath(Path.Combine(directory, ".GnOuGo/data/planning-smoke-vault.db"), directory, ".GnOuGo/data/planning-smoke-vault.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var factory = new PooledDbContextFactory<PlanningDbContext>(new DbContextOptionsBuilder<PlanningDbContext>()
            .UseSqlite("Data Source=" + database + ";Pooling=False").UseModel(CompiledModels.PlanningDbContextModel.Instance).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory);
        var store = new EfPlanningSessionStore(factory, records);
        var state = new PlanningSnapshot { Request = new() { TenantId = "smoke", SessionId = Guid.NewGuid().ToString("N"), Prompt = "Private published smoke content" } };
        if (!await store.TrySaveAsync(state, null, CancellationToken.None)) throw new InvalidOperationException("Insert failed.");
        state.Revision = 1; state.Status = PlanningStatus.Stopped; state.CurrentPhase = PlanningPhase.Intent;
        state.Intent.Forms = 1; state.Intent.Questions = 3;
        state.Intent.History.Add(new(0, "Previous private request", [], [new("INTENT_EVIDENCE_INVALID", "/evidence", "Invalid evidence")]));
        if (!await store.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("Revision update failed.");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        var restored = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (restored?.Revision != 1 || restored.Status != PlanningStatus.Stopped || restored.CurrentPhase != PlanningPhase.Intent ||
            restored.Intent.Forms != 1 || restored.Intent.Questions != 3 || restored.Intent.History.Count != 1 ||
            await reopened.LoadAsync("different", state.Request.SessionId, CancellationToken.None) is not null ||
            (await reopened.ListAsync("smoke", CancellationToken.None)).Count == 0)
            throw new InvalidOperationException("Persistence or tenant isolation failed.");
        state.Revision = 2; state.CurrentPhase = PlanningPhase.Behavior; state.BehaviorAssessmentCalls = 2;
        state.Graph = new()
        {
            Workflows = [new() { Key = "main", Outputs = [new() { Name = "pending", Schema = new() { CapabilityId = "missing", SchemaPointer = "/invalid" },
            Value = new() { Kind = "output", Source = "pending", ResultChannel = "structured", Path = ["value"] } }] }]
        };
        state.Diagnostics = [new("SCHEMA_REFERENCE_INVALID", "/workflows/0/outputs/0/schema", "Invalid retained candidate")];
        if (!await reopened.TrySaveAsync(state, 1, CancellationToken.None)) throw new InvalidOperationException("Behavior recovery update failed.");
        var behavior = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (behavior?.BehaviorAssessmentCalls != 2 || behavior.CurrentPhase != PlanningPhase.Behavior || behavior.Status != PlanningStatus.Stopped ||
            behavior.Graph?.Workflows[0].Outputs[0].Value.ResultChannel != "structured" || behavior.Outcome is not null)
            throw new InvalidOperationException("Behavior recovery did not survive persistence.");
        if (await reopened.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("A stale write was accepted.");
        state.Revision = 3; state.Status = PlanningStatus.BehaviorReview;
        state.BehaviorPlan = new() { Summary = "Review before constructing code", Workflows = [new() { Key = "main", Purpose = "Return a message", Steps = [new() { Key = "message", Purpose = "Return a message" }] }] };
        state.ApprovedBehaviorHash = GnOuGo.Flow.Planning.PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Attempts.Add(new(state.ApprovedBehaviorHash, PlanningPhase.Behavior, 1, true, []));
        state.Request.Generation.ReasoningProfile.Routine = "low";
        state.GenerationHistory.Add(new(2, new() { ReasoningProfile = new() { Routine = "medium" } }));
        state.Construction.Workflows.Add(new() { WorkflowKey = "main", Status = "validated", Calls = 2, RepairCalls = 1, GraphFingerprint = "receipt" });
        if (!await reopened.TrySaveAsync(state, 2, CancellationToken.None)) throw new InvalidOperationException("Early review persistence failed.");
        var early = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (early?.BehaviorPlan is null || early.ApprovedBehaviorHash != state.ApprovedBehaviorHash || early.Attempts.Count != 1 || early.Intent.Questions != 3 ||
            early.Request.Generation.ReasoningProfile.Routine != "low" || early.GenerationHistory.Count != 1 || early.Construction.Workflows.Count != 1 ||
            early.Construction.Workflows[0].RepairCalls != 1 || early.Construction.Workflows[0].GraphFingerprint != "receipt")
            throw new InvalidOperationException("Early review fields did not survive persistence.");
        state.Revision = 4;
        state.PreparationCheckpoint = new() { Version = PlanningPreparationCheckpoint.CurrentVersion, Stage = "matching", Fingerprint = "locked-contract", ValidatedResults = new System.Text.Json.Nodes.JsonObject { ["inventory"] = new System.Text.Json.Nodes.JsonArray() }, RequestHashes = ["preparation-receipt"] };
        state.Validation.Inputs = new System.Text.Json.Nodes.JsonObject { ["resource"] = "scenario-only" };
        state.Validation.InputsFingerprint = "fixture-contract";
        state.Preparation = new() { Decisions = [new() { Group = "permission", SourceOperationId = "confirm", SourceCapabilityId = "native", SourcePointer = "/response", PermissionOperationIds = ["confirm"] }] };
        state.Construction.Holes = [new() { Id = "h_private", WorkflowKey = "main", Path = "/workflows/0/outputs/0/value", CanonicalLocation = "/workflows/@main/outputs/@pending/value" }];
        state.Construction.Candidates = [new() { WorkflowKey = "main", GraphFingerprint = "exact-revision", ScopeFingerprint = "exact-scope",
            Targets = state.Construction.Holes, Payload = new System.Text.Json.Nodes.JsonObject { ["privateAssignment"] = "Encrypted staged content" },
            Diagnostics = [new("VALUE_CONTRACT", "/assignments/h_private", "Invalid candidate", Rule: "required-member")] }];
        state.RepairAllowances = [new() { WorkflowKey = "main", Gate = PlanningGates.Typed, Attempts = 4 }];
        state.BehaviorRevision = new() { Text = "Private human revision", Located = true,
            Fields = [new("/workflows/0/purpose", "/workflows/@main/purpose", "replace", "previous-field", "human revision")] };
        state.OperationAdmissionFingerprint = "operation-set-proof";
        state.RuntimeEvidenceFingerprint = "runtime-proof";
        state.RuntimeEvidence = [new("runtime", "source", "clause", "local_behavior", "anchor", null, "anchor", "local_processing", "action", null, null, PlanningOperationNecessity.Required, "proof")
            { ExecutionScope = PlanningRuntimeExecutionScope.GeneratedWorkflow, Origin = PlanningRuntimeEvidenceOrigin.SourceInterpretation, NecessityReference = "private_necessity_reference", OccurrenceBoundary = new("invocation", "private_owner", "private_boundary") },
            new("policy_runtime", "policy_source", "policy_clause", "policy", null, null, null, null, null, null, null, PlanningOperationNecessity.Unspecified, "engine_policy_proof")
            { ExecutionScope = PlanningRuntimeExecutionScope.Policy, Origin = PlanningRuntimeEvidenceOrigin.EngineSourceAuthority },
            new("baseline_runtime", "baseline_source", "baseline_clause", "contract", null, null, null, null, null, null, null, PlanningOperationNecessity.Unspecified, "baseline-proof")
            { ExecutionScope = PlanningRuntimeExecutionScope.PublicContract, Origin = PlanningRuntimeEvidenceOrigin.EngineBaseline }];
        state.Obligations = [new("action", ["anchor"], "workflow", "local_processing", true)
        { Disposition = "admitted", OperationAdmission = new(17, "action", "anchor", null,
            [new("decision", "clause", "anchor", "local_processing", PlanningOperationNecessity.Required, null, null) { RuntimeEvidenceId = "runtime", Disposition = "distinct", ResolutionOrigin = "deterministic", EffectId = "action",
                Effect = new(7, "effect-decision", "realizes", [new("main", "private_result", "result_realization", "private_result") { OccurrenceProof = new(1, "runtime", "main", null, new("invocation", "private_owner", "private_boundary"), null, "boundary-proof") }],
                    ["private_input"], ["private_result"], [], ["private_boundary_evidence"], "model", "effect-evidence-proof") },
             new("reuse", "rules", "rule_anchor", "local_processing", PlanningOperationNecessity.Unspecified, "action", null)], "evidence-proof", "operation-proof")
            { ExecutionRequests = [new(1, "request-authority", "request-decision", "request-domain",
                [new("request-unit", "requested_execution", "clause", "private_predicate", ["private_execution_evidence"], ["runtime", "runtime-details"], "action", "requested_result_production", "private_result", "private_result") { ExecutionRequestId = "request-authority" }], "model", "request-proof")],
              ExecutionContributions = [new(7, "runtime", "qualification", "qualified-domain",
                [new("qualified", "private_execution_evidence", "supports", "action", "requested_result_production", "private_result", "private_result", PlanningContributionOrigin.ModelQualification) { ExecutionRequestId = "request-authority", UnitId = "request-unit", RuntimeEvidenceIds = ["runtime", "runtime-details"] },
                 new("property", "property-evidence", "governing_property", null, "governing_property", null, null, PlanningContributionOrigin.ModelQualification) { UnitId = "qualifier-unit", RuntimeEvidenceIds = ["runtime"] },
                 new("fallback", "fallback-evidence", "governing_property", null, "governing_property", null, null, PlanningContributionOrigin.ModelQualification)
                 { UnitId = "fallback-unit", GoverningKind = "runtime_fallback", SourceBindings = [new("fallback-evidence", "clause", "fallback-obligation", "source-proof")] }], "qualified-proof")
                 { ClauseReference = "clause", RuntimeEvidenceIds = ["runtime", "runtime-details"], Units = [new("request-unit", "requested_execution", "clause", "private_predicate", ["private_execution_evidence"], ["runtime", "runtime-details"], "action", "requested_result_production", "private_result", "private_result") { ExecutionRequestId = "request-authority" },
                 new("qualifier-unit", "governing_property", "clause", null, ["property-evidence"], ["runtime"], null, "governing_property", null, null) { ParentRequestUnitId = "request-unit" },
                 new("fallback-unit", "governing_property", "clause", null, ["fallback-evidence"], [], null, "governing_property", null, null)
                 { GoverningKind = "runtime_fallback", SourceBindings = [new("fallback-evidence", "clause", "fallback-obligation", "source-proof")] }] }],
                RealizationCoverage = new(3, "coverage-decision", "coverage-domain", ["action"],
                [new("runtime", "supports", ["action"], ["private_execution_evidence"]), new("optional-runtime", "omitted", [], ["private_optional_evidence"])],
                [new("action", new("main", "private_result", "result_realization", "private_result"), ["runtime"], ["private_input"], ["private_result"])], "coverage-proof"),
                GoverningApplicability = [new(2, "property", "runtime", "property-evidence", "active", ["action"], null,
                    [new("action", ["property-evidence"])], [], null, PlanningApplicabilityOrigin.ModelApplicability,
                    "applicability-decision", "realized-set", "applicability-domain", "applicability-proof"),
                    new(2, "fallback", null, "fallback-evidence", "active", ["action"], null, [new("action", ["fallback-evidence"])], [], null,
                        PlanningApplicabilityOrigin.ModelApplicability, "fallback-applicability", "realized-set", "fallback-domain", "fallback-proof")
                        { SourceBindings = [new("fallback-evidence", "clause", "fallback-obligation", "source-proof")] }],
                Dependencies = new(1, "dependency-domain", [new("upstream", "action", "data", PlanningDependencyOrigin.ModelSemanticSelection,
                ["private_data_evidence"], "data-decision")], "dependency-proof") } }];
        state.References = [new("reference", "smoke:" + state.Request.SessionId, 4, "request", "source-fingerprint", "user_request", 0, 7),
            new("baseline_source", "smoke:" + state.Request.SessionId, 4, "baseline", "baseline-content", "existing_workflow", 0, 7)
            { Baseline = new(1, "baseline-fingerprint", "port", "main", null, "input", "private_port", "schema/description") }];
        state.DecisionPages = [new() { Id = "page", Phase = "behavior", WorkflowKey = "$plan", EvidenceFingerprint = "source-fingerprint",
            Decisions = ["decision"], References = ["reference"], Status = "completed", EstimatedInputTokens = 700, InputTargetTokens = 9600,
            EstimatedAnswerTokens = 30, Candidate = new System.Text.Json.Nodes.JsonObject { ["decision"] = "Private decision" }, RequestId = "receipt" }];
        state.DecisionCorrections = [new("decision", "source-fingerprint", "$plan", PlanningGates.Behavior)];
        state.Outcome = new PlanningNeedUserClarification(new("decision", ["reference"],
            new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["maxLength"] = 64 }, ["obligation"])
        { Question = "Private business question", DependencyFingerprint = "governing-proof",
            Choices = [new("choice", "Private business choice", true, "Private declared preference", ["reference"])] });
        state.BusinessDecisions = [new() { Id = "decision", SubjectReference = "reference", Status = "eligible", DependencyFingerprint = "governing-proof",
            ValuePresence = "omitted", Constraints = [new("prefer", "choice", "reference", "intent", "omitted")],
            Alternatives = [new() { Id = "choice", EvidenceReference = "reference", Label = "Private business choice" }], ReportedEvents = ["recorded"] }];
        state.Construction.Repair = new() { Ready = false, GraphFingerprint = "exact-revision",
            RequestContext = new System.Text.Json.Nodes.JsonObject { ["privateScope"] = "Encrypted scope" } };
        if (!await reopened.TrySaveAsync(state, 3, CancellationToken.None)) throw new InvalidOperationException("Decision preparation persistence failed.");
        var prepared = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (prepared?.RuntimeEvidenceFingerprint != "runtime-proof" || prepared.RuntimeEvidence[0].ProofFingerprint != "proof" || prepared.RuntimeEvidence[0].Necessity != PlanningOperationNecessity.Required || prepared.RuntimeEvidence[0].NecessityReference != "private_necessity_reference" || prepared.RuntimeEvidence[0].OccurrenceBoundary is not { Kind: "invocation", OwnerReference: "private_owner", BoundaryReference: "private_boundary" } ||
            prepared.RuntimeEvidence[0].ExecutionScope != PlanningRuntimeExecutionScope.GeneratedWorkflow || prepared.RuntimeEvidence[0].Origin != PlanningRuntimeEvidenceOrigin.SourceInterpretation || prepared.RuntimeEvidence[1].Origin != PlanningRuntimeEvidenceOrigin.EngineSourceAuthority || prepared.RuntimeEvidence[1].Role != "policy")
            throw new InvalidOperationException("Runtime execution evidence did not survive encrypted persistence.");
        if (prepared.RuntimeEvidence[2].Origin != PlanningRuntimeEvidenceOrigin.EngineBaseline ||
            prepared.References[1].Baseline is not { Version: 1, Fingerprint: "baseline-fingerprint", Port: "private_port", Field: "schema/description" })
            throw new InvalidOperationException("Structural baseline evidence did not survive encrypted persistence.");
        if (prepared?.OperationAdmissionFingerprint != "operation-set-proof" || prepared.Obligations.Single().OperationAdmission is not { Version: 17 } admission ||
            admission.ExecutionRequests.SingleOrDefault() is not { Version: 1, ProofFingerprint: "request-proof" } requestProof ||
            requestProof.Units.Single().ExecutionRequestId != "request-authority" ||
            admission.ExecutionContributions.SingleOrDefault() is not { Version: 7, ProofFingerprint: "qualified-proof" } contribution ||
            contribution.Contributions.Single(c => c.Role == "supports").Basis != "requested_result_production" ||
            contribution.Units.Single(u => u.Role == "requested_execution").PredicateReference != "private_predicate" || contribution.Units.Single(u => u.Role == "requested_execution").Role != "requested_execution" ||
            contribution.Contributions.Single(c => c.Role == "supports").UnitId != contribution.Units.Single(u => u.Role == "requested_execution").Id || !contribution.RuntimeEvidenceIds.SequenceEqual(new[] { "runtime", "runtime-details" }) ||
            contribution.Units.Single(u => u.Id == "qualifier-unit").ParentRequestUnitId != "request-unit" ||
            contribution.Contributions.Single(c => c.Id == "property").UnitId != "qualifier-unit" ||
            contribution.Contributions.Single(c => c.Id == "fallback") is not { GoverningKind: "runtime_fallback", RuntimeEvidenceIds.Count: 0, SourceBindings.Count: 1 } ||
            contribution.Units.Single(u => u.Id == "fallback-unit").SourceBindings.Single().SemanticObligationId != "fallback-obligation" ||
            admission.GoverningApplicability.Single(p => p.ContributionId == "fallback") is not { Version: 2, RuntimeEvidenceId: null, SourceBindings.Count: 1 } ||
            admission.GoverningApplicability.Single(p => p.ContributionId == "property") is not { Version: 2, ProofFingerprint: "applicability-proof", Outcome: "active" } ||
            admission.RealizationCoverage is not { Version: 3, DomainFingerprint: "coverage-domain", ProofFingerprint: "coverage-proof" } coverage ||
            coverage.Contributions[1].Disposition != "omitted" || coverage.Effects[0].SupportingEvidence.Single() != "runtime" ||
            admission.Dependencies is not { Version: 1, DomainFingerprint: "dependency-domain", ProofFingerprint: "dependency-proof" } dependency ||
            dependency.Assignments.Single().Origin != PlanningDependencyOrigin.ModelSemanticSelection || dependency.Assignments[0].EvidenceReferences.Single() != "private_data_evidence" ||
            admission.Assignments[0].RuntimeEvidenceId != "runtime" || admission.Assignments[0].Effect is not { Version: 7 } effect ||
            effect.Candidates.Single().OccurrenceProof is not { Version: 1, Fingerprint: "boundary-proof" } || effect.Candidates.Single().OwnerReference != "private_result" || effect.Inputs.Single() != "private_input" || admission.Assignments[0].EffectId != "action" || admission.Assignments[1].TargetId != "action" || admission.Assignments[1].ClauseReference != "rules" || admission.ProofFingerprint != "operation-proof")
            throw new InvalidOperationException("Canonical operation evidence did not survive encrypted persistence.");
        if (prepared?.BusinessDecisions.Single().Constraints.Single().Applicability != "omitted" ||
            prepared.BusinessDecisions.Single().ReportedEvents.Single() != "recorded" ||
            prepared.Outcome is not PlanningNeedUserClarification business || business.Decision.Choices.Single().PreferredReason != "Private declared preference")
            throw new InvalidOperationException("Business decision proof did not survive encrypted persistence.");
        if (prepared?.PreparationCheckpoint?.Stage != "matching" || prepared.PreparationCheckpoint.RequestHashes.Count != 1 ||
            prepared.Preparation?.Decisions.Single().PermissionOperationIds.Single() != "confirm" || prepared.Validation.Inputs?["resource"]?.GetValue<string>() != "scenario-only" ||
            prepared.SchemaVersion != 5 || prepared.Construction.Candidates.Single().Payload["privateAssignment"]?.ToString() != "Encrypted staged content" ||
            prepared.Construction.Candidates.Single().Diagnostics.Single().Rule != "required-member" || prepared.RepairAllowances.Single().Attempts != 4 ||
            prepared.BehaviorRevision?.Fields.Single().CanonicalLocation != "/workflows/@main/purpose")
            throw new InvalidOperationException("Decision and scenario contracts did not survive persistence.");
        if (prepared.References.Single(r => r.Id == "reference").SourceRevision != 4 || prepared.DecisionPages.Single().Candidate?["decision"]?.ToString() != "Private decision" ||
            prepared.DecisionCorrections.Single().DecisionId != "decision" || prepared.Outcome is not PlanningNeedUserClarification clarification ||
            clarification.Decision.EvidenceReferences.Single() != "reference" || prepared.Construction.Repair is not { Ready: false } pending ||
            pending.RequestContext["privateScope"]?.ToString() != "Encrypted scope")
            throw new InvalidOperationException("Schema-5 references, pages, outcomes or pending scopes did not survive persistence.");
        Console.WriteLine("Planning persistence smoke passed.");
    }
}
