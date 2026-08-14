using GnOuGo.Agent.Server.Formatting;

namespace GnOuGo.Agent.Server.Tests;

public sealed class IndentedJsonFormatterTests
{
    [Theory]
    [InlineData("{\"status\":\"ready\",\"count\":2}", "{\n  \"status\": \"ready\",\n  \"count\": 2\n}")]
    [InlineData("[1,{\"enabled\":true}]", "[\n  1,\n  {\n    \"enabled\": true\n  }\n]")]
    public void TryFormat_ValidObjectOrArray_ProducesIndentedJson(string input, string expected)
    {
        var success = IndentedJsonFormatter.TryFormat(input, out var formattedJson);

        Assert.True(success);
        Assert.Equal(expected, formattedJson.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"status\":")]
    [InlineData("[1,")]
    public void TryFormat_NonJsonOrMalformedJson_ReturnsFalse(string input)
    {
        var success = IndentedJsonFormatter.TryFormat(input, out var formattedJson);

        Assert.False(success);
        Assert.Empty(formattedJson);
    }
}
