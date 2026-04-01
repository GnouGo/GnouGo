using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

public sealed class ModelCatalogProviderTests
{
    [Fact]
    public async Task OpenAiProvider_ListModelsAsync_ParsesOpenAiResponse_AndUsesBearerAuth()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "data": [
                    { "id": "gpt-4o", "owned_by": "openai" },
                    { "id": "gpt-4o-mini", "owned_by": "openai" }
                  ]
                }
                """)
            };
        });

        var provider = new OpenAiLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://api.openai.com", ApiKey = "openai-secret", Type = "openai" },
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.openai.com/v1/models", capturedRequest!.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "openai-secret"), capturedRequest.Headers.Authorization);
        Assert.Collection(models,
            first => Assert.Equal("gpt-4o", first.Id),
            second => Assert.Equal("gpt-4o-mini", second.Id));
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
    public async Task CopilotProvider_ListModelsAsync_FallsBackAcrossCandidateEndpoints()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req.RequestUri!.ToString());
            if (req.RequestUri!.ToString().EndsWith("/catalog/models", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = JsonContent("{ \"error\": \"not found\" }")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "data": [
                    { "id": "openai/gpt-4.1", "owned_by": "github" },
                    { "id": "anthropic/claude-sonnet-4", "owned_by": "github" }
                  ]
                }
                """)
            };
        });

        var provider = new CopilotLLMProvider(new HttpClient(handler));
        var models = await provider.ListModelsAsync(
            new ModelProviderOptions { Url = "https://models.github.ai/inference", ApiKey = "gh-token", Type = "copilot" },
            CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.Equal("https://models.github.ai/catalog/models", requests[0]);
        Assert.Equal("https://models.github.ai/inference/models", requests[1]);
        Assert.Collection(models,
            first => Assert.Equal("openai/gpt-4.1", first.Id),
            second => Assert.Equal("anthropic/claude-sonnet-4", second.Id));
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

