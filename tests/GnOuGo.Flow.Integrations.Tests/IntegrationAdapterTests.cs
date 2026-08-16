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
}
