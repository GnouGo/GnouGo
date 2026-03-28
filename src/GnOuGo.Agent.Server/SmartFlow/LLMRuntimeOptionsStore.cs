using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Singleton that holds the live <see cref="LLMOptions"/> and allows runtime updates
/// (e.g. from the /llm configure wizard) without restarting the server.
/// Changes are persisted to <c>user-settings.json</c> in the GnOuGo.Agent app-data folder
/// so they survive restarts.
/// </summary>
public sealed class LLMRuntimeOptionsStore
{
    private readonly ILogger<LLMRuntimeOptionsStore> _logger;
    private readonly string _settingsPath;
    private LLMOptions _current;
    private readonly object _lock = new();

    public LLMRuntimeOptionsStore(
        IOptions<LLMOptions> initialOptions,
        ILogger<LLMRuntimeOptionsStore> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GnOuGo.Agent",
            "user-settings.json");

        // Start from appsettings, then overlay persisted user settings
        _current = DeepClone(initialOptions.Value);
        LoadPersisted();
    }

    /// <summary>Gets the current (live) LLM options.</summary>
    public LLMOptions Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Updates (or adds) a named provider and persists the change.
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

            // Update the default model if this is the default provider
            if (string.Equals(opts.DefaultProvider, providerKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(model))
            {
                opts.DefaultModel = model;
            }

            // Use the existing key casing if found, otherwise use the provided key
            var finalKey = existingKey ?? providerKey;
            opts.Models[finalKey] = existing;
            _current = opts;
        }

        Persist();
        _logger.LogInformation("LLM provider '{Provider}' updated at runtime.", providerKey);
    }

    // ── persistence ────────────────────────────────────────────────

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
            var node = JsonNode.Parse(json);
            var llmNode = node?["LLM"];
            if (llmNode is null) return;

            var persisted = llmNode.Deserialize<LLMOptions>();
            if (persisted is null) return;

            // Overlay: persisted values win over appsettings defaults
            // Use case-insensitive matching to avoid creating duplicate entries
            lock (_lock)
            {
                foreach (var kv in persisted.Models)
                {
                    // Find existing entry by case-insensitive match
                    string? existingKey = null;
                    foreach (var existing in _current.Models)
                    {
                        if (string.Equals(existing.Key, kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            existingKey = existing.Key;
                            break;
                        }
                    }
                    
                    // Use existing key casing if found (from appsettings), otherwise use persisted key
                    var finalKey = existingKey ?? kv.Key;
                    _current.Models[finalKey] = kv.Value;
                }
                if (!string.IsNullOrWhiteSpace(persisted.DefaultProvider))
                    _current.DefaultProvider = persisted.DefaultProvider;
                if (!string.IsNullOrWhiteSpace(persisted.DefaultModel))
                    _current.DefaultModel = persisted.DefaultModel;
            }

            _logger.LogInformation("Loaded persisted LLM settings from {Path}.", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persisted LLM settings from {Path}.", _settingsPath);
        }
    }

    private void Persist()
    {
        try
        {
            LLMOptions snapshot;
            lock (_lock) snapshot = DeepClone(_current);

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

            // Write as { "LLM": { ... } } so it can be layered on top of appsettings.json
            var wrapper = new JsonObject
            {
                ["LLM"] = JsonSerializer.SerializeToNode(snapshot)
            };

            File.WriteAllText(_settingsPath, wrapper.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist LLM settings to {Path}.", _settingsPath);
        }
    }

    private static LLMOptions DeepClone(LLMOptions src)
    {
        // Safe shallow clone — Dictionary re-created, nested objects re-created
        var clone = new LLMOptions
        {
            DefaultProvider = src.DefaultProvider,
            DefaultModel = src.DefaultModel,
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
            clone.McpServers[kv.Key] = kv.Value;
        return clone;
    }
}

