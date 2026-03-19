namespace GnOuGo.Flow.Core.Templating;

/// <summary>
/// Minimal Mustache parser. AOT-friendly (no reflection).
/// Supports: {{var}}, {{{raw}}}, {{#section}}, {{^inverted}}, {{/close}}.
/// </summary>
public static class MustacheParser
{
    public static List<MustacheToken> Parse(string template)
    {
        var tokens = new List<MustacheToken>();
        ParseInto(template, 0, tokens, null);
        return tokens;
    }

    private static int ParseInto(string template, int pos, List<MustacheToken> tokens, string? closingTag)
    {
        while (pos < template.Length)
        {
            var tagStart = template.IndexOf("{{", pos, StringComparison.Ordinal);
            if (tagStart < 0)
            {
                // Rest is text
                if (pos < template.Length)
                    tokens.Add(new TextToken(template[pos..]));
                pos = template.Length;
                break;
            }

            // Text before tag
            if (tagStart > pos)
                tokens.Add(new TextToken(template[pos..tagStart]));

            // Triple mustache {{{name}}}
            if (tagStart + 2 < template.Length && template[tagStart + 2] == '{')
            {
                var tripleEnd = template.IndexOf("}}}", tagStart + 3, StringComparison.Ordinal);
                if (tripleEnd < 0)
                    throw new MustacheParseException($"Unterminated triple mustache at position {tagStart}");
                var name = template[(tagStart + 3)..tripleEnd].Trim();
                tokens.Add(new RawVariableToken(name));
                pos = tripleEnd + 3;
                continue;
            }

            var tagEnd = template.IndexOf("}}", tagStart + 2, StringComparison.Ordinal);
            if (tagEnd < 0)
                throw new MustacheParseException($"Unterminated tag at position {tagStart}");

            var content = template[(tagStart + 2)..tagEnd].Trim();
            pos = tagEnd + 2;

            if (content.Length == 0)
                throw new MustacheParseException($"Empty tag at position {tagStart}");

            var firstChar = content[0];

            if (firstChar == '#')
            {
                // Section open
                var sectionName = content[1..].Trim();
                var children = new List<MustacheToken>();
                pos = ParseInto(template, pos, children, sectionName);
                tokens.Add(new SectionToken(sectionName, children));
            }
            else if (firstChar == '^')
            {
                // Inverted section
                var sectionName = content[1..].Trim();
                var children = new List<MustacheToken>();
                pos = ParseInto(template, pos, children, sectionName);
                tokens.Add(new InvertedSectionToken(sectionName, children));
            }
            else if (firstChar == '/')
            {
                // Section close
                var sectionName = content[1..].Trim();
                if (closingTag != null && sectionName == closingTag)
                    return pos;
                throw new MustacheParseException($"Unexpected closing tag '{{{{/{sectionName}}}}}' at position {tagStart}");
            }
            else if (firstChar == '!')
            {
                // Comment — ignore
            }
            else
            {
                // Variable
                tokens.Add(new VariableToken(content));
            }
        }

        if (closingTag != null)
            throw new MustacheParseException($"Unclosed section '{closingTag}'");

        return pos;
    }
}

public sealed class MustacheParseException : Exception
{
    public MustacheParseException(string message) : base(message) { }
}

