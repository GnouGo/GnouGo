using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactBindingCompilationTests(Xunit.ITestOutputHelper output)
{
    private static TaskValue Ref(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static TaskValue Field(TaskValue value, string port) => new() { Kind = "field", Port = port, Items = [value] };
    private static TaskPlan Plan(bool guarded, bool parallel) => new()
    {
        Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields = [
            new() { Name = "label" }, new() { Name = "noise" }, new() { Name = "children", Type = new() { Kind = "array", Items = new() { Kind = "string", Nullable = true } } }] } } }],
        Root = new() { Tasks = new[] { "first", "second" }.Select(id => new PlanTask
        {
            Id = id, Kind = "foreach", Objective = "Preserve selected observed fields", MaxItems = 100, Parallel = parallel,
            Items = new() { Kind = "input", Source = "records" }, Body = new() { Tasks = [new() {
                Id = id + "_view", Kind = "value", Objective = "Copy fields",
                Requires = guarded ? new() { Kind = "boolean", Boolean = true } : null,
                Outputs = [new("row", new() { Kind = "object", Members = [new("name", Field(new() { Kind = "item" }, "label")),
                    new("nested", Field(new() { Kind = "item" }, "children")), new("label", new() { Kind = "string", Text = "explicit business literal" })] })] }], Outputs = [new("rows", Ref(id + "_view", "row"))] }
        }).ToList(), Outputs = [new("rows", Ref("second", "rows"))] }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyLoopsFuseWithoutPerItemInvocationsAndPreserveExactValues(bool parallel)
    {
        var (catalog, _) = await Setup(); var plan = Plan(false, parallel);
        var legacy = new TaskPlanCompiler().Compile(plan, catalog); var current = new TaskPlanCompiler().Compile(plan, catalog, true);
        Assert.Empty(current.Diagnostics);
        var compiler = new PlanningGraphCompiler(); var oldYaml = compiler.Compile(legacy.Graph!, catalog); var yaml = compiler.Compile(current.Graph!, catalog);
        output.WriteLine($"copy loops: legacy YAML {oldYaml.Length} chars, compact {yaml.Length} chars; legacy workflows {legacy.Graph!.Workflows.Count}, compiled workflows 1");
        Assert.True(yaml.Length < oldYaml.Length, $"before={oldYaml.Length}, after={yaml.Length}");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)); Assert.Single(document.Workflows);
        Assert.DoesNotContain("workflow.call", yaml); Assert.DoesNotContain("loop.", yaml); Assert.DoesNotContain("llm.call", yaml);
        foreach (var count in new[] { 0, 2, 40 })
        {
            var records = Records(count, 200000);
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["records"] = records }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(JsonNode.DeepEquals(Expected(count), result.Outputs!["rows"]));
            Assert.Equal(document.Workflows["main"].Steps.Count, result.StepResults.Count);
        }
        // Profile omission reproduces the historical graph and YAML exactly.
        Assert.Equal(oldYaml, compiler.Compile(new TaskPlanCompiler().Compile(plan, catalog).Graph!, catalog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsecutiveBusinessLoopsDoNotCollectPriorLoopSnapshots(bool parallel)
    {
        var (catalog, _) = await Setup(); var compiled = new TaskPlanCompiler().Compile(Plan(true, parallel), catalog, true);
        Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        long priorBytes = 0;
        foreach (var count in new[] { 4, 8 })
        {
            var store = new InMemoryWorkflowRunStore();
            var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "test", RunId = "isolation" } };
            var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["records"] = Records(count, 1000) }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(Expected(count), result.Outputs!["rows"]));
            var run = (await store.ReadAsync("test", "isolation", PlannerFixture.Ct))!;
            var loops = run.Invocations.Values.Where(i => i.StepType is "loop.sequential" or "loop.parallel").ToArray(); Assert.Equal(2, loops.Length);
            long bytes = 0;
            foreach (var loop in loops)
            {
                var rows = loop.Output!["results"]!.AsArray(); Assert.Equal(count, rows.Count);
                Assert.All(rows, row => {
                    Assert.Single(row!.AsObject(), p => !p.Key.StartsWith("__", StringComparison.Ordinal));
                    Assert.All(row.AsObject().Where(p => p.Key.StartsWith("__", StringComparison.Ordinal)), p => Assert.True(p.Value!.ToJsonString().Length < 100));
                }); // This iteration's child call and the small legacy control input only.
                bytes += rows.ToJsonString().Length;
            }
            if (priorBytes > 0) Assert.InRange(bytes, priorBytes, priorBytes * 2 + 200);
            output.WriteLine($"isolated loops: parallel={parallel}, items={count}, collected bytes={bytes}");
            priorBytes = bytes;
        }
    }

    private static JsonArray Records(int count, int noise) => new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject {
        ["label"] = "same", ["noise"] = new string('x', noise), ["children"] = new JsonArray("a", null, "a") }).ToArray());
    private static JsonArray Expected(int count) => new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject {
        ["name"] = "same", ["nested"] = new JsonArray("a", null, "a"), ["label"] = "explicit business literal" }).ToArray());
    private static async Task<(PlanningCatalog, WorkflowPlanningRuntime)> Setup()
    {
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        return (await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct), runtime);
    }
}
