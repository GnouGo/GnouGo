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

    public DynamicRoutingLLMModelCatalogAdapter(HttpClient http, LLMRuntimeOptionsStore store, ILoggerFactory loggerFactory)
    {
        _http = http;
        _store = store;
        _loggerFactory = loggerFactory;
    }

    public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
    {
        var catalog = new RoutingLLMModelCatalog(_http, _store.Current, _loggerFactory);
        return catalog.ListModelsAsync(provider, ct);
    }
}

