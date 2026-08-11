using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed record CopilotKeyVaultConfigurationOverlayResult(
    IReadOnlyDictionary<string, string?> Values,
    string? Warning = null);

internal static class CopilotKeyVaultConfigurationOverlay
{
    internal const string CanonicalProviderPrefix = "LLM--Models--";
    internal const string LegacyProviderPrefix = "gnougo_llm_";
    internal const string McpOverridePrefix =
        "LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--Code--Copilot--";
    private const string CopilotConfigurationRoot = "Code:Copilot";
    private const string Author = "GnOuGo.GithubCopilot.Mcp";

    private static readonly HashSet<string> BooleanPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code:Copilot:UseLoggedInUser",
        "Code:Copilot:ForwardTraceContext",
        "Code:Copilot:EnableApproveAll",
        "Code:Copilot:Telemetry:Enabled",
        "Code:Copilot:Telemetry:CaptureContent"
    };

    private static readonly HashSet<string> PositiveIntegerPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code:Copilot:RequestTimeoutSeconds",
        "Code:Copilot:ManagedSessionTtlSeconds",
        "Code:Copilot:WorkflowGrantTtlSeconds"
    };

    public static async Task<CopilotKeyVaultConfigurationOverlayResult> LoadAsync(
        IKeyVaultSecretCatalogReader reader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        IReadOnlyList<KeyVaultSecretLookupResult> canonicalProviders;
        IReadOnlyList<KeyVaultSecretLookupResult> legacyProviders;
        IReadOnlyList<KeyVaultSecretLookupResult> mcpOverrides;
        try
        {
            canonicalProviders = await reader.GetDefaultTenantSecretValuesByPrefixAsync(
                CanonicalProviderPrefix,
                Author,
                ct);
            legacyProviders = await reader.GetDefaultTenantSecretValuesByPrefixAsync(
                LegacyProviderPrefix,
                Author,
                ct);
            mcpOverrides = await reader.GetDefaultTenantSecretValuesByPrefixAsync(
                McpOverridePrefix,
                Author,
                ct);
        }
        catch (KeyVaultAccessException)
        {
            return new CopilotKeyVaultConfigurationOverlayResult(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                "KeyVault configuration is unavailable; the Copilot MCP is using its non-KeyVault settings.");
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddProviders(values, legacyProviders, LegacyProviderPrefix, canonicalProviders, isLegacy: true);
        AddProviders(values, canonicalProviders, CanonicalProviderPrefix, canonicalProviders, isLegacy: false);
        AddMcpOverrides(values, mcpOverrides);

        var warning = canonicalProviders.Count == 0
                      && legacyProviders.Count == 0
                      && mcpOverrides.Count == 0
            ? "KeyVault contains no Copilot configuration; the Copilot MCP is using its non-KeyVault settings."
            : null;
        return new CopilotKeyVaultConfigurationOverlayResult(values, warning);
    }

    private static void AddProviders(
        Dictionary<string, string?> values,
        IReadOnlyList<KeyVaultSecretLookupResult> providers,
        string prefix,
        IReadOnlyList<KeyVaultSecretLookupResult> canonicalProviders,
        bool isLegacy)
    {
        var seenProviderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in providers)
        {
            var providerName = GetRequiredSuffix(secret.Key, prefix, "provider");
            if (!seenProviderNames.Add(providerName))
                throw new InvalidDataException("KeyVault contains ambiguous provider configuration keys.");

            if (isLegacy && canonicalProviders.Any(candidate =>
                    string.Equals(
                        GetRequiredSuffix(candidate.Key, CanonicalProviderPrefix, "provider"),
                        providerName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            JsonObject provider;
            try
            {
                provider = JsonNode.Parse(secret.Value) as JsonObject
                    ?? throw new InvalidDataException("A KeyVault provider configuration must be a JSON object.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("A KeyVault provider configuration contains invalid JSON.", exception);
            }

            var providerValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            FlattenJson(
                provider,
                $"{CopilotConfigurationRoot}:Providers:{providerName}",
                providerValues);
            foreach (var entry in providerValues)
                values[entry.Key] = entry.Value;
        }
    }

    private static void AddMcpOverrides(
        Dictionary<string, string?> values,
        IReadOnlyList<KeyVaultSecretLookupResult> overrides)
    {
        var mappedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in overrides)
        {
            var suffix = GetRequiredSuffix(secret.Key, McpOverridePrefix, "MCP override");
            var segments = suffix.Split("--", StringSplitOptions.None);
            if (segments.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("A KeyVault MCP override contains an invalid configuration path.");

            var configurationKey = CopilotConfigurationRoot + ":" + string.Join(':', segments);
            if (!mappedKeys.Add(configurationKey))
                throw new InvalidDataException("KeyVault contains ambiguous MCP override keys.");

            ValidateKnownTypedValue(configurationKey, secret.Value);
            values[configurationKey] = secret.Value;
        }
    }

    private static void FlattenJson(
        JsonNode? node,
        string path,
        Dictionary<string, string?> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (string.IsNullOrWhiteSpace(property.Key))
                        throw new InvalidDataException("A KeyVault provider configuration contains an empty property name.");
                    FlattenJson(property.Value, path + ":" + property.Key, values);
                }
                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    FlattenJson(array[index], path + ":" + index.ToString(CultureInfo.InvariantCulture), values);
                break;

            case JsonValue value:
                AddUnique(values, path, ReadScalar(value));
                break;

            case null:
                AddUnique(values, path, string.Empty);
                break;
        }
    }

    private static string ReadScalar(JsonValue value)
    {
        if (value.TryGetValue<string>(out var stringValue))
            return stringValue;

        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number => document.RootElement.GetRawText(),
            _ => throw new InvalidDataException("A KeyVault provider configuration must contain scalar values, objects, or arrays.")
        };
    }

    private static void AddUnique(
        Dictionary<string, string?> values,
        string key,
        string? value)
    {
        if (!values.TryAdd(key, value))
            throw new InvalidDataException("A KeyVault provider configuration contains ambiguous property names.");
    }

    private static string GetRequiredSuffix(string key, string prefix, string kind)
    {
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"A KeyVault {kind} key is outside its requested prefix.");

        var suffix = key[prefix.Length..];
        if (string.IsNullOrWhiteSpace(suffix))
            throw new InvalidDataException($"A KeyVault {kind} key has no logical name.");
        return suffix;
    }

    private static void ValidateKnownTypedValue(string path, string value)
    {
        if (BooleanPaths.Contains(path) && !bool.TryParse(value, out _))
            throw new InvalidDataException($"KeyVault setting '{path}' is not a valid boolean.");

        if (PositiveIntegerPaths.Contains(path)
            && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                || integer <= 0))
        {
            throw new InvalidDataException($"KeyVault setting '{path}' is not a positive integer.");
        }

        if (string.Equals(path, "Code:Copilot:Mode", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "ask", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "edit", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "agent", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "plan", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("KeyVault setting 'Code:Copilot:Mode' is invalid.");
        }
    }
}
