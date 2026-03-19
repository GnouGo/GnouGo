using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Templating;
using Xunit;

namespace GnOuGo.Flow.Tests.Templating;

public class MustacheParserTests
{
    [Fact]
    public void Parse_PlainText_ReturnsTextToken()
    {
        var tokens = MustacheParser.Parse("Hello world");
        Assert.Single(tokens);
        Assert.IsType<TextToken>(tokens[0]);
        Assert.Equal("Hello world", ((TextToken)tokens[0]).Text);
    }

    [Fact]
    public void Parse_Variable_ReturnsVariableToken()
    {
        var tokens = MustacheParser.Parse("{{name}}");
        Assert.Single(tokens);
        Assert.IsType<VariableToken>(tokens[0]);
        Assert.Equal("name", ((VariableToken)tokens[0]).Name);
    }

    [Fact]
    public void Parse_VariableWithSpaces_ReturnsTrimmedName()
    {
        var tokens = MustacheParser.Parse("{{ name }}");
        Assert.IsType<VariableToken>(tokens[0]);
        Assert.Equal("name", ((VariableToken)tokens[0]).Name);
    }

    [Fact]
    public void Parse_RawVariable_ReturnsRawToken()
    {
        var tokens = MustacheParser.Parse("{{{raw}}}");
        Assert.Single(tokens);
        Assert.IsType<RawVariableToken>(tokens[0]);
        Assert.Equal("raw", ((RawVariableToken)tokens[0]).Name);
    }

    [Fact]
    public void Parse_Section_ReturnsSectionToken()
    {
        var tokens = MustacheParser.Parse("{{#items}}content{{/items}}");
        Assert.Single(tokens);
        var section = Assert.IsType<SectionToken>(tokens[0]);
        Assert.Equal("items", section.Name);
        Assert.Single(section.Children);
    }

    [Fact]
    public void Parse_InvertedSection_ReturnsInvertedToken()
    {
        var tokens = MustacheParser.Parse("{{^empty}}no items{{/empty}}");
        Assert.Single(tokens);
        var inv = Assert.IsType<InvertedSectionToken>(tokens[0]);
        Assert.Equal("empty", inv.Name);
    }

    [Fact]
    public void Parse_Comment_IsIgnored()
    {
        var tokens = MustacheParser.Parse("before{{! comment }}after");
        Assert.Equal(2, tokens.Count);
        Assert.IsType<TextToken>(tokens[0]);
        Assert.IsType<TextToken>(tokens[1]);
    }

    [Fact]
    public void Parse_MixedContent_CorrectOrder()
    {
        var tokens = MustacheParser.Parse("Hello {{name}}, you have {{count}} items.");
        Assert.Equal(5, tokens.Count);
        Assert.IsType<TextToken>(tokens[0]);
        Assert.IsType<VariableToken>(tokens[1]);
        Assert.IsType<TextToken>(tokens[2]);
        Assert.IsType<VariableToken>(tokens[3]);
        Assert.IsType<TextToken>(tokens[4]);
    }

    [Fact]
    public void Parse_UnterminatedTag_Throws()
    {
        Assert.Throws<MustacheParseException>(() => MustacheParser.Parse("{{name"));
    }

    [Fact]
    public void Parse_UnterminatedTripleMustache_Throws()
    {
        Assert.Throws<MustacheParseException>(() => MustacheParser.Parse("{{{name"));
    }

    [Fact]
    public void Parse_EmptyTag_Throws()
    {
        Assert.Throws<MustacheParseException>(() => MustacheParser.Parse("{{}}"));
    }

    [Fact]
    public void Parse_UnclosedSection_Throws()
    {
        Assert.Throws<MustacheParseException>(() => MustacheParser.Parse("{{#items}}content"));
    }

    [Fact]
    public void Parse_UnexpectedCloseTag_Throws()
    {
        Assert.Throws<MustacheParseException>(() => MustacheParser.Parse("{{/items}}"));
    }

    [Fact]
    public void Parse_DotNotation_ReturnsDottedName()
    {
        var tokens = MustacheParser.Parse("{{person.name}}");
        var v = Assert.IsType<VariableToken>(tokens[0]);
        Assert.Equal("person.name", v.Name);
    }
}

