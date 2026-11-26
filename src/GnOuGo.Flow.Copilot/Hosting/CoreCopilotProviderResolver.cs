using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CoreCopilotProviderResolver : ICopilotProviderResolver
{
    private readonly ICopilotProviderConfigResolver _inner;

    public CoreCopilotProviderResolver(ICopilotProviderConfigResolver inner)
    {
        _inner = inner;
    }

    public async Task<CopilotProviderResolution?> ResolveAsync(
        string? providerName,
        string fallbackModel,
        string? fallbackBearerToken,
        CancellationToken cancellationToken)
    {
        var result = await _inner.ResolveAsync(providerName, fallbackModel, fallbackBearerToken, cancellationToken);
        return result is null ? null : new CopilotProviderResolution(result.ProviderName, result.Model, result.Provider);
    }
}
