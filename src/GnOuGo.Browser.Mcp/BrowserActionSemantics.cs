namespace GnOuGo.Browser.Mcp;

public static class BrowserActionSemantics
{
    public const string NavigationTypeNone = "none";
    public const string NavigationTypeNavigate = "navigate";
    public const string NavigationTypeReload = "reload";

    public static bool LooksLikeSubmitControl(string? tagName, string? typeAttribute, bool hasAssociatedForm)
    {
        if (!hasAssociatedForm)
            return false;

        var tag = Normalize(tagName);
        var type = Normalize(typeAttribute);

        return tag switch
        {
            "button" => string.IsNullOrEmpty(type) || type == "submit",
            "input" => type is "submit" or "image",
            _ => false
        };
    }

    public static bool TriggeredNavigation(bool mainFrameNavigated, string? previousUrl, string? currentUrl)
        => NavigationType(mainFrameNavigated, previousUrl, currentUrl) != NavigationTypeNone;

    public static string NavigationType(bool mainFrameNavigated, string? previousUrl, string? currentUrl)
    {
        var before = NormalizeUrl(previousUrl);
        var after = NormalizeUrl(currentUrl);

        if (!string.Equals(before, after, StringComparison.Ordinal))
            return NavigationTypeNavigate;

        if (mainFrameNavigated)
            return NavigationTypeReload;

        return NavigationTypeNone;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static string NormalizeUrl(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
}

