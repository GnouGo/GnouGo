using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server.Endpoints;

public static class LlmProviderEndpoints
{
    public static IResult ListProviders(LLMRuntimeOptionsStore store)
    {
        var current = store.Current;
        var providers = current.Models
            .Select(kv => new LlmConfiguredProviderDto(
                Key: kv.Key,
                ProviderType: kv.Value.ResolvedType,
                Url: kv.Value.Url,
                DefaultModel: string.Equals(current.DefaultProvider, kv.Key, StringComparison.OrdinalIgnoreCase)
                    ? current.DefaultModel
                    : null,
                IsDefault: string.Equals(current.DefaultProvider, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(providers);
    }

    public static async Task<IResult> ListModelsAsync(
        string provider,
        ILLMModelCatalog modelCatalog,
        LLMRuntimeOptionsStore store,
        CancellationToken ct)
    {
        var resolvedProvider = ResolveProviderKey(store.Current, provider);
        if (resolvedProvider is null)
            return Results.NotFound($"LLM provider '{provider}' is not configured.");

        try
        {
            var providerOptions = store.Current.ResolveProvider(resolvedProvider)
                ?? throw new InvalidOperationException($"LLM provider '{resolvedProvider}' is not configured.");

            var models = await modelCatalog.ListModelsAsync(resolvedProvider, ct);
            var response = new LlmProviderModelsDto(
                Provider: resolvedProvider,
                ProviderType: providerOptions.ResolvedType,
                Models: models
                    .Select(m => new LlmModelDto(m.Id, m.DisplayName, m.ProviderType, m.OwnedBy))
                    .ToList());

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    private static string? ResolveProviderKey(LLMOptions options, string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        foreach (var key in options.Models.Keys)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return null;
    }
}

