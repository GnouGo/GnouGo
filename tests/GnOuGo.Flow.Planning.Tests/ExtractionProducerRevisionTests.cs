using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ExtractionProducerRevisionTests
{
    // The retained native-snapshot proposal used page/pages, a flat rows contract,
    // and choice(operation). Keep the names arbitrary and the producer authoritative.
    [Theory]
    [InlineData("observe", "adapt", "pages", "rows")]
    [InlineData("inspect", "extract", "batches", "matches")]
    public async Task MixedProducerFailuresNeedExplicitRevisionWithoutConsumerRepair(
        string operation, string producer, string input, string port)
    {
        var (runtime, state) = await Fixture(operation, producer, input, port);
        var planner = new HybridWorkflowPlanner();
        var corrected = Clone(runtime.Proposal.Plan!);
        var task = runtime.Proposal.Plan!.Root.Tasks[1];
        task.Each = new("unbound", port);
        task.ResultType!.Fields[0].Type.Items = task.ResultType.Fields[0].Type.Items!.Items;
        runtime.Proposal.Plan.Root.Tasks[2].Requires = new() { Kind = "choice", Source = operation };
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.Stopped, string.Join("; ", state.Diagnostics));
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_TRANSFORM_EACH" && d.Message.Contains(input, StringComparison.Ordinal));
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_FLATTEN_INVALID" && d.Message.Contains(producer, StringComparison.Ordinal));
        Assert.Contains(state.Diagnostics, d => d.Code == "CHOICE_UNKNOWN" && d.Message.Contains("operation", StringComparison.Ordinal));
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_REQUIRED" && d.Location == "/tasks/" + producer + "/each");
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_REQUIRED" && d.Location == "/tasks/consume/requires");
        var stopped = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(stopped, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));

        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            EditablePaths = ["/tasks/" + producer + "/each", "/tasks/" + producer + "/resultType", "/tasks/consume/requires"],
            Text = "Correct only the extraction declaration and the typed assertion; preserve all business work." }, runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan = corrected;
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Equal(JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var request = runtime.Calls[1]; Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
        var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
        Assert.Equal(3, slots.Count);
        Assert.DoesNotContain(slots, s => s!["location"]!.ToString().Contains("/inputs/records", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorrectedCompositionReachesReviewInOneCallAndFixedProducerBindingsStayReadOnly()
    {
        var (runtime, state) = await Fixture("observe", "adapt", "batches", "rows");
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        // Diagnose only a consumer: this must not grant edits to the producer.
        state.Diagnostics = [new("TASK_FLATTEN_INVALID", "/tasks/consume/inputs/records", "Wrong operand")];
        state.RevisionScope = ["/tasks/consume/inputs/records"];
        var request = new PlanningPrompt(state).Request();
        var context = PlanningRepairPatch.RequestContext(request)["repair"]!;
        var producer = context["tasks"]!.AsArray().Single(t => t!["id"]!.ToString() == "adapt")!;
        Assert.Equal("batches", producer["boundInputs"]![0]!["name"]!.ToString());
        Assert.Equal("observe", producer["boundInputs"]![0]!["value"]!["source"]!.ToString());
        Assert.Null(producer["inputs"]);
        Assert.Single(context["slots"]!.AsArray());
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
        plan.Root.Tasks[2].Requires = new() { Kind = "choice", Source = "permission" };
        Assert.Empty(new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics);
        plan.Root.Tasks[2].Requires.Source = "missing";
        var unknown = Assert.Single(new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics, d => d.Code == "CHOICE_UNKNOWN");
        Assert.Contains("permission", unknown.Message); Assert.Contains("'missing' is not a declared choice", unknown.Message);
        plan.Root.Tasks[2].Requires.Source = "observe";
        var operation = Assert.Single(new TaskPlanCompiler().Compile(plan, state.Catalog!).Diagnostics, d => d.Code == "CHOICE_UNKNOWN");
        Assert.Contains("operation task", operation.Message); Assert.Contains("not success", operation.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EitherImmutableCauseAloneStopsMixedConsumerRepairs(bool invalidEach, bool invalidChoice)
    {
        var (runtime, state) = await Fixture("observe", "adapt", "batches", "rows");
        if (invalidEach) runtime.Proposal.Plan!.Root.Tasks[1].Each = new("missing", "rows");
        if (invalidChoice) runtime.Proposal.Plan!.Root.Tasks[2].Requires = new() { Kind = "choice", Source = "observe" };
        runtime.Proposal.Plan!.Root.Tasks[2].Inputs[0] = new("records", new() { Kind = "flatten", Items = [new() { Kind = "string", Text = "not an array" }] });
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.Stopped, string.Join("; ", state.Diagnostics)); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_REQUIRED");
    }

    [Fact]
    public async Task IssuedConsumerRepairReceiptIsProcessedBeforeFreshRequestChecks()
    {
        var (runtime, state) = await Fixture("observe", "adapt", "pages", "rows");
        var plan = runtime.Proposal.Plan!;
        plan.Root.Tasks[1].Each = new("missing", "rows");
        plan.Root.Tasks[2].Inputs[0] = new("records", new() { Kind = "string", Text = "invalid collection" });
        state.Plan = Clone(plan); state.Requirements = runtime.Proposal.Requirements;
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        var issued = new PlanningPrompt(state).Request(); issued.ClientRequestId = "retained-request";
        var requestBytes = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = issued.ClientRequestId, Purpose = "replan", Request = issued };
        state.ModelCalls = 2; state.ReplanAttempts = 1; state.Status = PlanningStatus.Generating;
        var correctedConsumer = Clone(plan);
        correctedConsumer.Root.Tasks[2].Inputs[0] = new("records", new() { Kind = "array", Items = [] });
        var receipt = TestRuntime.PatchResponse(issued, state, correctedConsumer);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(requestBytes, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = receipt.DeepClone() }; // Committed receipt only; no inference.
        };
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new(), runtime, PlannerFixture.Ct);
        // Whole-plan validation rejects the patch atomically, but its original
        // receipt was consumed and accounted before checks for any fresh request.
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Null(state.PendingCall);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Single(runtime.Calls);
        Assert.Equal("string", state.Plan!.Root.Tasks[2].Inputs[0].Value.Kind);
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_REQUIRED" && d.Location == "/tasks/adapt/each");

    }

    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static async Task<(TestRuntime, PlanningSession)> Fixture(string operation, string producer, string input, string port)
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
        return (runtime, state);
    }
}
