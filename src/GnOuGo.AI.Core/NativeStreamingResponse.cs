using System.Text.Json.Nodes;
using System.Text;

namespace GnOuGo.AI.Core;

/// <summary>Consumes real provider events and returns the existing complete native response shape.</summary>
internal static class NativeStreamingResponse
{
    internal static async Task<JsonObject> ReadAsync(Stream stream, string protocol, LLMHttpAttempt attempt, Func<Task> checkpoint, CancellationToken ct)
    {
        return protocol switch
        {
            LLMTransportResolver.ChatCompletions => await ChatAsync(stream, attempt, checkpoint, ct),
            LLMTransportResolver.Responses => await ResponsesAsync(stream, attempt, checkpoint, ct),
            LLMTransportResolver.Messages => await MessagesAsync(stream, attempt, checkpoint, ct),
            LLMTransportResolver.OllamaChat => await OllamaAsync(stream, attempt, checkpoint, ct),
            _ => throw Invalid("No native stream reader is available for this protocol.")
        };
    }

    private static JsonObject Object(string value) => JsonNode.Parse(value) as JsonObject ?? throw Invalid("Expected a native stream object.");
    private static InvalidDataException Invalid(string message) => new(message);
    private static InvalidDataException ReportedError(JsonNode error, LLMHttpAttempt attempt)
    {
        attempt.TerminationStatus = "provider_error";
        var code = Text((error as JsonObject)?["code"]) ?? Text((error as JsonObject)?["type"]);
        if (code is { Length: > 0 and <= 64 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) attempt.ProviderErrorCode = code;
        return Invalid("The provider reported an error inside its native generation stream; no completion was returned.");
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int Index(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0 ? index : throw Invalid("Native stream index is missing or invalid.");
    private static bool KnownUsage(JsonNode? usage, string input, string output)
        => usage?[input] is JsonValue i && i.TryGetValue<long>(out var a) && a >= 0 &&
           usage[output] is JsonValue o && o.TryGetValue<long>(out var b) && b >= 0;
    private static async Task ObserveOutputAsync(LLMHttpAttempt attempt, Func<Task> checkpoint, params JsonNode?[] values)
    {
        if (attempt.FirstOutputAtUtc is not null || !values.Any(value => !string.IsNullOrWhiteSpace(Text(value)))) return;
        // CapturedResponseStream records the arrival of the bytes that completed
        // this event. Initial roles, comments and reasoning are not output.
        attempt.FirstOutputAtUtc = attempt.LastByteAtUtc;
        if (attempt.FirstOutputAtUtc is not null) await checkpoint();
    }
    private sealed class Fragments
    {
        private readonly Dictionary<(JsonObject Target, string Key), StringBuilder> _values = [];
        internal void Append(JsonObject target, string key, JsonNode? value)
        {
            if (value is null) return;
            if (Text(value) is not { } fragment) throw Invalid("A native text delta is invalid.");
            if (!_values.TryGetValue((target, key), out var text))
            { text = new StringBuilder(Text(target[key]) ?? ""); _values.Add((target, key), text); }
            text.Append(fragment);
        }
        internal void Flush()
        {
            foreach (var entry in _values) entry.Key.Target[entry.Key.Key] = entry.Value.ToString();
        }
    }
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var field in source) target[field.Key] = field.Value?.DeepClone();
    }
    private static async Task IdentityAsync(JsonObject root, LLMHttpAttempt attempt, Func<Task> checkpoint)
    {
        var id = Text(root["id"]);
        if (string.IsNullOrEmpty(id)) return; // Azure filter/usage metadata has no generation identity.
        if (attempt.ProviderResponseId is not null && attempt.ProviderResponseId != id) throw Invalid("A stream mixed provider response identities.");
        if (attempt.ProviderResponseId is null) { attempt.ProviderResponseId = id; await checkpoint(); }
    }

    private static async Task<JsonObject> ChatAsync(Stream stream, LLMHttpAttempt attempt, Func<Task> checkpoint, CancellationToken ct)
    {
        var fragments = new Fragments();
        var result = new JsonObject { ["object"] = "chat.completion" };
        var choices = new SortedDictionary<int, JsonObject>();
        var tools = new Dictionary<(int Choice, int Tool), JsonObject>();
        var done = false;
        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            if (data == "[DONE]") { done = true; break; }
            var chunk = Object(data);
            if (chunk["error"] is { } reported) throw ReportedError(reported, attempt);
            await IdentityAsync(chunk, attempt, checkpoint);
            foreach (var name in new[] { "id", "model", "created", "system_fingerprint", "service_tier" })
                if (chunk[name] is { } field && (name is not ("id" or "model") || !string.IsNullOrEmpty(Text(field))) &&
                    (name != "created" || field.GetValue<long>() > 0)) result[name] = field.DeepClone();
            foreach (var name in new[] { "prompt_filter_results", "prompt_annotations" })
                if (chunk[name] is JsonArray annotations)
                {
                    if (result[name] is not JsonArray) result[name] = new JsonArray();
                    foreach (var annotation in annotations) result[name]!.AsArray().Add(annotation?.DeepClone());
                }
            if (chunk["choices"] is not JsonArray items) throw Invalid("Chat stream choices are missing.");
            foreach (var item in items)
            {
                var index = Index(item?["index"]);
                if (!choices.TryGetValue(index, out var choice))
                {
                    choice = new JsonObject { ["index"] = index, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" } };
                    choices.Add(index, choice);
                }
                var message = choice["message"]!.AsObject();
                if (item!["delta"] is JsonObject delta)
                {
                    if (choice["finish_reason"] is not null && delta.Count > 0) throw Invalid("Chat content arrived after its finish reason.");
                    if (delta["role"] is { } role) message["role"] = role.DeepClone();
                    foreach (var key in new[] { "content", "refusal", "reasoning_content", "reasoning" }) fragments.Append(message, key, delta[key]);
                    await ObserveOutputAsync(attempt, checkpoint, delta["content"], delta["refusal"]);
                    if (delta["function_call"] is JsonObject legacy)
                    {
                        if (message["function_call"] is not JsonObject) message["function_call"] = new JsonObject();
                        foreach (var key in new[] { "name", "arguments" }) fragments.Append(message["function_call"]!.AsObject(), key, legacy[key]);
                        await ObserveOutputAsync(attempt, checkpoint, legacy["name"], legacy["arguments"]);
                    }
                    if (delta["tool_calls"] is JsonArray calls)
                        foreach (var call in calls)
                        {
                            var toolIndex = Index(call?["index"]);
                            if (!tools.TryGetValue((index, toolIndex), out var tool)) { tool = new JsonObject { ["function"] = new JsonObject() }; tools.Add((index, toolIndex), tool); }
                            foreach (var key in new[] { "id", "type" })
                                if (call![key] is { } value)
                                {
                                    if (tool[key] is not null && Text(tool[key]) != Text(value)) throw Invalid("A stream changed a tool identity.");
                                    tool[key] = value.DeepClone();
                                }
                            if (call!["function"] is JsonObject function)
                            {
                                foreach (var key in new[] { "name", "arguments" }) fragments.Append(tool["function"]!.AsObject(), key, function[key]);
                                await ObserveOutputAsync(attempt, checkpoint, function["name"], function["arguments"]);
                            }
                        }
                }
                foreach (var name in new[] { "content_filter_results", "content_filter_offsets" })
                    if (item[name] is { } annotation)
                    {
                        if (choice["stream_filter_annotations"] is not JsonArray) choice["stream_filter_annotations"] = new JsonArray();
                        choice["stream_filter_annotations"]!.AsArray().Add((JsonNode)new JsonObject { [name] = annotation.DeepClone() });
                        choice[name] = annotation.DeepClone();
                    }
                if (item["finish_reason"] is { } finish)
                {
                    // Azure asynchronous filtering can supply a later content_filter finish after generated text.
                    if (choice["finish_reason"] is not null && Text(finish) != "content_filter") throw Invalid("Duplicate chat finish reason.");
                    choice["finish_reason"] = finish.DeepClone();
                    if (attempt.TerminationStatus is null or "stop") attempt.TerminationStatus = Text(finish);
                }
                if (item["logprobs"] is JsonObject logprobs)
                {
                    if (choice["logprobs"] is not JsonObject) choice["logprobs"] = new JsonObject();
                    foreach (var field in logprobs)
                    {
                        if (field.Value is not JsonArray values) continue;
                        if (choice["logprobs"]![field.Key] is not JsonArray) choice["logprobs"]![field.Key] = new JsonArray();
                        foreach (var value in values) choice["logprobs"]![field.Key]!.AsArray().Add(value?.DeepClone());
                    }
                }
            }
            if (chunk["usage"] is JsonObject usage)
            {
                attempt.UsageKnown = KnownUsage(usage, "prompt_tokens", "completion_tokens");
                if (choices.Count > 0 && choices.Values.All(c => c["finish_reason"] is not null)) result["usage"] = usage.DeepClone();
                else result["stream_provisional_usage"] = usage.DeepClone();
            }
        }
        if (!done || choices.Count == 0 || choices.Values.Any(c => c["finish_reason"] is null)) throw new IOException("Chat stream ended without complete choices and [DONE].");
        fragments.Flush();
        foreach (var (index, choice) in choices)
        {
            var ordered = tools.Where(t => t.Key.Choice == index).OrderBy(t => t.Key.Tool).ToArray();
            if (ordered.Length == 0) continue;
            for (var i = 0; i < ordered.Length; i++)
                if (ordered[i].Key.Tool != i || Text(ordered[i].Value["id"]) is null || Text(ordered[i].Value["function"]?["name"]) is null)
                    throw Invalid("A streamed tool call is incomplete.");
            choice["message"]!["tool_calls"] = new JsonArray(ordered.Select(t => (JsonNode?)t.Value).ToArray());
        }
        result["choices"] = new JsonArray(choices.Values.Select(c => (JsonNode?)c).ToArray());
        attempt.TerminationStatus = Text(choices.Values.First()["finish_reason"]);
        attempt.UsageKnown = KnownUsage(result["usage"], "prompt_tokens", "completion_tokens");
        return result;
    }

    private static async Task<JsonObject> ResponsesAsync(Stream stream, LLMHttpAttempt attempt, Func<Task> checkpoint, CancellationToken ct)
    {
        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            var evt = Object(data);
            var type = Text(evt["type"]);
            if (type == "error") throw ReportedError(evt["error"] ?? evt, attempt);
            if (type is "response.output_text.delta" or "response.refusal.delta" or "response.function_call_arguments.delta")
                await ObserveOutputAsync(attempt, checkpoint, evt["delta"]);
            if (type != "keepalive" && evt["sequence_number"] is JsonValue sequence && sequence.TryGetValue<long>(out var cursor))
            {
                if (attempt.StreamCursor is { } previous && cursor <= previous) throw Invalid("Responses stream events are out of order.");
                attempt.StreamCursor = cursor;
            }
            if (evt["response"] is JsonObject response)
            {
                await IdentityAsync(response, attempt, checkpoint);
                if (type is "response.completed" or "response.incomplete" or "response.failed")
                {
                    var status = Text(response["status"]);
                    if (status is not ("completed" or "incomplete" or "failed") || type != "response." + status)
                        throw Invalid("Responses terminal status does not match its event.");
                    if (status != "failed" && response["output"] is not JsonArray) throw Invalid("The terminal response has no complete output.");
                    attempt.TerminationStatus = status;
                    attempt.UsageKnown = KnownUsage(response["usage"], "input_tokens", "output_tokens");
                    return (JsonObject)response.DeepClone();
                }
            }
        }
        throw new IOException("Responses stream ended without a terminal response event.");
    }

