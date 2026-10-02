using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComposedOutcomeTests
{
    internal static PlanningSession Recorded(string name) => Recording(name)["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
    internal static JsonNode Recording(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ContractAwarePlanning", "retained-" + name + ".json")))!;

    [Fact]
    public void RetainedProductFailureRequiresProducerEffectsAndPerItemCoverageWithoutExecutableEdits()
    {
        var state = Recorded("products"); var originalPlan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Diagnostics);
        Assert.Equal(12, PlanningOutcomeValidation.Findings(state).Count);
        state.OutcomeVersion = 2;
        Assert.Contains(PlanningOutcomeValidation.Findings(state), f => f.Code == "OUTCOME_EFFECT_UNDECLARED");
        // Explicit test producer contracts, not inference from names in production.
        foreach (var capability in state.Catalog!.Capabilities)
            capability.EffectKind = capability.Method switch
            {
                "browser_get_content" or "browser_wait" or "document_get_policy" => "read",
                "document_write" => "write", "browser_click_text" or "browser_fill" => "execute", _ => capability.EffectKind
            };
        state.Requirements!.Outcomes = state.Requirements.Outcomes.Select(o => o.Id == "o_visit_extract" ? o with { Coverage = "each_item" } : o with { Coverage = "once" }).ToList();
        state.OutcomeBindings = state.OutcomeBindings!.Select(b => b.OutcomeId == "o_visit_extract" ? b with { ForEachTaskId = "scrape_products" } : b).ToList();
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        Assert.Equal(originalPlan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void RetainedAgentFailureKeepsBothRequiredDiagnosticsAndValidOutputConstraints()
    {
        var state = Recorded("copilot");
        var final = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!);
        Assert.Equal(new[] { "TASK_INPUT_TYPE", "AGENT_SCOPE_DYNAMIC" }, final.Diagnostics.Select(d => d.Code));
        var repairedSchemaPlan = Recording("copilot")["proposals"]!.AsArray().Single(p => p!["revision"]!.GetValue<int>() == 10)!["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(new[] { "TASK_INPUT_TYPE", "AGENT_SCOPE_DYNAMIC" }, new TaskPlanCompiler().Compile(repairedSchemaPlan, state.Catalog!).Diagnostics.Select(d => d.Code));
        var work = state.Plan!.Root.Tasks.Single(t => t.Id == "run_copilot_review_agent");
        var clone = state.Plan.Root.Tasks.Single(t => t.Id == "clone_repository_once");
        work.Inputs.Single(i => i.Name == "workspace").Value.Text = clone.Inputs.Single(i => i.Name == "targetDirectory").Value.Text;
        var workspace = work.Inputs.Single(i => i.Name == "workspace").Value;
        workspace.Kind = "string"; workspace.Source = null; workspace.Port = null;
        Assert.Equal("TASK_INPUT_TYPE", Assert.Single(new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics).Code);
        Assert.Equal(6, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PerItemSupportsEmptyCollectionsAndParallelScopesButDoesNotProveOnce(bool parallel, bool empty)
    {
        var state = await State(); var body = state.Plan!.Root;
        state.Plan.Root = new() { Tasks = [new() { Id = "batch", Kind = "foreach", Objective = "Process each entry", Parallel = parallel,
            Items = empty ? new() { Kind = "array" } : PlanningCorpus.Business("input", "entries"), Body = body }] };
        state.Plan.Inputs = [new() { Name = "entries", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }];
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Coverage = "each_item" };
        state.OutcomeBindings = [new("work", ["batch"], []) { ForEachTaskId = "batch" }];
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Coverage = "once" };
        state.OutcomeBindings = [new("work", ["batch"], [])];
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code is "OUTCOME_PATH_INCOMPLETE" or "OUTCOME_OPERATION_REQUIRED");
    }

    [Theory]
    [InlineData("unknown", "OUTCOME_EFFECT_UNDECLARED")]
    [InlineData("transform", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("denied", "OUTCOME_OPERATION_UNAVAILABLE")]
    [InlineData("disconnected", "OUTCOME_SUPPORT_DISCONNECTED")]
    [InlineData("output", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("placement", "OUTCOME_PLACEMENT_INVALID")]
    public async Task SupportingComputationCannotManufactureExecution(string defect, string expected)
    {
        var state = await State();
        switch (defect)
        {
            case "unknown": state.Catalog!.Capabilities.Single(c => c.Id == "external").EffectKind = "unknown"; break;
            case "transform": state.Plan!.Root.Tasks[0].Kind = "transform"; break;
            case "denied": state.Catalog!.Policy.DeniedCapabilityIds.Add("external"); break;
            case "disconnected": state.Plan!.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Other work" }); state.OutcomeBindings![0].TaskIds.Add("unrelated"); break;
            case "output": state.OutcomeBindings![0].TaskIds.Clear(); break;
            case "placement": state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Always = true }; break;
        }
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == expected);
    }

    [Fact]
    public async Task OutcomeMappingRepairHasItsOwnAtomicAuthorityAndInvalidatesApproval()
    {
        var state = await State(); state.OutcomeBindings![0].TaskIds.Clear();
        state.Diagnostics = PlanningOutcomeValidation.Findings(state); state.RevisionScope = ["/outcomeBindings/work"];
        var request = new PlanningPrompt(state).Request();
        Assert.Equal(5, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
        var slot = Assert.Single(PlanningRepairPatch.Slots(state, PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.AsObject()));
        var patch = new RepairPatch { Edits = [new() { Slot = slot.Id, Action = "replace", Value = JsonNode.Parse("""{"taskIds":["perform"],"outputs":["report"],"forEachTaskId":null}""") }] };
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var candidate = PlanningRepairPatch.Apply(state, patch, request, out var bindings);
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        state.Graph = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Graph; state.Yaml = "reviewed fixture";
        var beforeHash = state.ComputeArtifactHash(); state.Plan = candidate; state.OutcomeBindings = bindings;
        Assert.Empty(PlanningOutcomeValidation.Findings(state)); Assert.NotEqual(beforeHash, state.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Apply(state, patch, request));
    }

    internal static async Task<PlanningSession> State()
    {
        var state = PlannerFixture.Session(); state.IntentVersion = 1; state.OutcomeVersion = 2;
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject();
        state.Catalog.Capabilities.Add(new() { Id = "external", Kind = "tool", StepType = "mcp.call", Server = "arbitrary", Method = "omega", Version = "1", EffectKind = "write", InputSchema = empty, OutputSchema = empty });
        state.Requirements = new() { Summary = "Perform requested work", Inputs = [], Outcomes = [PlanningOutcomeTests.Outcome("work") with { Coverage = "once" }] };
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "perform", Objective = "Perform requested work", Operation = "external" }], Outputs = [new("report", PlanningCorpus.String("done"))] } };
        state.OutcomeBindings = [new("work", ["perform"], ["report"])]; return state;
    }

    [Fact]
    public async Task SharedPrerequisiteDoesNotConnectAnUnrelatedSibling()
    {
        var state = await State();
        state.Plan!.Root.Tasks.Insert(0, new() { Id = "seed", Kind = "value", Objective = "Shared data" });
        state.Plan.Root.Tasks[1].DependsOn = ["seed"];
        state.Plan.Root.Tasks.Add(new() { Id = "sibling", Kind = "value", Objective = "Other work", DependsOn = ["seed"] });
        state.OutcomeBindings![0].TaskIds.Add("sibling");
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_SUPPORT_DISCONNECTED");
    }

    [Theory]
    [InlineData("group", false)]
    [InlineData("group", true)]
    [InlineData("loop", false)]
    [InlineData("loop", true)]
    public async Task ScopeInputsAndIterationControlConnectOnlyTheirActualProducers(string boundary, bool nested)
    {
        var state = await State(); var operation = state.Plan!.Root.Tasks[0];
        state.Catalog!.Capabilities.Single(c => c.Id == "external").InputSchema = JsonNode.Parse("""{"type":"object","properties":{"payload":{"type":"string"}},"required":["payload"],"additionalProperties":false}""")!.AsObject();
        var seed = new PlanTask { Id = "seed", Kind = "value", Objective = "Prepare the requested data", Outputs = [new("payload", boundary == "group" ? PlanningCorpus.String("content") : new() { Kind = "array", Items = [PlanningCorpus.String("content")] })] };
        var body = new TaskScope { Tasks = [operation] };
        if (nested) body = new() { Tasks = [new() { Id = "nested", Kind = "sequence", Objective = "Nested work", Body = body }] };
        PlanTask container;
        if (boundary == "group")
        {
            operation.Inputs = [new("payload", PlanningCorpus.Business("input", "payload"))];
            state.Plan.Groups = [new() { Id = "worker", Inputs = [new() { Name = "payload", Type = new() { Kind = "string" } }], Body = body }];
            container = new() { Id = "invoke", Kind = "call", Objective = "Perform grouped work", Group = "worker", Inputs = [new("payload", PlanningCorpus.Business("output", "seed", "payload"))] };
        }
        else
        {
            // Even an operation using a literal is controlled by the collection source.
            operation.Inputs = [new("payload", PlanningCorpus.String("content"))];
            container = new() { Id = "invoke", Kind = "foreach", Objective = "Perform each item", Parallel = nested, Items = PlanningCorpus.Business("output", "seed", "payload"), Body = body };
            state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Coverage = "each_item" };
        }
        state.Plan.Root.Tasks = [seed, container];
        state.OutcomeBindings = [new("work", ["seed", "invoke"], ["report"]) { ForEachTaskId = boundary == "loop" ? "invoke" : null }];
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics);
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.Plan.Root.Tasks.Add(new() { Id = "sibling", Kind = "value", Objective = "Unrelated sibling", Outputs = [new("payload", PlanningCorpus.Business("output", "seed", "payload"))] });
        state.OutcomeBindings[0].TaskIds.Add("sibling");
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_SUPPORT_DISCONNECTED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnusedArgumentsAndUnreachableGroupCallsCannotLendSupport(bool unreachable)
    {
        var state = await State(); var operation = state.Plan!.Root.Tasks[0];
        var seed = new PlanTask { Id = "seed", Kind = "value", Objective = "Other data", Outputs = [new("payload", PlanningCorpus.String("unrelated"))] };
        state.Plan.Groups = [new() { Id = "worker", Inputs = [new() { Name = "payload", Type = new() { Kind = "string" } }], Body = new() { Tasks = [operation] } }];
        var call = new PlanTask { Id = "invoke", Kind = "call", Objective = "Perform work", Group = "worker", Inputs = [new("payload", PlanningCorpus.Business("output", "seed", "payload"))] };
        state.Plan.Root.Tasks = [seed, call];
        if (unreachable)
        {
            state.Catalog!.Capabilities.Single(c => c.Id == "external").InputSchema = JsonNode.Parse("""{"type":"object","properties":{"payload":{"type":"string"}},"required":["payload"],"additionalProperties":false}""")!.AsObject();
            operation.Inputs = [new("payload", PlanningCorpus.Business("input", "payload"))];
            call.Inputs = [new("payload", PlanningCorpus.String("used"))];
            state.Plan.Root.Tasks.Add(new() { Id = "skipped", Kind = "conditional", Objective = "Unreachable work", Condition = new() { Kind = "boolean", Boolean = false },
                Body = new() { Tasks = [new() { Id = "unreachable", Kind = "call", Objective = "Unused invocation", Group = "worker", Inputs = [new("payload", PlanningCorpus.Business("output", "seed", "payload"))] }] }, Otherwise = new() });
        }
        state.OutcomeBindings = [new("work", ["seed", "invoke"], ["report"])];
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics);
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_SUPPORT_DISCONNECTED");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NestedGroupPerItemCoverageChecksEveryConditionalPathAndCleanupPlacement(bool complete, bool cleanup)
    {
        var state = await State(); var operation = state.Plan!.Root.Tasks[0];
        var first = new TaskScope { Tasks = cleanup ? [] : [operation], Always = cleanup ? [operation] : [] };
        var second = new TaskScope();
        if (complete)
        {
            var other = new PlanTask { Id = "other", Objective = operation.Objective, Operation = operation.Operation };
            if (cleanup) second.Always.Add(other); else second.Tasks.Add(other);
        }
        state.Plan.Groups = [new() { Id = "worker", Body = new() { Tasks = [new() { Id = "branch", Kind = "conditional", Objective = "Choose", Condition = PlanningCorpus.Business("input", "enabled"), Body = first, Otherwise = second }] } }];
        state.Plan.Root = new() { Tasks = [new() { Id = "batch", Kind = "foreach", Objective = "Each", Items = new() { Kind = "array" }, Body = new() { Tasks = [new() { Id = "invoke", Kind = "call", Objective = "Process", Group = "worker" }] } }] };
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Coverage = "each_item", Always = cleanup };
        state.OutcomeBindings = [new("work", ["batch"], []) { ForEachTaskId = "batch" }];
        var findings = PlanningOutcomeValidation.Findings(state);
        if (complete) Assert.Empty(findings); else Assert.Contains(findings, d => d.Code == "OUTCOME_PATH_INCOMPLETE");
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("coverage")]
    public async Task MappingRepairRejectsInvalidCandidatesAtomicallyAfterRestart(string defect)
    {
        var state = await State(); state.OutcomeBindings![0].TaskIds.Clear();
        state.Plan!.Root.Tasks.Add(new() { Id = "sibling", Kind = "value", Objective = "Preserve other work", Outputs = [new("value", PlanningCorpus.String("unchanged"))] });
        state.Diagnostics = PlanningOutcomeValidation.Findings(state); state.RevisionScope = ["/outcomeBindings/work"];
        var request = new PlanningPrompt(state).Request();
        state = PlannerFixture.Clone(state); var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var value = JsonNode.Parse("""{"taskIds":["perform"],"outputs":["report"],"forEachTaskId":null}""")!;
        if (defect == "unrelated") value["taskIds"]!.AsArray().Add("sibling");
        if (defect == "duplicate") value["taskIds"]!.AsArray().Add("perform");
        if (defect == "missing") value["taskIds"]!.AsArray().Clear();
        if (defect == "coverage") value["forEachTaskId"] = "invented";
        var patch = new RepairPatch { Edits = [new() { Slot = "s0", Action = "replace", Value = value }] };
        Assert.ThrowsAny<Exception>(() => PlanningRepairPatch.Apply(state, patch, request, out _));
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task InterruptedMappingRepairReplaysIssuedVersionFiveWithoutResettingBudgets()
    {
        var state = await State(); var plan = state.Plan; state.Plan = null; state.OutcomeBindings = null;
        var runtime = new TestRuntime { Proposal = new() { Plan = plan, OutcomeBindings = [new("work", [], ["report"])] } };
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(new[] { "/outcomeBindings/work" }, state.RevisionScope); Assert.Null(state.ApprovedHash);
        runtime.Respond = (_, _) => throw new IOException("Retain issued repair");
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        var request = state.PendingCall!.Request; var schema = request.StructuredOutputSchema!.ToJsonString();
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        state = PlannerFixture.Clone(state); state.Status = PlanningStatus.Generating;
        runtime.Respond = (_, _) => new() { Json = JsonNode.Parse("""{"discoveryRequests":null,"clarifications":null,"patch":{"edits":[{"slot":"s0","action":"replace","value":{"taskIds":["perform"],"outputs":["report"],"forEachTaskId":null}}]}}""") };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Equal(request.ClientRequestId, runtime.Calls[^1].ClientRequestId);
        Assert.Equal(schema, runtime.Calls[^1].StructuredOutputSchema!.ToJsonString());
        Assert.Equal("write", state.Requirements!.Outcomes.Single().Execution); PlanningArtifactApproval.Verify(state);
    }
}
