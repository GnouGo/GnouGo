using System.Net.Http.Headers;
using GnOuGo.Auth.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;
using GnOuGo.ProxyCopilot.Server.Traffic;

namespace GnOuGo.ProxyCopilot.Server;

public interface IProxyAuthentication
{
    ValueTask Apply(HttpRequestMessage request, ModelRoute route, string callId, CancellationToken ct);
}

public sealed class ProxyAuthenticationService : IProxyAuthentication
{
    private readonly Dictionary<string, IApiKeyProvider> _credentials = new(StringComparer.Ordinal);
    private readonly ITrafficStore _traffic;

    public ProxyAuthenticationService(ProxyOptions options, HttpClient client, ITrafficStore traffic)
    {
        _traffic = traffic;
        foreach (var (name, provider) in options.Providers)
        {
            var connection = provider.Connection;
            IApiKeyProvider? credential = provider.Authentication switch
            {
                ProxyAuthentication.ApiKey => new StaticApiKeyProvider(connection.ApiKey!),
                ProxyAuthentication.CopilotEnvironment => new CopilotApiKeyProvider(),
                ProxyAuthentication.OidcClientSecret or ProxyAuthentication.OidcPrivateKey => new OidcJwtApiKeyProvider(client,
                    new OidcClientCredentialsConfig(connection.Issuer!, connection.ClientId!, connection.Scopes!, connection.ClientSecret, connection.PrivateKeyPem)),
                _ => null
            };
            if (credential is not null) _credentials.Add(name, credential);
        }
    }

    public async ValueTask Apply(HttpRequestMessage request, ModelRoute route, string callId, CancellationToken ct)
    {
        if (route.Type == "anthropic") request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!_credentials.TryGetValue(route.Provider, out var credential)) return;
        string token;
        try { token = await credential.GetApiKeyAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ProxyException(502, "upstream_authentication_failed", "Unable to authenticate with the configured upstream provider."); }
        _traffic.AddCredential(callId, token);
        if (route.Type == "anthropic" && route.Options.Authentication == ProxyAuthentication.ApiKey)
            request.Headers.TryAddWithoutValidation("x-api-key", token);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
