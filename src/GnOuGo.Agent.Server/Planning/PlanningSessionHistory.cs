using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Display metadata is never an executable or resumable planning session.</summary>
internal sealed record PlanningSessionListEntry(string SessionId, bool Workflow, string Name, DateTimeOffset UpdatedAtUtc, string Status, bool Available);

internal sealed record PlanningSessionInspection(PlanningSessionListEntry Entry, PlanningSession? Session)
{
    internal const string UnavailableMessage = "This saved planning session cannot be loaded by the current version. Start a new plan.";
    internal PlanningSession RequireSession() => Session ?? throw new PlanningConflictException(UnavailableMessage);
}

internal static class PlanningSessionHistory
{
    internal static PlanningSessionInspection Read(string payload, string tenant, string id, bool workflow, DateTimeOffset updated, long? revision = null)
    {
        var entry = new PlanningSessionListEntry(id, workflow, "Unavailable session", updated, "unavailable", false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            // Establish ownership from the stored envelope before exposing even a name.
            // Ambiguous headers cannot supply display metadata or executable authority.
            if (root.ValueKind != JsonValueKind.Object || !Unique(root) ||
                !root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object || !Unique(request) ||
                Text(request, "tenantId") != tenant || Text(request, "sessionId") != id ||
                revision is { } expected && Number(root, "revision") != expected)
                return new(entry, null);
            entry = entry with { Name = Text(request, "name") is { Length: > 0 } name ? name : id };
            if (root.TryGetProperty("updatedAtUtc", out var date) && date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out var timestamp))
                entry = entry with { UpdatedAtUtc = timestamp };
            if (Number(root, "schemaVersion") != 8) return new(entry, null);
            // Use the strict executable contract unchanged. Legacy fields are not removed or ignored.
            var state = JsonSerializer.Deserialize(payload, PlanningJsonContext.Default.PlanningSession);
            if (state?.Request is null || state.Request.TenantId != tenant || state.Request.SessionId != id || state.SchemaVersion != 8 ||
                revision is { } indexed && state.Revision != indexed)
                return new(entry, null);
            return new(entry with { Status = state.Status, Available = true }, state);
        }
        catch (JsonException) { return new(entry, null); }
    }

    private static bool Unique(JsonElement value) => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static long? Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : null;
}
