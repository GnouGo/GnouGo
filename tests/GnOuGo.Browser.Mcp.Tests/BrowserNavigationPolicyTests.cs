using GnOuGo.Browser.Mcp;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public class BrowserNavigationPolicyTests
{
    [Fact]
    public void LongEscapedLiteralUrlRetainsItsExactAuthority()
    {
        var target = "https://example.com/approved?token=" + new string('a', 4_000) + "&value=%2F%2B";
        var settings = new BrowserServerSettings
        {
            AllowedHosts = ["example.com"],
            AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(target)]
        };

        Assert.Equal(target, BrowserNavigationPolicy.ValidateNavigationTarget(target, settings).AbsoluteUri);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("port")]
    [InlineData("path")]
    [InlineData("fragment")]
    public void LongLiteralUrlCannotAuthorizeAnotherSpelling(string difference)
    {
        var target = "https://example.com/approved?token=" + new string('a', 4_000);
        var reference = difference switch
        {
            "query" => target + "&extra=1",
            "port" => target.Replace("example.com/", "example.com:443/", StringComparison.Ordinal),
            "path" => target.Replace("/approved", "/denied/../approved", StringComparison.Ordinal),
            _ => target + "#reviews"
        };
        var settings = new BrowserServerSettings
        {
            AllowedHosts = ["example.com"],
            AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(target)]
        };

        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(reference, settings));
    }

    [Fact]
    public void NonliteralRegexEscapesRetainTheirOriginalMeaning()
    {
        var settings = new BrowserServerSettings { AllowedNavigationUrlPatterns = [@"https://example\.com/record/\d+"] };
        Assert.Equal("https://example.com/record/123", BrowserNavigationPolicy.ValidateNavigationTarget("https://example.com/record/123", settings).AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget("https://example.com/record/d", settings));
    }

    [Fact]
    public void NonliteralPatternsStillRequireTheNonbacktrackingEngine()
    {
        var settings = new BrowserServerSettings { AllowedNavigationUrlPatterns = [@"https://example\.com/(a)\1"] };
        Assert.Throws<NotSupportedException>(() => BrowserNavigationPolicy.ValidateNavigationTarget("https://example.com/aa", settings));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com?query=1", "https://example.com/?query=1")]
    public void OmittedHttpRootPathStillUsesItsExplicitRequestTarget(string reference, string target)
    {
        var settings = new BrowserServerSettings { AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(target)] };
        Assert.Equal(target, BrowserNavigationPolicy.ValidateNavigationTarget(reference, settings).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.com:443/allowed")]
    [InlineData("https://example.com/denied/../allowed")]
    public void ExactRouteDoesNotAcquireAuthorityThroughUriCanonicalization(string reference)
    {
        var settings = new BrowserServerSettings
        {
            AllowedHosts = ["example.com"],
            AllowedNavigationUrlPatterns = [@"https://example\.com/allowed"]
        };
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(reference, settings));
        Assert.Equal("https://example.com/allowed", BrowserNavigationPolicy.ValidateNavigationTarget("https://example.com/allowed", settings).AbsoluteUri);
    }

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
