using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class KeyVaultCopilotPermissionGrantStore : ICopilotPermissionGrantStore
{
    internal const string CollectionName = "github-copilot.permission-grants";
    private const string AuditAuthor = "GnOuGo.GithubCopilot.Mcp";

    private readonly ConcurrentDictionary<string, CopilotPermissionGrant> _workflowGrants = new(StringComparer.Ordinal);
    private readonly IKeyVaultRecordStore _records;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _workflowGrantTtl;

    public KeyVaultCopilotPermissionGrantStore(
        IKeyVaultRecordStore records,
        IOptions<CodeServerSettings> settings)
        : this(records, settings, TimeProvider.System)
    {
    }

    internal KeyVaultCopilotPermissionGrantStore(
        IKeyVaultRecordStore records,
        IOptions<CodeServerSettings> settings,
        TimeProvider timeProvider)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        var configured = settings?.Value.Copilot
            ?? throw new ArgumentNullException(nameof(settings));
        _workflowGrantTtl = TimeSpan.FromSeconds(Math.Max(60, configured.WorkflowGrantTtlSeconds));
    }

    public async Task<CopilotPermissionGrant?> FindReusableGrantAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken)
    {
        ValidateTenant(context.TenantId);
        var now = _timeProvider.GetUtcNow();
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

        var recordKey = BuildRecordKey(context.AgentId);
        var record = await _records.GetAsync(
            CollectionName,
            context.TenantId,
            recordKey,
            AuditAuthor,
            cancellationToken);
        if (record is null)
            return null;

        var grant = DeserializeAndValidate(record, context.TenantId, context.AgentId);
        var touchedGrant = grant with
        {
            AgentName = context.AgentName ?? grant.AgentName,
            LastUsedAt = now
        };
        await SaveAsync(recordKey, touchedGrant, cancellationToken);
        return touchedGrant;
    }

    public Task<CopilotPermissionGrant> GrantWorkflowRunAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken)
    {
        ValidateTenant(context.TenantId);
        if (string.IsNullOrWhiteSpace(context.ExecutionId))
            throw new InvalidOperationException("A stable execution ID is required for workflow-run approval.");
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
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

        var now = _timeProvider.GetUtcNow();
        var recordKey = BuildRecordKey(context.AgentId);
        var record = await _records.GetAsync(
            CollectionName,
            context.TenantId,
            recordKey,
            AuditAuthor,
            cancellationToken);
        var existing = record is null
            ? null
            : DeserializeAndValidate(record, context.TenantId, context.AgentId);
        var grant = existing is null
            ? new CopilotPermissionGrant(
                Guid.NewGuid().ToString("N"),
                context.TenantId,
                CopilotPermissionGrantScope.FutureAgentRuns,
                ExecutionId: null,
                context.AgentId,
                context.AgentName,
                now,
                now)
            : existing with
            {
                AgentName = context.AgentName ?? existing.AgentName,
                LastUsedAt = now
            };

        await SaveAsync(recordKey, grant, cancellationToken);
        return grant;
    }

    public async Task<IReadOnlyList<CopilotPermissionGrant>> ListFutureAgentGrantsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        ValidateTenant(tenantId);
        var records = await _records.ListAsync(CollectionName, tenantId, AuditAuthor, cancellationToken);
        return records
            .Select(record => DeserializeAndValidate(record, tenantId, expectedAgentId: null))
            .OrderBy(grant => grant.AgentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(grant => grant.AgentId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> RevokeAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken)
    {
        ValidateTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        var records = await _records.ListAsync(CollectionName, tenantId, AuditAuthor, cancellationToken);
        foreach (var record in records)
        {
            var grant = DeserializeAndValidate(record, tenantId, expectedAgentId: null);
            if (string.Equals(grant.Id, grantId, StringComparison.Ordinal))
            {
                return await _records.DeleteAsync(
                    CollectionName,
                    tenantId,
                    record.Key,
                    AuditAuthor,
                    cancellationToken);
            }
        }

        return false;
    }

    public async Task<int> RevokeAgentAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken)
    {
        ValidateTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return await _records.DeleteAsync(
            CollectionName,
            tenantId,
            BuildRecordKey(agentId),
            AuditAuthor,
            cancellationToken)
            ? 1
            : 0;
    }

    private async Task SaveAsync(
        string recordKey,
        CopilotPermissionGrant grant,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            grant,
            CopilotCoreJsonContext.Default.CopilotPermissionGrant);
        await _records.UpsertAsync(
            CollectionName,
            grant.TenantId,
            recordKey,
            payload,
            AuditAuthor,
            cancellationToken);
    }

    private static CopilotPermissionGrant DeserializeAndValidate(
        KeyVaultRecordValue record,
        string expectedTenantId,
        string? expectedAgentId)
    {
        CopilotPermissionGrant? grant;
        try
        {
            grant = JsonSerializer.Deserialize(
                record.Value,
                CopilotCoreJsonContext.Default.CopilotPermissionGrant);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The stored Copilot permission grant is not valid JSON.", exception);
        }

        if (grant is null
            || grant.Scope != CopilotPermissionGrantScope.FutureAgentRuns
            || grant.AllowSandboxBypass
            || grant.ExecutionId is not null
            || string.IsNullOrWhiteSpace(grant.Id)
            || string.IsNullOrWhiteSpace(grant.AgentId)
            || !string.Equals(record.Collection, CollectionName, StringComparison.Ordinal)
            || !string.Equals(record.TenantId, expectedTenantId, StringComparison.Ordinal)
            || !string.Equals(grant.TenantId, expectedTenantId, StringComparison.Ordinal)
            || (expectedAgentId is not null
                && !string.Equals(grant.AgentId, expectedAgentId, StringComparison.Ordinal))
            || !string.Equals(record.Key, BuildRecordKey(grant.AgentId), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored Copilot permission grant identity or scope is invalid.");
        }

        return grant;
    }

    private void SweepExpiredWorkflowGrants(DateTimeOffset now)
    {
        foreach (var entry in _workflowGrants)
        {
            if (entry.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
                _workflowGrants.TryRemove(entry.Key, out _);
        }
    }

    internal static string BuildRecordKey(string agentId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

    private static string BuildWorkflowKey(string tenantId, string executionId)
        => tenantId + "\u001f" + executionId;

    private static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
    }
}
