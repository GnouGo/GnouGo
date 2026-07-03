using System.Text.Json;
using GnOuGo.Mcp.Core;
using ModelContextProtocol.Protocol;

namespace GnOuGo.Mcp.Core.Tests;

public sealed class McpToolErrorNormalizerTests
{
    [Fact]
    public void Normalize_ErrorResultWithPlainTextContent_AddsStructuredErrorEnvelope()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "An error occurred invoking 'git_clone': http parser did not consume entire buffer: Invalid EOF state" }]
        };

        McpToolErrorNormalizer.Normalize(result);

        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var structured = result.StructuredContent!.Value;
        Assert.False(structured.GetProperty("success").GetBoolean());
        Assert.False(structured.GetProperty("ok").GetBoolean());
        Assert.Equal("MCP_TOOL_ERROR", structured.GetProperty("error_code").GetString());
        Assert.Equal("An error occurred invoking 'git_clone': http parser did not consume entire buffer: Invalid EOF state", structured.GetProperty("error_message").GetString());
        Assert.Equal("An error occurred invoking 'git_clone': http parser did not consume entire buffer: Invalid EOF state", structured.GetProperty("message").GetString());

        var textBlock = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("An error occurred invoking 'git_clone': http parser did not consume entire buffer: Invalid EOF state", textBlock.Text);
    }

    [Fact]
    public void Normalize_ErrorResultWithStructuredContent_DoesNotOverwriteStructuredContent()
    {
        var result = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                success = false,
                error_code = "CUSTOM_ERROR",
                error_message = "Custom failure."
            }),
            Content = [new TextContentBlock { Text = "Fallback text." }]
        };

        McpToolErrorNormalizer.Normalize(result);

        Assert.True(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.False(structured.GetProperty("success").GetBoolean());
        Assert.Equal("CUSTOM_ERROR", structured.GetProperty("error_code").GetString());
        Assert.Equal("Custom failure.", structured.GetProperty("error_message").GetString());
        Assert.False(structured.TryGetProperty("ok", out _));
    }

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
        Assert.False(result.StructuredContent.HasValue);
    }
}
