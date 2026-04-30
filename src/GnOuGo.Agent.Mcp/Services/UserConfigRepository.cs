using Microsoft.EntityFrameworkCore;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class UserConfigRepository : IUserConfigRepository
{
    private readonly AgentDbContext _db;

    public UserConfigRepository(AgentDbContext db)
    {
        _db = db;
    }

    public async Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var entity = await _db.UserConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.TenantScopeKey == BuildTenantScopeKey(tenantId), ct);

        return entity is null
            ? new UserConfigSnapshot(null, null, null, null)
            : ToSnapshot(entity);
    }

    public async Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var tenantScopeKey = BuildTenantScopeKey(tenantId);
        var entity = await _db.UserConfigs
            .FirstOrDefaultAsync(config => config.TenantScopeKey == tenantScopeKey, ct);

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

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return ToSnapshot(entity);
    }

    private static string BuildTenantScopeKey(Guid? tenantId)
        => tenantId?.ToString("D") ?? "global";

    private static UserConfigSnapshot ToSnapshot(UserConfigRecord entity)
        => new(
            DefaultLlmProvider: string.IsNullOrWhiteSpace(entity.DefaultLlmProvider) ? null : entity.DefaultLlmProvider,
            DefaultLlmModel: string.IsNullOrWhiteSpace(entity.DefaultLlmModel) ? null : entity.DefaultLlmModel,
            DefaultAgent: string.IsNullOrWhiteSpace(entity.DefaultAgent) ? null : entity.DefaultAgent,
            UpdatedAt: entity.UpdatedAtTicks == 0 ? null : entity.UpdatedAt,
            DefaultEmbeddingConfig: string.IsNullOrWhiteSpace(entity.DefaultEmbeddingConfig) ? null : entity.DefaultEmbeddingConfig);
}

