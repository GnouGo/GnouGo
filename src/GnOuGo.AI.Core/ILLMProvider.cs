namespace GnOuGo.AI.Core;

/// <summary>
/// Abstraction for an LLM provider backend (OpenAI, Ollama, Copilot, Azure, etc.).
/// Each implementation handles one provider type and encapsulates protocol differences.
/// Register implementations via <see cref="RoutingLLMClient"/> to make them available.
/// </summary>
public interface ILLMProvider
{
    /// <summary>
    /// Unique type identifier for this provider (e.g. "openai", "ollama", "copilot").
    /// Must match <see cref="ModelProviderOptions.ResolvedType"/>.
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Sends a chat completion request to the provider and returns the response.
    /// </summary>
    /// <param name="model">Resolved model name.</param>
    /// <param name="provider">Provider configuration (URL, API key, etc.).</param>
    /// <param name="request">The LLM request to send.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LLMClientResponse> CallAsync(
        string model,
        ModelProviderOptions provider,
        LLMClientRequest request,
        CancellationToken ct);
}

