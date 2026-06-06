using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GnOuGo.Document.Mcp;

/// <summary>
/// Generates text-oriented PDFs from plain text or Markdown-like input.
/// </summary>
internal static class PdfWriter
{
    private const decimal PageWidth = 595m;
    private const decimal PageHeight = 842m;
    private const decimal Margin = 54m;
    private const decimal ContentWidth = PageWidth - (Margin * 2m);

    public static void WriteSimplePdf(string path, string content)
    {
        var builder = new PdfDocumentBuilder();
        var fonts = new PdfFonts(
            builder.AddStandard14Font(Standard14Font.Helvetica),
            builder.AddStandard14Font(Standard14Font.HelveticaBold),
            builder.AddStandard14Font(Standard14Font.HelveticaOblique),
            builder.AddStandard14Font(Standard14Font.Courier));

        var writer = new PageWriter(builder, fonts);
        var blocks = LooksLikeMarkdown(content)
            ? ParseMarkdown(content)
            : ParsePlainText(content);

        foreach (var block in blocks)
            writer.Write(block);

        File.WriteAllBytes(path, builder.Build());
    }

    private static bool LooksLikeMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var score = 0;
        var lines = NormalizeLines(content);

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

    private static IReadOnlyList<PdfBlock> ParsePlainText(string content)
        => NormalizeLines(content)
            .Select(line => string.IsNullOrWhiteSpace(line)
                ? PdfBlock.Spacer()
                : PdfBlock.Paragraph(ParseInline(line.TrimEnd('\r'))))
            .ToArray();

    private static IReadOnlyList<PdfBlock> ParseMarkdown(string content)
    {
        var blocks = new List<PdfBlock>();
        var lines = NormalizeLines(content);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                blocks.Add(PdfBlock.Spacer());
                continue;
            }

            if (Regex.IsMatch(line, @"^\s{0,3}```"))
            {
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !Regex.IsMatch(lines[i], @"^\s{0,3}```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }

                blocks.Add(PdfBlock.CodeBlock(string.Join('\n', codeLines)));
                continue;
            }

            if (TryParseTable(lines, ref i, out var table))
            {
                blocks.Add(table);
                continue;
            }

            if (IsHorizontalRule(line))
            {
                blocks.Add(PdfBlock.Rule());
                continue;
            }

            var heading = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
            if (heading.Success)
            {
                blocks.Add(PdfBlock.Heading(heading.Groups[1].Value.Length, ParseInline(heading.Groups[2].Value)));
                continue;
            }

            var bullet = Regex.Match(line, @"^\s{0,3}[-*+]\s+(.+)$");
            if (bullet.Success)
            {
                blocks.Add(PdfBlock.ListItem("•", ParseInline(bullet.Groups[1].Value)));
                continue;
            }

            var numbered = Regex.Match(line, @"^\s{0,3}(\d+)[\.)]\s+(.+)$");
            if (numbered.Success)
            {
                blocks.Add(PdfBlock.ListItem($"{numbered.Groups[1].Value}.", ParseInline(numbered.Groups[2].Value)));
                continue;
            }

            var quote = Regex.Match(line, @"^\s{0,3}>\s?(.+)$");
            if (quote.Success)
            {
                blocks.Add(PdfBlock.Quote(ParseInline(quote.Groups[1].Value)));
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

            blocks.Add(PdfBlock.Paragraph(ParseInline(string.Join(' ', paragraphLines))));
        }

        return blocks;
    }

    private static bool TryParseTable(string[] lines, ref int index, out PdfBlock table)
    {
        table = PdfBlock.Spacer();
        if (index + 1 >= lines.Length || !IsTableRow(lines[index]) || !IsTableSeparator(lines[index + 1]))
            return false;

        var rows = new List<IReadOnlyList<IReadOnlyList<InlineRun>>>
        {
            SplitTableRow(lines[index]).Select(ParseInline).ToArray()
        };

        index += 2;
        while (index < lines.Length && IsTableRow(lines[index]) && !IsTableSeparator(lines[index]))
        {
            rows.Add(SplitTableRow(lines[index]).Select(ParseInline).ToArray());
            index++;
        }

        index--;
        table = PdfBlock.Table(rows);
        return true;
    }

