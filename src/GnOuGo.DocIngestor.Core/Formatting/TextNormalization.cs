using System.Text;
using System.Text.RegularExpressions;

namespace DocIngestor.Core.Formatting;

internal static class TextNormalization
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeWhitespaceForEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Keep paragraph boundaries: convert CRLF -> LF, then map newlines to " \n " tokens
        // so we don't accidentally glue words when later collapsing whitespace.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = text.Replace('\t', ' ');

        // Ensure newlines have surrounding spaces (prevents word gluing at line breaks)
        text = text.Replace("\n", " \n ");

        text = MultiSpace.Replace(text, " ").Trim();
        return text;
    }

    public static string NormalizeWhitespaceForDisplay(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = text.Replace('\t', ' ');

        // Fix common PDF/Office "glued words" case: lowerCaseUpperCase -> lowerCase UpperCase.
        // Conservative: only when both chars are letters.
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (i > 0)
            {
                char prev = text[i - 1];
                if (char.IsLetter(prev) && char.IsLetter(c) && char.IsLower(prev) && char.IsUpper(c))
                {
                    // avoid inserting space after existing whitespace/punctuation
                    if (!char.IsWhiteSpace(prev) && prev != '-' && prev != '/' && prev != '_' && prev != '.')
                        sb.Append(' ');
                }
            }
            sb.Append(c);
        }

        // Collapse weird spacing but keep line breaks
        var normalized = sb.ToString();
        normalized = Regex.Replace(normalized, @"[ \f\v]+", " ");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        return normalized.Trim();
    }

    public static string EscapeMarkdownCell(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Basic escaping for markdown tables
        return s.Replace("|", "\\|").Replace("\n", "<br/>").Trim();
    }

    public static string CsvEscape(string? s)
    {
        s ??= "";
        bool mustQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (s.Contains('"')) s = s.Replace("\"", "\"\"");
        return mustQuote ? $"\"{s}\"" : s;
    }
}