public class MustacheEngineTests
{
    [Fact]
    public void Render_SimpleVariable_Replaces()
    {
        var data = new JsonObject { ["name"] = "World" };
        var result = MustacheEngine.Render("Hello {{name}}", data);
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void Render_HtmlEncoded_Variable()
    {
        var data = new JsonObject { ["text"] = "<b>bold</b>" };
        var result = MustacheEngine.Render("{{text}}", data);
        Assert.Equal("&lt;b&gt;bold&lt;/b&gt;", result);
    }

    [Fact]
    public void Render_RawVariable_NotEncoded()
    {
        var data = new JsonObject { ["text"] = "<b>bold</b>" };
        var result = MustacheEngine.Render("{{{text}}}", data);
        Assert.Equal("<b>bold</b>", result);
    }

    [Fact]
    public void Render_MissingVariable_Strict_Throws()
    {
        Assert.Throws<MustacheRenderException>(() =>
            MustacheEngine.Render("{{missing}}", new JsonObject(), strict: true));
    }

    [Fact]
    public void Render_MissingVariable_NotStrict_Empty()
    {
        var result = MustacheEngine.Render("{{missing}}", new JsonObject(), strict: false);
        Assert.Equal("", result);
    }

    [Fact]
    public void Render_NullData_Strict_Throws()
    {
        Assert.Throws<MustacheRenderException>(() =>
            MustacheEngine.Render("{{x}}", null, strict: true));
    }

    [Fact]
    public void Render_Section_WithTruthyValue_Renders()
    {
        var data = new JsonObject { ["show"] = true };
        var result = MustacheEngine.Render("{{#show}}visible{{/show}}", data);
        Assert.Equal("visible", result);
    }

    [Fact]
    public void Render_Section_WithFalsyValue_SkipsContent()
    {
        var data = new JsonObject { ["show"] = false };
        var result = MustacheEngine.Render("{{#show}}visible{{/show}}", data);
        Assert.Equal("", result);
    }

    [Fact]
    public void Render_Section_WithArray_IteratesItems()
    {
        var data = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["name"] = "A" },
                new JsonObject { ["name"] = "B" }
            )
        };
        var result = MustacheEngine.Render("{{#items}}{{name}},{{/items}}", data);
        Assert.Equal("A,B,", result);
    }

    [Fact]
    public void Render_Section_WithEmptyArray_SkipsContent()
    {
        var data = new JsonObject { ["items"] = new JsonArray() };
        var result = MustacheEngine.Render("{{#items}}X{{/items}}", data);
        Assert.Equal("", result);
    }

    [Fact]
    public void Render_Section_WithObject_PushesContext()
    {
        var data = new JsonObject
        {
            ["person"] = new JsonObject { ["name"] = "Alice" }
        };
        var result = MustacheEngine.Render("{{#person}}{{name}}{{/person}}", data);
        Assert.Equal("Alice", result);
    }

    [Fact]
    public void Render_InvertedSection_FalsyValue_Renders()
    {
        var data = new JsonObject { ["items"] = new JsonArray() };
        var result = MustacheEngine.Render("{{^items}}no items{{/items}}", data);
        Assert.Equal("no items", result);
    }

    [Fact]
    public void Render_InvertedSection_TruthyValue_Skips()
    {
        var data = new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1)) };
        var result = MustacheEngine.Render("{{^items}}no items{{/items}}", data);
        Assert.Equal("", result);
    }

    [Fact]
    public void Render_DotNotation_AccessesNestedProperty()
    {
        var data = new JsonObject { ["person"] = new JsonObject { ["name"] = "Bob" } };
        var result = MustacheEngine.Render("{{person.name}}", data);
        Assert.Equal("Bob", result);
    }

    [Fact]
    public void Render_BooleanValues_RendersAsString()
    {
        var data = new JsonObject { ["flag"] = true };
        var result = MustacheEngine.Render("{{flag}}", data);
        Assert.Equal("true", result);
    }

    [Fact]
    public void Render_NumberValues_RendersAsString()
    {
        var data = new JsonObject { ["count"] = 42 };
        var result = MustacheEngine.Render("{{count}}", data);
        Assert.Equal("42", result);
    }

    [Fact]
    public void Render_NullValue_InSection_Falsy()
    {
        var data = new JsonObject { ["x"] = (JsonNode?)null };
        var result = MustacheEngine.Render("{{#x}}yes{{/x}}{{^x}}no{{/x}}", data);
        Assert.Equal("no", result);
    }

    [Fact]
    public void Render_Dot_InSection_ReturnsCurrentElement()
    {
        var data = new JsonObject
        {
            ["items"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b"))
        };
        var result = MustacheEngine.Render("{{#items}}{{.}},{{/items}}", data);
        Assert.Equal("a,b,", result);
    }

    [Fact]
    public void Render_ZeroNumber_IsFalsy()
    {
        var data = new JsonObject { ["count"] = 0 };
        var result = MustacheEngine.Render("{{#count}}yes{{/count}}{{^count}}no{{/count}}", data);
        Assert.Equal("no", result);
    }

    [Fact]
    public void Render_EmptyString_IsFalsy()
    {
        var data = new JsonObject { ["text"] = "" };
        var result = MustacheEngine.Render("{{#text}}yes{{/text}}{{^text}}no{{/text}}", data);
        Assert.Equal("no", result);
    }

    [Fact]
    public void Render_PreParsedTokens()
    {
        var tokens = MustacheParser.Parse("Hello {{name}}");
        var data = new JsonObject { ["name"] = "Test" };
        var result = MustacheEngine.Render(tokens, data);
        Assert.Equal("Hello Test", result);
    }
}

