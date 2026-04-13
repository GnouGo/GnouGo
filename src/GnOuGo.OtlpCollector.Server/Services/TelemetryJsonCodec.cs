using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

internal static class TelemetryJsonCodec
{
    public static string SerializeObject(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            WriteObjectProperty(writer, pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string SerializeSpanEvents(IEnumerable<SpanEventDto> events)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartArray();
        foreach (var spanEvent in events)
        {
            writer.WriteStartObject();
            writer.WriteString("name", spanEvent.Name);
            writer.WriteString("timeUtc", spanEvent.TimeUtc);
            writer.WritePropertyName("attributes");
            WriteObject(writer, spanEvent.Attributes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static Dictionary<string, object?> DeserializeObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a JSON object.");

        return DeserializeObject(document.RootElement);
    }

    public static List<SpanEventDto> DeserializeSpanEvents(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a JSON array.");

        var events = new List<SpanEventDto>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected each event entry to be a JSON object.");

            var name = item.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : throw new JsonException("Span event is missing 'name'.");

            var timeUtc = item.TryGetProperty("timeUtc", out var timeElement)
                ? timeElement.GetDateTimeOffset()
                : throw new JsonException("Span event is missing 'timeUtc'.");

            var attributes = item.TryGetProperty("attributes", out var attributesElement)
                ? DeserializeObject(attributesElement)
                : [];

            events.Add(new SpanEventDto(name, timeUtc, attributes));
        }

        return events;
    }

    private static Dictionary<string, object?> DeserializeObject(JsonElement element)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = DeserializeValue(property.Value);

        return values;
    }

    private static object? DeserializeValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => DeserializeObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(DeserializeValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString()
        };

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> values)
    {
        writer.WriteStartObject();
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            WriteObjectProperty(writer, pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteObjectProperty(Utf8JsonWriter writer, string propertyName, object? value)
    {
        if (value is null)
            return;

        if (value is JsonElement jsonElement
            && (jsonElement.ValueKind == JsonValueKind.Null || jsonElement.ValueKind == JsonValueKind.Undefined))
            return;

        writer.WritePropertyName(propertyName);
        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
                return;
            }

            jsonElement.WriteTo(writer);
            return;
        }

        if (value is JsonDocument jsonDocument)
        {
            jsonDocument.RootElement.WriteTo(writer);
            return;
        }

        if (value is JsonNode jsonNode)
        {
            jsonNode.WriteTo(writer);
            return;
        }

        if (value is IEnumerable<KeyValuePair<string, object?>> objectPairs)
        {
            WriteObject(writer, objectPairs);
            return;
        }

        if (value is IEnumerable<KeyValuePair<string, string?>> stringPairs)
        {
            writer.WriteStartObject();
            foreach (var pair in stringPairs)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    continue;

                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            return;
        }

        if (value is System.Collections.IDictionary dictionary)
        {
            writer.WriteStartObject();
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var key = entry.Key.ToString();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                WriteObjectProperty(writer, key, entry.Value);
            }

            writer.WriteEndObject();
            return;
        }

        if (value is byte[] bytes)
        {
            writer.WriteBase64StringValue(bytes);
            return;
        }

        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            writer.WriteStartArray();
            foreach (var item in sequence)
                WriteValue(writer, item);

            writer.WriteEndArray();
            return;
        }

        switch (value)
        {
            case string text:
                writer.WriteStringValue(text);
                break;
            case char character:
                writer.WriteStringValue(character.ToString());
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case DateOnly dateOnly:
                writer.WriteStringValue(dateOnly.ToString("O"));
                break;
            case TimeOnly timeOnly:
                writer.WriteStringValue(timeOnly.ToString("O"));
                break;
            case Uri uri:
                writer.WriteStringValue(uri.ToString());
                break;
            case TimeSpan timeSpan:
                writer.WriteStringValue(timeSpan.ToString());
                break;
            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}


