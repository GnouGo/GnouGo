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
        if (!await store.TrySaveAsync(state, null, CancellationToken.None)) throw new InvalidOperationException("Insert failed.");
        state.Revision = 1; state.Status = PlanningStatus.Stopped; state.ModelCalls = 2; state.RepairAttempts = 1;
        state.IntentPlan = new() { Summary = "Private intent" };
        state.Graph = new() { Workflows = [new() { Key = "main" }] };
        state.Diagnostics = [new("HOLE_UNRESOLVED", "/workflows/0", "An input is missing.")];
        if (!await store.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("Update failed.");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        var restored = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (restored?.SchemaVersion != 7 || restored.Revision != 1 || restored.ModelCalls != 2 || restored.RepairAttempts != 1 || restored.IntentPlan?.Summary != "Private intent" || restored.Graph is null || restored.Diagnostics.Count != 1 ||
            await reopened.LoadAsync("another-tenant", state.Request.SessionId, CancellationToken.None) is not null || (await reopened.ListAsync("smoke", CancellationToken.None)).Count == 0)
            throw new InvalidOperationException("Published persistence or tenant isolation failed.");
        state.Revision = 2;
        if (await reopened.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("A stale update was accepted.");
        await Reviews.ReviewPersistenceSmoke.RunAsync(records);
        foreach (var file in Directory.EnumerateFiles(directory, "*.db"))
            if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)) is { } bytes &&
                (bytes.Contains("Private published smoke content", StringComparison.Ordinal) || bytes.Contains("Private published review smoke", StringComparison.Ordinal)))
                throw new InvalidOperationException("Sensitive session content was persisted unencrypted.");
        Console.WriteLine("Schema-7 planning persistence smoke passed.");
    }
}
