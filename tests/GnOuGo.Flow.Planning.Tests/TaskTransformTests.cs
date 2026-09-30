using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Planning.Examples;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskTransformTests
{
    [Fact]
    public async Task SemanticTransformLowersToValidatedStructuredCall()
    {
        var plan = JsonSerializer.Deserialize("""
            {"root":{"tasks":[{"id":"extract","kind":"transform","objective":"Extract product URLs from the supplied HTML.",
              "inputs":[{"name":"html","value":{"kind":"string","text":"<a href='/one'>One</a>"}}],
              "resultType":{"kind":"object","fields":[{"name":"urls","type":{"kind":"array","items":{"kind":"string"}}}]}}],
              "outputs":[{"name":"urls","value":{"kind":"output","source":"extract","port":"urls"}}]}}
            """, PlanningJsonContext.Default.TaskPlan)!;
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Graph);
        Assert.Equal(new[] { "template.render", "llm.call" }, result.Graph.Workflows[0].Steps.Select(s => s.Type));
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(result.Graph, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(result.Graph)), PlannerFixture.Ct));
    }

    [Theory]
    [InlineData("nominal")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("renamed")]
    public async Task BrowserTransformIterationTablePreservesIndependentOracle(string variant)
    {
        var fixture = variant == "renamed" ? new ProductTransformationFixture { BrowserSource = "alpha", DocumentSource = "beta", ReadMethod = "observe", WriteMethod = "persist", CloseMethod = "release", ReverseTools = true } : new ProductTransformationFixture(variant);
        var (plan, catalog, runtime) = await Setup(fixture);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compilation.Graph!, catalog));
        PlanningConfirmationGuards.Apply(compilation.Graph!, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var again = new TaskPlanCompiler().Compile(plan, catalog).Graph!; PlanningConfirmationGuards.Apply(again, catalog);
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(again, catalog));
        var result = await Execute(fixture, compilation.Graph!, catalog);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(fixture.VerifyText());
        Assert.Equal(fixture.ExpectedRows.Length + 1, fixture.Calls.Count);
        Assert.All(fixture.Calls, c => Assert.True(c.StructuredOutputStrict));
        Assert.Equal("close", fixture.Effects[^1]);
        Assert.Equal(fixture.ExpectedRows.Length, fixture.Effects.Count(e => e.StartsWith("read:", StringComparison.Ordinal)));
        Assert.Single(fixture.Effects, e => e.StartsWith("write:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetainedValueAliasesDoNotExecuteTheirObjectives()
    {
        var fixture = new ProductTransformationFixture(); var (plan, catalog, _) = await Setup(fixture);
        var urls = plan.Root.Tasks[1]; urls.Kind = "value"; urls.ResultType = null;
        urls.Outputs = [new("urls", ProductTransformationPlan.Ref("search", "content"))]; urls.Inputs.Clear();
        var table = plan.Root.Tasks[3]; table.Kind = "value"; table.ResultType = null;
        table.Outputs = [new("content", new() { Kind = "array", Items = [ProductTransformationPlan.Text("uncalculated")] })]; table.Inputs.Clear();
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_ITEMS_INVALID" && d.Location == "/tasks/products/items");
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/write/inputs/content");
        Assert.Empty(fixture.Calls); Assert.Empty(fixture.Effects);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("cancel")]
    [InlineData("refuse")]
    public async Task FailureCannotWriteSuccessAndAlwaysCloses(string failure)
    {
        var fixture = new ProductTransformationFixture { InvalidResponse = failure == "invalid" };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        if (failure == "cancel") fixture.CancelDuringExtraction = cancellation;
        var (plan, catalog, _) = await Setup(fixture);
        var graph = new TaskPlanCompiler().Compile(plan, catalog).Graph!;
        PlanningConfirmationGuards.Apply(graph, catalog);
        var result = await Execute(fixture, graph, catalog, failure != "refuse", cancellation.Token);
        Assert.False(result.Success);
        Assert.Null(fixture.WrittenContent);
        if (failure != "refuse") Assert.Equal("close", fixture.Effects[^1]);
        else Assert.Empty(fixture.Effects);
    }

    [Fact]
    public async Task IndependentOracleRejectsWellTypedFabrication()
    {
        var fixture = new ProductTransformationFixture { FabricatedResponse = true };
        var (plan, catalog, _) = await Setup(fixture);
        var result = await Execute(fixture, new TaskPlanCompiler().Compile(plan, catalog).Graph!, catalog);
        Assert.True(result.Success); // Schema validity alone is not factual correctness.
        Assert.False(fixture.VerifyText());
    }

    [Fact]
    public async Task TransformPolicyAndTypeErrorsAreCollectedAndRepairIsMinimal()
    {
        var (plan, catalog, _) = await Setup(new());
        var task = plan.Root.Tasks[1]; task.Inputs[0] = new("html", ProductTransformationPlan.Ref("missing", "content"));
        task.ResultType!.Fields[0].Type.Items = new() { Kind = "any" };
        task.ResultType.Fields[0].Required = false;
        task.ResultType.Fields[0].Default = ProductTransformationPlan.Text("invented");
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compilation.Graph); Assert.True(compilation.Diagnostics.Count >= 4);
        var scope = TaskPlanRevisions.Scope(plan, compilation.Diagnostics);
        var repaired = Clone(plan); var fixedTask = repaired.Root.Tasks[1];
        fixedTask.Inputs[0] = new("html", ProductTransformationPlan.Ref("search", "content"));
        fixedTask.ResultType!.Fields[0].Required = true; fixedTask.ResultType.Fields[0].Default = null; fixedTask.ResultType.Fields[0].Type.Items!.Kind = "string";
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, catalog).Diagnostics);
        repaired.Root.Tasks[3].Objective = "Unrelated rewrite";
        Assert.Contains(TaskPlanRevisions.Validate(plan, repaired, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
        repaired = Clone(plan); repaired.Root.Tasks[1].ResultType!.Fields[0].Name = "renamed";
        Assert.Contains(TaskPlanRevisions.Validate(plan, repaired, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
        catalog.AllowedStepTypes.Remove("llm.call");
        Assert.Contains(new TaskPlanCompiler().Compile(ProductTransformationPlan.Create(catalog, new()), catalog).Diagnostics, d => d.Code == "TASK_TRANSFORM_DENIED");
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("missing_items")]
    [InlineData("nullable_root")]
    [InlineData("optional")]
    [InlineData("default")]
    [InlineData("empty")]
    public async Task ResponseSchemaAndPreflightRejectIncompleteTransformResults(string defect)
    {
        var (plan, catalog, _) = await Setup(new()); var task = plan.Root.Tasks[1];
        switch (defect)
        {
            case "opaque": task.ResultType!.Fields[0].Type.Items!.Kind = "any"; break;
            case "missing_items": task.ResultType!.Fields[0].Type.Items = null; break;
            case "nullable_root": task.ResultType!.Nullable = true; break;
            case "optional": task.ResultType!.Fields[0].Required = false; break;
            case "default": task.ResultType!.Fields[0].Default = ProductTransformationPlan.Text("invented"); break;
            case "empty": task.ResultType!.Fields.Clear(); break;
        }
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_TRANSFORM_TYPE");
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        var contract = schema["$defs"]!["task"]!["anyOf"]!.AsArray().Single(n => n!["properties"]!["kind"]!["enum"]![0]!.ToString() == "transform")!["properties"]!["resultType"]!.DeepClone().AsObject();
        contract["$defs"] = schema["$defs"]!.DeepClone();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(task.ResultType, PlanningJsonContext.Default.TaskType), contract));
    }

    [Theory]
    [InlineData("text", "text")]
    [InlineData("json", "json")]
    [InlineData(null, null)]
    public void SharedTemplateContractOnlyGuaranteesKnownMode(string? mode, string? field)
    {
        var schema = TemplateRenderContract.OutputSchema(mode);
        var required = schema["required"]!.AsArray().Select(n => n!.ToString()).ToArray();
        Assert.Contains("meta", required);
        if (field is null) { Assert.DoesNotContain("text", required); Assert.DoesNotContain("json", required); }
        else Assert.Contains(field, required);
    }

    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog, WorkflowPlanningRuntime Runtime)> Setup(ProductTransformationFixture fixture)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = fixture.Factory() }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        return (ProductTransformationPlan.Create(catalog, fixture), catalog, runtime);
    }
    private static async Task<GnOuGo.Flow.Core.Models.RunResult> Execute(ProductTransformationFixture fixture, PlanningGraph graph, PlanningCatalog catalog, bool confirm = true, CancellationToken? ct = null)
    {
        PlanningConfirmationGuards.Apply(graph, catalog);
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var engine = new WorkflowEngine { McpClientFactory = fixture.Factory(), LLMClient = fixture, LlmDefaults = new() { Model = "mock" }, HumanInputProvider = new PlanningCorpus.Human(confirm) };
        return await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["search"] = ProductTransformationFixture.SearchUrl }, ct ?? PlannerFixture.Ct);
    }
}
