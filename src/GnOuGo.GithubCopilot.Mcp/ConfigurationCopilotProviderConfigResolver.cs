using GitHub.Copilot;
using GnOuGo.Auth.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace GnOuGo.GithubCopilot.Mcp;

internal interface ICopilotProviderConfigResolver
{
    Task<CopilotProviderOverride?> ResolveAsync(
        string? providerName,
        string fallbackModel,
        string? fallbackBearerToken,
        CancellationToken ct);
}

internal sealed record CopilotProviderOverride(
    string ProviderName,
    string Model,
    ProviderConfig Provider);

internal sealed class ConfigurationCopilotProviderConfigResolver : ICopilotProviderConfigResolver
{
    private const string ProvidersSectionPath = $"{CodeServerSettings.SectionName}:Copilot:Providers";
    private const string LlmProviderPrefix = "LLM--Models--";
    private const string LegacyLlmProviderPrefix = "gnougo_llm_";

    private readonly IConfiguration _configuration;
    private readonly CodeCopilotSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConfigurationCopilotProviderConfigResolver> _logger;

    public ConfigurationCopilotProviderConfigResolver(
        IConfiguration configuration,
        IOptions<CodeServerSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<ConfigurationCopilotProviderConfigResolver> logger)
    {
        _configuration = configuration;
        _settings = settings.Value.Copilot;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CopilotProviderOverride?> ResolveAsync(
        string? providerName,
        string fallbackModel,
        string? fallbackBearerToken,
        CancellationToken ct)
    {
        var hostDefaultProvider = _configuration["GNouGo:DefaultLlmProvider"]
            ?? _configuration["LLM:DefaultProvider"];
        var useHostDefault = string.IsNullOrWhiteSpace(providerName)
            || string.Equals(providerName, "Copilot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(hostDefaultProvider)
            && !string.Equals(hostDefaultProvider, "Copilot", StringComparison.OrdinalIgnoreCase);
        if (useHostDefault)
            providerName = hostDefaultProvider;
        if (string.IsNullOrWhiteSpace(providerName)
            || string.Equals(providerName, "Copilot", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedProviderName = providerName.Trim();
        var config = LoadProviderConfig(normalizedProviderName);
        if (config is null)
        {
            throw new McpException(
                $"Copilot provider '{normalizedProviderName}' was not found in typed configuration. " +
                $"Expected '{ProvidersSectionPath}:{normalizedProviderName}'.");
        }

        var model = NullIfWhiteSpace(config.Model) ?? fallbackModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new McpException(
                $"Copilot provider '{normalizedProviderName}' does not define a model and no fallback model is configured.");
        }

        var url = NullIfWhiteSpace(config.Url);
        if (url is null)
        {
            throw new McpException(
                $"Copilot provider '{normalizedProviderName}' exists in configuration but does not define a url.");
        }

        var providerType = NormalizeSdkProviderType(
            NullIfWhiteSpace(config.Type) ?? NullIfWhiteSpace(config.Provider),
            url);
        var apiVersion = NullIfWhiteSpace(config.ApiVersion);
        var deploymentRoute = TryNormalizeVersionedDeploymentRoute(url, apiVersion);
        if (deploymentRoute is not null)
        {
            providerType = "azure";
            url = deploymentRoute.BaseUrl;
        }

        var wireModel = NullIfWhiteSpace(config.WireModel)
            ?? deploymentRoute?.Deployment
            ?? model;
        var authType = NullIfWhiteSpace(config.AuthType) ?? "none";
        var apiKey = NullIfWhiteSpace(config.ApiKey);
        var bearerToken = await ResolveBearerTokenAsync(
            config,
            providerType,
            url,
            authType,
            apiKey,
            fallbackBearerToken,
            ct);
        var wireApi = NullIfWhiteSpace(config.WireApi) ?? GetDefaultWireApi(providerType);

        var provider = new ProviderConfig
        {
            Type = providerType,
            WireApi = wireApi,
            BaseUrl = url,
            ModelId = model,
            WireModel = wireModel,
            ApiKey = ShouldUseApiKey(providerType, url, authType) ? apiKey : null,
            BearerToken = bearerToken,
            Headers = BuildProviderHeaders(providerType),
            Azure = deploymentRoute is null ? null : new AzureOptions { ApiVersion = apiVersion }
        };

        _logger.LogInformation(
            "Resolved Copilot custom provider '{ProviderName}' from typed configuration using SDK provider type '{ProviderType}' and model '{Model}'.",
            normalizedProviderName,
            providerType,
            model);

        return new CopilotProviderOverride(normalizedProviderName, model, provider);
    }

    private CodeCopilotProviderSettings? LoadProviderConfig(string providerName)
    {
        foreach (var candidate in GetCandidateProviderKeys(providerName))
        {
            if (_settings.Providers.TryGetValue(candidate, out var provider))
                return provider;
        }

        return null;
    }

    private async Task<string?> ResolveBearerTokenAsync(
        CodeCopilotProviderSettings config,
        string providerType,
        string url,
        string authType,
        string? apiKey,
        string? fallbackBearerToken,
        CancellationToken ct)
    {
        if (HasOidcConfiguration(config))
        {
            var tokenProvider = new OidcJwtApiKeyProvider(
                _httpClientFactory.CreateClient(nameof(ConfigurationCopilotProviderConfigResolver)),
                new OidcClientCredentialsConfig(
                    ReadRequiredConfigString(config.OidcIssuer, "oidcIssuer"),
                    ReadRequiredConfigString(config.OidcClientId, "oidcClientId"),
                    ReadRequiredConfigString(config.OidcScopes, "oidcScopes"),
                    NullIfWhiteSpace(config.OidcClientSecret),
                    NullIfWhiteSpace(config.OidcPrivateKeyPem)));

            return await tokenProvider.GetApiKeyAsync(ct);
        }

        if (string.Equals(authType, "bearer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authType, "bearer_token", StringComparison.OrdinalIgnoreCase))
        {
            return NullIfWhiteSpace(config.BearerToken) ?? apiKey;
        }

        if (string.Equals(authType, "copilot_env", StringComparison.OrdinalIgnoreCase))
            return fallbackBearerToken;

        return ShouldTreatApiKeyAsBearer(providerType, url, authType) ? apiKey : null;
    }

    private static IEnumerable<string> GetCandidateProviderKeys(string providerName)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            providerName,
            LlmProviderPrefix + providerName,
            LegacyLlmProviderPrefix + providerName
        };

        if (string.Equals(providerName, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("claude");
            candidates.Add(LlmProviderPrefix + "claude");
            candidates.Add(LegacyLlmProviderPrefix + "claude");
        }
        else if (string.Equals(providerName, "claude", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("anthropic");
            candidates.Add(LlmProviderPrefix + "anthropic");
            candidates.Add(LegacyLlmProviderPrefix + "anthropic");
        }

        return candidates;
    }

    private static bool HasOidcConfiguration(CodeCopilotProviderSettings config)
        => !string.IsNullOrWhiteSpace(config.OidcIssuer)
           || !string.IsNullOrWhiteSpace(config.OidcClientId)
           || !string.IsNullOrWhiteSpace(config.OidcScopes)
           || !string.IsNullOrWhiteSpace(config.OidcClientSecret)
           || !string.IsNullOrWhiteSpace(config.OidcPrivateKeyPem);

    private static string ReadRequiredConfigString(string? value, string propertyName)
        => NullIfWhiteSpace(value)
           ?? throw new McpException($"OIDC configuration is missing required property '{propertyName}'.");

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeSdkProviderType(string? configuredType, string url)
    {
        var normalized = string.IsNullOrWhiteSpace(configuredType)
            ? InferProviderType(url)
            : configuredType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "azure" or "azure-openai" => "azure",
            "anthropic" or "claude" => "anthropic",
            "openai" or "copilot" or "github" or "github-models" or "ollama" => "openai",
            _ => normalized
        };
    }

    private static string InferProviderType(string url)
        => url.Contains("azure", StringComparison.OrdinalIgnoreCase) ? "azure"
            : url.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
              || url.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic"
            : "openai";

    private static VersionedDeploymentRoute? TryNormalizeVersionedDeploymentRoute(string url, string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion)
            || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        const string marker = "/deployments/";
        var markerIndex = endpoint.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var deploymentStart = markerIndex + marker.Length;
        var deploymentEnd = endpoint.AbsolutePath.IndexOf('/', deploymentStart);
        var deployment = deploymentEnd < 0
            ? endpoint.AbsolutePath[deploymentStart..]
            : endpoint.AbsolutePath[deploymentStart..deploymentEnd];
        if (string.IsNullOrWhiteSpace(deployment))
            return null;

        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath[..markerIndex].TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return new VersionedDeploymentRoute(builder.Uri.AbsoluteUri, Uri.UnescapeDataString(deployment));
    }

    private static string GetDefaultWireApi(string providerType)
        => string.Equals(providerType, "anthropic", StringComparison.OrdinalIgnoreCase)
            ? "messages"
            : "completions";

    private static IDictionary<string, string>? BuildProviderHeaders(string providerType)
        => string.Equals(providerType, "anthropic", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic-version"] = "2023-06-01"
            }
            : null;

    private static bool ShouldUseApiKey(string providerType, string url, string authType)
        => !ShouldTreatApiKeyAsBearer(providerType, url, authType);

    private static bool ShouldTreatApiKeyAsBearer(string providerType, string url, string authType)
        => string.Equals(authType, "bearer", StringComparison.OrdinalIgnoreCase)
           || string.Equals(authType, "bearer_token", StringComparison.OrdinalIgnoreCase)
           || url.Contains("models.github.ai", StringComparison.OrdinalIgnoreCase)
           || string.Equals(providerType, "copilot", StringComparison.OrdinalIgnoreCase);

    private sealed record VersionedDeploymentRoute(string BaseUrl, string Deployment);
}
