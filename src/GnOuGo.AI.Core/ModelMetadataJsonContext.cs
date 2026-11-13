using System.Text.Json.Serialization;

namespace GnOuGo.AI.Core;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LLMModelMetadata))]
[JsonSerializable(typeof(LLMProviderRequestPolicyOptions))]
[JsonSerializable(typeof(LLMProviderRetryPolicyOptions))]
public partial class ModelMetadataJsonContext : JsonSerializerContext;
