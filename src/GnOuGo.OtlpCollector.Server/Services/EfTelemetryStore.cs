using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

/// <summary>
/// Repository pour gérer les données de télémétrie avec SQLite sans réflexion runtime.
/// </summary>
public sealed class EfTelemetryStore
{
    private readonly AppOptions _options;
    private readonly ILogger<EfTelemetryStore> _logger;

    public EfTelemetryStore(AppOptions options, ILogger<EfTelemetryStore> logger)
    {
        _options = options;
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

            await TelemetryDatabaseBootstrap.EnsureInitializedAsync(_options.DbPath, resetSchema: devMode);
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
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, retention_minutes, created_utc
            FROM tenants
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(tenantId));

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTenant(reader) : null;
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

        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tenants (id, name, retention_minutes, created_utc)
            VALUES ($id, $name, $retention_minutes, $created_utc);
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(tenant.Id));
        command.Parameters.AddWithValue("$name", tenant.Name);
        command.Parameters.AddWithValue("$retention_minutes", tenant.RetentionMinutes);
        command.Parameters.AddWithValue("$created_utc", FormatUtc(tenant.CreatedUtc));
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created tenant {TenantId} with name {Name}", tenantId, name);
        return tenant;
    }

    public async Task<List<TenantEntity>> GetAllTenantsAsync()
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, retention_minutes, created_utc
            FROM tenants
            ORDER BY created_utc DESC;
            """;

        var tenants = new List<TenantEntity>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tenants.Add(ReadTenant(reader));

        return tenants;
    }

    public async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteTenantDeleteAsync(connection, transaction, "DELETE FROM span_records WHERE tenant_id = $tenant_id;", tenantId, CancellationToken.None);
        await ExecuteTenantDeleteAsync(connection, transaction, "DELETE FROM log_records WHERE tenant_id = $tenant_id;", tenantId, CancellationToken.None);
        await ExecuteTenantDeleteAsync(connection, transaction, "DELETE FROM tenants WHERE id = $tenant_id;", tenantId, CancellationToken.None);
        await transaction.CommitAsync();
        
        _logger.LogInformation("Deleted tenant {TenantId} and all associated data", tenantId);
    }

    public async Task<int> PurgeTenantDataAsync(Guid? tenantId)
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync();
        var spans = await ExecuteNullableTenantDeleteAsync(connection, transaction, "span_records", tenantId, null, CancellationToken.None);
        var logs = await ExecuteNullableTenantDeleteAsync(connection, transaction, "log_records", tenantId, null, CancellationToken.None);
        await transaction.CommitAsync();
        var total = spans + logs;
        
        _logger.LogInformation("Purged {Total} records ({Spans} spans, {Logs} logs) for tenant {TenantId}", total, spans, logs, tenantId);
        return total;
    }

    #endregion

    #region Span Management

    public async Task AddSpansAsync(IEnumerable<SpanRecordEntity> spans)
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var span in spans)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO span_records (
                    id, tenant_id, received_utc, trace_id, span_id, parent_span_id, name, kind,
                    start_unix_ns, end_unix_ns, status_code, status_message, attributes_json,
                    events_json, resource_json, scope_json, service_name)
                VALUES (
                    $id, $tenant_id, $received_utc, $trace_id, $span_id, $parent_span_id, $name, $kind,
                    $start_unix_ns, $end_unix_ns, $status_code, $status_message, $attributes_json,
                    $events_json, $resource_json, $scope_json, $service_name);
                """;
            AddSpanParameters(command, span);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
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
        var spanGroups = await GetSpansByTenantAsync(tenantId, CancellationToken.None);

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
        var spans = await GetSpansByTenantAsync(tenantId, CancellationToken.None, traceId);
        
        // Trier en mémoire car SQLite ne supporte pas bien les ORDER BY complexes
        return spans.OrderBy(s => s.StartUnixNs).ToList();
    }

    public async Task<List<SpanRecordEntity>> GetSpansByAttributeAsync(Guid? tenantId, string attributeKey, string attributeValue, int limit)
    {
        var allSpans = await GetSpansByTenantAsync(tenantId, CancellationToken.None);

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
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var log in logs)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO log_records (
                    id, tenant_id, received_utc, trace_id, span_id, severity_number, severity_text,
                    body, attributes_json, resource_json, scope_json, service_name)
                VALUES (
                    $id, $tenant_id, $received_utc, $trace_id, $span_id, $severity_number, $severity_text,
                    $body, $attributes_json, $resource_json, $scope_json, $service_name);
                """;
            AddLogParameters(command, log);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
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
        var logs = await GetLogsByTenantAsync(tenantId, CancellationToken.None);

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
        var logs = await GetLogsByTenantAsync(tenantId, CancellationToken.None, traceId);

        return logs
            .OrderBy(l => l.ReceivedUtc)
            .ToList();
    }

    #endregion

    #region Retention

    public async Task<int> DeleteOldSpansAsync(Guid? tenantId, DateTimeOffset cutoffTime)
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        return await ExecuteNullableTenantDeleteAsync(connection, null, "span_records", tenantId, cutoffTime, CancellationToken.None);
    }

    public async Task<int> DeleteOldLogsAsync(Guid? tenantId, DateTimeOffset cutoffTime)
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        return await ExecuteNullableTenantDeleteAsync(connection, null, "log_records", tenantId, cutoffTime, CancellationToken.None);
    }

    #endregion

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.DbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection($"Data Source={_options.DbPath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<List<SpanRecordEntity>> GetSpansByTenantAsync(Guid? tenantId, CancellationToken ct, byte[]? traceId = null)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = traceId is null
            ? """
                SELECT id, tenant_id, received_utc, trace_id, span_id, parent_span_id, name, kind,
                       start_unix_ns, end_unix_ns, status_code, status_message, attributes_json,
                       events_json, resource_json, scope_json, service_name
                FROM span_records
                WHERE (($tenant_id IS NULL AND tenant_id IS NULL) OR tenant_id = $tenant_id);
                """
            : """
                SELECT id, tenant_id, received_utc, trace_id, span_id, parent_span_id, name, kind,
                       start_unix_ns, end_unix_ns, status_code, status_message, attributes_json,
                       events_json, resource_json, scope_json, service_name
                FROM span_records
                WHERE (($tenant_id IS NULL AND tenant_id IS NULL) OR tenant_id = $tenant_id)
                  AND trace_id = $trace_id;
                """;
        AddNullableGuidParameter(command, "$tenant_id", tenantId);
        if (traceId is not null)
            command.Parameters.AddWithValue("$trace_id", traceId);

        var spans = new List<SpanRecordEntity>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            spans.Add(ReadSpan(reader));

        return spans;
    }

    private async Task<List<LogRecordEntity>> GetLogsByTenantAsync(Guid? tenantId, CancellationToken ct, byte[]? traceId = null)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = traceId is null
            ? """
                SELECT id, tenant_id, received_utc, trace_id, span_id, severity_number, severity_text,
                       body, attributes_json, resource_json, scope_json, service_name
                FROM log_records
                WHERE (($tenant_id IS NULL AND tenant_id IS NULL) OR tenant_id = $tenant_id);
                """
            : """
                SELECT id, tenant_id, received_utc, trace_id, span_id, severity_number, severity_text,
                       body, attributes_json, resource_json, scope_json, service_name
                FROM log_records
                WHERE (($tenant_id IS NULL AND tenant_id IS NULL) OR tenant_id = $tenant_id)
                  AND trace_id = $trace_id;
                """;
        AddNullableGuidParameter(command, "$tenant_id", tenantId);
        if (traceId is not null)
            command.Parameters.AddWithValue("$trace_id", traceId);

        var logs = new List<LogRecordEntity>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            logs.Add(ReadLog(reader));

        return logs;
    }

    private static async Task ExecuteTenantDeleteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, Guid tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tenant_id", FormatGuid(tenantId));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ExecuteNullableTenantDeleteAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string tableName, Guid? tenantId, DateTimeOffset? cutoffTime, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null)
            command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"DELETE FROM {tableName} WHERE (($tenant_id IS NULL AND tenant_id IS NULL) OR tenant_id = $tenant_id)" +
                              (cutoffTime.HasValue ? " AND received_utc < $cutoff_utc" : string.Empty) +
                              ";";
        AddNullableGuidParameter(command, "$tenant_id", tenantId);
        if (cutoffTime.HasValue)
            command.Parameters.AddWithValue("$cutoff_utc", FormatUtc(cutoffTime.Value));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddSpanParameters(SqliteCommand command, SpanRecordEntity span)
    {
        command.Parameters.AddWithValue("$id", FormatGuid(span.Id));
        AddNullableGuidParameter(command, "$tenant_id", span.TenantId);
        command.Parameters.AddWithValue("$received_utc", FormatUtc(span.ReceivedUtc));
        command.Parameters.AddWithValue("$trace_id", span.TraceId);
        command.Parameters.AddWithValue("$span_id", span.SpanId);
        command.Parameters.AddWithValue("$parent_span_id", DbValue(span.ParentSpanId));
        command.Parameters.AddWithValue("$name", span.Name);
        command.Parameters.AddWithValue("$kind", span.Kind);
        command.Parameters.AddWithValue("$start_unix_ns", span.StartUnixNs);
        command.Parameters.AddWithValue("$end_unix_ns", span.EndUnixNs);
        command.Parameters.AddWithValue("$status_code", span.StatusCode);
        command.Parameters.AddWithValue("$status_message", DbValue(span.StatusMessage));
        command.Parameters.AddWithValue("$attributes_json", DbValue(span.AttributesJson));
        command.Parameters.AddWithValue("$events_json", DbValue(span.EventsJson));
        command.Parameters.AddWithValue("$resource_json", DbValue(span.ResourceJson));
        command.Parameters.AddWithValue("$scope_json", DbValue(span.ScopeJson));
        command.Parameters.AddWithValue("$service_name", DbValue(span.ServiceName));
    }

    private static void AddLogParameters(SqliteCommand command, LogRecordEntity log)
    {
        command.Parameters.AddWithValue("$id", FormatGuid(log.Id));
        AddNullableGuidParameter(command, "$tenant_id", log.TenantId);
        command.Parameters.AddWithValue("$received_utc", FormatUtc(log.ReceivedUtc));
        command.Parameters.AddWithValue("$trace_id", DbValue(log.TraceId));
        command.Parameters.AddWithValue("$span_id", DbValue(log.SpanId));
        command.Parameters.AddWithValue("$severity_number", log.SeverityNumber);
        command.Parameters.AddWithValue("$severity_text", DbValue(log.SeverityText));
        command.Parameters.AddWithValue("$body", DbValue(log.Body));
        command.Parameters.AddWithValue("$attributes_json", DbValue(log.AttributesJson));
        command.Parameters.AddWithValue("$resource_json", DbValue(log.ResourceJson));
        command.Parameters.AddWithValue("$scope_json", DbValue(log.ScopeJson));
        command.Parameters.AddWithValue("$service_name", DbValue(log.ServiceName));
    }

    private static void AddNullableGuidParameter(SqliteCommand command, string name, Guid? value)
        => command.Parameters.AddWithValue(name, value.HasValue ? FormatGuid(value.Value) : DBNull.Value);

    private static TenantEntity ReadTenant(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        RetentionMinutes = reader.GetInt32(2),
        CreatedUtc = ParseUtc(reader.GetString(3))
    };

    private static SpanRecordEntity ReadSpan(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TenantId = ReadNullableGuid(reader, 1),
        ReceivedUtc = ParseUtc(reader.GetString(2)),
        TraceId = (byte[])reader[3],
        SpanId = (byte[])reader[4],
        ParentSpanId = reader.IsDBNull(5) ? null : (byte[])reader[5],
        Name = reader.GetString(6),
        Kind = reader.GetInt32(7),
        StartUnixNs = reader.GetInt64(8),
        EndUnixNs = reader.GetInt64(9),
        StatusCode = reader.GetInt32(10),
        StatusMessage = ReadNullableString(reader, 11),
        AttributesJson = ReadNullableString(reader, 12),
        EventsJson = ReadNullableString(reader, 13),
        ResourceJson = ReadNullableString(reader, 14),
        ScopeJson = ReadNullableString(reader, 15),
        ServiceName = ReadNullableString(reader, 16)
    };

    private static LogRecordEntity ReadLog(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TenantId = ReadNullableGuid(reader, 1),
        ReceivedUtc = ParseUtc(reader.GetString(2)),
        TraceId = reader.IsDBNull(3) ? null : (byte[])reader[3],
        SpanId = reader.IsDBNull(4) ? null : (byte[])reader[4],
        SeverityNumber = reader.GetInt32(5),
        SeverityText = ReadNullableString(reader, 6),
        Body = ReadNullableString(reader, 7),
        AttributesJson = ReadNullableString(reader, 8),
        ResourceJson = ReadNullableString(reader, 9),
        ScopeJson = ReadNullableString(reader, 10),
        ServiceName = ReadNullableString(reader, 11)
    };

    private static string FormatGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static object DbValue(byte[]? value) => value is null ? DBNull.Value : value;
}
