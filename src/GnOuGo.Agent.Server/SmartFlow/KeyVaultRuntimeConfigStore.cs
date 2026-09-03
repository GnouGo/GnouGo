using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.SmartFlow;

public interface IKeyVaultRuntimeConfigStore
{
    Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct);
    Task<string?> GetSecretValueAsync(string key, CancellationToken ct);
    Task SaveSecretValueAsync(string key, string value, CancellationToken ct);
    Task<bool> DeleteSecretAsync(string key, CancellationToken ct);
    Task<LLMOptions> BuildEffectiveOptionsAsync(LLMOptions baseOptions, CancellationToken ct);
}

public sealed record KeyVaultSecretSummary(string Key, string CreatedAt, int LatestVersion);

public sealed class KeyVaultRuntimeConfigStore : IKeyVaultRuntimeConfigStore
{
    private const string DefaultAuthor = "GnOuGo.Agent.Server";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KeyVaultRuntimeConfigStore> _logger;
    private readonly BundledMcpSettings _bundledMcpSettings;
    private readonly KeyVaultSettings _keyVaultSettings;

    public KeyVaultRuntimeConfigStore(
        IServiceScopeFactory scopeFactory,
        ILogger<KeyVaultRuntimeConfigStore> logger,
        IOptions<BundledMcpSettings>? bundledMcpSettings = null,
        IOptions<KeyVaultSettings>? keyVaultSettings = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _bundledMcpSettings = bundledMcpSettings?.Value ?? new BundledMcpSettings();
        _keyVaultSettings = keyVaultSettings?.Value ?? new KeyVaultSettings();
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

    public async Task<LLMOptions> BuildEffectiveOptionsAsync(LLMOptions baseOptions, CancellationToken ct)
    {
        var effective = CloneOptions(baseOptions);
        var summaries = await ListSecretsAsync(ct);

        foreach (var secret in KeyVaultConfigNaming.SelectPreferredSecrets(summaries, KeyVaultConfigSecretKind.LlmProvider))
        {
            var config = await LoadSecretJsonAsync(secret.Key, ct);
            if (config is null)
                continue;

            var provider = ReadConfigString(config, "provider")
                ?? KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.LlmProvider, secret.Key)
                ?? string.Empty;
            var url = ReadConfigString(config, "url") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(url))
                continue;

            var authType = ReadConfigString(config, "authType", "auth_type") ?? "none";
            var model = ReadConfigString(config, "model") ?? string.Empty;
            var existingProvider = effective.ResolveProvider(provider);

            effective.Models[provider] = new ModelProviderOptions
            {
                Url = url,
                Type = provider,
                ApiKey = string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase)
                    ? ReadConfigString(config, "apiKey", "api_key")
                    : null,
                Issuer = ReadConfigString(config, "oidcIssuer", "oidc_issuer"),
                ClientId = ReadConfigString(config, "oidcClientId", "oidc_client_id"),
                Scopes = ReadConfigString(config, "oidcScopes", "oidc_scopes"),
                ClientSecret = ReadConfigString(config, "oidcClientSecret", "oidc_client_secret"),
                PrivateKeyPem = ReadConfigString(config, "oidcPrivateKeyPem", "oidc_private_key_pem"),
                ApiVersion = ReadConfigString(config, "apiVersion", "api_version"),
                RequestPolicy = existingProvider is null
                    ? new LLMProviderRequestPolicyOptions()
                    : CloneRequestPolicy(existingProvider.RequestPolicy),
                RetryPolicy = existingProvider is null
                    ? new LLMProviderRetryPolicyOptions()
                    : CloneRetryPolicy(existingProvider.RetryPolicy)
            };

            if (string.Equals(effective.DefaultProvider, provider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(model))
            {
                effective.DefaultModel = model;
            }
        }

