using System.Text;

namespace GnOuGo.AI.Local;

internal static class LocalInferenceCompletion
{
    internal static async Task<(string Text, string Reason)> ReadAsync(IAsyncEnumerable<string> chunks,
        Func<string, string?> termination, Action? progress, CancellationToken ct)
    {
        var output = new StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (output.Length + chunk.Length > 16 * 1024 * 1024) throw new InvalidDataException("Local generation exceeds the response size limit.");
            output.Append(chunk);
            progress?.Invoke();
        }
        // Native iterator exhaustion alone cannot distinguish cancellation from completion.
        ct.ThrowIfCancellationRequested();
        var text = output.ToString();
        var reason = termination(text);
        if (reason is not ("stop" or "length")) throw new InvalidDataException("Local generation ended without a native termination condition.");
        return (text, reason);
    }
}
