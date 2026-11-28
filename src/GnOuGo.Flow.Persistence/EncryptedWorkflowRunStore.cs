using GnOuGo.Files.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Flow.Persistence;

/// <summary>KeyVault is authoritative. SQLite stores no execution content and can be rebuilt at any time.</summary>
public sealed partial class EncryptedWorkflowRunStore : IWorkflowRunStore, IWorkflowAttachmentRunStore, IMappingArtifactStore, IWorkflowImageStore
{
    public const string Collection = "flow-execution-journal-v9";
    private const string Author = "GnOuGo.Flow.Persistence";
    private readonly IKeyVaultRecordStore _records;
    private readonly string _lockDirectory;
    private readonly DbContextOptions<WorkflowRunDbContext> _options;
    private readonly ILogger _logger;
    private readonly OwnerRetentionPolicy _retention;
    public DurableFileStore Files { get; }
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public EncryptedWorkflowRunStore(IKeyVaultRecordStore records, string indexPath, string lockDirectory, ILogger? logger = null)
        : this(records, indexPath, lockDirectory, logger, OwnerRetentionPolicy.Default) { }

    public EncryptedWorkflowRunStore(IKeyVaultRecordStore records, string indexPath, string lockDirectory, ILogger? logger, OwnerRetentionPolicy retention, DurableFileStore? files = null)
    {
        _retention = retention;
        Files = files ?? new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath))!, "Files"), records, retention.Clock);
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
        return CreateWorkspaceWithRetention(OwnerRetentionPolicy.Default, keyVaultPath, indexPath, baseDirectory, logger, ownerPath);
    }

    public static EncryptedWorkflowRunStore CreateWorkspaceWithRetention(OwnerRetentionPolicy retention, string? keyVaultPath = null,
        string? indexPath = null, string? baseDirectory = null, ILogger? logger = null, string? ownerPath = null, string? filesPath = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var path = GnOuGoWorkspace.ResolveDatabasePath(indexPath, root, ".GnOuGo/data/flow-execution-v9.db");
        var vault = KeyVaultRecordStoreFactory.CreateWorkspaceStore(keyVaultPath, root);
        var filesRoot = DurableFileStore.ResolveRoot(filesPath, root);
        return new(vault, path,
            GnOuGoWorkspace.ResolveDatabasePath(ownerPath, root, ".GnOuGo/data/flow-execution-v9/owners"), logger, retention, new(filesRoot, vault, retention.Clock));
    }

    async Task IWorkflowImageStore.PutAsync(string tenant, string run, string sha256, byte[] bytes, CancellationToken ct)
    {
        await using var write = await WriteLockAsync(tenant, run, ct);
        if (!await CanUseImagesAsync(tenant, run, ct)) throw new WorkflowRunConflictException("Image owner unavailable.");
        await Files.PutImageAsync(tenant, new(FileOwnerKind.Execution, run), sha256, bytes, ct);
    }

    async Task<byte[]?> IWorkflowImageStore.GetAsync(string tenant, string run, string sha256, CancellationToken ct)
    {
        await using var write = await WriteLockAsync(tenant, run, ct);
        return await CanUseImagesAsync(tenant, run, ct)
            ? await Files.ReadImageAsync(tenant, new(FileOwnerKind.Execution, run), sha256, ct) : null;
    }

    private async Task<bool> CanUseImagesAsync(string tenant, string run, CancellationToken ct)
    {
        if (await IsFencedAsync(tenant, run, ct)) return false;
        var record = await _records.GetAsync(Collection, tenant, run, Author, ct);
        if (record is null || SplitJournal.Parse(record.Value, tenant, run) is not { } head) return false;
        return !IsExpired(tenant, run, new SplitJournal(_records, tenant, run, head).CommittedRetention, acquiring: false);
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
        await using var write = await WriteLockAsync(tenantId, runId, ct);
        return await ReadUnlockedAsync(tenantId, runId, ct);
    }

    private async Task<WorkflowRun?> ReadUnlockedAsync(string tenantId, string runId, CancellationToken ct)
    {
        if (await IsFencedAsync(tenantId, runId, ct)) return null;
        var record = await _records.GetAsync(Collection, tenantId, runId, Author, ct);
        if (record is null || SplitJournal.Parse(record.Value, tenantId, runId) is not { } head) return null;
        var journal = new SplitJournal(_records, tenantId, runId, head);
        if (IsExpired(tenantId, runId, journal.CommittedRetention, acquiring: false)) return null;
        return await journal.ReadAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var loaded = new List<WorkflowRun>();
        await foreach (var id in _records.EnumerateKeysAsync(Collection, tenantId, "", Author, ct))
        {
            await using var write = await WriteLockAsync(tenantId, id, ct);
            if (await ReadUnlockedAsync(tenantId, id, ct) is not { } run) continue;
            await IndexAsync(run, ct);
            loaded.Add(run);
        }
        return loaded.OrderByDescending(r => r.UpdatedAt).ToArray();
    }

    private const string CreationIntents = "flow-run-creation-intents-v1";

    public Task CreateAsync(WorkflowRun run, CancellationToken ct = default) => CreateWithUploadsAsync(run, [], null, ct);

    public async Task CreateWithUploadsAsync(WorkflowRun run, IReadOnlyList<string> uploadIds, string? savedAgentName, CancellationToken ct)
    {
        WorkflowRunStorage.ValidateOwnership(run, run.TenantId, run.RunId);
        if (run.Revision != 0) throw new WorkflowRunConflictException("A new run must start at revision zero.");
        await using var write = await WriteLockAsync(run.TenantId, run.RunId, ct);
        if (await IsFencedAsync(run.TenantId, run.RunId, ct) || await _records.GetAsync(Collection, run.TenantId, run.RunId, Author, ct) is not null)
            throw new WorkflowRunConflictException("This run already exists. Inspect it and resume using its current revision.");
        if (await _records.GetAsync(CreationIntents, run.TenantId, run.RunId, Author, ct) is not null)
        {
            await ReleaseDependenciesAsync(run.TenantId, run.RunId, ct);
            await _records.DeleteAsync(CreationIntents, run.TenantId, run.RunId, Author, ct);
        }
        run.Retention = _retention.Capture().ForRun(run, _retention.Clock.GetUtcNow(), true);
        var journal = new SplitJournal(_records, run.TenantId, run.RunId, SplitJournal.NewHead(run.TenantId, run.RunId));
        await _records.UpsertAsync(CreationIntents, run.TenantId, run.RunId, "pending", Author, ct);
        if (uploadIds.Count > 0 || savedAgentName is not null)
        {
            if (savedAgentName is not null)
            {
                var workflow = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(run.WorkflowYaml);
                await Files.TransferReferencesAsync(run.TenantId, FileOwner.SavedVersion(savedAgentName, run.WorkflowYaml),
                    new(FileOwnerKind.Execution, run.RunId), JsonSerializer.SerializeToNode(workflow, WorkflowRunJsonContext.Default.WorkflowDocument), ct);
            }
            foreach (var id in uploadIds.Distinct(StringComparer.Ordinal))
                await Files.ClaimAsync(run.TenantId, id, null, new(FileOwnerKind.Execution, run.RunId), ct);
        }
        await journal.SaveAsync(run, null, ct);
        await _records.DeleteAsync(CreationIntents, run.TenantId, run.RunId, Author, ct);
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
            var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct, acquiring: true);
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

    private async Task<(WorkflowRun Run, SplitJournal Journal)> ReadRequiredAsync(string tenant, string id, long revision, CancellationToken ct, bool acquiring = false)
    {
        if (await IsFencedAsync(tenant, id, ct)) throw new WorkflowRunConflictException("owner_expired");
        var record = await _records.GetAsync(Collection, tenant, id, Author, ct) ?? throw new WorkflowRunConflictException("Run not found for this tenant.");
        var head = SplitJournal.Parse(record.Value, tenant, id);
        if (head is null) throw new WorkflowRunConflictException("owner_expired");
        var journal = new SplitJournal(_records, tenant, id, head);
        if (IsExpired(tenant, id, journal.CommittedRetention, acquiring)) throw new WorkflowRunConflictException("owner_expired");
        var run = await journal.ReadAsync(ct, deferred: true);
        if (run.Revision != revision) throw new WorkflowRunConflictException("The run changed. Inspect its current revision before issuing a command.");
        return (run, journal);
    }

    public async Task<WorkflowRun> RequestInputAsync(string tenantId, string runId, long expectedRevision, HumanInputRequest request, CancellationToken ct = default)
    {
        await using var write = await WriteLockAsync(tenantId, runId, ct);
        var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct);
        WorkflowRunStorage.RequestInput(run, request);
        AdvanceRetention(run, true, request.StepId);
        run.Revision++; run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistAsync(run, ct, journal, [request.StepId]); return run;
    }

    public async Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId,
        System.Text.Json.Nodes.JsonNode? response, CancellationToken ct = default)
    {
        await using var write = await WriteLockAsync(tenantId, runId, ct);
        var (run, journal) = await ReadRequiredAsync(tenantId, runId, expectedRevision, ct);
        WorkflowRunStorage.Answer(run, invocationId, response);
        AdvanceRetention(run, true);
        run.Revision++;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistAsync(run, ct, journal, [invocationId]);
        return run;
    }

    private void AdvanceRetention(WorkflowRun run, bool checkpoint, string? requestedInput = null) =>
        run.Retention = run.Retention?.ForRun(run, _retention.Clock.GetUtcNow(), checkpoint, requestedInput);

    private async Task PersistAsync(WorkflowRun run, CancellationToken ct, SplitJournal journal, IReadOnlyCollection<string>? changed = null)
    {
        WorkflowRunStorage.ValidateOwnership(run, run.TenantId, run.RunId);
        await journal.SaveAsync(run, changed, ct);
        await IndexAsync(run, CancellationToken.None);
    }

    private static Task EnsureRunTableAsync(WorkflowRunDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS WorkflowRuns (
                    TenantId TEXT NOT NULL, RunId TEXT NOT NULL, Revision INTEGER NOT NULL,
                    Status TEXT NOT NULL, UpdatedAtTicks INTEGER NOT NULL, PRIMARY KEY (TenantId, RunId));
                CREATE INDEX IF NOT EXISTS IX_WorkflowRuns_TenantId_UpdatedAtTicks ON WorkflowRuns (TenantId, UpdatedAtTicks);
                """, ct);

    private async Task IndexAsync(WorkflowRun run, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try
        {
            await using var db = new WorkflowRunDbContext(_options);
            await EnsureRunTableAsync(db, ct);
            // A list rebuild and a writer can originate in different processes. Serialize the revision check and projection.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var tenant = run.TenantId;
            var id = run.RunId;
            // EF Core 11.0.0 query precompilation resolves local tokens but fails on a method parameter.
            var queryCancellation = ct;
            var row = await db.Runs.SingleOrDefaultAsync(r => r.TenantId == tenant && r.RunId == id, queryCancellation);
            if (row is null) { row = new() { TenantId = tenant, RunId = id }; db.Runs.Add(row); }
            if (row.Revision > run.Revision) return;
            row.Revision = run.Revision;
            if (run.Retention is { } retention)
            {
                await OwnerRetentionIndex.EnsureTableAsync(db, ct);
                var projection = await db.Retention.SingleOrDefaultAsync(r =>
                    r.TenantId == tenant && r.OwnerKind == "execution" && r.OwnerId == id, queryCancellation);
                OwnerRetentionIndex.Project(db.Retention, projection, tenant, "execution", id, run.Revision, retention);
            }
            row.Status = run.Status;
            row.UpdatedAtTicks = run.UpdatedAt.UtcTicks;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
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

    private sealed class Lease(EncryptedWorkflowRunStore store, WorkflowRun run, FileStream owner, SplitJournal journal) : IWorkflowRunLease
    {
        private bool _disposed;
        private readonly SemaphoreSlim _snapshotGate = new(1, 1);
        public WorkflowRun Run { get; } = run;
        private int _committedEventCount = run.Events.Count;
        public Task SaveAsync(CancellationToken ct = default) => SaveCoreAsync(null, ct);
        public Task SaveAsync(IReadOnlyCollection<string> changedInvocationIds, CancellationToken ct = default) => SaveCoreAsync(changedInvocationIds, ct);
        private async Task SaveCoreAsync(IReadOnlyCollection<string>? changed, CancellationToken ct)
        {
            await _snapshotGate.WaitAsync(ct);
            try { await SaveLockedAsync(changed, ct); }
            finally { _snapshotGate.Release(); }
        }
        public async Task CaptureSnapshotAsync(WorkflowInvocation invocation, JsonObject data, bool after, CancellationToken ct = default)
        {
            await _snapshotGate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await journal.CaptureAsync(invocation, data, after, ct);
            }
            finally { _snapshotGate.Release(); }
        }
        public async Task RestoreSnapshotAsync(WorkflowInvocation invocation, JsonObject data, bool after, CancellationToken ct = default)
        {
            await _snapshotGate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await journal.RestoreAsync(invocation, data, after, ct);
            }
            finally { _snapshotGate.Release(); }
        }
        private async Task SaveLockedAsync(IReadOnlyCollection<string>? changed, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using var write = await store.WriteLockAsync(Run.TenantId, Run.RunId, ct);
            var checkpoint = Run.Events.Skip(_committedEventCount).Any(e => e.Kind is not ("model_usage" or "cancel_requested"));
            var commands = await journal.MergeCommandsAsync(Run, ct);
            Run.Retention = journal.CommittedRetention;
            if (Run.Retention is { Phase: OwnerRetention.Waiting } wait && wait.IsDue(store._retention.Clock.GetUtcNow()))
                throw new WorkflowRunConflictException("owner_expired");
            store.AdvanceRetention(Run, checkpoint);
            Run.Revision = journal.Revision + 1;
            Run.UpdatedAt = DateTimeOffset.UtcNow;
            await journal.SaveAsync(Run, changed is null ? null : changed.Concat(commands).Distinct(StringComparer.Ordinal).ToArray(), ct);
            await store.IndexAsync(Run, CancellationToken.None);
            _committedEventCount = Run.Events.Count;
        }

        public async Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Do not mutate the lease's merge baseline from the monitor thread.
            var record = await store._records.GetAsync(Collection, Run.TenantId, Run.RunId, Author, ct)
                ?? throw new WorkflowRunConflictException("The owned execution journal is missing.");
            return SplitJournal.Parse(record.Value, Run.TenantId, Run.RunId)?["cancelRequested"]?.GetValue<bool>()
                ?? throw new WorkflowRunConflictException("The execution journal layout changed while owned.");
        }
        public async ValueTask DisposeAsync() { _disposed = true; await owner.DisposeAsync(); }
    }
}
