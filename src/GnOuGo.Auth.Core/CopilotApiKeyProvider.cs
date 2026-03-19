namespace GnOuGo.Auth.Core;

/// <summary>
/// API key provider for GitHub Copilot / GitHub Models.
/// Resolves the token with the following priority:
///   1. Explicit API key (from configuration)
///   2. GITHUB_TOKEN environment variable
///   3. COPILOT_API_KEY environment variable
///
/// For automated CI/CD pipelines, set GITHUB_TOKEN.
/// For local development, use a GitHub PAT with "copilot" scope
/// or the token obtained via the Copilot OAuth device flow.
/// </summary>
public sealed class CopilotApiKeyProvider : IApiKeyProvider
{
    private readonly string? _configuredKey;

    /// <summary>
    /// Creates a provider with an optional pre-configured key.
    /// If null, falls back to environment variables.
    /// </summary>
    public CopilotApiKeyProvider(string? configuredKey = null)
    {
        _configuredKey = configuredKey;
    }

    public ValueTask<string> GetApiKeyAsync(CancellationToken ct = default)
    {
        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "No GitHub/Copilot token available. " +
                "Set the 'ApiKey' in the Copilot provider configuration, " +
                "or define the GITHUB_TOKEN or COPILOT_API_KEY environment variable.");

        return ValueTask.FromResult(token);
    }

    private string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(_configuredKey))
            return _configuredKey;

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(githubToken))
            return githubToken;

        var copilotKey = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        if (!string.IsNullOrWhiteSpace(copilotKey))
            return copilotKey;

        return null;
    }
}

