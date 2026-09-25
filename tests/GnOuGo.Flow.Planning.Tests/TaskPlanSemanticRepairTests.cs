using System.Text.Json;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPlanSemanticRepairTests
{
    // Sanitized reproduction of d636c99:fixture:review_distractors:2. Business
    // requests and runtime oracles remain in the unchanged shared corpus.
    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog)> RetainedFailure()
    {
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = new PlanningBenchmarkCases.Environment("review_distractors").Factory() }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var plan = PlanningCorpus.Tasks("review_distractors", catalog);
        var checks = plan.Root.Tasks.Where(t => t.Id is "lint" or "unit" or "integration").ToArray();
        plan.Root.Tasks.RemoveAll(checks.Contains);
        plan.Root.Tasks.Insert(2, new() { Id = "checks", Kind = "parallel", Objective = "Run independent checks", Branches = checks.Select(t => new TaskScope { Tasks = [t] }).ToList() });
        foreach (var task in checks)
            foreach (var port in new[] { "evidence", "status" })
                plan.Root.Outputs.Add(new(task.Id + "_" + port, PlanningCorpus.Business("output", task.Id, port)));
        return (plan, catalog);
    }
    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    private static void ExportChecks(TaskPlan plan)
    {
        var parallel = plan.Root.Tasks.Single(t => t.Id == "checks");
        foreach (var branch in parallel.Branches)
            foreach (var port in new[] { "evidence", "status" })
            {
                var task = branch.Tasks[0]; var name = task.Id + "_" + port;
                branch.Outputs.Add(new(name, PlanningCorpus.Business("output", task.Id, port)));
                plan.Root.Outputs.Single(o => o.Name == name).Value.Source = parallel.Id;
                plan.Root.Outputs.Single(o => o.Name == name).Value.Port = name;
            }
    }

    [Fact]
    public async Task PreflightReportsAllSixConsumersAndThreeExportBoundariesWithoutLowering()
    {
        var (plan, catalog) = await RetainedFailure();
        plan.Inputs.Add(new() { Name = "optional", Required = false });
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Empty(result.Sources);
        Assert.Equal(6, result.Diagnostics.Count(d => d.Code == "TASK_REFERENCE_UNKNOWN"));
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == "TASK_EXPORT_REQUIRED"));
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_DEFAULT_REQUIRED");
        Assert.Equal(result.Diagnostics.OrderBy(d => d.Location, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal), result.Diagnostics);
    }

    [Fact]
    public async Task ExplicitMinimalExportsRepairTheRetainedFailureAndPreserveIndependentOutcomes()
    {
        var (plan, catalog) = await RetainedFailure();
        var failed = new TaskPlanCompiler().Compile(plan, catalog);
        var scope = TaskPlanRevisions.Scope(plan, failed.Diagnostics);
        var repaired = Clone(plan); ExportChecks(repaired);
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        var compiled = new TaskPlanCompiler().Compile(repaired, catalog); Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var variant in new[] { "nominal", "failure", "incomplete", "rejected", "head_changed", "cancelled", "workflow_denied", "permission_unavailable" })
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
            var environment = new PlanningBenchmarkCases.Environment("review_distractors", variant);
            if (variant == "cancelled") environment.CancelDuringWork(cancellation);
            var denied = variant is "workflow_denied" or "permission_unavailable";
            var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = variant == "permission_unavailable" ? null : new PlanningCorpus.Human(!denied) };
            var result = await engine.ExecuteAsync(document.Workflows["main"], PlanningBenchmarkCases.Inputs("review_distractors", variant), cancellation.Token);
            // Same independent acceptance rules as the retained benchmark harness.
            var correct = denied ? !result.Success && environment.Effects.Count == 0
                : variant == "cancelled" ? !result.Success && environment.Violations.Count == 0 && environment.Effects.LastOrDefault() == "remove_workspace" && !environment.Effects.Contains("publish_review")
                : environment.Verify(result);
            Assert.True(correct, variant + ": " + result.Error?.Code + "; " + string.Join(",", environment.Violations));
        }
    }

    [Theory]
    [InlineData("sibling")]
    [InlineData("container")]
    [InlineData("extra_export")]
    [InlineData("reorder")]
    public async Task ExportRepairCannotExpandUnrelatedPermissions(string mutation)
    {
        var (plan, catalog) = await RetainedFailure();
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var repaired = Clone(plan); ExportChecks(repaired);
        switch (mutation)
        {
            case "sibling": repaired.Root.Tasks.Single(t => t.Id == "review").Objective = "Other work"; break;
            case "container": repaired.Root.Tasks.Single(t => t.Id == "checks").MaxConcurrency++; break;
            case "extra_export": repaired.Root.Tasks.Single(t => t.Id == "checks").Branches[0].Outputs.Add(new("unrelated", PlanningCorpus.Number(42))); break;
            case "reorder": repaired.Root.Tasks.Reverse(); break;
        }
        Assert.Contains(TaskPlanRevisions.Validate(plan, repaired, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    private static TaskPlan NestedFailure() => new() { Root = new() { Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Outer scope", Body = new()
        { Tasks = [new() { Id = "inner", Kind = "sequence", Objective = "Inner scope", Body = PlanningCorpus.Greeting().Root }] } }],
        Outputs = [new("result", new() { Kind = "object", Members = [new("message", PlanningCorpus.Business("output", "greet", "message")), new("fixed", PlanningCorpus.Number(7))] })] } };

    [Fact]
    public void NestedExportsPermitOnlyTheExplicitChainAndPreserveOtherCompositeMembers()
    {
        var plan = NestedFailure();
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, new()).Diagnostics);
        var repaired = Clone(plan);
        var outer = repaired.Root.Tasks[0]; var inner = outer.Body!.Tasks[0];
        // The inner scope already declares its export; only the outer boundary is missing.
        outer.Body.Outputs.Add(new("message", PlanningCorpus.Business("output", "inner", "message")));
        repaired.Root.Outputs[0].Value.Members[0].Value.Source = "outer";
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, new()).Diagnostics);
        repaired.Root.Outputs[0].Value.Members[1].Value.Number = 99;
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, repaired, scope));
        repaired.Root.Outputs[0].Value.Members[1].Value.Number = 7;
        inner.Body!.Outputs.Add(new("unused", PlanningCorpus.Business("output", "greet", "message")));
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, repaired, scope));
    }

    [Fact]
    public void ConditionalCounterpartsMustBeExplicitAndCannotRewriteTheOtherBranch()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "decide", Kind = "conditional", Objective = "Select a value", Condition = new() { Kind = "boolean", Boolean = true },
            Body = PlanningCorpus.Greeting().Root, Otherwise = new() }], Outputs = [new("result", PlanningCorpus.Business("output", "decide", "message"))] } };
        var failed = new TaskPlanCompiler().Compile(plan, new());
        Assert.Contains(failed.Diagnostics, d => d.Location == "/tasks/decide/otherwise/outputs/message");
        var scope = TaskPlanRevisions.Scope(plan, failed.Diagnostics); var repaired = Clone(plan);
        repaired.Root.Tasks[0].Otherwise!.Outputs.Add(new("message", PlanningCorpus.String("Explicit alternative")));
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, new()).Diagnostics);
        repaired.Root.Tasks[0].Body!.Outputs[0].Value.Port = "undeclared";
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, repaired, scope));
    }

    [Fact]
    public void CrossingAConditionalRequiresBothExplicitExportsWithoutInventingFallbacks()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "decide", Kind = "conditional", Objective = "Select a value", Condition = new() { Kind = "boolean", Boolean = true },
            Body = new() { Tasks = PlanningCorpus.Greeting().Root.Tasks }, Otherwise = new() }], Outputs = [new("result", PlanningCorpus.Business("output", "greet", "message"))] } };
        var failed = new TaskPlanCompiler().Compile(plan, new());
        Assert.Equal(2, failed.Diagnostics.Count(d => d.Code == "TASK_EXPORT_REQUIRED"));
        var scope = TaskPlanRevisions.Scope(plan, failed.Diagnostics); var repaired = Clone(plan);
        repaired.Root.Tasks[0].Body!.Outputs.Add(new("text", PlanningCorpus.Business("output", "greet", "message")));
        repaired.Root.Tasks[0].Otherwise!.Outputs.Add(new("text", PlanningCorpus.String("Explicit alternative")));
        repaired.Root.Outputs[0].Value.Source = "decide"; repaired.Root.Outputs[0].Value.Port = "text";
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, new()).Diagnostics);
        repaired.Root.Tasks[0].Otherwise!.Outputs.Clear();
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, repaired, scope));
        Assert.NotEmpty(new TaskPlanCompiler().Compile(repaired, new()).Diagnostics);
    }

    [Fact]
    public async Task RecoveredRepairKeepsBaselineChoicesAndBudgetsAfterARejectedRewrite()
    {
        var runtime = new TestRuntime { Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = NestedFailure() } };
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var originalScope = state.RevisionScope.ToArray();
        runtime.Proposal.Plan!.Root.Tasks[0].Objective = "Unrelated rewrite";
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(originalScope, state.RevisionScope); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        runtime.Proposal.Plan = Clone(state.Plan!);
        runtime.Proposal.Plan.Root.Tasks[0].Body!.Outputs.Add(new("message", PlanningCorpus.Business("output", "inner", "message")));
        runtime.Proposal.Plan.Root.Outputs[0].Value.Members[0].Value.Source = "outer";
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Equal(1, runtime.Discoveries); Assert.NotNull(state.Yaml);
    }

    [Fact]
    public void UnmappedDiagnosticDoesNotAuthorizeEveryTask()
    {
        var plan = PlanningCorpus.Greeting();
        var scope = TaskPlanRevisions.Scope(plan, [new("UNKNOWN", "/", "Cannot locate")]);
        Assert.Empty(scope);
        var revised = Clone(plan); revised.Root.Tasks[0].Objective = "Other work";
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, revised, scope));
    }
}
