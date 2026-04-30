using System.Text.RegularExpressions;

namespace GnOuGo.Browser.Mcp;

public static class BrowserDomHeuristics
{
    private static readonly Regex CompleteScriptElementRegex = new(
        "<script\\b[^>]*>[\\s\\S]*?</script\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SelfClosingScriptElementRegex = new(
        "<script\\b[^>]*/\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnclosedScriptElementRegex = new(
        "<script\\b[^>]*>[\\s\\S]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string NormalizeWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string RemoveScriptElements(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0)
            return html;

        var withoutCompleteScripts = CompleteScriptElementRegex.Replace(html, string.Empty);
        var withoutSelfClosingScripts = SelfClosingScriptElementRegex.Replace(withoutCompleteScripts, string.Empty);
        return UnclosedScriptElementRegex.Replace(withoutSelfClosingScripts, string.Empty);
    }
}
