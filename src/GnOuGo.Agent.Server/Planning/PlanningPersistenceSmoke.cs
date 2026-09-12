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
        state.References = [new("reference", "smoke:" + state.Request.SessionId, 4, "request", "source-fingerprint", "user_request", 0, 7)];
        state.DecisionPages = [new() { Id = "page", Phase = "behavior", WorkflowKey = "$plan", EvidenceFingerprint = "source-fingerprint",
            Decisions = ["decision"], References = ["reference"], Status = "completed", EstimatedInputTokens = 700, InputTargetTokens = 9600,
            EstimatedAnswerTokens = 30, Candidate = new System.Text.Json.Nodes.JsonObject { ["decision"] = "Private decision" }, RequestId = "receipt" }];
        state.DecisionCorrections = [new("decision", "source-fingerprint", "$plan", PlanningGates.Behavior)];
        state.Outcome = new PlanningNeedUserClarification(new("decision", ["reference"],
            new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["maxLength"] = 64 }, ["obligation"]));
        state.Construction.Repair = new() { Ready = false, GraphFingerprint = "exact-revision",
            RequestContext = new System.Text.Json.Nodes.JsonObject { ["privateScope"] = "Encrypted scope" } };
        if (!await reopened.TrySaveAsync(state, 3, CancellationToken.None)) throw new InvalidOperationException("Decision preparation persistence failed.");
        var prepared = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (prepared?.PreparationCheckpoint?.Stage != "matching" || prepared.PreparationCheckpoint.RequestHashes.Count != 1 ||
            prepared.Preparation?.Decisions.Single().PermissionOperationIds.Single() != "confirm" || prepared.Validation.Inputs?["resource"]?.GetValue<string>() != "scenario-only" ||
            prepared.SchemaVersion != 5 || prepared.Construction.Candidates.Single().Payload["privateAssignment"]?.ToString() != "Encrypted staged content" ||
            prepared.Construction.Candidates.Single().Diagnostics.Single().Rule != "required-member" || prepared.RepairAllowances.Single().Attempts != 4 ||
            prepared.BehaviorRevision?.Fields.Single().CanonicalLocation != "/workflows/@main/purpose")
            throw new InvalidOperationException("Decision and scenario contracts did not survive persistence.");
        if (prepared.References.Single().SourceRevision != 4 || prepared.DecisionPages.Single().Candidate?["decision"]?.ToString() != "Private decision" ||
            prepared.DecisionCorrections.Single().DecisionId != "decision" || prepared.Outcome is not PlanningNeedUserClarification clarification ||
            clarification.Decision.EvidenceReferences.Single() != "reference" || prepared.Construction.Repair is not { Ready: false } pending ||
            pending.RequestContext["privateScope"]?.ToString() != "Encrypted scope")
            throw new InvalidOperationException("Schema-5 references, pages, outcomes or pending scopes did not survive persistence.");
        Console.WriteLine("Planning persistence smoke passed.");
    }
}
