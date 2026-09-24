using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
namespace GnOuGo.Agent.Server.Planning;
internal static class PlanningPersistenceSmoke
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var database = GnOuGoWorkspace.ResolveDatabasePath(Path.Combine(directory, "planning-smoke.db"), directory, ".GnOuGo/data/planning-smoke.db");
        var vault = GnOuGoWorkspace.ResolveDatabasePath(Path.Combine(directory, "planning-smoke-vault.db"), directory, ".GnOuGo/data/planning-smoke-vault.db");
        var factory = new PooledDbContextFactory<PlanningDbContext>(new DbContextOptionsBuilder<PlanningDbContext>()
            .UseSqlite("Data Source=" + database + ";Pooling=False").UseModel(CompiledModels.PlanningDbContextModel.Instance).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory);
        var store = new EfPlanningSessionStore(factory, records);
        var state = new PlanningSession { Request = new() { TenantId = "smoke", SessionId = Guid.NewGuid().ToString("N"), Prompt = "Private published smoke content" } };
        state.PendingDecision = new() { Id = "decision", Question = "Private decision question", Context = "Private decision context", Phase = PlanningPhase.Binding, Scope = "/actions/value",
            Options = [new("preferred", "Preferred", "Private decision reason", true), new("alternative", "Alternative", "Another valid option", false)], AllowCustomAnswer = true };
        state.DecisionContinuation = new() { Operation = "binding", InputHash = "fingerprint", Candidates = new() { ["preferred"] = new System.Text.Json.Nodes.JsonObject { ["private"] = "Private decision payload" } } };
        if (!await store.TrySaveAsync(state, null, CancellationToken.None)) throw new InvalidOperationException("Insert failed.");
        state.Revision = 1; state.Status = PlanningStatus.Stopped; state.ModelCalls = 2; state.ReplanAttempts = 1;
        state.GroundedPlan = new() { Summary = "Private grounded plan" };
        state.PendingRepair = new() { InputHash = "accepted", CandidateHash = "proposed", ActionIds = ["consumer"], Candidate = new() { Summary = "Private repair proposal" } };
        state.Graph = new() { Workflows = [new() { Key = "main" }] };
        state.Diagnostics = [new("GROUNDED_CONTRACT_INVALID", "/scopes/main/operations/consumer", "Private computation finding")
        {
            Prerequisite = new("blocked_dependency", "Private prerequisite", RootActionId: "producer"),
            Computation = new("alias.name", "inference_unsupported", new() { ["x-gnougo-opaque"] = true },
                new() { ["text"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } }, "JSON.parse(text)", "/scopes/main/operations/parse")
        }];
        if (!await store.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("Update failed.");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        var restored = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (restored?.SchemaVersion != 8 || restored.Revision != 1 || restored.ModelCalls != 2 || restored.ReplanAttempts != 1 || restored.GroundedPlan?.Summary != "Private grounded plan" || restored.Graph is null || restored.Diagnostics.Count != 1 ||
            restored.Diagnostics[0].Computation?.OriginExpression != "JSON.parse(text)" || restored.Diagnostics[0].Computation?.ProducerLocation != "/scopes/main/operations/parse" ||
            restored.Diagnostics[0].Prerequisite?.RootActionId != "producer" || restored.PendingRepair?.Candidate.Summary != "Private repair proposal" ||
            await reopened.LoadAsync("another-tenant", state.Request.SessionId, CancellationToken.None) is not null || (await reopened.ListAsync("smoke", CancellationToken.None)).Count == 0)
            throw new InvalidOperationException("Published persistence or tenant isolation failed.");
        state.Revision = 2;
        if (await reopened.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("A stale update was accepted.");
        var legacy = new PlanningSession { Request = new() { TenantId = "smoke", SessionId = Guid.NewGuid().ToString("N"), Name = "Legacy smoke session" },
            ModelCalls = 1, PendingCall = new() { Id = "uncertain" }, GroundedPlan = new() { Operations = [new CalculateGroundedOperation { Id = "value" }] } };
        if (!await store.TrySaveAsync(legacy, null, CancellationToken.None)) throw new InvalidOperationException("Legacy fixture insert failed.");
        string legacyKey;
        await using (var db = factory.CreateDbContext()) legacyKey = (await db.Sessions.SingleAsync(s => s.TenantId == "smoke" && s.SessionId == legacy.Request.SessionId)).PayloadKey;
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(legacy, PlanningJsonContext.Default.PlanningSession)!;
        payload["groundedPlan"]!["operations"]![0]!["resultType"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" };
        var before = await records.UpsertAsync(EfPlanningSessionStore.Collection, "smoke", legacyKey, payload.ToJsonString(), "smoke");
        var history = await EfPlanningSessionStore.InspectAllAsync(factory, records, "smoke", CancellationToken.None);
        if (history.Single(s => s.Entry.SessionId == legacy.Request.SessionId) is not { Entry.Available: false, Session: null } ||
            (await reopened.ListAsync("smoke", CancellationToken.None)).Any(s => s.Request.SessionId == legacy.Request.SessionId) ||
            before != await records.GetAsync(EfPlanningSessionStore.Collection, "smoke", legacyKey, "smoke"))
            throw new InvalidOperationException("Incompatible history isolation failed.");
        try { await reopened.LoadAsync("smoke", legacy.Request.SessionId, CancellationToken.None); throw new InvalidOperationException("Incompatible history was admitted."); }
        catch (PlanningConflictException) { }
        foreach (var file in Directory.EnumerateFiles(directory, "*.db"))
            if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)) is { } bytes &&
                (bytes.Contains("Private published smoke content", StringComparison.Ordinal) || bytes.Contains("JSON.parse(text)", StringComparison.Ordinal)))
                throw new InvalidOperationException("Sensitive session content was persisted unencrypted.");
        Console.WriteLine("Schema-8 planning persistence smoke passed.");
    }
}
