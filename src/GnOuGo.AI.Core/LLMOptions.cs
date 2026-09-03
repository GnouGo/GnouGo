namespace GnOuGo.AI.Core;

/// <summary>
/// Configuration for LLM and MCP providers, typically bound from appsettings "LLM" section.
/// </summary>
public sealed class LLMOptions
{
    public const string SectionName = "LLM";

    /// <summary>Default provider key when not specified in the request (must match a key in <see cref="Models"/>).</summary>
    public string DefaultProvider { get; set; } = "OpenAi";

    /// <summary>Default model name when not specified in the request.</summary>
    public string DefaultModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Named model provider configurations (key = logical name, e.g. "OpenAi", "Ollama").
    /// The <see cref="DefaultProvider"/> must match one of these keys.
    /// </summary>
    public Dictionary<string, ModelProviderOptions> Models { get; set; } = new();

    /// <summary>
    /// Optional cloud fallback used only after the embedded local provider exhausts its bounded retry.
    /// </summary>
    public LLMFallbackOptions? Fallback { get; set; }

    /// <summary>
    /// Named MCP server configurations (key = logical server name, e.g. "Github").
    /// </summary>
    public Dictionary<string, McpServerOptions> McpServers { get; set; } = new();

    /// <summary>
    /// When true, TLS certificate errors are ignored for outgoing LLM HTTP calls.
    /// USE ONLY for corporate proxies with self-signed or internal CA certificates.
    /// </summary>
    public bool DangerousAcceptAnyServerCertificate { get; set; }

    /// <summary>
    /// Optional JSON files that define or override model metadata (limits, pricing, capabilities, aliases).
    /// Later files win over earlier files. Paths can be absolute or relative to the process/base directory.
    /// </summary>
    public List<string> ModelMetadataFiles { get; set; } = new();

    /// <summary>
    /// Inline model metadata overrides. Key = model id/alias, or provider-qualified model id/alias
    /// (for example: "openai/gpt-4o" and "copilot/gpt-4o") when pricing or limits differ by provider.
    /// These have the highest precedence.
    /// </summary>
    public Dictionary<string, LLMModelMetadata> ModelOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the <see cref="ModelProviderOptions"/> for a given provider key.
    /// Falls back to <see cref="DefaultProvider"/> if <paramref name="provider"/> is null/empty.
    /// Returns null if the provider key is not found.
    /// </summary>
    public ModelProviderOptions? ResolveProvider(string? provider)
    {
        var key = string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider;
        // Case-insensitive lookup
        foreach (var kv in Models)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        var alias = GetProviderAlias(key);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            foreach (var kv in Models)
            {
                if (string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }

        return null;
    }

    private static string? GetProviderAlias(string? provider)
        => provider?.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "claude",
            "claude" => "anthropic",
            _ => null
        };
}

/// <summary>
/// Configuration for a named model provider (used in the "Models" dictionary).
/// </summary>
public sealed class ModelProviderOptions
{
    /// <summary>Base URL for this provider (e.g. "https://api.openai.com/v1" or "http://localhost:11434").</summary>
    public string Url { get; set; } = "";

    /// <summary>API key (optional for local providers like Ollama). Also checked via {KEY}_API_KEY env var.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Provider type hint: "openai", "ollama", "copilot", "anthropic", or "local". The legacy alias "claude" is also accepted. Inferred from URL if not set.</summary>
    public string? Type { get; set; }

    /// <summary>OAuth2 issuer URL for token-based auth.</summary>
    public string? Issuer { get; set; }

    /// <summary>OAuth2 client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth2 client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OAuth2 private key PEM used for client assertion authentication.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>OAuth2 scopes (space-separated).</summary>
    public string? Scopes { get; set; }

    /// <summary>
    /// Optional API version query parameter appended to all requests (Azure OpenAI-style endpoints).
    /// Example: "2025-01-01-preview".
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Provider-scoped request-shaping policy. Model metadata limits remain capability ceilings;
    /// they are not request defaults unless <see cref="LLMUnspecifiedOutputTokensMode.ModelMaximum"/>
    /// is explicitly selected.
    /// </summary>
    public LLMProviderRequestPolicyOptions RequestPolicy { get; set; } = new();

