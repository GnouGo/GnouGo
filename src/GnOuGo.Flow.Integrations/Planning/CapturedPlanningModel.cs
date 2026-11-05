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
            metadata.Capabilities.SupportsStructuredOutput != true || metadata.Pricing?.InputPer1MTokens is null || metadata.Pricing.OutputPer1MTokens is null)
            throw new PlanningConflictException("Model limits, structured output or pricing cannot be verified before session creation.");
        planning.Options[OptionName] = new JsonObject
        {
            ["version"] = 1, ["provider"] = provider, ["model"] = model, ["providerType"] = route.ResolvedType,
            ["routeHash"] = RouteHash(route),
            ["metadata"] = JsonSerializer.SerializeToNode(metadata, ModelMetadataJsonContext.Default.LLMModelMetadata),
            ["requestPolicy"] = JsonSerializer.SerializeToNode(route.RequestPolicy, ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions),
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
        if (capture["version"]?.GetValue<int>() != 1 || capture["routeHash"]?.ToString() != RouteHash(route) ||
            !JsonNode.DeepEquals(capture["requestPolicy"], JsonSerializer.SerializeToNode(route.RequestPolicy, ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions)) ||
            !JsonNode.DeepEquals(capture["retryPolicy"], JsonSerializer.SerializeToNode(route.RetryPolicy, ModelMetadataJsonContext.Default.LLMProviderRetryPolicyOptions)))
            throw new PlanningConflictException("The provider route or request policy changed since session creation. The captured request cannot use the new deployment.");
        var metadata = capture["metadata"]!.Deserialize(ModelMetadataJsonContext.Default.LLMModelMetadata)!;
        var frozenRoute = new ModelProviderOptions
        {
            Type = route.Type, Url = route.Url, ApiVersion = route.ApiVersion, ApiKey = route.ApiKey,
            Issuer = route.Issuer, ClientId = route.ClientId, ClientSecret = route.ClientSecret, PrivateKeyPem = route.PrivateKeyPem, Scopes = route.Scopes,
            RequestPolicy = capture["requestPolicy"]!.Deserialize(ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions)!,
            RetryPolicy = capture["retryPolicy"]!.Deserialize(ModelMetadataJsonContext.Default.LLMProviderRetryPolicyOptions)!
        };
        return new LLMOptions
        {
            DefaultProvider = provider, DefaultModel = model, Models = new(StringComparer.OrdinalIgnoreCase) { [provider] = frozenRoute }, McpServers = current.McpServers,
            DangerousAcceptAnyServerCertificate = current.DangerousAcceptAnyServerCertificate,
            CapturedModelMetadata = new(StringComparer.OrdinalIgnoreCase) { [ModelMetadataCatalog.ModelIdentityKey(route.ResolvedType, model)] = metadata }
        };
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
