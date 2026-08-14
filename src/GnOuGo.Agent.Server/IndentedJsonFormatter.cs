using System.Buffers;
using System.Text;
using System.Text.Json;

namespace GnOuGo.Agent.Server.Formatting;

internal static class IndentedJsonFormatter
{
    public static bool TryFormat(string input, out string formattedJson)
    {
        formattedJson = string.Empty;
        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(input);
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
            document.RootElement.WriteTo(writer);
            writer.Flush();
            formattedJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
