using System.Threading.Channels;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

public sealed class TelemetryIngestQueue
{
    public Channel<ITelemetryRow> Channel { get; }

    public TelemetryIngestQueue(AppOptions opt)
    {
        Channel = System.Threading.Channels.Channel.CreateBounded<ITelemetryRow>(
            new BoundedChannelOptions(opt.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public ValueTask EnqueueAsync(ITelemetryRow row, CancellationToken ct) =>
        Channel.Writer.WriteAsync(row, ct);
}
