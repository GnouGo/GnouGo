using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

/// <summary>
/// Repository for telemetry data using Entity Framework Core.
/// </summary>
public sealed class EfTelemetryStore
{
    private readonly TelemetryDbContext _db;
    private readonly ILogger<EfTelemetryStore> _logger;

    public EfTelemetryStore(TelemetryDbContext db, ILogger<EfTelemetryStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the database (creates tables if needed).
    /// In DevMode, recreates the DB for a clean schema.
    /// </summary>
    public async Task InitializeAsync(bool devMode = false)
    {
        try
        {
            if (devMode)
            {
                _logger.LogWarning("[DevMode] Database dropped and will be recreated with current schema.");
                await _db.Database.EnsureDeletedAsync();
            }

            await _db.Database.EnsureCreatedAsync();
            _logger.LogInformation("Database initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }

    #region Tenant Management

    public async Task<TenantEntity?> GetTenantAsync(Guid tenantId)
    {
        return await TelemetryQueries.GetTenantById(_db, tenantId);
    }

    public async Task<TenantEntity> CreateTenantAsync(Guid tenantId, string name, int retentionMinutes)
    {
        var tenant = new TenantEntity
        {
            Id = tenantId,
            Name = name,
            RetentionMinutes = retentionMinutes,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created tenant {TenantId} with name {Name}", tenantId, name);
        return tenant;
    }

    public async Task<List<TenantEntity>> GetAllTenantsAsync()
    {
        var tenants = new List<TenantEntity>();
        await foreach (var t in TelemetryQueries.GetAllTenants(_db))
            tenants.Add(t);
        return tenants
            .OrderByDescending(t => t.CreatedUtc)
            .ToList();
    }

    public async Task DeleteTenantAsync(Guid tenantId)
    {
        await _db.SpanRecords.Where(s => s.TenantId == tenantId).ExecuteDeleteAsync();
        await _db.LogRecords.Where(l => l.TenantId == tenantId).ExecuteDeleteAsync();
        await _db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync();

        _logger.LogInformation("Deleted tenant {TenantId} and all associated data", tenantId);
    }

    public async Task<int> PurgeTenantDataAsync(Guid? tenantId)
    {
        var spans = await _db.SpanRecords.Where(s => s.TenantId == tenantId).ExecuteDeleteAsync();
        var logs = await _db.LogRecords.Where(l => l.TenantId == tenantId).ExecuteDeleteAsync();
        var total = spans + logs;

        _logger.LogInformation("Purged {Total} records ({Spans} spans, {Logs} logs) for tenant {TenantId}", total, spans, logs, tenantId);
        return total;
    }

    #endregion

    #region Span Management

    public async Task AddSpansAsync(IEnumerable<SpanRecordEntity> spans)
    {
        _db.SpanRecords.AddRange(spans);
        await _db.SaveChangesAsync();
    }

    public async Task<List<TraceSummaryDto>> GetRecentTracesAsync(
        Guid? tenantId,
        int limit,
        string? serviceName = null,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        string? traceIdFilter = null,
        string? attributeContains = null,
        CancellationToken ct = default)
    {
        var requestedLimit = Math.Clamp(limit, 1, 500);
        var filteredSpans = _db.SpanRecords
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var normalizedServiceName = serviceName.Trim().ToLower();
            filteredSpans = filteredSpans.Where(s => s.ServiceName != null &&
                s.ServiceName.ToLower().Contains(normalizedServiceName));
        }

        if (startUtc.HasValue)
            filteredSpans = filteredSpans.Where(s => s.ReceivedUtc >= startUtc.Value);

        if (endUtc.HasValue)
            filteredSpans = filteredSpans.Where(s => s.ReceivedUtc <= endUtc.Value);

        var normalizedTraceIdFilter = traceIdFilter?.Trim();
        var exactTraceId = TryParseExactTraceId(normalizedTraceIdFilter);
        if (exactTraceId is not null)
            filteredSpans = filteredSpans.Where(s => s.TraceId == exactTraceId);

        if (!string.IsNullOrWhiteSpace(attributeContains))
        {
            var normalizedAttribute = attributeContains.Trim().ToLower();
            filteredSpans = filteredSpans.Where(s =>
                (s.AttributesJson != null && s.AttributesJson.ToLower().Contains(normalizedAttribute)) ||
                (s.ResourceJson != null && s.ResourceJson.ToLower().Contains(normalizedAttribute)));
        }

        var hasPartialTraceFilter = !string.IsNullOrWhiteSpace(normalizedTraceIdFilter) && exactTraceId is null;
        var databaseLimit = hasPartialTraceFilter
            ? Math.Clamp(requestedLimit * 20, requestedLimit, 5000)
            : requestedLimit;
        var groupedTraces = await filteredSpans
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                TraceId = g.Key,
                StartUnixNs = g.Min(s => s.StartUnixNs),
                EndUnixNs = g.Max(s => s.EndUnixNs),
                SpanCount = g.Count(),
                RootSpanName = g.Where(s => s.ParentSpanId == null).Select(s => s.Name).FirstOrDefault(),
                ServiceName = g.Select(s => s.ServiceName).FirstOrDefault()
            })
            .OrderByDescending(trace => trace.EndUnixNs)
            .Take(databaseLimit)
            .ToListAsync(ct);

        return groupedTraces
            .Select(trace => new
            {
                Trace = trace,
                TraceId = Convert.ToHexString(trace.TraceId).ToLowerInvariant()
            })
            .Where(trace => !hasPartialTraceFilter || trace.TraceId.Contains(normalizedTraceIdFilter!, StringComparison.OrdinalIgnoreCase))
            .Take(requestedLimit)
            .Select(trace => new TraceSummaryDto(
                TraceId: trace.TraceId,
                StartUtc: DateTimeOffset.FromUnixTimeMilliseconds(trace.Trace.StartUnixNs / 1_000_000),
                EndUtc: DateTimeOffset.FromUnixTimeMilliseconds(trace.Trace.EndUnixNs / 1_000_000),
                SpanCount: trace.Trace.SpanCount,
                RootSpanName: trace.Trace.RootSpanName,
                ServicesCsv: null,
                ServiceName: trace.Trace.ServiceName ?? "unknown-service"))
            .ToList();
    }

    public async Task<List<SpanRecordEntity>> GetTraceSpansAsync(
        Guid? tenantId,
        byte[] traceId,
        CancellationToken ct = default)
    {
        var spans = new List<SpanRecordEntity>();
        await foreach (var s in TelemetryQueries.GetSpansByTenantAndTrace(_db, tenantId, traceId).WithCancellation(ct))
            spans.Add(s);
        return spans.OrderBy(s => s.StartUnixNs).ToList();
    }

    public async Task<List<SpanRecordEntity>> GetSpansByAttributeAsync(
        Guid? tenantId,
        string attributeKey,
        string attributeValue,
        int limit,
        CancellationToken ct = default)
    {
        var requestedLimit = Math.Clamp(limit, 1, 500);
        var normalizedValue = attributeValue.Trim().ToLower();
        var candidates = await _db.SpanRecords
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId &&
                s.AttributesJson != null &&
                s.AttributesJson.ToLower().Contains(normalizedValue))
            .OrderByDescending(s => s.EndUnixNs)
            .Take(Math.Clamp(requestedLimit * 20, requestedLimit, 5000))
            .ToListAsync(ct);

        return candidates
            .Where(s =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(s.AttributesJson)) return false;
                    using var attributes = JsonDocument.Parse(s.AttributesJson);
                    if (!attributes.RootElement.TryGetProperty(attributeKey, out var value)) return false;
                    var valueStr = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString() ?? string.Empty,
                        JsonValueKind.Null => string.Empty,
                        _ => value.ToString()
                    };
                    return valueStr.Equals(attributeValue, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            })
            .OrderByDescending(s => s.ReceivedUtc)
            .Take(requestedLimit)
            .ToList();
    }

    public async Task<List<string>> FindTraceIdsByCorrelationAsync(
        Guid? tenantId,
        string correlationId,
        string? serviceName,
        int limit,
        CancellationToken ct = default)
    {
        var requestedLimit = Math.Clamp(limit, 1, 500);
        var normalizedCorrelationId = correlationId.Trim().ToLower();
        var query = _db.SpanRecords
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId &&
                ((s.AttributesJson != null && s.AttributesJson.ToLower().Contains(normalizedCorrelationId)) ||
                 (s.ResourceJson != null && s.ResourceJson.ToLower().Contains(normalizedCorrelationId))));

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var normalizedServiceName = serviceName.Trim().ToLower();
            query = query.Where(s => s.ServiceName != null &&
                s.ServiceName.ToLower().Contains(normalizedServiceName));
        }

        var traceIds = await query
            .GroupBy(s => s.TraceId)
            .Select(group => new
            {
                TraceId = group.Key,
                EndUnixNs = group.Max(span => span.EndUnixNs)
            })
            .OrderByDescending(group => group.EndUnixNs)
            .Take(requestedLimit)
            .Select(group => group.TraceId)
            .ToListAsync(ct);

        return traceIds
            .Select(traceId => Convert.ToHexString(traceId).ToLowerInvariant())
            .ToList();
    }

    #endregion

    #region Log Management

    public async Task AddLogsAsync(IEnumerable<LogRecordEntity> logs)
    {
        _db.LogRecords.AddRange(logs);
        await _db.SaveChangesAsync();
    }

    public async Task<List<LogRecordEntity>> GetRecentLogsAsync(
        Guid? tenantId,
        int limit,
        string? serviceName = null,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        int[]? severityLevels = null,
        string? traceIdFilter = null,
        string? attributeContains = null,
        CancellationToken ct = default)
    {
        var requestedLimit = Math.Clamp(limit, 1, 5000);
        var filtered = _db.LogRecords
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var normalizedServiceName = serviceName.Trim().ToLower();
            filtered = filtered.Where(l => l.ServiceName != null &&
                l.ServiceName.ToLower().Contains(normalizedServiceName));
        }

        if (startUtc.HasValue)
            filtered = filtered.Where(l => l.ReceivedUtc >= startUtc.Value);

        if (endUtc.HasValue)
            filtered = filtered.Where(l => l.ReceivedUtc <= endUtc.Value);

        if (severityLevels != null && severityLevels.Length > 0)
            filtered = filtered.Where(l => severityLevels.Contains(l.SeverityNumber));

        var normalizedTraceIdFilter = traceIdFilter?.Trim();
        var exactTraceId = TryParseExactTraceId(normalizedTraceIdFilter);
        if (exactTraceId is not null)
            filtered = filtered.Where(l => l.TraceId != null && l.TraceId == exactTraceId);

        if (!string.IsNullOrWhiteSpace(attributeContains))
        {
            var normalizedAttribute = attributeContains.Trim().ToLower();
            filtered = filtered.Where(l =>
                (l.AttributesJson != null && l.AttributesJson.ToLower().Contains(normalizedAttribute)) ||
                (l.ResourceJson != null && l.ResourceJson.ToLower().Contains(normalizedAttribute)) ||
                (l.Body != null && l.Body.ToLower().Contains(normalizedAttribute)));
        }

        var hasPartialTraceFilter = !string.IsNullOrWhiteSpace(normalizedTraceIdFilter) && exactTraceId is null;
        var databaseLimit = hasPartialTraceFilter
            ? Math.Clamp(requestedLimit * 20, requestedLimit, 10000)
            : requestedLimit;
        var logs = await filtered
            // OTLP ingestion normalizes this value to UTC. SQLite stores DateTimeOffset as
            // an ISO-8601 scalar and cannot translate ordering on the CLR type itself;
            // ordering its canonical text keeps the bounded query server-side and chronological.
            .OrderByDescending(l => l.ReceivedUtc.ToString())
            .Take(databaseLimit)
            .ToListAsync(ct);

        return logs
            .Where(log => !hasPartialTraceFilter ||
                (log.TraceId is not null && Convert.ToHexString(log.TraceId).Contains(normalizedTraceIdFilter!, StringComparison.OrdinalIgnoreCase)))
            .Take(requestedLimit)
            .ToList();
    }

    public async Task<List<LogRecordEntity>> GetLogsForTraceAsync(
        Guid? tenantId,
        byte[] traceId,
        CancellationToken ct = default)
    {
        var logs = new List<LogRecordEntity>();
        await foreach (var l in TelemetryQueries.GetLogsByTenantAndTrace(_db, tenantId, traceId).WithCancellation(ct))
            logs.Add(l);
        return logs.OrderBy(l => l.ReceivedUtc).ToList();
    }

    #endregion

    #region Retention

    public async Task<int> DeleteOldSpansAsync(Guid? tenantId, DateTimeOffset cutoffTime)
    {
        return await _db.SpanRecords
            .Where(s => s.TenantId == tenantId && s.ReceivedUtc < cutoffTime)
            .ExecuteDeleteAsync();
    }

    public async Task<int> DeleteOldLogsAsync(Guid? tenantId, DateTimeOffset cutoffTime)
    {
        return await _db.LogRecords
            .Where(l => l.TenantId == tenantId && l.ReceivedUtc < cutoffTime)
            .ExecuteDeleteAsync();
    }

    #endregion

    private static byte[]? TryParseExactTraceId(string? traceId)
    {
        if (traceId is null || traceId.Length != 32)
            return null;

        try
        {
            return Convert.FromHexString(traceId);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
