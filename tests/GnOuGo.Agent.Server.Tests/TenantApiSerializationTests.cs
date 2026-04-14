using System.Text.Json;
using System.Text.Json.Nodes;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Web;

namespace GnOuGo.Agent.Server.Tests;

public sealed class TenantApiSerializationTests
{
    [Fact]
    public void TelemetryApiMapper_ToLogResponse_ParsesNestedJson_AndCanOmitServiceName()
    {
        var log = new LogRecordEntity
        {
            TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ReceivedUtc = DateTimeOffset.Parse("2026-01-01T12:00:00+00:00"),
            TraceId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            SpanId = Enumerable.Range(17, 8).Select(i => (byte)i).ToArray(),
            SeverityNumber = 9,
            SeverityText = "Information",
            Body = "hello",
            ServiceName = "collector",
            AttributesJson = """
                {"nested":{"ok":true},"items":[1,"two",null]}
                """,
            ResourceJson = """
                {"service.name":"collector"}
                """,
            ScopeJson = """
                {"name":"workflow"}
                """
        };

        var mapped = TelemetryApiMapper.ToLogResponse(log, includeServiceName: false);

        Assert.Equal("collector", log.ServiceName);
        Assert.Null(mapped.ServiceName);
        Assert.Equal(Convert.ToHexString(log.TraceId), mapped.TraceId);
        Assert.Equal(Convert.ToHexString(log.SpanId), mapped.SpanId);
        Assert.Equal(JsonValueKind.Object, mapped.Attributes.ValueKind);
        Assert.True(mapped.Attributes.GetProperty("nested").GetProperty("ok").GetBoolean());
        Assert.Equal("two", mapped.Attributes.GetProperty("items")[1].GetString());
    }

    [Fact]
    public void OtlpApiJsonContext_SerializesTelemetryLogResponses_WithCamelCaseAndWithoutNullServiceName()
    {
        var payload = new List<TelemetryLogResponse>
        {
            new TelemetryLogResponse(
                TenantId: null,
                ReceivedUtc: DateTimeOffset.Parse("2026-01-01T12:00:00+00:00"),
                TraceId: "ABCDEF",
                SpanId: null,
                SeverityNumber: 13,
                SeverityText: "Warning",
                Body: "warn",
                ServiceName: null,
                Attributes: ParseJson("{\"scope\":{\"retry\":2}}"),
                Resource: ParseJson("{\"service.name\":\"collector\"}"),
                Scope: ParseJson("{\"name\":\"workflow\"}"))
        };

        var json = JsonSerializer.Serialize(payload, OtlpApiJsonContext.Default.ListTelemetryLogResponse);
        var array = JsonNode.Parse(json)!.AsArray();
        var first = array[0]!.AsObject();

        Assert.Equal("ABCDEF", first["traceId"]!.GetValue<string>());
        Assert.Equal("Warning", first["severityText"]!.GetValue<string>());
        Assert.False(first.ContainsKey("serviceName"));
        Assert.Equal(2, first["attributes"]!["scope"]!["retry"]!.GetValue<int>());
        Assert.Equal("collector", first["resource"]!["service.name"]!.GetValue<string>());
        Assert.Equal("workflow", first["scope"]!["name"]!.GetValue<string>());
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}


