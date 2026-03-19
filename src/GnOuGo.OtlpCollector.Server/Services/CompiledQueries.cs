using Microsoft.EntityFrameworkCore;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

/// <summary>
/// Requêtes EF Core précompilées pour de meilleures performances
/// </summary>
public static class CompiledQueries
{
    // ============================================================================
    // Tenant Queries
    // ============================================================================

    public static readonly Func<TelemetryDbContext, Guid, Task<TenantEntity?>> GetTenantByIdAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid tenantId) =>
            context.Tenants
                .AsNoTracking()
                .FirstOrDefault(t => t.Id == tenantId));

    public static readonly Func<TelemetryDbContext, IAsyncEnumerable<TenantEntity>> GetAllTenantsAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context) =>
            context.Tenants.AsNoTracking());

    // ============================================================================
    // Span Queries (TenantId nullable — null means "no tenant filter" in DevMode)
    // ============================================================================

    public static readonly Func<TelemetryDbContext, Guid?, IAsyncEnumerable<SpanRecordEntity>> GetSpansByTenantAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid? tenantId) =>
            context.Spans
                .AsNoTracking()
                .Where(s => tenantId == null ? s.TenantId == null : s.TenantId == tenantId));

    public static readonly Func<TelemetryDbContext, Guid?, byte[], IAsyncEnumerable<SpanRecordEntity>> GetTraceSpansAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid? tenantId, byte[] traceId) =>
            context.Spans
                .AsNoTracking()
                .Where(s => (tenantId == null ? s.TenantId == null : s.TenantId == tenantId) && s.TraceId == traceId));

    public static readonly Func<TelemetryDbContext, Guid?, IAsyncEnumerable<SpanRecordEntity>> GetSpansForDeletionAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid? tenantId) =>
            context.Spans
                .Where(s => tenantId == null ? s.TenantId == null : s.TenantId == tenantId));

    // ============================================================================
    // Log Queries (TenantId nullable)
    // ============================================================================

    public static readonly Func<TelemetryDbContext, Guid?, IAsyncEnumerable<LogRecordEntity>> GetLogsByTenantAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid? tenantId) =>
            context.Logs
                .AsNoTracking()
                .Where(l => tenantId == null ? l.TenantId == null : l.TenantId == tenantId));

    public static readonly Func<TelemetryDbContext, Guid?, byte[], IAsyncEnumerable<LogRecordEntity>> GetLogsForTraceAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid? tenantId, byte[] traceId) =>
            context.Logs
                .AsNoTracking()
                .Where(l => (tenantId == null ? l.TenantId == null : l.TenantId == tenantId) && l.TraceId == traceId));

    public static readonly Func<TelemetryDbContext, Guid?, IAsyncEnumerable<LogRecordEntity>> GetLogsForDeletionAsync =
        EF.CompileAsyncQuery((TelemetryDbContext context, Guid? tenantId) =>
            context.Logs
                .Where(l => tenantId == null ? l.TenantId == null : l.TenantId == tenantId));
}
