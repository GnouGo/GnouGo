using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using GnOuGo.KeyVault.Core.Models;
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
    private readonly KeyVaultService _service;
    private readonly ILogger<KeyVaultTools> _logger;

    public KeyVaultTools(KeyVaultService service, ILogger<KeyVaultTools> logger)
    {
        _service = service;
        _logger = logger;
    }

    [McpServerTool(Name = "keyvault_list_tenants", UseStructuredContent = true, OutputSchemaType = typeof(KeyVaultResult<List<TenantDto>>)), Description("Lists all active tenants in the key vault.")]
    public async Task<KeyVaultResult<List<TenantDto>>> ListTenantsAsync(CancellationToken ct = default)
    {
        try
        {
            var tenants = await _service.ListTenantsAsync(ct);
            return KeyVaultResult<List<TenantDto>>.FromData(tenants);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_list_tenants failed");
            return Fail<List<TenantDto>>(ex);
        }
    }

    [McpServerTool(Name = "keyvault_create_tenant", UseStructuredContent = true, OutputSchemaType = typeof(KeyVaultResult<TenantDto>)), Description("Creates a new tenant with a dedicated RSA key pair for secret encryption.")]
    public async Task<KeyVaultResult<TenantDto>> CreateTenantAsync(
        [Description("Human-readable name for the tenant.")] string name,
        [Description("Author or user creating the tenant.")] string author,
        CancellationToken ct = default)
    {
        try
        {
            var tenant = await _service.CreateTenantAsync(name, author, ct);
            return KeyVaultResult<TenantDto>.FromData(tenant);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_create_tenant failed for name={Name}", name);
            return Fail<TenantDto>(ex);
        }
    }

    [McpServerTool(Name = "keyvault_set_secret", UseStructuredContent = true, OutputSchemaType = typeof(KeyVaultResult<KeyVaultSecretMetadataResult>)), Description("Creates or updates a secret. The value is encrypted at rest using RSA+AES-GCM. Returns metadata only.")]
    public async Task<KeyVaultResult<KeyVaultSecretMetadataResult>> SetSecretAsync(
        [Description("Secret key (unique name within the tenant scope).")] string key,
        [Description("Plain-text value to encrypt and store.")] string value,
        [Description("Author or user setting the secret.")] string author,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.SetSecretAsync(key, value, tenantId, author, ct);
            var metadata = new KeyVaultSecretMetadataResult(result.Id, result.Key, result.Version, result.TenantId, result.CreatedAt);
            return KeyVaultResult<KeyVaultSecretMetadataResult>.FromData(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_set_secret failed for key={Key}", key);
            return Fail<KeyVaultSecretMetadataResult>(ex);
        }
    }

    [McpServerTool(Name = "keyvault_list_secrets", UseStructuredContent = true, OutputSchemaType = typeof(KeyVaultResult<List<SecretDto>>)), Description("Lists secret metadata without revealing values.")]
    public async Task<KeyVaultResult<List<SecretDto>>> ListSecretsAsync(
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            var secrets = await _service.ListSecretsAsync(tenantId, ct);
            return KeyVaultResult<List<SecretDto>>.FromData(secrets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_list_secrets failed");
            return Fail<List<SecretDto>>(ex);
        }
    }

    [McpServerTool(Name = "keyvault_get_secret", UseStructuredContent = true, OutputSchemaType = typeof(KeyVaultResult<KeyVaultSecretValueResult>)), Description("Reads the latest version of a secret by its key and returns the decrypted value with metadata.")]
    public async Task<KeyVaultResult<KeyVaultSecretValueResult>> GetSecretAsync(
        [Description("Secret key to read.")] string key,
        [Description("Author or user reading the secret.")] string author,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.GetSecretAsync(key, tenantId, author, ct);
            if (result is null)
                return KeyVaultResult<KeyVaultSecretValueResult>.NotFound($"Secret '{key}' not found.");

            var mapped = new KeyVaultSecretValueResult(result.Id, result.Key, result.Value, result.Version, result.TenantId, result.CreatedAt);
            return KeyVaultResult<KeyVaultSecretValueResult>.FromData(mapped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_get_secret failed for key={Key}", key);
            return Fail<KeyVaultSecretValueResult>(ex);
        }
    }

    [McpServerTool(Name = "keyvault_delete_secret", UseStructuredContent = true, OutputSchemaType = typeof(KeyVaultResult<KeyVaultMessage>)), Description("Soft-deletes a secret by its key.")]
    public async Task<KeyVaultResult<KeyVaultMessage>> DeleteSecretAsync(
        [Description("Secret key to delete.")] string key,
        [Description("Author or user performing the deletion.")] string author,
        [Description("Optional tenant identifier. Omit for the default tenant.")] Guid? tenantId = null,
        CancellationToken ct = default)
    {
        try
        {
            var deleted = await _service.DeleteSecretAsync(key, tenantId, author, ct);
            return deleted
                ? KeyVaultResult<KeyVaultMessage>.FromData(new KeyVaultMessage("Secret deleted."))
                : KeyVaultResult<KeyVaultMessage>.NotFound("Secret not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "keyvault_delete_secret failed for key={Key}", key);
            return Fail<KeyVaultMessage>(ex);
        }
    }

    private static KeyVaultResult<T> Fail<T>(Exception exception)
        => KeyVaultResult<T>.Fail(Classify(exception), exception is OperationCanceledException
            ? "The operation was cancelled by the client."
            : $"{exception.GetType().Name}: {exception.Message}");

    private static string Classify(Exception exception)
        => exception switch
        {
            OperationCanceledException => "CANCELLED",
            ArgumentException or InvalidOperationException => "INVALID_INPUT",
            UnauthorizedAccessException => "ACCESS_DENIED",
            IOException => "IO_ERROR",
            _ => "INTERNAL_ERROR"
        };
}
