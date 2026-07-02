using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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
        Assert.Equal(16384, root.GetProperty("max_tokens").GetInt32());
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
    public async Task CallAsync_UsesForcedToolForStructuredOutputAndParsesToolInput()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(async req =>
        {
            capturedBody = req.Content == null ? null : await req.Content.ReadAsStringAsync();

            return JsonResponse("""
            {
              "id": "msg_123",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-20250514",
              "content": [
                {
                  "type": "tool_use",
                  "id": "toolu_json",
                  "name": "gnougo_structured_output",
                  "input": {
                    "servers": [
                      { "name": "github", "reason": "Repository tools are relevant." }
                    ]
                  }
                }
              ],
              "stop_reason": "tool_use",
              "usage": { "input_tokens": 20, "output_tokens": 9 }
            }
            """);
        });

        using var http = new HttpClient(handler);
        var provider = new AnthropicLLMProvider(http);

        var response = await provider.CallAsync(
            "claude-opus-4-8",
            new ModelProviderOptions { Url = "https://api.anthropic.test/v1", ApiKey = "sk-ant", Type = "anthropic" },
            new LLMClientRequest
            {
                Prompt = "Select relevant servers.",
                Reasoning = "medium",
                StructuredOutputStrict = true,
                StructuredOutputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "servers": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string" },
                          "reason": { "type": "string" }
                        },
                        "required": ["name", "reason"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["servers"],
                  "additionalProperties": false
                }
                """)
            },
            CancellationToken.None);

        using var posted = JsonDocument.Parse(capturedBody!);
        var root = posted.RootElement;
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.DoesNotContain("Return only valid JSON", root.GetProperty("messages")[0].GetProperty("content").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("gnougo_structured_output", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.True(tool.GetProperty("strict").GetBoolean());

        var toolChoice = root.GetProperty("tool_choice");
        Assert.Equal("tool", toolChoice.GetProperty("type").GetString());
        Assert.Equal("gnougo_structured_output", toolChoice.GetProperty("name").GetString());

        Assert.NotNull(response.Json);
        var server = response.Json!["servers"]!.AsArray()[0]!.AsObject();
        Assert.Equal("github", server["name"]!.GetValue<string>());
        Assert.NotNull(response.ToolCalls);
        Assert.Equal("gnougo_structured_output", Assert.Single(response.ToolCalls!).Name);
    }

    [Fact]
    public void BuildMessagesPayload_OmitsStrictToolUseForClaudeOpus47()
    {
        var payload = AnthropicLLMProvider.BuildMessagesPayload(
            "claude-opus-4-7",
            "Return JSON.",
            structuredOutputSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "answer": { "type": "string" }
              },
              "required": ["answer"],
              "additionalProperties": false
            }
            """),
            structuredOutputStrict: true);

        using var posted = JsonDocument.Parse(payload);
        var tool = posted.RootElement.GetProperty("tools")[0];
        Assert.Equal("gnougo_structured_output", tool.GetProperty("name").GetString());
        Assert.False(tool.TryGetProperty("strict", out _));
    }

    [Fact]
    public void BuildMessagesPayload_UsesAdaptiveThinkingForClaudeOpus47()
    {
        var payload = AnthropicLLMProvider.BuildMessagesPayload(
            "claude-opus-4-7",
            "Think deeply.",
            temperature: 0.2,
            reasoning: "high");

        using var posted = JsonDocument.Parse(payload);
        var root = posted.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void BuildMessagesPayload_OmitsAdaptiveThinkingForClaudeOpus47WhenReasoningIsOff()
    {
        var payload = AnthropicLLMProvider.BuildMessagesPayload(
            "claude-opus-4-7",
            "No thinking requested.",
            temperature: 0.2,
            reasoning: "off");

        using var posted = JsonDocument.Parse(payload);
        var root = posted.RootElement;
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("output_config", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void BuildBatchPayload_UsesAdaptiveThinkingForClaudeOpus48()
    {
        var payload = AnthropicLLMProvider.BuildBatchPayload(
            "anthropic/claude-opus-4-8",
            "Think to solve this.",
            reasoning: "xhigh");

        using var posted = JsonDocument.Parse(payload);
        var request = posted.RootElement.GetProperty("requests")[0].GetProperty("params");
        Assert.Equal("adaptive", request.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(request.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        Assert.Equal("xhigh", request.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public void BuildBatchPayload_OmitsTemperatureForClaudeOpus48WithoutReasoning()
    {
        var payload = AnthropicLLMProvider.BuildBatchPayload(
            "anthropic/claude-opus-4-8",
            "Use the default behavior.",
            temperature: 0.2);

        using var posted = JsonDocument.Parse(payload);
        var request = posted.RootElement.GetProperty("requests")[0].GetProperty("params");
        Assert.False(request.TryGetProperty("thinking", out _));
        Assert.False(request.TryGetProperty("output_config", out _));
        Assert.False(request.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("anthropic/claude-opus-4-7", false)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("anthropic/claude-opus-4-8", true)]
    public void SupportsStrictToolUse_IsModelAware(string model, bool expected)
        => Assert.Equal(expected, AnthropicLLMProvider.SupportsStrictToolUse(model));

    [Theory]
    [InlineData("claude-opus-4-7", true)]
    [InlineData("anthropic/claude-opus-4-7", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("anthropic/claude-opus-4-8", true)]
    [InlineData("claude-sonnet-4-20250514", false)]
    public void RequiresAdaptiveThinking_IsModelAware(string model, bool expected)
        => Assert.Equal(expected, AnthropicLLMProvider.RequiresAdaptiveThinking(model));

    [Theory]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("anthropic/claude-opus-4-7", false)]
    [InlineData("claude-opus-4-8", false)]
    [InlineData("anthropic/claude-opus-4-8", false)]
    [InlineData("claude-sonnet-4-20250514", true)]
    public void SupportsTemperature_IsModelAware(string model, bool expected)
        => Assert.Equal(expected, AnthropicLLMProvider.SupportsTemperature(model));

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

    [Fact]
    public async Task CallAsync_WhenBatchUnsupported_LogsTechnicalFallbackDetails()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            calls.Add(req.RequestUri!.ToString());
            if (req.RequestUri!.AbsolutePath.EndsWith("/messages/batches", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = "Not Found",
                    Content = new StringContent("""
                    {"type":"error","error":{"type":"not_found_error","message":"Not found: /v1/messages/batches"}}
                    """)
                });
            }

            return Task.FromResult(JsonResponse("""
            {
              "id": "msg_123",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-20250514",
              "content": [{ "type": "text", "text": "sync ok" }],
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """));
        });

        var logger = new CapturingLogger<AnthropicLLMProvider>();
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new AnthropicLLMProvider(http, logger, cache);

        var response = await provider.CallAsync(
            "claude-sonnet-4-20250514",
            new ModelProviderOptions { Url = "https://api.anthropic.test/v1", ApiKey = "sk-ant", Type = "anthropic" },
            new LLMClientRequest
            {
                Prompt = "Hello",
                UseBackgroundMode = true
            },
            CancellationToken.None);
        var secondResponse = await provider.CallAsync(
            "claude-sonnet-4-20250514",
            new ModelProviderOptions { Url = "https://api.anthropic.test/v1", ApiKey = "sk-ant", Type = "anthropic" },
            new LLMClientRequest
            {
                Prompt = "Hello again",
                UseBackgroundMode = true
            },
            CancellationToken.None);

        Assert.Equal("sync ok", response.Text);
        Assert.Equal("sync ok", secondResponse.Text);
        Assert.Equal(
            [
                "https://api.anthropic.test/v1/messages/batches",
                "https://api.anthropic.test/v1/messages",
                "https://api.anthropic.test/v1/messages"
            ],
            calls);
        Assert.Single(calls, call => call.EndsWith("/messages/batches", StringComparison.Ordinal));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Anthropic batch API not available", warning.Message);
        Assert.Contains("https://api.anthropic.test/v1/messages/batches", warning.Message);
        Assert.Contains("StatusCode: 404", warning.Message);
        Assert.Contains("ReasonPhrase: Not Found", warning.Message);
        Assert.Contains("not_found_error", warning.Message);
        Assert.Contains("Not found: /v1/messages/batches", warning.Message);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("previously returned unsupported", StringComparison.Ordinal)
            && entry.Message.Contains("skipping background mode", StringComparison.Ordinal));
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
