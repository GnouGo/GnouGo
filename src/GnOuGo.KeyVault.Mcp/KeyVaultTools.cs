using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.KeyVault.Mcp;

/// <summary>
/// MCP tool definitions for the KeyVault secret manager.
/// Exposes tenant listing/creation, secret storage, secret metadata,
/// and direct access to the latest decrypted secret value.
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

    [McpServerTool(Name = "keyvault_set_secret"), Description("Creates or updates a secret. The value is encrypted at rest using RSA+AES-GCM. Returns metadata only.")]
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
            return KeyVaultResult.Ok(new KeyVaultSecretMetadataDto(
                result.Id,
                result.Key,
                result.Version,
                result.TenantId,
                result.CreatedAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_set_secret failed for key={Key}", key);
            throw;
        }
    }

    [McpServerTool(Name = "keyvault_list_secrets"), Description("Lists secret metadata without revealing values.")]
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

    [McpServerTool(Name = "keyvault_get_secret"), Description("Reads the latest version of a secret by its key and returns the decrypted value with metadata.")]
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

            return KeyVaultResult.Ok(result);
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
}

public sealed record KeyVaultResult(bool Success, object? Data, string? Error = null)
{
    public static KeyVaultResult Ok(object? data) => new(true, data);
    public static KeyVaultResult NotFound(string message) => new(false, null, message);
}

public sealed record KeyVaultSecretMetadataDto(
    Guid Id,
    string Key,
    int Version,
    Guid? TenantId,
    DateTimeOffset CreatedAt);

