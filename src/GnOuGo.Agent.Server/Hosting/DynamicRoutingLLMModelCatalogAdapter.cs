using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
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
