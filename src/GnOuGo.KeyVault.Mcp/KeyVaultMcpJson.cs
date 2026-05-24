using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.KeyVault.Core.Models;

namespace GnOuGo.KeyVault.Mcp;

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

[JsonSerializable(typeof(KeyVaultResult))]
[JsonSerializable(typeof(KeyVaultSecretMetadataDto))]
[JsonSerializable(typeof(TenantDto))]
[JsonSerializable(typeof(SecretDto))]
[JsonSerializable(typeof(SecretValueDto))]
[JsonSerializable(typeof(IReadOnlyList<TenantDto>))]
[JsonSerializable(typeof(IReadOnlyList<SecretDto>))]
[JsonSerializable(typeof(List<TenantDto>))]
[JsonSerializable(typeof(List<SecretDto>))]
internal sealed partial class KeyVaultMcpJsonContext : JsonSerializerContext;

