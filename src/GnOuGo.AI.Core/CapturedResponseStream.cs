using System.Text;

namespace GnOuGo.AI.Core;

/// <summary>One attempt's bounded encrypted journal evidence; never an independent recovery store.</summary>
internal sealed class CapturedResponseStream(Stream inner, LLMHttpAttempt attempt, Func<CancellationToken, Task> save, Func<DateTimeOffset> utcNow) : Stream
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly MemoryStream _bytes = new();
    private long _savedLength;
    private DateTimeOffset? _savedAt;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count;
        try { count = await inner.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            attempt.ObserveReadTermination(LLMHttpReadTermination.FromException(error, utcNow()));
            throw;
        }
        if (count == 0)
        {
            attempt.ObserveReadTermination(new() { Observer = "response_stream", Outcome = "eof", AtUtc = utcNow() });
            return 0;
        }
        if (_bytes.Length + count > MaximumBytes) throw new InvalidDataException("Native stream exceeds the response size limit.");
        _bytes.Write(buffer.Span[..count]);
        var now = utcNow();
        attempt.FirstByteAtUtc ??= now;
        attempt.LastByteAtUtc = now;
        if (_savedAt is null || _bytes.Length - _savedLength >= 32 * 1024 || now - _savedAt >= TimeSpan.FromSeconds(1))
            await CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }
    internal void Snapshot()
    {
        if (_bytes.Length == 0) return;
        // Base64 retains exact bytes even if disconnection split an UTF-8 code point.
        attempt.PartialBodyBase64 = Convert.ToBase64String(_bytes.GetBuffer(), 0, (int)_bytes.Length);
        attempt.PartialBody = Encoding.UTF8.GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length);
    }
    internal async Task CheckpointAsync(CancellationToken ct)
    {
        Snapshot();
        await save(ct).ConfigureAwait(false);
        _savedAt = utcNow(); _savedLength = _bytes.Length;
    }
    internal string CompleteText() => new UTF8Encoding(false, true).GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
}
