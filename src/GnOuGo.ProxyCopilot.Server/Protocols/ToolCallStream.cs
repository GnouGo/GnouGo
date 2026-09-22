using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

/// <summary>
/// Keeps each function's argument fragments contiguous on the client wire.
/// Copilot Chat 0.66 associates ID-less deltas with the last call, ignoring index.
/// Repeating IDs would corrupt clients that concatenate them, so overlapping calls
/// wait for the active call's JSON object to complete. No tool is executed here.
/// </summary>
public sealed class ToolCallStream
{
    public const int MaxCalls = 128;
    private readonly Dictionary<int, Call> _byIndex = [];
    private readonly List<Call> _calls = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private int _active;
    private int _characters;
    private bool _finished;

    public IEnumerable<JsonObject> Process(JsonObject chunk)
    {
        if (chunk["choices"] is not JsonArray choices) throw Invalid();
        if (choices.Count == 0) { yield return chunk; yield break; }
        if (_finished || choices.Count != 1 || choices[0] is not JsonObject choice
            || choice["delta"] is not JsonObject delta) throw Invalid();
        var finish = ChatContract.OptionalText(choice["finish_reason"]);
        if (delta["tool_calls"] is { } tools)
        {
            if (tools is not JsonArray fragments) throw Invalid();
            foreach (var fragment in fragments) Add(fragment);
        }

        var remaining = (JsonObject)chunk.DeepClone();
        var remainingChoice = remaining["choices"]![0]!.AsObject();
        remainingChoice["delta"]!.AsObject().Remove("tool_calls");
        remainingChoice["finish_reason"] = null;
        // Text and role deltas keep flowing while overlapping tool calls wait.
        if (remainingChoice["delta"]!.AsObject().Count > 0)
        {
            remaining.Remove("usage");
            yield return remaining;
        }
        while (_active < _calls.Count)
        {
            var call = _calls[_active];
            if (call.Id is null || call.Name.Length == 0 || call.Arguments.Length == 0) break;
            if (call.Emitted < call.Arguments.Length)
            {
                var function = new JsonObject { ["arguments"] = call.Arguments.ToString(call.Emitted, call.Arguments.Length - call.Emitted) };
                var fragment = new JsonObject { ["index"] = call.Index, ["function"] = function };
                if (!call.Announced)
                {
                    fragment["id"] = call.Id;
                    fragment["type"] = "function";
                    function["name"] = call.Name.ToString();
                    call.Announced = true;
                }
                call.Emitted = call.Arguments.Length;
                var output = (JsonObject)chunk.DeepClone();
                output.Remove("usage");
                output["choices"] = new JsonArray(new JsonObject {
                    ["index"] = choice["index"]?.DeepClone() ?? JsonValue.Create(0),
                    ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(fragment) }, ["finish_reason"] = null
                });
                yield return output;
            }
            if (!call.Complete) break;
            call.Closed = true;
            _active++;
        }
        if (finish is not null)
        {
            if (_active != _calls.Count || finish == "tool_calls" && _calls.Count == 0) throw Invalid();
            _finished = true;
        }
        if (finish is not null || chunk["usage"] is not null || delta.Count == 0)
        {
            var tail = (JsonObject)chunk.DeepClone();
            tail["choices"]![0]!["delta"] = new JsonObject();
            yield return tail;
        }
    }

    private void Add(JsonNode? fragment)
    {
        if (fragment is not JsonObject || fragment["index"] is not JsonValue indexValue
            || !indexValue.TryGetValue<int>(out var index) || index < 0) throw Invalid();
        if (!_byIndex.TryGetValue(index, out var call))
        {
            if (_calls.Count >= MaxCalls) throw Invalid();
            call = new Call(index);
            _byIndex.Add(index, call);
            _calls.Add(call);
        }
        var id = ChatContract.OptionalText(fragment["id"]);
        var type = ChatContract.OptionalText(fragment["type"]);
        if (type is not null and not "function") throw Invalid();
        if (!string.IsNullOrEmpty(id))
        {
            if (call.Id is null)
            {
                if (!_ids.Add(id)) throw Invalid();
                call.Id = id;
                Count(id.Length);
            }
            else if (call.Id != id) throw Invalid();
        }
        if (fragment["function"] is null) return;
        if (fragment["function"] is not JsonObject function) throw Invalid();
        var name = ChatContract.OptionalText(function["name"]);
        if (!string.IsNullOrEmpty(name))
        {
            if (call.Announced) throw Invalid();
            Count(name.Length);
            call.Name.Append(name);
        }
        var arguments = ChatContract.OptionalText(function["arguments"]);
        if (string.IsNullOrEmpty(arguments)) return;
        Count(arguments.Length);
        // Whitespace arriving after a closed JSON object cannot change the tool.
        if (call.Closed)
        {
            if (!string.IsNullOrWhiteSpace(arguments)) throw Invalid();
            return;
        }
        call.Append(arguments);
    }

    private void Count(int length)
    {
        _characters += length;
        if (_characters > WireReader.MaxFrameCharacters) throw Invalid();
    }

    private static ProxyException Invalid() => WireReader.Invalid("Upstream returned invalid, incomplete, or oversized streamed tool calls.");

    private sealed class Call(int index)
    {
        public int Index { get; } = index;
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
        public int Emitted { get; set; }
        public bool Announced { get; set; }
        public bool Closed { get; set; }
        public bool Complete { get; private set; }
        private int _depth;
        private bool _started;
        private bool _quoted;
        private bool _escaped;

        public void Append(string fragment)
        {
            Arguments.Append(fragment);
            foreach (var character in fragment)
            {
                if (Complete) { if (!char.IsWhiteSpace(character)) throw Invalid(); continue; }
                if (_quoted)
                {
                    if (_escaped) _escaped = false;
                    else if (character == '\\') _escaped = true;
                    else if (character == '"') _quoted = false;
                    continue;
                }
                if (!_started)
                {
                    if (char.IsWhiteSpace(character)) continue;
                    if (character != '{') throw Invalid();
                    _started = true;
                }
                if (character == '"') _quoted = true;
                else if (character is '{' or '[') _depth++;
                else if (character is '}' or ']')
                {
                    if (--_depth < 0) throw Invalid();
                    if (_depth == 0) Complete = true;
                }
            }
            if (Complete)
            {
                try { using var document = JsonDocument.Parse(Arguments.ToString()); }
                catch (JsonException) { throw Invalid(); }
            }
        }
    }
}
