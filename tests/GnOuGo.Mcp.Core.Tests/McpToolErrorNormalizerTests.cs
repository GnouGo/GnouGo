using System.Text.Json;
using GnOuGo.Mcp.Core;
using ModelContextProtocol.Protocol;

namespace GnOuGo.Mcp.Core.Tests;

public sealed class McpToolErrorNormalizerTests
{
    [Theory]
    [InlineData("""{"success":false,"error_code":"NOT_FOUND","error_message":"Missing."}""")]
    [InlineData("""{"ok":false,"message":"Rejected."}""")]
    [InlineData("""{"status":"error","message":"Failed."}""")]
    [InlineData("""{"error":"Remote API failed."}""")]
    [InlineData("""{"code":"POLICY_OR_INPUT_ERROR","message":"Denied."}""")]
    public void Normalize_TextJsonFailureEnvelope_SetsIsError(string json)
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }]
        };

        McpToolErrorNormalizer.Normalize(result);

        Assert.True(result.IsError);
    }

    [Fact]
    public void Normalize_StructuredFailureEnvelope_SetsIsError()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                success = false,
                error_code = "INVALID_INPUT",
                error_message = "Bad input."
            })
        };

        McpToolErrorNormalizer.Normalize(result);

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("""{"success":true,"message":"0 errors found."}""")]
    [InlineData("0 errors found.")]
    [InlineData("""{"status":"ok","message":"Recovered from previous error."}""")]
    public void Normalize_SuccessfulOrPlainDiagnosticContent_DoesNotSetIsError(string text)
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }]
        };

        McpToolErrorNormalizer.Normalize(result);

        Assert.False(result.IsError == true);
    }
}
