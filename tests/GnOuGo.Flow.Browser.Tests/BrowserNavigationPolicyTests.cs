using GnOuGo.Flow.Browser;
using Xunit;

namespace GnOuGo.Flow.Browser.Tests;

public class BrowserNavigationPolicyTests
{
    [Fact]
    public void ValidateNavigationTarget_AllowsHttps_WhenNoHostRestrictions()
    {
        var settings = new BrowserServerSettings();

        var uri = BrowserNavigationPolicy.ValidateNavigationTarget("https://example.com/docs", settings);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("example.com", uri.Host);
    }

    [Fact]
    public void ValidateNavigationTarget_RejectsNonHttpScheme()
    {
        var settings = new BrowserServerSettings();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BrowserNavigationPolicy.ValidateNavigationTarget("file:///c:/temp/test.html", settings));

        Assert.Contains("http:// and https://", ex.Message);
    }

    [Fact]
    public void ValidateNavigationTarget_RejectsHostOutsideWhitelist()
    {
        var settings = new BrowserServerSettings
        {
            AllowedHosts = ["example.com"]
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BrowserNavigationPolicy.ValidateNavigationTarget("https://contoso.com", settings));

        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public void IsHostAllowed_AllowsWildcardSubdomainsOnly()
    {
        Assert.True(BrowserNavigationPolicy.IsHostAllowed("docs.example.com", ["*.example.com"]));
        Assert.False(BrowserNavigationPolicy.IsHostAllowed("example.com", ["*.example.com"]));
    }
}

