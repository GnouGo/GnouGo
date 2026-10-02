using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

// Offline compiler fixtures only. These never resume a retired planning request,
// count a receipt as a dispatch, or claim approval/external execution.
internal static class RecordedPlanCompilation
{
    internal static async Task<PlanningSession> CompileAsync(JsonObject recording, TaskPlan? plan = null, PlanningCapability? replacement = null)
    {
        var template = recording["finalSession"] ?? recording["session"] ?? recording["initialSession"];
        var payload = template!.DeepClone().AsObject();
        foreach (var key in payload.Select(p => p.Key).Where(k => k.StartsWith("recorded", StringComparison.Ordinal) || k == "presentationQuery").ToArray()) payload.Remove(key);
        var state = payload.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        state.Catalog = (recording["catalog"] ?? template!["catalog"] ?? recording["initialCatalog"])!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
        if (recording["discovery"] is { } discovery) state.Discovery = discovery.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        foreach (var capability in state.Discovery.Resolved)
            if (state.Catalog.Capabilities.All(c => c.Id != capability.Id)) state.Catalog.Capabilities.Add(capability);
        if (replacement is not null) state.Catalog.Capabilities[state.Catalog.Capabilities.FindIndex(c => c.Id == replacement.Id)] = replacement;
        var proposals = recording["responses"]!.AsArray().Where(e => e?["response"]?["json"]?["plan"] is not null).Select(e => e!["response"]!["json"]!).ToArray();
        state.Plan = plan ?? (recording["plan"] ?? template!["plan"] ?? proposals.Last()["plan"])!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        state.Requirements ??= recording["responses"]!.AsArray().Select(e => e?["response"]?["json"]?["requirements"]).FirstOrDefault(r => r is not null)?.Deserialize(PlanningJsonContext.Default.PlanningRequirements)
            ?? new() { Summary = state.Request.Prompt, Outcomes = [new("work", state.Request.Prompt)] };
        state.Requirements.Inputs = state.Plan.Inputs;
        state.IntentVersion = 2; state.OutcomeVersion = null; state.OutcomeBindings = null; state.PendingCall = null; state.ApprovedHash = null;
        state.RevisionScope.Clear(); state.Graph = null; state.Yaml = null;
        state.Diagnostics.Clear(); state.Status = PlanningStatus.Clarification;
        RefreshNativeContracts(state);
        // Public model-free choice compilation path; no provider, journal replay or dispatch.
        return await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "configure_mode", Mode = PlanningMode.Auto, ExpectedRevision = state.Revision },
            new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask), TestContext.Current.CancellationToken);
    }

    // A newly labelled offline revision targets the current runtime. Original
    // recordings, issued requests and retired-session recovery never use this path.
    internal static void RefreshNativeContracts(PlanningSession state)
    {
        var contracts = new GnOuGo.Flow.Core.Runtime.WorkflowEngine().Registry.GetContracts();
        state.Catalog!.AllowedStepTypes.RemoveAll(t => !contracts.ContainsKey(t));
        state.Catalog.StepContracts = new JsonObject(state.Catalog.AllowedStepTypes.Select(t =>
            new KeyValuePair<string, JsonNode?>(t, new JsonObject
            { ["input"] = contracts[t].InputSchema.DeepClone(), ["output"] = contracts[t].OutputSchema.DeepClone() })));
    }

    // A new scripted proposal must explicitly inspect the operations it intends to select.
    // This creates selections only; the planner still resolves their authoritative contracts.
    internal static void InspectSelected(PlanningSession state, TaskPlan plan)
    {
        IEnumerable<PlanTask> Tasks(TaskScope scope) => scope.Tasks.Concat(scope.Always).SelectMany(t =>
            new[] { t }.Concat(t.Body is null ? [] : Tasks(t.Body)).Concat(t.Otherwise is null ? [] : Tasks(t.Otherwise)).Concat(t.Branches.SelectMany(Tasks)));
        var used = Tasks(plan.Root).Concat(plan.Groups.SelectMany(g => Tasks(g.Body))).Select(t => t.Operation).OfType<string>().ToHashSet(StringComparer.Ordinal);
        state.Discovery.Inspections = state.Discovery.Pages.SelectMany(p => p.Capabilities).Where(c => c.Operation is not null && used.Contains(c.Operation.Id))
            .DistinctBy(c => (c.SourceId, c.Operation!.Id)).GroupBy(c => c.SourceId)
            .Select(g => new PlanningDiscoveryRequest(g.Key, OperationIds: g.Select(c => c.Operation!.Id).ToList())).ToList();
    }

    internal static async Task RetiredAsync(PlanningSession state, IPlanningRuntime runtime)
    {
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var stopped = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status);
        Assert.Contains(stopped.Diagnostics, d => d.Code == "PLANNING_REVISION_REQUIRED");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(state.ModelCalls, stopped.ModelCalls); Assert.Equal(state.ReplanAttempts, stopped.ReplanAttempts);
        Assert.Equal(JsonSerializer.Serialize(state.PendingCall, PlanningJsonContext.Default.PlanningModelCall), JsonSerializer.Serialize(stopped.PendingCall, PlanningJsonContext.Default.PlanningModelCall));
        Assert.Equal(JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), JsonSerializer.Serialize(stopped.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
    }
}
