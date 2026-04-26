using System.Text.Json;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Embeddings;
using GnOuGo.Auth.Core;
using GnOuGo.DocsIngestor.Mcp.Models;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.DocsIngestor.Mcp.Services;

public sealed class KeyVaultEmbeddingConfigProvider
{
    private readonly KeyVaultService _keyVault;
    private readonly IHttpClientFactory _httpClientFactory;

    public KeyVaultEmbeddingConfigProvider(KeyVaultService keyVault, IHttpClientFactory httpClientFactory)
    {
        _keyVault = keyVault;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IEmbeddingModel> ResolveAsync(string embeddingConfigName, Guid? keyVaultTenantId, string author, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(embeddingConfigName))
            throw new ArgumentException("Embedding configuration name is required.", nameof(embeddingConfigName));

        if (TryCreateHashModel(embeddingConfigName, out var hashModel))
            return hashModel;

        var secret = await _keyVault.GetSecretAsync(embeddingConfigName, keyVaultTenantId, author, ct)
            ?? throw new KeyNotFoundException($"Embedding configuration secret '{embeddingConfigName}' was not found in KeyVault.");

        var config = JsonSerializer.Deserialize(secret.Value, DocsIngestorJsonContext.Default.EmbeddingConfig)
            ?? throw new InvalidOperationException($"Embedding configuration secret '{embeddingConfigName}' is empty or invalid JSON.");

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

