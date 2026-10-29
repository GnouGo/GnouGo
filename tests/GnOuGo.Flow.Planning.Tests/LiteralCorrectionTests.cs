using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LiteralCorrectionTests
{

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
        Assert.Empty(PlanningContractValidation.ValidateInstance(PatchResponse(encoded), request.StructuredOutputSchema!));
        var rejected = Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(state, Patch(encoded), request));
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "TASK_LITERAL_INVALID");
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


    private static PlanningSession OperationLiteralState(bool nested, bool invalid)
    {
        var state = RepairPatchTests.State();
        state.Construction = new() { Compact = new() };
        state.Plan!.Root.Tasks.RemoveAt(0);
        var value = new TaskValue { Kind = "string", Text = invalid ? "First line.\nOriginal ${callerValue}." : "Exact original data" };
        state.Plan.Root.Tasks[0].Inputs = [new("text", nested ? new() { Kind = "object", Members = [new("message", value)] } : value)];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = ["/tasks/consumer/inputs/text"];
        return state;
    }

    private static RepairPatch Patch(TaskValue value) => new() { Edits = [new() { Slot = "s0", Action = "replace",
        Value = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)) }] };
    private static JsonObject PatchResponse(TaskValue value) => new() { ["discoveryRequests"] = null, ["clarifications"] = null,
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
