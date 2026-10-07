using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConditionalInterfaceTests
{
    private static TaskPlan Fixture(bool corrected, string prefix)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConditionalInterfaces",
            corrected ? "explicit-ports.json" : "differing-objects.json")))!["plan"]!;
        Rename(json);
        return json.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        void Rename(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "id", "source", "name", "port", "operation" })
                    if (obj[key] is JsonValue value) obj[key] = prefix + value.ToString();
                foreach (var property in obj) Rename(property.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Rename(child);
        }
    }
    private static async Task<(WorkflowEngine Engine, PlanningCatalog Catalog, List<string> Calls)> Environment(string prefix)
    {
        var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
        var input = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [prefix + "selector"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray(prefix + "selector"), ["additionalProperties"] = false };
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = prefix + "dispatch", InputSchema = input,
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }, EffectKind = "none" }],
            ToolHandlers = new() { [prefix + "dispatch"] = data => { calls.Add(data![prefix + "selector"]!.ToString()); return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        // Use arbitrary published operation identities as well as task/port names.
        return (engine, catalog, calls);
    }
    private static void BindOperation(TaskPlan plan, PlanningCatalog catalog) =>
        TaskPlanRevisions.Tasks(plan).Single(t => t.Kind == "operation").Operation = catalog.Capabilities.Single(c => c.Server == "arbitrary").Id;

    [Theory]
    [InlineData("alpha_")][InlineData("omega_")]
    public async Task DifferentlyShapedWholeExportsRemainRejectedWithBranchContracts(string prefix)
    {
        var (_, catalog, _) = await Environment(prefix); var plan = Fixture(false, prefix); BindOperation(plan, catalog);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        foreach (var code in new[] { "TASK_INPUT_TYPE", "TASK_CONDITION_TYPE" })
        {
            var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == code);
            Assert.Contains($"/tasks/{prefix}choose/body/outputs/{prefix}state", diagnostic.Message);
            Assert.Contains($"/tasks/{prefix}choose/otherwise/outputs/{prefix}state", diagnostic.Message);
            Assert.Contains($"{prefix}selector:[\"string\",\"null\"] required", diagnostic.Message);
            Assert.Contains("explicit common consumer-facing interface", diagnostic.Message);
        }
        var scope = TaskPlanRevisions.Scope(plan, result.Diagnostics);
        var corrected = Fixture(true, prefix); BindOperation(corrected, catalog);
        Assert.Contains(TaskPlanRevisions.Validate(plan, corrected, scope.ToList(), catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData("alpha_", true, false)][InlineData("alpha_", false, false)]
    [InlineData("omega_", true, true)][InlineData("omega_", false, true)]
    public async Task ExplicitPortsReachReviewWithoutRepairAndExecuteExactCapturedValues(string prefix, bool branch, bool absent)
    {
        var (engine, catalog, calls) = await Environment(prefix); var plan = Fixture(true, prefix); BindOperation(plan, catalog);
        var runtime = new TestRuntime(engine) { Proposal = new() { Plan = plan, Requirements = new() { Summary = "Preserve the selected observations and use only an available selector", Inputs = plan.Inputs,
            Outcomes = [new("selected", "Preserve the selected observations")] } } };
        var session = PlannerFixture.Session(); session.Catalog = catalog; session.Request.Prompt = runtime.Proposal.Requirements.Summary;
        session.Request.Generation.MaxInputTokensPerRequest = 96000;
        var reviewed = await PlannerFixture.RunAsync(runtime, session);
        Assert.True(reviewed.Status == PlanningStatus.FinalReview, string.Join("; ", reviewed.Diagnostics));
        Assert.Single(runtime.Calls); Assert.Equal(0, reviewed.ReplanAttempts);
        var before = JsonSerializer.Serialize(reviewed, PlanningJsonContext.Default.PlanningSession);
        var restored = PlannerFixture.Clone(reviewed);
        Assert.Equal(before, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(reviewed.Yaml!));
        JsonObject Observation(string label, bool extra) { var data = new JsonObject { [prefix + "ready"] = true, [prefix + "blocked"] = false,
            [prefix + "selector"] = absent ? null : label + " ${literal}", [prefix + "secondary"] = null,
            [prefix + "address"] = "https://local.invalid/" + label, [prefix + "label"] = label + " é 東京" }; if (extra) data[prefix + "extra"] = true; return data; }
        var first = Observation("first", true); var second = Observation("second", false);
        var run = await engine.ExecuteAsync(doc.Workflows["main"], new JsonObject { [prefix + "branch"] = branch, [prefix + "first"] = first, [prefix + "second"] = second }, PlannerFixture.Ct);
        Assert.True(run.Success, run.Error?.Message);
        var expected = (branch ? first : second).DeepClone().AsObject(); expected.Remove(prefix + "extra");
        Assert.True(JsonNode.DeepEquals(expected, run.Outputs), run.Outputs?.ToJsonString());
        Assert.Equal(absent ? [] : new[] { expected[prefix + "selector"]!.ToString() }, calls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CompatibleObjectExportsAndOpaquePassThroughRetainTheirContracts(bool opaque)
    {
        var (_, catalog, _) = await Environment(""); var plan = Fixture(false, "");
        plan.Root.Tasks.RemoveRange(1, 2);
        plan.Inputs.Single(i => i.Name == "first").Type = plan.Inputs.Single(i => i.Name == "second").Type;
        if (opaque)
            foreach (var input in plan.Inputs.Where(i => i.Name != "branch")) input.Type = new() { Kind = "any" };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.NotNull(compiled.Graph);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph, catalog)));
        var observed = new JsonObject { ["ready"] = true, ["blocked"] = false, ["selector"] = null, ["secondary"] = null, ["address"] = "observed", ["label"] = "exact" };
        var result = await new WorkflowEngine().ExecuteAsync(doc.Workflows["main"], new JsonObject { ["branch"] = false,
            ["first"] = observed.DeepClone(), ["second"] = observed.DeepClone() }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(observed, result.Outputs!["selected"]));
        if (opaque)
        {
            plan.Root.Outputs[0] = new("selected", new() { Kind = "field", Port = "label", Items = [plan.Root.Outputs[0].Value] });
            Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_FIELD_TYPE");
        }
    }

    [Theory]
    [InlineData("boolean_guard", "TASK_INPUT_TYPE")]
    [InlineData("missing", "TASK_BRANCH_OUTPUTS")]
    [InlineData("incompatible", "TASK_CONDITION_TYPE")]
    [InlineData("invented", "TASK_FIELD_UNKNOWN")]
    public async Task DirectInterfacesDoNotWeakenNullabilityOrInventBranchValues(string variant, string code)
    {
        var (_, catalog, _) = await Environment(""); var plan = Fixture(true, ""); BindOperation(plan, catalog);
        var choose = plan.Root.Tasks[0].Body!.Tasks[0];
        switch (variant)
        {
            case "boolean_guard": plan.Root.Tasks[1].Condition = new() { Kind = "output", Source = "container", Port = "ready" }; break;
            case "missing": choose.Otherwise!.Outputs.RemoveAt(0); break;
            case "incompatible": plan.Inputs.Single(i => i.Name == "second").Type.Fields.Single(i => i.Name == "ready").Type.Kind = "string"; break;
            case "invented": choose.Otherwise!.Outputs[0].Value.Port = "unobserved"; break;
        }
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compiled.Graph); Assert.Contains(compiled.Diagnostics, d => d.Code == code);
    }
}
