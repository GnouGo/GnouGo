using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace GnOuGo.Document.Mcp;

internal static class DocumentMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    // The CLR result also represents host failures. MCP exposes those as isError
    // content, so its successful structured output can state the actual guarantees.
    internal static JsonElement SuccessfulWriteSchema(JsonElement original)
    {
        var schema = JsonNode.Parse(original.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        foreach (var name in new[] { "filePath", "filePathAbsolute" })
        {
            properties[name]!["type"] = "string";
            properties[name]!["minLength"] = 1;
        }
        properties["bytesWritten"]!["type"] = "integer";
        properties["bytesWritten"]!["minimum"] = 0;
        properties["success"]!["const"] = true;
        foreach (var name in new[] { "errorCode", "errorMessage" }) properties[name]!["type"] = "null";
        var required = schema["required"] as JsonArray ?? new JsonArray();
        schema["required"] ??= required;
        foreach (var name in new[] { "success", "filePath", "filePathAbsolute", "bytesWritten" })
            if (!required.Any(p => p?.ToString() == name)) required.Add((JsonNode?)JsonValue.Create(name));
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, DocumentMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(DocumentPolicyInfo))]
[JsonSerializable(typeof(DocumentReadResult))]
[JsonSerializable(typeof(DocumentWriteResult))]
[JsonSerializable(typeof(DocumentListResult))]
[JsonSerializable(typeof(DocumentSection))]
[JsonSerializable(typeof(DocumentFileInfo))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentSection>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentFileInfo>))]
[JsonSerializable(typeof(DocumentServerSettings))]
internal sealed partial class DocumentMcpJsonContext : JsonSerializerContext;
