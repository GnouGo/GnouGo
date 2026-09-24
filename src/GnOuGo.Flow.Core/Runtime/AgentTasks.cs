using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>An approved unit of adaptive work. Runners must enforce this scope before dispatch.</summary>
public sealed record AgentTaskDefinition
{
    public string Runner { get; init; } = "";
    public string Objective { get; init; } = "";
    public JsonObject Inputs { get; init; } = new();
    public JsonObject OutputSchema { get; init; } = new();
    public string Workspace { get; init; } = "";
    public List<string> Capabilities { get; init; } = [];
    public AgentTaskBudget Budget { get; init; } = new();
    public List<AgentVerificationRequirement> Verification { get; init; } = [];
}

public sealed record AgentTaskBudget
{
    public int MaxElapsedMilliseconds { get; init; } = 120_000;
    public int MaxModelCalls { get; init; } = 8;
    public long MaxTotalTokens { get; init; } = 128_000;
}

/// <summary>Evidence is supplied by an execution adapter, never parsed from assistant prose.</summary>
public sealed record AgentTaskEvidence(string Id, string Kind, string Subject, JsonObject Facts);
public sealed record AgentVerificationRequirement(string Id, string Kind, string Subject, JsonObject FactsSchema);
public sealed record AgentTaskArtifact(string Id, string Kind, string Location, string? ContentHash = null);
public sealed record AgentTaskUsage(int ModelCalls, long TotalTokens, double ElapsedMilliseconds)
{
    /// <summary>"observed" or a conservative "reserved_upper_bound"; never treat reservations as measured usage.</summary>
    public string Metering { get; init; } = "observed";
}
public sealed record AgentTaskResult(string Status, JsonNode? Output, IReadOnlyList<AgentTaskEvidence> Evidence,
    IReadOnlyList<AgentTaskArtifact> Artifacts, AgentTaskUsage Usage, string? Message = null)
{
    public IReadOnlyList<AgentVerificationFinding> Verification { get; init; } = [];
}
public sealed record AgentTaskContext(string TenantId, string RunId, string InvocationId, AgentTaskDefinition Task)
{
    public string? ExecutionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentName { get; init; }
    [JsonIgnore] public Action<AgentTaskProgress>? Progress { get; init; }
    [JsonIgnore] public Action<HumanInputRequest, string>? HumanInput { get; init; }
}
public sealed record AgentTaskProgress(string Kind, string Message);
public sealed record AgentTaskRunnerContract(string Description, JsonObject InputSchema);
public sealed record AgentVerificationFinding(string RequirementId, bool Passed, string Message);

public interface IAgentTaskRunner
{
    string Description => "Bounded adaptive tasks with host-enforced scopes and observed evidence.";
    Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct)
        => Task.FromResult(new AgentTaskRunnerContract("Bounded adaptive execution; the host validates the approved scope before dispatch.", AgentTaskContracts.InputSchema));
    /// <summary>Reject unsupported permissions, workspace constraints or unenforceable budgets before dispatch.</summary>
    Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct);
    Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct);
    /// <summary>Observe an interrupted invocation without repeating its effects.</summary>
    Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct);
}

public interface IAgentTaskVerifier
{
    Task<IReadOnlyList<AgentVerificationFinding>> VerifyAsync(AgentTaskContext context, AgentTaskResult result, CancellationToken ct);
}

/// <summary>Checks exact execution evidence against approved requirements; text is never evidence.</summary>
public sealed class EvidenceAgentTaskVerifier : IAgentTaskVerifier
{
    public Task<IReadOnlyList<AgentVerificationFinding>> VerifyAsync(AgentTaskContext context, AgentTaskResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<AgentVerificationFinding> findings = context.Task.Verification.Select(requirement =>
        {
            var matches = result.Evidence.Where(e => e.Kind == requirement.Kind && e.Subject == requirement.Subject).ToArray();
            var passed = matches.Length > 0 && matches.All(e =>
                JsonSchemaContractValidator.ValidateInstance(e.Facts, requirement.FactsSchema).Count == 0);
            return new AgentVerificationFinding(requirement.Id, passed,
                passed ? "Observed execution evidence satisfies the requirement." : "Required execution evidence is missing or contradictory.");
        }).ToArray();
        return Task.FromResult(findings);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentTaskDefinition))]
[JsonSerializable(typeof(AgentTaskRunnerContract))]
[JsonSerializable(typeof(AgentTaskContext))]
[JsonSerializable(typeof(AgentTaskResult))]
[JsonSerializable(typeof(List<AgentVerificationFinding>))]
public partial class AgentTaskJsonContext : JsonSerializerContext;
