using System.Text.Json;
using System.Text.Json.Serialization;

namespace GnOuGo.Document.Mcp;

internal static class DocumentMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, DocumentMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(DocumentPolicyInfo))]
[JsonSerializable(typeof(DocumentReadResult))]
[JsonSerializable(typeof(DocumentSection))]
[JsonSerializable(typeof(DocumentWriteResult))]
[JsonSerializable(typeof(DocumentListResult))]
[JsonSerializable(typeof(DocumentFileInfo))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentSection>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentFileInfo>))]
internal sealed partial class DocumentMcpJsonContext : JsonSerializerContext;

