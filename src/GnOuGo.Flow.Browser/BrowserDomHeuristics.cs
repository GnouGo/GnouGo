namespace GnOuGo.Flow.Browser;

public static class BrowserDomHeuristics
{
    public static string NormalizeWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
