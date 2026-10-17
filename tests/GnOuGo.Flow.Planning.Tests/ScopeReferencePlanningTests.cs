using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ScopeReferencePlanningTests
{
    private static TaskValue Output(string source, string port = "payload") => new() { Kind = "output", Source = source, Port = port };
    private static PlanTask Value(string id) => new() { Id = id, Kind = "value", Objective = "Observe data",
        Outputs = [new("payload", new() { Kind = "string", Text = "exact observation" })] };
    private static JsonObject Retained(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ScopeReferences", name + ".json")))!.AsObject();
    private static TaskPlan Fixture(string name, string prefix, string port)
    {
        var plan = Retained(name)["plan"]!;
        Rename(plan);
        return plan.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        void Rename(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "id", "source" }) if (obj[key] is JsonValue value) obj[key] = prefix + value.ToString();
                foreach (var key in new[] { "name", "port" }) if (obj[key]?.ToString() == "payload") obj[key] = port;
                if (obj["dependsOn"] is JsonArray dependencies)
                    for (var i = 0; i < dependencies.Count; i++) dependencies[i] = prefix + dependencies[i]!.ToString();
                foreach (var child in obj) Rename(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Rename(child);
        }
    }
    private static PlanningSession State(TaskPlan plan)
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements(); state.Plan = plan;
        state.Catalog = new() { AllowedStepTypes = ["set", "workflow.call", "switch"] };
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        return state;
    }

    [Theory]
    [InlineData("alpha_", "record")]
    [InlineData("beta_", "résultat")]
    public async Task RetainedHiddenTaskNeedsRevisionDespiteExistingDataExports(string prefix, string port)
    {
        var plan = Fixture("existing-export-hidden-task", prefix, port);
        var state = State(plan);
        Assert.Equal(2, state.Diagnostics.Count(d => d.Code == "TASK_DEPENDENCY_UNKNOWN"));
        var presence = Assert.Single(state.Diagnostics, d => d.Code == "TASK_PRESENCE_SCOPE");
        Assert.Contains("Consumer scope: /root; phase: always", presence.Message);
        Assert.Contains($"Producer '{prefix}choose': scope /tasks/{prefix}preserve/body; phase: tasks", presence.Message);
        Assert.Contains($"exports [{port}]", presence.Message);
        Assert.DoesNotContain(state.Diagnostics, d => d.Code is "TASK_REFERENCE_UNKNOWN" or "TASK_EXPORT_REQUIRED");
        Assert.Empty(TaskPlanRevisions.Exports(plan, state.RevisionScope).Additions);

        var runtime = new TestRuntime { Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = plan } };
        var stopped = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status); Assert.Single(runtime.Calls); Assert.Equal(0, stopped.ReplanAttempts);
        Assert.Contains(stopped.Diagnostics, d => d.Code == "REVISION_REQUIRED" && d.Location == $"/tasks/{prefix}consume/requires");
        var before = JsonSerializer.Serialize(stopped, PlanningJsonContext.Default.PlanningSession);
        var restored = PlannerFixture.Clone(stopped);
        restored = await new HybridWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(before, JsonSerializer.Serialize(stopped, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(stopped.ModelCalls, restored.ModelCalls); Assert.Equal(stopped.ReplanAttempts, restored.ReplanAttempts);
        Assert.Equal(JsonSerializer.Serialize(stopped.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(restored.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData("alpha_", "record")]
    [InlineData("beta_", "résultat")]
    public void RetainedMissingExportsUseOnlyTheNecessaryChainAndExplicitCounterpart(string prefix, string port)
    {
        var state = State(Fixture("missing-exports", prefix, port));
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_EXPORT_REQUIRED");
        var fixedPlan = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var boundary = fixedPlan.Root.Always[0]; var choice = boundary.Body!.Tasks[0];
        choice.Otherwise!.Outputs.Add(new(port, Output(prefix + "second", port)));
        boundary.Body.Outputs.Add(new(port, Output(choice.Id, port)));
        foreach (var consumer in fixedPlan.Root.Always.Skip(1))
        { consumer.DependsOn = [boundary.Id]; consumer.Outputs[0] = consumer.Outputs[0] with { Value = Output(boundary.Id, port) }; }
        Assert.Empty(TaskPlanRevisions.Validate(state.Plan, fixedPlan, state.RevisionScope, state.Catalog));
        var request = RepairPatchTests.Request(state);
        var response = TestRuntime.PatchResponse(request, state, fixedPlan).Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
        var applied = PlanningRepairPatch.Apply(state, response.Patch!, request);
        Assert.Empty(new TaskPlanCompiler().Compile(applied, state.Catalog!).Diagnostics);
        Assert.Equal(state.Plan!.Root.Always.Select(t => t.Id), applied.Root.Always.Select(t => t.Id));
        Assert.Empty(state.Plan.Root.Always[0].Body!.Outputs);
        Assert.Equal(2, TaskPlanRevisions.Exports(state.Plan, state.RevisionScope).Additions.Count);
        choice.Body!.Tasks[0].Objective = "Unrelated edit";
        Assert.Contains(TaskPlanRevisions.Validate(state.Plan, fixedPlan, state.RevisionScope), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    private static PlanningSession DependencyState()
    {
        var first = Value("first"); var consumer = Value("consume"); consumer.DependsOn = ["first", "unknown"];
        var dependent = Value("dependent"); dependent.Outputs[0] = new("payload", Output("consume"));
        var container = new PlanTask { Id = "container", Kind = "sequence", Objective = "Capture only available data", Body = new()
            { Outputs = [new("payload", Output("consume"))] } };
        var state = State(new() { Root = new() { Tasks = [first, consumer, dependent, container], Always = [Value("cleanup")] } });
        state.RevisionScope = ["/tasks/consume/dependsOn"];
        return state;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmutableDataConditionCanReuseSeparatelyAuthorizedExportsButCannotHideAnUnknownReference(bool unknown)
    {
        var observed = Value("observed"); observed.Outputs = [new("ready", new() { Kind = "boolean", Boolean = true })];
        var consumer = Value("consume"); consumer.Outputs = [new("ready", Output("observed", "ready"))];
        consumer.Requires = unknown ? new() { Kind = "predicate", Predicate = "and", Items = [Output("observed", "ready"), Output("absent", "ready")] }
            : Output("observed", "ready");
        var state = State(new() { Root = new() { Tasks = [new() { Id = "scope", Kind = "sequence", Objective = "Retain a scope", Body = new() { Tasks = [observed] } }, consumer] } });
        var blocked = TaskPlanRevisions.UnrepairableRequirements(state).ToArray();
        if (unknown) Assert.NotEmpty(blocked);
        else
        {
            Assert.Empty(blocked);
            var candidate = PlannerFixture.Clone(state).Plan!;
            candidate.Root.Tasks[0].Body!.Outputs = [new("ready", Output("observed", "ready"))];
            candidate.Root.Tasks[1].Outputs = [new("ready", Output("scope", "ready"))];
            Assert.Empty(TaskPlanRevisions.Validate(state.Plan, candidate, state.RevisionScope));
            Assert.Empty(new TaskPlanCompiler().Compile(candidate, state.Catalog!).Diagnostics);
            Assert.Equal("observed", candidate.Root.Tasks[1].Requires!.Source);
        }
    }

    [Theory]
    [InlineData("consume")]
    [InlineData("unknown")]
    [InlineData("dependent")]
    [InlineData("container")]
    [InlineData("cleanup")]
    public void DependencySchemaExcludesInvalidTargets(string target)
    {
        var state = DependencyState(); var slot = Assert.Single(RepairPatchTests.Slots(state));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(target), slot.ValueSchema));
        Assert.Empty(PlanningContractValidation.ValidateSchema(RepairPatchTests.Request(state).StructuredOutputSchema!.AsObject(), strict: true));
    }

    [Fact]
    public void DependencyRepairPreservesValidEdgesAndRejectsDuplicatesAtomically()
    {
        var state = DependencyState(); var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var edges in new[] { new JsonArray(), new JsonArray("first", "first") })
        {
            var request = RepairPatchTests.Request(state);
            var patch = new RepairPatch { Edits = [new() { Slot = Assert.Single(RepairPatchTests.Slots(state)).Id, Action = "replace", Value = edges }] };
            Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(state, patch, request));
            Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        }
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/tasks/consume/dependsOn", "replace", new JsonArray("first")));
        Assert.Equal(new[] { "first" }, repaired.Root.Tasks[1].DependsOn);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        var context = PlanningRepairContext.Build(state);
        var consumer = context["tasks"]!.AsArray().Single(t => t!["id"]!.ToString() == "consume")!;
        Assert.Equal("/root", consumer["scope"]!.ToString()); Assert.Equal("tasks", consumer["phase"]!.ToString());
        Assert.Equal("first", Assert.Single(consumer["eligibleDependencies"]!.AsArray())!.ToString());
    }

    [Fact]
    public void EmptyLocalDependencySetAllowsOnlyEmptyArray()
    {
        var task = Value("only"); task.DependsOn = ["absent"];
        var state = State(new() { Root = new() { Tasks = [task] } });
        var schema = Assert.Single(RepairPatchTests.Slots(state)).ValueSchema;
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonArray(), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray("only"), schema));
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/tasks/only/dependsOn", "replace", new JsonArray()));
        Assert.Empty(repaired.Root.Tasks[0].DependsOn);
    }

    [Fact]
    public void IndividuallyEligibleDependencyEditsCannotCreateACycleTogether()
    {
        var left = Value("left"); left.DependsOn = ["missing_left"];
        var right = Value("right"); right.DependsOn = ["missing_right"];
        var state = State(new() { Root = new() { Tasks = [left, right] } });
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var error = Assert.Throws<PlanningResponseException>(() => RepairPatchTests.Apply(state,
            RepairPatchTests.Edit(state, "/tasks/left/dependsOn", "replace", new JsonArray("right")),
            RepairPatchTests.Edit(state, "/tasks/right/dependsOn", "replace", new JsonArray("left"))));
        Assert.Contains(error.Diagnostics, d => d.Code == "TASK_DEPENDENCY_CYCLE");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("ancestor")]
    [InlineData("sibling")]
    [InlineData("group")]
    public void DependencyRepairNeverOffersInaccessibleTaskIdentities(string boundary)
    {
        var producer = Value("producer"); var consumer = Value("consumer"); consumer.DependsOn = ["producer"];
        var child = new TaskScope { Tasks = [consumer] };
        var plan = new TaskPlan();
        if (boundary == "ancestor") plan.Root.Tasks = [producer, new() { Id = "scope", Kind = "sequence", Objective = "Capture ancestors", Body = child }];
        if (boundary == "sibling") plan.Root.Tasks = [new() { Id = "scope", Kind = "parallel", Objective = "Independent work", Branches = [new() { Tasks = [producer] }, child] }];
        if (boundary == "group") { plan.Root.Tasks = [producer]; plan.Groups = [new() { Id = "group", Body = child }]; }
        var state = State(plan);
        var slot = Assert.Single(RepairPatchTests.Slots(state), s => s.Location == "/tasks/consumer/dependsOn");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray("producer"), slot.ValueSchema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonArray(), slot.ValueSchema));
    }

    [Fact]
    public void HistoricalVersionSevenRequestKeepsItsOriginalAuthorityAfterRestart()
    {
        var retained = Retained("issued-v7");
        var state = retained["state"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        var request = new LLMRequest { Prompt = retained["prompt"]!.ToString(), StructuredOutputSchema = retained["schema"]!.DeepClone() };
        var schema = request.StructuredOutputSchema.ToJsonString(); var prompt = request.Prompt;
        Assert.False(PlanningRepairPatch.Verify(PlannerFixture.Clone(state), request));
        var repaired = PlanningRepairPatch.Apply(state, new() { Edits = [new() { Slot = "s0", Action = "replace", Value = new JsonArray("source") }] }, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        Assert.Equal(schema, request.StructuredOutputSchema.ToJsonString()); Assert.Equal(prompt, request.Prompt);
        Assert.True(PlanningRepairPatch.Verify(state, RepairPatchTests.Request(state)));
        state.Request.TenantId = "another-tenant";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(state, request));
    }
}
