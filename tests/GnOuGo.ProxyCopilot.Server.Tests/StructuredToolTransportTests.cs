using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class StructuredToolTransportTests
{
    private const string Model = "ProxyCopilot:Providers:test:Models:model:";
    private static Dictionary<string, string?> Settings() => new()
    {
        [Model + "ToolTransport"] = StructuredToolSelection.TransportName,
        [Model + "Metadata:Capabilities:SupportsStructuredOutput"] = "true",
        [Model + "Metadata:Capabilities:SupportsTools"] = "false",
        [Model + "Metadata:Capabilities:SupportedReasoningEfforts:0"] = "xhigh"
    };

    private static JsonObject Completion(string action)
    {
        var response = JsonNode.Parse(ProtocolRoundTripTests.Fixture("openai", false, false))!.AsObject();
        response["choices"]![0]!["message"]!["content"] = action;
        return response;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditorCallsDeclaredToolAndContinuesWithItsResult(bool streaming)
    {
        var received = new List<JsonObject>();
        await using var upstream = await TestHost.Upstream(async context =>
        {
            Assert.Equal("application/json", context.Request.Headers.Accept.ToString());
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            received.Add(body);
            var action = received.Count == 1
                ? """{"action":{"kind":"tools","calls":[{"name":"lookup","arguments":"{\"city\":\"Paris\"}"}]}}"""
                : """{"action":{"kind":"final","content":"Observed result 371."}}""";
            await ProtocolRoundTripTests.Write(context, Completion(action).ToJsonString(), "application/json");
        });
        var settings = Settings();
        settings[Model + "Metadata:Capabilities:UnsupportedRequestParameters:0"] = "tools";
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: settings);
        var ct = TestContext.Current.CancellationToken;
        using var setup = await proxy.Client.GetAsync("/api/setup", ct);
        Assert.True((await TestHost.Read(setup))["configuration"]![0]!["models"]![0]!["toolCalling"]!.GetValue<bool>());

        var request = ProtocolRoundTripTests.Request(streaming);
        request["reasoning_effort"] = "xhigh";
        request["max_completion_tokens"] = 675;
        request["parallel_tool_calls"] = false;
        Task<HttpResponseMessage> Send() => proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), ct);
        using var first = await Send();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var calls = ProtocolRoundTripTests.ExtractCalls(await first.Content.ReadAsStringAsync(ct), streaming);
        var call = Assert.Single(calls)!;
        Assert.Equal("lookup", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Paris", JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!["city"]!.GetValue<string>());
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls.DeepClone() });
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call["id"]!.DeepClone(), ["content"] = "371" });
        using var second = await Send();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var answer = await second.Content.ReadAsStringAsync(ct);
        Assert.Contains("Observed result 371.", answer);
        Assert.Contains("\"total_tokens\":12", answer);
        if (streaming) Assert.Contains("data: [DONE]", answer);

        Assert.Equal(2, received.Count);
        Assert.All(received, body =>
        {
            Assert.Equal("vendor/upstream-model", body["model"]!.GetValue<string>());
            Assert.Equal("xhigh", body["reasoning_effort"]!.GetValue<string>());
            Assert.Equal(675, body["max_completion_tokens"]!.GetValue<int>());
            Assert.False(body["stream"]!.GetValue<bool>());
            Assert.False(body.ContainsKey("tools"));
            Assert.False(body.ContainsKey("stream_options"));
            Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
            var contracts = JsonNode.Parse(body["messages"]![2]!["content"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(request["tools"], contracts!["tools"]));
        });
        Assert.True(JsonNode.DeepEquals(request["messages"]!.AsArray()[^1], received[1]["messages"]!.AsArray()[^1]));
    }

    [Theory]
    [InlineData("unlisted")]
    [InlineData("truncated")]
    [InlineData("refused")]
    public async Task InvalidDecisionFailsBeforeAnyToolIsEmitted(string failure)
    {
        var body = Completion("""{"action":{"kind":"tools","calls":[{"name":"lookup","arguments":"{}"}]}}""");
        var choice = body["choices"]![0]!;
        if (failure == "unlisted") choice["message"]!["content"] = """{"action":{"kind":"tools","calls":[{"name":"other","arguments":"{}"}]}}""";
        if (failure == "truncated") choice["finish_reason"] = "length";
        if (failure == "refused") choice["message"]!["refusal"] = "declined";
        await using var upstream = await TestHost.Upstream(context => ProtocolRoundTripTests.Write(context, body.ToJsonString(), "application/json"));
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Settings());
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(ProtocolRoundTripTests.Request(true).ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("tool_calls", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TextOnlyRequestKeepsNativeStreamingAndItsOwnResponseFormat()
    {
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            Assert.Equal("text/event-stream", context.Request.Headers.Accept.ToString());
            Assert.True(body["stream"]!.GetValue<bool>());
            Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", true, false), "text/event-stream");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Settings());
        var request = ProtocolRoundTripTests.Request(true);
        request["response_format"] = new JsonObject { ["type"] = "json_object" };
        var ct = TestContext.Current.CancellationToken;
        using var conflict = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), ct);
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        request["tools"] = new JsonArray();
        using var success = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), ct);
        Assert.Contains("data: [DONE]", await success.Content.ReadAsStringAsync(ct));
    }

    [Theory]
    [InlineData("openai", false, StructuredToolSelection.TransportName)]
    [InlineData("openai", true, "unknown")]
    [InlineData("ollama", true, StructuredToolSelection.TransportName)]
    public void IncompatibleTransportConfigurationFailsAtStartup(string type, bool structured, string transport)
    {
        var options = ConfigurationAndTrafficTests.Options();
        var provider = options.Providers["test"];
        provider.Connection.Type = type;
        provider.Models["model"].Metadata.Capabilities.SupportsStructuredOutput = structured;
        provider.Models["model"].ToolTransport = transport;
        Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
    }
}
