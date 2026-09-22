using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

public sealed class AnthropicAdapter : IProxyAdapter
{
    public string Type => "anthropic";
    public string Endpoint(ModelRoute route)
    {
        var url = route.Options.Connection.Url.TrimEnd('/');
        return url.EndsWith("/messages", StringComparison.Ordinal) ? url : url.EndsWith("/v1", StringComparison.Ordinal) ? url + "/messages" : url + "/v1/messages";
    }

    public JsonObject CreateRequest(JsonObject request, ModelRoute route)
    {
        ChatContract.ValidateNative(request, anthropic: true);
        var messages = new JsonArray();
        var system = new List<string>();
        foreach (var message in request["messages"]!.AsArray())
        {
            var role = ChatContract.Text(message!["role"], "role");
            if (role is "system" or "developer")
            {
                if (messages.Count > 0) throw ChatContract.Unsupported("system messages after conversation start");
                system.Add(ChatContract.Content(message["content"]));
                continue;
            }
            var blocks = new JsonArray();
            if (role == "tool")
            {
                blocks.Add((JsonNode)new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = message["tool_call_id"]!.DeepClone(), ["content"] = ChatContract.Content(message["content"]) });
                role = "user";
            }
            else
            {
                var text = ChatContract.Content(message["content"]);
                if (text.Length > 0) blocks.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
                if (message["tool_calls"] is JsonArray calls)
                    foreach (var call in calls)
                        blocks.Add((JsonNode)new JsonObject { ["type"] = "tool_use", ["id"] = call!["id"]!.DeepClone(),
                            ["name"] = call["function"]!["name"]!.DeepClone(), ["input"] = ChatContract.Arguments(call["function"]!["arguments"]) });
                if (blocks.Count == 0) blocks.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = "" });
            }
            if (messages.LastOrDefault() is JsonObject previous && ChatContract.OptionalText(previous["role"]) == role)
                foreach (var block in blocks) previous["content"]!.AsArray().Add(block!.DeepClone());
            else messages.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = blocks });
        }
        if (messages.Count == 0) throw ChatContract.Unsupported("messages must contain a conversation");
        var body = new JsonObject { ["model"] = route.Model.UpstreamId, ["messages"] = messages,
            ["max_tokens"] = ChatContract.OutputLimit(request, route), ["stream"] = ChatContract.Bool(request["stream"]) };
        if (system.Count > 0) body["system"] = string.Join("\n\n", system);
        foreach (var name in new[] { "temperature", "top_p" }) if (request[name] is { } value) body[name] = value.DeepClone();
        if (request["stop"] is { } stop) body["stop_sequences"] = stop is JsonArray ? stop.DeepClone() : new JsonArray(stop.DeepClone());
        var choice = request["tool_choice"];
        if (request["tools"] is JsonArray tools && !(choice is JsonValue && ChatContract.OptionalText(choice) == "none"))
        {
            var nativeTools = new JsonArray();
            foreach (var tool in tools)
            {
                var function = tool!["function"]!.AsObject();
                if (ChatContract.Bool(function["strict"])) throw ChatContract.Unsupported("tools.function.strict");
                nativeTools.Add((JsonNode)new JsonObject { ["name"] = function["name"]!.DeepClone(),
                    ["description"] = function["description"]?.DeepClone(), ["input_schema"] = function["parameters"]!.DeepClone() });
            }
            body["tools"] = nativeTools;
            JsonObject nativeChoice;
            if (choice is JsonObject named)
            {
                if (ChatContract.OptionalText(named["type"]) != "function") throw ChatContract.Unsupported("tool_choice");
                nativeChoice = new JsonObject { ["type"] = "tool", ["name"] = ChatContract.Text(named["function"]?["name"], "tool_choice.function.name") };
            }
            else nativeChoice = new JsonObject { ["type"] = ChatContract.OptionalText(choice) switch
            {
                null or "auto" => "auto", "required" => "any", _ => throw ChatContract.Unsupported("tool_choice")
            } };
            if (request["parallel_tool_calls"] is { } parallel) nativeChoice["disable_parallel_tool_use"] = !ChatContract.Bool(parallel);
            body["tool_choice"] = nativeChoice;
        }
        return body;
    }

    public async IAsyncEnumerable<JsonObject> ReadResponse(Stream stream, bool streaming, bool includeUsage, ModelRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!streaming)
        {
            var response = await WireReader.ObjectAsync(stream, ct);
            if (response["content"] is not JsonArray blocks) throw WireReader.Invalid("Anthropic response has no content.");
            var text = new StringBuilder();
            var calls = new JsonArray();
            foreach (var block in blocks)
            {
                switch (ChatContract.OptionalText(block?["type"]))
                {
                    case "text": text.Append(ChatContract.Text(block!["text"], "text")); break;
                    case "tool_use":
                        calls.Add((JsonNode)new JsonObject { ["id"] = block!["id"]!.DeepClone(), ["type"] = "function", ["function"] = new JsonObject
                            { ["name"] = block["name"]!.DeepClone(), ["arguments"] = block["input"]!.ToJsonString(ProxyJsonContext.Default.Options) } }); break;
                    default: throw WireReader.Invalid("Anthropic returned an unsupported content block.");
                }
            }
            var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
            if (calls.Count > 0) message["tool_calls"] = calls;
            yield return ChatContract.Completion(ChatContract.Text(response["id"], "id"), route.Id, message,
                Finish(ChatContract.OptionalText(response["stop_reason"])), Usage(response["usage"]));
            yield break;
        }
        var id = "";
        var stop = false;
        var finished = false;
        var blocksByIndex = new Dictionary<int, ToolBlock>();
        var activeBlocks = new HashSet<int>();
        var nextToolIndex = 0;
        JsonObject? usage = null;
        await foreach (var data in WireReader.Sse(stream, ct))
        {
            var evt = WireReader.Object(data);
            var type = ChatContract.OptionalText(evt["type"]);
            if (type == "ping") continue;
            if (type == "error") throw WireReader.Invalid("Anthropic reported a streaming error.");
            if (type != "message_start" && id.Length == 0) throw WireReader.Invalid("Anthropic stream is missing message_start.");
            switch (type)
            {
                case "message_start":
                    if (id.Length > 0) throw WireReader.Invalid("Duplicate Anthropic message_start.");
                    id = ChatContract.Text(evt["message"]?["id"], "id");
                    usage = Usage(evt["message"]?["usage"]);
                    yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["role"] = "assistant", ["content"] = "" });
                    break;
                case "content_block_start":
                {
                    var index = ChatContract.Integer(evt["index"], "index");
                    if (!activeBlocks.Add(index)) throw WireReader.Invalid("Duplicate Anthropic content block.");
                    var block = evt["content_block"]!;
                    var kind = ChatContract.OptionalText(block["type"]);
                    if (kind == "tool_use")
                    {
                        var tool = new ToolBlock(nextToolIndex++);
                        blocksByIndex.Add(index, tool);
                        var input = block["input"] is JsonObject { Count: > 0 } initial ? initial.ToJsonString(ProxyJsonContext.Default.Options) : "";
                        tool.Arguments.Append(input);
                        yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject {
                            ["index"] = tool.Index, ["id"] = block["id"]!.DeepClone(), ["type"] = "function", ["function"] = new JsonObject {
                                ["name"] = block["name"]!.DeepClone(), ["arguments"] = input } }) });
                    }
                    else if (kind == "text")
                    {
                        if (ChatContract.OptionalText(block["text"]) is { Length: > 0 } text)
                            yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["content"] = text });
                    }
                    else throw WireReader.Invalid("Anthropic returned an unsupported content block.");
                    break;
                }
                case "content_block_delta":
                {
                    var index = ChatContract.Integer(evt["index"], "index");
                    if (!activeBlocks.Contains(index)) throw WireReader.Invalid("Anthropic delta has no active block.");
                    var delta = evt["delta"]!;
                    if (ChatContract.OptionalText(delta["type"]) == "text_delta")
                        yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["content"] = delta["text"]!.DeepClone() });
                    else if (ChatContract.OptionalText(delta["type"]) == "input_json_delta" && blocksByIndex.TryGetValue(index, out var tool))
                    {
                        var fragment = ChatContract.Text(delta["partial_json"], "partial_json");
                        if (tool.Arguments.Length + fragment.Length > WireReader.MaxFrameCharacters) throw WireReader.Invalid("Tool arguments exceed the size limit.");
                        tool.Arguments.Append(fragment);
                        yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject {
                            ["index"] = tool.Index, ["function"] = new JsonObject { ["arguments"] = fragment } }) });
                    }
                    else throw WireReader.Invalid("Anthropic returned an unsupported delta.");
                    break;
                }
                case "content_block_stop":
                {
                    var index = ChatContract.Integer(evt["index"], "index");
                    if (!activeBlocks.Remove(index)) throw WireReader.Invalid("Anthropic stopped an unknown block.");
                    if (blocksByIndex.TryGetValue(index, out var tool))
                    {
                        if (tool.Arguments.Length == 0)
                        {
                            tool.Arguments.Append("{}");
                            yield return ChatContract.Chunk(id, route.Id, new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject {
                                ["index"] = tool.Index, ["function"] = new JsonObject { ["arguments"] = "{}" } }) });
                        }
                        _ = WireReader.Object(tool.Arguments.ToString());
                    }
                    break;
                }
                case "message_delta":
                    if (activeBlocks.Count != 0 || finished) throw WireReader.Invalid("Anthropic ended with incomplete content blocks.");
                    if (evt["usage"] is JsonObject updates)
                    {
                        var input = updates["input_tokens"] is not null ? InputTokens(updates) : ChatContract.Count(usage?["prompt_tokens"]);
                        usage = ChatContract.Usage(input, ChatContract.Count(updates["output_tokens"]));
                    }
                    yield return ChatContract.Chunk(id, route.Id, new JsonObject(), Finish(ChatContract.OptionalText(evt["delta"]?["stop_reason"])));
                    finished = true;
                    break;
                case "message_stop": stop = true; break;
                default: throw WireReader.Invalid("Anthropic returned an unsupported stream event.");
            }
            if (stop) break;
        }
        if (!stop || !finished || activeBlocks.Count > 0) throw WireReader.Invalid("Anthropic stream ended before completion.");
        if (usage is not null)
        {
            var chunk = ChatContract.Chunk(id, route.Id, new JsonObject());
            chunk["choices"] = new JsonArray(); chunk["usage"] = usage;
            yield return chunk;
        }
    }

    private static string Finish(string? reason) => reason switch
    {
        "tool_use" => "tool_calls", "max_tokens" => "length", "end_turn" or "stop_sequence" => "stop",
        "refusal" => "content_filter", _ => throw WireReader.Invalid("Unsupported Anthropic stop reason.")
    };
    private static long InputTokens(JsonNode? usage) => ChatContract.Count(usage?["input_tokens"])
        + ChatContract.Count(usage?["cache_creation_input_tokens"]) + ChatContract.Count(usage?["cache_read_input_tokens"]);
    private static JsonObject Usage(JsonNode? usage) => ChatContract.Usage(InputTokens(usage), ChatContract.Count(usage?["output_tokens"]));
    private sealed class ToolBlock(int index)
    {
        public int Index { get; } = index;
        public StringBuilder Arguments { get; } = new();
    }
}
