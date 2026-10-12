using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactProjectionCompilationTests(Xunit.ITestOutputHelper output)
{
    private static TaskValue Ref(string id, string port) => new() { Kind = "output", Source = id, Port = port };
    private static TaskPlan Plan(bool parallel) => new()
    {
        Inputs = [new() { Name = "observations", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
            [new() { Name = "label", Type = new() { Nullable = true } }, new() { Name = "amount", Type = new() { Kind = "number" } },
             new() { Name = "children", Type = new() { Kind = "array", Items = new() { Nullable = true } } }] } } }],
        Root = new() { Tasks = [new() { Id = "project", Kind = "foreach", Objective = "Retain exact records and original positions", Parallel = parallel,
            MaxItems = 2000, Items = new() { Kind = "input", Source = "observations" }, Body = new() { Outputs =
                [new("records", new() { Kind = "item" }), new("positions", new() { Kind = "index" })] } }],
            Outputs = [new("records", Ref("project", "records")), new("positions", Ref("project", "positions"))] }
    };
    private static JsonArray Records(int count) => new(Enumerable.Range(0, count).Select(i => JsonNode.Parse(
        "{\"label\":" + (i % 2 == 0 ? "null" : JsonSerializer.Serialize("same ` ${literal} \\ é " + new string('z', 700))) +
        ",\"amount\":9007199254740993.00,\"children\":[\"duplicate\",null,\"duplicate\"]}")).ToArray());
    private static async Task<(PlanningCatalog Catalog, TaskCompilation Compilation)> Compile(TaskPlan plan)
    {
        var catalog = await TaskPlanCompilerTests.Catalog(new(new(), (_, _) => Task.CompletedTask));
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v10" } });
        Assert.Empty(compilation.Diagnostics); return (catalog, compilation);
    }
    private static string Yaml(PlanningGraph graph, PlanningCatalog catalog, bool compact) => new PlanningGraphCompiler()
        .Compile(graph, catalog, "projection", true, true, true, true, true, true, true, compact);

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(1602)]
    public async Task V11UnwrapsSinglePropertyLiteralsWithoutDroppingProjectionContracts(int count)
    {
        var (catalog, compilation) = await Compile(Plan(false)); var graph = compilation.Graph!;
        var before = Yaml(graph, catalog, true);
        var after = new PlanningGraphCompiler().Compile(graph, catalog, "projection", true, true, true, true, true, true, true, true, true);
        Assert.Contains("expression_contracts:", after);
        Assert.True(after.Length < before.Length);
        JsonNode? expected = null;
        foreach (var yaml in new[] { before, after })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                new JsonObject { ["observations"] = Records(count) }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            if (expected is null) expected = result.Outputs; else Assert.Equal(expected.ToJsonString(), result.Outputs!.ToJsonString());
            Assert.Equal(Enumerable.Range(0, count), result.Outputs!["positions"]!.AsArray().Select(n => n!.GetValue<int>()));
        }
    }

    [Theory]
    [InlineData("nominal")]
    [InlineData("alternate")]
    public async Task V13NullableCaptureKeepsPhysicalReferencesInItsLexicalScope(string variant)
    {
        var sample = new GnOuGo.Planning.Examples.PlanningBenchmarkCases.Environment("nullable_defaults", variant);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = sample.Factory() }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var plan = GnOuGo.Planning.Examples.PlanningCorpus.Tasks("nullable_defaults", catalog);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v13" } });
        Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog, "nullable", true, true, true, true, true, true, true, true, true, true, true);
        if (Environment.GetEnvironmentVariable("GNOU_GO_V13_EVIDENCE") is { Length: > 0 } evidence) File.WriteAllText(Path.Combine(evidence, "nullable.yaml"), yaml);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var result = await LookupCompilationTests.Execute(yaml, GnOuGo.Planning.Examples.PlanningBenchmarkCases.Inputs("nullable_defaults", variant), new() { McpClientFactory = sample.Factory() });
        Assert.True(sample.Verify(result), result.Error?.Message);
    }

    [Theory]
    [InlineData("nominal")]
    [InlineData("alternate")]
    public async Task GuardedNullableCaptureKeepsItsCheckedExportBoundary(string variant)
    {
        var sample = new GnOuGo.Planning.Examples.PlanningBenchmarkCases.Environment("nullable_defaults", variant);
        var engine = new WorkflowEngine { McpClientFactory = sample.Factory() };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var plan = GnOuGo.Planning.Examples.PlanningCorpus.Tasks("nullable_defaults", catalog);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, new PlanningRequest
            { Options = new() { ["compilation_profile"] = "compact-bindings-v10" } });
        Assert.Empty(compilation.Diagnostics);
        JsonNode? expected = null;
        foreach (var profile in new[] { 9, 10, 13, 14 })
        {
            var yaml = profile < 13 ? Yaml(compilation.Graph!, catalog, profile >= 10) :
                new PlanningGraphCompiler().Compile(compilation.Graph!, catalog, "projection", true, true, true, true, true, true, true, true, true, true, true, profile >= 14);
            Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog,
                PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
            var observed = new GnOuGo.Planning.Examples.PlanningBenchmarkCases.Environment("nullable_defaults", variant);
            var result = await LookupCompilationTests.Execute(yaml,
                GnOuGo.Planning.Examples.PlanningBenchmarkCases.Inputs("nullable_defaults", variant),
                new() { McpClientFactory = observed.Factory() });
            Assert.True(observed.Verify(result), result.Error?.Message);
            if (expected is null) expected = result.Outputs; else Assert.True(JsonNode.DeepEquals(expected, result.Outputs));
        }

    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(false, 3, false)] [InlineData(true, 3, false)] [InlineData(false, 1602, false)]
    [InlineData(false, 1602, true)] [InlineData(false, 0, true)]
    public async Task DeclaredProjectionPortsAvoidUnobservedCallEnvelopes(bool parallel, int count, bool single)
    {
        var plan = Plan(parallel);
        if (single) { plan.Root.Tasks[0].Body!.Outputs.RemoveAt(1); plan.Root.Outputs.RemoveAt(1); }
        var (catalog, compilation) = await Compile(plan); var graph = compilation.Graph!;
        var saved = JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph);
        var old = Yaml(graph, catalog, false); var current = Yaml(graph, catalog, true);
        Assert.Equal(saved, JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph));
        Assert.Equal(old, Yaml(graph, catalog, false));
        Assert.True(current.Length < old.Length, $"v9={old.Length}; v10={current.Length}\n{current}");
        Assert.DoesNotContain("outputs:", current.Split("output_schema:")[0]);
        Assert.DoesNotContain("checkedMapping(", current); Assert.Contains("expression_contracts:", current);
        if (!single) Assert.Contains("(item, index)", current); Assert.DoesNotContain("(item,\n", current);
        var input = new JsonObject { ["observations"] = Records(count) }; var original = input.ToJsonString();
        var allocations = new List<long>(); var invocations = new List<int>(); var journals = new List<int>(); JsonNode? expected = null;
        foreach (var yaml in new[] { old, current })
        {
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            var store = new InMemoryWorkflowRunStore(); var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "test", RunId = "projection" } };
            var allocated = GC.GetTotalAllocatedBytes(true);
            var result = await engine.ExecuteAsync(document.Workflows["main"], input, PlannerFixture.Ct);
            allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
            Assert.True(result.Success, result.Error?.Message); Assert.Equal(original, input.ToJsonString());
            Assert.Equal(input["observations"]!.ToJsonString(), result.Outputs!["records"]!.ToJsonString());
            if (!single) Assert.Equal(Enumerable.Range(0, count), result.Outputs["positions"]!.AsArray().Select(n => n!.GetValue<int>()));
            if (expected is null) expected = result.Outputs; else Assert.True(JsonNode.DeepEquals(expected, result.Outputs));
            var run = (await store.ReadAsync("test", "projection", PlannerFixture.Ct))!;
            invocations.Add(run.Invocations.Count); journals.Add(JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun).Length);
            Assert.All(run.Invocations.Values, r => Assert.Equal("set", r.StepType));
            Assert.True((await engine.ResumeAsync("test", "projection", run.Revision, document.Workflows["main"], PlannerFixture.Ct)).Success);
        }
        Assert.Equal(invocations[0], invocations[1]);
        Assert.True(journals[1] < journals[0]);
        output.WriteLine($"projection items={count}; single={single}; YAML={old.Length}→{current.Length}; allocations={allocations[0]}→{allocations[1]}; invocations={invocations[0]}→{invocations[1]}; logicalJournal={journals[0]}→{journals[1]}");
    }

    [Theory]
    [InlineData("whole")] [InlineData("presence")]
    public async Task ObservableLoopEnvelopesRemainIntact(string observation)
    {
        var (catalog, compilation) = await Compile(Plan(false)); var graph = compilation.Graph!; var main = graph.Workflows.Single(w => w.Key == graph.Entrypoint);
        var loop = PlanningGraphCompiler.Enumerate(main.Steps).Single(n => n.InternalRole == "typed_index_projection");
        main.Outputs.Clear();
        main.Outputs.Add(new() { Name = "observed", Value = new() { Kind = observation == "whole" ? "output" : "present", Source = loop.Key }, Schema = observation == "whole" ? new() { Contract = JsonNode.Parse("""{"type":"object","properties":{"results":{"type":"array","items":{"x-gnougo-opaque":true}},"count":{"type":"integer"}},"required":["results","count"]}""")!.AsObject() } : new() { Type = "boolean" } });
        var old = await LookupCompilationTests.Execute(Yaml(graph, catalog, false), new() { ["observations"] = Records(3) }, new());
        var current = await LookupCompilationTests.Execute(Yaml(graph, catalog, true), new() { ["observations"] = Records(3) }, new());
        Assert.True(old.Success, old.Error?.Message); Assert.True(current.Success, current.Error?.Message);
        Assert.True(JsonNode.DeepEquals(old.Outputs, current.Outputs));
        if (observation == "whole") Assert.Contains("outputs", current.Outputs!.ToJsonString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EntryChecksAndResourceLimitsStillFailAtomically(bool authorized)
    {
        var plan = Plan(false); plan.Inputs.Add(new() { Name = "authorized", Type = new() { Kind = "boolean" } });
        plan.Root.Tasks[0].Requires = new() { Kind = "input", Source = "authorized" };
        var (catalog, compilation) = await Compile(plan);
        foreach (var compact in new[] { false, true })
        {
            var yaml = Yaml(compilation.Graph!, catalog, compact);
            var result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(2), ["authorized"] = authorized }, new());
            Assert.Equal(authorized, result.Success); if (!authorized) Assert.Null(result.Outputs);
            var limited = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(30), ["authorized"] = true },
                new() { Limits = new() { MaxExpressionStatements = 20 } });
            Assert.False(limited.Success); Assert.Null(limited.Outputs);
        }
    }
}