    private static IReadOnlyList<InlineRun> ParseInline(string text)
    {
        var runs = new List<InlineRun>();
        var index = 0;

        while (index < text.Length)
        {
            if (TryReadDelimited(text, index, "**", out var boldText, out var boldEnd)
                || TryReadDelimited(text, index, "__", out boldText, out boldEnd))
            {
                runs.Add(new InlineRun(boldText, InlineStyle.Bold));
                index = boldEnd;
                continue;
            }

            if (TryReadDelimited(text, index, "`", out var codeText, out var codeEnd))
            {
                runs.Add(new InlineRun(codeText, InlineStyle.Code));
                index = codeEnd;
                continue;
            }

            if (TryReadMarkdownLink(text, index, out var linkText, out var linkUri, out var linkEnd))
            {
                var label = linkText == linkUri ? linkText : $"{linkText} ({linkUri})";
                runs.Add(new InlineRun(label, InlineStyle.Link));
                index = linkEnd;
                continue;
            }

            if (TryReadDelimited(text, index, "*", out var italicText, out var italicEnd)
                || TryReadDelimited(text, index, "_", out italicText, out italicEnd))
            {
                runs.Add(new InlineRun(italicText, InlineStyle.Italic));
                index = italicEnd;
                continue;
            }

            var next = FindNextInlineMarker(text, index + 1);
            runs.Add(new InlineRun(text[index..next], InlineStyle.Normal));
            index = next;
        }

        return runs;
    }

    private static bool TryReadDelimited(string text, int start, string delimiter, out string value, out int end)
    {
        value = string.Empty;
        end = start;

        if (!text.AsSpan(start).StartsWith(delimiter, StringComparison.Ordinal))
            return false;

        if (delimiter.Length == 1 && start + 1 < text.Length && text[start + 1] == delimiter[0])
            return false;

        var contentStart = start + delimiter.Length;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
            return false;

        var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        if (close < 0 || close == contentStart)
            return false;

        if (delimiter.Length == 1 && close + 1 < text.Length && text[close + 1] == delimiter[0])
            return false;

        if (char.IsWhiteSpace(text[close - 1]))
            return false;

        value = text[contentStart..close];
        end = close + delimiter.Length;
        return true;
    }

    private static bool TryReadMarkdownLink(string text, int start, out string linkText, out string uri, out int end)
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

    private static bool IsMarkdownBlockStart(string line)
        => Regex.IsMatch(line, @"^\s{0,3}(#{1,6}\s+\S|[-*+]\s+\S|\d+[\.)]\s+\S|>\s?\S|```)")
            || IsHorizontalRule(line)
            || IsTableRow(line);

