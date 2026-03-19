using GnOuGo.Auth.Core;
using Xunit;

namespace DocIngestor.Tests;

public sealed class AuthTests
{
    // ── StaticApiKeyProvider ─────────────────────────────────────────

    [Fact]
    public async Task StaticApiKeyProvider_ReturnsKey()
    {
        var provider = new StaticApiKeyProvider("my-secret-key");
        var key = await provider.GetApiKeyAsync();
        Assert.Equal("my-secret-key", key);
    }

    [Fact]
    public async Task StaticApiKeyProvider_ReturnsSameKeyEveryTime()
    {
        var provider = new StaticApiKeyProvider("key-123");

        var k1 = await provider.GetApiKeyAsync();
        var k2 = await provider.GetApiKeyAsync();

        Assert.Equal(k1, k2);
    }

    [Fact]
    public void StaticApiKeyProvider_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StaticApiKeyProvider(null!));
    }

    [Fact]
    public async Task StaticApiKeyProvider_SupportsCancellation()
    {
        var provider = new StaticApiKeyProvider("key");
        using var cts = new CancellationTokenSource();
        var key = await provider.GetApiKeyAsync(cts.Token);
        Assert.Equal("key", key);
    }
}

