using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Telemetry;

public sealed partial class LlmTraceContentStore
{
    internal async Task ExportDocumentsAsync(JsonObject detail, Stream stream, CancellationToken ct)
    {
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 16384, leaveOpen: true);
        await writer.WriteAsync(("{\"metadata\":" + detail.ToJsonString() + ",\"content\":{").AsMemory(), ct);
        var comma = "";
        foreach (var doc in Documents(detail))
        {
            await writer.WriteAsync((comma + JsonValue.Create(doc.Title)!.ToJsonString() + ":\"").AsMemory(), ct);
            var offset = 0;
            do
            {
                var page = await ReadDocumentAsync(detail, doc.Id, doc.Version, offset, 32768, ct);
                await writer.WriteAsync(System.Text.Json.JsonEncodedText.Encode(page.Text).ToString().AsMemory(), ct);
                offset = page.NextOffset;
                if (page.Complete) break;
            } while (true);
            await writer.WriteAsync("\"".AsMemory(), ct); comma = ",";
        }
        await writer.WriteAsync("}}".AsMemory(), ct);
        await writer.FlushAsync(ct);
    }
}
