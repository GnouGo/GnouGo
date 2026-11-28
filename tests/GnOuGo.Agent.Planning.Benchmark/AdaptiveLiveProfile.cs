using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

// Captured only for a fresh opt-in business session. All selected business calls still
// pass through the existing argument-level guards and independent scenario observers.
internal sealed class AdaptiveLiveProfile(string scenario, string? capturedVersion = null)
{
    internal const string Version = "workflow-agent-observations-v2", Runner = "observe";
    internal const string DeckCraftInspection = "deckcraft-inspection-v1";
    internal string ProfileVersion { get; } = capturedVersion switch {
        null => scenario == "deckcraft" ? DeckCraftInspection : Version,
        Version => Version,
        DeckCraftInspection when scenario == "deckcraft" => DeckCraftInspection,
        _ => throw new PlanningConflictException("Unknown captured observation profile.")
    };
    internal IReadOnlyList<CopilotToolBinding> Tools { get; } = scenario switch
    {
        "deckcraft" when capturedVersion == Version => [new("DeckCraft", "render_slide")],
        "deckcraft" => [new("DeckCraft", "list_commands"), new("DeckCraft", "inspect_document"),
            new("DeckCraft", "inspect_slide"), new("DeckCraft", "render_slide")],
        LocalReviewQualification.Scenario => [],
        "amazon" => [new("GnOuGo.Browser.Mcp", "browser_get_content")],
        PublishedReviewQualification.Scenario => [new("GnOuGo.Git.Mcp", "git_compare_refs")],
        _ => throw new ArgumentException("Unknown adaptive qualification scenario.")
    };
    internal IReadOnlyDictionary<string, string> Runners => scenario == LocalReviewQualification.Scenario
        ? new Dictionary<string, string> { ["coding"] = "code" } : new Dictionary<string, string> { [Runner] = "business" };
    internal IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>> Bindings =>
        scenario == LocalReviewQualification.Scenario ? new Dictionary<string, IReadOnlyList<CopilotToolBinding>>() :
        new Dictionary<string, IReadOnlyList<CopilotToolBinding>> { [Runner] = Tools };

    private readonly Dictionary<string, JsonObject> _approved = new(StringComparer.Ordinal);
    internal void BindStage(PlanningNode stage, PlanningCapability capability)
    {
        var runner = scenario == LocalReviewQualification.Scenario ? "coding" : Runner;
        if (capability.Kind != "agent" || capability.Method != runner || capability.FixedInput["runner"]?.ToString() != runner)
            throw new PlanningConflictException("The reviewed stage selected a different runner.");
        var scope = new JsonObject { ["runner"] = runner };
        foreach (var member in stage.Input.Members.Where(p => p.Name != "inputs"))
            scope[member.Name] = PlanningGraphValidation.Literal(member.Value);
        var task = AgentTaskContracts.Parse(scope);
        if (task.Budget.MaxActions is null || task.Capabilities.Count == 0 || (runner == "coding"
            ? !task.Capabilities.Contains("project.read") || task.Capabilities.Any(id => id is not ("project.read" or "project.native_read" or "project.write")) ||
                task.Verification.All(v => v.Kind != "file.read")
            : task.Capabilities.Any(id => !id.StartsWith("mcp_", StringComparison.Ordinal)) || task.Verification.Any(v => v.Kind != "mcp.result")))
            throw new PlanningConflictException("The reviewed runner requires bounded, host-only MCP observations.");
        var suffix = "/step/n_" + PlanningGraphCompiler.Fingerprint(stage.Key)[..16];
        var approved = Scope(task);
        if (_approved.TryGetValue(suffix, out var other) && !JsonNode.DeepEquals(other, approved))
            throw new PlanningConflictException("Two reviewed stages have ambiguous invocation identities.");
        _approved[suffix] = approved;
    }

    private static JsonObject Scope(AgentTaskDefinition task) =>
        JsonSerializer.SerializeToNode(task with { Inputs = new() }, AgentTaskJsonContext.Default.AgentTaskDefinition)!.AsObject();

    internal bool Approves(AgentTaskContext context) => _approved.Any(stage =>
        context.InvocationId.EndsWith(stage.Key, StringComparison.Ordinal) && JsonNode.DeepEquals(stage.Value, Scope(context.Task)));

    internal void RequireCall(McpCorrelationContext call, string run)
    {
        if (call.TenantId != "benchmark" || call.RunId != run || call.StepType != "agent.run" ||
            call.StepId is null || call.ServerName is null || call.MethodName is null ||
            !Tools.Contains(new(call.ServerName, call.MethodName)) ||
            call.Context?["agent_capability"]?.GetValue<string>() is not { } id ||
            !_approved.Any(stage => call.StepId.EndsWith(stage.Key, StringComparison.Ordinal) &&
                stage.Value["capabilities"]!.AsArray().Any(capability => capability?.GetValue<string>() == id)))
            throw new PlanningConflictException("This tool is outside the reviewed invocation or its pinned capabilities.");
    }
}
