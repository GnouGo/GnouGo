using System.Text.Json;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Embeddings;
using GnOuGo.Auth.Core;
using GnOuGo.DocIngestor.Mcp.Models;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.DocIngestor.Mcp.Services;

public sealed class KeyVaultEmbeddingConfigProvider
{
    private const string EmbeddingPrefix = "LLM--Embeddings--";
    private const string EmbeddingDefaultKey = "LLM--EmbeddingDefaults--default";
    private const string LegacyEmbeddingPrefix = "gnougo_embedding_";

    private readonly KeyVaultService _keyVault;
    private readonly IHttpClientFactory _httpClientFactory;

    public KeyVaultEmbeddingConfigProvider(KeyVaultService keyVault, IHttpClientFactory httpClientFactory)
    {
        _keyVault = keyVault;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IEmbeddingModel> ResolveAsync(string embeddingConfigName, Guid? keyVaultTenantId, string author, CancellationToken ct = default)
    {
        embeddingConfigName = await ResolveRequestedNameAsync(embeddingConfigName, keyVaultTenantId, author, ct);

        if (TryCreateHashModel(embeddingConfigName, out var hashModel))
            return hashModel;

        var (secretKey, secretValue) = await LoadEmbeddingSecretAsync(embeddingConfigName, keyVaultTenantId, author, ct);

        var config = JsonSerializer.Deserialize(secretValue, DocsIngestorJsonContext.Default.EmbeddingConfig)
            ?? throw new InvalidOperationException($"Embedding configuration secret '{secretKey}' is empty or invalid JSON.");

        var provider = (config.Provider ?? string.Empty).Trim().ToLowerInvariant();
        var name = string.IsNullOrWhiteSpace(config.Name) ? embeddingConfigName : config.Name!;
        var dims = config.Dimensions.GetValueOrDefault(provider == "ollama" ? 768 : 3072);
        var http = _httpClientFactory.CreateClient(nameof(KeyVaultEmbeddingConfigProvider));

        return provider switch
        {
            "hash" => new HashEmbeddingModel(name, config.Dimensions.GetValueOrDefault(384)),
            "ollama" => new OllamaEmbeddingModel(
                name,
                string.IsNullOrWhiteSpace(config.BaseUrl) ? "http://localhost:11434" : config.BaseUrl!,
                string.IsNullOrWhiteSpace(config.Model) ? "nomic-embed-text" : config.Model!,
                http,
                dims),
            "openai" or "openai-compatible" => new OpenAiCompatibleEmbeddingModel(
                name,
                config.EndpointUrl ?? throw new InvalidOperationException("OpenAI-compatible embedding config requires endpointUrl."),
                config.Model ?? throw new InvalidOperationException("OpenAI-compatible embedding config requires model."),
                new KeyVaultBackedApiKeyProvider(config, _keyVault, keyVaultTenantId, author),
                http,
                dims),
            _ => throw new NotSupportedException($"Embedding provider '{config.Provider}' is not supported.")
        };
    }

    public async Task<string> ResolveConfigNameAsync(string embeddingConfigName, Guid? keyVaultTenantId, string author, CancellationToken ct = default)
        => await ResolveRequestedNameAsync(embeddingConfigName, keyVaultTenantId, author, ct);

    private async Task<string> ResolveRequestedNameAsync(string embeddingConfigName, Guid? keyVaultTenantId, string author, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(embeddingConfigName))
            return embeddingConfigName.Trim();

        var defaultSecret = await _keyVault.GetSecretAsync(EmbeddingDefaultKey, keyVaultTenantId, author, ct);
        if (defaultSecret is not null && !string.IsNullOrWhiteSpace(defaultSecret.Value))
        {
            using var defaultJson = JsonDocument.Parse(defaultSecret.Value);
            if (defaultJson.RootElement.TryGetProperty("defaultEmbeddingConfig", out var defaultName)
                && defaultName.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(defaultName.GetString()))
            {
                return defaultName.GetString()!.Trim();
            }
        }

        throw new InvalidOperationException(
            "Embedding configuration is required. Configure one with `/embedding add` and `/embedding default`, " +
            "or pass embeddingConfigName explicitly (for tests, built-in values `hash-384` and `hash-768` are available). Document ingestion/search cannot safely reuse a chat LLM as an embedding model.");
    }

    private async Task<(string Key, string Value)> LoadEmbeddingSecretAsync(string embeddingConfigName, Guid? keyVaultTenantId, string author, CancellationToken ct)
    {
        foreach (var key in GetCandidateSecretKeys(embeddingConfigName))
        {
            var secret = await _keyVault.GetSecretAsync(key, keyVaultTenantId, author, ct);
            if (secret is not null)
                return (key, secret.Value);
        }

        throw new KeyNotFoundException(
            $"Embedding configuration '{embeddingConfigName}' was not found in KeyVault. Expected secret key '{EmbeddingPrefix}{embeddingConfigName}'. Run `/embedding add` or use the embedding config required by the target collection.");
    }

    private static IEnumerable<string> GetCandidateSecretKeys(string embeddingConfigName)
    {
        yield return EmbeddingPrefix + embeddingConfigName;
        yield return LegacyEmbeddingPrefix + embeddingConfigName;
        yield return embeddingConfigName;
    }

    private static bool TryCreateHashModel(string name, out IEmbeddingModel model)
    {
        if (name.Equals("hash-384", StringComparison.OrdinalIgnoreCase))
        {
            model = new HashEmbeddingModel("hash-384", 384);
            return true;
        }

        if (name.Equals("hash-768", StringComparison.OrdinalIgnoreCase))
        {
            model = new HashEmbeddingModel("hash-768", 768);
            return true;
        }

        model = null!;
        return false;
    }

    private sealed class KeyVaultBackedApiKeyProvider : IApiKeyProvider
    {
        private readonly EmbeddingConfig _config;
        private readonly KeyVaultService _keyVault;
        private readonly Guid? _tenantId;
        private readonly string _author;

        public KeyVaultBackedApiKeyProvider(EmbeddingConfig config, KeyVaultService keyVault, Guid? tenantId, string author)
        {
            _config = config;
            _keyVault = keyVault;
            _tenantId = tenantId;
            _author = author;
        }

        public async ValueTask<string> GetApiKeyAsync(CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
                return _config.ApiKey!;

            if (string.IsNullOrWhiteSpace(_config.ApiKeySecretKey))
                throw new InvalidOperationException("OpenAI-compatible embedding config requires apiKey or apiKeySecretKey.");

            var secret = await _keyVault.GetSecretAsync(_config.ApiKeySecretKey!, _tenantId, _author, ct)
                ?? throw new KeyNotFoundException($"API key secret '{_config.ApiKeySecretKey}' was not found in KeyVault.");

            return secret.Value;
        }
    }
}

