using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class WholeWorkflowResultContractTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WholeIsolatedCollectionExportKeepsClosedOuterShapeAndNullableMembers(bool parallel)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = new TaskPlan { Inputs = [new() { Name = "records", Type = new() { Kind = "array", MinItems = 0, MaxItems = 3,
            Items = new() { Kind = "object", Fields = [new() { Name = "label", Type = new() { Kind = "string", Nullable = true } }] } } }],
            Root = new() { Tasks = [new() { Id = "envelope", Kind = "sequence", Objective = "Preserve the complete collection result", Body = new() {
                Tasks = [new() { Id = "collect", Kind = "foreach", Objective = "Retain each record in order", Parallel = parallel, MaxItems = 3,
                    Items = new() { Kind = "input", Source = "records" }, Body = new() {
                        Tasks = [new() { Id = "nested", Kind = "sequence", Objective = "Preserve the original typed item", Body = new() {
                            Outputs = [new("record", new() { Kind = "item" })] } }],
                        Outputs = [new("record", new() { Kind = "output", Source = "nested", Port = "record" }),
                            new("status", new() { Kind = "string", Text = "observed" })] } }],
                Outputs = [new("observations", new() { Kind = "output", Source = "collect" })] } }],
                Outputs = [new("result", new() { Kind = "output", Source = "envelope", Port = "observations" })] } };
        var request = new PlanningRequest { Options = new() { ["compilation_profile"] = TaskPlanCompiler.CompactProfile } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compiled.Diagnostics); Assert.NotNull(compiled.Graph);
        Assert.Empty(PlanningGraphValidation.Validate(compiled.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph, catalog));
        var inputs = new JsonObject { ["records"] = new JsonArray(new JsonObject { ["label"] = null }, new JsonObject { ["label"] = null }) };
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph, catalog);
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], inputs, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"record":[{"label":null},{"label":null}],"status":["observed","observed"]}"""), result.Outputs!["result"]));
    }
}
