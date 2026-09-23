using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

public sealed class PlanningRequest
{
    public string Mode { get; set; } = PlanningMode.Interactive;
    public string TenantId { get; set; } = "";
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "generated";
    public string Prompt { get; set; } = "";
    public SemanticPlan? Baseline { get; set; }
    public string? RevisionContext { get; set; }
    public JsonObject? FailureEvidence { get; set; }
    public JsonObject Options { get; set; } = new();
    public PlanningPolicy Policy { get; set; } = new();
    public int MaxReplanAttempts { get; set; } = 2;
    public int MaxModelCalls { get; set; } = 8;
    public PlanningGenerationOptions Generation { get; set; } = new();
}

public sealed class PlanningGenerationOptions
{
    public string Reasoning { get; set; } = "medium";
    public int MaxInputTokensPerRequest { get; set; } = 12_000;
    public int MaxOutputTokens { get; set; } = 8_192;
}

/// <summary>Host-owned constraints. Model output cannot modify this policy.</summary>
public sealed class PlanningPolicy
{
    public List<string> AllowedStepTypes { get; set; } = [];
    public List<string> DeniedCapabilityIds { get; set; } = [];
    public bool RequireExternalConfirmation { get; set; } = true;
    public int MaxStepsTotal { get; set; } = 300;
    public string Instructions { get; set; } = "";
}

public static class PlanningStatus
{
    public const string Created = "created", Generating = "generating", Clarification = "clarification",
        FinalReview = "final_review", Approved = "approved", Saving = "saving", Saved = "saved",
        Stopped = "stopped", Failed = "failed", Cancelled = "cancelled";
    public const string WaitingForDecision = "waiting_for_decision";
    public static bool IsWaiting(string status) => status is Clarification or FinalReview or WaitingForDecision;
    public static bool IsTerminal(string status) => status is Approved or Saved or Stopped or Failed or Cancelled;
}

public sealed class PlanningCommand
{
    public string? Mode { get; set; }
    public PlanningDecisionAnswer? DecisionAnswer { get; set; }
    public string Kind { get; set; } = "advance";
    public long ExpectedRevision { get; set; }
    public string? ArtifactHash { get; set; }
    public string? Text { get; set; }
    public JsonObject? Answers { get; set; }
    public PlanningGenerationOptions? Generation { get; set; }
}

/// <summary>The sole durable state. Hosts encrypt its content and use optimistic revisions.</summary>
public sealed class PlanningSession
{
    public string? ComputeArtifactHash() => Yaml is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(new JsonObject
    {
        ["yaml"] = Yaml,
        ["groundedPlan"] = JsonSerializer.SerializeToNode(GroundedPlan, PlanningJsonContext.Default.GroundedPlan),
        ["semanticPlan"] = JsonSerializer.SerializeToNode(SemanticPlan, PlanningJsonContext.Default.SemanticPlan),
        ["grounding"] = JsonSerializer.SerializeToNode(Grounding, PlanningJsonContext.Default.CapabilityGrounding),
        ["graph"] = JsonSerializer.SerializeToNode(Graph, PlanningJsonContext.Default.PlanningGraph),
        ["catalog"] = JsonSerializer.SerializeToNode(Catalog, PlanningJsonContext.Default.PlanningCatalog),
        ["diagnostics"] = JsonSerializer.SerializeToNode(Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
        ["scenarios"] = JsonSerializer.SerializeToNode(Scenarios, PlanningJsonContext.Default.ListPlanningScenarioResult),
        ["fixtures"] = JsonSerializer.SerializeToNode(Fixtures, PlanningJsonContext.Default.PlanningFixtures)
    }.ToJsonString())));

    public int SchemaVersion { get; set; } = 8;
    public PlanningRequest Request { get; set; } = new();
    public long Revision { get; set; }
    public string Status { get; set; } = PlanningStatus.Created;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? WaitingSinceUtc { get; set; }
    public double ActiveMilliseconds { get; set; }
    public double HumanWaitMilliseconds { get; set; }
    public PlanningCatalog? Catalog { get; set; }
    public GroundedPlan? GroundedPlan { get; set; }
    public SemanticPlan? SemanticPlan { get; set; }
    public CapabilityGrounding? Grounding { get; set; }
    public GroundedBindingProgress? BindingProgress { get; set; }
    public string Phase { get; set; } = PlanningPhase.Semantic;
    public PlanningGraph? Graph { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<PlanningScenarioResult> Scenarios { get; set; } = [];
    public PlanningFixtures? Fixtures { get; set; }
    public List<PlanningAnswer> Answers { get; set; } = [];
    public PlanningDecision? PendingDecision { get; set; }
    public PlanningDecisionContinuation? DecisionContinuation { get; set; }
    public List<PlanningDecisionRecord> Decisions { get; set; } = [];
    public int ClarificationRounds { get; set; }
    public int ReplanAttempts { get; set; }
    public int ModelCalls { get; set; }
    public PlanningModelCall? PendingCall { get; set; }
    public string? RejectedProposalHash { get; set; }
    public LLMUsageBudgetSnapshot? Usage { get; set; }
    public string? Yaml { get; set; }
    public string? ApprovedHash { get; set; }
    public string? SavedAgentId { get; set; }
}

