namespace GnOuGo.AI.Core;

/// <summary>
/// Routes model-list requests to the appropriate provider-specific catalog implementation.
/// </summary>
public sealed class RoutingLLMModelCatalog : ILLMModelCatalog
{
    private readonly LLMOptions _options;
    private readonly Dictionary<string, ILLMModelCatalogProvider> _providers;

    public RoutingLLMModelCatalog(LLMOptions options, IEnumerable<ILLMModelCatalogProvider> providers)
    {
        _options = options;
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
        return models
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<string> RegisteredProviderTypes => _providers.Keys;

    public static ILLMModelCatalogProvider[] CreateDefaultProviders(HttpClient http) =>
    [
        new OpenAiLLMProvider(http),
        new OllamaLLMProvider(http),
        new CopilotLLMProvider(http)
    ];
}

