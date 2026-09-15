namespace GnOuGo.Flow.Core.Planning;

/// <summary>Evidence-backed permission policy over issued semantic actions, never an executor-wide switch.</summary>
public sealed class PlanningScopedPolicy
{
    public string Id { get; set; } = "";
    public string ObligationId { get; set; } = "";
    public string ClauseReference { get; set; } = "";
    public string EvidenceFingerprint { get; set; } = "";
    public string Rule { get; set; } = "unknown";
    public string ScopeKind { get; set; } = "unknown";
    public string Target { get; set; } = "";
    public string Applicability { get; set; } = "unknown";
    public string Origin { get; set; } = "unknown";
    public string Status { get; set; } = "pending";
    public List<string> TargetOperationIds { get; set; } = [];
    public string? PermissionOperationId { get; set; }
    public List<string> GoverningObligationIds { get; set; } = [];
    public List<string> GoverningReferences { get; set; } = [];
}
