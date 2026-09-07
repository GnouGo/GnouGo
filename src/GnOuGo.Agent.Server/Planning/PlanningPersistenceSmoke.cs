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
        state.Revision = 1; state.Status = PlanningStatus.Recovery; state.CurrentPhase = PlanningPhase.Intent;
        state.ClarificationForms = 1; state.ClarificationQuestions = 3;
        state.IntentHistory.Add(new(0, "Previous private request", [], [new("INTENT_EVIDENCE_INVALID", "/evidence", "Invalid evidence")]));
        if (!await store.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("Revision update failed.");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        var restored = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (restored?.Revision != 1 || restored.Status != PlanningStatus.Recovery || restored.CurrentPhase != PlanningPhase.Intent ||
            restored.ClarificationForms != 1 || restored.ClarificationQuestions != 3 || restored.IntentHistory.Count != 1 ||
            await reopened.LoadAsync("different", state.Request.SessionId, CancellationToken.None) is not null ||
            (await reopened.ListAsync("smoke", CancellationToken.None)).Count == 0)
            throw new InvalidOperationException("Persistence or tenant isolation failed.");
        state.Revision = 2; state.CurrentPhase = PlanningPhase.Behavior; state.BehaviorAssessmentCalls = 2;
        state.Graph = new() { Workflows = [new() { Key = "main", Outputs = [new() { Name = "pending", Schema = new() { CapabilityId = "missing", SchemaPointer = "/invalid" },
            Value = new() { Kind = "output", Source = "pending", ResultChannel = "structured", Path = ["value"] } }] }] };
        state.Diagnostics = [new("SCHEMA_REFERENCE_INVALID", "/workflows/0/outputs/0/schema", "Invalid retained candidate")];
        if (!await reopened.TrySaveAsync(state, 1, CancellationToken.None)) throw new InvalidOperationException("Behavior recovery update failed.");
        var behavior = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (behavior?.BehaviorAssessmentCalls != 2 || behavior.CurrentPhase != PlanningPhase.Behavior || behavior.Status != PlanningStatus.Recovery ||
            behavior.Graph?.Workflows[0].Outputs[0].Value.ResultChannel != "structured" || behavior.Outcome is not null)
            throw new InvalidOperationException("Behavior recovery did not survive persistence.");
        if (await reopened.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("A stale write was accepted.");
        state.Revision = 3; state.Status = PlanningStatus.BehaviorReview;
        state.BehaviorPlan = new() { Summary = "Review before constructing code", Workflows = [new() { Key = "main", Purpose = "Return a message", Steps = [new() { Key = "message", Purpose = "Return a message" }] }] };
        state.ApprovedBehaviorHash = GnOuGo.Flow.Planning.PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Attempts.Add(new(state.ApprovedBehaviorHash, PlanningPhase.Behavior, 1, true, []));
        state.Request.Generation.Reasoning = "low";
        state.GenerationHistory.Add(new(2, new() { Reasoning = "medium" }));
        state.ConstructionUnits.Add(new() { Key = "main:implementation:message", WorkflowKey = "main", NodeKeys = ["message"],
            Status = "validated", Calls = 2, RepairCalls = 1, CandidateHash = "receipt", RequestHashes = ["request"],
            Candidate = new System.Text.Json.Nodes.JsonObject { ["private"] = "Encrypted executable candidate" } });
        if (!await reopened.TrySaveAsync(state, 2, CancellationToken.None)) throw new InvalidOperationException("Early review persistence failed.");
        var early = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (early?.BehaviorPlan is null || early.ApprovedBehaviorHash != state.ApprovedBehaviorHash || early.Attempts.Count != 1 || early.ClarificationQuestions != 3 ||
            early.Request.Generation.Reasoning != "low" || early.GenerationHistory.Count != 1 || early.ConstructionUnits.Count != 1 ||
            early.ConstructionUnits[0].RepairCalls != 1 || early.ConstructionUnits[0].Candidate?["private"]?.GetValue<string>() != "Encrypted executable candidate")
            throw new InvalidOperationException("Early review fields did not survive persistence.");
        state.Revision = 4;
        state.PreparationCheckpoint = new() { Version = PlanningPreparationCheckpoint.CurrentVersion, Stage = "matching", Fingerprint = "locked-contract", ValidatedResults = new System.Text.Json.Nodes.JsonObject { ["inventory"] = new System.Text.Json.Nodes.JsonArray() }, RequestHashes = ["preparation-receipt"] };
        state.ScenarioInputs = new System.Text.Json.Nodes.JsonObject { ["resource"] = "scenario-only" };
        state.ScenarioInputsFingerprint = "fixture-contract";
        state.Preparation = new() { Decisions = [new() { Group = "permission", SourceOperationId = "confirm", SourceCapabilityId = "native", SourcePointer = "/response", PermissionOperationIds = ["confirm"] }] };
        if (!await reopened.TrySaveAsync(state, 3, CancellationToken.None)) throw new InvalidOperationException("Decision preparation persistence failed.");
        var prepared = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (prepared?.PreparationCheckpoint?.Stage != "matching" || prepared.PreparationCheckpoint.RequestHashes.Count != 1 ||
            prepared.Preparation?.Decisions.Single().PermissionOperationIds.Single() != "confirm" || prepared.ScenarioInputs?["resource"]?.GetValue<string>() != "scenario-only")
            throw new InvalidOperationException("Decision and scenario contracts did not survive persistence.");
        Console.WriteLine("Planning persistence smoke passed.");
    }
}
