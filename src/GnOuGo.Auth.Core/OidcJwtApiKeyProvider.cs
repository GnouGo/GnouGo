using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GnOuGo.Auth.Core;

public sealed class OidcJwtApiKeyProvider : IApiKeyProvider
{
    private readonly HttpClient _http;
    private readonly OidcClientCredentialsConfig _cfg;
    private readonly TimeProvider _clock;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public OidcJwtApiKeyProvider(HttpClient http, OidcClientCredentialsConfig cfg)
        : this(http, cfg, TimeProvider.System)
    {
    }

    public OidcJwtApiKeyProvider(HttpClient http, OidcClientCredentialsConfig cfg, TimeProvider clock)
    {
        _http = http;
        _cfg = cfg;
        _clock = clock;
    }

    public async ValueTask<string> GetApiKeyAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_token) && _clock.GetUtcNow() < _expiresAtUtc)
            return _token!;

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) && _clock.GetUtcNow() < _expiresAtUtc)
                return _token!;

            await RefreshAsync(ct);
            return _token!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var issuer = _cfg.Issuer?.TrimEnd('/') ?? "";
        if (string.IsNullOrWhiteSpace(issuer))
            throw new InvalidOperationException("OIDC issuer is required.");

        var clientId = _cfg.ClientId ?? "";
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("OIDC client_id is required.");

        if (string.IsNullOrWhiteSpace(_cfg.Scopes))
            throw new InvalidOperationException("OIDC scopes are required.");

        if (string.IsNullOrWhiteSpace(_cfg.ClientSecret) && string.IsNullOrWhiteSpace(_cfg.PrivateKeyPem))
            throw new InvalidOperationException("Either OIDC client_secret or private_key must be provided.");

        // ---- 1) Discovery
        var discoUrl = $"{issuer}/.well-known/openid-configuration";
        using var disco = await _http.GetAsync(discoUrl, ct);
        disco.EnsureSuccessStatusCode();

        await using var discoStream = await disco.Content.ReadAsStreamAsync(ct);
        using var discoJson = await JsonDocument.ParseAsync(discoStream, cancellationToken: ct);

        if (!discoJson.RootElement.TryGetProperty("token_endpoint", out var te) || te.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("OIDC discovery doc missing token_endpoint.");

        var tokenEndpoint = te.GetString()!;
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
            throw new InvalidOperationException("OIDC token_endpoint is empty.");

        // ---- 2) Token request (client_credentials)
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("scope", _cfg.Scopes),
            // some IdPs require it even with basic auth
            new("client_id", clientId),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        // Auth mode
        if (!string.IsNullOrWhiteSpace(_cfg.ClientSecret))
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{_cfg.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
        else
        {
            // private_key => client_assertion (JWT)
            var assertion = JwtClientAssertion.CreateRs256(clientId, tokenEndpoint, _cfg.PrivateKeyPem!);

            var fullForm = new List<KeyValuePair<string, string>>(form)
            {
                new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                new("client_assertion", assertion),
            };

            req.Content = new FormUrlEncodedContent(fullForm);
        }

        var requestedAt = _clock.GetUtcNow();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OIDC token request failed: HTTP {(int)resp.StatusCode}.");

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);

        if (!json.RootElement.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("OIDC token response missing access_token.");

        var accessToken = at.GetString()!;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("OIDC access_token is empty.");

        int expiresIn = 3600;
        if (json.RootElement.TryGetProperty("expires_in", out var ei))
        {
            if (ei.ValueKind != JsonValueKind.Number || !ei.TryGetInt32(out expiresIn) || expiresIn <= 0)
                throw new InvalidOperationException("OIDC token response has an invalid expires_in.");
        }

        // Never extend an issuer's lifetime. A proportional margin also lets short-lived
        // tokens be cached, and acquisition time counts against their usable lifetime.
        var refreshAt = requestedAt.AddSeconds(expiresIn - Math.Min(30d, expiresIn * 0.1d));
        if (_clock.GetUtcNow() >= refreshAt)
            throw new InvalidOperationException("OIDC token expired during acquisition.");
        _token = accessToken;
        _expiresAtUtc = refreshAt;
    }
}
