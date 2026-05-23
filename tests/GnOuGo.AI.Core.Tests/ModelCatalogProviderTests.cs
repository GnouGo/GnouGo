using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace GnOuGo.AI.Core.Tests;

public sealed class ModelCatalogProviderTests
{
    [Fact]
    public async Task OpenAiProvider_ListModelsAsync_ReturnsDiscoveredModels_AndUsesBearerAuth()
    {
        var requests = new List<(HttpMethod Method, string Url, AuthenticationHeaderValue? Authorization)>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add((req.Method, req.RequestUri!.ToString(), req.Headers.Authorization));

            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""
                    {
                      "data": [
                        { "id": "gpt-4o", "owned_by": "openai" },
                        { "id": "text-embedding-3-large", "owned_by": "openai" },
                        { "id": "gpt-4o-mini", "owned_by": "openai" }
                      ]
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{ \"choices\": [{ \"message\": { \"content\": \"OK\" } }] }")
            };
        });

        var provider = new OpenAiLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://api.openai.com", ApiKey = "openai-secret", Type = "openai" },
            CancellationToken.None);

        Assert.Single(requests);
        Assert.Equal((HttpMethod.Get, "https://api.openai.com/v1/models", new AuthenticationHeaderValue("Bearer", "openai-secret")), requests[0]);
        Assert.Collection(models,
            first => Assert.Equal("gpt-4o", first.Id),
            second => Assert.Equal("text-embedding-3-large", second.Id),
            third => Assert.Equal("gpt-4o-mini", third.Id));
    }

    [Fact]
    public async Task OpenAiProvider_ListModelsAsync_UsesOidcAccessToken_ForDiscoveryOnly()
    {
        var authorizedRequests = new List<(HttpMethod Method, string Url, AuthenticationHeaderValue? Authorization)>();
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();

            if (url == "https://issuer.example/.well-known/openid-configuration")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("{ \"token_endpoint\": \"https://issuer.example/oauth/token\" }")
                };
            }

            if (url == "https://issuer.example/oauth/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("{ \"access_token\": \"oidc-token\", \"expires_in\": 3600 }")
                };
            }

            authorizedRequests.Add((req.Method, url, req.Headers.Authorization));

            if (req.Method == HttpMethod.Get && url == "https://api.openai.com/v1/models")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""
                    {
                      "data": [
                        { "id": "gpt-4.1", "owned_by": "openai" }
                      ]
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{ \"choices\": [{ \"message\": { \"content\": \"OK\" } }] }")
            };
        });

        var provider = new OpenAiLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions
            {
                Url = "https://api.openai.com/v1",
                Type = "openai",
                Issuer = "https://issuer.example",
                ClientId = "client-id",
                ClientSecret = "client-secret",
                Scopes = "models.read"
            },
            CancellationToken.None);

        Assert.Single(models);
        Assert.All(authorizedRequests, request =>
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "oidc-token"), request.Authorization));
    }

    [Fact]
    public async Task OllamaProvider_ListModelsAsync_ParsesTagsResponse()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
            {
              "models": [
                { "name": "llama3.2:latest", "model": "llama3.2:latest" },
                { "name": "qwen2.5:14b", "model": "qwen2.5:14b" }
              ]
            }
            """)
        });

        var provider = new OllamaLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "http://localhost:11434", Type = "ollama" },
            CancellationToken.None);

        Assert.Collection(models,
            first => Assert.Equal("llama3.2:latest", first.Id),
            second => Assert.Equal("qwen2.5:14b", second.Id));
    }

    [Fact]
    public async Task CopilotProvider_ListModelsAsync_FallsBackAcrossCandidateEndpoints_AndReturnsCatalogAsIs()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.ToString());
            if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().EndsWith("/catalog/models", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = JsonContent("{ \"error\": \"not found\" }")
                };
            }

            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""
                    {
                      "data": [
                        { "id": "openai/gpt-4.1", "owned_by": "github" },
                        { "id": "anthropic/claude-sonnet-4", "owned_by": "github" },
                        { "id": "openai/text-embedding-3-small", "owned_by": "github" }
                      ]
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            {
                Content = JsonContent("{ \"error\": \"unexpected non-GET request\" }")
            };
        });

        var provider = new CopilotLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://models.github.ai/inference", ApiKey = "gh-token", Type = "copilot" },
            CancellationToken.None);

        Assert.Equal("https://models.github.ai/catalog/models", requests[0]);
        Assert.Equal("https://models.github.ai/inference/models", requests[1]);
        Assert.Collection(models,
            first => Assert.Equal("openai/gpt-4.1", first.Id),
            second => Assert.Equal("anthropic/claude-sonnet-4", second.Id),
            third => Assert.Equal("openai/text-embedding-3-small", third.Id));
    }

    [Fact]
    public async Task RoutingLlmModelCatalog_RoutesToMatchingCatalogProvider()
    {
        var options = new LLMOptions
        {
            DefaultProvider = "OpenAi",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["OpenAi"] = new() { Url = "https://api.openai.com/v1", Type = "openai" }
            }
        };

        var fakeProvider = new FakeCatalogProvider("openai", new[]
        {
            new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai")
        });

        var catalog = new RoutingLLMModelCatalog(options, new[] { fakeProvider });
        var models = await catalog.ListModelsAsync("OpenAi", CancellationToken.None);

        Assert.Single(models);
        Assert.Equal("gpt-4o", models[0].Id);
        Assert.Equal(1, fakeProvider.CallCount);
    }

    [Fact]
    public async Task RoutingLlmModelCatalog_EnrichesDiscoveredModelsAndAddsOverrides()
    {
        var options = new LLMOptions
        {
            DefaultProvider = "OpenAi",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["OpenAi"] = new() { Url = "https://api.openai.com/v1", Type = "openai" }
            },
            ModelOverrides = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["local-custom"] = new()
                {
                    Id = "local-custom",
                    ProviderType = "openai",
                    ContextWindowTokens = 32000,
                    MaxOutputTokens = 4096,
                    Pricing = new ModelPricingMetadata { InputPer1MTokens = 0.01m, OutputPer1MTokens = 0.02m },
                    Capabilities = new ModelCapabilityMetadata { SupportsTemperature = false }
                }
            }
        };

        var fakeProvider = new FakeCatalogProvider("openai", new[]
        {
            new LLMModelDescriptor("o4-mini", "o4-mini", "openai", "openai")
        });

        var catalog = new RoutingLLMModelCatalog(options, new[] { fakeProvider });
        var models = await catalog.ListModelsAsync("OpenAi", CancellationToken.None);

        var o4 = Assert.Single(models, m => m.Id == "o4-mini");
        Assert.False(o4.Capabilities!.SupportsTemperature);
        Assert.True(o4.Capabilities.SupportsReasoningEffort);
        Assert.Equal(200000, o4.ContextWindowTokens);
        Assert.Equal(1.10m, o4.Pricing!.InputPer1MTokens);

        var custom = Assert.Single(models, m => m.Id == "local-custom");
        Assert.Equal(32000, custom.ContextWindowTokens);
        Assert.Equal(0.02m, custom.Pricing!.OutputPer1MTokens);
        Assert.False(custom.Capabilities!.SupportsTemperature);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    [Fact]
    public async Task CopilotProvider_ListModelsAsync_DoesNotCallChatCompletionsDuringDiscovery()
    {
        var requests = new List<(HttpMethod Method, string Url)>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add((req.Method, req.RequestUri!.ToString()));

            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""
                    {
                      "data": [
                        { "id": "openai/gpt-4.1", "owned_by": "github" },
                        { "id": "meta/llama-3.3-70b-instruct", "owned_by": "github" }
                      ]
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            {
                Content = JsonContent("{ \"error\": \"unexpected non-GET request\" }")
            };
        });

        var provider = new CopilotLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://models.github.ai/inference", ApiKey = "gh-token", Type = "copilot" },
            CancellationToken.None);

        Assert.All(requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Collection(models,
            first => Assert.Equal("openai/gpt-4.1", first.Id),
            second => Assert.Equal("meta/llama-3.3-70b-instruct", second.Id));
    }

    [Fact]
    public async Task CopilotProvider_ListModelsAsync_ThrowsOnUnauthorizedDiscovery()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = JsonContent("Unauthorized")
            };
        });

        var provider = new CopilotLLMProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://models.github.ai/inference", ApiKey = "gh-token", Type = "copilot" },
            CancellationToken.None));

        Assert.Contains("401 Unauthorized", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeCatalogProvider : ILLMModelCatalogProvider
    {
        private readonly IReadOnlyList<LLMModelDescriptor> _models;

        public FakeCatalogProvider(string providerType, IReadOnlyList<LLMModelDescriptor> models)
        {
            ProviderType = providerType;
            _models = models;
        }

        public string ProviderType { get; }
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_models);
        }
    }
}