        foreach (var secret in KeyVaultConfigNaming.SelectPreferredSecrets(summaries, KeyVaultConfigSecretKind.McpServer))
        {
            var config = await LoadSecretJsonAsync(secret.Key, ct);
            if (config is null)
                continue;

            var name = ReadConfigString(config, "name")
                ?? KeyVaultConfigNaming.TryGetLogicalName(KeyVaultConfigSecretKind.McpServer, secret.Key)
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var transport = ReadConfigString(config, "transport") ?? "http";
            var authType = ReadConfigString(config, "authType", "auth_type") ?? "none";
            effective.McpServers.TryGetValue(name, out var existingServer);

            effective.McpServers[name] = new McpServerOptions
            {
                Type = transport,
                Description = ReadConfigString(config, "description"),
                DiscoveryTimeoutSeconds = ReadConfigInt(config, "discoveryTimeoutSeconds", "DiscoveryTimeoutSeconds", "discovery_timeout_seconds")
                                          ?? existingServer?.DiscoveryTimeoutSeconds,
                CallTimeoutSeconds = ReadConfigInt(config, "callTimeoutSeconds", "CallTimeoutSeconds", "call_timeout_seconds")
                                     ?? existingServer?.CallTimeoutSeconds,
                Url = ReadConfigString(config, "url") ?? string.Empty,
                Command = ReadConfigString(config, "command"),
                Args = ParseArgs(config["args"]),
                EnvironmentVariables = ParseEnvironmentVariables(config["environmentVariables"], existingServer?.EnvironmentVariables),
                ApiKey = string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase)
                    ? ReadConfigString(config, "apiKey", "api_key")
                    : null,
                Issuer = ReadConfigString(config, "oidcIssuer", "oidc_issuer"),
                ClientId = ReadConfigString(config, "oidcClientId", "oidc_client_id"),
                Scopes = ReadConfigString(config, "oidcScopes", "oidc_scopes"),
                ClientSecret = ReadConfigString(config, "oidcClientSecret", "oidc_client_secret")
            };
        }

        await ApplyBundledMcpFieldOverridesAsync(effective, ct);
        ApplyDirectKeyVaultLocations(effective);

        if (!effective.Models.ContainsKey(effective.DefaultProvider)
            && effective.Models.Count > 0)
        {
            var fallback = effective.Models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First();
            effective.DefaultProvider = fallback;
        }

        LLMOptionsValidation.ValidateAndThrow(effective);
        return effective;
    }

    private void ApplyDirectKeyVaultLocations(LLMOptions effective)
    {
        var configuredPath = string.IsNullOrWhiteSpace(_keyVaultSettings.DatabasePath)
            ? KeyVaultDatabasePathResolver.DefaultRelativePath
            : _keyVaultSettings.DatabasePath;

        foreach (var serverEntry in _bundledMcpSettings.Servers)
        {
            if (!serverEntry.Value.ReadsKeyVaultDirectly
                || !effective.McpServers.TryGetValue(serverEntry.Key, out var serverOptions))
            {
                continue;
            }

            serverOptions.EnvironmentVariables ??=
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            serverOptions.EnvironmentVariables["KeyVault__DatabasePath"] = configuredPath;
        }
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

    private static string? ReadConfigString(JsonObject config, string propertyName, string? legacyPropertyName = null)
        => TryGetString(config, propertyName)
           ?? (legacyPropertyName is null ? null : TryGetString(config, legacyPropertyName));

    private static int? ReadConfigInt(JsonObject config, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var node = config[propertyName];
            if (node is null)
                continue;

            if (node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var intValue))
                    return intValue;

                if (value.TryGetValue<string>(out var stringValue)
                    && int.TryParse(stringValue, out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }

        return null;
    }

    private static string? TryGetString(JsonObject config, string propertyName)
        => config[propertyName]?.GetValue<string>();

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

    private async Task ApplyBundledMcpFieldOverridesAsync(LLMOptions effective, CancellationToken ct)
    {
        foreach (var serverEntry in _bundledMcpSettings.Servers)
        {
            if (!effective.McpServers.TryGetValue(serverEntry.Key, out var serverOptions))
                continue;

            foreach (var fieldEntry in serverEntry.Value.EditableFields)
            {
                var field = fieldEntry.Value;
                if (string.IsNullOrWhiteSpace(field.Target))
                    continue;

                var secretKey = field.ResolveSecretKey(serverEntry.Key, fieldEntry.Key);
                var value = await GetSecretValueAsync(secretKey, ct);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                ApplyMcpFieldTarget(serverOptions, field.Target, value);
            }
        }
    }

    private static Dictionary<string, string?>? ParseEnvironmentVariables(
        JsonNode? node,
        Dictionary<string, string?>? existingEnvironmentVariables)
    {
        Dictionary<string, string?>? values = existingEnvironmentVariables is null
            ? null
            : new Dictionary<string, string?>(existingEnvironmentVariables, StringComparer.OrdinalIgnoreCase);

        if (node is not JsonObject obj)
            return values;

        values ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in obj)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
                values[kv.Key] = kv.Value?.GetValue<string>();
        }

        return values.Count == 0 ? null : values;
    }

    private static void ApplyMcpFieldTarget(McpServerOptions options, string target, string value)
    {
        var separator = target.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == target.Length - 1)
            return;

        var kind = target[..separator].Trim();
        var name = target[(separator + 1)..].Trim();

        if (string.Equals(kind, "env", StringComparison.OrdinalIgnoreCase))
        {
            options.EnvironmentVariables ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            options.EnvironmentVariables[name] = value;
            return;
        }

        if (!string.Equals(kind, "option", StringComparison.OrdinalIgnoreCase))
            return;

        switch (name.ToLowerInvariant())
        {
            case "apikey":
            case "api_key":
                options.ApiKey = value;
                break;
            case "url":
                options.Url = value;
                break;
            case "command":
                options.Command = value;
                break;
            case "description":
                options.Description = value;
                break;
            case "clientsecret":
            case "client_secret":
                options.ClientSecret = value;
                break;
        }
    }

    private static LLMOptions CloneOptions(LLMOptions source)
    {
        var clone = new LLMOptions
        {
            DefaultProvider = source.DefaultProvider,
            DefaultModel = source.DefaultModel,
            Fallback = source.Fallback is null
                ? null
                : new LLMFallbackOptions { Provider = source.Fallback.Provider, Model = source.Fallback.Model },
            DangerousAcceptAnyServerCertificate = source.DangerousAcceptAnyServerCertificate,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase),
            McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase),
            ModelMetadataFiles = [.. source.ModelMetadataFiles],
            ModelOverrides = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)
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
                PrivateKeyPem = kv.Value.PrivateKeyPem,
                Scopes = kv.Value.Scopes,
                ApiVersion = kv.Value.ApiVersion,
                RequestPolicy = CloneRequestPolicy(kv.Value.RequestPolicy),
                RetryPolicy = CloneRetryPolicy(kv.Value.RetryPolicy)
            };
        }

        foreach (var kv in source.McpServers)
        {
            clone.McpServers[kv.Key] = new McpServerOptions
            {
                Type = kv.Value.Type,
                Description = kv.Value.Description,
                DiscoveryTimeoutSeconds = kv.Value.DiscoveryTimeoutSeconds,
                CallTimeoutSeconds = kv.Value.CallTimeoutSeconds,
                Url = kv.Value.Url,
                ApiKey = kv.Value.ApiKey,
                Issuer = kv.Value.Issuer,
                ClientId = kv.Value.ClientId,
                ClientSecret = kv.Value.ClientSecret,
                Scopes = kv.Value.Scopes,
                Command = kv.Value.Command,
                Args = kv.Value.Args is null ? null : [.. kv.Value.Args],
                EnvironmentVariables = kv.Value.EnvironmentVariables is null
                    ? null
                    : new Dictionary<string, string?>(kv.Value.EnvironmentVariables, StringComparer.OrdinalIgnoreCase)
            };
        }

        foreach (var kv in source.ModelOverrides)
            clone.ModelOverrides[kv.Key] = ModelMetadataCatalog.Clone(kv.Value);

        return clone;
    }

    private static LLMProviderRequestPolicyOptions CloneRequestPolicy(LLMProviderRequestPolicyOptions source)
        => new()
        {
            BackgroundProtocol = source.BackgroundProtocol,
            UnspecifiedOutputTokens = source.UnspecifiedOutputTokens,
            DefaultMaxOutputTokens = source.DefaultMaxOutputTokens,
            MaxOutputTokensCap = source.MaxOutputTokensCap
        };

    private static LLMProviderRetryPolicyOptions CloneRetryPolicy(LLMProviderRetryPolicyOptions source)
        => new()
        {
            MaxAttempts = source.MaxAttempts,
            BaseDelayMilliseconds = source.BaseDelayMilliseconds,
            MaxDelayMilliseconds = source.MaxDelayMilliseconds,
            MaxTotalDelayMilliseconds = source.MaxTotalDelayMilliseconds,
            HonorRetryAfter = source.HonorRetryAfter
        };
}
