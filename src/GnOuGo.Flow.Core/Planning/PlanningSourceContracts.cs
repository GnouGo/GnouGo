namespace GnOuGo.Flow.Core.Planning;

public static class PlanningConstructionStrategies
{
    public const string TypedUnitsV2 = "typed-units-v2";
    public const string JavaScriptV1 = "javascript-v1";

    public static void Validate(string strategy)
    {
        if (strategy is not (TypedUnitsV2 or JavaScriptV1))
            throw new ArgumentException("Unknown planning construction strategy.", nameof(strategy));
    }
}

/// <summary>Provider-neutral source authoring boundary. Implementations never execute workflow effects.</summary>
public interface IPlanningSourceCompiler
{
    string Format { get; }
    string Version { get; }
    string Describe(PlanningSourceContext context);
    PlanningSourceResult Compile(string source, PlanningSourceContext context, CancellationToken ct);
}

public sealed record PlanningSourceContext(PlanningWorkflow Template, PlanningPreparation Preparation);
public sealed record PlanningSourceResult(PlanningWorkflow? Workflow, IReadOnlyList<PlanningDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, PlanningSourceLocation> Locations);
public sealed record PlanningSourceLocation(int Line, int Column);

/// <summary>Private source and candidates are encrypted with the owning tenant's snapshot.</summary>
public sealed class PlanningSourceCandidate
{
    public string WorkflowKey { get; set; } = "";
    public string SdkVersion { get; set; } = "";
    public string DependencyFingerprint { get; set; } = "";
    public string? Source { get; set; }
    public string? PendingPrompt { get; set; }
    public long? PendingRevision { get; set; }
    public PlanningWorkflow? Candidate { get; set; }
    public string? CandidateHash { get; set; }
    public string Status { get; set; } = "pending";
    // Cumulative across invalidation, restart, and explicit retry. Reserved once before checkpoint/dispatch.
    public int Calls { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<string> FindingFingerprints { get; set; } = [];
    public Dictionary<string, PlanningSourceLocation> Locations { get; set; } = new(StringComparer.Ordinal);
}
