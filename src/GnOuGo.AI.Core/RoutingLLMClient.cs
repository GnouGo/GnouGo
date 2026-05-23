using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM client that routes requests to the appropriate provider based on configuration.
/// Uses registered <see cref="ILLMProvider"/> implementations for extensibility.
/// The Models dictionary from <see cref="LLMOptions"/> resolves endpoints and credentials.
/// </summary>
public sealed class RoutingLLMClient
{
    private readonly LLMOptions _options;
    private readonly Dictionary<string, ILLMProvider> _providers;
    private readonly LLMModelMetadataResolver _metadataResolver;

    /// <summary>
    /// Creates a new routing client with the given options and provider implementations.
    /// </summary>
    /// <param name="options">LLM configuration (providers, models, defaults).</param>
    /// <param name="providers">Registered provider implementations.</param>
    public RoutingLLMClient(LLMOptions options, IEnumerable<ILLMProvider> providers)
    {
        _options = options;
        _metadataResolver = new LLMModelMetadataResolver(options);
        _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
            _providers[p.ProviderType] = p;
    }

    /// <summary>
    /// Convenience constructor that creates default providers (OpenAI, Ollama, Copilot, Anthropic)
    /// using the supplied HttpClient. Backward-compatible with existing call sites.
    /// </summary>
    public RoutingLLMClient(HttpClient http, LLMOptions options, ILoggerFactory? loggerFactory = null)
        : this(options, CreateDefaultProviders(http, loggerFactory))
    {
        LLMHttpClientDefaults.EnsureMinimumTimeout(http);
    }

    /// <summary>
    /// Sends a chat completion request to the appropriate provider and returns the response.
    /// </summary>
    public async Task<LLMClientResponse> CallAsync(LLMClientRequest request, CancellationToken ct = default)
    {
        var providerKey = ResolveProviderKey(request.Provider, request.Model);
        var providerOpts = _options.ResolveProvider(providerKey)
            ?? throw new InvalidOperationException(
                $"No model provider configured for '{providerKey}'. Available: [{string.Join(", ", _options.Models.Keys)}]");

        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.DefaultModel : request.Model;

        // Strip "vendor/model" prefix for model routing if the provider is specified via prefix
        if (!string.IsNullOrWhiteSpace(model) && model.Contains('/'))
        {
            var slashIdx = model.IndexOf('/');
            if (slashIdx > 0 && slashIdx < model.Length - 1)
            {
                var prefix = model[..slashIdx];
                // Only strip if prefix looks like a vendor name (not a file path)
                if (prefix.Length <= 30 && !prefix.Contains('.'))
                    model = model[(slashIdx + 1)..];
            }
        }

        var resolvedType = providerOpts.ResolvedType;

        if (_providers.TryGetValue(resolvedType, out var provider))
        {
            var metadata = _metadataResolver.Resolve(resolvedType, model);
            var sanitizedRequest = LLMRequestSanitizer.Sanitize(request, metadata);
            return await provider.CallAsync(model, providerOpts, sanitizedRequest, ct);
        }

        throw new InvalidOperationException(
            $"No ILLMProvider registered for type '{resolvedType}'. " +
            $"Registered: [{string.Join(", ", _providers.Keys)}]");
    }

    /// <summary>
    /// Returns the registered provider types (for diagnostics).
    /// </summary>
    public IReadOnlyCollection<string> RegisteredProviderTypes => _providers.Keys;

