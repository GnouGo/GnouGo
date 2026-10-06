using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Flow.Persistence;

/// <summary>KeyVault is authoritative. SQLite stores no execution content and can be rebuilt at any time.</summary>
public sealed partial class EncryptedWorkflowRunStore : IWorkflowRunStore, IMappingArtifactStore
{
    public const string Collection = "flow-execution-journal-v9";
    private const string Author = "GnOuGo.Flow.Persistence";
    private readonly IKeyVaultRecordStore _records;
    private readonly string _lockDirectory;
    private readonly DbContextOptions<WorkflowRunDbContext> _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public EncryptedWorkflowRunStore(IKeyVaultRecordStore records, string indexPath, string lockDirectory, ILogger? logger = null)
    {
        _records = records;
        _lockDirectory = Path.GetFullPath(lockDirectory);
        Directory.CreateDirectory(_lockDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        _options = new DbContextOptionsBuilder<WorkflowRunDbContext>().UseSqlite("Data Source=" + indexPath).UseModel(CompiledModels.WorkflowRunDbContextModel.Instance).Options;
        _logger = logger ?? NullLogger.Instance;
    }

    public static EncryptedWorkflowRunStore CreateWorkspace(string? keyVaultPath = null, string? indexPath = null,
        string? baseDirectory = null, ILogger? logger = null, string? ownerPath = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var path = GnOuGoWorkspace.ResolveDatabasePath(indexPath, root, ".GnOuGo/data/flow-execution-v9.db");
        return new(KeyVaultRecordStoreFactory.CreateWorkspaceStore(keyVaultPath, root), path,
            GnOuGoWorkspace.ResolveDatabasePath(ownerPath, root, ".GnOuGo/data/flow-execution-v9/owners"), logger);
    }

    private const string MappingCollection = "flow-validated-mappings-v1";
    async Task<MappingArtifact?> IMappingArtifactStore.ReadAsync(string tenant, string key, CancellationToken ct)
    {
        ValidateKey(tenant, key);
        var record = await _records.GetAsync(MappingCollection, tenant, key, Author, ct);
        if (record is null) return null;
        var artifact = JsonSerializer.Deserialize(record.Value, MappingArtifactJsonContext.Default.MappingArtifact);
        return artifact?.Key == key ? artifact : null;
    }
    async Task IMappingArtifactStore.WriteAsync(string tenant, MappingArtifact artifact, CancellationToken ct)
    {
        ValidateKey(tenant, artifact.Key);
        GnOuGo.Flow.Core.Scripting.JintSandbox.ValidateMapping(artifact.Script);
        await _records.UpsertAsync(MappingCollection, tenant, artifact.Key,
            JsonSerializer.Serialize(artifact, MappingArtifactJsonContext.Default.MappingArtifact), Author, ct);
    }
    async Task IMappingArtifactStore.RemoveAsync(string tenant, string key, CancellationToken ct)
    {
        ValidateKey(tenant, key);
        await _records.DeleteAsync(MappingCollection, tenant, key, Author, ct);
    }

    public async Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        ValidateKey(tenantId, runId);
        var record = await _records.GetAsync(Collection, tenantId, runId, Author, ct);
        if (record is null) return null;
        var head = SplitJournal.Parse(record.Value, tenantId, runId);
        return head is null ? WorkflowRunStorage.Read(record.Value, tenantId, runId) :
            await new SplitJournal(_records, tenantId, runId, head).ReadAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var records = await _records.ListAsync(Collection, tenantId, Author, ct);
        var loaded = new List<WorkflowRun>();
        foreach (var record in records)
        {
            var head = SplitJournal.Parse(record.Value, tenantId, record.Key);
            loaded.Add(head is null ? WorkflowRunStorage.Read(record.Value, tenantId, record.Key) :
                await new SplitJournal(_records, tenantId, record.Key, head).ReadAsync(ct));
        }
        var runs = loaded.OrderByDescending(r => r.UpdatedAt).ToArray();
        foreach (var run in runs) await IndexAsync(run, ct);
        return runs;
    }

