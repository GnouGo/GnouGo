namespace GnOuGo.GithubCopilot.Core;

/// <summary>Keeps the physical attempt deadline active after HTTP response headers arrive.</summary>
internal sealed class CopilotInferenceStream(Stream body, HttpContent owner, CancellationTokenSource deadline, Action? failed,
    Func<HttpContent, CancellationToken, Task>? settle = null) : Stream
{
    // A complete, successful wire response is needed to replace its own reservation.
    // Oversized, interrupted or partially consumed streams keep their original charge.
    private const int MaxReceiptBytes = 2 * 1024 * 1024;
    private MemoryStream? _receipt = settle is null ? null : new MemoryStream();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var combined = ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token) : null;
        var token = combined?.Token ?? deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var count = await body.ReadAsync(buffer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_receipt is { } receipt && !buffer.IsEmpty)
            {
                if (receipt.Length + count > MaxReceiptBytes)
                {
                    _receipt = null;
                    receipt.Dispose();
                }
                else if (count > 0) receipt.Write(buffer.Span[..count]);
                else
                {
                    _receipt = null;
                    using (receipt)
                    using (var content = new ByteArrayContent(receipt.GetBuffer(), 0, checked((int)receipt.Length)))
                    {
                        content.Headers.ContentType = owner.Headers.ContentType;
                        await settle!(content, token).ConfigureAwait(false);
                    }
                }
            }
            if (count == 0 && !buffer.IsEmpty) deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            return count;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { failed?.Invoke(); throw; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _receipt?.Dispose();
            owner.Dispose();
            deadline.Dispose();
        }
        base.Dispose(disposing);
    }
}
