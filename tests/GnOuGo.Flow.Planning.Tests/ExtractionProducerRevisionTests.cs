using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ExtractionProducerRevisionTests
{
    [Theory]
    [InlineData("observe", "adapt", "pages", "rows")]
    [InlineData("inspect", "extract", "batches", "matches")]
    public async Task MixedProducerFailuresNeedExplicitRevisionWithoutConsumerRepair(string operation, string producer, string input, string port)
    {
        var (_, state) = await Fixture(operation, producer, input, port); var corrected = Clone(state.Plan!);
        var task = state.Plan!.Root.Tasks[1]; task.Each = new("unbound", port);
        task.ResultType!.Fields[0].Type.Items = task.ResultType.Fields[0].Type.Items!.Items;
        state.Plan.Root.Tasks[2].Requires = new() { Kind = "choice", Source = operation };
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "TASK_TRANSFORM_EACH" && diagnostic.Message.Contains(input, StringComparison.Ordinal));
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "TASK_FLATTEN_INVALID" && diagnostic.Message.Contains(producer, StringComparison.Ordinal));
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CHOICE_UNKNOWN" && diagnostic.Message.Contains("operation", StringComparison.Ordinal));
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(state), diagnostic => diagnostic.Location == "/tasks/" + producer + "/each");
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(state), diagnostic => diagnostic.Location == "/tasks/consume/requires");
        state.EditablePaths = state.RevisionScope = ["/tasks/" + producer + "/each", "/tasks/" + producer + "/resultType", "/tasks/consume/requires"];
        var request = RepairPatchTests.Request(state); var restored = PlannerFixture.Clone(state);
        Assert.True(PlanningRepairPatch.Verify(restored, request));
        var correctedTask = JsonSerializer.SerializeToNode(corrected.Root.Tasks[1], PlanningJsonContext.Default.PlanTask)!;
        var payload = Patch(
            RepairPatchTests.Edit(restored, "/tasks/" + producer + "/each", "replace", correctedTask["each"]!.DeepClone()),
            RepairPatchTests.Edit(restored, "/tasks/" + producer + "/resultType", "replace", correctedTask["resultType"]!.DeepClone()),
            RepairPatchTests.Edit(restored, "/tasks/consume/requires", "replace",
                JsonSerializer.SerializeToNode(corrected.Root.Tasks[2].Requires, PlanningJsonContext.Default.TaskValue)));
        var response = PlanningCorpus.Transport(payload, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())
            .Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
        var repaired = PlanningRepairPatch.Apply(restored, response.Patch!, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        Assert.Equal(JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(repaired, PlanningJsonContext.Default.TaskPlan));
        var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
        Assert.Equal(3, slots.Count);
        Assert.DoesNotContain(slots, slot => slot!["location"]!.ToString().Contains("/inputs/records", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorrectedCompositionValidatesAndFixedProducerBindingsStayReadOnly()
    {
        var (_, state) = await Fixture("observe", "adapt", "batches", "rows");
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Diagnostics);
        state.Diagnostics = [new("TASK_FLATTEN_INVALID", "/tasks/consume/inputs/records", "Wrong operand")]; state.RevisionScope = ["/tasks/consume/inputs/records"];
        var context = PlanningRepairPatch.RequestContext(RepairPatchTests.Request(state))["repair"]!;
        var producer = context["tasks"]!.AsArray().Single(task => task!["id"]!.ToString() == "adapt")!;
        Assert.Equal("batches", producer["boundInputs"]![0]!["name"]!.ToString());
        Assert.Equal("observe", producer["boundInputs"]![0]!["value"]!["source"]!.ToString());
        Assert.Null(producer["inputs"]); Assert.Single(context["slots"]!.AsArray());
        Assert.Equal("/tasks/consume/inputs/records", context["slots"]![0]!["location"]!.ToString());
    }

    [Fact]
    public async Task DeclaredChoicesRemainValidAndUnknownChoiceDiagnosticsDistinguishOperations()
    {
        var (runtime, state) = await Fixture("observe", "adapt", "pages", "rows");
        var plan = runtime.Proposal.Plan!;
        plan.Choices = [new() { Id = "permission", Question = "Proceed?", Type = new() { Kind = "boolean" },
            Alternatives = [new("yes", "Proceed", new() { Kind = "boolean", Boolean = true }), new("no", "Stop", new() { Kind = "boolean", Boolean = false })],
            Recommended = "yes", Selected = "yes" }];
        var requirement = new TaskValue { Kind = "choice", Source = "permission" };
        plan.Root.Tasks[2].Requires = requirement;
        Assert.Empty(new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics);
        requirement.Source = "missing";
        var unknown = Assert.Single(new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics, d => d.Code == "CHOICE_UNKNOWN");
        Assert.Contains("permission", unknown.Message); Assert.Contains("'missing' is not a declared choice", unknown.Message);
        requirement.Source = "observe";
        var operation = Assert.Single(new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics, d => d.Code == "CHOICE_UNKNOWN");
        Assert.Contains("operation task", operation.Message); Assert.Contains("not success", operation.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EitherImmutableCauseAloneStopsMixedConsumerRepairs(bool invalidEach, bool invalidChoice)
    {
        var (_, state) = await Fixture("observe", "adapt", "batches", "rows");
        if (invalidEach) state.Plan!.Root.Tasks[1].Each = new("missing", "rows");
        if (invalidChoice) state.Plan!.Root.Tasks[2].Requires = new() { Kind = "choice", Source = "observe" };
        state.Plan!.Root.Tasks[2].Inputs[0] = new("records", new() { Kind = "flatten", Items = [new() { Kind = "string", Text = "not an array" }] });
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(state), diagnostic => diagnostic.Code == "REVISION_REQUIRED");
    }

    [Fact]
    public async Task IssuedConsumerRepairCannotBypassAnUnchangedImmutableProducerDefect()
    {
        var (_, state) = await Fixture("observe", "adapt", "pages", "rows");
        var plan = state.Plan!; plan.Root.Tasks[1].Each = new("missing", "rows"); plan.Root.Tasks[2].Inputs[0] = new("records", new() { Kind = "string", Text = "invalid collection" });
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics.ToList(); state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        state.ModelCalls = 2; state.ReplanAttempts = 1;
        var issued = RepairPatchTests.Request(state); issued.ClientRequestId = "current-request";
        var requestBytes = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var response = Patch(RepairPatchTests.Edit(state, "/tasks/consume/inputs/records", "replace",
            JsonSerializer.SerializeToNode(new TaskValue { Kind = "array" }, PlanningJsonContext.Default.TaskValue)))
            .Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
        var restored = PlannerFixture.Clone(state); Assert.True(PlanningRepairPatch.Verify(restored, issued));
        Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(restored, response.Patch!, issued));
        Assert.Equal("string", restored.Plan!.Root.Tasks[2].Inputs[0].Value.Kind);
        Assert.Equal(2, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts);
        Assert.Equal(requestBytes, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(restored), diagnostic => diagnostic.Location == "/tasks/adapt/each");
    }
    private static JsonObject Patch(params JsonObject[] edits) => new()
    {
        ["discoveryRequests"] = null, ["clarifications"] = null,
        ["patch"] = new JsonObject { ["edits"] = new JsonArray(edits.Select(edit => (JsonNode)edit).ToArray()) }
    };
    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    internal static async Task<(TestRuntime, PlanningSession)> Fixture(string operation, string producer, string input, string port)
    {
        var record = new TaskType { Kind = "object", Fields = [new() { Name = "id", Type = new() { Kind = "string" } }, new() { Name = "label", Type = new() { Kind = "string", Nullable = true } }] };
        var rows = new TaskType { Kind = "array", Items = record };
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("fixture", new() { Tools = [
            new() { Name = operation, EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
                OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"ready":{"type":"boolean"},"pages":{"type":"array","items":{"type":"string"}}},"required":["ready","pages"],"additionalProperties":false}""") },
            new() { Name = "consume", EffectKind = "write", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["records"] = TaskPlanCompiler.TypeSchema(rows) }, ["required"] = new JsonArray("records"), ["additionalProperties"] = false },
                OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"receipt":{"type":"string"}},"required":["receipt"],"additionalProperties":false}""") }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var state = PlannerFixture.Session(); state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual); state.Catalog.Policy.RequireExternalConfirmation = false;
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = operation, Objective = "Observe complete batches", Operation = state.Catalog.Capabilities.Single(c => c.Method == operation).Id },
            new() { Id = producer, Kind = "transform", Mode = "extract", Objective = "Extract the observed identity and label of each candidate in every batch",
                Inputs = [new(input, Output(operation, "pages"))], Each = new(input, port),
                ResultType = new() { Kind = "object", Fields = [new() { Name = port, Type = new() { Kind = "array", Items = rows } }] } },
            new() { Id = "consume", Objective = "Persist the observed records", Operation = state.Catalog.Capabilities.Single(c => c.Method == "consume").Id,
                Requires = Output(operation, "ready"), Inputs = [new("records", new() { Kind = "flatten", Items = [Output(producer, port)] })] }
        ], Outputs = [new("receipt", Output("consume", "receipt"))] } };
        runtime.Proposal = new() { Plan = plan, Requirements = new() { Summary = "Observe and preserve every candidate", Inputs = [],
            Outputs = [new() { Name = "receipt", Type = new() { Kind = "string" } }], Outcomes = [new("preserved", "Preserve every observed candidate")] } };
        state.Plan = plan; state.Requirements = runtime.Proposal.Requirements;
        return (runtime, state);
    }
}