    /// <summary>Provider-scoped bounded transient HTTP retry policy.</summary>
    public LLMProviderRetryPolicyOptions RetryPolicy { get; set; } = new();

    /// <summary>
    /// Returns the effective provider type: explicit <see cref="Type"/>, or inferred from URL.
    /// Supported values: "openai", "ollama", "copilot", "anthropic", "local". The legacy alias "claude" is accepted.
    /// </summary>
    public string ResolvedType =>
        !string.IsNullOrWhiteSpace(Type) ? NormalizeType(Type!)
        : Url.Contains("11434") || Url.Contains("ollama", StringComparison.OrdinalIgnoreCase) ? "ollama"
        : Url.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
          || Url.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic"
        : Url.Contains("models.github.ai", StringComparison.OrdinalIgnoreCase)
          || Url.Contains("copilot", StringComparison.OrdinalIgnoreCase) ? "copilot"
        : "openai";

    private static string NormalizeType(string type)
        => type.Trim().ToLowerInvariant() switch
        {
            "claude" => "anthropic",
            var normalized => normalized
        };
}

/// <summary>Protocol used when a caller requests background execution.</summary>
public enum LLMBackgroundProtocolMode
{
    Auto,
    Responses,
    ChatCompletions
}

/// <summary>How an omitted per-request output-token limit is represented on the wire.</summary>
public enum LLMUnspecifiedOutputTokensMode
{
    Omit,
    Configured,
    ModelMaximum
}

/// <summary>Provider-neutral request shaping policy for a configured model provider.</summary>
public sealed class LLMProviderRequestPolicyOptions
{
    /// <summary>
    /// Protocol selection for background calls. Auto probes Responses and falls back when the
    /// route is contractually unsupported; ChatCompletions bypasses that probe.
    /// </summary>
    public LLMBackgroundProtocolMode BackgroundProtocol { get; set; } = LLMBackgroundProtocolMode.Auto;

    /// <summary>Behavior when the caller does not set a maximum output-token count.</summary>
    public LLMUnspecifiedOutputTokensMode UnspecifiedOutputTokens { get; set; } = LLMUnspecifiedOutputTokensMode.Omit;

    /// <summary>Default used only when <see cref="UnspecifiedOutputTokens"/> is Configured.</summary>
    public int? DefaultMaxOutputTokens { get; set; }

    /// <summary>Optional provider-specific ceiling applied after the model capability ceiling.</summary>
    public int? MaxOutputTokensCap { get; set; }
}

/// <summary>Bounded recovery policy for safely retryable HTTP responses.</summary>
public sealed class LLMProviderRetryPolicyOptions
{
    /// <summary>Total attempts, including the initial request.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Initial full-jitter exponential-backoff bound.</summary>
    public int BaseDelayMilliseconds { get; set; } = 1_000;

    /// <summary>Maximum delay for one retry.</summary>
    public int MaxDelayMilliseconds { get; set; } = 30_000;

    /// <summary>Maximum cumulative retry delay for one HTTP operation.</summary>
    public int MaxTotalDelayMilliseconds { get; set; } = 60_000;

    /// <summary>Whether valid Retry-After response headers take precedence over jitter backoff.</summary>
    public bool HonorRetryAfter { get; set; } = true;
}

