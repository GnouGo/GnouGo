using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ExportRepairTests
{
    [Theory]
    [InlineData("parcel", "payload", true, true)]
    [InlineData("parcel", "payload", true, false)]
    [InlineData("segment", "records", true, true)]
    [InlineData("segment", "records", true, false)]
    [InlineData("parcel", "payload", false, true)]
    public async Task NestedExportsIssueOnlyMissingPortsAndOneConsumerCorrection(string container, string port, bool existing, bool selected)
    {
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var state = PlannerFixture.Session(); state.IntentVersion = 2;
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements = new() { Summary = "Return declared values", Inputs = [], Outcomes = [new("data", "Return the selected data")] };
        var producer = new PlanTask { Id = "origin", Kind = "value", Objective = "Preserve data", Outputs = [new(port, PlanningCorpus.Business("input", "entries"))] };
        var inner = new PlanTask { Id = "inner", Kind = "sequence", Objective = "Nested work", Body = new() { Tasks = [producer] } };
        var outer = new PlanTask { Id = container, Kind = "conditional", Objective = "Choose branch", Condition = new() { Kind = "boolean", Boolean = selected },
            Body = new() { Tasks = [inner] }, Otherwise = new() };
        if (existing)
        {
            inner.Body.Outputs = [new(port, PlanningCorpus.Business("output", "origin", port))];
            outer.Body.Outputs = [new(port, PlanningCorpus.Business("output", "inner", port))];
            outer.Otherwise.Outputs = [new(port, new() { Kind = "array", Items = [PlanningCorpus.String("fallback")] })];
        }
        state.Plan = new() { Inputs = [new() { Name = "entries", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }], Root = new() { Tasks = [outer], Outputs = [new("report", PlanningCorpus.Business("output", "origin", port))] } };
        state.Requirements.Inputs = state.Plan.Inputs;
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        if (existing)
        {
            Assert.Empty(state.Diagnostics); Assert.Empty(state.RevisionScope);
            var compiled = new TaskPlanCompiler().Compile(state.Plan, state.Catalog);
            var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!],
                new JsonObject { ["entries"] = new JsonArray("one", "two") }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(JsonNode.DeepEquals(new JsonArray((selected ? new[] { "one", "two" } : new[] { "fallback" }).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), result.Outputs!["report"]));
            Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
            return;
        }
        var request = new PlanningPrompt(state).Request();
        Assert.Equal(7, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
        foreach (var code in new[] { "LLM_BUDGET_UNVERIFIABLE", "MODEL_DISPATCH_UNVERIFIABLE", "PLANNING_HOST_FAILURE" })
        {
            var interrupted = PlannerFixture.Clone(state); interrupted.Diagnostics.Add(new(code, "/", "Transport interruption"));
            PlanningRepairPatch.Verify(interrupted, request);
        }
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        var slots = RepairPatchTests.Slots(state);
        Assert.Equal(existing ? 1 : 4, slots.Count);
        Assert.DoesNotContain(slots, s => s.Kind == "exports");
        var revised = PlannerFixture.Clone(state).Plan!;
        revised.Root.Outputs[0].Value.Source = container;
        if (!existing)
        {
            revised.Root.Tasks[0].Body!.Tasks[0].Body!.Outputs.Add(new(port, PlanningCorpus.Business("output", "origin", port)));
            revised.Root.Tasks[0].Body!.Outputs.Add(new(port, PlanningCorpus.Business("output", "inner", port)));
            revised.Root.Tasks[0].Otherwise!.Outputs.Add(new(port, new() { Kind = "array", Items = [PlanningCorpus.String("fallback")] }));
        }
        var response = TestRuntime.PatchResponse(request, state, revised);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        var candidate = PlanningRepairPatch.Apply(state, response.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!.Patch!, request);
        Assert.Empty(new TaskPlanCompiler().Compile(candidate, state.Catalog).Diagnostics);
        Assert.Equal(existing ? 1 : 0, state.Plan.Root.Tasks[0].Body!.Outputs.Count);
        var wrong = response.DeepClone(); wrong["patch"]!["edits"]![0]!["value"] = JsonSerializer.SerializeToNode(PlanningCorpus.Business("output", "origin", port), PlanningJsonContext.Default.TaskValue);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(wrong, request.StructuredOutputSchema!));
    }
}
