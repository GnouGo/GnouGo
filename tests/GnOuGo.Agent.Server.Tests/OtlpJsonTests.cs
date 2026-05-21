using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class OtlpJsonTests
{
    [Fact]
    public void ActivityTelemetryMapper_ToSpanRow_SerializesNestedAttributesAndEvents()
    {
        using var activity = new Activity("workflow").SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        activity.SetTag("answer", 42);
        activity.SetTag("flags", new object?[] { "one", true, null });
        activity.SetTag("details", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["duration"] = TimeSpan.FromSeconds(2)
        });
        activity.AddEvent(new ActivityEvent(
            "checkpoint",
            default,
            new ActivityTagsCollection
            {
                { "attempt", 2 },
                { "payload", new Dictionary<string, object?>(StringComparer.Ordinal) { ["nested"] = "yes" } }
            }));

        var row = ActivityTelemetryMapper.ToSpanRow(activity, null, "svc");

        var attributes = JsonNode.Parse(row.AttributesJson!)!.AsObject();
        Assert.Equal(42, attributes["answer"]!.GetValue<int>());
        var flags = Assert.IsType<JsonArray>(attributes["flags"]);
        Assert.Equal("one", flags[0]!.GetValue<string>());
        Assert.True(flags[1]!.GetValue<bool>());
        Assert.True(flags[2] is null);
        var details = Assert.IsType<JsonObject>(attributes["details"]);
        Assert.True(details["ok"]!.GetValue<bool>());
        Assert.Equal("00:00:02", details["duration"]!.GetValue<string>());

        var events = JsonNode.Parse(row.EventsJson!)!.AsArray();
        var firstEvent = Assert.IsType<JsonObject>(events[0]);
        Assert.Equal("checkpoint", firstEvent["name"]!.GetValue<string>());
        var eventAttributes = Assert.IsType<JsonObject>(firstEvent["attributes"]);
        Assert.Equal(2, eventAttributes["attempt"]!.GetValue<int>());
        var payload = Assert.IsType<JsonObject>(eventAttributes["payload"]);
        Assert.Equal("yes", payload["nested"]!.GetValue<string>());

        var resource = JsonNode.Parse(row.ResourceJson!)!.AsObject();
        Assert.Equal("svc", resource["service.name"]!.GetValue<string>());
    }

    [Fact]
    public void OtlpJson_SpanRecordToDto_ParsesNestedObjectsAndEvents()
    {
        var entity = new SpanRecordEntity
        {
            TraceId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            SpanId = Enumerable.Range(17, 8).Select(i => (byte)i).ToArray(),
            Name = "workflow",
            Kind = 1,
            StartUnixNs = 1_000_000,
            EndUnixNs = 3_000_000,
            StatusCode = 1,
            AttributesJson = """
                {"answer":42,"flags":["one",true,null],"details":{"ok":true}}
                """,
            EventsJson = """
                [{"name":"checkpoint","timeUtc":"2026-01-01T00:00:00+00:00","attributes":{"attempt":2,"payload":{"nested":"yes"}}}]
                """,
            ResourceJson = """
                {"service.name":"svc"}
                """,
            ScopeJson = """
                {"name":"workflow","version":"1.0.0"}
                """
        };

        var dto = OtlpJson.SpanRecordToDto(entity);

        Assert.Equal(Convert.ToHexString(entity.SpanId).ToLowerInvariant(), dto.SpanId);
        Assert.Equal(42L, dto.Attributes.GetProperty("answer").GetInt64());

        var flags = dto.Attributes.GetProperty("flags");
        Assert.Equal(JsonValueKind.Array, flags.ValueKind);
        Assert.Equal("one", flags[0].GetString());
        Assert.True(flags[1].GetBoolean());
        Assert.Equal(JsonValueKind.Null, flags[2].ValueKind);

        var details = dto.Attributes.GetProperty("details");
        Assert.Equal(JsonValueKind.Object, details.ValueKind);
        Assert.True(details.GetProperty("ok").GetBoolean());

        var spanEvent = Assert.Single(dto.Events);
        Assert.Equal("checkpoint", spanEvent.Name);
        Assert.Equal(2L, spanEvent.Attributes.GetProperty("attempt").GetInt64());
        var payload = spanEvent.Attributes.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("yes", payload.GetProperty("nested").GetString());

        Assert.Equal("svc", dto.Resource.GetProperty("service.name").GetString());
        Assert.Equal("workflow", dto.Scope.GetProperty("name").GetString());
        Assert.Equal("1.0.0", dto.Scope.GetProperty("version").GetString());
    }
}

