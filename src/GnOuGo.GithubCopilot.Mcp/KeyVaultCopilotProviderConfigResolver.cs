using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GnOuGo.GithubCopilot.Mcp;

internal interface IKeyVaultCopilotProviderConfigResolver
{
    Task<JsonObject?> ResolveAsync(
        IReadOnlyList<string> candidateSecretKeys,
        string providerName,
        CancellationToken ct);
}

internal sealed class KeyVaultCopilotProviderConfigResolver : IKeyVaultCopilotProviderConfigResolver
{
    private const string Author = "GnOuGo.GithubCopilot.Mcp";

    private readonly IKeyVaultSecretReader _secretReader;
    private readonly ILogger<KeyVaultCopilotProviderConfigResolver> _logger;

    public KeyVaultCopilotProviderConfigResolver(
        IKeyVaultSecretReader secretReader,
        ILogger<KeyVaultCopilotProviderConfigResolver> logger)
    {
        _secretReader = secretReader;
        _logger = logger;
    }

    public async Task<JsonObject?> ResolveAsync(
        IReadOnlyList<string> candidateSecretKeys,
        string providerName,
        CancellationToken ct)
    {
        if (candidateSecretKeys.Count == 0)
            return null;

        KeyVaultSecretLookupResult? secret;
        try
        {
            secret = await _secretReader.GetFirstDefaultTenantSecretValueAsync(candidateSecretKeys, Author, ct);
        }
        catch (KeyVaultAccessException ex)
        {
            _logger.LogDebug(ex, "Could not read Copilot provider '{ProviderName}' through KeyVault.", providerName);
            return null;
        }

        if (secret is null)
            return null;

        try
        {
            var config = JsonNode.Parse(secret.Value) as JsonObject;
            if (config is null)
                throw new McpException($"KeyVault secret '{secret.Key}' for Copilot provider '{providerName}' must contain a JSON object.");

            _logger.LogInformation(
                "Resolved Copilot provider '{ProviderName}' from KeyVault secret '{SecretKey}'.",
                providerName,
                secret.Key);
            return config;
        }
        catch (JsonException ex)
        {
            throw new McpException($"KeyVault secret '{secret.Key}' for Copilot provider '{providerName}' is not valid JSON.", ex);
        }
    }
}


