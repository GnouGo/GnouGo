using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmTransportConfigurationTests
{
    [Theory]
    [InlineData("{\"streamingMode\":null}")]
    [InlineData("{\"streamingMode\":1}")]
    [InlineData("{\"streamingMode\":\"Streaming\",\"StreamingMode\":\"Auto\"}")]
    [InlineData("{\"streamingSupport\":{\"ChatCompletions\":\"true\"}}")]
    [InlineData("{\"streamingSupport\":{\"unknownProtocol\":true}}")]
    [InlineData("{\"modelTransportOverrides\":{\"exact\":{\"streamingMode\":\"bogus\"}}}")]
    public void InvalidPresentConfigurationDoesNotBecomeAuto(string json)
        => Assert.Throws<InvalidOperationException>(() => LlmTransportConfiguration.Apply(JsonNode.Parse(json)!.AsObject(), new()));

    [Fact]
    public void RuntimeReplacementClonesExactOverridesAndRouteDeclarations()
    {
        var provider = new ModelProviderOptions
        {
            Type = "openai", RequestPolicy = new() { StreamingMode = LLMStreamingMode.Streaming, StreamingSupport = new() { [LLMTransportResolver.ChatCompletions] = true } },
            ModelTransportOverrides = new() { ["ExactDeployment"] = new() { StreamingMode = LLMStreamingMode.NonStreaming } }
        };
        var options = new LLMOptions { Models = new() { ["configured"] = provider } };
        var store = new LLMRuntimeOptionsStore(Options.Create(options), NullLogger<LLMRuntimeOptionsStore>.Instance);
        provider.RequestPolicy.StreamingSupport[LLMTransportResolver.ChatCompletions] = false;
        provider.ModelTransportOverrides["ExactDeployment"].StreamingMode = LLMStreamingMode.Auto;
        var saved = store.Current.Models["configured"];
        Assert.True(saved.RequestPolicy.StreamingSupport![LLMTransportResolver.ChatCompletions]);
        Assert.Equal(LLMStreamingMode.NonStreaming, saved.ModelTransportOverrides!["ExactDeployment"].StreamingMode);
        Assert.False(saved.ModelTransportOverrides.ContainsKey("exactdeployment"));
        var json = JsonNode.Parse("{\"streamingMode\":\"Auto\",\"streamingSupport\":{\"ChatCompletions\":true},\"modelTransportOverrides\":{\"ExactDeployment\":{\"streamingMode\":\"NonStreaming\"}}}")!.AsObject();
        var reloaded = new ModelProviderOptions();
        LlmTransportConfiguration.Apply(json, reloaded);
        Assert.Equal(LLMStreamingMode.Auto, reloaded.RequestPolicy.StreamingMode);
        Assert.Equal(LLMStreamingMode.NonStreaming, reloaded.ModelTransportOverrides!["ExactDeployment"].StreamingMode);
    }
}
