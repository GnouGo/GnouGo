using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public class BrowserDomHeuristicsTests
{
    [Fact]
    public void NormalizeWhitespace_ReturnsEmptyString_ForNullOrWhitespace()
    {
        Assert.Equal(string.Empty, BrowserDomHeuristics.NormalizeWhitespace(null));
        Assert.Equal(string.Empty, BrowserDomHeuristics.NormalizeWhitespace("   \t  \r\n "));
    }

    [Theory]
    [InlineData("Accept   all cookies", "Accept all cookies")]
    [InlineData("  Bonjour\r\nle\tmonde  ", "Bonjour le monde")]
    [InlineData("one\n\n two\t\tthree", "one two three")]
    public void NormalizeWhitespace_CollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, BrowserDomHeuristics.NormalizeWhitespace(input));
    }

    [Fact]
    public void RemoveScriptElements_RemovesInlineAndExternalScripts()
    {
        var html = "<body><h1>Products</h1><script>window.noisy = true;</script><script src=\"/app.js\"></script><a href=\"/p\">item</a></body>";

        var cleaned = BrowserDomHeuristics.RemoveScriptElements(html);

        Assert.Contains("<h1>Products</h1>", cleaned);
        Assert.Contains("<a href=\"/p\">item</a>", cleaned);
        Assert.DoesNotContain("<script", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("window.noisy", cleaned);
    }

    [Fact]
    public void RemoveScriptElements_RemovesAmazonStateScriptJson()
    {
        var html = "<body><script type=\"a-state\" data-a-state=\"{&quot;key&quot;:&quot;a-wlab-states&quot;}\">{\"AUI_72554\":\"C\"}</script><main>Visible result</main></body>";

        var cleaned = BrowserDomHeuristics.RemoveScriptElements(html);

        Assert.Equal("<body><main>Visible result</main></body>", cleaned);
    }

    [Fact]
    public void RemoveScriptElements_IsCaseInsensitiveAndHandlesAttributes()
    {
        var html = "<DIV><SCRIPT type=\"text/javascript\">alert('x')</SCRIPT><span>ok</span></DIV>";

        var cleaned = BrowserDomHeuristics.RemoveScriptElements(html);

        Assert.Equal("<DIV><span>ok</span></DIV>", cleaned);
    }

    [Fact]
    public void RemoveScriptElements_RemovesSelfClosingScriptWithoutDroppingFollowingHtml()
    {
        var html = "<body><script src=\"/legacy.js\" /><section>keep me</section></body>";

        var cleaned = BrowserDomHeuristics.RemoveScriptElements(html);

        Assert.Equal("<body><section>keep me</section></body>", cleaned);
    }

    [Fact]
    public void RemoveScriptElements_RemovesUnclosedScriptToEnd()
    {
        var html = "<body><p>before</p><script>unterminated";

        var cleaned = BrowserDomHeuristics.RemoveScriptElements(html);

        Assert.Equal("<body><p>before</p>", cleaned);
    }
}

