using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GnOuGo.Agent.Server.OpenAI;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAIResponseRequest))]
[JsonSerializable(typeof(OpenAIInputMessage))]
[JsonSerializable(typeof(List<OpenAIInputMessage>))]
public partial class OpenAIJsonContext : JsonSerializerContext
{
}

public sealed record OpenAIInputMessage(string Role, string Content);

public sealed record OpenAIResponseRequest(
    string Model,
    List<OpenAIInputMessage> Input,
    bool Stream,
    double Temperature,
    bool Store);
