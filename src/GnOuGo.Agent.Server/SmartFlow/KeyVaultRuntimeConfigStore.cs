using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.SmartFlow;

public interface IKeyVaultRuntimeConfigStore
{
    Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct);
    Task<string?> GetSecretValueAsync(string key, CancellationToken ct);
    Task SaveSecretValueAsync(string key, string value, CancellationToken ct);
    Task<bool> DeleteSecretAsync(string key, CancellationToken ct);
    Task<LLMOptions> BuildEffectiveOptionsAsync(LLMOptions baseOptions, bool includeKeyVaultMcp, CancellationToken ct);
}

public sealed record KeyVaultSecretSummary(string Key, string CreatedAt, int LatestVersion);

public sealed class KeyVaultRuntimeConfigStore : IKeyVaultRuntimeConfigStore
{
    private const string DefaultAuthor = "GnOuGo.Agent.Server";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KeyVaultRuntimeConfigStore> _logger;

    public KeyVaultRuntimeConfigStore(
        IServiceScopeFactory scopeFactory,
        ILogger<KeyVaultRuntimeConfigStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        var secrets = await service.ListSecretsAsync(null, ct);

        return secrets
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s => new KeyVaultSecretSummary(
                s.Key,
                s.CreatedAt.ToString("O"),
                s.LatestVersion))
            .ToList();
    }

    public async Task<string?> GetSecretValueAsync(string key, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        var secret = await service.GetSecretAsync(key, null, DefaultAuthor, ct);
        return secret?.Value;
    }

    public async Task SaveSecretValueAsync(string key, string value, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        await service.SetSecretAsync(key, value, null, DefaultAuthor, ct);
    }

    public async Task<bool> DeleteSecretValueAsync(string key, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        return await service.DeleteSecretAsync(key, null, DefaultAuthor, ct);
    }

    public Task<bool> DeleteSecretAsync(string key, CancellationToken ct)
        => DeleteSecretValueAsync(key, ct);

    public async Task<LLMOptions> BuildEffectiveOptionsAsync(LLMOptions baseOptions, bool includeKeyVaultMcp, CancellationToken ct)
    {
        var effective = CloneOptions(baseOptions);
        var summaries = await ListSecretsAsync(ct);

        foreach (var secret in summaries.Where(s => s.Key.StartsWith("gnougo_llm_", StringComparison.OrdinalIgnoreCase)))
        {
            var config = await LoadSecretJsonAsync(secret.Key, ct);
            if (config is null)
                continue;

            var provider = config["provider"]?.GetValue<string>() ?? secret.Key["gnougo_llm_".Length..];
            var url = config["url"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(url))
                continue;

            var authType = config["auth_type"]?.GetValue<string>() ?? "none";
            var model = config["model"]?.GetValue<string>() ?? string.Empty;

            effective.Models[provider] = new ModelProviderOptions
            {
                Url = url,
                Type = provider,
                ApiKey = string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase)
                    ? config["api_key"]?.GetValue<string>()
                    : null,
                Issuer = config["oidc_issuer"]?.GetValue<string>(),
                ClientId = config["oidc_client_id"]?.GetValue<string>(),
                Scopes = config["oidc_scopes"]?.GetValue<string>(),
                ClientSecret = config["oidc_client_secret"]?.GetValue<string>()
            };

            if (string.Equals(effective.DefaultProvider, provider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(model))
            {
                effective.DefaultModel = model;
            }
        }

        if (!includeKeyVaultMcp)
        {
            var keyVaultServerKey = effective.McpServers.Keys
                .FirstOrDefault(name => string.Equals(name, "GnOuGo.KeyVault.Mcp", StringComparison.OrdinalIgnoreCase));
            if (keyVaultServerKey is not null)
                effective.McpServers.Remove(keyVaultServerKey);
        }

        foreach (var secret in summaries.Where(s => s.Key.StartsWith("gnougo_mcp_", StringComparison.OrdinalIgnoreCase)))
        {
            var config = await LoadSecretJsonAsync(secret.Key, ct);
            if (config is null)
                continue;

            var name = config["name"]?.GetValue<string>() ?? secret.Key["gnougo_mcp_".Length..];
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!includeKeyVaultMcp && string.Equals(name, "GnOuGo.KeyVault.Mcp", StringComparison.OrdinalIgnoreCase))
                continue;

            var transport = config["transport"]?.GetValue<string>() ?? "http";
            var authType = config["auth_type"]?.GetValue<string>() ?? "none";

            effective.McpServers[name] = new McpServerOptions
            {
                Type = transport,
                Description = config["description"]?.GetValue<string>(),
                Url = config["url"]?.GetValue<string>() ?? string.Empty,
                Command = config["command"]?.GetValue<string>(),
                Args = ParseArgs(config["args"]),
                ApiKey = string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase)
                    ? config["api_key"]?.GetValue<string>()
                    : null,
                Issuer = config["oidc_issuer"]?.GetValue<string>(),
                ClientId = config["oidc_client_id"]?.GetValue<string>(),
                Scopes = config["oidc_scopes"]?.GetValue<string>(),
                ClientSecret = config["oidc_client_secret"]?.GetValue<string>()
            };
        }

        if (!effective.Models.ContainsKey(effective.DefaultProvider)
            && effective.Models.Count > 0)
        {
            var fallback = effective.Models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First();
            effective.DefaultProvider = fallback;
        }

        return effective;
    }

    private async Task<JsonObject?> LoadSecretJsonAsync(string key, CancellationToken ct)
    {
        try
        {
            var raw = await GetSecretValueAsync(key, ct);
            return string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse KeyVault configuration secret '{Key}'.", key);
            return null;
        }
    }

    private static List<string>? ParseArgs(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            var values = array
                .Select(item => item?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
            return values.Count == 0 ? null : values;
        }

        if (node is null)
            return null;

        var raw = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var valuesFromString = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return valuesFromString.Count == 0 ? null : valuesFromString;
    }

    private static LLMOptions CloneOptions(LLMOptions source)
    {
        var clone = new LLMOptions
        {
            DefaultProvider = source.DefaultProvider,
            DefaultModel = source.DefaultModel,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase),
            McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var kv in source.Models)
        {
            clone.Models[kv.Key] = new ModelProviderOptions
            {
                Url = kv.Value.Url,
                ApiKey = kv.Value.ApiKey,
                Type = kv.Value.Type,
                Issuer = kv.Value.Issuer,
                ClientId = kv.Value.ClientId,
                ClientSecret = kv.Value.ClientSecret,
                Scopes = kv.Value.Scopes
            };
        }

        foreach (var kv in source.McpServers)
        {
            clone.McpServers[kv.Key] = new McpServerOptions
            {
                Type = kv.Value.Type,
                Description = kv.Value.Description,
                Url = kv.Value.Url,
                ApiKey = kv.Value.ApiKey,
                Issuer = kv.Value.Issuer,
                ClientId = kv.Value.ClientId,
                ClientSecret = kv.Value.ClientSecret,
                Scopes = kv.Value.Scopes,
                Command = kv.Value.Command,
                Args = kv.Value.Args is null ? null : [.. kv.Value.Args]
            };
        }

        return clone;
    }
}