    private static async Task<JsonObject> MessagesAsync(Stream stream, LLMHttpAttempt attempt, Func<Task> checkpoint, CancellationToken ct)
    {
        var fragments = new Fragments();
        JsonObject? message = null;
        var blocks = new SortedDictionary<int, JsonObject>();
        var active = new HashSet<int>();
        var arguments = new Dictionary<int, string>();
        var stopped = false;
        var ended = false;
        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            var evt = Object(data);
            var type = Text(evt["type"]);
            if (type == "ping") continue;
            if (type == "error") throw ReportedError(evt["error"] ?? evt, attempt);
            if (type == "message_start")
            {
                if (message is not null || evt["message"] is not JsonObject start) throw Invalid("Invalid or duplicate Anthropic message_start.");
                message = (JsonObject)start.DeepClone();
                if (message["usage"] is JsonObject initialUsage)
                {
                    message["stream_initial_usage"] = initialUsage.DeepClone();
                    initialUsage.Remove("output_tokens"); // The initial zero is not final generated usage.
                }
                await IdentityAsync(message, attempt, checkpoint);
                continue;
            }
            if (message is null) throw Invalid("Anthropic stream is missing message_start.");
            switch (type)
            {
                case "content_block_start":
                {
                    var index = Index(evt["index"]);
                    if (ended || blocks.ContainsKey(index) || evt["content_block"] is not JsonObject block) throw Invalid("Invalid Anthropic content block.");
                    blocks.Add(index, (JsonObject)block.DeepClone()); active.Add(index);
                    if (Text(block["type"]) is "text" or "tool_use")
                        await ObserveOutputAsync(attempt, checkpoint, block["text"], block["name"]);
                    break;
                }
                case "content_block_delta":
                {
                    var index = Index(evt["index"]);
                    if (!active.Contains(index) || evt["delta"] is not JsonObject delta) throw Invalid("Anthropic delta has no active block.");
                    var block = blocks[index];
                    if (Text(delta["type"]) is "text_delta" or "refusal_delta" or "input_json_delta")
                        await ObserveOutputAsync(attempt, checkpoint, delta["text"], delta["refusal"], delta["partial_json"]);
                    switch (Text(delta["type"]))
                    {
                        case "text_delta": fragments.Append(block, "text", delta["text"]); break;
                        case "thinking_delta": fragments.Append(block, "thinking", delta["thinking"]); break;
                        case "signature_delta": fragments.Append(block, "signature", delta["signature"]); break;
                        case "input_json_delta": arguments[index] = arguments.GetValueOrDefault(index, "") + (Text(delta["partial_json"]) ?? throw Invalid("Invalid tool JSON delta.")); break;
                        case "citations_delta":
                            if (block["citations"] is not JsonArray) block["citations"] = new JsonArray();
                            block["citations"]!.AsArray().Add(delta["citation"]?.DeepClone()); break;
                        case "refusal_delta": fragments.Append(block, "refusal", delta["refusal"]); break;
                        default: throw Invalid("Unsupported Anthropic content delta; no partial message can be published.");
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var index = Index(evt["index"]);
                    if (!active.Remove(index)) throw Invalid("Anthropic stopped an unknown block.");
                    if (arguments.TryGetValue(index, out var json)) blocks[index]["input"] = JsonNode.Parse(json) as JsonObject ?? throw Invalid("Incomplete tool JSON.");
                    break;
                }
                case "message_delta":
                    if (active.Count > 0) throw Invalid("Anthropic ended with active content blocks.");
                    if (evt["delta"] is JsonObject top) Merge(message, top);
                    if (evt["usage"] is JsonObject updates)
                    {
                        if (message["usage"] is not JsonObject) message["usage"] = new JsonObject();
                        Merge(message["usage"]!.AsObject(), updates);
                    }
                    ended |= message["stop_reason"] is not null;
                    attempt.TerminationStatus = Text(message["stop_reason"]);
                    attempt.UsageKnown = ended && KnownUsage(message["usage"], "input_tokens", "output_tokens");
                    break;
                case "message_stop": stopped = true; break;
                default: break; // Future non-content events cannot waive the required terminal state.
            }
            if (stopped) break;
        }
        if (message is null || !stopped || !ended || active.Count > 0) throw new IOException("Anthropic stream ended before message_stop and its final stop reason.");
        fragments.Flush();
        message["content"] = new JsonArray(blocks.Values.Select(b => (JsonNode?)b).ToArray());
        attempt.TerminationStatus = Text(message["stop_reason"]);
        attempt.UsageKnown = KnownUsage(message["usage"], "input_tokens", "output_tokens");
        return message;
    }