/// <summary>Deterministic validation for provider request and retry policy configuration.</summary>
public static class LLMOptionsValidation
{
    public static void ValidateAndThrow(LLMOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var (providerName, provider) in options.Models)
        {
            if (provider is null)
                throw new InvalidOperationException($"LLM provider '{providerName}' configuration is missing.");

            ValidateProvider(providerName, provider);
        }
    }

    public static void ValidateProvider(string providerName, ModelProviderOptions provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(provider);

        var requestPolicy = provider.RequestPolicy
            ?? throw new InvalidOperationException($"LLM provider '{providerName}' RequestPolicy cannot be null.");
        if (!Enum.IsDefined(requestPolicy.BackgroundProtocol))
            throw new InvalidOperationException($"LLM provider '{providerName}' has an invalid BackgroundProtocol.");
        if (!Enum.IsDefined(requestPolicy.UnspecifiedOutputTokens))
            throw new InvalidOperationException($"LLM provider '{providerName}' has an invalid UnspecifiedOutputTokens mode.");
        if (requestPolicy.DefaultMaxOutputTokens is <= 0)
            throw new InvalidOperationException($"LLM provider '{providerName}' DefaultMaxOutputTokens must be positive when set.");
        if (requestPolicy.MaxOutputTokensCap is <= 0)
            throw new InvalidOperationException($"LLM provider '{providerName}' MaxOutputTokensCap must be positive when set.");
        if (requestPolicy.UnspecifiedOutputTokens == LLMUnspecifiedOutputTokensMode.Configured
            && requestPolicy.DefaultMaxOutputTokens is null)
        {
            throw new InvalidOperationException(
                $"LLM provider '{providerName}' requires DefaultMaxOutputTokens when UnspecifiedOutputTokens is Configured.");
        }
        if (requestPolicy.DefaultMaxOutputTokens is { } configuredDefault
            && requestPolicy.MaxOutputTokensCap is { } configuredCap
            && configuredDefault > configuredCap)
        {
            throw new InvalidOperationException(
                $"LLM provider '{providerName}' DefaultMaxOutputTokens cannot exceed MaxOutputTokensCap.");
        }

        var retryPolicy = provider.RetryPolicy
            ?? throw new InvalidOperationException($"LLM provider '{providerName}' RetryPolicy cannot be null.");
        if (retryPolicy.MaxAttempts is < 1 or > 20)
            throw new InvalidOperationException($"LLM provider '{providerName}' RetryPolicy.MaxAttempts must be between 1 and 20.");
        if (retryPolicy.BaseDelayMilliseconds <= 0)
            throw new InvalidOperationException($"LLM provider '{providerName}' RetryPolicy.BaseDelayMilliseconds must be positive.");
        if (retryPolicy.MaxDelayMilliseconds <= 0)
            throw new InvalidOperationException($"LLM provider '{providerName}' RetryPolicy.MaxDelayMilliseconds must be positive.");
        if (retryPolicy.MaxTotalDelayMilliseconds < 0)
            throw new InvalidOperationException($"LLM provider '{providerName}' RetryPolicy.MaxTotalDelayMilliseconds cannot be negative.");
        if (retryPolicy.BaseDelayMilliseconds > retryPolicy.MaxDelayMilliseconds)
            throw new InvalidOperationException($"LLM provider '{providerName}' retry base delay cannot exceed its maximum delay.");
    }
}

/// <summary>Optional provider/model pair used after local retry exhaustion.</summary>
public sealed class LLMFallbackOptions
{
    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";
}

/// <summary>
/// Configuration for a named MCP server (used in the "McpServers" dictionary).
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>Transport type: "http", "sse", or "stdio".</summary>
    public string Type { get; set; } = "http";

    /// <summary>Human-friendly description of what this MCP server is for.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Maximum time allowed for workflow planning discovery to connect to this server
    /// and list its capabilities. When omitted, the planner uses its default timeout.
    /// </summary>
    public int? DiscoveryTimeoutSeconds { get; set; }

    /// <summary>
    /// Recommended minimum timeout, in seconds, for workflow <c>mcp.call</c> executions
    /// against this server. Useful for tools backed by slow LLM providers.
    /// </summary>
    public int? CallTimeoutSeconds { get; set; }

    /// <summary>Server URL (for http/sse transports).</summary>
    public string Url { get; set; } = "";

    /// <summary>API key for Bearer auth.</summary>
    public string? ApiKey { get; set; }

    /// <summary>OAuth2 issuer URL for token-based auth.</summary>
    public string? Issuer { get; set; }

    /// <summary>OAuth2 client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth2 client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OAuth2 scopes (space-separated).</summary>
    public string? Scopes { get; set; }

    /// <summary>Command to run (for stdio transport).</summary>
    public string? Command { get; set; }

    /// <summary>Arguments for the command (stdio transport).</summary>
    public List<string>? Args { get; set; }

    /// <summary>
    /// Extra environment variables passed to stdio MCP subprocesses.
    /// Useful for injecting encrypted runtime settings without changing bundled appsettings.
    /// </summary>
    public Dictionary<string, string?>? EnvironmentVariables { get; set; }
}
