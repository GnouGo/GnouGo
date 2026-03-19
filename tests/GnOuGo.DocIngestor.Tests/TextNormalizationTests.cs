using DocIngestor.Core.Formatting;
using Xunit;

namespace DocIngestor.Tests;

public sealed class TextNormalizationTests
{
    // ── NormalizeWhitespaceForEmbedding ───────────────────────────────

    [Fact]
    public void ForEmbedding_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextNormalization.NormalizeWhitespaceForEmbedding(null!));
        Assert.Equal(string.Empty, TextNormalization.NormalizeWhitespaceForEmbedding(""));
        Assert.Equal(string.Empty, TextNormalization.NormalizeWhitespaceForEmbedding("   "));
    }

    [Fact]
    public void ForEmbedding_CollapsesMultipleSpaces()
    {
        var result = TextNormalization.NormalizeWhitespaceForEmbedding("hello    world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ForEmbedding_NormalizesLineBreaks()
    {
        var result = TextNormalization.NormalizeWhitespaceForEmbedding("hello\r\nworld\rfoo\nbar");
        // newlines become " \n " then whitespace is collapsed
        Assert.Contains("hello", result);
        Assert.Contains("world", result);
        Assert.DoesNotContain("\r", result);
    }

    [Fact]
    public void ForEmbedding_ConvertsTabs()
    {
        var result = TextNormalization.NormalizeWhitespaceForEmbedding("col1\tcol2\tcol3");
        Assert.DoesNotContain("\t", result);
        Assert.Contains("col1", result);
        Assert.Contains("col2", result);
    }

    // ── NormalizeWhitespaceForDisplay ─────────────────────────────────

    [Fact]
    public void ForDisplay_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextNormalization.NormalizeWhitespaceForDisplay(null!));
        Assert.Equal(string.Empty, TextNormalization.NormalizeWhitespaceForDisplay(""));
    }

    [Fact]
    public void ForDisplay_SplitsCamelCase()
    {
        var result = TextNormalization.NormalizeWhitespaceForDisplay("helloWorld");
        Assert.Equal("hello World", result);
    }

    [Fact]
    public void ForDisplay_CollapsesExcessiveNewlines()
    {
        var result = TextNormalization.NormalizeWhitespaceForDisplay("a\n\n\n\n\nb");
        Assert.Equal("a\n\nb", result);
    }

    [Fact]
    public void ForDisplay_TrimsResult()
    {
        var result = TextNormalization.NormalizeWhitespaceForDisplay("  hello  ");
        Assert.Equal("hello", result);
    }

    // ── EscapeMarkdownCell ───────────────────────────────────────────

    [Fact]
    public void EscapeMarkdownCell_Null_ReturnsEmpty()
    {
        Assert.Equal("", TextNormalization.EscapeMarkdownCell(null));
    }

    [Fact]
    public void EscapeMarkdownCell_EscapesPipes()
    {
        Assert.Equal("a\\|b", TextNormalization.EscapeMarkdownCell("a|b"));
    }

    [Fact]
    public void EscapeMarkdownCell_ReplacesNewlines()
    {
        Assert.Equal("line1<br/>line2", TextNormalization.EscapeMarkdownCell("line1\nline2"));
    }

    // ── CsvEscape ────────────────────────────────────────────────────

    [Fact]
    public void CsvEscape_Null_ReturnsEmpty()
    {
        Assert.Equal("", TextNormalization.CsvEscape(null));
    }

    [Fact]
    public void CsvEscape_NoSpecialChars_NoQuotes()
    {
        Assert.Equal("hello", TextNormalization.CsvEscape("hello"));
    }

    [Fact]
    public void CsvEscape_WithComma_Quotes()
    {
        Assert.Equal("\"a,b\"", TextNormalization.CsvEscape("a,b"));
    }

    [Fact]
    public void CsvEscape_WithQuotes_DoublesAndQuotes()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", TextNormalization.CsvEscape("say \"hi\""));
    }

    [Fact]
    public void CsvEscape_WithNewline_Quotes()
    {
        Assert.Equal("\"line1\nline2\"", TextNormalization.CsvEscape("line1\nline2"));
    }
}

