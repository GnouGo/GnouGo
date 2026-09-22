using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class ProtocolRoundTripTests
{
    [Theory]
    [InlineData("openai", true)] [InlineData("copilot", true)] [InlineData("anthropic", true)] [InlineData("ollama", true)]
    [InlineData("openai", false)] [InlineData("copilot", false)] [InlineData("anthropic", false)] [InlineData("ollama", false)]
    public async Task ConversationParallelToolsAndReversedResultsCompleteAcrossNativeProtocols(string type, bool streaming)
    {
        var received = new ConcurrentQueue<JsonObject>();
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject(); received.Enqueue(body);
            Assert.Equal("vendor/upstream-model", body["model"]!.GetValue<string>());
            var endpoint = type switch { "anthropic" => "/v1/messages", "ollama" => "/api/chat", _ => "/chat/completions" };
            Assert.Equal("/deployments/vendor%2Fupstream-model" + endpoint, context.Features.Get<IHttpRequestFeature>()!.RawTarget);
            var first = received.Count == 1;
            if (type == "anthropic") Assert.Equal("2023-06-01", context.Request.Headers["anthropic-version"]);
            await Write(context, Fixture(type, streaming, first), streaming ? type == "ollama" ? "application/x-ndjson" : "text/event-stream" : "application/json");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url + "/deployments/{model_name}", type);
        var request = Request(streaming);
        using var firstResponse = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var firstText = await firstResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var calls = ExtractCalls(firstText, streaming);
        Assert.Equal(2, calls.Count);
        Assert.NotEqual(calls[0]!["id"]!.GetValue<string>(), calls[1]!["id"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Paris\"}", calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Lyon\"}", calls[1]!["function"]!["arguments"]!.GetValue<string>());
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = (string?)null, ["tool_calls"] = calls.DeepClone() });
        // Deliberately complete same-name parallel tools out of order.
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = calls[1]!["id"]!.DeepClone(), ["content"] = "Lyon result" });
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = calls[0]!["id"]!.DeepClone(), ["content"] = "Paris result" });
        using var secondResponse = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var secondText = await secondResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Bonjour", secondText);
        if (streaming) { Assert.Contains("[DONE]", secondText); Assert.Contains("\"total_tokens\":12", secondText); }
        Assert.Equal(2, received.Count);
        var translated = received.Last();
        var translatedMessages = translated["messages"]!.AsArray();
        if (type == "ollama")
        {
            Assert.Equal("Paris result", translatedMessages[^2]!["content"]!.GetValue<string>());
            Assert.Equal("Lyon result", translatedMessages[^1]!["content"]!.GetValue<string>());
            Assert.Equal("lookup", translatedMessages[^1]!["tool_name"]!.GetValue<string>());
            Assert.False(translated["think"]!.GetValue<bool>());
        }
        else if (type == "anthropic")
        {
            Assert.Equal("You are helpful.", translated["system"]!.GetValue<string>());
            var resultBlocks = translatedMessages[^1]!["content"]!.AsArray();
            Assert.Equal(calls[1]!["id"]!.GetValue<string>(), resultBlocks[0]!["tool_use_id"]!.GetValue<string>());
            Assert.Equal("Lyon result", resultBlocks[0]!["content"]!.GetValue<string>());
        }
        else Assert.Equal(calls[1]!["id"]!.GetValue<string>(), translatedMessages[^2]!["tool_call_id"]!.GetValue<string>());
        using var snapshotResponse = await proxy.Client.GetAsync("/api/traffic", TestContext.Current.CancellationToken);
        var snapshot = await TestHost.Read(snapshotResponse);
        Assert.Equal(2, snapshot["calls"]!.AsArray().Count);
        Assert.All(snapshot["calls"]!.AsArray(), call => Assert.Equal("completed", call!["status"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("anthropic", "response_format", "{\"type\":\"json_object\"}")]
    [InlineData("ollama", "reasoning_effort", "\"high\"")]
    [InlineData("ollama", "tool_choice", "\"required\"")]
    public async Task UnsupportedNativeOptionsFailBeforeDispatch(string type, string field, string value)
    {
        var calls = 0;
        await using var upstream = await TestHost.Upstream(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, type);
        var request = Request(false); request[field] = JsonNode.Parse(value);
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, calls);
        Assert.Contains("unsupported_parameter", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("openai")] [InlineData("anthropic")] [InlineData("ollama")]
    public async Task TruncatedStreamsFailInsteadOfInventingCompletion(string type)
    {
        IProxyAdapter adapter = type switch { "anthropic" => new AnthropicAdapter(), "ollama" => new OllamaAdapter(), _ => new OpenAiAdapter() };
        var options = ConfigurationAndTrafficTests.Options();
        var route = new Configuration.ModelRegistry(options).Models.Single();
        var data = type switch
        {
            "anthropic" => "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{}}}\n\n",
            "ollama" => "{\"message\":{\"content\":\"hello\"},\"done\":false}\n",
            _ => "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\n"
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data));
        await Assert.ThrowsAsync<ProxyException>(async () => { await foreach (var _ in adapter.ReadResponse(stream, true, false, route, CancellationToken.None)) { } });
    }

    [Fact]
    public async Task WireReaderHandlesMultilineSseCommentsAndUtf8AndBoundsFrames()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(": heartbeat\r\ndata: {\"text\":\r\ndata: \"🌍\"}\r\n\r\n"));
        var events = new List<string>(); await foreach (var value in WireReader.Sse(input, CancellationToken.None)) events.Add(value);
        Assert.Equal("🌍", WireReader.Object(Assert.Single(events))["text"]!.GetValue<string>());
        using var large = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', WireReader.MaxFrameCharacters + 1)));
        await Assert.ThrowsAsync<ProxyException>(async () => { await foreach (var _ in WireReader.Lines(large, CancellationToken.None)) { } });
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task DashboardReceivesUsageEvenWhenClientDoesNotRequestUsageChunk(string type)
    {
        await using var upstream = await TestHost.Upstream(context => Write(context, Fixture(type, true, false), type == "ollama" ? "application/x-ndjson" : "text/event-stream"));
        await using var proxy = await TestHost.Proxy(upstream.Url, type);
        var request = Request(true); request.Remove("stream_options");
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"usage\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var snapshot = await proxy.Client.GetAsync("/api/traffic", TestContext.Current.CancellationToken);
        var body = await TestHost.Read(snapshot);
        Assert.Equal(12, body["calls"]![0]!["usage"]!["total_tokens"]!.GetValue<int>());
    }

    internal static JsonObject Request(bool streaming) => JsonNode.Parse("""
        {"model":"test/model","messages":[{"role":"system","content":"You are helpful."},{"role":"user","content":"Look up Paris and Lyon."}],"tools":[{"type":"function","function":{"name":"lookup","description":"Look up a city","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]}
        """)!.AsObject().Also(value => { value["stream"] = streaming; if (streaming) value["stream_options"] = new JsonObject { ["include_usage"] = true }; });

    internal static JsonArray ExtractCalls(string text, bool streaming)
    {
        if (!streaming) return JsonNode.Parse(text)!["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
        var calls = new SortedDictionary<int, JsonObject>();
        foreach (var line in text.Split('\n').Where(line => line.StartsWith("data: {") ))
        {
            var chunk = JsonNode.Parse(line[6..])!;
            foreach (var choice in chunk["choices"]!.AsArray())
                foreach (var call in choice?["delta"]?["tool_calls"]?.AsArray() ?? [])
                {
                    var index = call!["index"]!.GetValue<int>();
                    if (!calls.TryGetValue(index, out var output)) calls[index] = output = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                    if (call["id"] is { } id) output["id"] = id.DeepClone();
                    foreach (var field in new[] { "name", "arguments" })
                        if (call["function"]?[field] is { } part) output["function"]![field] = output["function"]![field]!.GetValue<string>() + part.GetValue<string>();
                }
        }
        return new JsonArray(calls.Values.Select(call => (JsonNode)call).ToArray());
    }

    internal static async Task Write(HttpContext context, string body, string contentType)
    {
        context.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        // Deliberately split JSON, SSE framing and non-ASCII code points at byte boundaries.
        for (var index = 0; index < bytes.Length; index += 7)
        {
            await context.Response.Body.WriteAsync(bytes.AsMemory(index, Math.Min(7, bytes.Length - index)), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    internal static string Fixture(string type, bool streaming, bool tools)
    {
        var functionCalls = JsonNode.Parse("""[{"id":"a","type":"function","function":{"name":"lookup","arguments":"{\"city\":\"Paris\"}"}},{"id":"b","type":"function","function":{"name":"lookup","arguments":"{\"city\":\"Lyon\"}"}}]""")!.AsArray();
        var nativeCalls = JsonNode.Parse("""[{"function":{"name":"lookup","arguments":{"city":"Paris"}}},{"function":{"name":"lookup","arguments":{"city":"Lyon"}}}]""")!;
        string Sse(JsonObject obj) => "data: " + obj.ToJsonString() + "\n\n";
        if (type == "ollama") return new JsonObject { ["model"] = "vendor/upstream-model", ["message"] = new JsonObject {
            ["role"] = "assistant", ["content"] = tools ? "" : "Bonjour 🌍", ["tool_calls"] = tools ? nativeCalls : new JsonArray() },
            ["done"] = true, ["done_reason"] = "stop", ["prompt_eval_count"] = 10, ["eval_count"] = 2 }.ToJsonString() + (streaming ? "\n" : "");
        if (type == "anthropic")
        {
            var blocks = tools ? JsonNode.Parse("""[{"type":"tool_use","id":"a","name":"lookup","input":{"city":"Paris"}},{"type":"tool_use","id":"b","name":"lookup","input":{"city":"Lyon"}}]""")!.AsArray()
                : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Bonjour 🌍" });
            var usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 2 };
            if (!streaming) return new JsonObject { ["id"] = "message-1", ["content"] = blocks, ["stop_reason"] = tools ? "tool_use" : "end_turn", ["usage"] = usage }.ToJsonString();
            var result = Sse(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "message-1", ["usage"] = usage } });
            for (var index = 0; index < blocks.Count; index++)
            {
                var block = blocks[index]!.DeepClone();
                var input = tools ? block["input"]!.ToJsonString() : "Bonjour 🌍";
                block[tools ? "input" : "text"] = tools ? new JsonObject() : JsonValue.Create("");
                result += Sse(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block });
                result += Sse(new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = new JsonObject { ["type"] = tools ? "input_json_delta" : "text_delta", [tools ? "partial_json" : "text"] = input } });
                result += Sse(new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
            }
            return result + Sse(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = tools ? "tool_use" : "end_turn" }, ["usage"] = new JsonObject { ["output_tokens"] = 2 } }) + Sse(new JsonObject { ["type"] = "message_stop" });
        }
        var message = new JsonObject { ["role"] = "assistant", ["content"] = tools ? "" : "Bonjour 🌍" };
        if (tools) message["tool_calls"] = functionCalls;
        if (!streaming) return ChatContract.Completion("chat-1", "upstream", message, tools ? "tool_calls" : "stop", ChatContract.Usage(10, 2)).ToJsonString();
        if (tools) for (var i = 0; i < functionCalls.Count; i++) functionCalls[i]!["index"] = i;
        var usageChunk = ChatContract.Chunk("chat-1", "upstream", new()); usageChunk["choices"] = new JsonArray(); usageChunk["usage"] = ChatContract.Usage(10, 2);
        return Sse(ChatContract.Chunk("chat-1", "upstream", message)) + Sse(ChatContract.Chunk("chat-1", "upstream", new(), tools ? "tool_calls" : "stop")) + Sse(usageChunk) + "data: [DONE]\n\n";
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> configure) { configure(value); return value; }
}
