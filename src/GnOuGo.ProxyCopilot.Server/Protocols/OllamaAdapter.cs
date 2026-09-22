using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

public sealed class OllamaAdapter : IProxyAdapter
{
    public string Type => "ollama";
    public string Endpoint(ModelRoute route) => route.Options.Connection.Url.TrimEnd('/').EndsWith("/api/chat", StringComparison.Ordinal)
        ? route.Options.Connection.Url.TrimEnd('/') : OllamaEndpoints.Chat(route.Options.Connection.Url);

    public JsonObject CreateRequest(JsonObject request, ModelRoute route)
    {
        ChatContract.ValidateNative(request, anthropic: false);
        var messages = new JsonArray();
        var pending = new List<(string Id, string Name)>();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var original in request["messages"]!.AsArray())
        {
            var role = ChatContract.Text(original!["role"], "role");
            if (role == "tool")
            {
                results.Add(ChatContract.Text(original["tool_call_id"], "tool_call_id"), ChatContract.Content(original["content"]));
                if (results.Count == pending.Count)
                {
                    // Native Ollama associates tool results by name and ordering. Restore call
                    // order even when the client completed parallel tools in a different order.
                    foreach (var call in pending) messages.Add((JsonNode)new JsonObject { ["role"] = "tool", ["tool_name"] = call.Name, ["content"] = results[call.Id] });
                    pending.Clear(); results.Clear();
                }
                continue;
            }
            var message = new JsonObject { ["role"] = role == "developer" ? "system" : role, ["content"] = ChatContract.Content(original["content"]) };
            if (original["tool_calls"] is JsonArray calls)
            {
                var nativeCalls = new JsonArray();
                foreach (var call in calls)
                {
                    var name = ChatContract.Text(call!["function"]!["name"], "tool name");
                    pending.Add((ChatContract.Text(call["id"], "tool ID"), name));
                    nativeCalls.Add((JsonNode)new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = ChatContract.Arguments(call["function"]!["arguments"]) } });
                }
                message["tool_calls"] = nativeCalls;
            }
            messages.Add((JsonNode)message);
        }
        var body = new JsonObject { ["model"] = route.Model.UpstreamId, ["messages"] = messages, ["stream"] = ChatContract.Bool(request["stream"]), ["think"] = false };
        if (request["tools"] is { } tools)
        {
            foreach (var tool in tools.AsArray()) if (ChatContract.Bool(tool?["function"]?["strict"])) throw ChatContract.Unsupported("tools.function.strict");
            body["tools"] = tools.DeepClone();
            foreach (var tool in body["tools"]!.AsArray()) tool!["function"]!.AsObject().Remove("strict");
        }
        var options = new JsonObject();
        foreach (var name in new[] { "temperature", "top_p" }) if (request[name] is { } value) options[name] = value.DeepClone();
        if (ChatContract.OutputLimit(request, route) is { } limit) options["num_predict"] = limit;
        if (request["stop"] is { } stop) options["stop"] = stop is JsonArray ? stop.DeepClone() : new JsonArray(stop.DeepClone());
        if (options.Count > 0) body["options"] = options;
        return body;
    }

    public async IAsyncEnumerable<JsonObject> ReadResponse(Stream stream, bool streaming, bool includeUsage, ModelRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        if (!streaming)
        {
            var response = await WireReader.ObjectAsync(stream, ct);
            Validate(response);
            if (!ChatContract.Bool(response["done"])) throw WireReader.Invalid("Incomplete Ollama response.");
            var message = new JsonObject { ["role"] = "assistant", ["content"] = response["message"]?["content"]?.DeepClone() ?? JsonValue.Create("") };
            var calls = Calls(response["message"]?["tool_calls"], id, 0, false);
            if (calls.Count > 0) message["tool_calls"] = calls;
            yield return ChatContract.Completion(id, route.Id, message, Finish(response, calls.Count > 0), Usage(response));
            yield break;
        }
        var finished = false;
        var toolCount = 0;
        yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["role"] = "assistant", ["content"] = "" });
        await foreach (var line in WireReader.Lines(stream, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = WireReader.Object(line);
            Validate(response);
            var delta = new JsonObject();
            if (response["message"]?["content"] is { } content) delta["content"] = content.DeepClone();
            var calls = Calls(response["message"]?["tool_calls"], id, toolCount, true);
            toolCount += calls.Count;
            if (calls.Count > 0) delta["tool_calls"] = calls;
            if (delta.Count > 0) yield return ChatContract.Chunk(id, route.Id, delta);
            if (ChatContract.Bool(response["done"]))
            {
                yield return ChatContract.Chunk(id, route.Id, new JsonObject(), Finish(response, toolCount > 0));
                {
                    var usage = ChatContract.Chunk(id, route.Id, new JsonObject());
                    usage["choices"] = new JsonArray(); usage["usage"] = Usage(response);
                    yield return usage;
                }
                finished = true;
                break;
            }
        }
        if (!finished) throw WireReader.Invalid("Ollama stream ended before completion.");
    }

    private static void Validate(JsonObject response)
    {
        if (response["error"] is not null || response["message"] is not JsonObject) throw WireReader.Invalid("Ollama returned an invalid chat response.");
    }
    private static JsonArray Calls(JsonNode? source, string id, int offset, bool streaming)
    {
        var calls = new JsonArray();
        if (source is null) return calls;
        if (source is not JsonArray array) throw WireReader.Invalid("Invalid Ollama tool calls.");
        foreach (var call in array)
        {
            if (call?["function"]?["arguments"] is not JsonObject arguments) throw WireReader.Invalid("Invalid Ollama tool arguments.");
            var index = offset + calls.Count;
            var mapped = new JsonObject { ["id"] = $"call_{id}_{index}", ["type"] = "function", ["function"] = new JsonObject {
                ["name"] = ChatContract.Text(call["function"]!["name"], "tool name"), ["arguments"] = arguments.ToJsonString(ProxyJsonContext.Default.Options) } };
            if (streaming) mapped["index"] = index;
            calls.Add((JsonNode)mapped);
        }
        return calls;
    }
    private static string Finish(JsonObject response, bool tools) => tools ? "tool_calls"
        : ChatContract.OptionalText(response["done_reason"]) switch { "length" => "length", null or "stop" => "stop", _ => throw WireReader.Invalid("Unsupported Ollama stop reason.") };
    private static JsonObject Usage(JsonObject response) => ChatContract.Usage(ChatContract.Count(response["prompt_eval_count"]), ChatContract.Count(response["eval_count"]));
}
