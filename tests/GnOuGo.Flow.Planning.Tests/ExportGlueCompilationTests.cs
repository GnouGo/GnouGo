using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ExportGlueCompilationTests(Xunit.ITestOutputHelper output)
{
    [Fact]
    public async Task FreshProfileAndDescriptionsRemainBoundToApprovalAndExplicitRevision()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("compact-bindings-v3", state.Request.Options["compilation_profile"]!.ToString());
        Assert.Contains("description:", state.Yaml);
        var hash = state.ComputeArtifactHash(); var stored = PlannerFixture.Clone(state);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision,
            ArtifactHash = hash, ReviewedRequirementIds = ["message"] }, runtime, PlannerFixture.Ct);
        PlanningArtifactApproval.Verify(PlannerFixture.Clone(state));
        var altered = PlannerFixture.Clone(state); altered.Yaml = altered.Yaml!.Replace("description:", "description: Changed ", StringComparison.Ordinal);
        Assert.NotEqual(hash, altered.ComputeArtifactHash()); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(altered));
        var approvedYaml = state.Yaml;
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Use a warmer greeting." }, runtime, PlannerFixture.Ct);
        Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml); Assert.Single(runtime.Calls);
        Assert.Equal(approvedYaml, stored.Yaml); Assert.Equal(hash, stored.ComputeArtifactHash());
    }

    [Theory]
    [InlineData(false, 60, "read_record")]
    [InlineData(true, 60, "arbitrary_fetch")]
    [InlineData(false, 0, "read_record")]
    [InlineData(false, 8, "read_record")]
    [InlineData(true, 8, "arbitrary_fetch")]
    public async Task NormalExportsDoNotConsumeCleanupAllowance(bool parallel, int count, string operation)
    {
        var closed = 0; var reads = 0;
        var factory = new InMemoryMcpClientFactory();
        var contract = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":["string","null"]},"children":{"type":"array","items":{"type":["string","null"]}}},"additionalProperties":false}""")!;
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        factory.RegisterServer("generic", new() { Tools = [
            new() { Name = operation, InputSchema = empty, OutputSchema = contract, EffectKind = "read" },
            new() { Name = "finish", InputSchema = empty, OutputSchema = empty, EffectKind = "lifecycle" }],
            ToolHandlers = new() {
                [operation] = _ => { Interlocked.Increment(ref reads); return new() { Content = new JsonObject { ["label"] = null, ["children"] = new JsonArray("x", null, "x") } }; },
                ["finish"] = _ => { Interlocked.Increment(ref closed); return new() { Content = new JsonObject() }; }
            } });
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        catalog.Policy.RequireExternalConfirmation = false;
        TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "output", Source = "observe" }] };
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "pages", Kind = "foreach", Objective = "Observe every item", Parallel = parallel,
            MaxItems = 100, MaxConcurrency = 4, Items = new() { Kind = "array", Items = Enumerable.Range(0, count).Select(_ => new TaskValue { Kind = "string", Text = "duplicate" }).ToList() },
            Body = new() { Tasks = [new() { Id = "observe", Objective = "Observe declared values", Operation = catalog.Capabilities.Single(c => c.Method == operation).Id }],
                Outputs = [new("row", new() { Kind = "object", Members = [new("label", Field("label")), new("children", Field("children"))] }), new("label", Field("label"))] } }],
            Always = [new() { Id = "close", Objective = "Release the observed resource", Operation = catalog.Capabilities.Single(c => c.Method == "finish").Id }],
            Outputs = [new("rows", new() { Kind = "output", Source = "pages", Port = "row" })] } };
        var old = new TaskPlanCompiler().Compile(plan, catalog, true);
        var current = new TaskPlanCompiler().Compile(plan, catalog, true, true);
        Assert.Empty(current.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(current.Graph!, catalog));
        var compiler = new PlanningGraphCompiler(); var previous = compiler.Compile(old.Graph!, catalog);
        var yaml = compiler.Compile(current.Graph!, catalog, "generated", true);
        var plain = compiler.Compile(current.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        Assert.Single(current.Graph!.Workflows.SelectMany(w => w.Finally));
        Assert.Equal("mcp.call", current.Graph.Workflows.SelectMany(w => w.Finally).Single().Type);
        var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { McpClientFactory = factory, RunStore = store, Limits = new() { TenantId = "tenant", RunId = "current" } };
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(count, reads); Assert.Equal(1, closed);
        var expected = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)new JsonObject { ["label"] = null, ["children"] = new JsonArray("x", null, "x") }).ToArray());
        Assert.True(JsonNode.DeepEquals(expected, result.Outputs!["rows"]));
        var run = (await store.ReadAsync("tenant", "current", PlannerFixture.Ct))!;
        Assert.Equal(1, run.FinalizationStepsStarted); Assert.True(run.StepsStarted < run.Limits.MaxTotalStepsExecuted);
        Assert.All(run.Invocations.Values, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));
        Assert.DoesNotContain(run.Invocations.Values, i => i.StepType is "llm.call" or "mapping.dynamic");
        static IEnumerable<CompiledStep> All(IEnumerable<CompiledStep> steps) => steps.SelectMany(s => new[] { s }
            .Concat(All(s.Steps ?? [])).Concat(All(s.Default ?? [])).Concat(All(s.Branches?.SelectMany(b => b) ?? []))
            .Concat(All(s.Cases?.SelectMany(c => c.Steps) ?? [])));
        var oldDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(previous));
        var oldNodes = All(oldDocument.Workflows.Values.SelectMany(w => w.Steps.Concat(w.Finally))).ToArray();
        var newNodes = All(document.Workflows.Values.SelectMany(w => w.Steps.Concat(w.Finally))).ToArray();
        var oldSteps = oldNodes.Length; var newSteps = newNodes.Length;
        Assert.True(newSteps < oldSteps); Assert.True(yaml.Length < previous.Length);
        output.WriteLine($"sets={oldNodes.Count(n => n.Type == "set")}->{newNodes.Count(n => n.Type == "set")}; workflows={oldDocument.Workflows.Count}->{document.Workflows.Count}; lines={previous.Count(c => c == '\n')}->{yaml.Count(c => c == '\n')}");
        output.WriteLine($"items={count}; parallel={parallel}; steps={oldSteps}->{newSteps}; YAML={previous.Length}->{yaml.Length}; descriptions={yaml.Length-plain.Length}; invocations={run.Invocations.Count}; finalization={run.FinalizationStepsStarted}; journal={JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun).Length}");
        Assert.Equal(previous, compiler.Compile(new TaskPlanCompiler().Compile(plan, catalog, new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v1" } }).Graph!, catalog));
        if ((!parallel && count > 50) || count == 8)
        {
            reads = closed = 0;
            var legacyStore = new InMemoryWorkflowRunStore();
            var legacy = new WorkflowEngine { McpClientFactory = factory, RunStore = legacyStore, Limits = new() { TenantId = "tenant", RunId = "legacy" } };
            var failed = await legacy.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(previous)).Workflows["main"], new JsonObject(), PlannerFixture.Ct);
            var oldRun = (await legacyStore.ReadAsync("tenant", "legacy", PlannerFixture.Ct))!;
            if (count > 50) { Assert.False(failed.Success); Assert.Equal(0, closed); Assert.Equal(50, oldRun.FinalizationStepsStarted); }
            else
            {
                Assert.True(failed.Success, failed.Error?.Message); Assert.Equal(1, closed);
                Assert.True(JsonNode.DeepEquals(failed.Outputs, result.Outputs));
                Assert.True(run.Invocations.Count < oldRun.Invocations.Count);
                var oldBytes = JsonSerializer.Serialize(oldRun, WorkflowRunJsonContext.Default.WorkflowRun).Length;
                var newBytes = JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun).Length;
                Assert.True(newBytes < oldBytes);
                output.WriteLine($"comparable invocations={oldRun.Invocations.Count}->{run.Invocations.Count}; normal={oldRun.StepsStarted}->{run.StepsStarted}; finalization={oldRun.FinalizationStepsStarted}->{run.FinalizationStepsStarted}; journal={oldBytes}->{newBytes}");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupDependentExportsStayAfterTheirProducer(bool nullable)
    {
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "normal", Kind = "value", Objective = "Keep normal value", Outputs = [new("value", new() { Kind = "string", Text = "before" })] }],
            Always = [new() { Id = "final", Kind = "value", Objective = "Preserve final value", Outputs = [new("value", nullable ? new() { Kind = "null" } : new() { Kind = "string", Text = "after" })] }],
            Outputs = [new("mixed", new() { Kind = "object", Members = [new("a", new() { Kind = "output", Source = "normal", Port = "value" }), new("b", new() { Kind = "output", Source = "final", Port = "value" })] })] } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, true, true); Assert.Empty(compiled.Diagnostics);
        Assert.True(compiled.Graph!.Workflows[0].Finally.Count >= 2);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph, catalog, "generated", true);
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("before", result.Outputs!["mixed"]!["a"]!.ToString());
        Assert.Equal(nullable ? null : "after", result.Outputs["mixed"]!["b"]?.ToString());
    }
}
