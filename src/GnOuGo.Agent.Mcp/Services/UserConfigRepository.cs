using System.Text.Json;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;
using Microsoft.Data.Sqlite;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class UserConfigRepository : IUserConfigRepository
{
    private readonly AgentSqliteStore _store;
    private readonly DiffService _diff;

    private const string DiffEntityType = "AgentConfiguration";
    private const string DiffAuthor = "GnOuGo.Agent.Mcp";

    public UserConfigRepository(AgentSqliteStore store, DiffService diff)
    {
        _store = store;
        _diff = diff;
    }

    public async Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        await using var connection = _store.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "TenantId", "TenantScopeKey", "DefaultLlmProvider", "DefaultLlmModel",
                   "DefaultEmbeddingConfig", "DefaultAgent", "ModelOverridesJson", "UpdatedAtTicks"
            FROM "UserConfigs"
            WHERE "TenantScopeKey" = $tenantScopeKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenantScopeKey", BuildTenantScopeKey(tenantId));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ToSnapshot(ReadRecord(reader))
            : new UserConfigSnapshot(null, null, null, null);
    }

    public async Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var connection = _store.OpenConnection();
        var tenantScopeKey = BuildTenantScopeKey(tenantId);
        var entity = await GetRecordAsync(connection, tenantScopeKey, ct) ?? new UserConfigRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TenantScopeKey = tenantScopeKey
        };

        if (update.ClearDefaultLlm)
        {
            entity.DefaultLlmProvider = null;
            entity.DefaultLlmModel = null;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(update.DefaultLlmProvider))
                entity.DefaultLlmProvider = update.DefaultLlmProvider.Trim();

            if (!string.IsNullOrWhiteSpace(update.DefaultLlmModel))
                entity.DefaultLlmModel = update.DefaultLlmModel.Trim();
        }

        if (update.ClearDefaultAgent)
        {
            entity.DefaultAgent = null;
        }
        else if (!string.IsNullOrWhiteSpace(update.DefaultAgent))
        {
            entity.DefaultAgent = update.DefaultAgent.Trim();
        }

        if (update.ClearDefaultEmbedding)
        {
            entity.DefaultEmbeddingConfig = null;
        }
        else if (!string.IsNullOrWhiteSpace(update.DefaultEmbeddingConfig))
        {
            entity.DefaultEmbeddingConfig = update.DefaultEmbeddingConfig.Trim();
        }

        if (update.ModelOverrides is not null)
        {
            entity.ModelOverridesJson = update.ModelOverrides.Count == 0
                ? null
                : JsonSerializer.Serialize(update.ModelOverrides, AgentMcpJsonContext.Default.IReadOnlyDictionaryStringLLMModelMetadata);
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await UpsertAsync(connection, entity, ct);

        var snapshot = ToSnapshot(entity);
        await SaveConfigurationRevisionAsync(tenantScopeKey, snapshot, ct);
        return snapshot;
    }

    private async Task SaveConfigurationRevisionAsync(string tenantScopeKey, UserConfigSnapshot snapshot, CancellationToken ct)
    {
        var currentValue = JsonSerializer.Serialize(snapshot, AgentMcpJsonContext.Default.UserConfigSnapshot);
        await _diff.CreateRevisionAsync(new CreateRevisionRequest(
            DiffEntityType,
            tenantScopeKey,
            currentValue,
            DiffAuthor,
            ForceCreate: true), ct);
    }

    private static async Task<UserConfigRecord?> GetRecordAsync(SqliteConnection connection, string tenantScopeKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "TenantId", "TenantScopeKey", "DefaultLlmProvider", "DefaultLlmModel",
                   "DefaultEmbeddingConfig", "DefaultAgent", "ModelOverridesJson", "UpdatedAtTicks"
            FROM "UserConfigs"
            WHERE "TenantScopeKey" = $tenantScopeKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenantScopeKey", tenantScopeKey);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    private static async Task UpsertAsync(SqliteConnection connection, UserConfigRecord entity, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "UserConfigs" (
                "Id", "TenantId", "TenantScopeKey", "DefaultLlmProvider", "DefaultLlmModel",
                "DefaultEmbeddingConfig", "DefaultAgent", "ModelOverridesJson", "UpdatedAtTicks")
            VALUES (
                $id, $tenantId, $tenantScopeKey, $defaultLlmProvider, $defaultLlmModel,
                $defaultEmbeddingConfig, $defaultAgent, $modelOverridesJson, $updatedAtTicks)
            ON CONFLICT("TenantScopeKey") DO UPDATE SET
                "TenantId" = excluded."TenantId",
                "DefaultLlmProvider" = excluded."DefaultLlmProvider",
                "DefaultLlmModel" = excluded."DefaultLlmModel",
                "DefaultEmbeddingConfig" = excluded."DefaultEmbeddingConfig",
                "DefaultAgent" = excluded."DefaultAgent",
                "ModelOverridesJson" = excluded."ModelOverridesJson",
                "UpdatedAtTicks" = excluded."UpdatedAtTicks";
            """;
        command.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
        command.Parameters.AddWithValue("$tenantId", ToDbValue(entity.TenantId?.ToString("D")));
        command.Parameters.AddWithValue("$tenantScopeKey", entity.TenantScopeKey);
        command.Parameters.AddWithValue("$defaultLlmProvider", ToDbValue(entity.DefaultLlmProvider));
        command.Parameters.AddWithValue("$defaultLlmModel", ToDbValue(entity.DefaultLlmModel));
        command.Parameters.AddWithValue("$defaultEmbeddingConfig", ToDbValue(entity.DefaultEmbeddingConfig));
        command.Parameters.AddWithValue("$defaultAgent", ToDbValue(entity.DefaultAgent));
        command.Parameters.AddWithValue("$modelOverridesJson", ToDbValue(entity.ModelOverridesJson));
        command.Parameters.AddWithValue("$updatedAtTicks", entity.UpdatedAtTicks);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static UserConfigRecord ReadRecord(SqliteDataReader reader)
        => new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            TenantScopeKey = reader.GetString(2),
            DefaultLlmProvider = reader.IsDBNull(3) ? null : reader.GetString(3),
            DefaultLlmModel = reader.IsDBNull(4) ? null : reader.GetString(4),
            DefaultEmbeddingConfig = reader.IsDBNull(5) ? null : reader.GetString(5),
            DefaultAgent = reader.IsDBNull(6) ? null : reader.GetString(6),
            ModelOverridesJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAtTicks = reader.GetInt64(8)
        };

    private static string BuildTenantScopeKey(Guid? tenantId)
        => tenantId?.ToString("D") ?? "global";

    private static UserConfigSnapshot ToSnapshot(UserConfigRecord entity)
        => new(
            DefaultLlmProvider: string.IsNullOrWhiteSpace(entity.DefaultLlmProvider) ? null : entity.DefaultLlmProvider,
            DefaultLlmModel: string.IsNullOrWhiteSpace(entity.DefaultLlmModel) ? null : entity.DefaultLlmModel,
            DefaultAgent: string.IsNullOrWhiteSpace(entity.DefaultAgent) ? null : entity.DefaultAgent,
            UpdatedAt: entity.UpdatedAtTicks == 0 ? null : entity.UpdatedAt,
            DefaultEmbeddingConfig: string.IsNullOrWhiteSpace(entity.DefaultEmbeddingConfig) ? null : entity.DefaultEmbeddingConfig,
            ModelOverrides: DeserializeModelOverrides(entity.ModelOverridesJson));

    private static IReadOnlyDictionary<string, LLMModelMetadata> DeserializeModelOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.Deserialize(json, AgentMcpJsonContext.Default.DictionaryStringLLMModelMetadata)
                   ?? new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static object ToDbValue(string? value) => value is null ? DBNull.Value : value;
}
