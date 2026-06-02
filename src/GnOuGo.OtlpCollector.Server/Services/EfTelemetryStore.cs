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
        string? attributeContains = null)
    {
        var spanGroups = await GetSpansByTenantAsync(tenantId);

        var filteredSpans = spanGroups.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(serviceName))
            filteredSpans = filteredSpans.Where(s => s.ServiceName != null &&
                s.ServiceName.Contains(serviceName, StringComparison.OrdinalIgnoreCase));

        if (startUtc.HasValue)
            filteredSpans = filteredSpans.Where(s => s.ReceivedUtc >= startUtc.Value);

        if (endUtc.HasValue)
            filteredSpans = filteredSpans.Where(s => s.ReceivedUtc <= endUtc.Value);

        if (!string.IsNullOrWhiteSpace(traceIdFilter))
            filteredSpans = filteredSpans.Where(s =>
                Convert.ToHexString(s.TraceId).Contains(traceIdFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(attributeContains))
            filteredSpans = filteredSpans.Where(s =>
                (s.AttributesJson != null && s.AttributesJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)) ||
                (s.ResourceJson != null && s.ResourceJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)));

        var traces = filteredSpans
            .GroupBy(s => Convert.ToHexString(s.TraceId).ToLowerInvariant())
            .OrderByDescending(g => g.Max(s => s.ReceivedUtc))
            .Take(limit)
            .Select(g =>
            {
                var startUnixNs = g.Min(s => s.StartUnixNs);
                var endUnixNs = g.Max(s => s.EndUnixNs);
                var service = g.FirstOrDefault()?.ServiceName ?? "unknown-service";

                return new TraceSummaryDto(
                    TraceId: g.Key,
                    StartUtc: DateTimeOffset.FromUnixTimeMilliseconds(startUnixNs / 1_000_000),
                    EndUtc: DateTimeOffset.FromUnixTimeMilliseconds(endUnixNs / 1_000_000),
                    SpanCount: g.Count(),
                    RootSpanName: g.FirstOrDefault(s => s.ParentSpanId == null)?.Name,
                    ServicesCsv: null,
                    ServiceName: service
                );
            })
            .ToList();

        return traces;
    }

    public async Task<List<SpanRecordEntity>> GetTraceSpansAsync(Guid? tenantId, byte[] traceId)
    {
        var spans = new List<SpanRecordEntity>();
        await foreach (var s in TelemetryQueries.GetSpansByTenantAndTrace(_db, tenantId, traceId))
            spans.Add(s);
        return spans.OrderBy(s => s.StartUnixNs).ToList();
    }

    public async Task<List<SpanRecordEntity>> GetSpansByAttributeAsync(Guid? tenantId, string attributeKey, string attributeValue, int limit)
    {
        var allSpans = await GetSpansByTenantAsync(tenantId);

        return allSpans
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
            .Take(limit)
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
        string? attributeContains = null)
    {
        var logs = await GetLogsByTenantAsync(tenantId);

        var filtered = logs.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(serviceName))
            filtered = filtered.Where(l => l.ServiceName != null &&
                l.ServiceName.Contains(serviceName, StringComparison.OrdinalIgnoreCase));

        if (startUtc.HasValue)
            filtered = filtered.Where(l => l.ReceivedUtc >= startUtc.Value);

        if (endUtc.HasValue)
            filtered = filtered.Where(l => l.ReceivedUtc <= endUtc.Value);

        if (severityLevels != null && severityLevels.Length > 0)
            filtered = filtered.Where(l => severityLevels.Contains(l.SeverityNumber));

        if (!string.IsNullOrWhiteSpace(traceIdFilter))
            filtered = filtered.Where(l =>
                l.TraceId != null && Convert.ToHexString(l.TraceId).Contains(traceIdFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(attributeContains))
            filtered = filtered.Where(l =>
                (l.AttributesJson != null && l.AttributesJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)) ||
                (l.ResourceJson != null && l.ResourceJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)) ||
                (l.Body != null && l.Body.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)));

        return filtered
            .OrderByDescending(l => l.ReceivedUtc)
            .Take(limit)
            .ToList();
    }

    public async Task<List<LogRecordEntity>> GetLogsForTraceAsync(Guid? tenantId, byte[] traceId)
    {
        var logs = new List<LogRecordEntity>();
        await foreach (var l in TelemetryQueries.GetLogsByTenantAndTrace(_db, tenantId, traceId))
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

    private async Task<List<SpanRecordEntity>> GetSpansByTenantAsync(Guid? tenantId, byte[]? traceId = null)
    {
        if (traceId is not null)
        {
            var spans = new List<SpanRecordEntity>();
            await foreach (var s in TelemetryQueries.GetSpansByTenantAndTrace(_db, tenantId, traceId))
                spans.Add(s);
            return spans;
        }
        else
        {
            var spans = new List<SpanRecordEntity>();
            await foreach (var s in TelemetryQueries.GetSpansByTenant(_db, tenantId))
                spans.Add(s);
            return spans;
        }
    }

    private async Task<List<LogRecordEntity>> GetLogsByTenantAsync(Guid? tenantId, byte[]? traceId = null)
    {
        if (traceId is not null)
        {
            var logs = new List<LogRecordEntity>();
            await foreach (var l in TelemetryQueries.GetLogsByTenantAndTrace(_db, tenantId, traceId))
                logs.Add(l);
            return logs;
        }
        else
        {
            var logs = new List<LogRecordEntity>();
            await foreach (var l in TelemetryQueries.GetLogsByTenant(_db, tenantId))
                logs.Add(l);
            return logs;
        }
    }
}
