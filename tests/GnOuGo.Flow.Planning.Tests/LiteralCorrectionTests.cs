using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LiteralCorrectionTests
{
    [Theory]
    [InlineData("greeting")]
    [InlineData("entrée_技术")]
    public async Task LiteralUsesOneIssuedValueSlotAndExecutesTheOriginalInputExactly(string name)
    {
        var (runtime, state) = Fixture(name);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID");
        Assert.Contains("For a declared input named name: {\"kind\":\"input\",\"source\":\"name\"}", runtime.Calls[0].Prompt);
        Assert.Contains("Return the smallest sufficient executable business plan", runtime.Calls[0].Prompt);
        Assert.Contains(PlanningSchemaReferences.Walk(runtime.Calls[0].StructuredOutputSchema!.AsObject(), "", 0),
            s => s.Schema["description"]?.ToString().Contains("Literal strings exclude ${}", StringComparison.Ordinal) == true);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan!.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "input", Source = name });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        var repair = PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!;
        Assert.Equal("/tasks/greet/outputs/message", Assert.Single(repair["slots"]!.AsArray())!["location"]!.ToString());
        Assert.Contains("never strip or escape interpolation", runtime.Calls[1].Prompt);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        var text = "Original ${unexecuted} text\n whitespace\t二";
        var result = await LookupCompilationTests.Execute(state.Yaml!, new() { [name] = text }, new WorkflowEngine());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(text, result.Outputs!["message"]!.GetValue<string>());
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task ExhaustedLiteralSessionCannotAcquireAnotherCorrectionCall()
    {
        var (runtime, state) = Fixture("greeting"); state.Request.MaxModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Contains(state.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID");
        await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state)); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task LiteralInAProtectedAssertionCannotBecomeInventedSuccess()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Plan!.Root.Tasks[0].Requires = new() { Kind = "string", Text = "${ready}" };
        // Retained malformed assertions still require explicit revision. Fresh
        // generation rejects this shape in its Boolean response domain instead.
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = runtime.Proposal.Plan; state.Requirements = runtime.Proposal.Requirements;
        state.Revision = 1; state.ModelCalls = 1;
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Yaml); Assert.Contains(state.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID" && d.Location.EndsWith("/requires", StringComparison.Ordinal));
        Assert.Equal("${ready}", state.Plan!.Root.Tasks[0].Requires!.Text);
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("guard")]
    [InlineData("export")]
    [InlineData("operation")]
    public async Task LiteralCorrectionCannotChangeIndependentWork(string change)
    {
        var (runtime, state) = Fixture("greeting"); state.Request.MaxReplanAttempts = 1;
        runtime.Proposal.Plan!.Root.Tasks[0].Requires = new() { Kind = "boolean", Boolean = false };
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "input", Source = "greeting" });
        if (change == "objective") runtime.Proposal.Plan.Root.Tasks[0].Objective = "Different business work";
        if (change == "guard") runtime.Proposal.Plan.Root.Tasks[0].Requires!.Boolean = true;
        if (change == "export") runtime.Proposal.Plan.Root.Outputs.Clear();
        if (change == "operation") runtime.Proposal.Plan.Root.Tasks.Add(new() { Id = "unrequested", Kind = "operation", Operation = "invented", Objective = "Invent external work" });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(before, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains(state.Diagnostics, d => d.Code is "PLANNING_RESPONSE_INVALID" or "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task RepeatedLiteralProposalStopsWithinTheExistingFallbackAllowance()
    {
        var (runtime, state) = Fixture("greeting");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
        await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state)); Assert.Equal(3, runtime.Calls.Count);
    }

    [Fact]
    public async Task CommittedLiteralReceiptKeepsItsRequestAndAccountingAtTheLimit()
    {
        var (runtime, state) = Fixture("greeting"); state.Request.MaxModelCalls = 2;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "input", Source = "greeting" });
        runtime.Respond = (_, _) => throw new InvalidOperationException("Interrupted after dispatch");
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.NotNull(state.PendingCall);
        var issued = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        var receipt = TestRuntime.PatchResponse(state.PendingCall.Request, state, runtime.Proposal.Plan);
        var reads = 0;
        var replay = new TestRuntime { Respond = (request, _) =>
        {
            reads++; Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = receipt.DeepClone() };
        } };
        var restored = PlannerFixture.Clone(state); restored.Status = PlanningStatus.Generating;
        restored = await planner.AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, replay, PlannerFixture.Ct);
        Assert.True(restored.Status == PlanningStatus.FinalReview, string.Join(';', restored.Diagnostics));
        Assert.Equal(2, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts); Assert.Equal(1, reads);
        await PlannerFixture.RunAsync(replay, restored); Assert.Equal(1, reads);
        Assert.Equal(issued, JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshRepairDoesNotEncodeAnOriginalWithADiagnosedInvalidLiteral(bool nested)
    {
        var state = OperationLiteralState(nested, invalid: true);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var original = state.Plan!.Root.Tasks[0].Inputs[0].Value;
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("TASK_LITERAL_INVALID", diagnostic.Code);
        Assert.Equal("/tasks/consumer/inputs/text", diagnostic.Location);
        Assert.True(diagnostic.Required);
        Assert.Equal(12, PlanningRepairPatch.Version(state));
        var request = RepairPatchTests.Request(state);
        Assert.Equal(12, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
        Assert.True(PlanningRepairPatch.Verify(state, request));
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        var encoded = new TaskValue { Kind = "json", Items = [original] };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PatchResponse(encoded), request.StructuredOutputSchema!));
        Assert.DoesNotContain("${callerValue}", request.StructuredOutputSchema!.ToJsonString());
        // A multiline replacement is ordinary data, not an inferred reference or escaped interpolation.
        var corrected = new TaskValue { Kind = "string", Text = "Keep the exact first line.\nKeep the exact second line." };
        Assert.Empty(PlanningContractValidation.ValidateInstance(PatchResponse(corrected), request.StructuredOutputSchema!));
        var repaired = PlanningRepairPatch.Apply(state, Patch(corrected), request);
        Assert.Equal(corrected.Text, repaired.Root.Tasks[0].Inputs[0].Value.Text);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        Assert.Equal(JsonSerializer.Serialize(state.Plan.Root.Tasks[1], PlanningJsonContext.Default.PlanTask),
            JsonSerializer.Serialize(repaired.Root.Tasks[1], PlanningJsonContext.Default.PlanTask));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("type_mismatch")]
    [InlineData("unrelated_literal")]
    [InlineData("advisory_literal")]
    public void FreshRepairKeepsValidOriginalJsonConversionWithoutAnExactRequiredLiteralFinding(string diagnosis)
    {
        var state = OperationLiteralState(nested: true, invalid: false);
        var original = state.Plan!.Root.Tasks[0].Inputs[0].Value;
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_INPUT_TYPE");
        if (diagnosis == "unrelated_literal") state.Diagnostics.Add(new("TASK_LITERAL_INVALID", "/tasks/unrelated/outputs/data", "Other slot"));
        if (diagnosis == "advisory_literal") state.Diagnostics.Add(new("TASK_LITERAL_INVALID", "/tasks/consumer/inputs/text", "Advisory", Required: false));
        var request = RepairPatchTests.Request(state);
        var encoded = new TaskValue { Kind = "json", Items = [original] };
        Assert.Empty(PlanningContractValidation.ValidateInstance(PatchResponse(encoded), request.StructuredOutputSchema!));
        var repaired = PlanningRepairPatch.Apply(state, Patch(encoded), request);
        Assert.Equal("json", repaired.Root.Tasks[0].Inputs[0].Value.Kind);
        Assert.Equal(JsonSerializer.Serialize(original, PlanningJsonContext.Default.TaskValue),
            JsonSerializer.Serialize(repaired.Root.Tasks[0].Inputs[0].Value.Items[0], PlanningJsonContext.Default.TaskValue));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task IssuedHistoricalLiteralRepairKeepsItsSchemaAuthorityAndAccounting(int version)
    {
        var state = OperationLiteralState(nested: false, invalid: true);
        var schema = PlanningRepairPatch.Schema(state, PlanningRepairPatch.Template(state, version, clarifications: false), version);
        var authority = PlanningRepairPatch.Authority(state, version: version);
        var request = new LLMRequest { ClientRequestId = "retained-literal-" + version, StructuredOutputSchema = schema,
            Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = version, ["authority"] = authority } } };
        var original = state.Plan!.Root.Tasks[0].Inputs[0].Value;
        var encoded = new TaskValue { Kind = "json", Items = [original] };
        Assert.Empty(PlanningContractValidation.ValidateInstance(PatchResponse(encoded), schema));
        // Its old schema still permits the historical encoding; unchanged compiler validation rejects it.
        var rejected = Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(state, Patch(encoded), request));
        Assert.Contains(rejected.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID");
        state.PendingCall = new() { Id = request.ClientRequestId, Request = request, Purpose = "replan" };
        state.ModelCalls = 3; state.ReplanAttempts = 1;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var corrected = new TaskValue { Kind = "string", Text = "Retained first line.\nRetained second line." };
        var restored = PlannerFixture.Clone(state);
        var runtime = new TestRuntime { Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = PatchResponse(corrected) };
        } };
        var response = await PlanningModelCalls.CallAsync(restored, runtime, "replan", "New context cannot replace issued bytes",
            RepairPatchTests.Request(restored).StructuredOutputSchema!.AsObject(), PlannerFixture.Ct);
        var repaired = PlanningRepairPatch.Apply(restored, response.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!.Patch!, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, restored.Catalog!).Diagnostics);
        Assert.Equal(3, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts); Assert.Single(runtime.Calls);
        Assert.Equal(authority, PlanningRepairPatch.Authority(restored, version: version));
        Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        // Existing revision-3/default authorities continue to choose the old grammar.
        restored.Construction!.Compact!.PresentationVersion = 3;
        restored.Request.Options["construction_presentation_version"] = 3;
        Assert.Equal(11, PlanningRepairPatch.Version(restored));
        Assert.Equal(schema.ToJsonString(), PlanningRepairPatch.Schema(restored,
            PlanningRepairPatch.Template(restored, version, clarifications: false), version).ToJsonString());
    }

    private static PlanningSession OperationLiteralState(bool nested, bool invalid)
    {
        var state = RepairPatchTests.State();
        state.Construction = new() { Compact = new() { PresentationVersion = 4 } };
        state.Request.Options["construction_presentation_version"] = 4;
        state.Plan!.Root.Tasks.RemoveAt(0);
        var value = new TaskValue { Kind = "string", Text = invalid ? "First line.\nOriginal ${callerValue}." : "Exact original data" };
        state.Plan.Root.Tasks[0].Inputs = [new("text", nested ? new() { Kind = "object", Members = [new("message", value)] } : value)];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = ["/tasks/consumer/inputs/text"];
        return state;
    }

    private static RepairPatch Patch(TaskValue value) => new() { Edits = [new() { Slot = "s0", Action = "replace",
        Value = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)) }] };
    private static JsonObject PatchResponse(TaskValue value) => new() { ["discoveryRequests"] = null,
        ["patch"] = JsonSerializer.SerializeToNode(Patch(value), RepairJsonContext.Default.RepairPatch) };

    private static (TestRuntime Runtime, PlanningSession State) Fixture(string name)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Plan!.Inputs = [new() { Name = name, Type = new() { Kind = "string" } }];
        runtime.Proposal.Requirements!.Inputs = runtime.Proposal.Plan.Inputs;
        runtime.Proposal.Plan.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "string", Text = "${" + name + "}" });
        return (runtime, state);
    }
}
