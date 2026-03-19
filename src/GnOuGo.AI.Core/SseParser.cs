using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GnOuGo.AI.Core;

/// <summary>
/// Parses Server-Sent Events (SSE) streams from AI APIs, yielding text deltas.
/// </summary>
public static class SseParser
{
    /// <summary>
    /// Reads an SSE stream and yields raw "data:" payloads as <see cref="JsonDocument"/>.
    /// Stops on "[DONE]" sentinel or end of stream.
    /// Caller is responsible for disposing each <see cref="JsonDocument"/>.
    /// </summary>
    public static async IAsyncEnumerable<JsonDocument> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;

            if (data == "[DONE]")
                break;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                // Skip malformed events
                continue;
            }

            yield return doc;
        }
    }
}

