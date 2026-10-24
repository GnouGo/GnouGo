using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Tests;

// Preserve the captured one-draft protocol used by approval/clarification fixtures.
// This test-only factory does not alter the production fresh-session capture path.
internal sealed class RetainedVersionOnePlanningFactory(IKeyVaultRecordStore records, string leaseDirectory) : IPlanningRuntimeFactory
{
    private readonly WorkflowPlanningRuntimeFactory _inner = new(records, leaseDirectory);
    public Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSession initial, CancellationToken ct)
    {
        initial.Construction = new() { Version = 1 };
        initial.Request.MaxModelCalls = Math.Min(initial.Request.MaxModelCalls, 12);
        initial.Request.Options["construction_version"] = 1;
        initial.Request.Options.Remove("construction_input_limits");
        initial.Request.Options.TryAdd("compilation_profile", TaskPlanCompiler.CompactProfile);
        initial.Request.Options.TryAdd("mapping_profile", TaskPlanCompiler.AdaptiveMappingProfile);
        initial.Request.Options.TryAdd("planning_validation_profile", TaskPlanCompiler.AssertionValidationProfile);
        initial.Request.Options.TryAdd("planning_diagnostics_version", 1);
        initial.Request.Options.TryAdd("planning_input_review_version", 2);
        initial.Request.Options.TryAdd("business_judge_review_version", 2);
        initial.Request.Options.TryAdd("clarification_policy", 1);
        var retained = JsonSerializer.Deserialize(JsonSerializer.Serialize(initial, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        return _inner.OpenAsync(context, retained, ct);
    }

    public Task<string> ReadApprovedYamlAsync(StepExecutionContext context, string sessionId, string artifactHash, CancellationToken ct)
        => _inner.ReadApprovedYamlAsync(context, sessionId, artifactHash, ct);
}
