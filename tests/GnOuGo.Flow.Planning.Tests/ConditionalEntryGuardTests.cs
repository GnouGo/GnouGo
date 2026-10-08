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
    [InlineData("reference")][InlineData("action")][InlineData("authorized")]
    public async Task MissingEntryFactIsDiagnosedAtItsControllingCondition(string missing)
    {
        var (_, catalog, _) = await Environment(); var plan = Plan();
        var conditions = new[] { P("not_equal", Input("reference"), new()), P("equal", Input("action"), Text("enable")), Input("authorized") };
        plan.Root.Tasks[0].Condition = conditions.Where((_, i) => i != Array.IndexOf(new[] { "reference", "action", "authorized" }, missing)).Aggregate((a, b) => P("and", a, b));
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Null(compiled.Graph);
        var diagnostic = Assert.Single(compiled.Diagnostics); Assert.Equal("TASK_CONDITIONAL_REQUIREMENT", diagnostic.Code);
        Assert.Equal("/tasks/route/condition", diagnostic.Location); Assert.Equal("/tasks/act/requires", diagnostic.Rule);
        Assert.Contains(missing, diagnostic.Message); Assert.Contains("does not establish", diagnostic.Message);
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
        if (missingEntryGuard) { Assert.Contains(compiled.Diagnostics, d => d.Code == "TASK_CONDITIONAL_REQUIREMENT" && d.Message.Contains("authorized", StringComparison.Ordinal)); return; }
        Assert.Empty(compiled.Diagnostics);
        var result = await Execute(engine, catalog, plan, "observed", "enable", true);
        Assert.False(result.Success); Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.Single(calls);
        Assert.Contains(result.StepResults, r => r.Output?["cleaned"]?.GetValue<bool>() == true);
        action.Requires = P("or", Input("authorized"), Output("observe", "complete")); route.Condition = Input("ready");
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task IsolatedRepairIsExactAtomicAndSurvivesRestart(bool otherwise)
    {
        var (_, catalog, _) = await Environment(); var state = State(Plan(otherwise: otherwise), catalog);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var proposed = TaskPlanCompiler.ConditionalRepairs(state.Plan!, catalog)["/tasks/route/condition"]!;
        var definitions = PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.AsObject();
        var slot = Assert.Single(PlanningRepairPatch.Slots(state, definitions));
        var request = RepairPatchTests.Request(state); var restored = PlannerFixture.Clone(state);
        var schema = slot.ValueSchema.DeepClone().AsObject(); schema["$defs"] = definitions.DeepClone();
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposed, PlanningJsonContext.Default.TaskValue), schema, schema), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["kind"] = "boolean", ["boolean"] = false }, schema));
        Assert.True(PlanningRepairPatch.Verify(restored, request));
        var context = PlanningRepairContext.Select(restored);
        Assert.Contains("act", context.Tasks); Assert.DoesNotContain("act", context.EditableTasks);
        var candidate = PlannerFixture.Clone(state).Plan!; candidate.Root.Tasks[0].Condition = proposed;
        var response = TestRuntime.PatchResponse(request, restored, candidate).Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
        var fixedPlan = PlanningRepairPatch.Apply(restored, response.Patch!, request);
        Assert.Empty(new TaskPlanCompiler().Compile(fixedPlan, catalog).Diagnostics);
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(TaskPlanRevisions.Validate(state.Plan, fixedPlan, state.RevisionScope, catalog));
        fixedPlan.Root.Tasks[0].Condition = Flag(false);
        Assert.Contains(TaskPlanRevisions.Validate(state.Plan, fixedPlan, state.RevisionScope, catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
        restored.Plan!.Root.Tasks[0].Objective = "Changed authority";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(restored, request));
    }

    [Theory]
    [InlineData("subsequent_operation")][InlineData("active_alternative")][InlineData("cleanup")][InlineData("contradiction")]
    public async Task SharedOrContradictoryEntryRequiresRevisionBeforeRepairDispatch(string variant)
    {
        var (engine, catalog, _) = await Environment(); var plan = Plan(); var route = plan.Root.Tasks[0];
        var follow = new PlanTask { Id = "observe_again", Objective = "Observe a fresh result", Operation = "act", Inputs = [new("reference", Input("reference"))] };
        if (variant == "subsequent_operation") route.Body!.Tasks.Add(follow);
        if (variant == "active_alternative") route.Otherwise!.Tasks.Add(follow);
        if (variant == "cleanup") route.Body!.Always.Add(follow);
        if (variant == "contradiction") route.Condition = P("not", Input("authorized"));
        var state = State(plan, catalog); Assert.NotEmpty(TaskPlanRevisions.UnrepairableRequirements(state));
        var runtime = new TestRuntime(engine) { Proposal = new() { Plan = plan, Requirements = state.Requirements } };
        state.Plan = null; state.Diagnostics.Clear(); state.RevisionScope.Clear();
        var stopped = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status); Assert.Single(runtime.Calls); Assert.Equal(0, stopped.ReplanAttempts);
        Assert.Contains(stopped.Diagnostics, d => d.Code == "REVISION_REQUIRED");
    }

    [Fact]
    public async Task CorrectProposalReachesReviewInOneCallWithRuntimeGuardsUnchanged()
    {
        var (engine, catalog, _) = await Environment(); var plan = Plan(corrected: true); var state = State(plan, catalog);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var runtime = new TestRuntime(engine) { Proposal = new() { Plan = plan, Requirements = state.Requirements } };
        state.Plan = null; state.Diagnostics.Clear(); state.RevisionScope.Clear();
        var reviewed = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, reviewed.Status); Assert.Single(runtime.Calls); Assert.Equal(0, reviewed.ReplanAttempts);
        Assert.Equal(before, JsonSerializer.Serialize(reviewed.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains("Required condition:", reviewed.Yaml!);
    }

    [Theory]
    [InlineData("declared_nonnull", true)][InlineData("different_flag", false)][InlineData("different_producer", false)]
    [InlineData("mixed_or", false)][InlineData("opposite", false)][InlineData("nonnull_from_equality", true)][InlineData("enum", true)]
    public async Task OnlyAuthoritativeFactsEstablishARequirement(string variant, bool valid)
    {
        var (_, catalog, _) = await Environment(); var plan = Plan(corrected: true); var route = plan.Root.Tasks[0];
        if (variant == "declared_nonnull") { plan.Inputs.Single(i => i.Name == "reference").Type.Nullable = false; route.Condition = P("and", P("equal", Input("action"), Text("enable")), Input("authorized")); }
        if (variant == "nonnull_from_equality") route.Condition = P("and", P("equal", Input("reference"), Text("observed")), P("and", P("equal", Input("action"), Text("enable")), Input("authorized")));
        if (variant == "enum") { plan.Inputs.Single(i => i.Name == "action").Type = new() { Kind = "string", Enum = ["enable"] }; route.Condition = P("and", P("not_equal", Input("reference"), new()), Input("authorized")); }
        if (variant == "different_flag") route.Condition = Input("ready");
        if (variant == "different_producer")
        {
            var first = new PlanTask { Id = "first", Objective = "Observe the first flag", Operation = "act", Inputs = [new("reference", Input("reference"))] };
            var second = new PlanTask { Id = "second", Objective = "Observe another flag", Operation = "act", Inputs = [new("reference", Input("reference"))] };
            plan.Root.Tasks.InsertRange(0, [first, second]); route.Condition = Output("first", "complete"); route.Body!.Tasks[0].Requires = Output("second", "complete");
        }
        if (variant == "mixed_or") route.Condition = P("or", Required(), Input("ready"));
        if (variant == "opposite") route.Condition = P("not", Input("authorized"));
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        Assert.Equal(valid, findings.Count == 0);
        if (!valid) Assert.Contains(findings, d => d.Code == "TASK_CONDITIONAL_REQUIREMENT");
        if (variant == "opposite") Assert.Contains(findings, d => d.Message.Contains("establishes its opposite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GroupCallsAndNestedLoopAliasesDoNotShareFacts()
    {
        var (_, catalog, _) = await Environment(); var plan = Plan(corrected: true); var route = plan.Root.Tasks[0];
        plan.Groups.Add(new() { Id = "work", Inputs = plan.Inputs, Body = route.Body! });
        route.Body = new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "work", Objective = "Use the supplied authorization",
            Inputs = plan.Inputs.Select(i => new TaskOutput(i.Name, Input(i.Name == "authorized" ? "ready" : i.Name))).ToList() }] };
        route.Condition = Required();
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_CONDITIONAL_REQUIREMENT");

        plan = Plan(); route = plan.Root.Tasks[0]; route.Condition = new() { Kind = "item" }; route.Body!.Tasks[0].Requires = Output("outer_flag", "allowed");
        plan.Root.Tasks = [new() { Id = "outer", Kind = "foreach", Objective = "Keep each outer flag", Items = new() { Kind = "array", Items = [Flag(false)] }, Body = new() { Tasks = [
            new() { Id = "outer_flag", Kind = "value", Objective = "Retain the outer flag", Outputs = [new("allowed", new() { Kind = "item" })] },
            new() { Id = "inner", Kind = "foreach", Objective = "Check a distinct inner flag", Items = new() { Kind = "array", Items = [Flag(true)] }, Body = new() { Tasks = [route] } }] } }];
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_CONDITIONAL_REQUIREMENT");
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task NullableBooleanInequalityDoesNotProveTheOtherBooleanValue(bool value)
    {
        var (_, catalog, _) = await Environment(); var plan = Plan(); var route = plan.Root.Tasks[0];
        plan.Inputs.Single(i => i.Name == "authorized").Type.Nullable = true;
        route.Condition = P("not_equal", Input("authorized"), Flag(value));
        route.Body!.Tasks[0].Requires = P("equal", Input("authorized"), Flag(!value));
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_CONDITIONAL_REQUIREMENT");
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

    [Theory]
    [InlineData("open_resource", "ressource", "permission")]
    [InlineData("perform_action", "handle", "allowed")]
    public async Task RetainedOptionalActionFailureDoesNotDependOnOperationOrFieldNames(string operation, string resource, string authorization)
    {
        var (_, catalog, _) = await Environment(); var tree = JsonSerializer.SerializeToNode(Plan(), PlanningJsonContext.Default.TaskPlan)!;
        var names = new Dictionary<string, string> { ["act"] = operation, ["reference"] = resource, ["authorized"] = authorization };
        Rename(tree); catalog.Capabilities.Single(c => c.Id == "act").Id = operation;
        var schema = catalog.Capabilities.Single(c => c.Id == operation).InputSchema;
        schema["properties"]![resource] = schema["properties"]!["reference"]!.DeepClone(); schema["properties"]!.AsObject().Remove("reference"); schema["required"] = new JsonArray(resource);
        var plan = tree.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        Assert.Equal(3, findings.Count); Assert.All(findings, d => Assert.Equal("TASK_CONDITIONAL_REQUIREMENT", d.Code));
        Assert.Contains(findings, d => d.Message.Contains(resource, StringComparison.Ordinal));
        Assert.Contains(findings, d => d.Message.Contains(authorization, StringComparison.Ordinal));
        void Rename(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var (key, value) in obj.ToArray())
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && names.TryGetValue(text, out var replacement)) obj[key] = replacement; else Rename(value);
            else if (node is JsonArray items) foreach (var child in items) Rename(child);
        }
    }

    private static async Task<RunResult> Execute(WorkflowEngine engine, PlanningCatalog catalog, TaskPlan plan, string? reference, string? action, bool authorized)
    {
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        return await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["ready"] = true, ["reference"] = reference, ["action"] = action, ["authorized"] = authorized }, PlannerFixture.Ct);
    }
}
