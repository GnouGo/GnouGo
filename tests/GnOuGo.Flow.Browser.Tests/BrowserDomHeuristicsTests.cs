using Xunit;

namespace GnOuGo.Flow.Browser.Tests;

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
}

