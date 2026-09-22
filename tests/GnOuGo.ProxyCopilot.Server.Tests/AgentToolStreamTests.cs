using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Protocols;
using Microsoft.AspNetCore.Http;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class AgentToolStreamTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("copilot")]
    public async Task InterleavedToolsStreamBeforeCompletionAndRoundTripThroughCopilotConsumer(string type)
    {
        var partialObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = Fragments();
        // The installed Copilot Chat 0.66 consumer misassembles these legal deltas.
        Assert.NotEqual("{\"city\":\"Paris 🌍\"}", Collect(input, copilot: true)[0]!["function"]!["arguments"]!.GetValue<string>());
        var turn = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var request = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            if (Interlocked.Increment(ref turn) == 2)
            {
                var messages = request["messages"]!.AsArray();
                Assert.Equal("call-b", messages[^2]!["tool_call_id"]!.GetValue<string>());
                Assert.Equal("call-a", messages[^1]!["tool_call_id"]!.GetValue<string>());
                Assert.Equal("{\"city\":\"Paris 🌍\"}", messages[^3]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
                await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, true, false), "text/event-stream");
                return;
            }
            context.Response.ContentType = "text/event-stream";
            for (var i = 0; i < input.Count; i++)
            {
                await context.Response.WriteAsync("data: " + input[i].ToJsonString() + "\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (i == 1) await partialObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
            }
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, type);
        var request = ProtocolRoundTripTests.Request(true);
        using var first = await proxy.Client.SendAsync(new(HttpMethod.Post, "/v1/chat/completions") { Content = TestHost.Json(request.ToJsonString()) },
            HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var output = new List<JsonObject>();
        await foreach (var data in WireReader.Sse(await first.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))
        {
            if (data == "[DONE]") break;
            var chunk = WireReader.Object(data); output.Add(chunk);
            if (chunk["choices"]?[0]?["delta"]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>() == "{\"city\":")
                partialObserved.TrySetResult();
        }
        Assert.True(partialObserved.Task.IsCompletedSuccessfully);
        var calls = Collect(output, copilot: true);
        Assert.Equal(2, calls.Count);
        Assert.True(JsonNode.DeepEquals(Collect(output, copilot: false), calls));
        Assert.Equal("lookup", calls[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Paris 🌍\"}", calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Lyon\"}", calls[1]!["function"]!["arguments"]!.GetValue<string>());
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = (string?)null, ["tool_calls"] = calls });
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-b", ["content"] = "second result" });
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-a", ["content"] = "first result" });
        using var second = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Bonjour", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, turn);
    }

    [Fact]
    public void IdentityOnlyDeltaAndUsageAfterFinishArePreserved()
    {
        var stream = new ToolCallStream();
        var identity = ChatContract.Chunk("chat", "test/model", new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject {
            ["index"] = 0, ["id"] = "a", ["type"] = "function"
        }) });
        Assert.Empty(stream.Process(identity));
        var chunk = Assert.Single(stream.Process(Chunk(0, null, "lookup", "{}")));
        Assert.Equal("a", chunk["choices"]![0]!["delta"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Single(stream.Process(ChatContract.Chunk("chat", "test/model", new(), "tool_calls")));
        var usage = new JsonObject { ["choices"] = new JsonArray(), ["usage"] = ChatContract.Usage(10, 5) };
        Assert.Same(usage, Assert.Single(stream.Process(usage)));
    }

    [Fact]
    public void NestedJsonStringsAndEscapesDoNotCloseTheActiveCallEarly()
    {
        var stream = new ToolCallStream();
        var output = new List<JsonObject>();
        output.AddRange(stream.Process(Chunk(0, "a", "first", "{\"text\":\"} \\\"")));
        output.AddRange(stream.Process(Chunk(1, "b", "second", "{}")));
        Assert.Single(Collect(output, true));
        output.AddRange(stream.Process(Chunk(0, null, null, "ok\",\"nested\":[{\"n\":1}]}")));
        output.AddRange(stream.Process(ChatContract.Chunk("chat", "test/model", new(), "tool_calls")));
        var calls = Collect(output, true);
        Assert.Equal(2, calls.Count);
        Assert.True(JsonNode.DeepEquals(calls, Collect(output, false)));
        Assert.Equal("} \"ok", ChatContract.Arguments(calls[0]!["function"]!["arguments"])["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"x\":")]
    [InlineData("[1]")]
    [InlineData("{bad}")]
    [InlineData("{}{}")]
    public void InvalidOrIncompleteArgumentsCannotFinishSuccessfully(string arguments)
    {
        var stream = new ToolCallStream();
        var error = Assert.Throws<ProxyException>(() =>
        {
            _ = stream.Process(Chunk(0, "a", "lookup", arguments)).ToArray();
            _ = stream.Process(ChatContract.Chunk("chat", "test/model", new(), "tool_calls")).ToArray();
        });
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain(arguments, error.Message);
    }

    [Fact]
    public void BufferAndIdentityLimitsFailClosed()
    {
        var stream = new ToolCallStream();
        _ = stream.Process(Chunk(0, "a", "lookup", "{\"x\":\"")).ToArray();
        Assert.Throws<ProxyException>(() => stream.Process(Chunk(1, "a", "lookup", "{}")).ToArray());
        Assert.Throws<ProxyException>(() => stream.Process(Chunk(0, "different", null, "")).ToArray());
        Assert.Throws<ProxyException>(() => new ToolCallStream().Process(Chunk(0, "a", "lookup", "{\"x\":\"" + new string('x', WireReader.MaxFrameCharacters))).ToArray());
        stream = new ToolCallStream();
        for (var i = 0; i < ToolCallStream.MaxCalls; i++) _ = stream.Process(Chunk(i, "id-" + i, "lookup", "{")).ToArray();
        Assert.Throws<ProxyException>(() => stream.Process(Chunk(ToolCallStream.MaxCalls, "overflow", "lookup", "{}")).ToArray());
    }

    private static List<JsonObject> Fragments() => [
        Chunk(0, "call-a", "look", ""), Chunk(0, null, "up", "{\"city\":"),
        Chunk(1, "call-b", "lookup", "{\"city\":"), Chunk(0, null, null, "\"Paris 🌍\"}"),
        Chunk(1, null, null, "\"Lyon\"}"), ChatContract.Chunk("chat", "test/model", new(), "tool_calls")
    ];

    private static JsonObject Chunk(int index, string? id, string? name, string arguments) =>
        ChatContract.Chunk("chat", "test/model", new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject {
            ["index"] = index, ["id"] = id, ["type"] = id is null ? null : "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
        }) });

    // Behavioral reproduction of the installed Copilot Chat 0.66 consumer:
    // identify by ID or use the last call; replace name and append arguments.
    // The second mode follows clients that instead index and concatenate fields.
    private static JsonArray Collect(IEnumerable<JsonObject> chunks, bool copilot)
    {
        var calls = new List<JsonObject>();
        var byIndex = new Dictionary<int, JsonObject>();
        foreach (var chunk in chunks)
            foreach (var choice in chunk["choices"]!.AsArray())
                foreach (var fragment in choice?["delta"]?["tool_calls"]?.AsArray() ?? [])
                {
                    var id = fragment!["id"]?.GetValue<string>();
                    var index = fragment["index"]!.GetValue<int>();
                    JsonObject? call;
                    if (copilot)
                    {
                        call = id is null ? null : calls.Find(c => c["id"]?.GetValue<string>() == id);
                        call ??= calls.LastOrDefault();
                        if (call is not null && id is not null && call["id"] is not null && call["id"]!.GetValue<string>() != id) call = null;
                    }
                    else byIndex.TryGetValue(index, out call);
                    if (call is null)
                    {
                        call = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                        calls.Add(call); byIndex[index] = call;
                    }
                    if (!string.IsNullOrEmpty(id)) call["id"] = copilot ? id : (call["id"]?.GetValue<string>() ?? "") + id;
                    foreach (var field in new[] { "name", "arguments" })
                        if (fragment["function"]?[field]?.GetValue<string>() is { Length: > 0 } text)
                            call["function"]![field] = copilot && field == "name" ? text : call["function"]![field]!.GetValue<string>() + text;
                }
        return new JsonArray(calls.Select(c => (JsonNode)c).ToArray());
    }
}
