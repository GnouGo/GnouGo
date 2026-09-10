using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Versioned, provider-neutral input to a resumable planning session.</summary>
public sealed class PlanningRequest
{
    public string TenantId { get; set; } = "";
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "generated";
    public string Prompt { get; set; } = "";
    public PlanningGraph? Baseline { get; set; }
    public JsonObject? FailureEvidence { get; set; }
    public JsonObject Options { get; set; } = new();
    public int MaxConcurrency { get; set; } = 4;
    public int MaxRepairsPerWorkflowGate { get; set; } = 5;
    public PlanningGenerationOptions Generation { get; set; } = new();
}

/// <summary>Request-scoped model limits; changing these never changes accepted behavior.</summary>
public sealed class PlanningGenerationOptions
{
    public string? Reasoning { get; set; }
    public int MaxInputTokensPerRequest { get; set; } = 12_000;
    public int MaxOutputTokens { get; set; } = 8_192;
}

public static class PlanningStatus
{
    public const string Created = "created";
    public const string Clarification = "clarification";
    public const string Recovery = "recovery";
    public const string BehaviorReview = "behavior_review";
    public const string Generating = "generating";
    public const string Revising = "revising";
    public const string Validating = "validating";
    public const string FinalReview = "final_review";
    public const string Approved = "approved";
    public const string Saved = "saved";
    public const string Saving = "saving";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";
    public const string Cancelled = "cancelled";
    public static bool IsTerminal(string status) => status is Approved or Saved or Failed or Unsupported or Cancelled;
    public static bool IsWaiting(string status) => status is Clarification or Recovery or BehaviorReview or FinalReview;
}

public static class PlanningPhase
{
    public const string Intent = "intent";
    public const string Capabilities = "capabilities";
    public const string Behavior = "behavior";

    public const string Dataflow = "dataflow";
    public const string Construction = "construction";
    public const string Repair = "repair";
    public static string Resolve(PlanningSnapshot snapshot) => snapshot.Status == PlanningStatus.Created
        ? !snapshot.Intent.Checked ? Intent : snapshot.Preparation is null ? Capabilities : Behavior
        : snapshot.CurrentPhase ?? snapshot.Status;
}

/// <summary>Commands always target an exact persisted revision; approval also targets its artifact hash.</summary>
public sealed class PlanningCommand
{
    public string Kind { get; set; } = "advance";
    public long ExpectedRevision { get; set; }
    public string? ArtifactHash { get; set; }
    public string? Text { get; set; }
    public JsonObject? Answers { get; set; }
    public PlanningGenerationOptions? Generation { get; set; }
}

/// <summary>Private session state. Hosts encrypt all content and persist exact revisions.</summary>
public sealed class PlanningSnapshot
{
    public int SchemaVersion { get; set; } = 4;
    public PlanningRequest Request { get; set; } = new();
    public long Revision { get; set; }
    public string Status { get; set; } = PlanningStatus.Created;
    public string? CurrentPhase { get; set; } = PlanningPhase.Intent;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? WaitingSinceUtc { get; set; }
    public double ActiveMilliseconds { get; set; }
    public double HumanWaitMilliseconds { get; set; }
    public PlanningIntentState Intent { get; set; } = new();
    public PlanningPreparation? Preparation { get; set; }
    public PlanningPreparationCheckpoint? PreparationCheckpoint { get; set; }
    public PlanningBehaviorPlan? BehaviorPlan { get; set; }
    public string? ApprovedBehaviorHash { get; set; }
    public int BehaviorAssessmentCalls { get; set; }
    public PlanningAssessmentState BehaviorAssessment { get; set; } = new();
    public PlanningBehaviorRevisionState? BehaviorRevision { get; set; }
    public List<PlanningGateAllowance> RepairAllowances { get; set; } = [];
    public PlanningGraph? Graph { get; set; }
    public PlanningConstructionState Construction { get; set; } = new();
    public PlanningValidationState Validation { get; set; } = new();
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<PlanningAttempt> Attempts { get; set; } = [];
    public List<PlanningEvent> Events { get; set; } = [];
    public List<PlanningRevision> History { get; set; } = [];
    public List<PlanningGenerationRevision> GenerationHistory { get; set; } = [];
    public LLMUsageBudgetSnapshot? Usage { get; set; }
    public string? Yaml { get; set; }
    public string? ArtifactHash { get; set; }
    public string? ApprovedHash { get; set; }
    public string? ReviewMarkdown { get; set; }
    public string? SavedAgentId { get; set; }
    public PlanningPendingCommand? PendingCommand { get; set; }
    public string? Outcome => Status switch
    {
        PlanningStatus.FinalReview or PlanningStatus.Approved => "generated",
        PlanningStatus.Saved => "saved",
        PlanningStatus.Cancelled => "cancelled",
        PlanningStatus.Unsupported => "unsupported",
        PlanningStatus.Failed => "failed",
        _ => null
    };
}