    /// <summary>
    /// Resolves the provider key from the request or model name heuristic.
    /// </summary>
    private string ResolveProviderKey(string? provider, string? model)
    {
        if (!string.IsNullOrWhiteSpace(provider))
            return provider;

        // Heuristic: if model uses "vendor/model" format, try to match vendor to a configured provider
        if (!string.IsNullOrWhiteSpace(model) && model.Contains('/'))
        {
            var prefix = model[..model.IndexOf('/')].ToLowerInvariant();
            // Map known vendor prefixes to provider keys
            foreach (var kv in _options.Models)
            {
                if (string.Equals(kv.Key, prefix, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
            if (prefix is "anthropic" or "claude")
            {
                foreach (var kv in _options.Models)
                {
                    if (string.Equals(kv.Value.ResolvedType, "anthropic", StringComparison.OrdinalIgnoreCase))
                        return kv.Key;
                }
            }
            // If vendor prefix matches a known Copilot pattern, use Copilot
            if (prefix is "openai" or "anthropic" or "meta" or "mistral" or "google" or "cohere" or "deepseek")
            {
                // Check if a Copilot provider is configured
                foreach (var kv in _options.Models)
                {
                    if (string.Equals(kv.Value.ResolvedType, "copilot", StringComparison.OrdinalIgnoreCase))
                        return kv.Key;
                }
            }
        }

        // Heuristic: if model name matches known Ollama patterns, route to Ollama
        if (!string.IsNullOrWhiteSpace(model))
        {
            var m = model.ToLowerInvariant();
            if (m.StartsWith("llama") || m.StartsWith("mistral") || m.StartsWith("phi") ||
                m.StartsWith("gemma") || m.StartsWith("qwen") || m.StartsWith("deepseek") ||
                m.StartsWith("codellama") || m.StartsWith("vicuna") || m.StartsWith("solar") ||
                m.StartsWith("command-r") || m.StartsWith("starcoder") || m.Contains(":"))
                return "Ollama";
        }

        return _options.DefaultProvider;
    }

    /// <summary>
    /// Creates the default set of providers (OpenAI, Ollama, Copilot, Anthropic) using a shared HttpClient.
    /// </summary>
    public static ILLMProvider[] CreateDefaultProviders(HttpClient http, ILoggerFactory? loggerFactory = null) =>
    [
        new OpenAiLLMProvider(http, loggerFactory?.CreateLogger<OpenAiLLMProvider>()),
        new OllamaLLMProvider(http, loggerFactory?.CreateLogger<OllamaLLMProvider>()),
        new CopilotLLMProvider(http, loggerFactory?.CreateLogger<CopilotLLMProvider>()),
        new AnthropicLLMProvider(http, loggerFactory?.CreateLogger<AnthropicLLMProvider>())
    ];
}

/// <summary>
/// Shared HTTP defaults for outbound LLM calls.
/// </summary>
public static class LLMHttpClientDefaults
{
    public static readonly TimeSpan MinimumTimeout = TimeSpan.FromMinutes(10);
    private static readonly ILogger Logger = NullLogger.Instance;

    public static void EnsureMinimumTimeout(HttpClient http)
    {
        if (http.Timeout == Timeout.InfiniteTimeSpan || http.Timeout >= MinimumTimeout)
            return;

        try
        {
            http.Timeout = MinimumTimeout;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "HttpClient timeout could not be adjusted because requests have already started.");
            // The HttpClient has already started requests; keep the existing timeout.
        }
    }
}

/// <summary>
/// Request DTO for <see cref="RoutingLLMClient"/>.
/// </summary>
public sealed class LLMClientRequest
{
    public string? Provider { get; set; }
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public double? Temperature { get; set; }
    public JsonNode? StructuredOutputSchema { get; set; }
    public bool? StructuredOutputStrict { get; set; }
    /// <summary>
    /// Optional reasoning / thinking effort. See <c>GnOuGo.Flow.Core.Runtime.LLMRequest.Reasoning</c>.
    /// Accepted: "minimal"|"low"|"medium"|"high"|"max"|"auto"|null.
    /// </summary>
    public string? Reasoning { get; set; }
    /// <summary>
    /// Requests provider-managed background generation for long-running calls when supported.
    /// Providers that do not support it may ignore this hint.
    /// </summary>
    public bool UseBackgroundMode { get; set; }
    public IReadOnlyList<LLMToolDef>? Tools { get; set; }
}

/// <summary>
/// Response DTO from <see cref="RoutingLLMClient"/>.
/// </summary>
public sealed class LLMClientResponse
{
    public string Text { get; set; } = "";
    public JsonNode? Json { get; set; }
    public JsonNode? Usage { get; set; }
    public JsonNode? Raw { get; set; }
    public List<ToolCallResult>? ToolCalls { get; set; }
}
