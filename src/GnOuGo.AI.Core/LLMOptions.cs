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
    /// Named MCP server configurations (key = logical server name, e.g. "Github").
    /// </summary>
    public Dictionary<string, McpServerOptions> McpServers { get; set; } = new();

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
        return null;
    }
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

    /// <summary>Provider type hint: "openai", "ollama", or "copilot". Inferred from URL if not set.</summary>
    public string? Type { get; set; }

    /// <summary>OAuth2 issuer URL for token-based auth.</summary>
    public string? Issuer { get; set; }

    /// <summary>OAuth2 client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth2 client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OAuth2 scopes (space-separated).</summary>
    public string? Scopes { get; set; }

    /// <summary>
    /// Returns the effective provider type: explicit <see cref="Type"/>, or inferred from URL.
    /// Supported values: "openai", "ollama", "copilot".
    /// </summary>
    public string ResolvedType =>
        !string.IsNullOrWhiteSpace(Type) ? Type!.ToLowerInvariant()
        : Url.Contains("11434") || Url.Contains("ollama", StringComparison.OrdinalIgnoreCase) ? "ollama"
        : Url.Contains("models.github.ai", StringComparison.OrdinalIgnoreCase)
          || Url.Contains("copilot", StringComparison.OrdinalIgnoreCase) ? "copilot"
        : "openai";
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
}
