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
        await using var lines = GnOuGo.AI.Core.SseParser.ReadLinesAsync(stream, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool next;
            try { next = await lines.MoveNextAsync(); }
            catch (InvalidDataException) { throw Invalid("Upstream stream frame is invalid or oversized."); }
            if (!next) yield break;
            yield return lines.Current;
        }
    }

    public static async IAsyncEnumerable<string> Sse(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var events = GnOuGo.AI.Core.SseParser.ReadDataAsync(stream, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool next;
            try { next = await events.MoveNextAsync(); }
            catch (InvalidDataException) { throw Invalid("Upstream SSE is truncated or oversized."); }
            if (!next) yield break;
            yield return events.Current;
        }
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
