
using Microsoft.Extensions.Logging.Abstractions;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services.Options;
using OtlpTenantCollector.Services.Routing;
using Xunit;

namespace GnOuGo.OtlpCollector.Server.Tests;

public sealed class TelemetryRouteClassifierTests
{
    [Fact]
    public void SelectCollector_ReturnsC_WhenNameFilterRuleMatchesBeforeRagRule()
    {
        var classifier = new TelemetryRouteClassifier();
        var options = CreateOptions();
        var span = CreateSpan(
            name: "important-operation /rag",
            attributesJson: """{"workflow.type":"rag"}""");

        var collector = classifier.SelectCollector([span], [], options, NullLogger.Instance);

        Assert.Equal("C", collector);
    }

    [Fact]
    public void SelectCollector_ReturnsA_WhenAnyTraceSpanHasRagMarker()
    {
        var classifier = new TelemetryRouteClassifier();
        var options = CreateOptions();
        var regularSpan = CreateSpan(name: "GET /chat", attributesJson: "{}", spanId: 1);
        var ragSpan = CreateSpan(name: "vector retrieval", attributesJson: """{"workflow.type":"rag"}""", spanId: 2);

        var collector = classifier.SelectCollector([regularSpan, ragSpan], [], options, NullLogger.Instance);

        Assert.Equal("A", collector);
    }

    [Fact]
    public void SelectCollector_ReturnsDefaultB_WhenNoRuleMatches()
    {
        var classifier = new TelemetryRouteClassifier();
        var options = CreateOptions();
        var span = CreateSpan(name: "GET /health", attributesJson: "{}", spanId: 3);

        var collector = classifier.SelectCollector([span], [], options, NullLogger.Instance);

        Assert.Equal("B", collector);
    }

    [Fact]
    public void SelectCollector_CanMatchAttributeValueForCollectorC()
    {
        var classifier = new TelemetryRouteClassifier();
        var options = CreateOptions();
        var span = CreateSpan(
            name: "normal-operation",
            attributesJson: """{"workflow.name":"very-important-workflow"}""",
            spanId: 4);

        var collector = classifier.SelectCollector([span], [], options, NullLogger.Instance);

        Assert.Equal("C", collector);
    }

    private static TelemetryRoutingOptions CreateOptions()
        => new()
        {
            Enabled = true,
            DefaultCollector = "B",
            Collectors =
            {
                ["A"] = new TelemetryCollectorOptions { Endpoint = "http://collector-a:4318" },
                ["B"] = new TelemetryCollectorOptions { Endpoint = "http://collector-b:4318" },
                ["C"] = new TelemetryCollectorOptions { Endpoint = "http://collector-c:4318" },
            },
            Rules =
            [
                new TelemetryRouteRuleOptions
                {
                    Name = "name-or-value-filter-to-c",
                    Collector = "C",
                    Signals = ["traces", "logs"],
                    MatchAny =
                    [
                        new TelemetryMatchOptions { SpanNameContains = ["important-operation"] },
                        new TelemetryMatchOptions
                        {
                            AnyAttributes =
                            [
                                new TelemetryAttributeMatchOptions
                                {
                                    Key = "workflow.name",
                                    Contains = "important-workflow"
                                }
                            ]
                        }
                    ]
                },
                new TelemetryRouteRuleOptions
                {
                    Name = "rag-genai-workflows-to-a",
                    Collector = "A",
                    Signals = ["traces", "logs"],
                    MatchAny =
                    [
                        new TelemetryMatchOptions
                        {
                            AnyAttributes =
                            [
                                new TelemetryAttributeMatchOptions
                                {
                                    Key = "workflow.type",
                                    Value = "rag"
                                }
                            ]
                        },
                        new TelemetryMatchOptions { SpanNameContains = ["rag", "retrieval", "embedding", "vector"] }
                    ]
                }
            ]
        };

    private static SpanRow CreateSpan(string name, string attributesJson, int spanId = 1)
        => new(
            TenantId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ReceivedUtc: DateTimeOffset.UtcNow,
            TraceId: Convert.FromHexString("00112233445566778899AABBCCDDEEFF"),
            SpanId: BitConverter.GetBytes(spanId).Concat(new byte[4]).ToArray(),
            ParentSpanId: null,
            Name: name,
            Kind: 1,
            StartUnixNs: 1,
            EndUnixNs: 2,
            StatusCode: 1,
            StatusMessage: null,
            AttributesJson: attributesJson,
            EventsJson: "[]",
            ResourceJson: """{"service.name":"test-service"}""",
            ScopeJson: """{"name":"tests","version":"1.0.0"}""",
            ServiceName: "test-service");
}


