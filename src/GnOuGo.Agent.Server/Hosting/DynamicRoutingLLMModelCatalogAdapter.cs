using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Hosting;

/// <summary>
/// Resolves the latest <see cref="LLMOptions"/> from <see cref="LLMRuntimeOptionsStore"/>
/// on every model-catalog request so provider updates are immediately visible.
/// </summary>
internal sealed class DynamicRoutingLLMModelCatalogAdapter : ILLMModelCatalog
{
    private readonly HttpClient _http;
    private readonly LLMRuntimeOptionsStore _store;

    public DynamicRoutingLLMModelCatalogAdapter(HttpClient http, LLMRuntimeOptionsStore store)
    {
        _http = http;
        _store = store;
    }

    public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
    {
        var catalog = new RoutingLLMModelCatalog(_http, _store.Current);
        return catalog.ListModelsAsync(provider, ct);
    }
}

