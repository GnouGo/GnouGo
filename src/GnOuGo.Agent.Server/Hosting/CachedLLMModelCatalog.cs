using System.Security.Cryptography;
using System.Text;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using Microsoft.Extensions.Caching.Memory;

namespace GnOuGo.Agent.Server.Hosting;

/// <summary>
/// Decorates <see cref="ILLMModelCatalog"/> with a short-lived in-memory cache.
/// The cache key includes the current provider configuration fingerprint so runtime updates
/// invalidate cached entries automatically.
/// </summary>
internal sealed class CachedLlmModelCatalog : ILLMModelCatalog
{
    private readonly ILLMModelCatalog _inner;
    private readonly LLMRuntimeOptionsStore _store;
    private readonly IMemoryCache _cache;
    private readonly ModelCatalogCacheSettings _settings;
    private readonly ILogger<CachedLlmModelCatalog> _logger;

    public CachedLlmModelCatalog(
        ILLMModelCatalog inner,
        LLMRuntimeOptionsStore store,
        IMemoryCache cache,
        ModelCatalogCacheSettings settings,
        ILogger<CachedLlmModelCatalog> logger)
    {
        _inner = inner;
        _store = store;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
    {
        if (!_settings.Enabled || _settings.AbsoluteExpirationSeconds <= 0)
            return await _inner.ListModelsAsync(provider, ct);

        var cacheKey = TryBuildCacheKey(provider);
        if (cacheKey is null)
            return await _inner.ListModelsAsync(provider, ct);

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<LLMModelDescriptor>? cached) && cached is not null)
        {
            _logger.LogDebug("Using cached model catalog for provider '{Provider}'.", provider);
            return cached;
        }

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_settings.AbsoluteExpirationSeconds);

            var models = await _inner.ListModelsAsync(provider, ct);
            var snapshot = models as LLMModelDescriptor[] ?? models.ToArray();
            _logger.LogDebug(
                "Cached {Count} model(s) for provider '{Provider}' for {TtlSeconds}s.",
                snapshot.Length,
                provider,
                _settings.AbsoluteExpirationSeconds);
            return (IReadOnlyList<LLMModelDescriptor>)snapshot;
        }) ?? [];
    }

    private string? TryBuildCacheKey(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        var options = _store.Current.ResolveProvider(provider);
        if (options is null)
            return null;

        var fingerprint = ComputeFingerprint(options);
        return $"llm-model-catalog::{provider.Trim().ToLowerInvariant()}::{fingerprint}";
    }

    private static string ComputeFingerprint(ModelProviderOptions options)
    {
        var payload = string.Join("\n",
            options.Url,
            options.ResolvedType,
            options.Type ?? string.Empty,
            options.Issuer ?? string.Empty,
            options.ClientId ?? string.Empty,
            options.Scopes ?? string.Empty,
            options.ApiKey ?? string.Empty,
            options.ClientSecret ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}


