namespace OtlpTenantCollector.Models;

public sealed record Tenant(
    Guid Id,
    string Name,
    int RetentionMinutes,
    DateTimeOffset CreatedUtc
);
