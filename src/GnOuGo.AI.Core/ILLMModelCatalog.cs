namespace GnOuGo.AI.Core;

/// <summary>
/// Provider-agnostic service used to retrieve the list of available models
/// from a configured LLM backend.
/// </summary>
public interface ILLMModelCatalog
{
    /// <summary>
    /// Lists models for a configured provider key (for example: <c>openai</c>, <c>ollama</c>, <c>copilot</c>).
    /// </summary>
    Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default);
}

/// <summary>
/// Internal/provider-facing abstraction implemented by each provider-specific backend.
/// </summary>
public interface ILLMModelCatalogProvider
{
    /// <summary>
    /// Unique provider type identifier (must match <see cref="ModelProviderOptions.ResolvedType"/>).
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Lists models available from the provider using the resolved provider configuration.
    /// </summary>
    Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct);
}

/// <summary>
/// Describes an available model exposed by an LLM provider.
/// </summary>
public sealed record LLMModelDescriptor(
    string Id,
    string DisplayName,
    string ProviderType,
    string? OwnedBy = null);

