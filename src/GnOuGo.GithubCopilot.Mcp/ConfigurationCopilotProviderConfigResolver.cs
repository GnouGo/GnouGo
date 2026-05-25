using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using GnOuGo.Auth.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IKeyVaultCopilotProviderConfigResolver _keyVaultProviderConfigResolver;
	private readonly ILogger<ConfigurationCopilotProviderConfigResolver> _logger;

	public ConfigurationCopilotProviderConfigResolver(
		IConfiguration configuration,
		IHttpClientFactory httpClientFactory,
		IKeyVaultCopilotProviderConfigResolver keyVaultProviderConfigResolver,
		ILogger<ConfigurationCopilotProviderConfigResolver> logger)
	{
		_configuration = configuration;
		_httpClientFactory = httpClientFactory;
		_keyVaultProviderConfigResolver = keyVaultProviderConfigResolver;
		_logger = logger;
	}

	public async Task<CopilotProviderOverride?> ResolveAsync(
		string? providerName,
		string fallbackModel,
		string? fallbackBearerToken,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(providerName))
			return null;

		var normalizedProviderName = providerName.Trim();
		var config = await LoadProviderConfigAsync(normalizedProviderName, ct);
		if (config is null)
			throw new McpException($"Copilot provider '{normalizedProviderName}' was not found in configuration or KeyVault. Expected configuration section '{ProvidersSectionPath}:{normalizedProviderName}' or KeyVault secret 'LLM--Models--{normalizedProviderName}'.");

		var model = ReadConfigString(config, "model") ?? fallbackModel;
		if (string.IsNullOrWhiteSpace(model))
			throw new McpException($"Copilot provider '{normalizedProviderName}' does not define a model and no fallback model is configured.");

		var url = ReadConfigString(config, "url");
		if (string.IsNullOrWhiteSpace(url))
			throw new McpException($"Copilot provider '{normalizedProviderName}' exists in configuration but does not define a url.");

		var providerType = NormalizeSdkProviderType(
			ReadConfigString(config, "type") ?? ReadConfigString(config, "provider"),
			url);
		var wireModel = ReadConfigString(config, "wireModel", "wire_model") ?? model;
		var authType = ReadConfigString(config, "authType", "auth_type") ?? "none";
		var apiKey = ReadConfigString(config, "apiKey", "api_key");
		var bearerToken = await ResolveBearerTokenAsync(config, authType, apiKey, fallbackBearerToken, ct);
		var wireApi = ReadConfigString(config, "wireApi", "wire_api") ?? GetDefaultWireApi(providerType);

		var provider = new ProviderConfig
		{
			Type = providerType,
			WireApi = wireApi,
			BaseUrl = url,
			ModelId = model,
			WireModel = wireModel,
			ApiKey = ShouldUseApiKey(providerType, url, authType) ? apiKey : null,
			BearerToken = bearerToken,
			Headers = BuildProviderHeaders(providerType)
		};

		_logger.LogInformation(
			"Resolved Copilot custom provider '{ProviderName}' from configuration using SDK provider type '{ProviderType}' and model '{Model}'.",
			normalizedProviderName,
			providerType,
			model);

		return new CopilotProviderOverride(normalizedProviderName, model, provider);
	}

	private async Task<JsonObject?> LoadProviderConfigAsync(string providerName, CancellationToken ct)
	{
		var candidateKeys = GetCandidateProviderKeys(providerName).ToArray();
		var configurationConfig = LoadProviderConfigFromConfiguration(candidateKeys, providerName);
		var keyVaultConfig = await _keyVaultProviderConfigResolver.ResolveAsync(candidateKeys, providerName, ct);

		return configurationConfig is null
			? keyVaultConfig
			: MergeProviderConfig(configurationConfig, keyVaultConfig);
	}

	private JsonObject? LoadProviderConfigFromConfiguration(IEnumerable<string> candidateKeys, string providerName)
	{
		foreach (var key in candidateKeys)
		{
			var section = _configuration.GetSection($"{ProvidersSectionPath}:{key}");
			if (!section.Exists())
				continue;

			if (!string.IsNullOrWhiteSpace(section.Value))
				return ParseConfig(section.Value, providerName);

			return BuildConfigObject(section);
		}

		return null;
	}

	private static JsonObject MergeProviderConfig(JsonObject configurationConfig, JsonObject? keyVaultConfig)
	{
		if (keyVaultConfig is null)
			return configurationConfig;

		var merged = CloneConfigObject(keyVaultConfig);
		foreach (var item in configurationConfig)
		{
			if (item.Value is null)
				continue;

			if (item.Value is JsonValue value
				&& value.TryGetValue<string>(out var stringValue)
				&& string.IsNullOrWhiteSpace(stringValue))
			{
				continue;
			}

			merged[item.Key] = item.Value.DeepClone();
		}

		return merged;
	}

	private static JsonObject CloneConfigObject(JsonObject source)
	{
		var clone = new JsonObject();
		foreach (var item in source)
		{
			if (item.Value is not null)
				clone[item.Key] = item.Value.DeepClone();
		}

		return clone;
	}

	private static JsonObject BuildConfigObject(IConfigurationSection section)
	{
		var config = new JsonObject();
		foreach (var child in section.GetChildren())
		{
			if (child.Value is not null)
				config[child.Key] = child.Value;
		}

		return config;
	}

	private async Task<string?> ResolveBearerTokenAsync(
		JsonObject config,
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
					ReadRequiredConfigString(config, "oidcIssuer", "oidc_issuer"),
					ReadRequiredConfigString(config, "oidcClientId", "oidc_client_id"),
					ReadRequiredConfigString(config, "oidcScopes", "oidc_scopes"),
					ReadConfigString(config, "oidcClientSecret", "oidc_client_secret"),
					ReadConfigString(config, "oidcPrivateKeyPem", "oidc_private_key_pem")));

			return await tokenProvider.GetApiKeyAsync(ct);
		}

		if (string.Equals(authType, "bearer", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(authType, "bearer_token", StringComparison.OrdinalIgnoreCase))
		{
			return ReadConfigString(config, "bearerToken", "bearer_token") ?? apiKey;
		}

		if (string.Equals(authType, "copilot_env", StringComparison.OrdinalIgnoreCase))
			return fallbackBearerToken;

		return ShouldTreatApiKeyAsBearer(config, authType) ? apiKey : null;
	}

	private static JsonObject ParseConfig(string rawConfig, string providerName)
	{
		try
		{
			return JsonNode.Parse(rawConfig) as JsonObject
				   ?? throw new McpException($"Copilot provider '{providerName}' configuration must contain a JSON object.");
		}
		catch (JsonException ex)
		{
			throw new McpException($"Copilot provider '{providerName}' configuration is not valid JSON.", ex);
		}
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

		foreach (var candidate in candidates)
			yield return candidate;
	}

	private static bool HasOidcConfiguration(JsonObject config)
		=> !string.IsNullOrWhiteSpace(ReadConfigString(config, "oidcIssuer", "oidc_issuer"))
		   || !string.IsNullOrWhiteSpace(ReadConfigString(config, "oidcClientId", "oidc_client_id"))
		   || !string.IsNullOrWhiteSpace(ReadConfigString(config, "oidcScopes", "oidc_scopes"))
		   || !string.IsNullOrWhiteSpace(ReadConfigString(config, "oidcClientSecret", "oidc_client_secret"))
		   || !string.IsNullOrWhiteSpace(ReadConfigString(config, "oidcPrivateKeyPem", "oidc_private_key_pem"));

	private static string ReadRequiredConfigString(JsonObject config, string propertyName, string legacyPropertyName)
		=> ReadConfigString(config, propertyName, legacyPropertyName)
		   ?? throw new McpException($"OIDC configuration is missing required property '{propertyName}'.");

	private static string? ReadConfigString(JsonObject config, string propertyName, string? legacyPropertyName = null)
		=> TryGetString(config, propertyName)
		   ?? (legacyPropertyName is null ? null : TryGetString(config, legacyPropertyName));

	private static string? TryGetString(JsonObject config, string propertyName)
		=> config[propertyName]?.GetValue<string>();

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

	private static string GetDefaultWireApi(string providerType)
		=> string.Equals(providerType, "anthropic", StringComparison.OrdinalIgnoreCase)
			? "messages"
			: "chat-completions";

	private static IDictionary<string, string>? BuildProviderHeaders(string providerType)
		=> string.Equals(providerType, "anthropic", StringComparison.OrdinalIgnoreCase)
			? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["anthropic-version"] = "2023-06-01"
			}
			: null;

	private static bool ShouldUseApiKey(string providerType, string url, string authType)
		=> !ShouldTreatApiKeyAsBearer(providerType, url, authType);

	private static bool ShouldTreatApiKeyAsBearer(JsonObject config, string authType)
	{
		var url = ReadConfigString(config, "url") ?? string.Empty;
		var type = NormalizeSdkProviderType(ReadConfigString(config, "type"), url);
		return ShouldTreatApiKeyAsBearer(type, url, authType);
	}

	private static bool ShouldTreatApiKeyAsBearer(string providerType, string url, string authType)
		=> string.Equals(authType, "bearer", StringComparison.OrdinalIgnoreCase)
		   || string.Equals(authType, "bearer_token", StringComparison.OrdinalIgnoreCase)
		   || url.Contains("models.github.ai", StringComparison.OrdinalIgnoreCase)
		   || string.Equals(providerType, "copilot", StringComparison.OrdinalIgnoreCase);
}


