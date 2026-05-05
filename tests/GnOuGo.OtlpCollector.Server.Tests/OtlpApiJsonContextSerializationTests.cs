using System.Text.Json;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Web;
using Xunit;

namespace GnOuGo.OtlpCollector.Server.Tests;

public sealed class OtlpApiJsonContextSerializationTests
{
    [Fact]
    public void Serialize_TraceDto_WithNumericAttributes_DoesNotThrow()
    {
        var span = new SpanDto(
            SpanId: "0123456789abcdef",
            ParentSpanId: null,
            Name: "test-span",
            Kind: 1,
            StartUtc: DateTimeOffset.UtcNow,
            EndUtc: DateTimeOffset.UtcNow.AddMilliseconds(5),
            DurationMs: 5,
            StatusCode: 1,
            StatusMessage: null,
            Attributes: ParseJson("""
            {"tokenCount":1234567890123,"ok":true,"ratio":1.5}
            """),
            Events:
            [
                new SpanEventDto(
                    Name: "evt",
                    TimeUtc: DateTimeOffset.UtcNow,
                    Attributes: ParseJson("""{"eventCode":42}"""))
            ],
            Resource: ParseJson("""{"service.name":"otlp-tests"}"""),
            Scope: ParseJson("""{"name":"tests","version":"1.0"}"""));

        var trace = new TraceDto(
            TraceId: "00112233445566778899aabbccddeeff",
            StartUtc: span.StartUtc,
            EndUtc: span.EndUtc,
            Spans: [span]);

        var json = JsonSerializer.Serialize(trace, OtlpApiJsonContext.Default.TraceDto);

        Assert.Contains("\"tokenCount\":1234567890123", json);
        Assert.Contains("\"eventCode\":42", json);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

