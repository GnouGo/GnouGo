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
        state.Revision = 1; state.Status = PlanningStatus.BehaviorReview;
        if (!await store.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("Revision update failed.");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        if ((await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None))?.Revision != 1 ||
            await reopened.LoadAsync("different", state.Request.SessionId, CancellationToken.None) is not null ||
            (await reopened.ListAsync("smoke", CancellationToken.None)).Count == 0)
            throw new InvalidOperationException("Persistence or tenant isolation failed.");
        state.Revision = 2;
        if (await reopened.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("A stale write was accepted.");
        Console.WriteLine("Planning persistence smoke passed.");
    }
}
