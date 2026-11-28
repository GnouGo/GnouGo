using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Images;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

public sealed record WorkflowImage(string Sha256, string MimeType, int Width, int Height, int Size)
{
    [JsonIgnore] public byte[]? Bytes { get; internal init; }
    internal JsonObject Reference() => new() { ["sha256"] = Sha256, ["mimeType"] = MimeType,
        ["width"] = Width, ["height"] = Height, ["size"] = Size };
}

public interface IWorkflowImageStore
{
    Task PutAsync(string tenant, string run, string sha256, byte[] bytes, CancellationToken ct);
    Task<byte[]?> GetAsync(string tenant, string run, string sha256, CancellationToken ct);
}

internal static class WorkflowImages
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxImages = 8;

    private static WorkflowRuntimeException Invalid(string reason) => new(ErrorCodes.InputValidation, reason);

    private static WorkflowImage Describe(string mime, byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes || mime is not ("image/png" or "image/jpeg"))
            throw Invalid("Vision input requires a PNG or JPEG up to 4 MiB.");
        var (width, height) = EncodedImageHeader.TryGetSize(mime, bytes);
        if (width is null or <= 0 or > 8192 || height is null or <= 0 or > 8192 || (long)width * height > 16_777_216)
            throw Invalid("The encoded image has invalid or excessive dimensions.");
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), mime, width.Value, height.Value, bytes.Length) { Bytes = bytes };
    }

    private static (IWorkflowImageStore store, string tenant, string run) Owner(StepExecutionContext ctx)
    {
        if (ctx.Engine.ImageArtifacts is not { } store || string.IsNullOrWhiteSpace(ctx.Limits.TenantId) || string.IsNullOrWhiteSpace(ctx.Limits.RunId))
            throw Invalid("Vision requires a run-owned image artifact store, tenant and run ID.");
        return (store, ctx.Limits.TenantId, ctx.Limits.RunId);
    }

    private static async Task<WorkflowImage> SaveAsync(StepExecutionContext ctx, string mime, string data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (data.Length > (MaxBytes + 2) / 3 * 4) throw Invalid("The encoded image exceeds 4 MiB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw Invalid("The encoded image is invalid."); }
        var image = Describe(mime, bytes);
        var (store, tenant, run) = Owner(ctx);
        await store.PutAsync(tenant, run, image.Sha256, bytes, ct);
        return image;
    }

    internal static async Task<JsonArray?> ProjectAsync(StepExecutionContext ctx, McpCallResult receipt, CancellationToken ct)
    {
        if (receipt.ContentBlocks is not { Count: > 0 } blocks) return null;
        var projection = new JsonArray();
        foreach (var item in blocks)
        {
            var block = item?.AsObject();
            var type = block?["type"]?.ToString();
            var obj = type == "resource" ? block?["resource"] as JsonObject : block;
            var field = type == "resource" ? "blob" : "data";
            if ((type == "image" || type == "resource" && obj?["mimeType"]?.ToString().StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) &&
                (obj?["mimeType"]?.ToString() is not ("image/png" or "image/jpeg") || obj[field] is not JsonValue))
                throw Invalid("The MCP image must contain PNG or JPEG data.");
            if (type is not ("image" or "resource") || obj?["mimeType"]?.ToString() is not ("image/png" or "image/jpeg") ||
                obj[field] is not JsonValue data)
            {
                projection.Add(item?.DeepClone());
                continue;
            }
            var image = await SaveAsync(ctx, obj["mimeType"]!.ToString(), data.GetValue<string>(), ct);
            var copy = block!.DeepClone().AsObject();
            var binary = type == "resource" ? copy["resource"]!.AsObject() : copy;
            binary.Remove(field);
            binary["image"] = image.Reference();
            projection.Add((JsonNode)copy);
        }
        return projection;
    }

    internal static JsonArray References(JsonArray? blocks)
    {
        var result = new JsonArray();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        if (blocks is null) return result;
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var reference = block["type"]?.ToString() switch {
                "image" => block["image"], "resource" => block["resource"]?["image"], _ => null
            };
            if (reference is JsonObject image && image["sha256"] is JsonValue digest &&
                digest.TryGetValue<string>(out var hash) && hashes.Add(hash)) result.Add(image.DeepClone());
        }
        return result;
    }

    internal static async Task<IReadOnlyList<WorkflowImage>?> LoadAsync(StepExecutionContext ctx, JsonNode? value, CancellationToken ct)
    {
        if (value is null) return null;
        if (value is not JsonArray inputs || inputs.Count > MaxImages) throw Invalid("Supply an array of up to eight images.");
        var images = new List<WorkflowImage>();
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (input is not JsonObject obj) throw Invalid("An image input must be a reference or encoded image.");
            if (obj["mimeType"] is not JsonValue mime || !mime.TryGetValue<string>(out var mimeType)) throw Invalid("An image MIME type is required.");
            WorkflowImage image;
            if (obj["data"] is JsonValue data)
                image = await SaveAsync(ctx, mimeType, data.GetValue<string>(), ct);
            else
            {
                var hash = obj["sha256"]?.ToString();
                if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigitLower)) throw Invalid("Invalid image reference.");
                var (store, tenant, run) = Owner(ctx);
                var bytes = await store.GetAsync(tenant, run, hash, ct) ?? throw Invalid("The image is unavailable in this run.");
                image = Describe(mimeType, bytes);
                if (!JsonNode.DeepEquals(obj, image.Reference())) throw Invalid("The image reference does not match its content.");
            }
            if (images.All(i => i.Sha256 != image.Sha256)) images.Add(image);
        }
        return images.Count == 0 ? null : images;
    }
}
