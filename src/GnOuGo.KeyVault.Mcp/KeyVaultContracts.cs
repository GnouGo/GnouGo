using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.KeyVault.Core.Models;

namespace GnOuGo.KeyVault.Mcp;

public sealed record KeyVaultResult<T>(bool Success, T? Data, string? Error = null)
{
    public static KeyVaultResult<T> Ok(T data) => new(true, data);
    public static KeyVaultResult<T> NotFound(string message) => new(false, default, message);
}

public sealed record KeyVaultSecretMetadataResult(
    Guid Id,
    string Key,
    int Version,
    Guid? TenantId,
    DateTimeOffset CreatedAt);

public sealed record KeyVaultSecretValueResult(
    Guid Id,
    string Key,
    string Value,
    int Version,
    Guid? TenantId,
    DateTimeOffset CreatedAt);

public sealed record KeyVaultMessage(string Message);

public sealed record KeyVaultHealthResponse(string Status);

internal static class KeyVaultMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, KeyVaultMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(KeyVaultResult<List<TenantDto>>))]
[JsonSerializable(typeof(KeyVaultResult<TenantDto>))]
[JsonSerializable(typeof(KeyVaultResult<KeyVaultSecretMetadataResult>))]
[JsonSerializable(typeof(KeyVaultResult<List<SecretDto>>))]
[JsonSerializable(typeof(KeyVaultResult<KeyVaultSecretValueResult>))]
[JsonSerializable(typeof(KeyVaultResult<KeyVaultMessage>))]
[JsonSerializable(typeof(KeyVaultHealthResponse))]
[JsonSerializable(typeof(TenantDto))]
[JsonSerializable(typeof(SecretDto))]
[JsonSerializable(typeof(List<TenantDto>))]
[JsonSerializable(typeof(List<SecretDto>))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class KeyVaultMcpJsonContext : JsonSerializerContext
{
}


