using System.Threading.Channels;

namespace OtlpTenantCollector.Services;

/// <summary>
/// In-process event bus that notifies SSE subscribers whenever the BatchWriter flushes new telemetry.
/// Each subscriber gets its own Channel so slow readers don't block others.
/// </summary>
public sealed class TelemetryEventBus
{
    private readonly Lock _lock = new();
    private readonly List<Channel<FlushEvent>> _subscribers = [];

    public sealed record FlushEvent(int SpanCount, int LogCount, DateTimeOffset FlushedUtc);

    /// <summary>Called by TelemetryBatchWriter after every successful flush.</summary>
    public void NotifyFlushed(int spanCount, int logCount)
    {
        var evt = new FlushEvent(spanCount, logCount, DateTimeOffset.UtcNow);

        lock (_lock)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                // TryWrite — if the subscriber's buffer is full we just drop the event (back-pressure)
                if (!_subscribers[i].Writer.TryWrite(evt))
                {
                    // Still keep the subscriber; it will catch up on the next event
                }
            }
        }
    }

    /// <summary>
    /// Subscribe and return a ChannelReader that will receive flush events.
    /// The caller must call <see cref="Unsubscribe"/> when done.
    /// </summary>
    public Channel<FlushEvent> Subscribe()
    {
        var ch = Channel.CreateBounded<FlushEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            _subscribers.Add(ch);
        }

        return ch;
    }

    public void Unsubscribe(Channel<FlushEvent> channel)
    {
        lock (_lock)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }
}

