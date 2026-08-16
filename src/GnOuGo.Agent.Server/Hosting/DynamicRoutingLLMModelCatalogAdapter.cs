using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Hosting;

/// <summary>
/// Resolves the latest <see cref="LLMOptions"/> from <see cref="LLMRuntimeOptionsStore"/>
/// on every model-catalog request so provider updates are immediately visible.
/// </summary>
internal sealed class DynamicRoutingLLMModelCatalogAdapter : ILLMModelCatalog
{
    private readonly HttpClient _http;
    private readonly LLMRuntimeOptionsStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILLMModelCatalogProvider? _localCatalog;

    public DynamicRoutingLLMModelCatalogAdapter(
        HttpClient http,
        LLMRuntimeOptionsStore store,
        ILoggerFactory loggerFactory,
        ILLMModelCatalogProvider? localCatalog = null)
    {
        _http = http;
        _store = store;
        _loggerFactory = loggerFactory;
        _localCatalog = localCatalog;
    }

    public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
    {
        var providers = RoutingLLMModelCatalog.CreateDefaultProviders(_http, _loggerFactory).AsEnumerable();
        if (_localCatalog is not null)
            providers = providers.Append(_localCatalog);
        var catalog = new RoutingLLMModelCatalog(_store.Current, providers);
        return catalog.ListModelsAsync(provider, ct);
    }
}

internal sealed class FlowLlmCapabilityResolver : ILLMCapabilityResolver
{
    private readonly ILLMModelCatalog _modelCatalog;
    private readonly LLMRuntimeOptionsStore _store;
    private readonly ILogger<FlowLlmCapabilityResolver> _logger;

    public FlowLlmCapabilityResolver(
        ILLMModelCatalog modelCatalog,
        LLMRuntimeOptionsStore store,
        ILogger<FlowLlmCapabilityResolver> logger)
    {
        _modelCatalog = modelCatalog;
        _store = store;
        _logger = logger;
    }

    public async Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
    {
        var resolvedProvider = string.IsNullOrWhiteSpace(provider)
            ? _store.Current.DefaultProvider
            : provider;
        var resolvedModel = string.IsNullOrWhiteSpace(model)
            ? _store.Current.DefaultModel
            : model;

        if (string.IsNullOrWhiteSpace(resolvedProvider) || string.IsNullOrWhiteSpace(resolvedModel))
            return null;

        try
        {
            var models = await _modelCatalog.ListModelsAsync(resolvedProvider, ct);
            var descriptor = models.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, resolvedModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.DisplayName, resolvedModel, StringComparison.OrdinalIgnoreCase));
            return descriptor?.Capabilities?.SupportsStructuredOutput;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to resolve structured-output capability for provider '{Provider}' model '{Model}'",
                resolvedProvider,
                resolvedModel);
            return null;
        }
    }
}
