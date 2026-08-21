using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
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
    private static readonly ActivitySource ActivitySource = new("GnOuGo.AI.Core.Routing");
    private static readonly Meter Meter = new("GnOuGo.AI.Core.Routing");
    private static readonly Counter<long> LocalRetries = Meter.CreateCounter<long>("gnougo.local_llm.retry.count");
    private static readonly Counter<long> LocalFallbacks = Meter.CreateCounter<long>("gnougo.local_llm.fallback.count");
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
    public RoutingLLMClient(
        HttpClient http,
        LLMOptions options,
        ILoggerFactory? loggerFactory = null,
        IMemoryCache? backgroundModeCache = null)
        : this(options, CreateDefaultProviders(http, loggerFactory, backgroundModeCache))
    {
        LLMHttpClientDefaults.EnsureMinimumTimeout(http);
    }

    /// <summary>
    /// Sends a chat completion request to the appropriate provider and returns the response.
    /// </summary>
    public async Task<LLMClientResponse> CallAsync(LLMClientRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        if (!_providers.TryGetValue(resolvedType, out var provider))
        {
            throw new InvalidOperationException(
                $"No ILLMProvider registered for type '{resolvedType}'. " +
                $"Registered: [{string.Join(", ", _providers.Keys)}]");
        }

        var metadata = _metadataResolver.Resolve(resolvedType, model);
        var sanitizedRequest = LLMRequestSanitizer.Sanitize(request, metadata);
        try
        {
            if (!string.Equals(resolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase))
                return await provider.CallAsync(model, providerOpts, sanitizedRequest, ct).ConfigureAwait(false);

            return await CallLocalWithFallbackAsync(provider, model, providerOpts, sanitizedRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw LLMProviderFailureClassifier.Classify(ex);
        }
    }

    private async Task<LLMClientResponse> CallLocalWithFallbackAsync(
        ILLMProvider localProvider,
        string model,
        ModelProviderOptions providerOptions,
        LLMClientRequest request,
        CancellationToken ct)
    {
        LocalLLMException? lastFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1)
            {
                LocalRetries.Add(
                    1,
                    new KeyValuePair<string, object?>("model", model),
                    new KeyValuePair<string, object?>("failure_kind", lastFailure?.Kind.ToString()));
            }

            using var activity = ActivitySource.StartActivity("local_llm.call");
            activity?.SetTag("gen_ai.provider.name", LocalLLMProvider.Type);
            activity?.SetTag("gen_ai.request.model", model);
            activity?.SetTag("gnougo.local.attempt", attempt);

            try
            {
                return await localProvider.CallAsync(model, providerOptions, request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                throw;
            }
            catch (LocalLLMException ex)
            {
                lastFailure = ex;
                activity?.SetTag("gnougo.local.failure_kind", ex.Kind.ToString());
                activity?.SetStatus(ActivityStatusCode.Error, ex.Kind.ToString());
                if (attempt == 1 && ex.Kind == LocalLLMFailureKind.InvalidStructuredOutput)
                    request = CreateStructuredRetryRequest(request, ex.ValidationErrors);
            }
        }

        var fallback = _options.Fallback;
        if (fallback is null || string.IsNullOrWhiteSpace(fallback.Provider))
            throw lastFailure!;

        var fallbackKey = fallback.Provider.Trim();
        var fallbackOptions = _options.ResolveProvider(fallbackKey)
            ?? throw new InvalidOperationException("The configured local LLM fallback provider does not exist.");
        if (string.Equals(fallbackOptions.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured local LLM fallback must be a non-local provider.");

        if (!_providers.TryGetValue(fallbackOptions.ResolvedType, out var fallbackProvider))
            throw new InvalidOperationException("The configured local LLM fallback provider is not registered.");

        var fallbackModel = string.IsNullOrWhiteSpace(fallback.Model) ? _options.DefaultModel : fallback.Model.Trim();
        var fallbackRequest = LocalLLMProvider.CloneRequest(request);
        fallbackRequest.Provider = fallbackKey;
        fallbackRequest.Model = fallbackModel;
        var metadata = _metadataResolver.Resolve(fallbackOptions.ResolvedType, fallbackModel);
        fallbackRequest = LLMRequestSanitizer.Sanitize(fallbackRequest, metadata);

        using var fallbackActivity = ActivitySource.StartActivity("local_llm.fallback");
        fallbackActivity?.SetTag("gen_ai.provider.name", fallbackOptions.ResolvedType);
        fallbackActivity?.SetTag("gen_ai.request.model", fallbackModel);
        fallbackActivity?.SetTag("gnougo.local.failure_kind", lastFailure?.Kind.ToString());
        LocalFallbacks.Add(
            1,
            new KeyValuePair<string, object?>("provider", fallbackOptions.ResolvedType),
            new KeyValuePair<string, object?>("failure_kind", lastFailure?.Kind.ToString()));

        return await fallbackProvider.CallAsync(fallbackModel, fallbackOptions, fallbackRequest, ct).ConfigureAwait(false);
    }

    private static LLMClientRequest CreateStructuredRetryRequest(
        LLMClientRequest request,
        IReadOnlyList<string> validationErrors)
    {
        var retry = LocalLLMProvider.CloneRequest(request);
        var feedback = validationErrors.Count == 0
            ? "The previous response did not satisfy the requested JSON schema."
            : $"The previous response did not satisfy the requested JSON schema: {string.Join("; ", validationErrors.Take(5))}.";
        retry.Prompt = $"{request.Prompt}\n\n{feedback} Return only a corrected JSON value.";
        return retry;
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

        // An explicitly configured local default owns local aliases such as qwen3:0.6b.
        // This must run before the legacy Ollama model-name heuristic.
        if (_options.ResolveProvider(_options.DefaultProvider) is { ResolvedType: LocalLLMProvider.Type })
            return _options.DefaultProvider;

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
    public static ILLMProvider[] CreateDefaultProviders(
        HttpClient http,
        ILoggerFactory? loggerFactory = null,
        IMemoryCache? backgroundModeCache = null) =>
    [
        new OpenAiLLMProvider(http, loggerFactory?.CreateLogger<OpenAiLLMProvider>(), backgroundModeCache),
        new OllamaLLMProvider(http, loggerFactory?.CreateLogger<OllamaLLMProvider>()),
        new CopilotLLMProvider(http, loggerFactory?.CreateLogger<CopilotLLMProvider>()),
        new AnthropicLLMProvider(http, loggerFactory?.CreateLogger<AnthropicLLMProvider>(), backgroundModeCache)
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
    /// <summary>
    /// Maximum number of output tokens the model may produce.
    /// Populated automatically from model metadata when available.
    /// Providers use this instead of hard-coded defaults.
    /// </summary>
    public int? MaxOutputTokens { get; set; }
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
