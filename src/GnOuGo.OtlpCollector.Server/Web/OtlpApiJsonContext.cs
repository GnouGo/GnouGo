using System.Text.Json;
using System.Text.Json.Serialization;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Web;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(ApiMessageResponse))]
[JsonSerializable(typeof(HealthStatusResponse))]
[JsonSerializable(typeof(QueueStatusResponse))]
[JsonSerializable(typeof(CollectorConfigResponse))]
[JsonSerializable(typeof(CreateTenantRequest))]
[JsonSerializable(typeof(TenantAdminCreatedResponse))]
[JsonSerializable(typeof(TenantSummaryResponse))]
[JsonSerializable(typeof(List<TenantSummaryResponse>))]
[JsonSerializable(typeof(TelemetryLogResponse))]
[JsonSerializable(typeof(List<TelemetryLogResponse>))]
[JsonSerializable(typeof(TraceSummaryDto))]
[JsonSerializable(typeof(List<TraceSummaryDto>))]
[JsonSerializable(typeof(TraceDto))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(List<object?>))]
public partial class OtlpApiJsonContext : JsonSerializerContext
{
}