    public async Task CreateAsync(WorkflowRun run, CancellationToken ct = default)
    {
        WorkflowRunStorage.ValidateOwnership(run, run.TenantId, run.RunId);
        if (run.Revision != 0) throw new WorkflowRunConflictException("A new run must start at revision zero.");
        await using var write = await WriteLockAsync(run.TenantId, run.RunId, ct);
        if (await ReadAsync(run.TenantId, run.RunId, ct) is not null)
            throw new WorkflowRunConflictException("This run already exists. Inspect it and resume using its current revision.");
        var journal = new SplitJournal(_records, run.TenantId, run.RunId, SplitJournal.NewHead(run.TenantId, run.RunId));
        await journal.SaveAsync(run, null, ct);
        await IndexAsync(run, CancellationToken.None);
    }

    public async Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
    {
        ValidateKey(tenantId, runId);
        FileStream owner;
        try { owner = OpenLock(tenantId, runId, "owner"); }
        catch (IOException) { throw new WorkflowRunConflictException("This run already has an active owner."); }
        try
        {
            await using var write = await WriteLockAsync(tenantId, runId, ct);
            var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct);
            return new Lease(this, run, owner, journal);
        }
        catch { await owner.DisposeAsync(); throw; }
    }

    public async Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
    {
        await using var write = await WriteLockAsync(tenantId, runId, ct);
        var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct);
        run.CancelRequested = true;
        run.Revision++;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        run.Events.Add(new(run.UpdatedAt, "cancel_requested", null));
        await PersistAsync(run, ct, journal, []);
        return run;
    }

    private async Task<(WorkflowRun Run, SplitJournal? Journal)> ReadRequiredAsync(string tenant, string id, long revision, CancellationToken ct)
    {
        var record = await _records.GetAsync(Collection, tenant, id, Author, ct) ?? throw new WorkflowRunConflictException("Run not found for this tenant.");
        var head = SplitJournal.Parse(record.Value, tenant, id);
        var journal = head is null ? null : new SplitJournal(_records, tenant, id, head);
        var run = journal is null ? WorkflowRunStorage.Read(record.Value, tenant, id) : await journal.ReadAsync(ct);
        if (run.Revision != revision) throw new WorkflowRunConflictException("The run changed. Inspect its current revision before issuing a command.");
        return (run, journal);
    }

    public async Task<WorkflowRun> RequestInputAsync(string tenantId, string runId, long expectedRevision, HumanInputRequest request, CancellationToken ct = default)
    {
        await using var write = await WriteLockAsync(tenantId, runId, ct);
        var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct);
        WorkflowRunStorage.RequestInput(run, request);
        run.Revision++; run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistAsync(run, ct, journal, [request.StepId]); return run;
    }

    public async Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId,
        System.Text.Json.Nodes.JsonNode? response, CancellationToken ct = default)
    {
        await using var write = await WriteLockAsync(tenantId, runId, ct);
        var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct);
        WorkflowRunStorage.Answer(run, invocationId, response);
        run.Revision++;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistAsync(run, ct, journal, [invocationId]);
        return run;
    }

    private async Task PersistAsync(WorkflowRun run, CancellationToken ct, SplitJournal? journal = null, IReadOnlyCollection<string>? changed = null)
    {
        WorkflowRunStorage.ValidateOwnership(run, run.TenantId, run.RunId);
        if (journal is null)
            await _records.UpsertAsync(Collection, run.TenantId, run.RunId,
                JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun), Author, ct);
        else
            await journal.SaveAsync(run, changed, ct);
        await IndexAsync(run, CancellationToken.None);
    }

    private async Task IndexAsync(WorkflowRun run, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try
        {
            await using var db = new WorkflowRunDbContext(_options);
            // Fixed, content-free schema created through the owning EF Core context. Avoid runtime migrations under Native AOT.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS WorkflowRuns (
                    TenantId TEXT NOT NULL, RunId TEXT NOT NULL, Revision INTEGER NOT NULL,
                    Status TEXT NOT NULL, UpdatedAtTicks INTEGER NOT NULL, PRIMARY KEY (TenantId, RunId));
                CREATE INDEX IF NOT EXISTS IX_WorkflowRuns_TenantId_UpdatedAtTicks ON WorkflowRuns (TenantId, UpdatedAtTicks);
                """, ct);
            var tenant = run.TenantId;
            var id = run.RunId;
            // EF Core 10.0.12 query precompilation resolves local tokens but fails on a method parameter.
            var queryCancellation = ct;
            var row = await db.Runs.SingleOrDefaultAsync(r => r.TenantId == tenant && r.RunId == id, queryCancellation);
            if (row is null) { row = new() { TenantId = tenant, RunId = id }; db.Runs.Add(row); }
            if (row.Revision > run.Revision) return;
            row.Revision = run.Revision;
            row.Status = run.Status;
            row.UpdatedAtTicks = run.UpdatedAt.UtcTicks;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not turn a committed authoritative receipt into an uncertain dispatch because a cache failed.
            _logger.LogWarning("Workflow run index update failed ({ErrorType}); the encrypted journal remains authoritative and the index will be rebuilt.", ex.GetType().Name);
        }
        finally { _indexGate.Release(); }
    }

    private FileStream OpenLock(string tenant, string id, string kind) => new(
        Path.Combine(_lockDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonArray(tenant, id).ToJsonString()))) + "." + kind),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private async Task<FileStream> WriteLockAsync(string tenant, string id, CancellationToken ct)
    {
        ValidateKey(tenant, id);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return OpenLock(tenant, id, "write"); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(20, ct); }
        }
    }

    private static void ValidateKey(string tenant, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
    }

    private sealed class Lease(EncryptedWorkflowRunStore store, WorkflowRun run, FileStream owner, SplitJournal? journal) : IWorkflowRunLease
    {
        private bool _disposed;
        public WorkflowRun Run { get; } = run;
        public Task SaveAsync(CancellationToken ct = default) => SaveCoreAsync(null, ct);
        public Task SaveAsync(IReadOnlyCollection<string> changedInvocationIds, CancellationToken ct = default) => SaveCoreAsync(changedInvocationIds, ct);
        private async Task SaveCoreAsync(IReadOnlyCollection<string>? changed, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using var write = await store.WriteLockAsync(Run.TenantId, Run.RunId, ct);
            if (journal is null)
            {
                var saved = await store.ReadAsync(Run.TenantId, Run.RunId, ct)
                    ?? throw new WorkflowRunConflictException("The owned execution journal is missing.");
                WorkflowRunStorage.MergeCommands(Run, saved);
                Run.Revision = saved.Revision + 1;
                Run.UpdatedAt = DateTimeOffset.UtcNow;
                await store.PersistAsync(Run, ct);
            }
            else
            {
                var commands = await journal.MergeCommandsAsync(Run, ct);
                Run.Revision = journal.Revision + 1;
                Run.UpdatedAt = DateTimeOffset.UtcNow;
                await journal.SaveAsync(Run, changed is null ? null : changed.Concat(commands).Distinct(StringComparer.Ordinal).ToArray(), ct);
                await store.IndexAsync(Run, CancellationToken.None);
            }
        }
        public async Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (journal is null) return (await store.ReadAsync(Run.TenantId, Run.RunId, ct))?.CancelRequested
                ?? throw new WorkflowRunConflictException("The owned execution journal is missing.");
            // Do not mutate the lease's merge baseline from the monitor thread.
            var record = await store._records.GetAsync(Collection, Run.TenantId, Run.RunId, Author, ct)
                ?? throw new WorkflowRunConflictException("The owned execution journal is missing.");
            return SplitJournal.Parse(record.Value, Run.TenantId, Run.RunId)?["cancelRequested"]?.GetValue<bool>()
                ?? throw new WorkflowRunConflictException("The execution journal layout changed while owned.");
        }
        public async ValueTask DisposeAsync() { _disposed = true; await owner.DisposeAsync(); }
    }
}
