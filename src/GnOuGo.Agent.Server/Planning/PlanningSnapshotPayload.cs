using System.IO.Compression;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Consumer-owned encoding, always stored through encrypted KeyVault records.</summary>
internal static class PlanningSnapshotPayload
{
    private const string Prefix = "gnougo-planning-br4:";

    internal static string Encode(PlanningSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 4) throw new InvalidOperationException("Unsupported planning snapshot schema.");
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
                throw new JsonException("Unsupported planning payload format.");
            using var buffer = new MemoryStream(Convert.FromBase64String(value[Prefix.Length..]));
            using var compressed = new BrotliStream(buffer, CompressionMode.Decompress);
            var snapshot = JsonSerializer.Deserialize(compressed, PlanningJsonContext.Default.PlanningSnapshot)
                ?? throw new JsonException();
            return snapshot.SchemaVersion == 4 ? snapshot : throw new JsonException("Unsupported planning schema.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        { throw new InvalidOperationException("The encrypted planning revision is invalid.", ex); }
    }
}
