using System.Text.Json;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Rejects obsolete encrypted payloads without attempting to upgrade executable authority.</summary>
public static class PlanningSessionStorage
{
    public const string IncompatibleMessage = "This planning session is incompatible with planning format 10. Regenerate and approve the workflow. Its original record is unchanged.";
    public static PlanningSession Read(string payload, string tenantId, string sessionId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 10)
                throw new PlanningConflictException(IncompatibleMessage);
            var state = JsonSerializer.Deserialize(payload, PlanningJsonContext.Default.PlanningSession);
            if (state?.Request.TenantId != tenantId || state.Request.SessionId != sessionId)
                throw new PlanningConflictException("The planning session does not belong to this tenant and identity.");
            return state;
        }
        catch (JsonException) { throw new PlanningConflictException(IncompatibleMessage); }
    }
}
