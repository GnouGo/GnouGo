using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.RegularExpressions;

namespace GnOuGo.Document.Mcp;

/// <summary>
/// Generates DOCX files from plain text or Markdown-like input.
/// </summary>
internal static class DocxWriter
{
    public static void WriteSimpleDocx(string path, string content)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
        var body = mainPart.Document.AppendChild(new Body());

        if (LooksLikeMarkdown(content))
        {
            AddDefaultStyles(mainPart);
            AddDefaultNumbering(mainPart);
            WriteMarkdownBody(mainPart, body, content);
            mainPart.Document.Save();
            return;
        }

        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var para = body.AppendChild(new Paragraph());
            var run = para.AppendChild(new Run());
            run.AppendChild(new Text(line.TrimEnd('\r')) { Space = SpaceProcessingModeValues.Preserve });
        }

        mainPart.Document.Save();
    }

    private static bool LooksLikeMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var score = 0;
        var lines = content.Replace("\r\n", "\n").Split('\n');

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^\s{0,3}#{1,6}\s+\S"))
                score += 3;
            if (Regex.IsMatch(line, @"^\s{0,3}([-*+])\s+\S"))
                score += 2;
            if (Regex.IsMatch(line, @"^\s{0,3}\d+[\.)]\s+\S"))
                score += 2;
            if (Regex.IsMatch(line, @"^\s{0,3}>\s+\S"))
                score += 2;
            if (Regex.IsMatch(line, @"^\s{0,3}```"))
                score += 3;
            if (IsHorizontalRule(line))
                score += 2;
            if (IsTableRow(line))
                score += 1;
        }

        if (Regex.IsMatch(content, @"(\*\*|__)\S.+?\S\1"))
            score += 2;
        if (Regex.IsMatch(content, @"(?<!\*)\*\S[^*\n]+?\S\*(?!\*)|(?<!_)_\S[^_\n]+?\S_(?!_)"))
            score += 1;
        if (Regex.IsMatch(content, @"`[^`\n]+`"))
            score += 1;
        if (Regex.IsMatch(content, @"\[[^\]\n]+\]\([^)]+\)"))
            score += 2;

        return score >= 2;
    }

    private static void WriteMarkdownBody(MainDocumentPart mainPart, Body body, string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fence = Regex.Match(line, @"^\s{0,3}```");
            if (fence.Success)
            {
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !Regex.IsMatch(lines[i], @"^\s{0,3}```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }

                AppendCodeBlock(body, string.Join('\n', codeLines));
                continue;
            }

            if (TryAppendTable(mainPart, body, lines, ref i))
                continue;

            if (IsHorizontalRule(line))
            {
                AppendHorizontalRule(body);
                continue;
            }

            var heading = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
            if (heading.Success)
            {
                AppendParagraph(mainPart, body, heading.Groups[2].Value, $"Heading{heading.Groups[1].Value.Length}");
                continue;
            }

            var bullet = Regex.Match(line, @"^\s{0,3}[-*+]\s+(.+)$");
            if (bullet.Success)
            {
                AppendListParagraph(mainPart, body, bullet.Groups[1].Value, numberingId: 1);
                continue;
            }

            var numbered = Regex.Match(line, @"^\s{0,3}\d+[\.)]\s+(.+)$");
            if (numbered.Success)
            {
                AppendListParagraph(mainPart, body, numbered.Groups[1].Value, numberingId: 2);
                continue;
            }

            var quote = Regex.Match(line, @"^\s{0,3}>\s?(.+)$");
            if (quote.Success)
            {
                AppendParagraph(mainPart, body, quote.Groups[1].Value, "Quote");
                continue;
            }

            var paragraphLines = new List<string> { line.Trim() };
            while (i + 1 < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i + 1])
                   && !IsMarkdownBlockStart(lines[i + 1]))
            {
                i++;
                paragraphLines.Add(lines[i].Trim());
            }

            AppendParagraph(mainPart, body, string.Join(' ', paragraphLines), styleId: null);
        }
    }

    private static bool IsMarkdownBlockStart(string line)
        => Regex.IsMatch(line, @"^\s{0,3}(#{1,6}\s+\S|[-*+]\s+\S|\d+[\.)]\s+\S|>\s?\S|```)")
            || IsHorizontalRule(line)
            || IsTableRow(line);

    private static bool TryAppendTable(MainDocumentPart mainPart, Body body, string[] lines, ref int index)
    {
        if (index + 1 >= lines.Length
            || !IsTableRow(lines[index])
            || !IsTableSeparator(lines[index + 1]))
        {
            return false;
        }

        var rows = new List<string[]>
        {
            SplitTableRow(lines[index])
        };

        index += 2;
        while (index < lines.Length && IsTableRow(lines[index]) && !IsTableSeparator(lines[index]))
        {
            rows.Add(SplitTableRow(lines[index]));
            index++;
        }

        index--;
        AppendTable(mainPart, body, rows);
        return true;
    }

    private static string[] SplitTableRow(string line)
        => SplitUnescapedPipes(line.Trim().Trim('|'))
            .Select(cell => cell.Trim().Replace(@"\|", "|"))
            .ToArray();

    private static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Contains('|', StringComparison.Ordinal)
            && SplitUnescapedPipes(trimmed.Trim('|')).Length >= 2
            && !IsHorizontalRule(trimmed);
    }

    private static bool IsTableSeparator(string line)
    {
        if (!IsTableRow(line))
            return false;

        var cells = SplitTableRow(line);
        return cells.Length >= 2
            && cells.All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$"));
    }

    private static string[] SplitUnescapedPipes(string line)
    {
        var cells = new List<string>();
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '|' && (i == 0 || line[i - 1] != '\\'))
            {
                cells.Add(line[start..i]);
                start = i + 1;
            }
        }

        cells.Add(line[start..]);
        return cells.ToArray();
    }

    private static void AppendParagraph(MainDocumentPart mainPart, Body body, string text, string? styleId)
    {
        var paragraph = body.AppendChild(new Paragraph());
        if (!string.IsNullOrWhiteSpace(styleId))
        {
            paragraph.ParagraphProperties = new ParagraphProperties(
                new ParagraphStyleId { Val = styleId });
        }

        AppendInlineRuns(mainPart, paragraph, text);
    }

    private static void AppendListParagraph(MainDocumentPart mainPart, Body body, string text, int numberingId)
    {
        var paragraph = body.AppendChild(new Paragraph());
        paragraph.ParagraphProperties = new ParagraphProperties(
            new NumberingProperties(
                new NumberingLevelReference { Val = 0 },
                new NumberingId { Val = numberingId }));

        AppendInlineRuns(mainPart, paragraph, text);
    }

    private static void AppendCodeBlock(Body body, string code)
    {
        foreach (var line in code.Split('\n'))
        {
            var paragraph = body.AppendChild(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "CodeBlock" })));
            paragraph.AppendChild(new Run(
                new RunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }),
                new Text(line) { Space = SpaceProcessingModeValues.Preserve }));
        }
    }

    private static void AppendHorizontalRule(Body body)
    {
        body.AppendChild(new Paragraph(
            new ParagraphProperties(
                new ParagraphBorders(
                    new BottomBorder
                    {
                        Val = BorderValues.Single,
                        Size = 8,
                        Space = 1,
                        Color = "808080"
                    }))));
    }

    private static bool IsHorizontalRule(string line)
        => Regex.IsMatch(line, @"^\s{0,3}(([-*_=])\s*){3,}$");

    private static void AppendTable(MainDocumentPart mainPart, Body body, IReadOnlyList<string[]> rows)
    {
        var table = body.AppendChild(new Table());
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        foreach (var row in rows)
        {
            var tr = table.AppendChild(new TableRow());
            foreach (var cell in row)
            {
                var tc = tr.AppendChild(new TableCell());
                var paragraph = tc.AppendChild(new Paragraph());
                AppendInlineRuns(mainPart, paragraph, cell);
            }
        }
    }

    private static void AppendInlineRuns(MainDocumentPart mainPart, Paragraph paragraph, string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadDelimited(text, index, "**", out var boldText, out var boldEnd)
                || TryReadDelimited(text, index, "__", out boldText, out boldEnd))
            {
                paragraph.AppendChild(CreateTextRun(boldText, bold: true));
                index = boldEnd;
                continue;
            }

            if (TryReadDelimited(text, index, "`", out var codeText, out var codeEnd))
            {
                paragraph.AppendChild(CreateTextRun(codeText, code: true));
                index = codeEnd;
                continue;
            }

            if (TryReadMarkdownLink(text, index, out var linkText, out var linkUri, out var linkEnd))
            {
                AppendHyperlink(mainPart, paragraph, linkText, linkUri);
                index = linkEnd;
                continue;
            }

            if (TryReadDelimited(text, index, "*", out var italicText, out var italicEnd)
                || TryReadDelimited(text, index, "_", out italicText, out italicEnd))
            {
                paragraph.AppendChild(CreateTextRun(italicText, italic: true));
                index = italicEnd;
                continue;
            }

            var next = FindNextInlineMarker(text, index + 1);
            paragraph.AppendChild(CreateTextRun(text[index..next]));
            index = next;
        }
    }

    private static bool TryReadDelimited(string text, int start, string delimiter, out string value, out int end)
    {
        value = string.Empty;
        end = start;

        if (!text.AsSpan(start).StartsWith(delimiter, StringComparison.Ordinal))
            return false;

        if (delimiter.Length == 1
            && start + 1 < text.Length
            && text[start + 1] == delimiter[0])
            return false;

        var contentStart = start + delimiter.Length;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
            return false;

        var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        if (close < 0 || close == contentStart)
            return false;

        if (delimiter.Length == 1
            && close + 1 < text.Length
            && text[close + 1] == delimiter[0])
            return false;

        if (char.IsWhiteSpace(text[close - 1]))
            return false;

        value = text[contentStart..close];
        end = close + delimiter.Length;
        return true;
    }

    private static bool TryReadMarkdownLink(
        string text,
        int start,
        out string linkText,
        out string uri,
        out int end)
    {
        linkText = string.Empty;
        uri = string.Empty;
        end = start;

        if (text[start] != '[')
            return false;

        var textEnd = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (textEnd < 0)
            return false;

        var uriStart = textEnd + 2;
        var uriEnd = text.IndexOf(')', uriStart);
        if (uriEnd < 0 || uriEnd == uriStart)
            return false;

        linkText = text[(start + 1)..textEnd];
        uri = text[uriStart..uriEnd].Trim();
        end = uriEnd + 1;
        return linkText.Length > 0;
    }

    private static int FindNextInlineMarker(string text, int start)
    {
        var next = text.Length;
        foreach (var marker in new[] { "**", "__", "`", "[", "*", "_" })
        {
            var index = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (index >= 0 && index < next)
                next = index;
        }

        return next;
    }

    private static Run CreateTextRun(string text, bool bold = false, bool italic = false, bool code = false)
    {
        var run = new Run();
        var properties = new RunProperties();
        if (bold)
            properties.AppendChild(new Bold());
        if (italic)
            properties.AppendChild(new Italic());
        if (code)
            properties.AppendChild(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
        if (properties.HasChildren)
            run.AppendChild(properties);

        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static void AppendHyperlink(MainDocumentPart mainPart, Paragraph paragraph, string text, string uri)
    {
        if (uri.StartsWith("#", StringComparison.Ordinal) && uri.Length > 1)
        {
            paragraph.AppendChild(new Hyperlink(CreateHyperlinkRun(text))
            {
                Anchor = uri[1..],
                History = OnOffValue.FromBoolean(true)
            });
            return;
        }

        if (!Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out var linkUri))
        {
            paragraph.AppendChild(CreateTextRun($"{text} ({uri})"));
            return;
        }

        var relationship = mainPart.AddHyperlinkRelationship(linkUri, true);

        paragraph.AppendChild(new Hyperlink(CreateHyperlinkRun(text))
        {
            Id = relationship.Id,
            History = OnOffValue.FromBoolean(true)
        });
    }

    private static Run CreateHyperlinkRun(string text)
        => new(
            new RunProperties(
                new Color { Val = "0563C1" },
                new Underline { Val = UnderlineValues.Single }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static void AddDefaultStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        styles.AppendChild(CreateParagraphStyle("Heading1", "heading 1", 32, bold: true));
        styles.AppendChild(CreateParagraphStyle("Heading2", "heading 2", 28, bold: true));
        styles.AppendChild(CreateParagraphStyle("Heading3", "heading 3", 24, bold: true));
        styles.AppendChild(CreateParagraphStyle("Heading4", "heading 4", 22, bold: true));
        styles.AppendChild(CreateParagraphStyle("Heading5", "heading 5", 20, bold: true));
        styles.AppendChild(CreateParagraphStyle("Heading6", "heading 6", 18, bold: true));
        styles.AppendChild(CreateParagraphStyle("Quote", "Quote", 22, italic: true, leftIndent: "360"));
        styles.AppendChild(CreateParagraphStyle("CodeBlock", "Code Block", 20, font: "Consolas"));

        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    private static Style CreateParagraphStyle(
        string styleId,
        string name,
        int fontSize,
        bool bold = false,
        bool italic = false,
        string? font = null,
        string? leftIndent = null)
    {
        var style = new Style { Type = StyleValues.Paragraph, StyleId = styleId };
        style.AppendChild(new StyleName { Val = name });

        var paragraphProperties = new StyleParagraphProperties();
        if (!string.IsNullOrWhiteSpace(leftIndent))
            paragraphProperties.AppendChild(new Indentation { Left = leftIndent });
        if (paragraphProperties.HasChildren)
            style.AppendChild(paragraphProperties);

        var runProperties = new StyleRunProperties();
        if (bold)
            runProperties.AppendChild(new Bold());
        if (italic)
            runProperties.AppendChild(new Italic());
        if (!string.IsNullOrWhiteSpace(font))
            runProperties.AppendChild(new RunFonts { Ascii = font, HighAnsi = font });
        runProperties.AppendChild(new FontSize { Val = fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        style.AppendChild(runProperties);

        return style;
    }

    private static void AddDefaultNumbering(MainDocumentPart mainPart)
    {
        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        numberingPart.Numbering = new Numbering(
            new AbstractNum(
                new Level(
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new AbstractNum(
                new Level(
                    new NumberingFormat { Val = NumberFormatValues.Decimal },
                    new LevelText { Val = "%1." },
                    new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });
        numberingPart.Numbering.Save();
    }
}
