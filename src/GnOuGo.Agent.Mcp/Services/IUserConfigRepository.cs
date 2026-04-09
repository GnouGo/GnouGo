namespace GnOuGo.Agent.Mcp.Services;

public sealed record UserConfigSnapshot(
    string? DefaultLlmProvider,
    string? DefaultLlmModel,
    string? DefaultAgent,
    DateTimeOffset? UpdatedAt);

public sealed record UserConfigUpdate(
    string? DefaultLlmProvider,
    string? DefaultLlmModel,
    string? DefaultAgent,
    bool ClearDefaultLlm = false,
    bool ClearDefaultAgent = false);

public interface IUserConfigRepository
{
    Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default);

    Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default);
}

