using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

internal enum KeyVaultConfigSecretKind
{
    LlmProvider,
    McpServer
}

internal static class KeyVaultConfigNaming
{
    private const string KeyVaultSectionSeparator = "--";
    private const string LegacyLlmPrefix = "gnougo_llm_";
    private const string LegacyMcpPrefix = "gnougo_mcp_";

    private static readonly string LlmPrefix = $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}Models{KeyVaultSectionSeparator}";
    private static readonly string McpPrefix = $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}McpServers{KeyVaultSectionSeparator}";

    public static string BuildSecretKey(KeyVaultConfigSecretKind kind, string logicalName)
        => GetPrefix(kind) + logicalName;

    public static string GetDisplayConvention(KeyVaultConfigSecretKind kind)
        => kind switch
        {
            KeyVaultConfigSecretKind.LlmProvider => $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}Models{KeyVaultSectionSeparator}(name)",
            KeyVaultConfigSecretKind.McpServer => $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}McpServers{KeyVaultSectionSeparator}(name)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    public static IEnumerable<string> GetCandidateKeys(KeyVaultConfigSecretKind kind, string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
            yield break;

        yield return BuildSecretKey(kind, logicalName);
        yield return GetLegacyPrefix(kind) + logicalName;
    }

    public static bool MatchesSecretKey(KeyVaultConfigSecretKind kind, string key)
        => !string.IsNullOrWhiteSpace(TryGetLogicalName(kind, key));

    public static string? TryGetLogicalName(KeyVaultConfigSecretKind kind, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var prefix = GetPrefix(kind);
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return key[prefix.Length..];

        var legacyPrefix = GetLegacyPrefix(kind);
        if (key.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
            return key[legacyPrefix.Length..];

        return null;
    }

    public static string? ResolveExistingSecretKey(
        IEnumerable<KeyVaultSecretSummary> secrets,
        KeyVaultConfigSecretKind kind,
        string logicalName)
    {
        var secretKeys = secrets
            .Select(secret => secret.Key)
            .ToList();

        foreach (var candidate in GetCandidateKeys(kind, logicalName))
        {
            var existing = secretKeys
                .FirstOrDefault(key => string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
                return existing;
        }

        return null;
    }

    public static IReadOnlyList<KeyVaultSecretSummary> SelectPreferredSecrets(
        IEnumerable<KeyVaultSecretSummary> secrets,
        KeyVaultConfigSecretKind kind)
    {
        var preferred = new Dictionary<string, KeyVaultSecretSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var secret in secrets
                     .Where(summary => MatchesSecretKey(kind, summary.Key))
                     .OrderBy(summary => GetPriority(kind, summary.Key))
                     .ThenBy(summary => summary.Key, StringComparer.OrdinalIgnoreCase))
        {
            var logicalName = TryGetLogicalName(kind, secret.Key);
            if (string.IsNullOrWhiteSpace(logicalName) || preferred.ContainsKey(logicalName))
                continue;

            preferred[logicalName] = secret;
        }

        return preferred
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Value)
            .ToList();
    }

    private static int GetPriority(KeyVaultConfigSecretKind kind, string key)
        => key.StartsWith(GetPrefix(kind), StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string GetPrefix(KeyVaultConfigSecretKind kind)
        => kind switch
        {
            KeyVaultConfigSecretKind.LlmProvider => LlmPrefix,
            KeyVaultConfigSecretKind.McpServer => McpPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string GetLegacyPrefix(KeyVaultConfigSecretKind kind)
        => kind switch
        {
            KeyVaultConfigSecretKind.LlmProvider => LegacyLlmPrefix,
            KeyVaultConfigSecretKind.McpServer => LegacyMcpPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}

