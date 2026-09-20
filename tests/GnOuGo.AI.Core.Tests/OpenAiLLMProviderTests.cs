using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GnOuGo.AI.Core.Tests;

public sealed class OpenAiLlmProviderTests
{
    [Theory]
    [InlineData(400, false)][InlineData(422, false)][InlineData(404, true)][InlineData(405, true)][InlineData(501, true)]
    public async Task NonTransientRejectionNeverChangesProtocolOrDropsTokenCeiling(int status, bool background)
    {
        var calls = new List<string>(); var bodies = new List<string>();
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath); bodies.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("Unsupported parameter: max_completion_tokens") };
        }));
        var provider = new OpenAiLLMProvider(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync("test", new() { Url = "https://proxy.example/v1" },
            new() { Prompt = "Hello", MaxOutputTokens = 1234, UseBackgroundMode = background }, TestContext.Current.CancellationToken));
        Assert.Single(calls); Assert.Equal(background ? "/v1/responses" : "/v1/chat/completions", calls[0]);
        Assert.Contains(background ? "max_output_tokens" : "max_completion_tokens", Assert.Single(bodies));
    }
    [Fact]
    public async Task RequiredOutputCeilingNeverFallsBackToAnUnboundedRequest()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(8192, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported max_completion_tokens") };
        }));
        var provider = new OpenAiLLMProvider(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync("model", new() { Type = "openai", Url = "https://gateway.example/v1", ApiKey = "test" },
            new() { Prompt = "bounded", MaxOutputTokens = 8192, RequireOutputTokenLimit = true }, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task JournaledRoutingDisablesHiddenInferenceRetriesWithoutMutatingProviderSettings()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("temporary upstream failure") });
        }));
        var provider = new ModelProviderOptions { Type = "openai", Url = "https://gateway.example/v1", ApiKey = "test", RetryPolicy = new() { MaxAttempts = 4 } };
        var options = new LLMOptions { DefaultProvider = "provider", DefaultModel = "model", Models = new() { ["provider"] = provider } };
        var client = new RoutingLLMClient(http, options);
        await Assert.ThrowsAsync<LLMProviderException>(() => client.CallAsync(new() { Prompt = "bounded", MaxOutputTokens = 8192, RequireOutputTokenLimit = true, DisableTransportRetries = true }, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.Equal(4, provider.RetryPolicy.MaxAttempts);
    }
    [Fact]
    public async Task RoutingClient_LargeModelCeilingDoesNotBecomeImplicitWireLimit()
    {
        var bodies = new List<string>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return JsonResponse("""
            {
              "choices": [ { "message": { "content": "ok" } } ]
            }
            """);
        });
        var options = new LLMOptions
        {
            DefaultProvider = "gateway",
            DefaultModel = "large-model",
            Models =
            {
                ["gateway"] = new ModelProviderOptions
                {
                    Url = "https://gateway.example/v1",
                    Type = "openai",
                    ApiKey = "secret"
                }
            },
            ModelOverrides =
            {
                ["openai/large-model"] = new LLMModelMetadata
                {
                    Id = "large-model",
                    ProviderType = "openai",
                    ContextWindowTokens = 1_050_000,
                    MaxInputTokens = 1_050_000,
                    MaxOutputTokens = 128_000
                }
            }
        };
        using var http = new HttpClient(handler);
        var client = new RoutingLLMClient(http, options);

        await client.CallAsync(new LLMClientRequest
        {
            Provider = "gateway",
            Model = "large-model",
            Prompt = "test"
        }, TestContext.Current.CancellationToken);
        await client.CallAsync(new LLMClientRequest
        {
            Provider = "gateway",
            Model = "large-model",
            Prompt = "test",
            MaxOutputTokens = 4_096
        }, TestContext.Current.CancellationToken);

        using var unspecified = JsonDocument.Parse(bodies[0]);
        using var explicitLimit = JsonDocument.Parse(bodies[1]);
        Assert.False(unspecified.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal(4_096, explicitLimit.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

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
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger);

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
                    "name": { "type": "string" },
                    "value": { "type": ["string", "number", "boolean", "null"] },
                    "metadata": {
                      "type": ["object", "null"],
                      "properties": {
                        "source": { "type": "string" }
                      },
                      "required": ["source"]
                    }
                  },
                  "required": ["name", "value", "metadata"]
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
        var responseSchema = format.GetProperty("schema");
        Assert.Equal("object", responseSchema.GetProperty("type").GetString());
        Assert.False(responseSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["string", "number", "boolean", "null"],
            responseSchema.GetProperty("properties").GetProperty("value").GetProperty("type")
                .EnumerateArray().Select(type => type.GetString()!).ToArray());
        var metadata = responseSchema.GetProperty("properties").GetProperty("metadata");
        Assert.Equal(
            ["object", "null"],
            metadata.GetProperty("type").EnumerateArray().Select(type => type.GetString()!).ToArray());
        Assert.False(metadata.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("UseBackgroundMode=True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("OpenAI Responses background call starting", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains("https://api.openai.test", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("OpenAI Responses background call completed", StringComparison.Ordinal)
            && e.Message.Contains("resp_123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not found")]
    [InlineData("{\"error\":{\"message\":\"model not found\",\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":\"model_not_found\"}}")]
    public async Task CallAsync_WithOfficialNotFound_NeverFallsBackOrPoisonsCache(string responseBody)
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
        var provider = new OpenAiLLMProvider(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiLLMProvider>.Instance);
        var options = new ModelProviderOptions { Url = "https://api.openai.com", ApiKey = "secret", Type = "openai" };

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
    public async Task CallAsync_RequestSpecificResponses404IsNotCachedOrRoutedToChat()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":{\"code\":\"model_not_found\",\"message\":\"requested model does not exist\"}}")
            });
        });

        using var http = new HttpClient(handler);
        var provider = new OpenAiLLMProvider(http);
        var options = new ModelProviderOptions { Url = "https://proxy.example", ApiKey = "secret", Type = "openai" };

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
    }

    [Theory]
    [InlineData("https://proxy.example", "/v1/chat/completions")]
    [InlineData("https://proxy.example/v1", "/v1/chat/completions")]
    [InlineData("https://proxy.example/openai/deployments/model", "/openai/deployments/model/chat/completions")]
    public async Task CallAsync_ChatCompletionsBackgroundPolicyBypassesResponsesRoute(string endpoint, string expectedPath)
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse("""
            {
              "choices": [ { "message": { "content": "ok" } } ]
            }
            """));
        });

        using var http = new HttpClient(handler);
        var provider = new OpenAiLLMProvider(http);
        var options = new ModelProviderOptions
        {
            Url = endpoint,
            ApiKey = "secret",
            Type = "openai",
            RequestPolicy = new LLMProviderRequestPolicyOptions
            {
                BackgroundProtocol = LLMBackgroundProtocolMode.ChatCompletions
            }
        };

        await provider.CallAsync(
            "model",
            options,
            new LLMClientRequest { Prompt = "Hello", UseBackgroundMode = true },
            CancellationToken.None);

        Assert.Equal([expectedPath], requests);
    }

    [Theory]
    [InlineData("https://proxy.example", null, "bad request")]
    [InlineData("https://proxy.example", 1234, "{\"error\":{\"message\":\"response format rejected\",\"param\":\"response_format\"}}")]
    [InlineData("https://api.openai.com", 1234, "bad request")]
    public async Task CallAsync_WithIneligibleLegacyChatFailure_NeverDuplicatesRequest(
        string endpoint,
        int? maxOutputTokens,
        string responseBody)
    {
        var requestBodies = new List<string>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            requestBodies.Add(await req.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(responseBody)
            };
        });

        using var http = new HttpClient(handler);
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
            "gpt-5.5-2026-04-24",
            new ModelProviderOptions { Url = endpoint, ApiKey = "secret", Type = "openai" },
            new LLMClientRequest
            {
                Prompt = "Hello",
                MaxOutputTokens = maxOutputTokens,
                UseBackgroundMode = false
            },
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Single(requestBodies);
        using var posted = JsonDocument.Parse(requestBodies[0]);
        Assert.Equal(maxOutputTokens.HasValue, posted.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("retrying once without only that optional field", StringComparison.Ordinal));
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
        var provider = new OpenAiLLMProvider(http);
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
