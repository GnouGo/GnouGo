using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Mcp.Core;
using Xunit;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class IntegrationAdapterTests
{
    [Fact]
    public async Task RoutingAdapter_MapsFlowRequestsAndResponses()
    {
        var provider = new RecordingProvider();
        var routingClient = new RoutingLLMClient(
            new LLMOptions
            {
                DefaultProvider = "fake",
                Models =
                {
                    ["fake"] = new ModelProviderOptions { Type = "fake" }
                }
            },
            [provider]);
        var adapter = new RoutingLLMClientAdapter(routingClient);

        var response = await adapter.CallAsync(new LLMRequest
        {
            Provider = "fake",
            Model = "vendor/test-model",
            Prompt = "Use a tool.",
            MaxTokens = 123,
            Tools =
            [
                new LLMTool
                {
                    Name = "lookup",
                    Description = "Lookup data",
                    InputSchema = JsonNode.Parse("{\"type\":\"object\"}")
                }
            ]
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(provider.Request);
        Assert.Equal("Use a tool.", provider.Request.Prompt);
        Assert.Equal(123, provider.Request.MaxOutputTokens);
        Assert.Equal("lookup", Assert.Single(provider.Request.Tools!).Name);
        Assert.Equal("done", response.Text);
        Assert.Equal("lookup", Assert.Single(response.ToolCalls!).Name);
    }

    [Fact]
    public void CostEstimator_DelegatesToModelMetadataCatalog()
    {
        var estimator = new ModelMetadataUsageCostEstimator();

        var expected = ModelMetadataCatalog.EstimateCost(
            "gpt-5.4",
            1_000,
            250,
            providerType: "openai");

        Assert.Equal(expected, estimator.EstimateCost("gpt-5.4", 1_000, 250, "openai"));
    }

    [Fact]
    public void CostEstimator_UsesConfiguredProviderNeutralModelOverride()
    {
        var options = new LLMOptions();
        options.ModelOverrides["neutral/custom-model"] = new LLMModelMetadata
        {
            Pricing = new ModelPricingMetadata
            {
                InputPer1MTokens = 2m,
                OutputPer1MTokens = 4m
            }
        };
        var estimator = new ModelMetadataUsageCostEstimator(options);

        var cost = estimator.EstimateCost("custom-model", 1_000_000, 500_000, "neutral");

        Assert.Equal(4m, cost);
    }

    [Fact]
    public void CostEstimator_ReturnsNullWhenConfiguredPricingIsIncomplete()
    {
        var options = new LLMOptions();
        options.ModelOverrides["neutral/partial-price-model"] = new LLMModelMetadata
        {
            Pricing = new ModelPricingMetadata
            {
                InputPer1MTokens = 2m
            }
        };
        var estimator = new ModelMetadataUsageCostEstimator(options);

        var cost = estimator.EstimateCost("partial-price-model", 1_000, 500, "neutral");

        Assert.Null(cost);
    }

    [Fact]
    public void ConfiguredFactory_MapsDeclaredArtifactMetadataToFlowContract()
    {
        var tool = new McpToolInfo
        {
            Name = "create_workspace",
            Meta = new JsonObject
            {
                [McpArtifactContractMetadata.MetaPropertyName] = JsonNode.Parse(
                    McpArtifactContractMetadata.WorkspaceDirectoryProducerProjectRootRelativeJson)
            },
            OutputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "projectRootRelative": { "type": "string" } },
                  "required": ["projectRootRelative"]
                }
                """)
        };
        var adapterType = typeof(ConfiguredMcpClientFactory).Assembly.GetType(
            "GnOuGo.Flow.Integrations.McpSessionAdapter");
        var method = adapterType!.GetMethod(
            "ResolveArtifactContract",
            BindingFlags.Static | BindingFlags.NonPublic);

        var resolution = Assert.IsType<McpArtifactContractResolution>(method!.Invoke(null, [tool]));

        Assert.Empty(resolution.Errors);
        var produced = Assert.Single(resolution.Contract!.Produces);
        Assert.Equal(McpArtifactContractConventions.WorkspaceDirectoryKind, produced.Kind);
        Assert.Equal("/projectRootRelative", produced.Pointer);
    }

    [Fact]
    public async Task RoutingAdapter_MapsTypedProviderFailureWithoutRawBody()
    {
        var routingClient = new RoutingLLMClient(
            new LLMOptions
            {
                DefaultProvider = "failing",
                DefaultModel = "model",
                Models = { ["failing"] = new ModelProviderOptions { Type = "failing" } }
            },
            [new FailingProvider()]);
        var adapter = new RoutingLLMClientAdapter(routingClient);

        var failure = await Assert.ThrowsAsync<LLMClientException>(() => adapter.CallAsync(
            new LLMRequest { Provider = "failing", Model = "model", Prompt = "secret prompt" },
            TestContext.Current.CancellationToken));

        Assert.Equal(LLMClientFailureKind.QuotaOrBilling, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("credit_balance_exhausted", failure.SafeProviderCode);
        Assert.DoesNotContain("raw-provider-body", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LLMProviderFailureKind.Transport, LLMClientFailureKind.Transport, true)]
    [InlineData(LLMProviderFailureKind.Timeout, LLMClientFailureKind.Timeout, true)]
    [InlineData(LLMProviderFailureKind.RateLimited, LLMClientFailureKind.RateLimited, true)]
    [InlineData(LLMProviderFailureKind.ServiceUnavailable, LLMClientFailureKind.ServiceUnavailable, true)]
    [InlineData(LLMProviderFailureKind.Authentication, LLMClientFailureKind.Authentication, false)]
    [InlineData(LLMProviderFailureKind.Authorization, LLMClientFailureKind.Authorization, false)]
    [InlineData(LLMProviderFailureKind.QuotaOrBilling, LLMClientFailureKind.QuotaOrBilling, false)]
    [InlineData(LLMProviderFailureKind.InvalidRequest, LLMClientFailureKind.InvalidRequest, false)]
    [InlineData(LLMProviderFailureKind.ModelUnavailable, LLMClientFailureKind.ModelUnavailable, false)]
    [InlineData(LLMProviderFailureKind.Unknown, LLMClientFailureKind.Unknown, false)]
    public async Task RoutingAdapter_MapsEveryTypedFailureKind(
        LLMProviderFailureKind providerKind,
        LLMClientFailureKind expectedKind,
        bool retryable)
    {
        var routingClient = new RoutingLLMClient(
            new LLMOptions
            {
                DefaultProvider = "typed-failing",
                DefaultModel = "model",
                Models = { ["typed-failing"] = new ModelProviderOptions { Type = "typed-failing" } }
            },
            [new TypedFailingProvider(providerKind, retryable)]);
        var adapter = new RoutingLLMClientAdapter(routingClient);

        var failure = await Assert.ThrowsAsync<LLMClientException>(() => adapter.CallAsync(
            new LLMRequest { Provider = "typed-failing", Model = "model", Prompt = "secret prompt" },
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(retryable, failure.Retryable);
        Assert.Equal(418, failure.StatusCode);
        Assert.Equal("safe_code", failure.SafeProviderCode);
    }

    [Fact]
    public void ProviderFailureMapper_PreservesOnlyTheRedactedFailureContract()
    {
        var providerFailure = new LLMProviderException(
            LLMProviderFailureKind.InvalidRequest,
            "The LLM provider rejected the request as invalid.",
            retryable: false,
            statusCode: 400,
            safeProviderCode: "invalid_request_error");

        var failure = LLMProviderFailureMapper.Map(providerFailure);

        Assert.Equal(LLMClientFailureKind.InvalidRequest, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("invalid_request_error", failure.SafeProviderCode);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void ConfiguredFactory_MapsDeclaredCompositionMetadataToFlowContract()
    {
        var tool = new McpToolInfo
        {
            Name = "review_complete",
            Meta = JsonNode.Parse("""
                {
                  "gnougo": {
                    "composition": {
                      "version": 1,
                      "kind": "complete_operation",
                      "encapsulates": [
                        { "kind": "tool", "method": "review_start" },
                        { "kind": "tool", "method": "review_finish" }
                      ]
                    }
                  }
                }
                """)
        };
        var adapterType = typeof(ConfiguredMcpClientFactory).Assembly.GetType(
            "GnOuGo.Flow.Integrations.McpSessionAdapter");
        var method = adapterType!.GetMethod(
            "ResolveCompositionContract",
            BindingFlags.Static | BindingFlags.NonPublic);

        var resolution = Assert.IsType<McpCapabilityCompositionResolution>(method!.Invoke(null, [tool]));

        Assert.Empty(resolution.Errors);
        Assert.Equal(McpCapabilityCompositionConventions.CompleteOperationKind, resolution.Contract!.Kind);
        Assert.Equal(2, resolution.Contract.Encapsulates.Count);
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public string ProviderType => "fake";
        public LLMClientRequest? Request { get; private set; }

        public Task<LLMClientResponse> CallAsync(
            string model,
            ModelProviderOptions provider,
            LLMClientRequest request,
            CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new LLMClientResponse
            {
                Text = "done",
                ToolCalls =
                [
                    new ToolCallResult
                    {
                        Id = "call-1",
                        Name = "lookup",
                        Arguments = new JsonObject { ["id"] = 42 }
                    }
                ]
            });
        }
    }

    private sealed class FailingProvider : ILLMProvider
    {
        public string ProviderType => "failing";

        public Task<LLMClientResponse> CallAsync(
            string model,
            ModelProviderOptions provider,
            LLMClientRequest request,
            CancellationToken ct)
            => Task.FromException<LLMClientResponse>(new HttpRequestException(
                "raw-provider-body credit_balance_exhausted",
                inner: null,
                statusCode: System.Net.HttpStatusCode.BadRequest));
    }

    private sealed class TypedFailingProvider(
        LLMProviderFailureKind kind,
        bool retryable) : ILLMProvider
    {
        public string ProviderType => "typed-failing";

        public Task<LLMClientResponse> CallAsync(
            string model,
            ModelProviderOptions provider,
            LLMClientRequest request,
            CancellationToken ct)
            => Task.FromException<LLMClientResponse>(new LLMProviderException(
                kind,
                "redacted typed failure",
                retryable,
                statusCode: 418,
                safeProviderCode: "safe_code"));
    }
}
