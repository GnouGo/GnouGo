using Bunit;
using GnOuGo.Agent.Server.Components.Markdown;
using GnOuGo.Agent.Server.Components.Pages;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlainTextContentTests : BunitContext
{
    [Fact]
    public void Render_PreservesMultilineLargeTextAndEncodesMarkup()
    {
        var content = string.Join(
            "\n",
            Enumerable.Range(0, 2_000).Select(static index => $"line {index:D4} <script>alert('no')</script>"));

        var cut = Render<PlainTextContent>(parameters => parameters
            .Add(component => component.Class, "user-content")
            .Add(component => component.Content, content));

        var rendered = cut.Find(".user-content");
        Assert.Equal(content, rendered.TextContent);
        Assert.DoesNotContain("<script>", rendered.InnerHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", rendered.InnerHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreserveForSubmission_NormalizesLineEndingsWithoutTrimmingOrTruncating()
    {
        var input = "\r\n  first line\r\nsecond line  \r" + new string('x', 64 * 1024) + "\n";

        var preserved = ChatComposerText.PreserveForSubmission(input);

        Assert.StartsWith("\n  first line\nsecond line  \n", preserved, StringComparison.Ordinal);
        Assert.EndsWith(new string('x', 64 * 1024) + "\n", preserved, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', preserved);
    }
}
