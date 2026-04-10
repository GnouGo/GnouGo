using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

/// <summary>
/// Repository pour gérer les données de télémétrie avec Entity Framework Core
/// </summary>
public class EfTelemetryStore
{
    private readonly TelemetryDbContext _context;
    private readonly ILogger<EfTelemetryStore> _logger;

    public EfTelemetryStore(TelemetryDbContext context, ILogger<EfTelemetryStore> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Initialise la base de données (crée les tables si nécessaire).
    /// En DevMode, recrée la DB pour garantir un schéma propre.
    /// </summary>
    public async Task InitializeAsync(bool devMode = false)
    {
        try
        {
            if (devMode)
                _logger.LogWarning("[DevMode] Database dropped and will be recreated with current schema.");

            await TelemetryDatabaseBootstrap.EnsureInitializedAsync(_context, resetSchema: devMode);
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
        return await _context.Tenants.FindAsync(tenantId);
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

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created tenant {TenantId} with name {Name}", tenantId, name);
        return tenant;
    }

    public async Task<List<TenantEntity>> GetAllTenantsAsync()
    {
        return await _context.Tenants.ToListAsync();
    }

    public async Task DeleteTenantAsync(Guid tenantId)
    {
        // Supprimer toutes les données du tenant
        await _context.Spans.Where(s => s.TenantId == tenantId).ExecuteDeleteAsync();
        await _context.Logs.Where(l => l.TenantId == tenantId).ExecuteDeleteAsync();
        await _context.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync();
        
        _logger.LogInformation("Deleted tenant {TenantId} and all associated data", tenantId);
    }

    public async Task<int> PurgeTenantDataAsync(Guid? tenantId)
    {
        var spans = await _context.Spans.Where(s => tenantId == null ? s.TenantId == null : s.TenantId == tenantId).ExecuteDeleteAsync();
        var logs = await _context.Logs.Where(l => tenantId == null ? l.TenantId == null : l.TenantId == tenantId).ExecuteDeleteAsync();
        var total = spans + logs;
        
        _logger.LogInformation("Purged {Total} records ({Spans} spans, {Logs} logs) for tenant {TenantId}", total, spans, logs, tenantId);
        return total;
    }

    #endregion

    #region Span Management

    public async Task AddSpansAsync(IEnumerable<SpanRecordEntity> spans)
    {
        await _context.Spans.AddRangeAsync(spans);
        await _context.SaveChangesAsync();
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
        // Récupérer les spans du tenant avec requête précompilée
        var spanGroups = new List<SpanRecordEntity>();
        await foreach (var span in CompiledQueries.GetSpansByTenantAsync(_context, tenantId))
        {
            spanGroups.Add(span);
        }

        // Appliquer les filtres sur les spans avant le groupement
        var filteredSpans = spanGroups.AsEnumerable();
        
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            filteredSpans = filteredSpans.Where(s => s.ServiceName != null && 
                                                     s.ServiceName.Contains(serviceName, StringComparison.OrdinalIgnoreCase));
        }
        
        if (startUtc.HasValue)
        {
            filteredSpans = filteredSpans.Where(s => s.ReceivedUtc >= startUtc.Value);
        }
        
        if (endUtc.HasValue)
        {
            filteredSpans = filteredSpans.Where(s => s.ReceivedUtc <= endUtc.Value);
        }

        // Filter by traceId prefix (partial match)
        if (!string.IsNullOrWhiteSpace(traceIdFilter))
        {
            filteredSpans = filteredSpans.Where(s =>
                Convert.ToHexString(s.TraceId).Contains(traceIdFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by attribute value (searches in attributes, resource, scope JSON)
        if (!string.IsNullOrWhiteSpace(attributeContains))
        {
            filteredSpans = filteredSpans.Where(s =>
                (s.AttributesJson != null && s.AttributesJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)) ||
                (s.ResourceJson != null && s.ResourceJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)));
        }

        // Grouper par TraceId (convertir en string pour comparaison correcte), trier et limiter côté client
        var traces = filteredSpans
            .GroupBy(s => Convert.ToHexString(s.TraceId).ToLowerInvariant()) // Clé string pour groupement correct
            .OrderByDescending(g => g.Max(s => s.ReceivedUtc))
            .Take(limit)
            .Select(g =>
            {
                var startUnixNs = g.Min(s => s.StartUnixNs);
                var endUnixNs = g.Max(s => s.EndUnixNs);
                var durationMs = (endUnixNs - startUnixNs) / 1_000_000.0;
                var service = g.FirstOrDefault()?.ServiceName ?? "unknown-service";
                
                _logger.LogDebug("[EfTelemetryStore] TraceId={TraceId}, Service={ServiceName}: StartUnixNs={Start}, EndUnixNs={End}, DurationMs={Duration}",
                    g.Key.Substring(0, 16), service, startUnixNs, endUnixNs, durationMs);
                
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
        await foreach (var span in CompiledQueries.GetTraceSpansAsync(_context, tenantId, traceId))
        {
            spans.Add(span);
        }
        
        // Trier en mémoire car SQLite ne supporte pas bien les ORDER BY complexes
        return spans.OrderBy(s => s.StartUnixNs).ToList();
    }

    public async Task<List<SpanRecordEntity>> GetSpansByAttributeAsync(Guid? tenantId, string attributeKey, string attributeValue, int limit)
    {
        // Récupérer tous les spans du tenant avec requête précompilée
        var allSpans = new List<SpanRecordEntity>();
        await foreach (var span in CompiledQueries.GetSpansByTenantAsync(_context, tenantId))
        {
            allSpans.Add(span);
        }

        // Filtrer en mémoire par attribut (SQLite ne supporte pas JSON query)
        var matchingSpans = allSpans
            .Where(s =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(s.AttributesJson))
                        return false;

                    using var attributes = JsonDocument.Parse(s.AttributesJson);
                    if (!attributes.RootElement.TryGetProperty(attributeKey, out var value))
                        return false;

                    var valueStr = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString() ?? string.Empty,
                        JsonValueKind.Null => string.Empty,
                        _ => value.ToString()
                    };

                    return valueStr.Equals(attributeValue, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .OrderByDescending(s => s.ReceivedUtc)
            .Take(limit)
            .ToList();

        return matchingSpans;
    }

    #endregion

    #region Log Management

    public async Task AddLogsAsync(IEnumerable<LogRecordEntity> logs)
    {
        await _context.Logs.AddRangeAsync(logs);
        await _context.SaveChangesAsync();
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
        // SQLite ne supporte pas DateTimeOffset dans ORDER BY
        // On doit charger avec requête précompilée puis filtrer et trier en mémoire
        var logs = new List<LogRecordEntity>();
        await foreach (var log in CompiledQueries.GetLogsByTenantAsync(_context, tenantId))
        {
            logs.Add(log);
        }

        // Appliquer les filtres
        var filtered = logs.AsEnumerable();
        
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            filtered = filtered.Where(l => l.ServiceName != null && 
                                           l.ServiceName.Contains(serviceName, StringComparison.OrdinalIgnoreCase));
        }
        
        if (startUtc.HasValue)
        {
            filtered = filtered.Where(l => l.ReceivedUtc >= startUtc.Value);
        }
        
        if (endUtc.HasValue)
        {
            filtered = filtered.Where(l => l.ReceivedUtc <= endUtc.Value);
        }
        
        if (severityLevels != null && severityLevels.Length > 0)
        {
            filtered = filtered.Where(l => severityLevels.Contains(l.SeverityNumber));
        }

        // Filter by traceId prefix (partial match)
        if (!string.IsNullOrWhiteSpace(traceIdFilter))
        {
            filtered = filtered.Where(l =>
                l.TraceId != null && Convert.ToHexString(l.TraceId).Contains(traceIdFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by attribute value (searches in attributes, resource, body JSON)
        if (!string.IsNullOrWhiteSpace(attributeContains))
        {
            filtered = filtered.Where(l =>
                (l.AttributesJson != null && l.AttributesJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)) ||
                (l.ResourceJson != null && l.ResourceJson.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)) ||
                (l.Body != null && l.Body.Contains(attributeContains, StringComparison.OrdinalIgnoreCase)));
        }

        return filtered
            .OrderByDescending(l => l.ReceivedUtc)
            .Take(limit)
            .ToList();
    }

    public async Task<List<LogRecordEntity>> GetLogsForTraceAsync(Guid? tenantId, byte[] traceId)
    {
        // SQLite ne supporte pas DateTimeOffset dans ORDER BY
        // Utiliser requête précompilée puis trier en mémoire
        var logs = new List<LogRecordEntity>();
        await foreach (var log in CompiledQueries.GetLogsForTraceAsync(_context, tenantId, traceId))
        {
            logs.Add(log);
        }

        return logs
            .OrderBy(l => l.ReceivedUtc)
            .ToList();
    }

    #endregion

    #region Retention

    public async Task<int> DeleteOldSpansAsync(Guid? tenantId, DateTimeOffset cutoffTime)
    {
        // SQLite ne supporte pas ExecuteDeleteAsync avec DateTimeOffset
        // On doit charger les entités avec requête précompilée puis les supprimer
        var oldSpans = new List<SpanRecordEntity>();
        await foreach (var span in CompiledQueries.GetSpansForDeletionAsync(_context, tenantId))
        {
            oldSpans.Add(span);
        }

        // Filtrer en mémoire (LINQ to Objects)
        var spansToDelete = oldSpans.Where(s => s.ReceivedUtc < cutoffTime).ToList();

        if (spansToDelete.Count > 0)
        {
            _context.Spans.RemoveRange(spansToDelete);
            await _context.SaveChangesAsync();
        }

        return spansToDelete.Count;
    }

    public async Task<int> DeleteOldLogsAsync(Guid? tenantId, DateTimeOffset cutoffTime)
    {
        // SQLite ne supporte pas ExecuteDeleteAsync avec DateTimeOffset
        // On doit charger les entités avec requête précompilée puis les supprimer
        var oldLogs = new List<LogRecordEntity>();
        await foreach (var log in CompiledQueries.GetLogsForDeletionAsync(_context, tenantId))
        {
            oldLogs.Add(log);
        }

        // Filtrer en mémoire (LINQ to Objects)
        var logsToDelete = oldLogs.Where(l => l.ReceivedUtc < cutoffTime).ToList();

        if (logsToDelete.Count > 0)
        {
            _context.Logs.RemoveRange(logsToDelete);
            await _context.SaveChangesAsync();
        }

        return logsToDelete.Count;
    }

    #endregion
}
