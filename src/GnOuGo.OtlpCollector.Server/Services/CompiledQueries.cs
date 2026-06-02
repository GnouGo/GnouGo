using Microsoft.EntityFrameworkCore;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

/// <summary>
/// Precompiled EF Core queries for optimal startup performance.
/// </summary>
internal static class TelemetryQueries
{
    // ── Tenant ───────────────────────────────────────────────────────

    public static readonly Func<TelemetryDbContext, Guid, Task<TenantEntity?>> GetTenantById =
        EF.CompileAsyncQuery(
            (TelemetryDbContext db, Guid id) =>
                db.Tenants.FirstOrDefault(t => t.Id == id));

    public static readonly Func<TelemetryDbContext, IAsyncEnumerable<TenantEntity>> GetAllTenants =
        EF.CompileAsyncQuery(
            (TelemetryDbContext db) =>
                db.Tenants.AsQueryable());

    // ── Spans ────────────────────────────────────────────────────────

    public static readonly Func<TelemetryDbContext, Guid?, IAsyncEnumerable<SpanRecordEntity>> GetSpansByTenant =
        EF.CompileAsyncQuery(
            (TelemetryDbContext db, Guid? tenantId) =>
                db.SpanRecords.Where(s => s.TenantId == tenantId).AsQueryable());

    public static readonly Func<TelemetryDbContext, Guid?, byte[], IAsyncEnumerable<SpanRecordEntity>> GetSpansByTenantAndTrace =
        EF.CompileAsyncQuery(
            (TelemetryDbContext db, Guid? tenantId, byte[] traceId) =>
                db.SpanRecords.Where(s => s.TenantId == tenantId && s.TraceId == traceId).AsQueryable());

    // ── Logs ─────────────────────────────────────────────────────────

    public static readonly Func<TelemetryDbContext, Guid?, IAsyncEnumerable<LogRecordEntity>> GetLogsByTenant =
        EF.CompileAsyncQuery(
            (TelemetryDbContext db, Guid? tenantId) =>
                db.LogRecords.Where(l => l.TenantId == tenantId).AsQueryable());

    public static readonly Func<TelemetryDbContext, Guid?, byte[], IAsyncEnumerable<LogRecordEntity>> GetLogsByTenantAndTrace =
        EF.CompileAsyncQuery(
            (TelemetryDbContext db, Guid? tenantId, byte[] traceId) =>
                db.LogRecords.Where(l => l.TenantId == tenantId && l.TraceId == traceId).AsQueryable());
}
