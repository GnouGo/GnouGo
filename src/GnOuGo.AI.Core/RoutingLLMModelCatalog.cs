namespace GnOuGo.AI.Core;

/// <summary>
/// Routes model-list requests to the appropriate provider-specific catalog implementation.
/// </summary>
public sealed class RoutingLLMModelCatalog : ILLMModelCatalog
{
    private readonly LLMOptions _options;
    private readonly Dictionary<string, ILLMModelCatalogProvider> _providers;
    private readonly LLMModelMetadataResolver _metadataResolver;

    public RoutingLLMModelCatalog(LLMOptions options, IEnumerable<ILLMModelCatalogProvider> providers)
    {
        _options = options;
        _metadataResolver = new LLMModelMetadataResolver(options);
        _providers = new Dictionary<string, ILLMModelCatalogProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            _providers[provider.ProviderType] = provider;
    }

    public RoutingLLMModelCatalog(HttpClient http, LLMOptions options)
        : this(options, CreateDefaultProviders(http))
    {
    }

    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required.", nameof(provider));

        var providerOptions = _options.ResolveProvider(provider)
            ?? throw new InvalidOperationException(
                $"No model provider configured for '{provider}'. Available: [{string.Join(", ", _options.Models.Keys)}]");

        var resolvedType = providerOptions.ResolvedType;
        if (!_providers.TryGetValue(resolvedType, out var catalogProvider))
        {
            throw new InvalidOperationException(
                $"No ILLMModelCatalogProvider registered for type '{resolvedType}'. Registered: [{string.Join(", ", _providers.Keys)}]");
        }

        var models = await catalogProvider.ListModelsAsync(providerOptions, ct);
        var enriched = new Dictionary<string, LLMModelDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
            enriched[model.Id] = Enrich(model, resolvedType);

        foreach (var metadata in _metadataResolver.ListConfiguredMetadata(resolvedType))
        {
            if (!enriched.ContainsKey(metadata.Id))
                enriched[metadata.Id] = ToDescriptor(metadata, resolvedType);
        }

        return enriched.Values
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private LLMModelDescriptor Enrich(LLMModelDescriptor descriptor, string providerType)
    {
        var metadata = _metadataResolver.Resolve(providerType, descriptor.Id);
        return descriptor with
        {
            DisplayName = metadata.DisplayName ?? descriptor.DisplayName,
            ProviderType = metadata.ProviderType ?? descriptor.ProviderType,
            OwnedBy = metadata.OwnedBy ?? descriptor.OwnedBy,
            ContextWindowTokens = metadata.ContextWindowTokens,
            MaxInputTokens = metadata.MaxInputTokens,
            MaxOutputTokens = metadata.MaxOutputTokens,
            Pricing = metadata.Pricing,
            Capabilities = metadata.Capabilities,
            Extra = metadata.Extra
        };
    }

    private static LLMModelDescriptor ToDescriptor(LLMModelMetadata metadata, string providerType)
        => new(
            metadata.Id,
            metadata.DisplayName ?? metadata.Id,
            metadata.ProviderType ?? providerType,
            metadata.OwnedBy,
            metadata.ContextWindowTokens,
            metadata.MaxInputTokens,
            metadata.MaxOutputTokens,
            metadata.Pricing,
            metadata.Capabilities,
            metadata.Extra);

    public IReadOnlyCollection<string> RegisteredProviderTypes => _providers.Keys;

    public static ILLMModelCatalogProvider[] CreateDefaultProviders(HttpClient http) =>
    [
        new OpenAiLLMProvider(http),
        new OllamaLLMProvider(http),
        new CopilotLLMProvider(http)
    ];
}

