using GnOuGo.Browser.Mcp;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public class BrowserNavigationPolicyTests
{
    [Theory]
    [InlineData("https%3A%2F%2Fexample.invalid%2Fitems%2F7")]
    [InlineData("/items/7")]
    [InlineData("https://example.invalid/item 7")]
    [InlineData("https://example.invalid/items/7\n")]
    public void InvalidReferencesFailBeforeNavigation(string reference)
        => Assert.True(Record.Exception(() => BrowserNavigationPolicy.ValidateNavigationTarget(reference, new())) is ArgumentException or InvalidOperationException);

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
