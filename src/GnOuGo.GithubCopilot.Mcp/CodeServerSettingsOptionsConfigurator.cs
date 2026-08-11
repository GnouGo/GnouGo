using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CodeServerSettingsOptionsConfigurator(IConfiguration configuration) : IConfigureOptions<CodeServerSettings>
{
    public void Configure(CodeServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var section = configuration.GetSection(CodeServerSettings.SectionName);
        settings.DefaultWorkingDirectory = ReadString(section, nameof(CodeServerSettings.DefaultWorkingDirectory), settings.DefaultWorkingDirectory);
        settings.MaxFileSizeBytes = ReadInt64(section, nameof(CodeServerSettings.MaxFileSizeBytes), settings.MaxFileSizeBytes);
        settings.MaxSearchResults = ReadInt32(section, nameof(CodeServerSettings.MaxSearchResults), settings.MaxSearchResults);
        settings.MaxPromptCharacters = ReadInt32(section, nameof(CodeServerSettings.MaxPromptCharacters), settings.MaxPromptCharacters);
        settings.AllowWrites = ReadBoolean(section, nameof(CodeServerSettings.AllowWrites), settings.AllowWrites);
        settings.AllowedWorkingRoots = ReadStringList(section, nameof(CodeServerSettings.AllowedWorkingRoots), settings.AllowedWorkingRoots);
        settings.AllowedExtensions = ReadStringList(section, nameof(CodeServerSettings.AllowedExtensions), settings.AllowedExtensions);

        ConfigureCopilot(section.GetSection(nameof(CodeServerSettings.Copilot)), settings.Copilot);
    }

    private static void ConfigureCopilot(IConfigurationSection section, CodeCopilotSettings settings)
    {
        settings.Provider = ReadString(section, nameof(CodeCopilotSettings.Provider), settings.Provider);
        settings.Model = ReadString(section, nameof(CodeCopilotSettings.Model), settings.Model);
        settings.Mode = ReadString(section, nameof(CodeCopilotSettings.Mode), settings.Mode);
        settings.ReasoningEffort = ReadNullableString(section, nameof(CodeCopilotSettings.ReasoningEffort), settings.ReasoningEffort);
        settings.Endpoint = ReadString(section, nameof(CodeCopilotSettings.Endpoint), settings.Endpoint);
        settings.ApiKey = ReadNullableString(section, nameof(CodeCopilotSettings.ApiKey), settings.ApiKey);
        settings.UseLoggedInUser = ReadBoolean(section, nameof(CodeCopilotSettings.UseLoggedInUser), settings.UseLoggedInUser);
        settings.ForwardTraceContext = ReadBoolean(section, nameof(CodeCopilotSettings.ForwardTraceContext), settings.ForwardTraceContext);
        settings.LogLevel = ReadString(section, nameof(CodeCopilotSettings.LogLevel), settings.LogLevel);
        settings.RequestTimeoutSeconds = ReadInt32(section, nameof(CodeCopilotSettings.RequestTimeoutSeconds), settings.RequestTimeoutSeconds);
        settings.ManagedSessionTtlSeconds = ReadInt32(section, nameof(CodeCopilotSettings.ManagedSessionTtlSeconds), settings.ManagedSessionTtlSeconds);
        settings.EnableApproveAll = ReadBoolean(section, nameof(CodeCopilotSettings.EnableApproveAll), settings.EnableApproveAll);
        settings.WorkflowGrantTtlSeconds = ReadInt32(section, nameof(CodeCopilotSettings.WorkflowGrantTtlSeconds), settings.WorkflowGrantTtlSeconds);
        settings.TokenEnvironmentVariables = ReadStringList(section, nameof(CodeCopilotSettings.TokenEnvironmentVariables), settings.TokenEnvironmentVariables);
        settings.Providers = ReadProviders(
            section.GetSection(nameof(CodeCopilotSettings.Providers)),
            settings.Providers);

        ConfigureTelemetry(section.GetSection(nameof(CodeCopilotSettings.Telemetry)), settings.Telemetry);
    }

    private static void ConfigureTelemetry(IConfigurationSection section, CodeCopilotTelemetrySettings settings)
    {
        settings.Enabled = ReadBoolean(section, nameof(CodeCopilotTelemetrySettings.Enabled), settings.Enabled);
        settings.ExporterType = ReadString(section, nameof(CodeCopilotTelemetrySettings.ExporterType), settings.ExporterType);
        settings.OtlpEndpoint = ReadNullableString(section, nameof(CodeCopilotTelemetrySettings.OtlpEndpoint), settings.OtlpEndpoint);
        settings.FilePath = ReadNullableString(section, nameof(CodeCopilotTelemetrySettings.FilePath), settings.FilePath);
        settings.SourceName = ReadString(section, nameof(CodeCopilotTelemetrySettings.SourceName), settings.SourceName);
        settings.CaptureContent = ReadBoolean(section, nameof(CodeCopilotTelemetrySettings.CaptureContent), settings.CaptureContent);
    }

    private static string ReadString(IConfiguration section, string key, string currentValue)
        => section[key] ?? currentValue;

    private static string? ReadNullableString(IConfiguration section, string key, string? currentValue)
        => section[key] ?? currentValue;

    private static bool ReadBoolean(IConfiguration section, string key, bool currentValue)
        => bool.TryParse(section[key], out var value) ? value : currentValue;

    private static int ReadInt32(IConfiguration section, string key, int currentValue)
        => int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : currentValue;

    private static long ReadInt64(IConfiguration section, string key, long currentValue)
        => long.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : currentValue;

    private static List<string> ReadStringList(IConfiguration section, string key, List<string> currentValue)
    {
        var children = section.GetSection(key).GetChildren().ToArray();
        if (children.Length == 0)
        {
            return currentValue;
        }

        return children
            .Select(child => child.Value)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
    }

    private static Dictionary<string, CodeCopilotProviderSettings> ReadProviders(
        IConfigurationSection section,
        Dictionary<string, CodeCopilotProviderSettings> currentValue)
    {
        var providers = new Dictionary<string, CodeCopilotProviderSettings>(
            currentValue,
            StringComparer.OrdinalIgnoreCase);
        foreach (var providerSection in section.GetChildren())
        {
            var provider = new CodeCopilotProviderSettings();
            if (!string.IsNullOrWhiteSpace(providerSection.Value))
                ApplyRawProviderJson(provider, providerSection.Value, providerSection.Key);

            ApplyProviderSection(provider, providerSection);
            providers[providerSection.Key] = provider;
        }

        return providers;
    }

    private static void ApplyRawProviderJson(
        CodeCopilotProviderSettings provider,
        string rawJson,
        string providerName)
    {
        JsonObject config;
        try
        {
            config = JsonNode.Parse(rawJson) as JsonObject
                ?? throw new InvalidDataException(
                    $"Copilot provider '{providerName}' configuration must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Copilot provider '{providerName}' configuration contains invalid JSON.",
                exception);
        }

        provider.Provider = ReadJsonString(config, "provider");
        provider.Type = ReadJsonString(config, "type");
        provider.Url = ReadJsonString(config, "url");
        provider.Model = ReadJsonString(config, "model");
        provider.WireApi = ReadJsonString(config, "wireApi", "wire_api");
        provider.WireModel = ReadJsonString(config, "wireModel", "wire_model");
        provider.AuthType = ReadJsonString(config, "authType", "auth_type");
        provider.ApiKey = ReadJsonString(config, "apiKey", "api_key");
        provider.BearerToken = ReadJsonString(config, "bearerToken", "bearer_token");
        provider.ApiVersion = ReadJsonString(config, "apiVersion", "api_version");
        provider.OidcIssuer = ReadJsonString(config, "oidcIssuer", "oidc_issuer");
        provider.OidcClientId = ReadJsonString(config, "oidcClientId", "oidc_client_id");
        provider.OidcScopes = ReadJsonString(config, "oidcScopes", "oidc_scopes");
        provider.OidcClientSecret = ReadJsonString(config, "oidcClientSecret", "oidc_client_secret");
        provider.OidcPrivateKeyPem = ReadJsonString(config, "oidcPrivateKeyPem", "oidc_private_key_pem");
    }

    private static void ApplyProviderSection(
        CodeCopilotProviderSettings provider,
        IConfiguration providerSection)
    {
        provider.Provider = ReadNullableString(providerSection, "provider", provider.Provider);
        provider.Type = ReadNullableString(providerSection, "type", provider.Type);
        provider.Url = ReadNullableString(providerSection, "url", provider.Url);
        provider.Model = ReadNullableString(providerSection, "model", provider.Model);
        provider.WireApi = ReadNullableString(providerSection, "wireApi", "wire_api", provider.WireApi);
        provider.WireModel = ReadNullableString(providerSection, "wireModel", "wire_model", provider.WireModel);
        provider.AuthType = ReadNullableString(providerSection, "authType", "auth_type", provider.AuthType);
        provider.ApiKey = ReadNullableString(providerSection, "apiKey", "api_key", provider.ApiKey);
        provider.BearerToken = ReadNullableString(providerSection, "bearerToken", "bearer_token", provider.BearerToken);
        provider.ApiVersion = ReadNullableString(providerSection, "apiVersion", "api_version", provider.ApiVersion);
        provider.OidcIssuer = ReadNullableString(providerSection, "oidcIssuer", "oidc_issuer", provider.OidcIssuer);
        provider.OidcClientId = ReadNullableString(providerSection, "oidcClientId", "oidc_client_id", provider.OidcClientId);
        provider.OidcScopes = ReadNullableString(providerSection, "oidcScopes", "oidc_scopes", provider.OidcScopes);
        provider.OidcClientSecret = ReadNullableString(providerSection, "oidcClientSecret", "oidc_client_secret", provider.OidcClientSecret);
        provider.OidcPrivateKeyPem = ReadNullableString(providerSection, "oidcPrivateKeyPem", "oidc_private_key_pem", provider.OidcPrivateKeyPem);
    }

    private static string? ReadNullableString(
        IConfiguration section,
        string key,
        string legacyKey,
        string? currentValue)
        => section[key] ?? section[legacyKey] ?? currentValue;

    private static string? ReadJsonString(
        JsonObject config,
        string key,
        string? legacyKey = null)
        => config[key]?.GetValue<string>()
           ?? (legacyKey is null ? null : config[legacyKey]?.GetValue<string>());
}
