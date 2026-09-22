using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class ReasoningSetupTests
{
    [Theory]
    [InlineData("openai", true, false, true)]
    [InlineData("copilot", true, false, true)]
    [InlineData("openai", false, false, false)]
    [InlineData("openai", true, true, false)]
    [InlineData("anthropic", true, false, false)]
    [InlineData("ollama", true, false, false)]
    public void SelectorRequiresDeclaredLevelsAndAdapterSupport(string type, bool supports, bool unsupported, bool advertised)
    {
        var options = ConfigurationAndTrafficTests.Options();
        var provider = options.Providers["test"];
        provider.Connection.Type = type;
        provider.Connection.RequestPolicy.UnspecifiedOutputTokens = LLMUnspecifiedOutputTokensMode.Configured;
        provider.Connection.RequestPolicy.DefaultMaxOutputTokens = 1000;
        var capabilities = provider.Models["model"].Metadata.Capabilities;
        capabilities.SupportsReasoningEffort = supports;
        capabilities.SupportedReasoningEfforts = ["low", "high", "", "high"];
        if (unsupported) capabilities.UnsupportedRequestParameters = ["reasoning_effort"];
        var model = ProxyApplication.Setup(new ModelRegistry(options), new("localhost:5087"))["configuration"]![0]!["models"]![0]!;
        if (advertised)
        {
            Assert.Equal(new[] { "low", "high" }, model["supportsReasoningEffort"]!.AsArray().Select(level => level!.GetValue<string>()));
            Assert.Equal("chat-completions", model["reasoningEffortFormat"]!.GetValue<string>());
        }
        else
        {
            Assert.Null(model["supportsReasoningEffort"]);
            Assert.Null(model["reasoningEffortFormat"]);
        }
        capabilities.SupportedReasoningEfforts = [];
        model = ProxyApplication.Setup(new ModelRegistry(options), new("localhost:5087"))["configuration"]![0]!["models"]![0]!;
        Assert.Null(model["supportsReasoningEffort"]);
    }

    [Theory]
    [InlineData("openai", false)] [InlineData("openai", true)]
    [InlineData("copilot", false)] [InlineData("copilot", true)]
    public async Task AdvertisedEffortReachesUpstreamWithToolsAndHistory(string type, bool streaming)
    {
        JsonObject? received = null;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            received = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, streaming, false),
                streaming ? "text/event-stream" : "application/json");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, type, new()
        {
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportsReasoningEffort"] = "true",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportedReasoningEfforts:0"] = "low",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportedReasoningEfforts:1"] = "high"
        });
        using var setupResponse = await proxy.Client.GetAsync("/api/setup", TestContext.Current.CancellationToken);
        var setup = await TestHost.Read(setupResponse);
        var selected = setup["configuration"]![0]!["models"]![0]!["supportsReasoningEffort"]![1]!.GetValue<string>();
        var request = ProtocolRoundTripTests.Request(streaming);
        request["reasoning_effort"] = selected;
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Bonjour", answer);
        if (streaming) Assert.Contains("[DONE]", answer);
        Assert.Equal("high", received!["reasoning_effort"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(request["tools"], received["tools"]));
        Assert.True(JsonNode.DeepEquals(request["messages"], received["messages"]));
    }
}
