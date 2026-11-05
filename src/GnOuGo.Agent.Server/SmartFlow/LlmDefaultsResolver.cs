using GnOuGo.Agent.Mcp.Services;
using GnOuGo.AI.Core;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>Publishes one effective selection before workers and the operator browser start.</summary>
public sealed class LlmDefaultsResolver(LLMRuntimeOptionsStore runtime, IKeyVaultRuntimeConfigStore keyVault,
    IServiceScopeFactory scopes, ILogger<LlmDefaultsResolver> logger)
{
    private readonly SemaphoreSlim _resolution = new(1, 1);
    private (string User, string Configured)? _ignoredModel;

    public async Task InitializeAsync(CancellationToken ct = default) => _ = await ResolveAsync(ct);

    public async Task<LLMOptions> ResolveAsync(CancellationToken ct = default)
    {
        await _resolution.WaitAsync(ct);
        try { return await ResolveCoreAsync(ct); }
        finally { _resolution.Release(); }
    }

    private async Task<LLMOptions> ResolveCoreAsync(CancellationToken ct)
    {
        var initial = runtime.Snapshot();
        initial.DefaultProvider = runtime.HostDefaults.Provider;
        initial.DefaultModel = runtime.HostDefaults.Model;
        var effective = await keyVault.BuildEffectiveOptionsAsync(initial, ct);
        UserConfigSnapshot? user = null;
        try
        {
            using var scope = scopes.CreateScope();
            user = await scope.ServiceProvider.GetRequiredService<IUserConfigRepository>().GetAsync(ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Persisted user defaults are unavailable ({ErrorType}); configured provider defaults remain effective.", ex.GetType().Name);
        }

        if (user?.ModelOverrides is { } overrides)
            foreach (var (id, metadata) in overrides)
                effective.ModelOverrides[id] = GnOuGo.AI.Core.ModelMetadataCatalog.Clone(metadata);

        var userProvider = effective.Models.Keys.FirstOrDefault(key =>
            string.Equals(key, user?.DefaultLlmProvider, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(user?.DefaultLlmProvider) && userProvider is null)
            logger.LogWarning("The persisted default provider is not configured; the configured provider selection remains effective.");

        if (userProvider is not null) effective.DefaultProvider = userProvider;
        var configuredModel = await keyVault.GetConfiguredModelAsync(effective.DefaultProvider, ct);
        var userModel = userProvider is not null && !string.IsNullOrWhiteSpace(user?.DefaultLlmModel) ? user.DefaultLlmModel : null;
        var modelSource = "host_configuration";
        if (configuredModel is not null)
        {
            effective.DefaultModel = configuredModel;
            modelSource = "keyvault_provider_configuration";
            if (userModel is not null && !string.Equals(userModel, configuredModel, StringComparison.Ordinal) && _ignoredModel != (userModel, configuredModel))
            {
                logger.LogWarning("The persisted user model differs from the configured provider model; KeyVault provider configuration takes precedence.");
                _ignoredModel = (userModel, configuredModel);
            }
        }
        else if (userModel is not null)
        {
            effective.DefaultModel = userModel;
            modelSource = "persisted_user_default";
        }
        else
        {
            // A different provider cannot inherit another provider's model identifier.
            effective.DefaultModel = string.Equals(effective.DefaultProvider, runtime.HostDefaults.Provider, StringComparison.OrdinalIgnoreCase)
                ? runtime.HostDefaults.Model : "";
        }
        runtime.ReplaceRuntimeOptions(effective, modelSource);
        return runtime.Snapshot();
    }
}
