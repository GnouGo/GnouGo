using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

internal enum KeyVaultConfigSecretKind
{
    LlmProvider,
    McpServer,
    EmbeddingConfig,
    EmbeddingDefault
}

internal static class KeyVaultConfigNaming
{
    private const string KeyVaultSectionSeparator = "--";
    private const string LegacyLlmPrefix = "gnougo_llm_";
    private const string LegacyMcpPrefix = "gnougo_mcp_";
    private const string LegacyEmbeddingPrefix = "gnougo_embedding_";
    private const string LegacyEmbeddingDefaultPrefix = "gnougo_embedding_default_";

    private static readonly string LlmPrefix = $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}Models{KeyVaultSectionSeparator}";
    private static readonly string McpPrefix = $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}McpServers{KeyVaultSectionSeparator}";
    private static readonly string EmbeddingPrefix = $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}Embeddings{KeyVaultSectionSeparator}";
    private static readonly string EmbeddingDefaultPrefix = $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}EmbeddingDefaults{KeyVaultSectionSeparator}";

    public static string BuildSecretKey(KeyVaultConfigSecretKind kind, string logicalName)
        => GetPrefix(kind) + logicalName;

    public static string GetDisplayConvention(KeyVaultConfigSecretKind kind)
        => kind switch
        {
            KeyVaultConfigSecretKind.LlmProvider => $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}Models{KeyVaultSectionSeparator}(name)",
            KeyVaultConfigSecretKind.McpServer => $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}McpServers{KeyVaultSectionSeparator}(name)",
            KeyVaultConfigSecretKind.EmbeddingConfig => $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}Embeddings{KeyVaultSectionSeparator}(name)",
            KeyVaultConfigSecretKind.EmbeddingDefault => $"{LLMOptions.SectionName}{KeyVaultSectionSeparator}EmbeddingDefaults{KeyVaultSectionSeparator}(name)",
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
        return SelectPreferredSecrets(FindEquivalentSecrets(secrets, kind, logicalName), kind)
            .SingleOrDefault()
            ?.Key;
    }

    public static string ResolveWriteSecretKey(
        IEnumerable<KeyVaultSecretSummary> secrets,
        KeyVaultConfigSecretKind kind,
        string logicalName)
    {
        var preferredKey = BuildSecretKey(kind, logicalName);
        var canonical = FindEquivalentSecrets(secrets, kind, logicalName)
            .Where(secret => GetPriority(kind, secret.Key) == 0)
            .ToArray();

        var exact = canonical.FirstOrDefault(secret =>
            string.Equals(secret.Key, preferredKey, StringComparison.Ordinal));
        if (exact is not null)
            return exact.Key;

        return canonical.Length switch
        {
            0 => preferredKey,
            1 => canonical[0].Key,
            _ => throw CreateAmbiguousConfigurationException()
        };
    }

    public static IReadOnlyList<KeyVaultSecretSummary> FindEquivalentSecrets(
        IEnumerable<KeyVaultSecretSummary> secrets,
        KeyVaultConfigSecretKind kind,
        string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
            return [];

        return secrets
            .Where(secret => string.Equals(
                TryGetLogicalName(kind, secret.Key),
                logicalName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(secret => GetPriority(kind, secret.Key))
            .ThenBy(secret => secret.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<KeyVaultSecretSummary> SelectPreferredSecrets(
        IEnumerable<KeyVaultSecretSummary> secrets,
        KeyVaultConfigSecretKind kind)
    {
        var preferred = new List<(string LogicalName, KeyVaultSecretSummary Secret)>();
        foreach (var group in secrets
                     .Where(summary => MatchesSecretKey(kind, summary.Key))
                     .GroupBy(
                         summary => TryGetLogicalName(kind, summary.Key)!,
                         StringComparer.OrdinalIgnoreCase))
        {
            var priorityGroups = group
                .GroupBy(secret => GetPriority(kind, secret.Key))
                .ToArray();
            if (priorityGroups.Any(static candidates => candidates.Count() != 1))
                throw CreateAmbiguousConfigurationException();

            var candidate = priorityGroups
                .OrderBy(static candidates => candidates.Key)
                .First()
                .Single();

            preferred.Add((group.Key, candidate));
        }

        return preferred
            .OrderBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Secret.Key, StringComparer.Ordinal)
            .Select(item => item.Secret)
            .ToList();
    }

    private static InvalidDataException CreateAmbiguousConfigurationException()
        => new("KeyVault contains ambiguous configuration keys for the same logical setting.");

    private static int GetPriority(KeyVaultConfigSecretKind kind, string key)
        => key.StartsWith(GetPrefix(kind), StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string GetPrefix(KeyVaultConfigSecretKind kind)
        => kind switch
        {
            KeyVaultConfigSecretKind.LlmProvider => LlmPrefix,
            KeyVaultConfigSecretKind.McpServer => McpPrefix,
            KeyVaultConfigSecretKind.EmbeddingConfig => EmbeddingPrefix,
            KeyVaultConfigSecretKind.EmbeddingDefault => EmbeddingDefaultPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string GetLegacyPrefix(KeyVaultConfigSecretKind kind)
        => kind switch
        {
            KeyVaultConfigSecretKind.LlmProvider => LegacyLlmPrefix,
            KeyVaultConfigSecretKind.McpServer => LegacyMcpPrefix,
            KeyVaultConfigSecretKind.EmbeddingConfig => LegacyEmbeddingPrefix,
            KeyVaultConfigSecretKind.EmbeddingDefault => LegacyEmbeddingDefaultPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
