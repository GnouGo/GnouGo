using System.Text.Json;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

// These scripted interaction/correction tests retain the version-one extension
// contract. Fresh compact creation is exercised by CompactConstructionHostTests.
internal static class RetainedVersionOneConstruction
{
    internal static PlanningSession Restore(PlanningSession state)
    {
        state.Construction ??= new();
        state.Construction.Version = 1;
        state.Construction.Compact = null;
        state.Construction.Limits.InputLimits = null;
        state.Request.Options["construction_version"] = 1;
        state.Request.Options.Remove("construction_input_limits");
        state.Request.Options.TryAdd("compilation_profile", TaskPlanCompiler.CompactProfile);
        state.Request.Options.TryAdd("mapping_profile", TaskPlanCompiler.AdaptiveMappingProfile);
        state.Request.Options.TryAdd("planning_validation_profile", TaskPlanCompiler.AssertionValidationProfile);
        state.Request.Options.TryAdd("planning_diagnostics_version", 1);
        state.Request.Options.TryAdd("planning_input_review_version", 2);
        state.Request.Options.TryAdd("business_judge_review_version", 2);
        state.Request.Options.TryAdd("clarification_policy", 1);
        state.Request.MaxModelCalls = Math.Min(state.Request.MaxModelCalls, 12);
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    }

    internal static async Task<PlanningSession> SeedAsync(PlanningPersistenceTests.StoreFixture fixture,
        IMcpSession catalog, ILLMClient model, string name, string prompt, CancellationToken ct, int corrections = 4)
    {
        using var creation = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model,
            settings: new() { BackgroundProcessingEnabled = false, MaxModelCalls = 12, MaxReplanAttempts = corrections });
        var state = Restore(await creation.StartAsync(name, prompt, false, ct));
        var expected = state.Revision++;
        Assert.True(await fixture.Store.TrySaveAsync(state, expected, ct));
        return state;
    }
}
