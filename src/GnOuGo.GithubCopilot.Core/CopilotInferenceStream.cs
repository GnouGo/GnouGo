namespace GnOuGo.GithubCopilot.Core;

/// <summary>Keeps the physical attempt deadline active after HTTP response headers arrive.</summary>
internal sealed class CopilotInferenceStream(Stream body, HttpContent owner, CancellationTokenSource deadline, Action? failed) : Stream
{
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
            owner.Dispose();
            deadline.Dispose();
        }
        base.Dispose(disposing);
    }
}