    private static bool IsHorizontalRule(string line)
        => Regex.IsMatch(line, @"^\s{0,3}(([-*_=])\s*){3,}$");

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
        return cells.Length >= 2 && cells.All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$"));
    }

    private static string[] SplitTableRow(string line)
        => SplitUnescapedPipes(line.Trim().Trim('|'))
            .Select(cell => cell.Trim().Replace(@"\|", "|"))
            .ToArray();

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

    private static string[] NormalizeLines(string content)
        => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string ToPdfAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(ch switch
            {
                '’' or '‘' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                '•' => '*',
                _ when ch >= 32 && ch <= 126 => ch,
                _ => '?'
            });
        }

        return builder.ToString();
    }

    private sealed class PageWriter
    {
        private readonly PdfDocumentBuilder _builder;
        private readonly PdfFonts _fonts;
        private PdfPageBuilder _page;
        private decimal _y;

        public PageWriter(PdfDocumentBuilder builder, PdfFonts fonts)
        {
            _builder = builder;
            _fonts = fonts;
            _page = _builder.AddPage(PageSize.A4);
            _y = PageHeight - Margin;
        }

        public void Write(PdfBlock block)
        {
            switch (block.Kind)
            {
                case PdfBlockKind.Spacer:
                    MoveDown(8m);
                    break;
                case PdfBlockKind.Rule:
                    EnsureSpace(18m);
                    _page.DrawLine(new PdfPoint((double)Margin, (double)_y), new PdfPoint((double)(PageWidth - Margin), (double)_y), 1);
                    MoveDown(18m);
                    break;
                case PdfBlockKind.Heading:
                    WriteRuns(block.Runs, block.Level switch
                    {
                        1 => 22m,
                        2 => 18m,
                        3 => 16m,
                        _ => 14m
                    }, _fonts.Bold, 4m, 10m);
                    break;
                case PdfBlockKind.ListItem:
                    WriteListItem(block.Marker, block.Runs);
                    break;
                case PdfBlockKind.Quote:
                    WriteRuns(block.Runs, 11m, _fonts.Italic, 22m, 8m);
                    break;
                case PdfBlockKind.Code:
                    foreach (var line in block.Code.Split('\n'))
                        WriteTextLine(ToPdfAscii(line), 10m, _fonts.Code, 18m, 2m);
                    MoveDown(6m);
                    break;
                case PdfBlockKind.Table:
                    WriteTable(block.Rows);
                    break;
                default:
                    WriteRuns(block.Runs, 11m, _fonts.Regular, 0m, 6m);
                    break;
            }
        }

        private void WriteListItem(string marker, IReadOnlyList<InlineRun> runs)
        {
            EnsureSpace(16m);
            _page.AddText(ToPdfAscii(marker), 11, new PdfPoint((double)Margin, (double)_y), _fonts.Regular);
            WriteRuns(runs, 11m, _fonts.Regular, 24m, 4m);
        }

        private void WriteRuns(
            IReadOnlyList<InlineRun> runs,
            decimal fontSize,
            PdfDocumentBuilder.AddedFont defaultFont,
            decimal indent,
            decimal after)
        {
            var words = FlattenRuns(runs, defaultFont).ToArray();
            var line = new List<TextToken>();
            var lineWidth = 0m;
            var maxWidth = ContentWidth - indent;
            var spaceWidth = fontSize * 0.32m;

            foreach (var word in words)
            {
                var tokenWidth = EstimateWidth(word.Text, fontSize);
                if (line.Count > 0 && lineWidth + spaceWidth + tokenWidth > maxWidth)
                {
                    WriteTokenLine(line, fontSize, indent);
                    line.Clear();
                    lineWidth = 0m;
                }

                if (line.Count > 0)
                    lineWidth += spaceWidth;

                line.Add(word);
                lineWidth += tokenWidth;
            }

            if (line.Count > 0)
                WriteTokenLine(line, fontSize, indent);

            MoveDown(after);
        }

        private void WriteTextLine(string text, decimal fontSize, PdfDocumentBuilder.AddedFont font, decimal indent, decimal after)
        {
            EnsureSpace(fontSize + after);
            _page.AddText(text, (double)fontSize, new PdfPoint((double)(Margin + indent), (double)_y), font);
            MoveDown(fontSize + after);
        }

        private void WriteTokenLine(IReadOnlyList<TextToken> tokens, decimal fontSize, decimal indent)
        {
            EnsureSpace(fontSize + 4m);
            var x = Margin + indent;
            foreach (var token in tokens)
            {
                if (x > Margin + indent)
                    x += fontSize * 0.32m;

                if (token.Style == InlineStyle.Link)
                    _page.SetTextAndFillColor(5, 99, 193);

                _page.AddText(ToPdfAscii(token.Text), (double)fontSize, new PdfPoint((double)x, (double)_y), token.Font);

                if (token.Style == InlineStyle.Link)
                    _page.ResetColor();

                x += EstimateWidth(token.Text, fontSize);
            }

            MoveDown(fontSize + 4m);
        }

        private void WriteTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> rows)
        {
            if (rows.Count == 0)
                return;

            var columnCount = rows.Max(row => row.Count);
            if (columnCount == 0)
                return;

            var columnWidth = ContentWidth / columnCount;
            foreach (var row in rows)
            {
                EnsureSpace(18m);
                var top = _y + 4m;
                var x = Margin;
                for (var column = 0; column < columnCount; column++)
                {
                    _page.DrawRectangle(new PdfPoint((double)x, (double)(top - 16m)), (double)columnWidth, 18, 0.5);
                    var cellRuns = column < row.Count ? row[column] : Array.Empty<InlineRun>();
                    var text = string.Join("", cellRuns.Select(r => r.Text));
                    var font = cellRuns.Any(r => r.Style == InlineStyle.Bold) ? _fonts.Bold : _fonts.Regular;
                    _page.AddText(ToPdfAscii(TrimForWidth(text, 10m, columnWidth - 8m)), 10, new PdfPoint((double)(x + 4m), (double)(top - 11m)), font);
                    x += columnWidth;
                }

                MoveDown(18m);
            }

            MoveDown(8m);
        }

        private IEnumerable<TextToken> FlattenRuns(
            IReadOnlyList<InlineRun> runs,
            PdfDocumentBuilder.AddedFont defaultFont)
        {
            foreach (var run in runs)
            {
                var font = run.Style switch
                {
                    InlineStyle.Bold => _fonts.Bold,
                    InlineStyle.Italic => _fonts.Italic,
                    InlineStyle.Code => _fonts.Code,
                    _ => defaultFont
                };

                foreach (var word in Regex.Split(run.Text, @"\s+").Where(s => s.Length > 0))
                    yield return new TextToken(word, run.Style, font);
            }
        }

        private void EnsureSpace(decimal height)
        {
            if (_y - height > Margin)
                return;

            _page = _builder.AddPage(PageSize.A4);
            _y = PageHeight - Margin;
        }

        private void MoveDown(decimal amount) => _y -= amount;

        private static decimal EstimateWidth(string text, decimal fontSize) => text.Length * fontSize * 0.52m;

        private static string TrimForWidth(string text, decimal fontSize, decimal width)
        {
            var normalized = Regex.Replace(text, @"\s+", " ").Trim();
            while (normalized.Length > 0 && EstimateWidth(normalized, fontSize) > width)
                normalized = normalized[..^1];
            return normalized;
        }
    }

    private sealed record PdfFonts(
        PdfDocumentBuilder.AddedFont Regular,
        PdfDocumentBuilder.AddedFont Bold,
        PdfDocumentBuilder.AddedFont Italic,
        PdfDocumentBuilder.AddedFont Code);

    private sealed record PdfBlock(
        PdfBlockKind Kind,
        IReadOnlyList<InlineRun> Runs,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> Rows,
        string Marker,
        string Code,
        int Level)
    {
        public static PdfBlock Spacer() => new(PdfBlockKind.Spacer, [], [], "", "", 0);
        public static PdfBlock Rule() => new(PdfBlockKind.Rule, [], [], "", "", 0);
        public static PdfBlock Paragraph(IReadOnlyList<InlineRun> runs) => new(PdfBlockKind.Paragraph, runs, [], "", "", 0);
        public static PdfBlock Heading(int level, IReadOnlyList<InlineRun> runs) => new(PdfBlockKind.Heading, runs, [], "", "", level);
        public static PdfBlock Quote(IReadOnlyList<InlineRun> runs) => new(PdfBlockKind.Quote, runs, [], "", "", 0);
        public static PdfBlock ListItem(string marker, IReadOnlyList<InlineRun> runs) => new(PdfBlockKind.ListItem, runs, [], marker, "", 0);
        public static PdfBlock CodeBlock(string code) => new(PdfBlockKind.Code, [], [], "", code, 0);
        public static PdfBlock Table(IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> rows) => new(PdfBlockKind.Table, [], rows, "", "", 0);
    }

    private sealed record InlineRun(string Text, InlineStyle Style);

    private sealed record TextToken(string Text, InlineStyle Style, PdfDocumentBuilder.AddedFont Font);

    private enum PdfBlockKind
    {
        Spacer,
        Paragraph,
        Heading,
        ListItem,
        Quote,
        Code,
        Table,
        Rule
    }

    private enum InlineStyle
    {
        Normal,
        Bold,
        Italic,
        Code,
        Link
    }
}
