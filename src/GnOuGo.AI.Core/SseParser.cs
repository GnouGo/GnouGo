using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GnOuGo.AI.Core;

/// <summary>Bounded native SSE/NDJSON framing shared with the protocol proxy.</summary>
public static class SseParser
{
    public const int MaxFrameCharacters = 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), ct).AsTask().WaitAsync(ct).ConfigureAwait(false)) > 0)
            for (var i = 0; i < length; i++)
                if (buffer[i] == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                }
                else
                {
                    if (line.Length >= MaxFrameCharacters) throw new InvalidDataException("Native stream frame exceeds the size limit.");
                    line.Append(buffer[i]);
                }
        ct.ThrowIfCancellationRequested();
        if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
    }

    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var data = new StringBuilder();
        await foreach (var line in ReadLinesAsync(stream, ct).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(0, data.Length - 1); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsMemory(5);
                if (value.Length > 0 && value.Span[0] == ' ') value = value[1..];
                if (data.Length + value.Length > MaxFrameCharacters) throw new InvalidDataException("Native SSE event exceeds the size limit.");
                data.Append(value.Span).Append('\n');
            }
        }
        if (data.Length > 0) throw new InvalidDataException("Native SSE ended inside an event.");
    }

    /// <summary>Compatibility JSON reader. Malformed JSON is an error, never silently discarded.</summary>
    public static async IAsyncEnumerable<JsonDocument> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var data in ReadDataAsync(stream, ct).ConfigureAwait(false))
        {
            if (data == "[DONE]") yield break;
            yield return JsonDocument.Parse(data);
        }
    }
}
