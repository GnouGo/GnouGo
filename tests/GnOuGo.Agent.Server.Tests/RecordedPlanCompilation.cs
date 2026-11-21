using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

// Offline compiler fixtures only. These never resume a retired planning request,
// count a receipt as a dispatch, or claim approval/external execution.
internal static class RecordedPlanCompilation
{
    internal static Task<PlanningSession> CompileAsync(JsonObject recording, TaskPlan? plan = null, PlanningCapability? replacement = null)
    {
        var template = recording["finalSession"] ?? recording["session"] ?? recording["initialSession"];
        var state = new PlanningSession
        {
            Request = template!["request"]!.Deserialize(PlanningJsonContext.Default.PlanningRequest)!,
            Requirements = template["requirements"]?.Deserialize(PlanningJsonContext.Default.PlanningRequirements),
            Catalog = (recording["catalog"] ?? template["catalog"] ?? recording["initialCatalog"])!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!
        };
        if (recording["discovery"] is { } discovery) state.Discovery = discovery.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        foreach (var capability in state.Discovery.Resolved)
            if (state.Catalog.Capabilities.All(c => c.Id != capability.Id)) state.Catalog.Capabilities.Add(capability);
        if (replacement is not null) state.Catalog.Capabilities[state.Catalog.Capabilities.FindIndex(c => c.Id == replacement.Id)] = replacement;
        var proposals = (recording["responses"] as JsonArray ?? []).Where(e => e?["response"]?["json"]?["plan"] is not null)
            .Select(e => e!["response"]!["json"]!).ToArray();
        state.Plan = plan ?? (recording["plan"] ?? template["plan"] ?? proposals.Last()["plan"])!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        state.Requirements ??= (recording["responses"] as JsonArray ?? []).Select(e => e?["response"]?["json"]?["requirements"])
            .FirstOrDefault(r => r is not null)?.Deserialize(PlanningJsonContext.Default.PlanningRequirements)
            ?? new() { Summary = state.Request.Prompt, Outcomes = [new("work", state.Request.Prompt)] };
        state.Requirements.Inputs = state.Plan.Inputs;
        RefreshNativeContracts(state);
        var compilation = new TaskPlanCompiler().Compile(state.Plan, state.Catalog, state.Request);
        state.Diagnostics = compilation.Diagnostics.ToList(); state.Graph = compilation.Graph;
        if (state.Graph is not null && state.Diagnostics.Count == 0)
        {
            PlanningConfirmationGuards.Apply(state.Graph, state.Catalog);
            state.Diagnostics.AddRange(PlanningExecutableValidation.Validate(state.Graph, state.Catalog));
            if (state.Diagnostics.Count == 0)
            {
                state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name,
                    true, TaskPlanCompiler.UsesFusedBindings(state.Request), TaskPlanCompiler.UsesConsumerBindings(state.Request),
                    TaskPlanCompiler.UsesDirectProjections(state.Request), TaskPlanCompiler.UsesNativeMappings(state.Request),
                    TaskPlanCompiler.UsesReadableMappings(state.Request), TaskPlanCompiler.UsesLeanMappings(state.Request),
                    TaskPlanCompiler.UsesCompactProjections(state.Request), TaskPlanCompiler.UsesInlineConsumers(state.Request),
                    TaskPlanCompiler.UsesDirectValues(state.Request), TaskPlanCompiler.UsesCheckedControls(state.Request),
                    TaskPlanCompiler.UsesReadableBranches(state.Request), TaskPlanCompiler.UsesSimplifiedLocals(state.Request));
                state.Status = PlanningStatus.FinalReview;
            }
        }
        return Task.FromResult(state);
    }

    internal static PlanningSession CompileStatic(GnOuGo.Planning.Examples.TaskPlanFixture fixture, PlanningCatalog catalog, PlanningRequest? request = null)
    {
        var state = new PlanningSession { Request = request ?? new() { TenantId = "fixture", Prompt = fixture.Requirements?.Summary ?? "Static compiler fixture" },
            Plan = fixture.Plan, Requirements = fixture.Requirements, Catalog = catalog };
        RefreshNativeContracts(state);
        var compiled = new TaskPlanCompiler().Compile(state.Plan!, catalog, state.Request);
        state.Diagnostics = compiled.Diagnostics.ToList(); state.Graph = compiled.Graph;
        if (state.Graph is null || state.Diagnostics.Count != 0) return state;
        PlanningConfirmationGuards.Apply(state.Graph, catalog);
        state.Diagnostics.AddRange(PlanningExecutableValidation.Validate(state.Graph, catalog));
        if (state.Diagnostics.Count != 0) return state;
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, catalog, state.Request.Name,
            true, TaskPlanCompiler.UsesFusedBindings(state.Request), TaskPlanCompiler.UsesConsumerBindings(state.Request),
            TaskPlanCompiler.UsesDirectProjections(state.Request), TaskPlanCompiler.UsesNativeMappings(state.Request),
                    TaskPlanCompiler.UsesReadableMappings(state.Request), TaskPlanCompiler.UsesLeanMappings(state.Request),
                    TaskPlanCompiler.UsesCompactProjections(state.Request), TaskPlanCompiler.UsesInlineConsumers(state.Request),
                    TaskPlanCompiler.UsesDirectValues(state.Request), TaskPlanCompiler.UsesCheckedControls(state.Request),
                    TaskPlanCompiler.UsesReadableBranches(state.Request), TaskPlanCompiler.UsesSimplifiedLocals(state.Request));
        state.Status = PlanningStatus.FinalReview;
        return state;
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
        Assert.Throws<PlanningConflictException>(() => PlanningSessionStorage.Read(before, state.Request.TenantId, state.Request.SessionId));
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }
}
