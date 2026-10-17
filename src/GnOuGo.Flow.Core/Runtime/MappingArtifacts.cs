using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>A validated extraction program, isolated from workflow intent and execution receipts.</summary>
public sealed record MappingArtifact(string Key, string Script, string? ContentHash, int ProfileVersion);

public interface IMappingArtifactStore
{
    Task<MappingArtifact?> ReadAsync(string tenant, string key, CancellationToken ct);
    Task WriteAsync(string tenant, MappingArtifact artifact, CancellationToken ct);
    Task RemoveAsync(string tenant, string key, CancellationToken ct);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MappingArtifact))]
public partial class MappingArtifactJsonContext : JsonSerializerContext;
