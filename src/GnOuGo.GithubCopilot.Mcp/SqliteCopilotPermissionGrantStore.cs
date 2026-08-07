using System.Collections.Concurrent;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class SqliteCopilotPermissionGrantStore : ICopilotPermissionGrantStore
{
    private readonly ConcurrentDictionary<string, CopilotPermissionGrant> _workflowGrants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly string _databasePath;
    private readonly TimeSpan _workflowGrantTtl;
    private bool _initialized;

    public SqliteCopilotPermissionGrantStore(IOptions<CodeServerSettings> settings)
    {
        var configured = settings.Value.Copilot;
        _databasePath = GnOuGoWorkspace.ResolveDatabasePath(
            configured.PermissionDatabasePath,
            AppContext.BaseDirectory,
            ".GnOuGo/data/gnougo-copilot-permissions.db");
        _workflowGrantTtl = TimeSpan.FromSeconds(Math.Max(60, configured.WorkflowGrantTtlSeconds));
    }

    public async Task<CopilotPermissionGrant?> FindReusableGrantAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken)
    {
        ValidateTenant(context.TenantId);
        var now = DateTimeOffset.UtcNow;
        SweepExpiredWorkflowGrants(now);

        if (!string.IsNullOrWhiteSpace(context.ExecutionId)
            && _workflowGrants.TryGetValue(BuildWorkflowKey(context.TenantId, context.ExecutionId), out var workflowGrant))
        {
            var touched = workflowGrant with { LastUsedAt = now, ExpiresAt = now + _workflowGrantTtl };
            _workflowGrants[BuildWorkflowKey(context.TenantId, context.ExecutionId)] = touched;
            return touched;
        }

        if (string.IsNullOrWhiteSpace(context.AgentId))
            return null;

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureInitializedAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, TenantId, AgentId, AgentName, CreatedAtTicks, LastUsedAtTicks
                FROM CopilotPermissionGrants
                WHERE TenantId = $tenantId AND AgentId = $agentId
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$tenantId", context.TenantId);
            command.Parameters.AddWithValue("$agentId", context.AgentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var grant = ReadPersistentGrant(reader);
            await reader.DisposeAsync();
            await using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE CopilotPermissionGrants SET LastUsedAtTicks = $ticks WHERE Id = $id;";
            touch.Parameters.AddWithValue("$ticks", now.UtcTicks);
            touch.Parameters.AddWithValue("$id", grant.Id);
            await touch.ExecuteNonQueryAsync(cancellationToken);
            return grant with { LastUsedAt = now };
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public Task<CopilotPermissionGrant> GrantWorkflowRunAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken)
    {
        ValidateTenant(context.TenantId);
        if (string.IsNullOrWhiteSpace(context.ExecutionId))
            throw new InvalidOperationException("A stable execution ID is required for workflow-run approval.");
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var key = BuildWorkflowKey(context.TenantId, context.ExecutionId);
        var grant = _workflowGrants.AddOrUpdate(
            key,
            _ => new CopilotPermissionGrant(
                Guid.NewGuid().ToString("N"),
                context.TenantId,
                CopilotPermissionGrantScope.WorkflowRun,
                context.ExecutionId,
                context.AgentId,
                context.AgentName,
                now,
                now,
                now + _workflowGrantTtl),
            (_, existing) => existing with
            {
                AgentId = context.AgentId ?? existing.AgentId,
                AgentName = context.AgentName ?? existing.AgentName,
                LastUsedAt = now,
                ExpiresAt = now + _workflowGrantTtl
            });
        return Task.FromResult(grant);
    }

    public async Task<CopilotPermissionGrant> GrantFutureAgentRunsAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken)
    {
        ValidateTenant(context.TenantId);
        if (string.IsNullOrWhiteSpace(context.AgentId))
            throw new InvalidOperationException("A stable agent ID is required for future-agent approval.");

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureInitializedAsync(connection, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid().ToString("N");
            await using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO CopilotPermissionGrants
                    (Id, TenantId, AgentId, AgentName, CreatedAtTicks, LastUsedAtTicks)
                VALUES
                    ($id, $tenantId, $agentId, $agentName, $createdAt, $lastUsedAt)
                ON CONFLICT(TenantId, AgentId) DO UPDATE SET
                    AgentName = excluded.AgentName,
                    LastUsedAtTicks = excluded.LastUsedAtTicks;
                """;
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$tenantId", context.TenantId);
            upsert.Parameters.AddWithValue("$agentId", context.AgentId);
            upsert.Parameters.AddWithValue("$agentName", (object?)context.AgentName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$createdAt", now.UtcTicks);
            upsert.Parameters.AddWithValue("$lastUsedAt", now.UtcTicks);
            await upsert.ExecuteNonQueryAsync(cancellationToken);

            return await ReadPersistentGrantAsync(connection, context.TenantId, context.AgentId, cancellationToken)
                   ?? throw new InvalidOperationException("The persistent Copilot permission grant could not be read after saving.");
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<CopilotPermissionGrant>> ListFutureAgentGrantsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        ValidateTenant(tenantId);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureInitializedAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, TenantId, AgentId, AgentName, CreatedAtTicks, LastUsedAtTicks
                FROM CopilotPermissionGrants
                WHERE TenantId = $tenantId
                ORDER BY AgentName COLLATE NOCASE, AgentId;
                """;
            command.Parameters.AddWithValue("$tenantId", tenantId);
            var grants = new List<CopilotPermissionGrant>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                grants.Add(ReadPersistentGrant(reader));
            return grants;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<bool> RevokeAsync(string tenantId, string grantId, CancellationToken cancellationToken)
    {
        ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(grantId))
            throw new ArgumentException("Grant ID is required.", nameof(grantId));
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureInitializedAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CopilotPermissionGrants WHERE TenantId = $tenantId AND Id = $id;";
            command.Parameters.AddWithValue("$tenantId", tenantId);
            command.Parameters.AddWithValue("$id", grantId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<int> RevokeAgentAsync(string tenantId, string agentId, CancellationToken cancellationToken)
    {
        ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent ID is required.", nameof(agentId));
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureInitializedAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CopilotPermissionGrants WHERE TenantId = $tenantId AND AgentId = $agentId;";
            command.Parameters.AddWithValue("$tenantId", tenantId);
            command.Parameters.AddWithValue("$agentId", agentId);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task EnsureInitializedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CopilotPermissionGrants (
                Id TEXT NOT NULL PRIMARY KEY,
                TenantId TEXT NOT NULL,
                AgentId TEXT NOT NULL,
                AgentName TEXT NULL,
                CreatedAtTicks INTEGER NOT NULL,
                LastUsedAtTicks INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CopilotPermissionGrants_Tenant_Agent
                ON CopilotPermissionGrants (TenantId, AgentId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _initialized = true;
    }

    private static async Task<CopilotPermissionGrant?> ReadPersistentGrantAsync(
        SqliteConnection connection,
        string tenantId,
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TenantId, AgentId, AgentName, CreatedAtTicks, LastUsedAtTicks
            FROM CopilotPermissionGrants
            WHERE TenantId = $tenantId AND AgentId = $agentId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$agentId", agentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPersistentGrant(reader) : null;
    }

    private static CopilotPermissionGrant ReadPersistentGrant(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            CopilotPermissionGrantScope.FutureAgentRuns,
            ExecutionId: null,
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero));

    private void SweepExpiredWorkflowGrants(DateTimeOffset now)
    {
        foreach (var entry in _workflowGrants)
        {
            if (entry.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
                _workflowGrants.TryRemove(entry.Key, out _);
        }
    }

    private static string BuildWorkflowKey(string tenantId, string executionId)
        => tenantId + "\u001f" + executionId;

    private static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
    }
}
