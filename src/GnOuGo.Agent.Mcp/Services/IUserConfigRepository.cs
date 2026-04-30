namespace GnOuGo.Agent.Mcp.Services;

public sealed record UserConfigSnapshot(
    string? DefaultLlmProvider,
    string? DefaultLlmModel,
    string? DefaultAgent,
    DateTimeOffset? UpdatedAt,
    string? DefaultEmbeddingConfig = null);

public sealed record UserConfigUpdate(
    string? DefaultLlmProvider,
    string? DefaultLlmModel,
    string? DefaultAgent,
    bool ClearDefaultLlm = false,
    bool ClearDefaultAgent = false,
    string? DefaultEmbeddingConfig = null,
    bool ClearDefaultEmbedding = false);

public interface IUserConfigRepository
{
    Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default);

    Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default);
}

