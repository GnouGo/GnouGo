using System.Security.Cryptography;
using GnOuGo.AI.Core;

namespace GnOuGo.ProxyCopilot.Server.Configuration;

public sealed class ProxyOptions
{
    public string TenantId { get; set; } = "local";
    public Dictionary<string, ProxyProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);
    public CaptureOptions Capture { get; set; } = new();
    public int MaxConcurrentRequests { get; set; } = 32;
    public int MaxRequestBytes { get; set; } = 2 * 1024 * 1024;
}

public sealed class CaptureOptions
{
    public int MaxCalls { get; set; } = 200;
    public int MaxBodyBytes { get; set; } = 256 * 1024;
    public int MaxTotalBytes { get; set; } = 64 * 1024 * 1024;
}

public enum ProxyAuthentication { None, ApiKey, OidcClientSecret, OidcPrivateKey, CopilotEnvironment }

public sealed class ProxyProviderOptions
{
    public ProxyAuthentication Authentication { get; set; }
    public ModelProviderOptions Connection { get; set; } = new()
    {
        RetryPolicy = new() { MaxAttempts = 1, MaxUncertainRetries = 0 },
        RequestPolicy = new() { BackgroundProtocol = LLMBackgroundProtocolMode.ChatCompletions }
    };
    public Dictionary<string, ProxyModelOptions> Models { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProxyModelOptions
{
    public string UpstreamId { get; set; } = "";
    public LLMModelMetadata Metadata { get; set; } = new();
}

public sealed record ModelRoute(string Id, string Provider, ProxyProviderOptions Options, ProxyModelOptions Model)
{
    public string Type => Options.Connection.ResolvedType;
    public bool SupportsTools => Model.Metadata.Capabilities.SupportsTools == true;
}

public interface IModelRegistry
{
    IReadOnlyList<ModelRoute> Models { get; }
    ModelRoute Resolve(string id);
}

public sealed class ModelRegistry : IModelRegistry
{
    public IReadOnlyList<ModelRoute> Models { get; }
    private readonly Dictionary<string, ModelRoute> _models;

    public ModelRegistry(ProxyOptions options)
    {
        Validate(options);
        Models = options.Providers.SelectMany(p => p.Value.Models.Select(m =>
            new ModelRoute($"{p.Key}/{m.Key}", p.Key, p.Value, m.Value))).ToArray();
        _models = Models.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    public ModelRoute Resolve(string id) => _models.TryGetValue(id, out var model) ? model
        : throw new ProxyException(404, "model_not_found", "The requested model is not configured.");

    public static void Validate(ProxyOptions options)
    {
        static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string field)
        {
            if (!valid) throw new InvalidOperationException($"Invalid ProxyCopilot configuration: {field}.");
        }
        Require(!string.IsNullOrWhiteSpace(options.TenantId), "TenantId");
        Require(options.MaxConcurrentRequests is > 0 and <= 1024, "MaxConcurrentRequests");
        Require(options.MaxRequestBytes is > 0 and <= 64 * 1024 * 1024, "MaxRequestBytes");
        Require(options.Capture is { MaxCalls: > 0 and <= 10000, MaxBodyBytes: > 0 and <= 4 * 1024 * 1024, MaxTotalBytes: > 0 and <= 512 * 1024 * 1024 }, "Capture limits");
        Require(options.Providers is not null, "Providers");
        var providerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, provider) in options.Providers!)
        {
            Require(IsAlias(name) && providerNames.Add(name), "provider alias");
            Require(provider?.Connection is not null && provider.Models is not null, "provider connection/models");
            var connection = provider!.Connection;
            Require(connection.Type is "openai" or "copilot" or "anthropic" or "ollama", "explicit provider Type");
            Require(IsEndpoint(connection.Url), "provider Url (HTTPS or loopback HTTP)");
            Require(Enum.IsDefined(provider.Authentication), "Authentication");
            var oidc = provider.Authentication is ProxyAuthentication.OidcClientSecret or ProxyAuthentication.OidcPrivateKey;
            Require(oidc || string.IsNullOrEmpty(connection.Issuer) && string.IsNullOrEmpty(connection.ClientId)
                && string.IsNullOrEmpty(connection.Scopes) && string.IsNullOrEmpty(connection.ClientSecret)
                && string.IsNullOrEmpty(connection.PrivateKeyPem), "unexpected OIDC fields");
            Require(provider.Authentication == ProxyAuthentication.ApiKey
                ? !string.IsNullOrWhiteSpace(connection.ApiKey) : string.IsNullOrEmpty(connection.ApiKey), "ApiKey authentication");
            if (oidc)
            {
                Require(IsEndpoint(connection.Issuer) && !string.IsNullOrWhiteSpace(connection.ClientId)
                    && !string.IsNullOrWhiteSpace(connection.Scopes), "OIDC issuer/client/scopes");
                Require(provider.Authentication == ProxyAuthentication.OidcClientSecret
                    ? !string.IsNullOrWhiteSpace(connection.ClientSecret) && string.IsNullOrEmpty(connection.PrivateKeyPem)
                    : !string.IsNullOrWhiteSpace(connection.PrivateKeyPem) && string.IsNullOrEmpty(connection.ClientSecret), "OIDC credential");
                if (provider.Authentication == ProxyAuthentication.OidcPrivateKey)
                {
                    try { using var key = RSA.Create(); key.ImportFromPem(connection.PrivateKeyPem); _ = key.ExportParameters(true); }
                    catch (Exception ex) when (ex is ArgumentException or CryptographicException)
                    { throw new InvalidOperationException("Invalid ProxyCopilot configuration: RSA private key."); }
                }
            }
            Require(provider.Authentication != ProxyAuthentication.CopilotEnvironment || connection.Type == "copilot", "CopilotEnvironment provider");
            // Core validation identifies fields without printing credential values.
            LLMOptionsValidation.ValidateProvider("configured provider", connection);
            Require(connection.RequestPolicy.BackgroundProtocol == LLMBackgroundProtocolMode.ChatCompletions, "only ChatCompletions execution is supported");
            Require(connection.RetryPolicy.MaxUncertainRetries == 0, "uncertain generation retries must be disabled");
            if (connection.Type == "anthropic")
                Require(connection.RequestPolicy.UnspecifiedOutputTokens == LLMUnspecifiedOutputTokensMode.Configured
                    && connection.RequestPolicy.DefaultMaxOutputTokens > 0, "Anthropic configured output-token default");
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (alias, model) in provider.Models!)
            {
                Require(IsAlias(alias) && aliases.Add(alias), "model alias");
                Require(model is not null && !string.IsNullOrWhiteSpace(model.UpstreamId) && model.Metadata?.Capabilities is not null, "model ID/metadata");
                var metadata = model!.Metadata;
                Require(metadata.MaxInputTokens > 0 && metadata.MaxOutputTokens > 0, "model input/output limits");
                Require(metadata.ContextWindowTokens is null or > 0, "context window");
                // Input and output are independent ceilings, not simultaneous defaults.
                Require(metadata.ContextWindowTokens is null || metadata.MaxInputTokens <= metadata.ContextWindowTokens
                    && metadata.MaxOutputTokens <= metadata.ContextWindowTokens, "context window capacity");
                var price = metadata.Pricing;
                Require(price is null || price.InputPer1MTokens is not < 0 && price.OutputPer1MTokens is not < 0
                    && price.CachedInputPer1MTokens is not < 0 && price.ReasoningOutputPer1MTokens is not < 0, "model pricing");
            }
        }
    }

    public static bool IsEndpoint(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0;

    private static bool IsAlias(string value) => value.Length is > 0 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
