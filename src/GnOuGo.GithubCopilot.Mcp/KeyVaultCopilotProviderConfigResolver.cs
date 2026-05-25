using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Configuration;
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

    private readonly IConfiguration _configuration;
    private readonly ILogger<KeyVaultCopilotProviderConfigResolver> _logger;

    public KeyVaultCopilotProviderConfigResolver(
        IConfiguration configuration,
        ILogger<KeyVaultCopilotProviderConfigResolver> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<JsonObject?> ResolveAsync(
        IReadOnlyList<string> candidateSecretKeys,
        string providerName,
        CancellationToken ct)
    {
        if (candidateSecretKeys.Count == 0)
            return null;

        var databasePath = ResolveDatabasePath();
        var reader = new KeyVaultSecretReader(databasePath);

        KeyVaultSecretLookupResult? secret;
        try
        {
            secret = await reader.GetFirstDefaultTenantSecretValueAsync(candidateSecretKeys, Author, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogDebug(ex, "Could not read Copilot provider '{ProviderName}' from KeyVault database '{KeyVaultDatabasePath}'.", providerName, databasePath);
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

    private string ResolveDatabasePath()
    {
        var configuredPath = _configuration["KeyVault:DatabasePath"]
            ?? KeyVaultDatabasePathResolver.DefaultRelativePath;
        return KeyVaultDatabasePathResolver.Resolve(configuredPath, AppContext.BaseDirectory);
    }
}


