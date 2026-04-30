using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services.Routing;

public interface ITelemetryRouter
{
    Task RouteAsync(IReadOnlyList<SpanRow> spans, IReadOnlyList<LogRow> logs, CancellationToken ct);
    Task FlushExpiredAsync(CancellationToken ct);
    Task FlushAllAsync(CancellationToken ct);
}

