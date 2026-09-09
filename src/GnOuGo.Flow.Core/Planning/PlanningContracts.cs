using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Versioned, provider-neutral input to a resumable planning session.</summary>
public sealed class PlanningRequest
{
    public string ConstructionStrategy { get; set; } = PlanningConstructionStrategies.TypedUnitsV2;
    public string TenantId { get; set; } = "";
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "generated";
    public string Prompt { get; set; } = "";
    public string? ExistingYaml { get; set; }
    public JsonObject Options { get; set; } = new();
    public int MaxConcurrency { get; set; } = 4;
    public int MaxRepairs { get; set; } = 3;
    public PlanningGenerationOptions Generation { get; set; } = new();
    public List<PlanningDiagnostic> PreparationFeedback { get; set; } = [];
}

/// <summary>Request-scoped construction limits; changing these never changes accepted behavior.</summary>
public sealed class PlanningGenerationOptions
{
    public string? Reasoning { get; set; }
    public int MaxNodesPerUnit { get; set; } = 4;
    public int MaxInputTokensPerUnit { get; set; } = 12_000;
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

    public static string Resolve(PlanningSnapshot snapshot) => snapshot.Status == PlanningStatus.Created
        ? !snapshot.IntentChecked ? Intent : snapshot.Preparation is null ? Capabilities : Behavior
        : snapshot.CurrentPhase ??
        (snapshot.Graph is null ? !snapshot.IntentChecked ? Intent : snapshot.Preparation is null ? Capabilities : Behavior : snapshot.Status);
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

/// <summary>Contains private user content. Hosts must encrypt snapshots at rest, never log them.</summary>
public sealed class PlanningSnapshot
{
    public int SchemaVersion { get; set; } = 2;
    public PlanningRequest Request { get; set; } = new();
    public long Revision { get; set; }
    public string Status { get; set; } = PlanningStatus.Created;
    public string? CurrentPhase { get; set; }
    public string? Outcome => Status switch
    {
        PlanningStatus.FinalReview or PlanningStatus.Approved => "generated",
        PlanningStatus.Saved => "saved", PlanningStatus.Cancelled => "cancelled",
        PlanningStatus.Unsupported => "unsupported", PlanningStatus.Failed => "failed", _ => null
    };
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? WaitingSinceUtc { get; set; }
    public double ActiveMilliseconds { get; set; }
    public double HumanWaitMilliseconds { get; set; }
    public PlanningPreparation? Preparation { get; set; }
    public PlanningGraph? Graph { get; set; }
    public PlanningBehaviorPlan? BehaviorPlan { get; set; }
    public string? ApprovedBehaviorHash { get; set; }
    public List<PlanningAttempt> Attempts { get; set; } = [];
    public List<PlanningAnswer> Answers { get; set; } = [];
    // Null identifies older snapshots; planners initialize these from the retained answers and pending form.
    public int? ClarificationForms { get; set; }
    public int? ClarificationQuestions { get; set; }
    public List<PlanningIntentRevision> IntentHistory { get; set; } = [];
    public HumanInputRequest? Question { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<PlanningScenarioResult> Scenarios { get; set; } = [];
    public List<PlanningScenarioResult> BestScenarios { get; set; } = [];
    public JsonObject? ScenarioInputs { get; set; }
    public string? ScenarioInputsFingerprint { get; set; }
    public JsonObject ScenarioObservations { get; set; } = new();
    public List<PlanningRevision> History { get; set; } = [];
    public List<PlanningEvent> Events { get; set; } = [];
    public Dictionary<string, PlanningFragment> Fragments { get; set; } = new(StringComparer.Ordinal);
    public List<PlanningConstructionUnit> ConstructionUnits { get; set; } = [];
    public List<PlanningSourceCandidate> SourceCandidates { get; set; } = [];
    public string? SourceBehaviorHash { get; set; }
    public PlanningDataflowContract? Dataflow { get; set; }
    public PlanningPreparationCheckpoint? PreparationCheckpoint { get; set; }
    public List<PlanningDiagnostic> PreparationFeedback { get; set; } = [];
    public int PreparationReassessments { get; set; }
    // Cumulative reassessments are retained; an explicit Retry opens a new bounded allowance.
    public int PreparationReassessmentsAtRetry { get; set; }
    public string? PreparationReviewFingerprint { get; set; }
    public List<PlanningGenerationRevision> GenerationHistory { get; set; } = [];
    public string? Yaml { get; set; }
    public string? ArtifactHash { get; set; }
    public string? ApprovedHash { get; set; }
    public int RepairAttempt { get; set; }
    // The current automatic behavior assessment, separate from fragment repairs.
    public int BehaviorAssessmentCalls { get; set; }
    public int NonImprovingAttempts { get; set; }
    public string? PreviousDiagnosticHash { get; set; }
    public LLMUsageBudgetSnapshot? Usage { get; set; }
    public string? SavedAgentId { get; set; }
    public bool IntentChecked { get; set; }
    public string? Feedback { get; set; }
    // Additive provenance; older snapshots may recover assessment identity from retained attempts.
    public string? FeedbackSource { get; set; }
    public string? FeedbackAssessmentHash { get; set; }
    public PlanningGraph? BestGraph { get; set; }
    public List<PlanningDiagnostic> BestDiagnostics { get; set; } = [];
    public Dictionary<string, PlanningFragment> BestFragments { get; set; } = new(StringComparer.Ordinal);
    public PlanningGraph? ReviewedGraph { get; set; }
    public List<string> ChangedFragments { get; set; } = [];
    public PlanningGraph? PreviousGraph { get; set; }
    public PlanningBehaviorPlan? BehaviorRevisionSource { get; set; }
    public JsonObject? BehaviorRevisionPatch { get; set; }
    public string? ReviewMarkdown { get; set; }
    public PlanningPendingCommand? PendingCommand { get; set; }
}

public sealed record PlanningAnswer(string Question, JsonObject Answers);
public sealed record PlanningIntentRevision(long Revision, string Prompt, List<PlanningAnswer> Answers, List<PlanningDiagnostic> Diagnostics);
public sealed record PlanningPendingCommand(string PreviousStatus, PlanningCommand Command);
public sealed record PlanningRevision(long Revision, string ArtifactHash, string Status, List<string> ChangedFragments);
public sealed record PlanningEvent(string Kind, string Phase, DateTimeOffset TimestampUtc, int Count = 0);
public sealed record PlanningDiagnostic(string Code, string Location, string Message, bool Required = true, string? ValidationStage = null);
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
    /// <summary>Null/default retains legacy addressing; structured selects validated post-processing JSON.</summary>
    public string? ResultChannel { get; set; }
    public List<string> Path { get; set; } = [];
    public List<PlanningMember> Members { get; set; } = [];
    public List<PlanningValue> Items { get; set; } = [];
}

public sealed record PlanningMember(string Name, PlanningValue Value);

public sealed class PlanningNode
{
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
public sealed record PlanningFragment(string Fingerprint, PlanningWorkflow Workflow, bool Validated);

/// <summary>Encrypted, resumable construction checkpoint. Candidates remain untrusted until validated.</summary>
public sealed class PlanningConstructionUnit
{
    public string Key { get; set; } = "";
    public string WorkflowKey { get; set; } = "";
    public string Kind { get; set; } = "implementation";
    public List<string> NodeKeys { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "pending";
    public JsonObject? Candidate { get; set; }
    // Additive checkpoints: technical producer review never changes behavior approval.
    public List<string> ConsumerContractReviews { get; set; } = [];
    public bool WaitingForProducerReview { get; set; }
    public JsonObject? ProducerReviewBaseline { get; set; }
    public bool FlatSchemaGeneration { get; set; }
    public JsonObject? SchemaDeclarations { get; set; }
    public bool PartialCandidate { get; set; }
    public int GeneratedFieldGroups { get; set; }
    public string? CandidateHash { get; set; }
    public List<string> RequestHashes { get; set; } = [];
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public int Calls { get; set; }
    public List<PlanningDiagnostic> DispatchDiagnostics { get; set; } = [];
    public int RepairCalls { get; set; }
    public int RepairCallsAtRetry { get; set; }
    /// <summary>Exact undeclared-field findings actually sent for repair under the current dependency contracts.</summary>
    public List<string> RepairedFieldFindings { get; set; } = [];
    public string? RepairedFieldCandidateHash { get; set; }
    public string? Functions { get; set; }
    public int ContractVersion { get; set; }
    public int? EstimatedInputTokens { get; set; }
    public int? InputTokenLimit { get; set; }
    public string? DispatchOutcome { get; set; }
}

/// <summary>Resolved data provenance; hosts encrypt this together with the planning snapshot.</summary>
public sealed class PlanningDataflowContract
{
    public int Version { get; set; } = 1;
    public string Fingerprint { get; set; } = "";
    public List<PlanningBinding> Bindings { get; set; } = [];
    public List<PlanningOperationDataflow> Operations { get; set; } = [];
    public Dictionary<string, List<string>> InputObligations { get; set; } = new(StringComparer.Ordinal);
    public List<string> AssessedWorkflows { get; set; } = [];
    public int AssessmentCalls { get; set; }
    public int AssessmentCallsAtRetry { get; set; }
}

public sealed record PlanningBinding(string Id, string WorkflowKey, PlanningValue Value, JsonObject Schema, string Availability);
public sealed record PlanningOperationDataflow(string WorkflowKey, string NodeKey, List<string> Consumes, List<string> BusinessInputs);

public sealed record PlanningGenerationRevision(long Revision, PlanningGenerationOptions Options);

public interface IWorkflowPlanner
{
    Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot snapshot, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct);
}

/// <summary>Host-independent boundary to existing discovery, policies, model transport and validators.</summary>
/// <summary>Compiler-derived capability ownership, separate from executable YAML.</summary>
public sealed record PlanningArtifactBinding(string Workflow, string Step, string CapabilityId);

public interface IPlanningRuntime
{
    Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct);
    async Task<PlanningPreparationProgress> AdvancePreparationAsync(PlanningRequest request, PlanningPreparationCheckpoint checkpoint,
        Func<CancellationToken, Task> persist, CancellationToken ct) => new(checkpoint, await PrepareAsync(request, ct));
    Task EnrichPreparationAsync(PlanningPreparation preparation, CancellationToken ct) => Task.CompletedTask;
    Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation,
        IReadOnlyList<PlanningArtifactBinding> bindings, CancellationToken ct) => ValidateAsync(yaml, request, preparation, ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, JsonObject inputs, CancellationToken ct)
        => ValidateScenariosAsync(yaml, preparation, ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, JsonObject inputs, JsonObject loopItemSchemas, CancellationToken ct)
        => ValidateScenariosAsync(yaml, preparation, inputs, ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, JsonObject inputs, JsonObject loopItemSchemas, JsonObject observations, CancellationToken ct)
        => ValidateScenariosAsync(yaml, preparation, inputs, loopItemSchemas, ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
    Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
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
[JsonSerializable(typeof(PlanningPreparationCheckpoint))]
[JsonSerializable(typeof(PlanningDecisionContract))]
[JsonSerializable(typeof(PlanningInteractionContract))]
[JsonSerializable(typeof(PlanningDataflowContract))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(PlanningGraph))]
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
[JsonSerializable(typeof(LLMRequest))]
[JsonSerializable(typeof(LLMResponse))]
[JsonSerializable(typeof(LLMUsageBudgetSnapshot))]
public partial class PlanningJsonContext : JsonSerializerContext;