public sealed class PlanningIntentState
{
    public PlanningAssessmentState Assessment { get; set; } = new();
    public bool Checked { get; set; }
    public int Forms { get; set; }
    public int Questions { get; set; }
    public HumanInputRequest? Question { get; set; }
    public List<PlanningAnswer> Answers { get; set; } = [];
    public List<PlanningIntentRevision> History { get; set; } = [];
}

public sealed class PlanningConstructionState
{
    public string? SkeletonFingerprint { get; set; }
    public List<PlanningHole> Holes { get; set; } = [];
    public List<PlanningStagedAssignments> Candidates { get; set; } = [];
    public PlanningRepairState? Repair { get; set; }
    public PlanningDataflowContract? Dataflow { get; set; }
    public List<PlanningWorkflowProgress> Workflows { get; set; } = [];
    public List<PlanningModelCall> PendingCalls { get; set; } = [];
    public long ModelSequence { get; set; }
    public List<string> RejectedCandidates { get; set; } = [];
}

public sealed class PlanningRepairState
{
    public string GraphFingerprint { get; set; } = "";
    public JsonObject Patches { get; set; } = new();
}

public sealed class PlanningWorkflowProgress
{
    public string WorkflowKey { get; set; } = "";
    public string Status { get; set; } = "pending";
    public List<string> Dependencies { get; set; } = [];
    public string DependencyFingerprint { get; set; } = "";
    public string? GraphFingerprint { get; set; }
    public int Calls { get; set; }
    public int RepairCalls { get; set; }
    public bool ResponseRepairPending { get; set; }
    public int ResolvedHoles { get; set; }
    public int UnresolvedHoles { get; set; }
    public string? Gate { get; set; }
    public int? EstimatedInputTokens { get; set; }
    public int? InputTokenLimit { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
}

/// <summary>Exact request reserved before dispatch. Completed payloads belong to the host journal.</summary>
public sealed class PlanningModelCall
{
    public PlanningStagedAssignments? Assignments { get; set; }
    public string Id { get; set; } = "";
    public string Phase { get; set; } = "";
    public string WorkflowKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string? Gate { get; set; }
    public string? ScopeFingerprint { get; set; }
    public long Revision { get; set; }
    public LLMRequest Request { get; set; } = new();
}

public sealed class PlanningValidationState
{
    public string? ContractFingerprint { get; set; }
    public string? FixtureFingerprint { get; set; }
    public bool FixturesEstablished { get; set; }
    public PlanningAssessmentState Assessment { get; set; } = new();
    public string? GraphFingerprint { get; set; }
    public int Stage { get; set; }
    public List<PlanningScenarioResult> Scenarios { get; set; } = [];
    public JsonObject? Inputs { get; set; }
    public string? InputsFingerprint { get; set; }
    public JsonObject Observations { get; set; } = new();
}

public sealed class PlanningAssessmentState
{
    public int Stage { get; set; }
    public List<string> RejectedCandidates { get; set; } = [];
    public string? Fingerprint { get; set; }
    public int Attempts { get; set; }
    public JsonObject? Candidate { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
}

public sealed record PlanningAnswer(string Question, JsonObject Answers);
public sealed record PlanningIntentRevision(long Revision, string Prompt, List<PlanningAnswer> Answers, List<PlanningDiagnostic> Diagnostics);
public sealed record PlanningPendingCommand(string PreviousStatus, PlanningCommand Command);
public sealed record PlanningRevision(long Revision, string ArtifactHash, string Status, List<string> ChangedWorkflows);
public sealed record PlanningEvent(string Kind, string Phase, DateTimeOffset TimestampUtc, int Count = 0);
public sealed record PlanningDiagnostic(string Code, string Location, string Message, bool Required = true, string? ValidationStage = null, string? Rule = null);
public static class PlanningValidationStage
{
    public const string RuntimeContracts = "runtime_contracts";
    public const string CapabilityContracts = "capability_contracts";
    public const string ConditionalActivation = "conditional_activation";
}
public sealed record PlanningAttempt(string CandidateHash, string Phase, int Stage, bool Retained, List<PlanningDiagnostic> Diagnostics);
public sealed record PlanningScenarioResult(string Id, string Outcome, string Description, List<PlanningDiagnostic> Diagnostics);

public sealed class PlanningPreparation
{
    public int DecisionContractVersion { get; set; }
    public List<PlanningDecisionContract> Decisions { get; set; } = [];
    public List<PlanningInteractionContract> Interactions { get; set; } = [];
    public string Fingerprint { get; set; } = "";
    public JsonObject LockedContract { get; set; } = new();
    public JsonObject RuntimeState { get; set; } = new();
    public List<PlanningCapability> Capabilities { get; set; } = [];
    public List<string> AllowedStepTypes { get; set; } = [];
    public JsonObject StepContracts { get; set; } = new();
}

public sealed class PlanningCapability
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string StepType { get; set; } = "";
    public string? Server { get; set; }
    public string? Method { get; set; }
    public string? Kind { get; set; }
    public JsonObject InputSchema { get; set; } = new();
    public JsonObject OutputSchema { get; set; } = new();
    public JsonObject FixedInput { get; set; } = new();
    public List<string> OperationIds { get; set; } = [];
    public List<string> InputOperationIds { get; set; } = [];
    public bool Required { get; set; }
    public List<PlanningLiteralBinding> RequestBindings { get; set; } = [];
    public string? DeclarationFingerprint { get; set; }
    public string EffectKind { get; set; } = "unknown";
    public McpArtifactContract? ArtifactContract { get; set; }
    public McpCapabilityActivation? Activation { get; set; }
    public string? CatalogId { get; set; }
    public string? Resolution { get; set; }
}

public sealed record PlanningLiteralBinding(string Path, JsonNode? Value);

/// <summary>Reviewable obligations, without executable expressions, schemas or code.</summary>
public sealed class PlanningBehaviorPlan
{
    public string Summary { get; set; } = "";
    public string Entrypoint { get; set; } = "main";
    public List<PlanningBehaviorWorkflow> Workflows { get; set; } = [];
}

public sealed class PlanningBehaviorWorkflow
{
    public string Key { get; set; } = "main";
    public string Purpose { get; set; } = "";
    public List<string> OperationIds { get; set; } = [];
    public List<PlanningBehaviorPort> Inputs { get; set; } = [];
    public List<PlanningBehaviorPort> Outputs { get; set; } = [];
    public List<PlanningBehaviorNode> Steps { get; set; } = [];
    public List<PlanningBehaviorNode> Finally { get; set; } = [];
}

public sealed record PlanningBehaviorPort(string Name, string Description, bool Required);

public sealed class PlanningBehaviorNode
{
    public string? WorkflowKey { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "operation";
    public string Purpose { get; set; } = "";
    public List<string> OperationIds { get; set; } = [];
    public string? CapabilityId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? InputDependencies { get; set; }
    public List<PlanningBehaviorOutcome> Outcomes { get; set; } = [];
    public List<PlanningBehaviorNode> Steps { get; set; } = [];
}

public sealed record PlanningBehaviorOutcome(string Key, string Description, bool IsDefault, List<PlanningBehaviorNode> Steps);

/// <summary>Stable behavior and executable structure. Evidence never becomes executable YAML fields.</summary>
public sealed class PlanningGraph
{
    public string Summary { get; set; } = "";
    public List<PlanningWorkflow> Workflows { get; set; } = [];
    public string Entrypoint { get; set; } = "main";
    public string? Functions { get; set; }
}

public sealed class PlanningWorkflow
{
    public string Key { get; set; } = "main";
    public string Purpose { get; set; } = "";
    public List<string> OperationIds { get; set; } = [];
    public List<PlanningPort> Inputs { get; set; } = [];
    public List<PlanningOutput> Outputs { get; set; } = [];
    public List<PlanningNode> Steps { get; set; } = [];
    public List<PlanningNode> Finally { get; set; } = [];
    public string? Functions { get; set; }
}

public sealed class PlanningPort
{
    public string Name { get; set; } = "";
    public PlanningSchema Schema { get; set; } = new();
    public bool Required { get; set; } = true;
    public PlanningValue? Default { get; set; }
}

public sealed class PlanningOutput
{
    public string Name { get; set; } = "";
    public PlanningSchema Schema { get; set; } = new();
    public PlanningValue Value { get; set; } = new();
}

/// <summary>A structural schema or an exact JSON pointer into an authoritative capability schema.</summary>
public sealed class PlanningSchema
{
    public string Type { get; set; } = "string";
    public bool Nullable { get; set; }
    public string? Description { get; set; }
    public List<string> Enum { get; set; } = [];
    public PlanningSchema? Items { get; set; }
    public List<PlanningPort> Properties { get; set; } = [];
    public PlanningSchema? AdditionalProperties { get; set; }
    public string? CapabilityId { get; set; }
    public string? SchemaPointer { get; set; }
}

/// <summary>Literal, input/output reference, expression, object, array, or workflow reference.</summary>
public sealed class PlanningValue
{
    public string Kind { get; set; } = "null";
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public bool? Boolean { get; set; }
    public string? Source { get; set; }
    /// <summary>The default channel is the declared raw result; structured selects validated post-processing JSON.</summary>
    public string? ResultChannel { get; set; }
    public List<string> Path { get; set; } = [];
    public List<PlanningMember> Members { get; set; } = [];
    public List<PlanningValue> Items { get; set; } = [];
}

public sealed record PlanningMember(string Name, PlanningValue Value);

public sealed class PlanningNode
{
    /// <summary>Coordinator-owned pure adapter role; never supplied by model assignments.</summary>
    public string? InternalRole { get; set; }
    public string Key { get; set; } = "";
    public string Type { get; set; } = "set";
    public string Purpose { get; set; } = "";
    public string? CapabilityId { get; set; }
    public List<string> OperationIds { get; set; } = [];
    public PlanningValue Input { get; set; } = new() { Kind = "object" };
    public PlanningValue? If { get; set; }
    public PlanningValue? Expr { get; set; }
    public PlanningSchema? OutputSchema { get; set; }
    public PlanningStructuredOutput? StructuredOutput { get; set; }
    public string? Output { get; set; }
    public string? ItemVar { get; set; }
    public string? IndexVar { get; set; }
    public Models.RetryPolicy? Retry { get; set; }
    public List<PlanningErrorCase> OnError { get; set; } = [];
    public List<PlanningNode> Steps { get; set; } = [];
    public List<PlanningBranch> Branches { get; set; } = [];
    public List<PlanningCase> Cases { get; set; } = [];
    public List<PlanningNode> Default { get; set; } = [];
}

public sealed record PlanningBranch(List<PlanningNode> Steps);
public sealed record PlanningStructuredOutput(PlanningSchema Schema, bool Strict = true);
public sealed record PlanningCase(string? Value, PlanningValue? When, List<PlanningNode> Steps);
public sealed record PlanningErrorCase(PlanningValue? If, string Action, PlanningValue? SetOutput, Models.RetryPolicy? Retry);
/// <summary>Resolved data provenance; hosts encrypt this together with the planning snapshot.</summary>
public sealed class PlanningDataflowContract
{
    public string ContractFingerprint { get; set; } = "";
    public string? GraphFingerprint { get; set; }
    public int Version { get; set; } = 1;
    public string Fingerprint { get; set; } = "";
    public List<PlanningBinding> Bindings { get; set; } = [];
    public List<PlanningOperationDataflow> Operations { get; set; } = [];
    public Dictionary<string, List<string>> InputObligations { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PlanningBinding(string Id, string WorkflowKey, PlanningValue Value, JsonObject Schema, string Availability);
public sealed record PlanningOperationDataflow(string WorkflowKey, string NodeKey, List<string> Consumes, List<string> BusinessInputs);

public sealed record PlanningGenerationRevision(long Revision, PlanningGenerationOptions Options);

public interface IWorkflowPlanner
{
    Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot snapshot, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct);
}

public sealed record PlanningArtifactBinding(string Workflow, string Step, string CapabilityId);
public sealed record PlanningArtifactValidationRequest(string Yaml, PlanningRequest Request, PlanningPreparation Preparation, IReadOnlyList<PlanningArtifactBinding> Bindings);
public sealed record PlanningScenarioValidationRequest(string Yaml, PlanningPreparation Preparation, JsonObject Inputs, JsonObject LoopItemSchemas, JsonObject Observations);

/// <summary>Required host effects. Ownership and scenario evidence cannot be silently omitted.</summary>
public interface IPlanningRuntime
{
    Task<PlanningPreparationProgress> PrepareAsync(PlanningSnapshot snapshot, CancellationToken ct);
    Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct);
    Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct);
}

public interface IPlanningSessionStore
{
    Task<PlanningSnapshot?> LoadAsync(string tenantId, string sessionId, CancellationToken ct);
    Task<bool> TrySaveAsync(PlanningSnapshot snapshot, long? expectedRevision, CancellationToken ct);
    Task<IReadOnlyList<PlanningSnapshot>> ListAsync(string tenantId, CancellationToken ct);
}

public sealed class PlanningConflictException(string message) : InvalidOperationException(message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlanningSnapshot))]
[JsonSerializable(typeof(PlanningHole))]
[JsonSerializable(typeof(List<PlanningHole>))]
[JsonSerializable(typeof(PlanningStagedAssignments))]
[JsonSerializable(typeof(PlanningPreparationCheckpoint))]
[JsonSerializable(typeof(PlanningDecisionContract))]
[JsonSerializable(typeof(PlanningInteractionContract))]
[JsonSerializable(typeof(PlanningDataflowContract))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(PlanningGraph))]
[JsonSerializable(typeof(PlanningDataflowContract))]
[JsonSerializable(typeof(PlanningBehaviorPlan))]
[JsonSerializable(typeof(PlanningBehaviorWorkflow))]
[JsonSerializable(typeof(PlanningStructuredOutput))]
[JsonSerializable(typeof(PlanningErrorCase))]
[JsonSerializable(typeof(PlanningGenerationOptions))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(PlanningWorkflow))]
[JsonSerializable(typeof(PlanningCommand))]
[JsonSerializable(typeof(PlanningRequest))]
[JsonSerializable(typeof(PlanningPreparation))]
[JsonSerializable(typeof(PlanningCapability))]
[JsonSerializable(typeof(PlanningNode))]
[JsonSerializable(typeof(PlanningSchema))]
[JsonSerializable(typeof(PlanningValue))]
[JsonSerializable(typeof(List<PlanningDiagnostic>))]
[JsonSerializable(typeof(List<PlanningSnapshot>))]
[JsonSerializable(typeof(PlanningWorkflowProgress))]
[JsonSerializable(typeof(PlanningConstructionState))]
[JsonSerializable(typeof(PlanningValidationState))]
[JsonSerializable(typeof(LLMRequest))]
[JsonSerializable(typeof(LLMResponse))]
[JsonSerializable(typeof(LLMUsageBudgetSnapshot))]
public partial class PlanningJsonContext : JsonSerializerContext;
