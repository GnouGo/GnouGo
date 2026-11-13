using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairVocabularyTests
{
    [Theory]
    [InlineData("lookup")]
    [InlineData("flatten")]
    public async Task CurrentTypedRepairPreservesRecordIdentityOrderAndDuplicates(string kind)
    {
        var state = PlannerFixture.Session(); state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var plan = LookupCompilationTests.Plan();
        var expected = plan.Root.Tasks[0].Outputs[0].Value;
        if (kind == "flatten")
        {
            plan.Inputs[0].Type = LookupCompilationTests.ArrayOf(plan.Inputs[0].Type);
            expected = new() { Kind = "flatten", Items = [LookupCompilationTests.Input("records")] };
        }
        var invalid = JsonSerializer.SerializeToNode(expected, PlanningJsonContext.Default.TaskValue)!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
        if (kind == "lookup") invalid.Items.Reverse(); else invalid.Items[0] = LookupCompilationTests.Input("selected");
        plan.Root.Tasks[0].Outputs[0] = new("rows", invalid); state.Plan = plan;
        state.Requirements = new() { Outputs = [new() { Name = "rows", Type = kind == "flatten" ? plan.Inputs[0].Type.Items! : plan.Inputs[0].Type }] };
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, state.Catalog).Diagnostics.ToList();
        state.EditablePaths = state.RevisionScope = ["/tasks/resolve/outputs/rows"];
        var request = RepairPatchTests.Request(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, true));
        Assert.True(PlanningRepairPatch.Verify(PlannerFixture.Clone(state), request));
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/tasks/resolve/outputs/rows", "replace",
            JsonNode.Parse(kind == "flatten" ? """{"kind":"flatten","items":[{"kind":"input","source":"records"}]}""" : """{"kind":"lookup","items":[{"kind":"input","source":"records"},{"kind":"input","source":"selected"}],"port":"key"}""")));
        var compiled = new TaskPlanCompiler().Compile(repaired, state.Catalog); Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog);
        var result = await LookupCompilationTests.Execute(yaml, new() {
            ["records"] = JsonNode.Parse(kind == "flatten" ? "[[{\"key\":\"a\",\"payload\":null}],[],[{\"key\":\"a\",\"payload\":null}]]" : "[{\"key\":\"a\",\"payload\":null}]"),
            ["selected"] = new JsonArray("a", "a") }, new WorkflowEngine { HumanInputProvider = new PlanningCorpus.Human(true) });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("[{\"key\":\"a\",\"payload\":null},{\"key\":\"a\",\"payload\":null}]", result.Outputs!["rows"]!.ToJsonString());
        Assert.Equal(0, state.ModelCalls);
    }

    [Theory]
    [InlineData("encoded", "string", "output:encoded.rows")]
    [InlineData("reversed", "array", "input:selected")]
    public async Task LookupDiagnosticIdentifiesOperandTypeAndProducer(string variant, string type, string producer)
    {
        var plan = LookupCompilationTests.Plan(); var lookup = plan.Root.Tasks[0].Outputs[0].Value;
        if (variant == "reversed") lookup.Items.Reverse();
        else
        {
            plan.Root.Tasks.Insert(0, new() { Id = "encoded", Kind = "value", Objective = "Explicit text encoding",
                Outputs = [new("rows", new() { Kind = "json", Items = [LookupCompilationTests.Input("records")] })] });
            lookup.Items[0] = new() { Kind = "output", Source = "encoded", Port = "rows" };
        }
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var error = Assert.Single(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_LOOKUP_INVALID");
        Assert.Equal("/tasks/resolve/outputs/rows", error.Location);
        Assert.Contains("items[0] expected records", error.Message); Assert.Contains("received \"" + type + "\"", error.Message);
        Assert.Contains(producer, error.Message); Assert.Contains("items[1] expected IDs", error.Message);
    }

    [Theory]
    [InlineData("alpha", "records")]
    [InlineData("beta", "résultats")]
    public async Task EncodedProducerNeedsExplicitAuthorityAndIsNeverAutomaticallyDecoded(string producer, string port)
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var plan = LookupCompilationTests.Plan(); plan.Inputs[0].Type = LookupCompilationTests.ArrayOf(plan.Inputs[0].Type);
        plan.Root.Tasks.Insert(0, new() { Id = producer, Kind = "value", Objective = "Preserve supplied records",
            Outputs = [new(port, new() { Kind = "json", Items = [LookupCompilationTests.Input("records")] })] });
        plan.Root.Tasks[1].Outputs[0].Value.Items[0] = new() { Kind = "output", Source = producer, Port = port };
        state.Plan = plan; state.Diagnostics = new TaskPlanCompiler().Compile(plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        var producerPath = "/tasks/" + producer + "/outputs/" + port;
        Assert.DoesNotContain(producerPath, state.RevisionScope);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var request = RepairPatchTests.Request(state);
        Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(state, new() { Edits = [new() {
            Slot = producerPath, Action = "replace", Value = JsonNode.Parse("""{"kind":"flatten","items":[{"kind":"input","source":"records"}]}""") }] }, request));
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));

        state.EditablePaths = [producerPath]; state.RevisionScope = [producerPath];
        request = RepairPatchTests.Request(state);
        var repaired = PlanningRepairPatch.Apply(state, new() { Edits = [new() { Slot = "s0", Action = "replace",
            Value = JsonNode.Parse("""{"kind":"flatten","items":[{"kind":"input","source":"records"}]}""") }] }, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog).Diagnostics);
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(JsonSerializer.Serialize(plan.Root.Tasks[1], PlanningJsonContext.Default.PlanTask),
            JsonSerializer.Serialize(repaired.Root.Tasks[1], PlanningJsonContext.Default.PlanTask));
        Assert.Equal("json", plan.Root.Tasks[0].Outputs[0].Value.Kind);
        Assert.Equal("flatten", repaired.Root.Tasks[0].Outputs[0].Value.Kind);
    }
}
