namespace GnOuGo.KeyVault.Core.Models;

// ── Request DTOs ─────────────────────────────────────────────────────

public sealed record CreateTenantRequest(string Name, string Author);

public sealed record SetSecretRequest(string Value, string Author, Guid? TenantId = null);

// ── Response DTOs ────────────────────────────────────────────────────

public sealed record TenantDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record SecretDto(
    Guid Id,
    string Key,
    Guid? TenantId,
    string? TenantName,
    int LatestVersion,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record SecretValueDto(
    Guid Id,
    string Key,
    string Value,
    int Version,
    Guid? TenantId,
    DateTimeOffset CreatedAt);

public sealed record SecretVersionDto(
    Guid Id,
    int Version,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record AuditEntryDto(
    Guid Id,
    Guid? TenantId,
    string? SecretKey,
    string Operation,
    string Author,
    DateTimeOffset Timestamp,
    string? Details);

