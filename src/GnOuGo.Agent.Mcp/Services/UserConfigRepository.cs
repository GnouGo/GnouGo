using System.Text.Json;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class UserConfigRepository : IUserConfigRepository
{
    private readonly AgentMcpDbContext _db;
    private readonly DiffService _diff;

    private const string DiffEntityType = "AgentConfiguration";
    private const string DiffAuthor = "GnOuGo.Agent.Mcp";

    public UserConfigRepository(AgentMcpDbContext db, DiffService diff)
    {
        _db = db;
        _diff = diff;
    }

    public async Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var tenantScopeKey = BuildTenantScopeKey(tenantId);
        var entity = await AgentMcpQueries.GetUserConfigByScope(_db, tenantScopeKey);
        return entity is not null
            ? ToSnapshot(entity)
            : new UserConfigSnapshot(null, null, null, null);
    }

    public async Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var tenantScopeKey = BuildTenantScopeKey(tenantId);
        var entity = await AgentMcpQueries.GetUserConfigByScope(_db, tenantScopeKey);

        if (entity is null)
        {
            entity = new UserConfigRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TenantScopeKey = tenantScopeKey
            };
            _db.UserConfigs.Add(entity);
        }

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
        await _db.SaveChangesAsync(ct);

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
}
