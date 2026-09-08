using System.IO.Compression;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Consumer-owned encoding, always stored through encrypted KeyVault records.</summary>
internal static class PlanningSnapshotPayload
{
    private const string Prefix = "gnougo-planning-br1:";

    internal static string Encode(PlanningSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        using (var compressed = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(compressed, snapshot, PlanningJsonContext.Default.PlanningSnapshot);
        return Prefix + Convert.ToBase64String(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    internal static PlanningSnapshot Decode(string value)
    {
        try
        {
            if (!value.StartsWith(Prefix, StringComparison.Ordinal))
                return JsonSerializer.Deserialize(value, PlanningJsonContext.Default.PlanningSnapshot)
                    ?? throw new JsonException();
            using var buffer = new MemoryStream(Convert.FromBase64String(value[Prefix.Length..]));
            using var compressed = new BrotliStream(buffer, CompressionMode.Decompress);
            return JsonSerializer.Deserialize(compressed, PlanningJsonContext.Default.PlanningSnapshot)
                ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        { throw new InvalidOperationException("The encrypted planning revision is invalid.", ex); }
    }
}
