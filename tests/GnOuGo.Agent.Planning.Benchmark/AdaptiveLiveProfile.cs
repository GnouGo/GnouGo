using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

// Captured only for a fresh opt-in business session. All selected business calls still
// pass through the existing argument-level guards and independent scenario observers.
internal sealed class AdaptiveLiveProfile(string scenario)
{
    internal const string Version = "host-mcp-observations-v1", Runner = "observe", Backend = "GnOuGo.GithubCopilot.Mcp";
    internal IReadOnlyList<CopilotToolBinding> Tools { get; } = scenario switch
    {
        "amazon" => [new("GnOuGo.Browser.Mcp", "browser_get_content")],
        PublishedReviewQualification.Scenario => [new("GnOuGo.Git.Mcp", "git_compare_refs"), new(Backend, "code_read_file")],
        _ => throw new ArgumentException("Unknown adaptive qualification scenario.")
    };
    internal IReadOnlyDictionary<string, string> Runners => new Dictionary<string, string> { [Runner] = Backend };
    internal IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>> Bindings =>
        new Dictionary<string, IReadOnlyList<CopilotToolBinding>> { [Runner] = Tools };

    private readonly Dictionary<string, JsonObject> _approved = new(StringComparer.Ordinal);
    private readonly AsyncLocal<McpCorrelationContext?> _call = new();
    internal static bool IsMetadata(string server, string method, JsonNode? args) => server == Backend &&
        method == "copilot_task_contract" && args is JsonObject { Count: 1 } &&
        args["hostToolsOnly"] is JsonValue value && value.TryGetValue<bool>(out var only) && only;

    internal void BindStage(PlanningNode stage, PlanningCapability capability)
    {
        if (capability.Kind != "agent" || capability.Method != Runner || capability.FixedInput["runner"]?.ToString() != Runner)
            throw new PlanningConflictException("The reviewed stage selected a different runner.");
        var scope = new JsonObject { ["runner"] = Runner };
        foreach (var member in stage.Input.Members.Where(p => p.Name != "inputs"))
            scope[member.Name] = PlanningGraphValidation.Literal(member.Value);
        var task = AgentTaskContracts.Parse(scope);
        if (task.Budget.MaxActions is null || task.Capabilities.Count == 0 || task.Capabilities.Any(id =>
            !id.StartsWith("mcp_", StringComparison.Ordinal)) || task.Verification.Any(v => v.Kind != "mcp.result"))
            throw new PlanningConflictException("The reviewed runner requires bounded, host-only MCP observations.");
        var suffix = "/step/n_" + PlanningGraphCompiler.Fingerprint(stage.Key)[..16];
        var approved = Scope(task);
        if (_approved.TryGetValue(suffix, out var other) && !JsonNode.DeepEquals(other, approved))
            throw new PlanningConflictException("Two reviewed stages have ambiguous invocation identities.");
        _approved[suffix] = approved;
    }

    private static JsonObject Scope(AgentTaskDefinition task) =>
        JsonSerializer.SerializeToNode(task with { Inputs = new() }, AgentTaskJsonContext.Default.AgentTaskDefinition)!.AsObject();

    internal static bool IsControl(string server, string method) => server == Backend && method is
        "copilot_task_validate" or "copilot_task_run" or "copilot_task_inspect" or "copilot_task_exchange";

    internal IDisposable Enter(McpCorrelationContext call, string run)
    {
        if (call.TenantId != "benchmark" || call.RunId != run || call.StepType != "agent.run" || call.StepId is null ||
            !_approved.Keys.Any(suffix => call.StepId.EndsWith(suffix, StringComparison.Ordinal)))
            throw new PlanningConflictException("Adaptive execution requires the reviewed Server stage.");
        var previous = _call.Value;
        _call.Value = call;
        return new CallScope(() => _call.Value = previous);
    }

    internal void Require(string method, JsonNode? args)
    {
        if (_call.Value is not { StepId: { } step } call || call.MethodName != method || args is not JsonObject)
            throw new PlanningConflictException("The adaptive control request has no matching execution scope.");
        if (method == "copilot_task_exchange")
        {
            if (args["runId"]?.ToString() != call.RunId || args["invocationId"]?.ToString() != step)
                throw new PlanningConflictException("The exchange belongs to another invocation.");
            return;
        }
        var context = JsonSerializer.Deserialize(args["contextJson"]!.GetValue<string>(), AgentTaskJsonContext.Default.AgentTaskContext);
        var expected = _approved.Single(p => step.EndsWith(p.Key, StringComparison.Ordinal)).Value;
        if (context is null || context.TenantId != call.TenantId || context.RunId != call.RunId || context.InvocationId != step ||
            !JsonNode.DeepEquals(expected, Scope(context.Task)))
            throw new PlanningConflictException("The adaptive task differs from the reviewed scope.");
        if (method == "copilot_task_inspect") return;
        var manifest = JsonNode.Parse(args["toolsJson"]!.GetValue<string>())!.AsObject();
        if (!context.Task.Capabilities.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Select(p => p.Key)) ||
            manifest.Any(p => p.Value is not JsonObject item || AgentTaskContracts.McpToolId(item) != p.Key ||
                !Tools.Contains(new(item["server"]!.GetValue<string>(), item["method"]!.GetValue<string>()))))
            throw new PlanningConflictException("The run requested a tool outside its approved observation profile.");
    }

    internal sealed class CallScope(Action close) : IDisposable { public void Dispose() => close(); }
}
