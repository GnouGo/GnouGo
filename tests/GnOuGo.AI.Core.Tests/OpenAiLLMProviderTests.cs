using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GnOuGo.AI.Core.Tests;

public sealed class OpenAiLlmProviderTests
{
    [Fact]
    public async Task CallAsync_WithStructuredBackgroundMode_PreservesContractAndParsesPolledJson()
    {
        var requests = new List<(HttpMethod Method, string Url, string? Body)>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            var body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
            requests.Add((req.Method, req.RequestUri!.ToString(), body));

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/v1/responses", StringComparison.Ordinal))
            {
                return JsonResponse("""
                {
                  "id": "resp_123",
                  "status": "queued"
                }
                """);
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/v1/responses/resp_123", StringComparison.Ordinal))
            {
                return JsonResponse("""
                {
                  "id": "resp_123",
                  "status": "completed",
                  "output": [
                    {
                      "type": "message",
                      "role": "assistant",
                      "content": [
                        { "type": "output_text", "text": "{\"name\":\"generated\"}" }
                      ]
                    }
                  ],
                  "usage": { "input_tokens": 10, "output_tokens": 5, "total_tokens": 15 }
                }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found")
            };
        });

        using var http = new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger, cache);

        var response = await provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://api.openai.test", ApiKey = "secret", Type = "openai" },
            new LLMClientRequest
            {
                Prompt = "Generate workflow",
                Reasoning = "medium",
                UseBackgroundMode = true,
                MaxOutputTokens = 1_234,
                StructuredOutputStrict = true,
                StructuredOutputSchema = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" }
                  },
                  "required": ["name"]
                }
                """)
            },
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), http.Timeout);
        Assert.Equal("{\"name\":\"generated\"}", response.Text);
        Assert.Equal("generated", response.Json!["name"]!.GetValue<string>());
        Assert.Equal(2, requests.Count);
        Assert.Equal((HttpMethod.Post, "https://api.openai.test/v1/responses"), (requests[0].Method, requests[0].Url));
        Assert.Equal((HttpMethod.Get, "https://api.openai.test/v1/responses/resp_123"), (requests[1].Method, requests[1].Url));

        using var posted = JsonDocument.Parse(requests[0].Body!);
        var root = posted.RootElement;
        Assert.True(root.GetProperty("background").GetBoolean());
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(1_234, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("Generate workflow", root.GetProperty("input")[0].GetProperty("content").GetString());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("output", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("UseBackgroundMode=True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("OpenAI Responses background call starting", StringComparison.Ordinal)
            && e.Message.Contains("https://api.openai.test/v1/responses", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("OpenAI Responses background call completed", StringComparison.Ordinal)
            && e.Message.Contains("resp_123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "responses endpoint not found")]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"/responses route not found\",\"type\":\"invalid_request_error\",\"param\":\"route\",\"code\":\"route_not_found\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported parameter: background\",\"type\":\"invalid_request_error\",\"param\":\"background\",\"code\":\"unsupported_parameter\"}}")]
    public async Task CallAsync_WithExplicitProxyBackgroundIncompatibility_FallsBackOnceAndCaches(
        HttpStatusCode statusCode,
        string responseBody)
    {
        var requests = new List<(HttpMethod Method, string Url)>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add((req.Method, req.RequestUri!.ToString()));

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/v1/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody)
                });
            }

            return Task.FromResult(JsonResponse("""
            {
              "choices": [
                { "message": { "content": "fallback ok" } }
              ]
            }
            """));
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger, cache);

        var response = await provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://proxy.example", ApiKey = "secret", Type = "openai" },
            new LLMClientRequest
            {
                Prompt = "Hello",
                UseBackgroundMode = true
            },
            CancellationToken.None);
        var secondResponse = await provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://proxy.example", ApiKey = "secret", Type = "openai" },
            new LLMClientRequest
            {
                Prompt = "Hello again",
                UseBackgroundMode = true
            },
            CancellationToken.None);

        Assert.Equal("fallback ok", response.Text);
        Assert.Equal("fallback ok", secondResponse.Text);
        Assert.Equal((HttpMethod.Post, "https://proxy.example/v1/responses"), requests[0]);
        Assert.Equal((HttpMethod.Post, "https://proxy.example/v1/chat/completions"), requests[1]);
        Assert.Equal((HttpMethod.Post, "https://proxy.example/v1/chat/completions"), requests[2]);
        Assert.Equal(3, requests.Count);
        Assert.Single(requests, request => request.Url == "https://proxy.example/v1/responses");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("OpenAI Responses background API not available", StringComparison.Ordinal)
            && e.Message.Contains("falling back to Chat Completions", StringComparison.Ordinal)
            && e.Message.Contains($"StatusCode={(int)statusCode}", StringComparison.Ordinal)
            && e.Message.Contains(responseBody, StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("previously returned unsupported", StringComparison.Ordinal)
            && e.Message.Contains("skipping background mode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://api.openai.com", "not found")]
    [InlineData("https://proxy.example", "{\"error\":{\"message\":\"model not found\",\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":\"model_not_found\"}}")]
    public async Task CallAsync_WithOfficialOrModelSpecificNotFound_NeverFallsBackOrPoisonsCache(
        string endpoint,
        string responseBody)
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(responseBody)
            });
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenAiLLMProvider(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiLLMProvider>.Instance, cache);
        var options = new ModelProviderOptions { Url = endpoint, ApiKey = "secret", Type = "openai" };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
                "missing-model",
                options,
                new LLMClientRequest { Prompt = "Hello", UseBackgroundMode = true },
                CancellationToken.None));
            Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        }

        Assert.Equal(["/v1/responses", "/v1/responses"], requests);
        Assert.DoesNotContain(requests, path => path.EndsWith("/chat/completions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallAsync_WithUnexpectedBackgroundStatus_DoesNotPoll()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return Task.FromResult(JsonResponse("""
            {
              "id": "resp_unexpected",
              "status": "pending"
            }
            """));
        });

        using var http = new HttpClient(handler);
        var provider = new OpenAiLLMProvider(http);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://api.openai.com", ApiKey = "secret", Type = "openai" },
            new LLMClientRequest { Prompt = "Hello", UseBackgroundMode = true },
            CancellationToken.None));

        Assert.Contains("unexpected status 'pending'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task CallAsync_WithOfficialProviderError_PreservesStatusAndRedactsSensitiveValues()
    {
        const string prompt = "private planning prompt";
        const string apiKey = "sk-sensitive-test-value";
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"request '{prompt}' rejected for credential '{apiKey}'")
            }));

        using var http = new HttpClient(handler);
        var provider = new OpenAiLLMProvider(http);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://api.openai.com", ApiKey = apiKey, Type = "openai" },
            new LLMClientRequest { Prompt = prompt, UseBackgroundMode = true },
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.DoesNotContain(prompt, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains("<redacted>", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_WithProxyAuthenticationFailureOnMethodStatus_NeverFallsBackOrCaches()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            {
                Content = new StringContent("invalid api key")
            });
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenAiLLMProvider(http, backgroundModeCache: cache);
        var options = new ModelProviderOptions { Url = "https://proxy.example", ApiKey = "secret", Type = "openai" };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
                "gpt-4o-mini",
                options,
                new LLMClientRequest { Prompt = "Hello", UseBackgroundMode = true },
                CancellationToken.None));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, failure.StatusCode);
        }

        Assert.Equal(["/v1/responses", "/v1/responses"], requests);
    }

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
