using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

public static class ChatContract
{
    public static ProxyException Unsupported(string field) => new(400, "unsupported_parameter", $"Unsupported request field or value: {field}.");
    public static string Text(JsonNode? node, string field)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Unsupported(field);
    public static string? OptionalText(JsonNode? node) => node is null ? null : Text(node, "string");
    public static bool Bool(JsonNode? node, bool fallback = false)
        => node is null ? fallback : node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : throw Unsupported("boolean");
    public static int Integer(JsonNode? node, string field)
        => node is JsonValue value && value.TryGetValue<int>(out var result) ? result : throw Unsupported(field);

    public static JsonObject PrepareRequest(JsonObject request, ModelRoute route)
    {
        Validate(request, route);
        var capabilities = route.Model.Metadata.Capabilities;
        if (request.ContainsKey("temperature") && (capabilities.SupportsTemperature == false
            || capabilities.UnsupportedRequestParameters?.Contains("temperature", StringComparer.Ordinal) == true))
        {
            // Temperature is an optional sampling hint. Keep the original capture
            // intact while allowing the upstream model to use its own default.
            var prepared = (JsonObject)request.DeepClone();
            prepared.Remove("temperature");
            return prepared;
        }
        return request;
    }

    public static void Validate(JsonObject request, ModelRoute route)
    {
        _ = Bool(request["stream"]);
        if (request["stream_options"] is not null && request["stream_options"] is not JsonObject) throw Unsupported("stream_options");
        foreach (var field in new[] { "temperature", "top_p" })
            if (request[field] is { } parameter && (parameter is not JsonValue number || !number.TryGetValue<double>(out var value)
                || !double.IsFinite(value) || value < 0 || value > (field == "temperature" ? 2 : 1))) throw Unsupported(field);
        if (request["stop"] is { } stop)
        {
            if (stop is JsonArray stops) { foreach (var item in stops) _ = Text(item, "stop"); }
            else _ = Text(stop, "stop");
        }
        if (request["n"] is not null && Integer(request["n"], "n") != 1) throw Unsupported("n (only 1 is supported)");
        if (request["messages"] is not JsonArray { Count: > 0 } messages) throw Unsupported("messages");
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in messages)
        {
            if (item is not JsonObject message) throw Unsupported("messages");
            var role = Text(message["role"], "messages.role");
            if (message["tool_calls"] is not null && message["tool_calls"] is not JsonArray) throw Unsupported("messages.tool_calls");
            if (role is not ("system" or "developer" or "user" or "assistant" or "tool")) throw Unsupported("messages.role");
            if (role != "tool" && pending.Count > 0) throw Unsupported("unanswered tool calls");
            if (message["content"] is not null) _ = Content(message["content"]);
            else if (role != "assistant" || message["tool_calls"] is not JsonArray { Count: > 0 }) throw Unsupported("messages.content");
            if (role == "tool")
            {
                var id = Text(message["tool_call_id"], "tool_call_id");
                if (!pending.Remove(id)) throw Unsupported("unmatched tool result");
            }
            if (message["tool_calls"] is JsonArray calls)
            {
                if (role != "assistant" || !route.SupportsTools) throw Unsupported("tool_calls");
                foreach (var call in calls)
                {
                    var id = Text(call?["id"], "tool call ID");
                    if (!ids.Add(id) || Text(call?["type"], "tool type") != "function") throw Unsupported("tool call ID/type");
                    var name = Text(call?["function"]?["name"], "tool name");
                    _ = Arguments(call?["function"]?["arguments"]);
                    pending.Add(id, name);
                }
            }
        }
        if (pending.Count > 0) throw Unsupported("unanswered tool calls");
        if (request["tools"] is not null)
        {
            if (!route.SupportsTools || request["tools"] is not JsonArray tools) throw Unsupported("tools");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                if (Text(tool?["type"], "tool type") != "function" || tool?["function"] is not JsonObject function
                    || !names.Add(Text(function["name"], "tool name")) || function["parameters"] is not JsonObject) throw Unsupported("tools");
            }
        }
        foreach (var name in route.Model.Metadata.Capabilities.UnsupportedRequestParameters ?? [])
        {
            // PrepareRequest omits unsupported temperature after validating its value.
            if (name == "temperature") continue;
            // The OpenAI-compatible adapter translates these two client aliases and
            // validates the emitted field against upstream capabilities itself.
            if (route.Type is "openai" or "copilot" && name is "max_tokens" or "max_completion_tokens") continue;
            if (request[name] is not null) throw Unsupported(name);
        }
        foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
            if (request[name] is not null && Integer(request[name], name) <= 0) throw Unsupported(name);
        if (request["max_tokens"] is not null && request["max_completion_tokens"] is not null) throw Unsupported("conflicting output-token limits");
    }

    public static void ValidateNative(JsonObject request, bool anthropic)
    {
        string[] allowed = ["model", "messages", "stream", "stream_options", "tools", "tool_choice", "parallel_tool_calls", "temperature", "top_p", "stop", "max_tokens", "max_completion_tokens", "n"];
        foreach (var field in request)
            if (field.Value is not null && !allowed.Contains(field.Key, StringComparer.Ordinal)) throw Unsupported(field.Key);
        if (request["stream_options"] is JsonObject streamOptions)
            foreach (var field in streamOptions)
                if (field.Key != "include_usage") throw Unsupported("stream_options");
        if (request["stream_options"] is not null && request["stream_options"] is not JsonObject) throw Unsupported("stream_options");
        if (request["stream_options"]?["include_usage"] is { } usage) _ = Bool(usage);
        if (!anthropic && request["tool_choice"] is { } choice && OptionalText(choice) != "auto") throw Unsupported("tool_choice");
        if (!anthropic && request["parallel_tool_calls"] is { } parallel && !Bool(parallel)) throw Unsupported("parallel_tool_calls=false");
        foreach (var item in request["messages"]!.AsArray())
        {
            foreach (var field in item!.AsObject())
                if (field.Value is not null && field.Key is not ("role" or "content" or "tool_calls" or "tool_call_id" or "name")) throw Unsupported("messages." + field.Key);
        }
    }

    public static string Content(JsonNode? content)
    {
        if (content is null) return "";
        if (content is JsonValue) return Text(content, "messages.content");
        if (content is not JsonArray parts) throw Unsupported("messages.content");
        return string.Concat(parts.Select(part => part is JsonObject obj && Text(obj["type"], "content.type") == "text"
            ? Text(obj["text"], "content.text") : throw Unsupported("non-text content")));
    }

    public static JsonObject Arguments(JsonNode? node)
    {
        try { return JsonNode.Parse(Text(node, "tool arguments")) as JsonObject ?? throw Unsupported("tool arguments"); }
        catch (System.Text.Json.JsonException) { throw Unsupported("tool arguments JSON"); }
    }

    public static int? OutputLimit(JsonObject request, ModelRoute route)
    {
        var policy = route.Options.Connection.RequestPolicy;
        int? requested = request["max_completion_tokens"] is { } modern ? Integer(modern, "max_completion_tokens")
            : request["max_tokens"] is { } legacy ? Integer(legacy, "max_tokens")
            : policy.UnspecifiedOutputTokens switch
            {
                LLMUnspecifiedOutputTokensMode.Configured => policy.DefaultMaxOutputTokens,
                LLMUnspecifiedOutputTokensMode.ModelMaximum => route.Model.Metadata.MaxOutputTokens,
                _ => null
            };
        if (requested is null) return null;
        return Math.Min(requested.Value, Math.Min(route.Model.Metadata.MaxOutputTokens!.Value, policy.MaxOutputTokensCap ?? int.MaxValue));
    }

    public static JsonObject Usage(long input, long output) => new() { ["prompt_tokens"] = input, ["completion_tokens"] = output, ["total_tokens"] = input + output };
    public static JsonObject Completion(string id, string model, JsonObject message, string finish, JsonObject? usage) => new()
    {
        ["id"] = id, ["object"] = "chat.completion", ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }), ["usage"] = usage
    };
    public static JsonObject Chunk(string id, string model, JsonObject delta, string? finish = null) => new()
    {
        ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
    };
    public static long Count(JsonNode? value) => value is JsonValue node && node.TryGetValue<long>(out var count) ? count : 0;
}
