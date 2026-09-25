using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPlanStabilizationTests
{
    private static JsonObject Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TaskPlanStabilization", name + ".json")))!.AsObject();
    private static TaskPlan Plan(JsonObject fixture) => fixture["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
    private static PlanningCatalog Catalog(JsonObject fixture) => fixture["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;

    [Theory]
    [InlineData("review_french-1")]
    [InlineData("review_french-2")]
    public async Task ReportsIndependentInputFailuresTogetherAndAllowsOnlyTheirRepair(string name)
    {
        var fixture = Fixture(name); var plan = Plan(fixture);
        var compilation = new TaskPlanCompiler().Compile(plan, Catalog(fixture));
        Assert.Equal(new[] { "/inputs/pr_url", "/inputs/review_text" }, compilation.Diagnostics.Select(d => d.Location));
        Assert.All(compilation.Diagnostics, d => Assert.Equal("TASK_DEFAULT_REQUIRED", d.Code));
        var scope = TaskPlanRevisions.Scope(plan, compilation.Diagnostics);
        var repaired = Clone(plan); repaired.Inputs.ForEach(i => i.Required = true);
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        var catalog = Catalog(fixture);
        var compiled = new TaskPlanCompiler().Compile(repaired, catalog); Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = new PlanningBenchmarkCases.Environment("review_french").Factory() }, (_, _) => Task.CompletedTask);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compiled.Graph!)), PlannerFixture.Ct));
        repaired.Root.Outputs.Clear();
        var rejected = TaskPlanRevisions.Validate(plan, repaired, scope).ToArray();
        Assert.Contains(rejected, d => d.Location.StartsWith("/root/outputs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetainedCompositeOutputPreservesEveryIndependentExecutionVariant()
    {
        var fixture = Fixture("review_distractors-3"); var catalog = Catalog(fixture);
        var compilation = new TaskPlanCompiler().Compile(Plan(fixture), catalog);
        Assert.Empty(compilation.Diagnostics);
        PlanningConfirmationGuards.Apply(compilation.Graph!, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = new PlanningBenchmarkCases.Environment("review_distractors").Factory() }, (_, _) => Task.CompletedTask);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var variant in new[] { "nominal", "failure", "incomplete", "rejected", "head_changed" })
        {
            var sample = new PlanningBenchmarkCases.Environment("review_distractors", variant);
            var result = await new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = new PlanningCorpus.Human() }
                .ExecuteAsync(document.Workflows["main"], PlanningBenchmarkCases.Inputs("review_distractors", variant), PlannerFixture.Ct);
            Assert.True(sample.Verify(result), variant + ": " + result.Error?.Message + "; " + string.Join(",", sample.Violations));
        }
    }

    [Fact]
    public async Task CleanupAllowsMinimalCorrectionAndRejectsRetainedWholesaleRewrites()
    {
        var fixture = Fixture("review_distractors-1"); var original = Plan(fixture); var catalog = Catalog(fixture);
        original.Choices.ForEach(c => c.Selected = c.Recommended);
        var failed = new TaskPlanCompiler().Compile(original, catalog);
        Assert.Contains(failed.Diagnostics, d => d.Code == "TASK_PRESENCE_SCOPE");
        var scope = TaskPlanRevisions.Scope(original, failed.Diagnostics);
        Assert.Equal(new[] { "/tasks/always_cleanup_workspace/condition" }, scope);
        var repaired = Clone(original); repaired.Root.Always[0].Condition!.Source = original.Root.Tasks[0].Id;
        Assert.Empty(TaskPlanRevisions.Validate(original, repaired, scope));
        var compilation = new TaskPlanCompiler().Compile(repaired, catalog); Assert.Empty(compilation.Diagnostics);
        PlanningConfirmationGuards.Apply(compilation.Graph!, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = new PlanningBenchmarkCases.Environment("review_distractors").Factory() }, (_, _) => Task.CompletedTask);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        foreach (var response in fixture["responses"]!.AsArray().Where(r => r?["proposal"]?["plan"] is not null).Skip(1))
        {
            var rewrite = response!["proposal"]!["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
            rewrite.Choices.ForEach(c => c.Selected = c.Recommended);
            Assert.Contains(TaskPlanRevisions.Validate(original, rewrite, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
        }
    }

    [Fact]
    public void ExhaustedDiscoverySchemaForbidsRetainedMixedActions()
    {
        var fixture = Fixture("review_french-1");
        var state = PlannerFixture.Session();
        state.Discovery.Sources = [new("source", "Source")];
        state.Discovery.Pages = [new("source", null, [], null)];
        var schema = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Equal("null", schema["properties"]!["discoveryRequests"]!["type"]!.ToString());
        foreach (var response in fixture["responses"]!.AsArray().Where(r => r?["proposal"]?["sourceId"] is not null))
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response!["proposal"], schema));
    }

    [Fact]
    public void InputSchemaRequiresDefaultsWithoutRequiringDefaultsForOptionalObjectFields()
    {
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        var inputSchema = PlanningSchemas.Ref("input"); inputSchema["$defs"] = schema["$defs"]!.DeepClone();
        var input = JsonNode.Parse("""{"name":"value","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":false,"default":null}""")!;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(input, inputSchema));
        input["default"] = new JsonObject { ["kind"] = "string", ["text"] = "fallback" };
        Assert.Empty(PlanningContractValidation.ValidateInstance(input, inputSchema));
        input["default"] = new JsonObject { ["kind"] = "input", ["source"] = "another" };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(input, inputSchema));
        var typeSchema = PlanningSchemas.Ref("businessType"); typeSchema["$defs"] = schema["$defs"]!.DeepClone();
        input["default"] = null;
        var businessType = new JsonObject { ["kind"] = "object", ["nullable"] = false, ["items"] = null, ["fields"] = new JsonArray(input.DeepClone()) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(businessType, typeSchema));
    }

    [Fact]
    public void GroupInputRepairCannotAlterRootOrAnotherGroupsSameNamedInput()
    {
        var plan = new TaskPlan { Inputs = [new() { Name = "value" }], Groups = [new() { Id = "group", Inputs = [new() { Name = "value", Required = false }] }],
            Root = new() { Tasks = [new() { Id = "call", Objective = "Call group", Kind = "call", Group = "group", Inputs = [new("value", PlanningCorpus.String("value"))] }] } };
        var failed = new TaskPlanCompiler().Compile(plan, new());
        Assert.Equal("/groups/group/inputs/value", Assert.Single(failed.Diagnostics).Location);
        var scope = TaskPlanRevisions.Scope(plan, failed.Diagnostics);
        var repair = Clone(plan); repair.Groups[0].Inputs[0].Required = true;
        Assert.Empty(TaskPlanRevisions.Validate(plan, repair, scope));
        repair.Inputs[0].Type.Kind = "boolean";
        Assert.Contains(TaskPlanRevisions.Validate(plan, repair, scope), d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location.StartsWith("/inputs/value", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedOutputRepairPreservesTheOtherBranchAndItsInterface()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "container", Objective = "Export a value", Kind = "sequence", Body = new()
        { Tasks = PlanningCorpus.Greeting().Root.Tasks, Outputs = [new("message", PlanningCorpus.Business("output", "greet", "missing"))] } }] } };
        var failed = new TaskPlanCompiler().Compile(plan, new());
        Assert.Equal("/tasks/container/body/outputs/message", Assert.Single(failed.Diagnostics).Location);
        var scope = TaskPlanRevisions.Scope(plan, failed.Diagnostics);
        var repair = Clone(plan); repair.Root.Tasks[0].Body!.Outputs[0].Value.Port = "message";
        Assert.Empty(TaskPlanRevisions.Validate(plan, repair, scope));
        repair.Root.Tasks[0].Body!.Tasks[0].Outputs[0].Value.Text = "Unrelated change";
        Assert.Contains(TaskPlanRevisions.Validate(plan, repair, scope), d => d.Location.StartsWith("/tasks/greet/", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnosticsRetainBusinessPortsAfterConfirmationWrapping()
    {
        var fixture = Fixture("review_distractors-3"); var catalog = Catalog(fixture);
        var compilation = new TaskPlanCompiler().Compile(Plan(fixture), catalog); Assert.Empty(compilation.Diagnostics);
        PlanningConfirmationGuards.Apply(compilation.Graph!, catalog);
        Assert.Equal("/root/outputs/review_result", compilation.Locate(new("TEST", "/workflows/1/outputs/0", "Test")).Location);
        var stage = compilation.Graph!.Workflows[1].Steps[0];
        Assert.Equal("/tasks/clone_repository_once/inputs/pr_url", compilation.Locate(new("TEST", "/workflows/1/steps/0/input/members/0/value/members/0/value", "Test")).Location);
        Assert.NotNull(stage.CapabilityId);
    }

    [Fact]
    public async Task CompositeExportsRunAfterCleanupAndKeepNestedSchemas()
    {
        var plan = new TaskPlan { Root = new() { Tasks = PlanningCorpus.Greeting().Root.Tasks,
            Always = [new() { Id = "cleanup", Objective = "Finish cleanup", Kind = "value", Outputs = [new("done", new() { Kind = "boolean", Boolean = true })] }],
            Outputs = [new("result", new() { Kind = "object", Members = [new("cleaned", PlanningCorpus.Business("output", "cleanup", "done")),
                new("messages", new() { Kind = "array", Items = [new() { Kind = "object", Members = [new("text", PlanningCorpus.Business("output", "greet", "message"))] }] })] })] } };
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], null, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Outputs!["result"]!["cleaned"]!.GetValue<bool>());
        Assert.Equal("Hello", result.Outputs["result"]!["messages"]![0]!["text"]!.ToString());
    }

    [Fact]
    public void DiscoverySchemaAdvertisesOnlyUnconsumedPages()
    {
        var state = PlannerFixture.Session();
        state.Discovery.Sources = [new("complete", "Complete"), new("paged", "Paged"), new("unseen", "Unseen")];
        state.Discovery.Pages = [new("complete", null, [], null), new("paged", null, [], "next")];
        var schema = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, true));
        var batch = schema["properties"]!["discoveryRequests"]!.DeepClone().AsObject();
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""[{"sourceId":"paged","cursor":"next"},{"sourceId":"unseen","cursor":null}]"""), batch));
        foreach (var invalid in new[] { """[{"sourceId":"complete","cursor":null}]""", """[{"sourceId":"unseen","cursor":"next"}]""", """[{"sourceId":"paged","cursor":"invented"}]""" })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse(invalid), batch));
    }

    [Fact]
    public async Task PendingRequestRetainsItsSchemaWhenDiscoveryChanges()
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new IOException("Interrupted") };
        var state = await PlannerFixture.RunAsync(runtime);
        var pending = state.PendingCall!;
        var schema = pending.Request.StructuredOutputSchema!.ToJsonString();
        state.Discovery.Sources.Add(new("new-source", "New source"));
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Proposal);
        state.Status = PlanningStatus.Generating;
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(pending.Id, runtime.Calls[^1].ClientRequestId);
        Assert.Equal(schema, runtime.Calls[^1].StructuredOutputSchema!.ToJsonString());
    }
}
