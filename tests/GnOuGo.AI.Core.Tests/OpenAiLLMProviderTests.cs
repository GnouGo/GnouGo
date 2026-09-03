using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GnOuGo.AI.Core.Tests;

public sealed class OpenAiLlmProviderTests
{
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
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.NotFound, "responses endpoint not found")]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"/responses route not found\",\"type\":\"invalid_request_error\",\"param\":\"route\",\"code\":\"route_not_found\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported parameter: background\",\"type\":\"invalid_request_error\",\"param\":\"background\",\"code\":\"unsupported_parameter\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "unprocessable response contract")]
    public async Task CallAsync_WithProxyBackgroundIncompatibility_CachesOnlyAfterSuccessfulFallback(
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
            && e.Message.Contains("Chat Completions fallback succeeded", StringComparison.Ordinal)
            && e.Message.Contains($"StatusCode={(int)statusCode}", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains(responseBody, StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("previously returned unsupported", StringComparison.Ordinal)
            && e.Message.Contains("skipping background mode", StringComparison.Ordinal));
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
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenAiLLMProvider(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiLLMProvider>.Instance, cache);
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
    public async Task CallAsync_WithFailedProxyFallback_CachesDeterministicResponsesIncompatibility()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            var responseBody = req.RequestUri.AbsolutePath.EndsWith("/v1/responses", StringComparison.Ordinal)
                ? "not found"
                : "{\"error\":{\"message\":\"model not found\",\"param\":\"model\",\"code\":\"model_not_found\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(responseBody)
            });
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger, cache);
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

        Assert.Equal(
            ["/v1/responses", "/v1/chat/completions", "/v1/chat/completions"],
            requests);
        Assert.Single(logger.Entries, entry =>
            entry.Message.Contains("Cached OpenAI Responses background unsupported result", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("previously returned unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallAsync_WithProxyFallback_PreservesStructuredOutputReasoningAndTokenLimit()
    {
        var requests = new List<(string Route, string? Body)>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            requests.Add((
                req.RequestUri!.PathAndQuery,
                req.Content == null ? null : await req.Content.ReadAsStringAsync()));

            if (req.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found")
                };
            }

            return JsonResponse("""
            {
              "choices": [
                { "message": { "content": "{\"name\":\"generated\"}" } }
              ]
            }
            """);
        });

        using var http = new HttpClient(handler);
        var provider = new OpenAiLLMProvider(http);
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "metadata": {
              "type": "object",
              "properties": {
                "source": { "type": "string" }
              },
              "required": ["source"]
            }
          },
          "required": ["name", "metadata"]
        }
        """);

        var response = await provider.CallAsync(
            "gpt-5.5-2026-04-24",
            new ModelProviderOptions
            {
                Url = "https://proxy.example/providers/openai/deployments/gpt-5.5-2026-04-24",
                ApiVersion = "2025-01-01-preview",
                ApiKey = "secret",
                Type = "openai"
            },
            new LLMClientRequest
            {
                Prompt = "Generate workflow",
                Reasoning = "medium",
                MaxOutputTokens = 1_234,
                StructuredOutputSchema = schema,
                StructuredOutputStrict = true,
                UseBackgroundMode = true
            },
            CancellationToken.None);

        Assert.Equal("generated", response.Json!["name"]!.GetValue<string>());
        Assert.Equal(
            [
                "/providers/openai/deployments/gpt-5.5-2026-04-24/responses?api-version=2025-01-01-preview",
                "/providers/openai/deployments/gpt-5.5-2026-04-24/chat/completions?api-version=2025-01-01-preview"
            ],
            requests.Select(request => request.Route));

        using var posted = JsonDocument.Parse(requests[1].Body!);
        var root = posted.RootElement;
        Assert.Equal("gpt-5.5-2026-04-24", root.GetProperty("model").GetString());
        Assert.Equal(1_234, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("Generate workflow", root.GetProperty("messages")[0].GetProperty("content").GetString());

        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var jsonSchema = format.GetProperty("json_schema");
        Assert.Equal("output", jsonSchema.GetProperty("name").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        var responseSchema = jsonSchema.GetProperty("schema");
        Assert.False(responseSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(responseSchema.GetProperty("properties").GetProperty("metadata").GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"request rejected\",\"type\":\"invalid_request_error\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"message\":\"unsupported token limit\",\"param\":\"max_completion_tokens\"}}")]
    public async Task CallAsync_WithProxyChatTokenLimitRejection_RetriesLegacyAndCachesCompatibilityLadder(
        HttpStatusCode standardStatusCode,
        string standardErrorBody)
    {
        var requests = new List<(string Route, string? Body, string? BearerToken)>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            var body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
            requests.Add((req.RequestUri!.PathAndQuery, body, req.Headers.Authorization?.Parameter));

            if (req.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found")
                };
            }

            using var posted = JsonDocument.Parse(body!);
            if (posted.RootElement.TryGetProperty("max_completion_tokens", out _))
            {
                return new HttpResponseMessage(standardStatusCode)
                {
                    Content = new StringContent(standardErrorBody)
                };
            }

            return JsonResponse("""
            {
              "choices": [
                { "message": { "content": "{\"name\":\"generated\"}" } }
              ]
            }
            """);
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger, cache);
        var options = new ModelProviderOptions
        {
            Url = "https://proxy.example/providers/openai/deployments/gpt-5.5-2026-04-24",
            ApiVersion = "2025-01-01-preview",
            ApiKey = "secret",
            Type = "openai"
        };
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" }
          },
          "required": ["name"]
        }
        """);
        var request = new LLMClientRequest
        {
            Prompt = "Generate workflow",
            Reasoning = "medium",
            MaxOutputTokens = 128_000,
            StructuredOutputSchema = schema,
            StructuredOutputStrict = true,
            Tools =
            [
                new LLMToolDef
                {
                    Name = "lookup",
                    Description = "Look up a record.",
                    InputSchema = System.Text.Json.Nodes.JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "id": { "type": "string" }
                      },
                      "required": ["id"]
                    }
                    """)
                }
            ],
            UseBackgroundMode = true
        };

        var response = await provider.CallAsync("gpt-5.5-2026-04-24", options, request, CancellationToken.None);
        var cachedResponse = await provider.CallAsync("gpt-5.5-2026-04-24", options, request, CancellationToken.None);

        Assert.Equal("generated", response.Json!["name"]!.GetValue<string>());
        Assert.Equal("generated", cachedResponse.Json!["name"]!.GetValue<string>());
        Assert.Equal(
            [
                "/providers/openai/deployments/gpt-5.5-2026-04-24/responses?api-version=2025-01-01-preview",
                "/providers/openai/deployments/gpt-5.5-2026-04-24/chat/completions?api-version=2025-01-01-preview",
                "/providers/openai/deployments/gpt-5.5-2026-04-24/chat/completions?api-version=2025-01-01-preview",
                "/providers/openai/deployments/gpt-5.5-2026-04-24/chat/completions?api-version=2025-01-01-preview"
            ],
            requests.Select(entry => entry.Route));
        Assert.All(requests, entry => Assert.Equal("secret", entry.BearerToken));

        using var standard = JsonDocument.Parse(requests[1].Body!);
        using var legacy = JsonDocument.Parse(requests[2].Body!);
        using var cachedLegacy = JsonDocument.Parse(requests[3].Body!);
        Assert.Equal(128_000, standard.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(legacy.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(cachedLegacy.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal("gpt-5.5-2026-04-24", legacy.RootElement.GetProperty("model").GetString());
        Assert.Equal("medium", legacy.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("Generate workflow", legacy.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("lookup", legacy.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.True(legacy.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("retrying once without only that optional field", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("fallback succeeded after omitting max_completion_tokens", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("previously required legacy Chat Completions", StringComparison.Ordinal));
        Assert.Single(logger.Entries, entry =>
            entry.Message.Contains("Cached OpenAI Responses background unsupported result", StringComparison.Ordinal));
        Assert.Single(logger.Entries, entry =>
            entry.Message.Contains("Cached legacy-compatible OpenAI Chat Completions requirement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallAsync_LegacyChatCompatibilityCache_IsScopedByModel()
    {
        var requests = new List<(string Model, bool HasTokenLimit)>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            using var posted = JsonDocument.Parse(body);
            var model = posted.RootElement.GetProperty("model").GetString()!;
            var hasTokenLimit = posted.RootElement.TryGetProperty("max_completion_tokens", out _);
            requests.Add((model, hasTokenLimit));

            return hasTokenLimit
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("bad request")
                }
                : JsonResponse("""
                  {
                    "choices": [
                      { "message": { "content": "ok" } }
                    ]
                  }
                  """);
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenAiLLMProvider(http, backgroundModeCache: cache);
        var options = new ModelProviderOptions
        {
            Url = "https://proxy.example",
            ApiVersion = "2025-01-01-preview",
            ApiKey = "secret",
            Type = "openai"
        };
        var request = new LLMClientRequest
        {
            Prompt = "Hello",
            MaxOutputTokens = 1_234,
            UseBackgroundMode = false
        };

        await provider.CallAsync("model-a", options, request, CancellationToken.None);
        await provider.CallAsync("model-a", options, request, CancellationToken.None);
        await provider.CallAsync("model-b", options, request, CancellationToken.None);

        Assert.Equal(
            [
                ("model-a", true),
                ("model-a", false),
                ("model-a", false),
                ("model-b", true),
                ("model-b", false)
            ],
            requests);
    }

    [Fact]
    public async Task CallAsync_WithFailedLegacyChatFallback_CachesOnlyDeterministicResponsesRouteFailure()
    {
        const string prompt = "private planning prompt";
        const string apiKey = "secret-api-key";
        var requests = new List<(string Route, bool HasTokenLimit)>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            var body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
            var hasTokenLimit = false;
            if (body != null)
            {
                using var posted = JsonDocument.Parse(body);
                hasTokenLimit = posted.RootElement.TryGetProperty("max_completion_tokens", out _);
            }

            requests.Add((req.RequestUri!.AbsolutePath, hasTokenLimit));
            if (req.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found")
                };
            }

            var errorBody = hasTokenLimit
                ? $"standard request echoed {prompt} and {apiKey}"
                : $"legacy request echoed {prompt} and {apiKey}";
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(errorBody)
            };
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger, cache);
        var options = new ModelProviderOptions { Url = "https://proxy.example", ApiKey = apiKey, Type = "openai" };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
                "gpt-5.5-2026-04-24",
                options,
                new LLMClientRequest
                {
                    Prompt = prompt,
                    MaxOutputTokens = 128_000,
                    UseBackgroundMode = true
                },
                CancellationToken.None));
            Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
            Assert.DoesNotContain(prompt, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(
            [
                ("/v1/responses", false),
                ("/v1/chat/completions", true),
                ("/v1/chat/completions", false),
                ("/v1/chat/completions", true),
                ("/v1/chat/completions", false)
            ],
            requests);
        Assert.Single(logger.Entries, entry =>
            entry.Message.Contains("Cached OpenAI Responses background unsupported result", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("Cached legacy-compatible OpenAI Chat Completions requirement", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("compatibility result was not cached", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(prompt, StringComparison.Ordinal)
            || entry.Message.Contains(apiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallAsync_DeterministicResponses404IsCachedWhenChatFallbackIsRateLimited()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(req.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("route not found")
                }
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\"}}")
                });
        });

        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenAiLLMProvider(http, backgroundModeCache: cache);
        var options = new ModelProviderOptions
        {
            Url = "https://proxy.example",
            ApiKey = "secret",
            Type = "openai",
            RetryPolicy = new LLMProviderRetryPolicyOptions { MaxAttempts = 1 }
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CallAsync(
                "model",
                options,
                new LLMClientRequest { Prompt = "Hello", UseBackgroundMode = true },
                CancellationToken.None));
            Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        }

        Assert.Equal(
            ["/v1/responses", "/v1/chat/completions", "/v1/chat/completions"],
            requests);
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
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenAiLLMProvider(http, backgroundModeCache: cache);
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

    [Fact]
    public async Task CallAsync_ChatCompletionsBackgroundPolicyBypassesResponsesRoute()
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
            Url = "https://proxy.example",
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

        Assert.Equal(["/v1/chat/completions"], requests);
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
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<OpenAiLLMProvider>();
        var provider = new OpenAiLLMProvider(http, logger, cache);

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
