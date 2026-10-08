using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class IndexedProjectionCompilationTests(Xunit.ITestOutputHelper output)
{
    private static TaskValue Ref(string id, string port) => ProductTransformationPlan.Ref(id, port);
    private static TaskPlan Plan(bool parallel, bool wrapper = true) => new()
    {
        Inputs = [new() { Name = "observations", Type = LookupCompilationTests.ArrayOf(ProductTransformationPlan.Obj(
            ("label", new() { Nullable = true }), ("children", LookupCompilationTests.ArrayOf(new() { Nullable = true })))) }],
        Root = new() { Tasks = [new() { Id = "identify", Kind = "foreach", Objective = "Retain original positions and observations", MaxItems = 2000,
            Parallel = parallel, Items = LookupCompilationTests.Input("observations"), Body = new()
            {
                Tasks = wrapper ? [new() { Id = "assemble", Kind = "value", Objective = "Attach position", Outputs = [new("row", Row())] }] : [],
                Outputs = [new("rows", wrapper ? Ref("assemble", "row") : Row())]
            } }], Outputs = [new("records", Ref("identify", "rows"))] }
    };
    private static TaskValue Row() => new() { Kind = "object", Members = [new("identity", new() { Kind = "index" }), new("original", new() { Kind = "item" })] };
    private static JsonArray Records(int count) => new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
        { ["label"] = i % 2 == 0 ? null : "duplicate", ["children"] = new JsonArray("same", null, "same") }).ToArray());
    private static async Task<(string Yaml, TaskCompilation Compilation)> Compile(TaskPlan plan, string? profile)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(
            new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var request = new PlanningRequest(); if (profile is not null) request.Options["compilation_profile"] = profile;
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var before = JsonSerializer.Serialize(compiled.Graph, PlanningJsonContext.Default.PlanningGraph);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "generated", TaskPlanCompiler.UsesNormalExports(request));
        Assert.Equal(before, JsonSerializer.Serialize(compiled.Graph, PlanningJsonContext.Default.PlanningGraph));
        Assert.DoesNotContain("checkedMapping", before);
        return (yaml, compiled);
    }

    [Theory]
    [InlineData(false, true, 1602)]
    [InlineData(true, true, 1602)]
    [InlineData(false, false, 1602)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 2)]
    public async Task OriginalIndicesFuseWithoutPerRecordInvocations(bool parallel, bool wrapper, int count)
    {
        var plan = Plan(parallel, wrapper); var (yaml, _) = await Compile(plan, TaskPlanCompiler.CompactProfile);
        var (old, _) = await Compile(plan, "compact-bindings-v2");
        Assert.Contains("loop.", old); Assert.DoesNotContain("loop.", yaml); Assert.DoesNotContain("workflow.call", yaml);
        Assert.DoesNotContain("mapping.dynamic", yaml); Assert.DoesNotContain("llm.call", yaml); Assert.True(yaml.Length < old.Length);
        var records = Records(count); var original = records.ToJsonString(); var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "test", RunId = "index" } };
        Assert.Equal(1000, engine.Limits.MaxLoopIterations);
        var result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = records }, engine);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(original, records.ToJsonString());
        var rows = result.Outputs!["records"]!.AsArray(); Assert.Equal(count, rows.Count);
        for (var i = 0; i < count; i++)
        { Assert.Equal(i, rows[i]!["identity"]!.GetValue<int>()); Assert.True(JsonNode.DeepEquals(records[i], rows[i]!["original"])); }
        var run = (await store.ReadAsync("test", "index", PlannerFixture.Ct))!;
        Assert.All(run.Invocations.Values, i => Assert.Equal("set", i.StepType)); Assert.True(run.Invocations.Count < 12);
        Assert.Equal(0, run.FinalizationStepsStarted);
        output.WriteLine($"items={count}; parallel={parallel}; YAML={Encoding.UTF8.GetByteCount(old)}->{Encoding.UTF8.GetByteCount(yaml)}; invocations={run.Invocations.Count}; journal={JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun).Length}");
    }

    [Theory]
    [InlineData("compact-bindings-v1")]
    [InlineData("compact-bindings-v2")]
    [InlineData(null)]
    public async Task HistoricalProfileSnapshot(string? profile)
    {
        var (yaml, _) = await Compile(Plan(false), profile);
        Assert.Contains("loop.sequential", yaml);
        // Captured by the same fixture against untouched commit 65b1c315.
        var expected = profile switch
        {
            "compact-bindings-v1" => "383d37741610f6f7c915873dfc8207fd1cb2685b2f70d5b5902945eb9861b873",
            "compact-bindings-v2" => "053805dfc79b11618bde21d47b24c6c690a9d90372b79ec368b1ff4a8903cc04",
            _ => "009a1466afb0ca54477f05ec0d5ed453d8ddcd55bb3b5d47dcc1271031f728c3"
        };
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryGuardsAndPerItemGuardsKeepTheirSemantics(bool perItem)
    {
        var plan = Plan(false); plan.Inputs.Add(new() { Name = "complete", Type = new() { Kind = "boolean" } });
        if (perItem) plan.Root.Tasks[0].Body!.Tasks[0].Requires = LookupCompilationTests.Input("complete");
        else plan.Root.Tasks[0].Requires = LookupCompilationTests.Input("complete");
        var (yaml, _) = await Compile(plan, TaskPlanCompiler.CompactProfile);
        Assert.Equal(perItem, yaml.Contains("loop.sequential", StringComparison.Ordinal));
        foreach (var complete in new[] { false, true })
        {
            var result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(3), ["complete"] = complete }, new());
            Assert.Equal(complete, result.Success); if (!complete) Assert.Null(result.Outputs);
        }
    }

    [Fact]
    public async Task ScopedCapturedValuesAndRepeatedLookupKeepOriginalIdentities()
    {
        var plan = Plan(true); plan.Inputs.Add(new() { Name = "shared", Type = new() { Nullable = true } });
        plan.Root.Tasks[0].Body!.Tasks[0].Outputs[0].Value.Members.Add(new("context", LookupCompilationTests.Input("shared")));
        var inner = plan.Root;
        plan.Root = new() { Tasks = [new() { Id = "scope", Kind = "sequence", Objective = "Preserve scope", Body = inner },
            new() { Id = "resolve", Kind = "value", Objective = "Reconnect identities in selected order", Outputs = [new("selected",
                LookupCompilationTests.Lookup(Ref("scope", "records"), new() { Kind = "array", Items = [new() { Kind = "number", Number = 2 }, new() { Kind = "number", Number = 0 }, new() { Kind = "number", Number = 2 }] }, "identity"))] }],
            Outputs = [new("records", Ref("resolve", "selected"))] };
        var (yaml, _) = await Compile(plan, TaskPlanCompiler.CompactProfile);
        var result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(3), ["shared"] = null }, new());
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { 2, 0, 2 }, result.Outputs!["records"]!.AsArray().Select(r => r!["identity"]!.GetValue<int>()));
        Assert.All(result.Outputs["records"]!.AsArray(), r => Assert.True(r!.AsObject().ContainsKey("context") && r["context"] is null));
    }

    [Fact]
    public async Task CollectionAndExpressionBoundsRemainEnforcedWithoutPublication()
    {
        var plan = Plan(false); plan.Root.Tasks[0].MaxItems = 2;
        var (yaml, _) = await Compile(plan, TaskPlanCompiler.CompactProfile);
        var result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(3) }, new());
        Assert.False(result.Success); Assert.Null(result.Outputs);
        (yaml, _) = await Compile(Plan(false), TaskPlanCompiler.CompactProfile);
        result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(30) }, new() { Limits = new() { MaxExpressionStatements = 20 } });
        Assert.False(result.Success); Assert.Null(result.Outputs);
    }

    [Fact]
    public async Task IndexedProjectionReducesJournalWorkAndPreservesTheOldIterationCeiling()
    {
        var metrics = new List<(int Invocations, int Bytes)>();
        foreach (var profile in new[] { "compact-bindings-v2", TaskPlanCompiler.CompactProfile })
        {
            var (yaml, _) = await Compile(Plan(false), profile);
            var store = new InMemoryWorkflowRunStore(); var engine = new WorkflowEngine { RunStore = store,
                Limits = new() { TenantId = "test", RunId = profile } };
            var result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(12) }, engine);
            Assert.True(result.Success, result.Error?.Message);
            var saved = (await store.ReadAsync("test", profile, PlannerFixture.Ct))!;
            metrics.Add((saved.Invocations.Count, JsonSerializer.Serialize(saved, WorkflowRunJsonContext.Default.WorkflowRun).Length));
            // A smaller host ceiling reproduces the old boundary without allocating
            // thousands of legacy snapshots. The new path still uses checked sets.
            result = await LookupCompilationTests.Execute(yaml, new() { ["observations"] = Records(4) }, new() { Limits = new() { MaxLoopIterations = 3 } });
            Assert.Equal(profile == TaskPlanCompiler.CompactProfile, result.Success);
            if (!result.Success) Assert.Equal(ErrorCodes.LoopLimit, result.Error!.Code);
        }
        Assert.True(metrics[1].Invocations < metrics[0].Invocations); Assert.True(metrics[1].Bytes < metrics[0].Bytes);
        output.WriteLine($"items=12; invocations={metrics[0].Invocations}->{metrics[1].Invocations}; logical_journal_bytes={metrics[0].Bytes}->{metrics[1].Bytes}");
    }
}
