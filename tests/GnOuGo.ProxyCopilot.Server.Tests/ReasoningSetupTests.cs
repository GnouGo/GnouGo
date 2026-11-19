using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class ReasoningSetupTests
{
    [Fact]
    public void ConfiguredDefaultIsExportedAndMustBeAnAdvertisedLevel()
    {
        var options = ConfigurationAndTrafficTests.Options();
        var model = options.Providers["test"].Models["model"];
        model.Metadata.Capabilities.SupportsReasoningEffort = true;
        model.Metadata.Capabilities.SupportedReasoningEfforts = ["none", "low", "high"];
        model.DefaultReasoningEffort = "none";
        var setup = ProxyApplication.Setup(new ModelRegistry(options), new("localhost:5087"));
        Assert.Equal("none", setup["configuration"]![0]!["models"]![0]!["defaultReasoningEffort"]!.GetValue<string>());
        model.DefaultReasoningEffort = "max";
        Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
        model.DefaultReasoningEffort = "none";
        model.Metadata.Capabilities.SupportsReasoningEffort = false;
        Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
    }

    [Theory]
    [InlineData("openai", "\"high\"")]
    [InlineData("copilot", "\"max\"")]
    [InlineData("openai", "\"NONE\"")]
    [InlineData("openai", "\"\"")]
    [InlineData("openai", "42")]
    [InlineData("openai", "{}")]
    public async Task InvalidOrStaleReasoningSelectionFailsBeforeDispatch(string type, string selected)
    {
        var calls = 0;
        await using var upstream = await TestHost.Upstream(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, type, new()
        {
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportsReasoningEffort"] = "true",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportedReasoningEfforts:0"] = "none"
        });
        var request = ProtocolRoundTripTests.Request(true);
        request["reasoning_effort"] = JsonNode.Parse(selected);
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await TestHost.Read(response))["error"]!;
        Assert.Equal("unsupported_parameter", error["code"]!.GetValue<string>());
        Assert.Contains("reasoning_effort", error["message"]!.GetValue<string>());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ExplicitlyDisabledReasoningIsRejectedButUnknownCapabilitiesRemainTransparent()
    {
        var registry = new ModelRegistry(ConfigurationAndTrafficTests.Options());
        var route = registry.Models[0];
        var request = ProtocolRoundTripTests.Request(false);
        request["reasoning_effort"] = "custom-depth";
        var capabilities = route.Model.Metadata.Capabilities;
        capabilities.SupportsReasoningEffort = null;
        capabilities.SupportedReasoningEfforts = null;
        Assert.Same(request, Protocols.ChatContract.PrepareRequest(request, route));
        capabilities.SupportsReasoningEffort = true;
        capabilities.SupportedReasoningEfforts = ["custom-depth"];
        Assert.Same(request, Protocols.ChatContract.PrepareRequest(request, route));
        capabilities.SupportsReasoningEffort = false;
        Assert.Throws<ProxyException>(() => Protocols.ChatContract.PrepareRequest(request, route));
    }

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
    [InlineData("openai")]
    [InlineData("copilot")]
    public async Task TextOnlyTurnCanUseCompletedToolHistoryAtExtraHigh(string type)
    {
        var received = new List<JsonObject>();
        await using var upstream = await TestHost.Upstream(async context =>
        {
            received.Add((await JsonNode.ParseAsync(context.Request.Body))!.AsObject());
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, true, false), "text/event-stream");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, type, new()
        {
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportsTools"] = "false",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportedReasoningEfforts:0"] = "xhigh"
        });
        var request = ProtocolRoundTripTests.Request(true);
        var toolDefinitions = request["tools"]!;
        request.Remove("tools");
        request["reasoning_effort"] = "xhigh";
        var messages = request["messages"]!.AsArray();
        messages.Add(JsonNode.Parse("""
            {"role":"assistant","content":null,"tool_calls":[{"id":"past","type":"function","function":{"name":"lookup","arguments":"{}"}}]}
            """));
        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "past", ["content"] = "Paris" });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Which city did the tool return?" });

        Task<HttpResponseMessage> Send() => proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        using var success = await Send();
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Contains("[DONE]", await success.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var forwarded = Assert.Single(received);
        Assert.True(JsonNode.DeepEquals(messages, forwarded["messages"]));
        Assert.Equal("xhigh", forwarded["reasoning_effort"]!.GetValue<string>());
        Assert.False(forwarded.ContainsKey("tools"));

        request["tools"] = toolDefinitions;
        using var activeTools = await Send();
        Assert.Equal(HttpStatusCode.BadRequest, activeTools.StatusCode);
        request.Remove("tools");
        request["tool_choice"] = "required";
        using var forcedCall = await Send();
        Assert.Equal(HttpStatusCode.BadRequest, forcedCall.StatusCode);
        request.Remove("tool_choice");
        messages[^2]!["tool_call_id"] = "unmatched";
        using var invalidHistory = await Send();
        Assert.Equal(HttpStatusCode.BadRequest, invalidHistory.StatusCode);
        Assert.Single(received);
    }

    [Theory]
    [InlineData("openai", false, "high")] [InlineData("openai", true, "high")]
    [InlineData("copilot", false, "high")] [InlineData("copilot", true, "high")]
    [InlineData("openai", false, "none")] [InlineData("openai", true, "none")]
    public async Task AdvertisedEffortReachesUpstreamWithToolsAndHistory(string type, bool streaming, string effort)
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
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportedReasoningEfforts:1"] = effort
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
        Assert.Equal(effort, received!["reasoning_effort"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(request["tools"], received["tools"]));
        Assert.True(JsonNode.DeepEquals(request["messages"], received["messages"]));
    }
}
