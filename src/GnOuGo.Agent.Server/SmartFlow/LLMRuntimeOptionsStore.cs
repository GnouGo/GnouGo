using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Singleton that holds the live <see cref="LLMOptions"/> and allows runtime updates
/// (e.g. from the /llm configure wizard) without restarting the server.
/// Provider and MCP server definitions are hydrated from the trusted KeyVault store.
/// Default provider/model selection is hydrated separately from the Agent MCP
/// user-config persistence layer.
/// </summary>
public sealed class LLMRuntimeOptionsStore
{
    private readonly ILogger<LLMRuntimeOptionsStore> _logger;
    private LLMOptions _current;
    private readonly object _lock = new();
    private readonly HashSet<string> _transientMcpServers = new(StringComparer.OrdinalIgnoreCase);

    public LLMRuntimeOptionsStore(
        IOptions<LLMOptions> initialOptions,
        ILogger<LLMRuntimeOptionsStore> logger)
    {
        _logger = logger;
        _current = DeepClone(initialOptions.Value);
    }

    /// <summary>Gets the current (live) LLM options.</summary>
    public LLMOptions Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Replaces the live runtime options snapshot.
    /// </summary>
    public void ReplaceRuntimeOptions(LLMOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            _current = DeepClone(options);
        }

        _logger.LogInformation(
            "LLM runtime options reloaded with {ProviderCount} provider(s) and {McpServerCount} MCP server(s).",
            options.Models.Count,
            options.McpServers.Count);
    }

    /// <summary>
    /// Updates (or adds) a named provider in memory.
    /// </summary>
    public void UpdateProvider(
        string providerKey,
        string url,
        string model,
        string? apiKey,
        string? authType = null,
        string? oidcIssuer = null,
        string? oidcClientId = null,
        string? oidcScopes = null,
        string? oidcClientSecret = null)
    {
        lock (_lock)
        {
            var opts = DeepClone(_current);

            // Find existing entry using case-insensitive match to avoid creating duplicates
            string? existingKey = null;
            ModelProviderOptions? existing = null;
            foreach (var kv in opts.Models)
            {
                if (string.Equals(kv.Key, providerKey, StringComparison.OrdinalIgnoreCase))
                {
                    existingKey = kv.Key;
                    existing = kv.Value;
                    break;
                }
            }

            if (existing is null)
                existing = new ModelProviderOptions();

            existing.Url = url;
            if (!string.IsNullOrWhiteSpace(apiKey))
                existing.ApiKey = apiKey;
            if (!string.IsNullOrWhiteSpace(authType))
                existing.Type = authType == "api_key" || authType == "none" || authType == "copilot_env"
                    ? existing.Type  // keep existing Type (openai/ollama/copilot)
                    : authType;
            if (!string.IsNullOrWhiteSpace(oidcIssuer)) existing.Issuer = oidcIssuer;
            if (!string.IsNullOrWhiteSpace(oidcClientId)) existing.ClientId = oidcClientId;
            if (!string.IsNullOrWhiteSpace(oidcScopes)) existing.Scopes = oidcScopes;
            if (!string.IsNullOrWhiteSpace(oidcClientSecret)) existing.ClientSecret = oidcClientSecret;

            // Use the existing key casing if found, otherwise use the provided key
            var finalKey = existingKey ?? providerKey;
            opts.Models[finalKey] = existing;

            var hasConfiguredDefault = opts.Models.Keys.Any(key =>
                string.Equals(key, opts.DefaultProvider, StringComparison.OrdinalIgnoreCase));

            if (!hasConfiguredDefault)
            {
                opts.DefaultProvider = finalKey;
                if (!string.IsNullOrWhiteSpace(model))
                    opts.DefaultModel = model;
            }
            else if (string.Equals(opts.DefaultProvider, finalKey, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(model))
            {
                opts.DefaultModel = model;
            }

            _current = opts;
        }
        _logger.LogInformation("LLM provider '{Provider}' updated at runtime.", providerKey);
    }

    /// <summary>
    /// Sets the default provider/model used by runtime workflow execution.
    /// </summary>
    public bool SetDefaultProvider(string providerKey, string? model)
    {
        string? resolvedProvider;

        lock (_lock)
        {
            var opts = DeepClone(_current);
            resolvedProvider = opts.Models.Keys.FirstOrDefault(key =>
                string.Equals(key, providerKey, StringComparison.OrdinalIgnoreCase));

            if (resolvedProvider is null)
                return false;

            opts.DefaultProvider = resolvedProvider;
            if (!string.IsNullOrWhiteSpace(model))
                opts.DefaultModel = model;

            _current = opts;
        }
        _logger.LogInformation(
            "LLM default provider set to '{Provider}' with model '{Model}'.",
            resolvedProvider,
            string.IsNullOrWhiteSpace(model) ? "<unchanged>" : model);
        return true;
    }

    /// <summary>
    /// Updates or inserts model metadata in memory without restarting the server.
    /// </summary>
    public void UpsertModelOverride(string modelId, LLMModelMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(metadata);

        lock (_lock)
        {
            var opts = DeepClone(_current);
            var clone = ModelMetadataCatalog.Clone(metadata);
            if (string.IsNullOrWhiteSpace(clone.Id))
                clone.Id = modelId.Trim();
            opts.ModelOverrides[modelId.Trim()] = clone;
            _current = opts;
        }

        _logger.LogInformation("LLM model metadata override '{ModelId}' updated at runtime.", modelId);
    }

    /// <summary>
    /// Removes a named provider from the live runtime options.
    /// </summary>
    public bool RemoveProvider(string providerKey)
    {
        lock (_lock)
        {
            var opts = DeepClone(_current);
            var existingKey = opts.Models.Keys.FirstOrDefault(key =>
                string.Equals(key, providerKey, StringComparison.OrdinalIgnoreCase));

            if (existingKey is null)
                return false;

            opts.Models.Remove(existingKey);

            if (string.Equals(opts.DefaultProvider, existingKey, StringComparison.OrdinalIgnoreCase))
            {
                var nextProvider = opts.Models.Keys
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                opts.DefaultProvider = nextProvider ?? string.Empty;
                opts.DefaultModel = string.Empty;
            }

            _current = opts;
        }
        _logger.LogInformation("LLM provider '{Provider}' removed from runtime options.", providerKey);
        return true;
    }

    /// <summary>
    /// Updates or inserts an MCP server in memory without persisting the change.
    /// Intended for runtime-only endpoint normalization such as loopback/self-hosted MCP mounts.
    /// </summary>
    public void UpsertTransientMcpServer(string serverName, McpServerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            var snapshot = DeepClone(_current);
            snapshot.McpServers[serverName] = CloneMcpServerOptions(options);
            _current = snapshot;
            _transientMcpServers.Add(serverName);
        }

        _logger.LogInformation(
            "Transient MCP server '{ServerName}' updated at runtime with transport '{Transport}' and url '{Url}'.",
            serverName,
            options.Type,
            options.Url);
    }

    private static LLMOptions DeepClone(LLMOptions src)
    {
        // Safe shallow clone — Dictionary re-created, nested objects re-created
        var clone = new LLMOptions
        {
            DefaultProvider = src.DefaultProvider,
            DefaultModel = src.DefaultModel,
            ModelMetadataFiles = [.. src.ModelMetadataFiles],
        };
        foreach (var kv in src.Models)
        {
            clone.Models[kv.Key] = new ModelProviderOptions
            {
                Url = kv.Value.Url,
                ApiKey = kv.Value.ApiKey,
                Type = kv.Value.Type,
                Issuer = kv.Value.Issuer,
                ClientId = kv.Value.ClientId,
                ClientSecret = kv.Value.ClientSecret,
                Scopes = kv.Value.Scopes,
            };
        }
        foreach (var kv in src.McpServers)
            clone.McpServers[kv.Key] = CloneMcpServerOptions(kv.Value);
        foreach (var kv in src.ModelOverrides)
            clone.ModelOverrides[kv.Key] = ModelMetadataCatalog.Clone(kv.Value);
        return clone;
    }

    private static McpServerOptions CloneMcpServerOptions(McpServerOptions source)
        => new()
        {
            Type = source.Type,
            Description = source.Description,
            DiscoveryTimeoutSeconds = source.DiscoveryTimeoutSeconds,
            CallTimeoutSeconds = source.CallTimeoutSeconds,
            Url = source.Url,
            ApiKey = source.ApiKey,
            Issuer = source.Issuer,
            ClientId = source.ClientId,
            ClientSecret = source.ClientSecret,
            Scopes = source.Scopes,
            Command = source.Command,
            Args = source.Args is null ? null : [.. source.Args]
        };
}

