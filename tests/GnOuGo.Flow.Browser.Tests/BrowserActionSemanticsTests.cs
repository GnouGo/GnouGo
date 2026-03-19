using Xunit;

namespace GnOuGo.Flow.Browser.Tests;

public class BrowserActionSemanticsTests
{
    [Theory]
    [InlineData("button", null, true, true)]
    [InlineData("button", "submit", true, true)]
    [InlineData("BUTTON", "SUBMIT", true, true)]
    [InlineData("input", "submit", true, true)]
    [InlineData("input", "image", true, true)]
    [InlineData("button", "button", true, false)]
    [InlineData("input", "button", true, false)]
    [InlineData("a", null, true, false)]
    [InlineData("button", null, false, false)]
    [InlineData("input", "submit", false, false)]
    public void LooksLikeSubmitControl_ReturnsExpectedValue(string tagName, string? typeAttribute, bool hasAssociatedForm, bool expected)
    {
        var actual = BrowserActionSemantics.LooksLikeSubmitControl(tagName, typeAttribute, hasAssociatedForm);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, "https://example.com/form", "https://example.com/form", BrowserActionSemantics.NavigationTypeReload)]
    [InlineData(false, "https://example.com/form", "https://example.com/submitted", BrowserActionSemantics.NavigationTypeNavigate)]
    [InlineData(false, "https://example.com/form", "https://example.com/form", BrowserActionSemantics.NavigationTypeNone)]
    [InlineData(false, "https://example.com/form", " https://example.com/form ", BrowserActionSemantics.NavigationTypeNone)]
    [InlineData(false, null, "https://example.com/submitted", BrowserActionSemantics.NavigationTypeNavigate)]
    public void NavigationType_ReturnsExpectedValue(bool mainFrameNavigated, string? previousUrl, string? currentUrl, string expected)
    {
        var actual = BrowserActionSemantics.NavigationType(mainFrameNavigated, previousUrl, currentUrl);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, "https://example.com/form", "https://example.com/form", true)]
    [InlineData(false, "https://example.com/form", "https://example.com/submitted", true)]
    [InlineData(false, "https://example.com/form", "https://example.com/form", false)]
    [InlineData(false, "https://example.com/form", " https://example.com/form ", false)]
    [InlineData(false, null, "https://example.com/submitted", true)]
    public void TriggeredNavigation_ReturnsExpectedValue(bool mainFrameNavigated, string? previousUrl, string? currentUrl, bool expected)
    {
        var actual = BrowserActionSemantics.TriggeredNavigation(mainFrameNavigated, previousUrl, currentUrl);

        Assert.Equal(expected, actual);
    }
}

