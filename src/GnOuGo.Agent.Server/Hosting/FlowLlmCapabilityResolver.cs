using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Hosting;

/// <summary>Reads declared model capabilities from the same live metadata used by the configuration editor.</summary>
internal sealed class FlowLlmCapabilityResolver(LLMRuntimeOptionsStore store) : ILLMCapabilityResolver
{
    public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct)
    {
        var capabilities = Resolve(provider, model, ct);
        return Task.FromResult<IReadOnlyList<string>?>(capabilities?.SupportsReasoningEffort switch
        {
            true => capabilities.SupportedReasoningEfforts?.ToArray(),
            false => [],
            _ => null
        });
    }

    public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
        => Task.FromResult(Resolve(provider, model, ct)?.SupportsStructuredOutput);

    private ModelCapabilityMetadata? Resolve(string? provider, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var options = store.Current;
        var providerKey = string.IsNullOrWhiteSpace(provider) ? options.DefaultProvider : provider;
        var modelId = string.IsNullOrWhiteSpace(model) ? options.DefaultModel : model;
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(modelId)) return null;
        // Reuse dispatch's provider/model normalization without constructing any transport.
        return new RoutingLLMClient(options, []).ResolveDeclaredCapabilities(providerKey, modelId);
    }
}
