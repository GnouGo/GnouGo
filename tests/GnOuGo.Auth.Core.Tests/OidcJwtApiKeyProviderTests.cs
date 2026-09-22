using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GnOuGo.Auth.Core;

namespace GnOuGo.Auth.Core.Tests;

public sealed class OidcJwtApiKeyProviderTests
{
    [Fact]
    public async Task ConcurrentCallsShareTokenAndRefreshBeforeActualShortExpiry()
    {
        var clock = new Clock();
        var tokens = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("{\"token_endpoint\":\"https://issuer.test/token\"}");
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("client:secret", Encoding.ASCII.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
            Assert.Contains("grant_type=client_credentials", await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            await Task.Yield();
            return Json($"{{\"access_token\":\"token-{Interlocked.Increment(ref tokens)}\",\"expires_in\":10}}");
        }));
        var provider = new OidcJwtApiKeyProvider(http, new("https://issuer.test", "client", "models.read", "secret"), clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => provider.GetApiKeyAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.All(results, token => Assert.Equal("token-1", token)); Assert.Equal(1, tokens);
        clock.Advance(TimeSpan.FromSeconds(8)); Assert.Equal("token-1", await provider.GetApiKeyAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(2)); Assert.Equal("token-2", await provider.GetApiKeyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, tokens);
    }

    [Fact]
    public async Task PrivateKeyAssertionHasValidSignatureClaimsAndFreshJti()
    {
        using var rsa = RSA.Create(2048);
        var clock = new Clock();
        var jtis = new HashSet<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("{\"token_endpoint\":\"https://issuer.test/token\"}");
            Assert.Null(request.Headers.Authorization);
            var form = (await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
            Assert.Equal("client_credentials", form["grant_type"]);
            var parts = form["client_assertion"].Split('.');
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var claims = JsonDocument.Parse(Decode(parts[1]));
            Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
            Assert.Equal("client", claims.RootElement.GetProperty("iss").GetString());
            Assert.Equal("client", claims.RootElement.GetProperty("sub").GetString());
            Assert.Equal("https://issuer.test/token", claims.RootElement.GetProperty("aud").GetString());
            Assert.Equal(300, claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64());
            Assert.True(jtis.Add(claims.RootElement.GetProperty("jti").GetString()!));
            return Json("{\"access_token\":\"signed-token\",\"expires_in\":10}");
        }));
        var provider = new OidcJwtApiKeyProvider(http, new("https://issuer.test", "client", "scope", PrivateKeyPem: rsa.ExportRSAPrivateKeyPem()), clock);
        Assert.Equal("signed-token", await provider.GetApiKeyAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(10)); await provider.GetApiKeyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, jtis.Count);
    }

    [Fact]
    public async Task FailedTokenResponseDoesNotExposeBodyOrReasonPhrase()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("{\"token_endpoint\":\"https://issuer.test/token\"}")
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("secret private PEM access_token"), ReasonPhrase = "sensitive reason" })));
        var provider = new OidcJwtApiKeyProvider(http, new("https://issuer.test", "client", "scope", "secret"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetApiKeyAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("OIDC token request failed: HTTP 400.", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"10\"")]
    [InlineData("null")]
    public async Task InvalidLifetimeFailsClosed(string expiry)
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("{\"token_endpoint\":\"https://issuer.test/token\"}") : Json($"{{\"access_token\":\"token\",\"expires_in\":{expiry}}}"))));
        var provider = new OidcJwtApiKeyProvider(http, new("https://issuer.test", "client", "scope", "secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetApiKeyAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task AcquisitionTimeCountsAgainstTokenLifetime()
    {
        var clock = new Clock();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"token_endpoint\":\"https://issuer.test/token\"}"));
            clock.Advance(TimeSpan.FromSeconds(12));
            return Task.FromResult(Json("{\"access_token\":\"expired\",\"expires_in\":10}"));
        }));
        var provider = new OidcJwtApiKeyProvider(http, new("https://issuer.test", "client", "scope", "secret"), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetApiKeyAsync(TestContext.Current.CancellationToken).AsTask());
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
