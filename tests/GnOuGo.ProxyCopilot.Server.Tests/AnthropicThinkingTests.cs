using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class AnthropicThinkingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ThinkingByDefaultUpstreamCompletesToolLoopInTextMode(bool streaming)
    {
        var requests = new List<JsonObject>();
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            requests.Add(body);
            var type = streaming ? "text/event-stream" : "application/json";
            if (body["thinking"]?["type"]?.GetValue<string>() != "disabled")
            {
                // Reproduce a native model enabling signed thinking unless explicitly
                // disabled. The v1 Chat Completions bridge cannot replay these blocks.
                var thinking = streaming
                    ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-thinking\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n"
                    : "{\"id\":\"msg-thinking\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"signed-state\"},{\"type\":\"text\",\"text\":\"Answer\"}],\"stop_reason\":\"end_turn\"}";
                await ProtocolRoundTripTests.Write(context, thinking, type);
                return;
            }
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("anthropic", streaming, requests.Count == 1), type);
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, "anthropic");
        var request = ProtocolRoundTripTests.Request(streaming);
        using var first = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var calls = ProtocolRoundTripTests.ExtractCalls(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), streaming);
        Assert.Equal(2, calls.Count);
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = (string?)null, ["tool_calls"] = calls.DeepClone() });
        foreach (var call in calls)
            request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call!["id"]!.DeepClone(), ["content"] = "Observed result" });
        using var second = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var answer = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Bonjour", answer);
        if (streaming) Assert.Contains("[DONE]", answer);
        Assert.Equal(2, requests.Count);
        var results = requests[1]["messages"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("tool_result", result!["type"]!.GetValue<string>()));
    }
}
