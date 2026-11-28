using System.Text.Json.Nodes;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

internal static class ChatFileAttachments
{
    internal static async Task BindAsync(DurableFileStore files, string tenant, IReadOnlyList<string> selected,
        JsonObject inputs, CancellationToken ct)
    {
        var attachments = new JsonArray();
        var images = new JsonArray();
        foreach (var id in selected.Distinct(StringComparer.Ordinal))
        {
            var file = await files.InspectUploadAsync(tenant, id, ct) ?? throw new FileNotFoundException("An attached file is unavailable.");
            attachments.Add((JsonNode)new JsonObject { ["fileId"] = id, ["sha256"] = file.Digest,
                ["mimeType"] = file.ContentType, ["size"] = file.Size, ["name"] = file.Name });
            if (file.ContentType is not ("image/png" or "image/jpeg")) continue;
            if (images.Count >= 8 || file.Size > 4 * 1024 * 1024)
                throw new ArgumentException("Chat vision accepts eight images of up to 4 MiB each.");
            using var bytes = new MemoryStream();
            if (!await files.CopyAsync(tenant, id, null, bytes, ct)) throw new FileNotFoundException("An attached image expired.");
            images.Add((JsonNode)WorkflowImage.DescribeReference(file.ContentType, bytes.ToArray()));
        }
        inputs["attachments"] = attachments;
        if (images.Count > 0) inputs["images"] = images;
    }
}
