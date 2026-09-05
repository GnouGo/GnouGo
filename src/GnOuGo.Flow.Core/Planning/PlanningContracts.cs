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
    public string? ExistingYaml { get; set; }
    public JsonObject Options { get; set; } = new();
    public int MaxConcurrency { get; set; } = 4;
    public int MaxRepairs { get; set; } = 3;
}

public static class PlanningStatus
{
    public const string Created = "created";
    public const string Clarification = "clarification";
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
    public static bool IsWaiting(string status) => status is Clarification or BehaviorReview or FinalReview;
}

/// <summary>Commands always target an exact persisted revision; approval also targets its artifact hash.</summary>
public sealed class PlanningCommand
{
    public string Kind { get; set; } = "advance";
    public long ExpectedRevision { get; set; }
    public string? ArtifactHash { get; set; }
    public string? Text { get; set; }
    public JsonObject? Answers { get; set; }
}

/// <summary>Contains private user content. Hosts must encrypt snapshots at rest, never log them.</summary>
public sealed class PlanningSnapshot
{
    public int SchemaVersion { get; set; } = 2;
    public PlanningRequest Request { get; set; } = new();
    public long Revision { get; set; }
    public string Status { get; set; } = PlanningStatus.Created;
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
    public List<PlanningAnswer> Answers { get; set; } = [];
    public HumanInputRequest? Question { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<PlanningScenarioResult> Scenarios { get; set; } = [];
    public List<PlanningRevision> History { get; set; } = [];
    public List<PlanningEvent> Events { get; set; } = [];
    public Dictionary<string, PlanningFragment> Fragments { get; set; } = new(StringComparer.Ordinal);
    public string? Yaml { get; set; }
    public string? ArtifactHash { get; set; }
    public string? ApprovedHash { get; set; }
    public int RepairAttempt { get; set; }
    public int NonImprovingAttempts { get; set; }
    public string? PreviousDiagnosticHash { get; set; }
    public LLMUsageBudgetSnapshot? Usage { get; set; }
    public string? SavedAgentId { get; set; }
    public bool IntentChecked { get; set; }
    public string? Feedback { get; set; }
    public PlanningGraph? BestGraph { get; set; }
    public List<PlanningDiagnostic> BestDiagnostics { get; set; } = [];
    public Dictionary<string, PlanningFragment> BestFragments { get; set; } = new(StringComparer.Ordinal);
    public PlanningGraph? ReviewedGraph { get; set; }
    public List<string> ChangedFragments { get; set; } = [];
    public PlanningGraph? PreviousGraph { get; set; }
    public string? ReviewMarkdown { get; set; }
    public PlanningPendingCommand? PendingCommand { get; set; }
}

public sealed record PlanningAnswer(string Question, JsonObject Answers);
public sealed record PlanningPendingCommand(string PreviousStatus, PlanningCommand Command);
public sealed record PlanningRevision(long Revision, string ArtifactHash, string Status, List<string> ChangedFragments);
public sealed record PlanningEvent(string Kind, string Phase, DateTimeOffset TimestampUtc, int Count = 0);
public sealed record PlanningDiagnostic(string Code, string Location, string Message, bool Required = true);
public sealed record PlanningScenarioResult(string Id, string Outcome, string Description, List<PlanningDiagnostic> Diagnostics);

public sealed class PlanningPreparation
{
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
    public bool Required { get; set; }
    public List<PlanningLiteralBinding> RequestBindings { get; set; } = [];
    public string? DeclarationFingerprint { get; set; }
    public string EffectKind { get; set; } = "unknown";
}

public sealed record PlanningLiteralBinding(string Path, JsonNode? Value);

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
public sealed record PlanningCase(string? Value, PlanningValue? When, List<PlanningNode> Steps);
public sealed record PlanningErrorCase(PlanningValue? If, string Action, PlanningValue? SetOutput, Models.RetryPolicy? Retry);
public sealed record PlanningFragment(string Fingerprint, PlanningWorkflow Workflow, bool Validated);

public interface IWorkflowPlanner
{
    Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot snapshot, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct);
}

/// <summary>Host-independent boundary to existing discovery, policies, model transport and validators.</summary>
public interface IPlanningRuntime
{
    Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct);
    Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct);
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
[JsonSerializable(typeof(PlanningGraph))]
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
