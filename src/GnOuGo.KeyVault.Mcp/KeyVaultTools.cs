using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.KeyVault.Mcp;

/// <summary>
/// MCP tool definitions for the KeyVault secret manager.
/// Exposes tenant management, secret storage (write-only), secret metadata,
/// version history, and audit log. Secret values are never returned.
/// </summary>
[McpServerToolType]
public sealed class KeyVaultTools
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KeyVaultTools> _logger;

    public KeyVaultTools(IServiceScopeFactory scopeFactory, ILogger<KeyVaultTools> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Tenant tools ─────────────────────────────────────────────────

    [McpServerTool(Name = "keyvault_list_tenants"), Description("Lists all active tenants in the key vault.")]
    public async Task<KeyVaultResult> ListTenantsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var tenants = await svc.ListTenantsAsync(ct);
            return KeyVaultResult.Ok(tenants);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_list_tenants failed");
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_create_tenant"), Description("Creates a new tenant with a dedicated RSA key pair for secret encryption.")]
    public async Task<KeyVaultResult> CreateTenantAsync(
        [Description("Human-readable name for the tenant.")] string name,
        [Description("Author or user creating the tenant.")] string author,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var tenant = await svc.CreateTenantAsync(name, author, ct);
            return KeyVaultResult.Ok(tenant);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_create_tenant failed for name={Name}", name);
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_delete_tenant"), Description("Soft-deletes a tenant by its identifier.")]
    public async Task<KeyVaultResult> DeleteTenantAsync(
        [Description("Unique identifier of the tenant to delete.")] Guid tenantId,
        [Description("Author or user performing the deletion.")] string author,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var deleted = await svc.DeleteTenantAsync(tenantId, author, ct);
            return deleted
                ? KeyVaultResult.Ok("Tenant deleted.")
                : KeyVaultResult.NotFound("Tenant not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_delete_tenant failed for tenantId={TenantId}", tenantId);
            throw;
        }
    }

    // ── Secret tools ─────────────────────────────────────────────────

    [McpServerTool(Name = "keyvault_set_secret"), Description("Creates or updates a secret. The value is encrypted at rest using RSA+AES-GCM. Returns metadata only (the value is never echoed back).")]
    public async Task<KeyVaultResult> SetSecretAsync(
        [Description("Secret key (unique name within the tenant scope).")] string key,
        [Description("Plain-text value to encrypt and store.")] string value,
        [Description("Author or user setting the secret.")] string author,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var result = await svc.SetSecretAsync(key, value, tenantId, author, ct);
            // Return metadata only — never echo the secret value
            return KeyVaultResult.Ok(new
            {
                result.Id,
                result.Key,
                result.Version,
                result.TenantId,
                result.CreatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_set_secret failed for key={Key}", key);
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_list_secrets"), Description("Lists secret metadata (key, tenant, latest version) without revealing values.")]
    public async Task<KeyVaultResult> ListSecretsAsync(
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var secrets = await svc.ListSecretsAsync(tenantId, ct);
            return KeyVaultResult.Ok(secrets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_list_secrets failed");
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_get_secret"), Description("Reads a secret value by its key. Returns the decrypted value along with metadata. An audit entry is created for every read.")]
    public async Task<KeyVaultResult> GetSecretAsync(
        [Description("Secret key to read.")] string key,
        [Description("Author or user reading the secret.")] string author,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var result = await svc.GetSecretAsync(key, tenantId, author, ct);
            if (result is null)
                return KeyVaultResult.NotFound($"Secret '{key}' not found.");

            return KeyVaultResult.Ok(new
            {
                result.Id,
                result.Key,
                result.Value,
                result.Version,
                result.TenantId,
                result.CreatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_get_secret failed for key={Key}", key);
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_delete_secret"), Description("Soft-deletes a secret by its key.")]
    public async Task<KeyVaultResult> DeleteSecretAsync(
        [Description("Secret key to delete.")] string key,
        [Description("Author or user performing the deletion.")] string author,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var deleted = await svc.DeleteSecretAsync(key, tenantId, author, ct);
            return deleted
                ? KeyVaultResult.Ok("Secret deleted.")
                : KeyVaultResult.NotFound("Secret not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_delete_secret failed for key={Key}", key);
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_get_secret_versions"), Description("Returns the version history of a secret (version number, creation date, author) without revealing values.")]
    public async Task<KeyVaultResult> GetSecretVersionsAsync(
        [Description("Secret key.")] string key,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var versions = await svc.GetSecretVersionsAsync(key, tenantId, ct);
            return KeyVaultResult.Ok(versions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_get_secret_versions failed for key={Key}", key);
            throw;
        }
    }

    // ── Audit tool ───────────────────────────────────────────────────

    [McpServerTool(Name = "keyvault_get_audit_log"), Description("Returns the audit trail for the key vault. Supports filtering by tenant and secret key, with pagination.")]
    public async Task<KeyVaultResult> GetAuditLogAsync(
        [Description("Optional tenant identifier to filter by.")] Guid? tenantId = null,
        [Description("Optional secret key to filter by.")] string? key = null,
        [Description("Number of entries to skip (default 0).")] int skip = 0,
        [Description("Number of entries to return (default 50).")] int take = 50,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            var entries = await svc.GetAuditLogAsync(tenantId, key, skip, take, ct);
            return KeyVaultResult.Ok(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_get_audit_log failed");
            throw;
        }
    }
}

/// <summary>Simple wrapper returned by every MCP tool.</summary>
public sealed record KeyVaultResult(bool Success, object? Data, string? Error = null)
{
    public static KeyVaultResult Ok(object? data) => new(true, data);
    public static KeyVaultResult NotFound(string message) => new(false, null, message);
}

