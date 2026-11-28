using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GnOuGo.Agent.Server.Planning;

internal static class OwnerRetentionSmoke
{
    private const string Tenant = "retention-smoke";
    private static readonly DateTimeOffset Anchor = new(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
    private sealed class SmokeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task RunAsync(string directory, bool write)
    {
        if (write && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("The write fixture needs an empty scratch directory.");
        if (!write && !File.Exists(Path.Combine(directory, "vault.db")))
            throw new InvalidOperationException("The encrypted write fixture is missing.");
        Directory.CreateDirectory(directory);
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(directory, "vault.db"), directory);
        var clock = new SmokeClock(write ? Anchor : Anchor.AddDays(1));
        var policy = new OwnerRetentionPolicy(write ? 2 : 7, clock);
        // The verifier starts with fresh projection databases and exactly the same protected records.
        var prefix = write ? "write-" : "rebuilt-";
        var planningOptions = new DbContextOptionsBuilder<PlanningDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(directory, prefix + "planning.db"))
            .UseModel(CompiledModels.PlanningDbContextModel.Instance).Options;
        var factory = new PooledDbContextFactory<PlanningDbContext>(planningOptions);
        await using var planningDb = factory.CreateDbContext();
        await planningDb.Database.EnsureCreatedAsync();
        var plans = new EfPlanningSessionStore(factory, records, policy);
        var index = Path.Combine(directory, prefix + "runs.db");
        var files = new GnOuGo.Files.Core.DurableFileStore(Path.Combine(directory, "Files"), records, clock);
        var runs = new EncryptedWorkflowRunStore(records, index, Path.Combine(directory, "owners"), null, policy, files);
        byte[] image = [10, 20, 30];
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image));
        var ct = CancellationToken.None;
        var payload = new JsonObject { ["shared"] = new string('x', 8192) };
        if (write)
        {
            var session = new PlanningSession { Request = new() { TenantId = Tenant, SessionId = "plan" },
                Status = PlanningStatus.Clarification, PendingQuestions = [new("q", "Continue?", [], null)] };
            if (!await plans.TrySaveAsync(session, null, ct)) throw new InvalidOperationException("Fixture already exists.");
            var run = new WorkflowRun { TenantId = Tenant, RunId = "split", Status = WorkflowRunStatus.Completed,
                Limits = new() { TenantId = Tenant, RunId = "split" } };
            run.Invocations["step"] = new() { Id = "step", StepType = "set", Status = "completed",
                DataBefore = payload.DeepClone().AsObject(), DataAfter = payload.DeepClone().AsObject() };
            await runs.CreateAsync(run, ct);
            await ((IWorkflowImageStore)runs).PutAsync(Tenant, "split", digest, image, ct);
        }
        else
        {
            await plans.RebuildIndexAsync(Tenant, ct);
            var plan = (await plans.ListAsync(Tenant, ct)).Single();
            Verify(plan.Retention, OwnerRetention.Waiting);
            if (plan.Request.SessionId != "plan" || plan.Revision != 0 || await plans.LoadAsync("foreign", "plan", ct) is not null)
                throw new InvalidOperationException("The committed planning owner changed.");
            var projectedPlan = await planningDb.Retention.SingleAsync();
            if (projectedPlan.Revision != plan.Revision || projectedPlan.DeadlineUtcTicks != Anchor.AddDays(2).UtcTicks)
                throw new InvalidOperationException("The rebuilt planning projection differs from the encrypted owner.");
            var restored = await runs.ListAsync(Tenant, ct);
            if (!restored.Select(r => r.RunId).Order().SequenceEqual(["split"]))
                throw new InvalidOperationException("An execution root was lost.");
            foreach (var run in restored)
            {
                Verify(run.Retention, OwnerRetention.Terminal);
                var step = run.Invocations["step"];
                if (!JsonNode.DeepEquals(step.DataBefore, payload) || !JsonNode.DeepEquals(step.DataAfter, payload))
                    throw new InvalidOperationException("A reachable execution snapshot changed.");
            }
            // Read the physical scratch index independently of the EF writer to verify what the binary actually published.
            await using var indexConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = index, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
            await indexConnection.OpenAsync();
            await using var command = indexConnection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM OwnerRetention WHERE TenantId = $tenant AND OwnerKind = 'execution'
                  AND OwnerId = 'split' AND Phase = 'terminal' AND DeadlineUtcTicks = $deadline
                """;
            command.Parameters.AddWithValue("$tenant", Tenant);
            command.Parameters.AddWithValue("$deadline", Anchor.AddDays(2).UtcTicks);
            if ((long?)await command.ExecuteScalarAsync() != 1)
                throw new InvalidOperationException("The rebuilt execution projection differs from the encrypted owner.");
            if (await ((IWorkflowImageStore)runs).GetAsync(Tenant, "split", digest, ct) is not { } restoredImage ||
                !restoredImage.SequenceEqual(image)) throw new InvalidOperationException("The run image was lost.");
            clock.Now = Anchor.AddDays(2);
            if (await plans.LoadAsync(Tenant, "plan", ct) is not null || await runs.ReadAsync(Tenant, "split", ct) is not null)
                throw new InvalidOperationException("An expired owner was available at the boundary.");
            await plans.SweepAsync(Tenant, ct);
            await runs.SweepAsync(Tenant, ct);
            await plans.RebuildIndexAsync(Tenant, ct);
            if ((await runs.ListAsync(Tenant, ct)).Count != 0 || (await plans.ListAsync(Tenant, ct)).Count != 0 ||
                Directory.EnumerateFiles(directory, "*.enc", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Expired dependencies or index rows were retained.");
        }
        Console.WriteLine(write ? "Wrote two encrypted retention owners." : "Recovered both owners, rebuilt their indexes and collected them at the deadline.");
    }

    private static void Verify(OwnerRetention? retention, string phase)
    {
        if (retention is not { IsValid: true, PolicyVersion: OwnerRetentionPolicy.CurrentVersion, ResumeDays: 2, TerminalDays: 2 } ||
            retention.Phase != phase || retention.EnteredUtc != Anchor || retention.CheckpointUtc != Anchor ||
            retention.DeadlineUtc != Anchor.AddDays(2) || retention.CrashResumeUntilUtc != Anchor.AddDays(2))
            throw new InvalidOperationException("Cold recovery changed the captured policy or deadline.");
    }
}
