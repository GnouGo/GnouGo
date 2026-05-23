using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core.Tests;

public sealed class AnthropicLlmProviderTests
{
    [Fact]
    public async Task CallAsync_SendsAnthropicMessagesRequestAndParsesResponse()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            capturedBody = req.Content == null ? null : await req.Content.ReadAsStringAsync();

            return JsonResponse("""
            {
              "id": "msg_123",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-20250514",
              "content": [
                { "type": "text", "text": "hello" },
                { "type": "tool_use", "id": "toolu_1", "name": "lookup", "input": { "query": "docs" } }
              ],
              "usage": { "input_tokens": 12, "output_tokens": 5 }
            }
            """);
        });

        using var http = new HttpClient(handler);
        var provider = new AnthropicLLMProvider(http);

        var response = await provider.CallAsync(
            "claude-sonnet-4-20250514",
            new ModelProviderOptions { Url = "https://api.anthropic.test/v1", ApiKey = "sk-ant", Type = "anthropic" },
            new LLMClientRequest
            {
                Prompt = "Hello",
                Temperature = 0.2,
                Reasoning = "medium",
                Tools =
                [
                    new LLMToolDef
                    {
                        Name = "lookup",
                        Description = "Lookup docs",
                        InputSchema = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["query"] = new JsonObject { ["type"] = "string" }
                            }
                        }
                    }
                ]
            },
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.anthropic.test/v1/messages", capturedRequest.RequestUri!.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("anthropic-version", out var versions));
        Assert.Equal("2023-06-01", Assert.Single(versions));
        Assert.True(capturedRequest.Headers.TryGetValues("x-api-key", out var keys));
        Assert.Equal("sk-ant", Assert.Single(keys));

        using var posted = JsonDocument.Parse(capturedBody!);
        var root = posted.RootElement;
        Assert.Equal("claude-sonnet-4-20250514", root.GetProperty("model").GetString());
        Assert.Equal(5120, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(4096, root.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal("Hello", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("lookup", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());

        Assert.Equal("hello", response.Text);
        Assert.Equal(12, response.Usage!["input_tokens"]!.GetValue<int>());
        Assert.NotNull(response.ToolCalls);
        var toolCall = Assert.Single(response.ToolCalls!);
        Assert.Equal("toolu_1", toolCall.Id);
        Assert.Equal("lookup", toolCall.Name);
        Assert.Equal("docs", toolCall.Arguments!["query"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListModelsAsync_ParsesAnthropicModelCatalog()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var requestUrl = req.RequestUri!.ToString();
            Assert.Equal("https://api.anthropic.test/v1/models", requestUrl);
            return Task.FromResult(JsonResponse("""
            {
              "data": [
                { "id": "claude-sonnet-4-20250514", "display_name": "Claude Sonnet 4" },
                { "id": "claude-3-5-haiku-20241022" }
              ]
            }
            """));
        });

        using var http = new HttpClient(handler);
        var provider = new AnthropicLLMProvider(http);

        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://api.anthropic.test/v1", ApiKey = "sk-ant", Type = "anthropic" },
            CancellationToken.None);

        Assert.Collection(
            models,
            model =>
            {
                Assert.Equal("claude-sonnet-4-20250514", model.Id);
                Assert.Equal("Claude Sonnet 4", model.DisplayName);
                Assert.Equal("anthropic", model.ProviderType);
                Assert.Equal("anthropic", model.OwnedBy);
            },
            model => Assert.Equal("claude-3-5-haiku-20241022", model.Id));
    }

    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://proxy.example/v1/messages", "https://proxy.example/v1/messages")]
    public void BuildMessagesUrl_NormalizesEndpoint(string input, string expected)
        => Assert.Equal(expected, AnthropicLLMProvider.BuildMessagesUrl(input));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}



