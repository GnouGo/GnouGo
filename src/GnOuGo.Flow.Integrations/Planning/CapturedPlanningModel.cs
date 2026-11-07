using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations.Planning;

/// <summary>Credential-free creation capture. Replay journals remain authoritative for issued work.</summary>
public static class CapturedPlanningModel
{
    public const string OptionName = "captured_model_metadata";

    public static LLMModelMetadata Capture(PlanningRequest planning, LLMOptions options)
    {
        var provider = planning.Options["generator"]?["provider"]?.GetValue<string>() ?? options.DefaultProvider;
        var model = planning.Options["generator"]?["model"]?.GetValue<string>() ?? options.DefaultModel;
        var route = options.ResolveProvider(provider) ?? throw new PlanningConflictException("The configured model provider is unavailable.");
        planning.Options["generator"] = new JsonObject { ["provider"] = provider, ["model"] = model };
        var metadata = new RoutingLLMClient(options, []).ResolveDeclaredMetadata(provider, model)
            ?? throw new PlanningConflictException("Exact model/deployment metadata is unavailable. Configure an explicit deployment override; no public model is inferred from its name.");
        if (metadata.MaxInputTokens is not > 0 || metadata.ContextWindowTokens is not > 0 || metadata.MaxOutputTokens is not > 0 ||
            metadata.Capabilities.SupportsStructuredOutput != true || !PlanningApplicationBudget.Unlimited(planning) &&
            (metadata.Pricing?.InputPer1MTokens is null || metadata.Pricing.OutputPer1MTokens is null))
            throw new PlanningConflictException("Model limits, structured output or pricing cannot be verified before session creation.");
        planning.Options[OptionName] = new JsonObject
        {
            ["version"] = 2, ["provider"] = provider, ["model"] = model, ["providerType"] = route.ResolvedType,
            ["routeHash"] = RouteHash(route),
            ["metadata"] = JsonSerializer.SerializeToNode(metadata, ModelMetadataJsonContext.Default.LLMModelMetadata),
            ["requestPolicy"] = JsonSerializer.SerializeToNode(route.RequestPolicy, ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions),
            ["transport"] = JsonSerializer.SerializeToNode(LLMTransportResolver.Resolve(model, route, false, metadata.Capabilities), StreamingJsonContext.Default.LLMTransportSelection),
            ["modelTransportOverrides"] = route.ModelTransportOverrides is null ? null : JsonSerializer.SerializeToNode(route.ModelTransportOverrides, StreamingJsonContext.Default.DictionaryStringLLMModelTransportOptions),
            ["retryPolicy"] = JsonSerializer.SerializeToNode(route.RetryPolicy, ModelMetadataJsonContext.Default.LLMProviderRetryPolicyOptions),
            ["capturedAtUtc"] = DateTimeOffset.UtcNow
        };
        return metadata;
    }

    public static LLMOptions Restore(LLMOptions current, PlanningRequest? planning)
    {
        if (planning?.Options[OptionName] is not JsonObject capture) return current;
        var provider = capture["provider"]!.GetValue<string>();
        var model = capture["model"]!.GetValue<string>();
        var route = current.ResolveProvider(provider) ?? throw new PlanningConflictException("The captured provider is unavailable.");
        var version = capture["version"]?.GetValue<int>();
        if (version is not (1 or 2) || capture["routeHash"]?.ToString() != RouteHash(route) ||
            !JsonNode.DeepEquals(WithoutTransport(capture["requestPolicy"]), WithoutTransport(JsonSerializer.SerializeToNode(route.RequestPolicy, ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions))) ||
            !JsonNode.DeepEquals(capture["retryPolicy"], JsonSerializer.SerializeToNode(route.RetryPolicy, ModelMetadataJsonContext.Default.LLMProviderRetryPolicyOptions)))
            throw new PlanningConflictException("The provider route or request policy changed since session creation. The captured request cannot use the new deployment.");
        var metadata = capture["metadata"]!.Deserialize(ModelMetadataJsonContext.Default.LLMModelMetadata)!;
        var transport = version == 1 ? new LLMTransportSelection(LLMStreamingMode.NonStreaming, LLMStreamingMode.NonStreaming,
            LLMTransportResolver.Protocol(route, false), "historical non-streaming capture")
            : capture["transport"]?.Deserialize(StreamingJsonContext.Default.LLMTransportSelection)
                ?? throw new PlanningConflictException("The captured generation transport is unavailable.");
        var frozenRoute = new ModelProviderOptions
        {
            Type = route.Type, Url = route.Url, ApiVersion = route.ApiVersion, ApiKey = route.ApiKey,
            Issuer = route.Issuer, ClientId = route.ClientId, ClientSecret = route.ClientSecret, PrivateKeyPem = route.PrivateKeyPem, Scopes = route.Scopes,
            RequestPolicy = capture["requestPolicy"]!.Deserialize(ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions)!,
            ModelTransportOverrides = capture["modelTransportOverrides"]?.Deserialize(StreamingJsonContext.Default.DictionaryStringLLMModelTransportOptions),
            RetryPolicy = capture["retryPolicy"]!.Deserialize(ModelMetadataJsonContext.Default.LLMProviderRetryPolicyOptions)!
        };
        if (version == 1) frozenRoute.RequestPolicy.StreamingMode = LLMStreamingMode.NonStreaming;
        else if (transport != LLMTransportResolver.Resolve(model, frozenRoute, false, metadata.Capabilities))
            throw new PlanningConflictException("The captured generation transport does not match its captured capabilities and policy.");
        return new LLMOptions
        {
            DefaultProvider = provider, DefaultModel = model, Models = new(StringComparer.OrdinalIgnoreCase) { [provider] = frozenRoute }, McpServers = current.McpServers,
            DangerousAcceptAnyServerCertificate = current.DangerousAcceptAnyServerCertificate,
            CapturedTransports = new(StringComparer.OrdinalIgnoreCase) { [ModelMetadataCatalog.ModelIdentityKey(route.ResolvedType, model)] = transport },
            CapturedModelMetadata = new(StringComparer.OrdinalIgnoreCase) { [ModelMetadataCatalog.ModelIdentityKey(route.ResolvedType, RoutingLLMClient.NormalizeModel(model))] = metadata }
        };
    }

    private static JsonNode? WithoutTransport(JsonNode? policy)
    {
        var copy = policy?.DeepClone();
        if (copy is JsonObject value) { value.Remove("streamingMode"); value.Remove("streamingSupport"); }
        return copy;
    }

    private static string RouteHash(ModelProviderOptions provider) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider.ResolvedType + "\n" + provider.Url + "\n" + provider.ApiVersion)));

    public static ILLMCapabilityResolver Capabilities(LLMOptions options) => new Resolver(options);
    private sealed class Resolver(LLMOptions options) : ILLMCapabilityResolver
    {
        private LLMModelMetadata? Resolve(string? provider, string model) => new RoutingLLMClient(options, []).ResolveDeclaredMetadata(provider, model);
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var metadata = Resolve(provider, model);
            return Task.FromResult(metadata?.MaxInputTokens is > 0 && metadata.ContextWindowTokens is > 0 && metadata.MaxOutputTokens >= outputTokens
                ? (int?)Math.Max(0, Math.Min(metadata.MaxInputTokens.Value, metadata.ContextWindowTokens.Value - outputTokens)) : null);
        }
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult(Resolve(provider, model)?.Capabilities.SupportsStructuredOutput);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(Resolve(provider, model)?.Capabilities.SupportedReasoningEfforts);
    }
}