    private static async Task<JsonObject> OllamaAsync(Stream stream, LLMHttpAttempt attempt, Func<Task> checkpoint, CancellationToken ct)
    {
        var fragments = new Fragments();
        var message = new JsonObject { ["role"] = "assistant", ["content"] = "" };
        var tools = new JsonArray();
        await foreach (var line in SseParser.ReadLinesAsync(stream, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var chunk = Object(line);
            if (chunk["error"] is { } error) throw ReportedError(error, attempt);
            if (chunk["message"] is not JsonObject delta) throw Invalid("Ollama returned an invalid native message.");
            fragments.Append(message, "content", delta["content"]);
            fragments.Append(message, "thinking", delta["thinking"]);
            await ObserveOutputAsync(attempt, checkpoint, delta["content"]);
            if (delta["tool_calls"] is JsonArray calls)
                foreach (var call in calls)
                {
                    tools.Add(call?.DeepClone());
                    await ObserveOutputAsync(attempt, checkpoint, call?["function"]?["name"]);
                }
            if (chunk["done"]?.GetValue<bool>() != true) continue;
            fragments.Flush();
            if (tools.Count > 0) message["tool_calls"] = tools;
            chunk["message"] = message;
            attempt.TerminationStatus = Text(chunk["done_reason"]);
            attempt.UsageKnown = KnownUsage(chunk, "prompt_eval_count", "eval_count");
            return chunk;
        }
        throw new IOException("Ollama stream ended before done:true.");
    }
}
