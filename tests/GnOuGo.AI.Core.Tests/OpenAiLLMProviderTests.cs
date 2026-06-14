using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GnOuGo.AI.Core.Tests;

public sealed class OpenAiLlmProviderTests
{
    [Fact]
    public async Task CallAsync_WithBackgroundMode_UsesResponsesApiAndPollsUntilCompleted()
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
                        { "type": "output_text", "text": "version: \"1.0\"\nname: generated\nworkflows: {}" }
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
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger);

        var response = await provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://api.openai.test", ApiKey = "secret", Type = "openai" },
            new LLMClientRequest
            {
                Prompt = "Generate workflow",
                Reasoning = "medium",
                UseBackgroundMode = true
            },
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), http.Timeout);
        Assert.Equal("version: \"1.0\"\nname: generated\nworkflows: {}", response.Text);
        Assert.Equal(2, requests.Count);
        Assert.Equal((HttpMethod.Post, "https://api.openai.test/v1/responses"), (requests[0].Method, requests[0].Url));
        Assert.Equal((HttpMethod.Get, "https://api.openai.test/v1/responses/resp_123"), (requests[1].Method, requests[1].Url));

        using var posted = JsonDocument.Parse(requests[0].Body!);
        var root = posted.RootElement;
        Assert.True(root.GetProperty("background").GetBoolean());
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("Generate workflow", root.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("UseBackgroundMode=True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("OpenAI Responses background call starting", StringComparison.Ordinal)
            && e.Message.Contains("https://api.openai.test/v1/responses", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("OpenAI Responses background call completed", StringComparison.Ordinal)
            && e.Message.Contains("resp_123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallAsync_WithBackgroundUnsupported_FallsBackToChatCompletions()
    {
        var requests = new List<(HttpMethod Method, string Url)>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add((req.Method, req.RequestUri!.ToString()));

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/v1/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("responses endpoint not found")
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
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger);

        var response = await provider.CallAsync(
            "gpt-4o-mini",
            new ModelProviderOptions { Url = "https://proxy.example", ApiKey = "secret", Type = "openai" },
            new LLMClientRequest
            {
                Prompt = "Hello",
                UseBackgroundMode = true
            },
            CancellationToken.None);

        Assert.Equal("fallback ok", response.Text);
        Assert.Equal((HttpMethod.Post, "https://proxy.example/v1/responses"), requests[0]);
        Assert.Equal((HttpMethod.Post, "https://proxy.example/v1/chat/completions"), requests[1]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("OpenAI Responses background API not available", StringComparison.Ordinal)
            && e.Message.Contains("falling back to Chat Completions", StringComparison.Ordinal)
            && e.Message.Contains("StatusCode=404", StringComparison.Ordinal)
            && e.Message.Contains("responses endpoint not found", StringComparison.Ordinal));
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

