using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>A locked runtime decision. Permission and business-result selection have distinct sources.</summary>
public sealed class PlanningDecisionContract
{
    public const int CurrentVersion = 1;
    public const string HumanConfirmation = "human_confirmation";
    public int Version { get; set; } = CurrentVersion;
    public string Group { get; set; } = "";
    public string SourceOperationId { get; set; } = "";
    public string SourceCapabilityId { get; set; } = "";
    public string SourcePointer { get; set; } = "";
    public string ContractSource { get; set; } = "";
    public JsonObject ResponseSchema { get; set; } = new();
    public List<string> AllowedValues { get; set; } = [];
    public List<string> NoEffectValues { get; set; } = [];
    public List<string> EffectOperationIds { get; set; } = [];
    public List<string> InputOperationIds { get; set; } = [];
    public List<string> PermissionOperationIds { get; set; } = [];
}

public sealed class PlanningInteractionContract
{
    public string OperationId { get; set; } = "";
    public string CapabilityId { get; set; } = "";
    public string Mode { get; set; } = HumanInputContract.ModeConfirm;
    public string ResponsePointer { get; set; } = "/response";
    public JsonObject OutputSchema { get; set; } = HumanInputContract.ResolveOutputSchema(HumanInputContract.ConfirmationInput(""));
}

/// <summary>Private preparation content; persisted only through the host's encrypted snapshot store.</summary>
public sealed class PlanningPreparationCheckpoint
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = 1;
    public string Fingerprint { get; set; } = "";
    public string Stage { get; set; } = "discovery";
    /// <summary>Retry must check current producer contracts before reusing catalog-dependent results.</summary>
    public bool RefreshDiscovery { get; set; }
    public JsonObject ValidatedResults { get; set; } = new();
    public List<string> RequestHashes { get; set; } = [];
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];

    public static bool IsObsoleteMatchingQuestion(PlanningSnapshot state) => state.Status == PlanningStatus.Clarification &&
        state.Question?.StepId.StartsWith("capability-clarification-", StringComparison.Ordinal) == true &&
        state.Preparation is null && state.PreparationCheckpoint is { Version: < CurrentVersion } checkpoint &&
        checkpoint.ValidatedResults["matching_candidate"] is not null;
}

public sealed record PlanningPreparationProgress(PlanningPreparationCheckpoint Checkpoint, PlanningPreparation? Preparation);
