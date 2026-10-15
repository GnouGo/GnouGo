using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class NullableGuardCorrectionTests
{
    [Theory]
    [InlineData("string", "\"\"")]
    [InlineData("object", "{}")]
    [InlineData("array", "[]")]
    [InlineData("number", "0")]
    public async Task CorrectedGuardPreservesNullAbsenceAndNonTruthyObservedValues(string type, string json)
    {
        var (runtime, state) = await Fixture(type);
        runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "after", Objective = "Dispatch only after the assertion",
            Operation = runtime.Proposal.Plan.Root.Tasks[0].Operation, DependsOn = ["use"] });
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan.Root.Tasks[1].Requires = And(Field("ready"), NonNull(Field("payload")));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        foreach (var variant in new[] { "present", "null", "absent", "false_flag" })
        {
            var effects = 0;
            var result = new JsonObject { ["ready"] = variant != "false_flag" };
            if (variant != "absent") result["payload"] = variant == "null" ? null : JsonNode.Parse(json);
            var factory = new InMemoryMcpClientFactory();
            var contract = state.Catalog!.Capabilities.Single(c => c.Method == "read");
            factory.RegisterServer("records", new() { Tools = [new() { Name = "read", EffectKind = "none",
                InputSchema = contract.InputSchema, OutputSchema = contract.OutputSchema }], ToolHandlers = new() { ["read"] = _ =>
            { effects++; return new() { Content = result.DeepClone() }; } } });
            var engine = new WorkflowEngine { McpClientFactory = factory };
            var outcome = await engine.ExecuteAsync(workflow.Workflows[workflow.Entrypoint!], null, PlannerFixture.Ct);
            Assert.True((variant == "present") == outcome.Success, $"{variant}: {outcome.Error?.Code}: {outcome.Error?.Message}; {outcome.Error?.Details}");
            Assert.Equal(variant == "present" ? 2 : 1, effects);
            if (variant != "present") Assert.NotNull(outcome.Error);
        }
        Assert.Equal(2, runtime.Calls.Count); // No runtime inference for the guard.
    }

    [Fact]
    public async Task CommittedGuardReceiptKeepsVersionAndAllowanceAcrossRestart()
    {
        var (runtime, state) = await Fixture("string");
        state.Request.MaxModelCalls = 2; state.Request.MaxReplanAttempts = 1;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks[1].Requires = NonNull(Field("payload"));
        runtime.Respond = (_, _) => throw new InvalidOperationException("Interrupted after the request was issued");
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.NotNull(state.PendingCall);
        Assert.Equal(2, state.Request.Options["automatic_correction_progress"]!["version"]!.GetValue<int>());
        var issued = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        var response = TestRuntime.PatchResponse(state.PendingCall.Request, state, runtime.Proposal.Plan);
        var restored = PlannerFixture.Clone(state); restored.Status = PlanningStatus.Generating;
        var reads = 0;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal("replan", purpose); reads++; return new() { Json = response.DeepClone() };
        };
        restored = await planner.AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(restored.Status == PlanningStatus.FinalReview, string.Join(';', restored.Diagnostics));
        Assert.Equal(1, reads); Assert.Equal(2, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts);
        await planner.AdvanceAsync(PlannerFixture.Clone(restored), new() { ExpectedRevision = restored.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(1, reads);
        var other = PlannerFixture.Clone(state); other.Status = PlanningStatus.Generating; other.Request.TenantId = "other";
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(other, new() { ExpectedRevision = other.Revision }, runtime, PlannerFixture.Ct));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("comparison")]
    [InlineData("removed")]
    [InlineData("reordered")]
    public async Task ValidSurroundingAssertionsCannotBeChangedWithTheInvalidOperand(string change)
    {
        var (runtime, state) = await Fixture("string");
        var task = runtime.Proposal.Plan!.Root.Tasks[1];
        TaskValue EqualNull() => new() { Kind = "predicate", Predicate = "equal", Items = [Field("payload"), new() { Kind = "null" }] };
        task.Requires = And(Field("payload"), And(Field("ready"), EqualNull()));
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        var next = And(NonNull(Field("payload")), And(Field("ready"), EqualNull()));
        if (change == "comparison") next.Items[1].Items[1].Items[0] = NonNull(Field("payload"));
        if (change == "removed") next = NonNull(Field("payload"));
        if (change == "reordered") next = And(next.Items[1], next.Items[0]);
        task.Requires = next;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, runtime.Calls.Count); Assert.Null(state.Yaml);
        Assert.Contains(state.Diagnostics, d => d.Code == "AUTOMATIC_GUARD_CORRECTION_UNSAFE");
    }

    [Theory]
    [InlineData("string")]
    [InlineData("object")]
    public async Task HistoricalCorrectionKeepsItsOriginalPreservationRules(string type)
    {
        var (runtime, state) = await Fixture(type);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        // A journal from version 1 must not acquire the new authority on restart.
        state.Request.Baseline = PlannerFixture.Clone(state).Plan;
        state.Request.Options["automatic_correction_progress"] = new JsonObject { ["version"] = 1,
            ["attempts"] = new JsonArray(), ["locations"] = new JsonArray("/tasks/use/requires") };
        state.Request.MaxReplanAttempts = 1;
        runtime.Proposal.Plan!.Root.Tasks[1].Requires = NonNull(Field("payload"));
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/use/requires");
        Assert.DoesNotContain("nullable nonboolean", runtime.Calls[1].Prompt);
        Assert.Equal(1, state.Request.Options["automatic_correction_progress"]!["version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("nonnullable")]
    [InlineData("nullable_boolean")]
    public async Task UnknownOrAmbiguousConditionStopsBeforeAnUnhelpfulCorrection(string type)
    {
        var (runtime, state) = await Fixture("string");
        var contract = state.Catalog!.Capabilities.Single(c => c.Method == "read").OutputSchema!["properties"]!["payload"]!.AsObject();
        contract["type"] = type == "nullable_boolean" ? new JsonArray("boolean", "null") : JsonValue.Create("string");
        var compiled = new TaskPlanCompiler().Compile(runtime.Proposal.Plan!, state.Catalog);
        Assert.True(compiled.Diagnostics.Any(d => d.Code == "TASK_CONDITION_TYPE"), string.Join(';', compiled.Diagnostics));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "AUTOMATIC_GUARD_CORRECTION_UNSAFE");
    }

    [Fact]
    public async Task OpaqueReferenceCannotAuthorizeNullableGuardConversion()
    {
        var (runtime, state) = await Fixture("string");
        state.Catalog!.Capabilities.Single(c => c.Method == "read").OutputSchema!["properties"]!["payload"]!.AsObject().Clear();
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Message.Contains("opaque", StringComparison.Ordinal));
        runtime.Proposal.Plan!.Root.Tasks[1].Requires = NonNull(Field("payload"));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.InRange(runtime.Calls.Count, 1, 3);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task IndependentObjectiveChangesHaveAnExactFieldDiagnostic()
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("observe", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        // A no-progress targeted response permits the existing single fallback.
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks[1].Requires!.Items[1].Port = "ready";
        runtime.Proposal.Plan.Root.Tasks[0].Objective = "Different extraction/interpretation objective";
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(3, runtime.Calls.Count);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/observe/objective");
    }

    [Theory]
    [InlineData("string", false)]
    [InlineData("object", false)]
    [InlineData("array", false)]
    [InlineData("number", false)]
    [InlineData("string", true)]
    [InlineData("object", true)]
    public async Task DiagnosedNullableReferenceUsesOneTargetedCorrection(string type, bool nested)
    {
        var (runtime, state) = await Fixture(type);
        var task = runtime.Proposal.Plan!.Root.Tasks[1];
        if (nested) task.Requires = And(Field("ready"), task.Requires!);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "CONTRACT_UNSATISFIED" && d.ValidationStage == "contract:TASK_CONDITION_TYPE" || d.Code == "TASK_CONDITION_TYPE");
        task.Requires = And(Field("ready"), NonNull(Field("payload")));
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal(2, progress["version"]!.GetValue<int>());
        Assert.Equal("targeted_revision", Assert.Single(progress["attempts"]!.AsArray())!["action"]!.ToString());
        Assert.Equal("/tasks/use/requires", Assert.Single(PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!["editablePaths"]!.AsArray())!.ToString());
        Assert.Contains("nullable nonboolean", runtime.Calls[1].Prompt);
        Assert.Contains(runtime.Checkpoints, s => s.PendingCall is not null && s.Request.Options["automatic_correction_progress"]?["version"]?.GetValue<int>() == 2);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("or")]
    [InlineData("completion")]
    [InlineData("unrelated")]
    [InlineData("equal_null")]
    public async Task UnsafeReplacementStopsWithoutStructuralRegeneration(string defect)
    {
        var (runtime, state) = await Fixture("string");
        if (defect == "unrelated") runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "unrelated_read",
            Operation = runtime.Proposal.Plan.Root.Tasks[0].Operation, Objective = "Independent producer with the same Boolean contract" });
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        var guard = NonNull(Field("payload"));
        runtime.Proposal.Plan!.Root.Tasks[1].Requires = defect switch
        {
            "literal" => new() { Kind = "boolean", Boolean = true },
            "or" => new() { Kind = "predicate", Predicate = "or", Items = [guard, Field("ready")] },
            "completion" => And(new() { Kind = "present", Source = "read" }, guard),
            "unrelated" => And(new() { Kind = "field", Port = "ready", Items = [new() { Kind = "output", Source = "unrelated_read" }] }, guard),
            _ => new() { Kind = "predicate", Predicate = "equal", Items = [Field("payload"), new() { Kind = "null" }] }
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "AUTOMATIC_GUARD_CORRECTION_UNSAFE" && d.Location == "/tasks/use/requires");
        Assert.Equal("field", state.Plan!.Root.Tasks[1].Requires!.Kind);
    }

    internal static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture(string type)
    {
        var payload = new JsonObject { ["type"] = new JsonArray(type, "null") };
        if (type == "array") payload["items"] = new JsonObject { ["type"] = "string" };
        if (type == "object") { payload["properties"] = new JsonObject(); payload["additionalProperties"] = false; }
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("records", new() { Tools = [new() { Name = "read", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["payload"] = payload, ["ready"] = new JsonObject { ["type"] = "boolean" } },
                ["required"] = new JsonArray("payload", "ready"), ["additionalProperties"] = false } }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        runtime.Proposal.Plan = new() { Root = new() { Tasks = [
            new() { Id = "read", Objective = "Read the original record", Operation = catalog.Capabilities.Single(c => c.Method == "read").Id },
            new() { Id = "use", Kind = "value", Objective = "Preserve the observed result", Requires = Field("payload"),
                Outputs = [new("message", new() { Kind = "string", Text = "checked" })] }],
            Outputs = [new("message", new() { Kind = "output", Source = "use", Port = "message" })] } };
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Request.Options["automatic_correction"] = true;
        return (runtime, state);
    }
    internal static TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "output", Source = "read" }] };
    internal static TaskValue NonNull(TaskValue value) => new() { Kind = "predicate", Predicate = "not_equal", Items = [value, new() { Kind = "null" }] };
    internal static TaskValue And(TaskValue left, TaskValue right) => new() { Kind = "predicate", Predicate = "and", Items = [left, right] };
}
