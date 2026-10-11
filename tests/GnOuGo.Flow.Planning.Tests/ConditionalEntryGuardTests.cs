using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConditionalEntryGuardTests
{
    private static TaskValue Flag(bool value) => new() { Kind = "boolean", Boolean = value };
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
    private static TaskValue Text(string value) => new() { Kind = "string", Text = value };
    private static TaskValue P(string op, params TaskValue[] values) => new() { Kind = "predicate", Predicate = op, Items = values.ToList() };
    private static TaskValue Required() => P("and", P("not_equal", Input("reference"), new()), P("and", P("equal", Input("action"), Text("enable")), Input("authorized")));
    private static TaskPlan Plan(bool corrected = false, bool otherwise = false) => new()
    {
        Inputs = [new() { Name = "ready", Type = new() { Kind = "boolean" } }, new() { Name = "reference", Type = new() { Kind = "string", Nullable = true } },
            new() { Name = "action", Type = new() { Kind = "string", Nullable = true } }, new() { Name = "authorized", Type = new() { Kind = "boolean" } }],
        Root = new() { Tasks = [new() { Id = "route", Kind = "conditional", Objective = "Use an available authorized resource",
            Condition = corrected ? otherwise ? P("not", Required()) : P("and", Input("ready"), Required()) : Input("ready"),
            Body = otherwise ? new() : Action(), Otherwise = otherwise ? Action() : new() }],
            Always = [new() { Id = "cleanup", Kind = "value", Objective = "Finish the workflow", Outputs = [new("cleaned", Flag(true))] }] }
    };
    private static TaskScope Action() => new() { Tasks = [new() { Id = "act", Objective = "Use the original resource", Operation = "act", Requires = Required(), Inputs = [new("reference", Input("reference"))] }] };
    private static async Task<(WorkflowEngine Engine, PlanningCatalog Catalog, List<string?> Calls)> Environment()
    {
        var calls = new List<string?>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "perform", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"reference":{"type":["string","null"]}},"required":["reference"],"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"complete":{"type":"boolean"}},"required":["complete"],"additionalProperties":false}""") }],
            ToolHandlers = new() { ["perform"] = args => { calls.Add(args!["reference"]?.ToString()); return new() { Content = new JsonObject { ["complete"] = false } }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory }; var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        catalog.Capabilities.Single(c => c.Method == "perform").Id = "act";
        return (engine, catalog, calls);
    }
    private static PlanningSession State(TaskPlan plan, PlanningCatalog catalog)
    {
        var state = PlannerFixture.Session(); state.Plan = plan; state.Catalog = catalog; state.Requirements = new() { Summary = "Use available resources",
            Inputs = plan.Inputs, Outcomes = [new("work", "Use available authorized resources and finish")] };
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList(); return state;
    }

    [Theory]
    [InlineData(null, "enable", true)]
    [InlineData("exact", "follow", true)]
    [InlineData("exact", "enable", false)]
    [InlineData("exact", "enable", true)]
    public async Task LocalRequiresIsAuthoritativeWithoutConditionalProof(string? reference, string action, bool authorized)
    {
        var (engine, catalog, calls) = await Environment(); var plan = Plan();
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        var result = await Execute(engine, catalog, plan, reference, action, authorized);
        var permitted = reference is not null && action == "enable" && authorized;
        Assert.Equal(permitted, result.Success); Assert.Equal(permitted ? 1 : 0, calls.Count);
        if (!permitted) Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains(result.StepResults, r => r.Output?["cleaned"]?.GetValue<bool>() == true);
    }

    [Theory]
    [InlineData(false, null, null, false, 0)][InlineData(false, "exact", "follow", true, 0)]
    [InlineData(false, "exact", "enable", false, 0)][InlineData(false, "exact", "enable", true, 1)]
    [InlineData(true, null, null, false, 0)][InlineData(true, "exact", "enable", true, 1)]
    public async Task CorrectGuardExecutesExactlyThePermittedOriginalAction(bool otherwise, string? reference, string? action, bool authorized, int expected)
    {
        var (engine, catalog, calls) = await Environment(); var plan = Plan(corrected: true, otherwise: otherwise);
        var result = await Execute(engine, catalog, plan, reference, action, authorized);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(expected, calls.Count);
        Assert.All(calls, value => Assert.Equal(reference, value));
        Assert.Contains(result.StepResults, r => r.Output?["cleaned"]?.GetValue<bool>() == true);
    }

    [Theory]
    [InlineData("conjunction")][InlineData("common_disjunction")][InlineData("negation")][InlineData("alias")][InlineData("export")][InlineData("nested")][InlineData("group")][InlineData("loop")]
    public async Task ExplicitFactsSurviveScopeAndBooleanComposition(string variant)
    {
        var (_, catalog, _) = await Environment(); var plan = Plan(corrected: true); var route = plan.Root.Tasks[0];
        if (variant == "common_disjunction") route.Condition = P("or", P("and", Required(), Input("ready")), P("and", Required(), P("not", Input("ready"))));
        if (variant == "negation") route.Condition = P("not", P("or", P("equal", Input("reference"), new()), P("or", P("not_equal", Input("action"), Text("enable")), P("not", Input("authorized")))));
        if (variant == "alias") { plan.Root.Tasks.Insert(0, new() { Id = "copy", Kind = "value", Objective = "Retain the original authorization", Outputs = [new("allowed", Input("authorized"))] }); route.Body!.Tasks[0].Requires = P("and", P("not_equal", Input("reference"), new()), P("and", P("equal", Input("action"), Text("enable")), Output("copy", "allowed"))); }
        if (variant == "export") { plan.Root.Tasks.Insert(0, new() { Id = "scope", Kind = "sequence", Objective = "Export the original flag", Body = new() { Outputs = [new("flag", Input("authorized"))] } }); route.Body!.Tasks[0].Requires = Output("scope", "flag"); }
        if (variant == "nested") { route.Condition = Input("authorized"); var nested = Plan(corrected: true).Root.Tasks[0]; nested.Id = "nested"; route.Body = new() { Tasks = [nested] }; }
        if (variant == "group") { var action = route.Body!.Tasks[0]; plan.Groups.Add(new() { Id = "work", Inputs = plan.Inputs, Body = new() { Tasks = [action] } }); route.Body = new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "work", Objective = "Use the bound resource", Inputs = plan.Inputs.Select(i => new TaskOutput(i.Name, Input(i.Name))).ToList() }] }; }
        if (variant == "loop") { var action = route.Body!.Tasks[0]; route.Body = new() { Tasks = [new() { Id = "loop", Kind = "foreach", Objective = "Repeat the guarded work", Items = new() { Kind = "array", Items = [Text("first"), Text("second")] }, Body = new() { Tasks = [action] } }] }; }
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LaterResultAssertionsRemainRuntimeChecksButDoNotHideKnownConjuncts(bool missingEntryGuard)
    {
        var (engine, catalog, calls) = await Environment(); var plan = Plan(corrected: true); var route = plan.Root.Tasks[0];
        var action = route.Body!.Tasks[0];
        route.Body.Tasks.Insert(0, new() { Id = "observe", Objective = "Observe a new result", Operation = "act", Inputs = [new("reference", Input("reference"))] });
        action.Requires = P("and", Input("authorized"), Output("observe", "complete"));
        if (missingEntryGuard) route.Condition = Input("ready");
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        var result = await Execute(engine, catalog, plan, "observed", "enable", true);
        Assert.False(result.Success); Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.Single(calls);
        Assert.Contains(result.StepResults, r => r.Output?["cleaned"]?.GetValue<bool>() == true);
        action.Requires = P("or", Input("authorized"), Output("observe", "complete")); route.Condition = Input("ready");
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
    }

    [Fact]
    public async Task CorrectProposalReachesReviewInOneCallWithRuntimeGuardsUnchanged()
    {
        var (engine, catalog, _) = await Environment(); var plan = Plan(); var state = State(plan, catalog);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var runtime = new TestRuntime(engine) { Proposal = new() { Plan = plan, Requirements = state.Requirements } };
        state.Plan = null; state.Diagnostics.Clear(); state.RevisionScope.Clear();
        var reviewed = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, reviewed.Status); Assert.Single(runtime.Calls); Assert.Equal(0, reviewed.ReplanAttempts);
        Assert.Equal(before, JsonSerializer.Serialize(reviewed.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains("Required condition:", reviewed.Yaml!);
    }

    [Fact]
    public async Task PermissionDenialAndRequiredConditionsCannotBeRepairedAway()
    {
        var (_, catalog, _) = await Environment(); var state = State(Plan(), catalog);
        var copy = PlannerFixture.Clone(state).Plan!; copy.Root.Tasks[0].Body!.Tasks[0].Requires = Flag(true);
        Assert.Contains(TaskPlanRevisions.Validate(state.Plan, copy, state.RevisionScope, catalog), d => d.Location == "/tasks/act/requires");
        catalog.Policy.DeniedCapabilityIds.Add("act");
        Assert.Contains(new TaskPlanCompiler().Compile(Plan(corrected: true), catalog).Diagnostics, d => d.Code == "TASK_OPERATION_DENIED");
    }

    [Fact]
    public async Task RetiredGuardRepairRequiresRevisionWithoutRedispatch()
    {
        var (_, catalog, _) = await Environment(); var state = State(Plan(), catalog);
        state.Diagnostics = [new("TASK_CONDITIONAL_REQUIREMENT", "/tasks/route/condition", "Retained legacy proof diagnostic")];
        state.PendingCall = new() { Id = "issued", Purpose = "replan", Request = new() { Prompt = "original", Model = "test", StructuredOutputSchema = new JsonObject { ["type"] = "object" } } };
        state.ModelCalls = 2; state.ReplanAttempts = 1;
        var saved = JsonSerializer.Serialize(state.PendingCall, PlanningJsonContext.Default.PlanningModelCall);
        var runtime = new TestRuntime(); var restored = PlannerFixture.Clone(state);
        var stopped = await new HybridWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status); Assert.Empty(runtime.Calls);
        Assert.Equal(2, stopped.ModelCalls); Assert.Equal(1, stopped.ReplanAttempts);
        Assert.Equal(saved, JsonSerializer.Serialize(stopped.PendingCall, PlanningJsonContext.Default.PlanningModelCall));
        Assert.Contains(stopped.Diagnostics, d => d.Code == "PLANNING_REVISION_REQUIRED");
    }

    private static async Task<RunResult> Execute(WorkflowEngine engine, PlanningCatalog catalog, TaskPlan plan, string? reference, string? action, bool authorized)
    {
        var request = new PlanningRequest { Options = new() { ["compilation_profile"] = TaskPlanCompiler.CompactProfile } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request); Assert.Empty(compiled.Diagnostics);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "guards", true, true)));
        var result = await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["ready"] = true, ["reference"] = reference, ["action"] = action, ["authorized"] = authorized }, PlannerFixture.Ct);
        return result;
    }
}
