using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

public static class WireReader
{
    public const int MaxFrameCharacters = 1024 * 1024;

    public static async IAsyncEnumerable<string> Lines(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < length; i++)
            {
                if (buffer[i] == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                }
                else
                {
                    if (line.Length >= MaxFrameCharacters) throw Invalid("Upstream stream frame exceeds the size limit.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
    }

    public static async IAsyncEnumerable<string> Sse(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var data = new StringBuilder();
        await foreach (var line in Lines(stream, ct))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString().TrimEnd('\n'); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsMemory(5);
                if (value.Length > 0 && value.Span[0] == ' ') value = value[1..];
                if (data.Length + value.Length > MaxFrameCharacters) throw Invalid("Upstream SSE event exceeds the size limit.");
                data.Append(value.Span).Append('\n');
            }
        }
        // An unterminated event is a truncated transport, not a successful completion.
        if (data.Length > 0) throw Invalid("Upstream SSE ended inside an event.");
    }

    public static JsonObject Object(string json)
    {
        try { return JsonNode.Parse(json)?.AsObject() ?? throw Invalid("Empty upstream response."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw Invalid("Upstream returned malformed JSON."); }
    }

    public static async Task<JsonObject> ObjectAsync(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + count > 8 * 1024 * 1024) throw Invalid("Upstream response exceeds the size limit.");
            body.Write(buffer, 0, count);
        }
        return Object(Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
    }

    public static ProxyException Invalid(string message) => new(502, "invalid_upstream_response", message);
}
