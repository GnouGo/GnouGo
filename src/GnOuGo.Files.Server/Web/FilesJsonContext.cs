using System.Text.Json.Serialization;

namespace GnOuGo.Files.Server.Web;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(HealthStatusResponse))]
[JsonSerializable(typeof(FileUploadResponse))]
[JsonSerializable(typeof(FileListItemResponse))]
[JsonSerializable(typeof(List<FileListItemResponse>))]
[JsonSerializable(typeof(FileListResponse))]
[JsonSerializable(typeof(FilesConfigResponse))]
public partial class FilesJsonContext : JsonSerializerContext
{
}