public sealed class PlanningModelCall
{
    public string Id { get; set; } = "";
    public string Purpose { get; set; } = "semantic";
    public LLMRequest Request { get; set; } = new();
}

public sealed class PlanningCatalog
{
    public List<PlanningCapability> Capabilities { get; set; } = [];
    public List<string> AllowedStepTypes { get; set; } = [];
    public JsonObject StepContracts { get; set; } = new();
    public PlanningPolicy Policy { get; set; } = new();
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
    public List<PlanningLiteralBinding> RequestBindings { get; set; } = [];
    public string EffectKind { get; set; } = "unknown";
    public McpArtifactContract? ArtifactContract { get; set; }
    public JsonNode? Metadata { get; set; }
    public JsonNode? ExampleResponse { get; set; }
}

public sealed record PlanningLiteralBinding(string Path, JsonNode? Value);
public sealed record PlanningBinding(string Id, string WorkflowKey, PlanningValue Value, JsonObject Schema, string Availability);
public sealed record PlanningAnswer(string Question, JsonObject Answers);
public sealed record PlanningDiagnostic(string Code, string Location, string Message, bool Required = true, string? ValidationStage = null, string? Rule = null)
{
    // Omit absent additions so existing artifact hashes and schema-8 histories remain stable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningComputationContext? Computation { get; init; }
}
public sealed record PlanningComputationContext(string Expression, string Limitation, JsonObject ReceiverContract,
    JsonObject ParameterContracts, string? OriginExpression = null, string? ProducerLocation = null);
public sealed record PlanningScenarioResult(string Id, string Outcome, string Description, List<PlanningDiagnostic> Diagnostics);
public sealed record PlanningArtifactBinding(string Workflow, string Step, string CapabilityId);
public sealed record PlanningArtifactValidationRequest(string Yaml, PlanningRequest Request, PlanningCatalog Catalog, IReadOnlyList<PlanningArtifactBinding> Bindings);
public sealed record PlanningScenarioValidationRequest(string Yaml, PlanningCatalog Catalog, JsonObject? Inputs, JsonObject LoopItemSchemas, JsonObject Observations);

public interface IWorkflowPlanner
{
    Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct);
}

public interface IPlanningRuntime
{
    Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct);
    Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct);
    Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct);
    Task CheckpointAsync(PlanningSession session, CancellationToken ct);
}

public interface IPlanningSessionStore
{
    Task<PlanningSession?> LoadAsync(string tenantId, string sessionId, CancellationToken ct);
    Task<bool> TrySaveAsync(PlanningSession session, long? expectedRevision, CancellationToken ct);
    Task<IReadOnlyList<PlanningSession>> ListAsync(string tenantId, CancellationToken ct);
}

public sealed class PlanningConflictException(string message) : InvalidOperationException(message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(PlanningSession))]
[JsonSerializable(typeof(List<PlanningSession>))]
[JsonSerializable(typeof(PlanningRequest))]
[JsonSerializable(typeof(PlanningCommand))]
[JsonSerializable(typeof(PlanningDecision))]
[JsonSerializable(typeof(PlanningDecisionAnswer))]
[JsonSerializable(typeof(PlanningGenerationOptions))]
[JsonSerializable(typeof(PlanningCatalog))]
[JsonSerializable(typeof(PlanningCapability))]
[JsonSerializable(typeof(GroundedPlan))]
[JsonSerializable(typeof(SemanticPlan))]
[JsonSerializable(typeof(SemanticAction))]
[JsonSerializable(typeof(CapabilityGrounding))]
[JsonSerializable(typeof(GroundingPageResult))]
[JsonSerializable(typeof(GroundedOperation))]
[JsonSerializable(typeof(GroundedValue))]
[JsonSerializable(typeof(BusinessType))]
[JsonSerializable(typeof(GroundedInput))]
[JsonSerializable(typeof(GroundedOutput))]
[JsonSerializable(typeof(PlanningFixtures))]
[JsonSerializable(typeof(PlanningGraph))]
[JsonSerializable(typeof(PlanningWorkflow))]
[JsonSerializable(typeof(PlanningNode))]
[JsonSerializable(typeof(PlanningSchema))]
[JsonSerializable(typeof(PlanningValue))]
[JsonSerializable(typeof(List<PlanningDiagnostic>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(LLMRequest))]
[JsonSerializable(typeof(LLMResponse))]
[JsonSerializable(typeof(LLMUsageBudgetSnapshot))]
[JsonSerializable(typeof(HumanInputRequest))]
public partial class PlanningJsonContext : JsonSerializerContext;
