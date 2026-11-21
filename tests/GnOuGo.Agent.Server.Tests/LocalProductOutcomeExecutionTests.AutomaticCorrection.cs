using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task StaticCorrectedGuardsPreserveBusinessWorkAndDoNotExecuteEffects()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, ct); catalog.Policy.RequireExternalConfirmation = false;
        var retained = GuardRetained(catalog); var corrected = GuardRetained(catalog);
        foreach (var task in RetainedTasks(corrected.Plan!.Root).Where(t => t.Id is "extract_choices" or "extract_record"))
            task.Requires!.Items[1].Port = "available";
        var result = await AutomaticCompositionSession(catalog, runtime, corrected, "retained_guard", ct, retained);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, factory.InvocationAttempts);
        // This corrects the diagnosed references, not the retained plan's undiagnosed
        // business omissions. It must still undergo requirement/artifact review.
        Assert.Null(result.ApprovedHash);
    }

    [Theory]
    [InlineData("guard", "complete")]
    [InlineData("guard", "missing")]
    [InlineData("guard", "incomplete")]
    [InlineData("choice", "repeated")]
    [InlineData("choice", "unknown")]
    [InlineData("choice", "denied")]
    [InlineData("requirements", "complete")]
    [InlineData("requirements", "repeated")]
    [InlineData("initial_followup", "complete")]
    [InlineData("initial_followup", "missing")]
    [InlineData("initial_followup", "repeated")]
    [InlineData("initial_followup", "incomplete")]
    public Task StaticCorrectedBindingsProduceIndependentlyVerifiedLocalWorkbook(string defect, string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", automaticCorrection: defect);

    private static async Task<PlanningSession> AutomaticCompositionSession(PlanningCatalog catalog, WorkflowPlanningRuntime runtime,
        TaskPlanFixture corrected, string defect, CancellationToken ct, TaskPlanFixture? retained = null)
    {
        if (defect == "commitment") return await CommitmentCompositionSession(catalog, runtime, corrected, ct);
        var broken = JsonSerializer.SerializeToNode(retained ?? corrected, PlanningFixtureJsonContext.Default.TaskPlanFixture)!.Deserialize(PlanningFixtureJsonContext.Default.TaskPlanFixture)!;
        if (defect == "requirements") broken.Requirements = null;
        else if (retained is null && defect is "choice" or "initial_followup") broken.Plan!.Root.Outputs[0].Value.Kind = "choice";
        else if (retained is null)
        {
            var guard = RetainedTasks(broken.Plan!.Root).Select(t => t.Requires).OfType<TaskValue>()
                .SelectMany(TaskPlanCompiler.Values).First(v => v.Kind == "output" && v.Port is not null);
            guard.Port += "},{";
        }
        var findings = new TaskPlanCompiler().Compile(broken.Plan!, catalog).Diagnostics;
        if (defect == "requirements") Assert.Empty(findings); // Valid bindings cannot replace missing business intent.
        else Assert.Contains(findings, d => d.Code == (retained is not null ? "TASK_FIELD_UNKNOWN" : defect is "choice" or "initial_followup" ? "CHOICE_UNKNOWN" : "TASK_OUTPUT_UNKNOWN"));
        var original = JsonSerializer.Serialize(broken, PlanningFixtureJsonContext.Default.TaskPlanFixture);
        await Task.CompletedTask; ct.ThrowIfCancellationRequested();
        var session = RecordedPlanCompilation.CompileStatic(corrected, catalog, new()
        { TenantId = "local", Prompt = corrected.Requirements!.Summary, Policy = new() { RequireExternalConfirmation = false },
            Options = new() { ["compilation_profile"] = "compact-bindings-v15", ["mapping_profile"] = "adaptive-each-v1" } });
        Assert.True(session.Status == PlanningStatus.FinalReview, string.Join(';', session.Diagnostics));
        Assert.Equal(original, JsonSerializer.Serialize(broken, PlanningFixtureJsonContext.Default.TaskPlanFixture));
        Assert.Equal(0, session.ModelCalls); Assert.Null(session.ApprovedHash);
        return session;

    }
}
