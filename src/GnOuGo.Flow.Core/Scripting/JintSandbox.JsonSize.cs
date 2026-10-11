using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Scripting;

public sealed partial class JintSandbox
{
    // Count the default JSON wire representation, without constructing a second
    // copy of a large observation. Keep exact numeric lexemes and JSON escaping.
    internal static long JsonBytes(JsonNode? value, CancellationToken ct = default, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        switch (value)
        {
            case null: return 4;
            case JsonObject obj:
            {
                if (depth >= 64) throw new InvalidOperationException("JSON depth exceeds the default writer limit.");
                long count = 2;
                foreach (var pair in obj) count += StringBytes(pair.Key, ct) + 1 + JsonBytes(pair.Value, ct, depth + 1) + 1;
                return obj.Count == 0 ? count : count - 1;
            }
            case JsonArray array:
            {
                if (depth >= 64) throw new InvalidOperationException("JSON depth exceeds the default writer limit.");
                long count = 2;
                foreach (var item in array) count += JsonBytes(item, ct, depth + 1) + 1;
                return array.Count == 0 ? count : count - 1;
            }
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                return StringBytes(text, ct);
            default:
                // Numeric/raw JsonElement values retain their original writers.
                // Unusual custom JsonValue writers stream instead of building a
                // complete UTF-16 JSON string just for accounting.
                using (var counter = new ByteCounter())
                {
                    using (var writer = new Utf8JsonWriter(counter)) value.WriteTo(writer);
                    return counter.Count;
                }
        }
    }

    private static long StringBytes(string text, CancellationToken ct)
    {
        long bytes = 2;
        ReadOnlySpan<char> remaining = text;
        Span<char> buffer = stackalloc char[512];
        while (!remaining.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            var status = JavaScriptEncoder.Default.Encode(remaining, buffer, out var consumed, out var written, isFinalBlock: true);
            bytes += Encoding.UTF8.GetByteCount(buffer[..written]);
            remaining = remaining[consumed..];
            if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall))
                throw new InvalidOperationException("Invalid JSON string encoding.");
        }
        return bytes;
    }

    private sealed class ByteCounter : Stream
    {
        internal long Count;
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
