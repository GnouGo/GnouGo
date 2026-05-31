using System.Text.Json.Serialization;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, LLMModelMetadata>))]
internal partial class ModelMetadataJsonContext : JsonSerializerContext
{
}

