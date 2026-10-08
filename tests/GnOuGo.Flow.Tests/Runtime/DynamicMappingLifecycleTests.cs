using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class DynamicMappingCollectionTests
{
    [Fact]
    public async Task RetainedProgramsWithInvalidSignaturesAreRejectedWithoutInferenceOrItemExecution()
    {
        await using var stream = typeof(DynamicMappingCollectionTests).Assembly.GetManifestResourceStream("MappingRetainedPrograms")!;
        var receipts = (await JsonNode.ParseAsync(stream, cancellationToken: Ct))!.AsArray();
        var invalid = 0;
        foreach (var receipt in receipts)
        {
            var script = receipt!["response"]!["script"]!.ToString();
            try { JintSandbox.ValidateMapping(script); }
            catch (WorkflowRuntimeException error)
            {
                Assert.Equal("program", error.Details!["mapping_failure_kind"]!.ToString()); invalid++;
            }
        }
        Assert.Equal(20, receipts.Count); Assert.True(invalid >= 6, invalid.ToString());
        var lastProduct = receipts.Single(r => r!["invocation"]!.ToString().EndsWith("n_575964d6f73194a2/mapping/9", StringComparison.Ordinal))!;
        var lastError = Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(lastProduct["response"]!["script"]!.ToString()));
        Assert.Equal("select", lastError.Details!["helper"]!.ToString());
    }

    [Fact]
    public async Task GlobalSyntaxRepairDoesNotInventAMandatoryOversizedFailingItem()
    {
        var values = JsonNode.Parse("[{\"label\":\"a\",\"huge\":\"\"},{\"label\":\"b\"}]")!.AsArray();
        values[0]!["huge"] = new string('x', 20000);
        var model = new Model("m.select(item,[['label']])", "m.select(item,[['label']],false)");
        var result = await AdaptiveRun(AdaptiveEngine(model, calls: 2), values);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Requests.Count);
        Assert.Contains("\"failing_index\":null", model.Requests[1].Prompt);
        Assert.DoesNotContain(new string('x', 100), model.Requests[1].Prompt);
    }
    [Theory]
    [InlineData("m.select(item,[['label']])")]
    [InlineData("m.select(item,[['label']],1)")]
    [InlineData("m.select(item,[item.paths],false)")]
    [InlineData("m.has(item,0)?item.label:item.label")]
    [InlineData("m.text(item.label,'(.*)',1.5)")]
    [InlineData("m.trim(item.label,item.label)")]
    [InlineData("m.parse()")]
    [InlineData("m.trim('invented')")]
    [InlineData("m.resolveUri(item.label,'https://invented.invalid')")]
    [InlineData("m.test(item.label,'(?=a)a')?item.label:item.label")]
    [InlineData("m.has(item,'missing')?m.select(item,[['label']]):item.label")]
    [InlineData("item.records.map(r=>r.label+'invented')")]
    public async Task InvalidProgramsRepairGloballyBeforeAnyItemRegardlessOfShapes(string script)
    {
        var invalid = Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(script));
        Assert.Equal("program", invalid.Details!["mapping_failure_kind"]!.ToString());
        var store = new InMemoryWorkflowRunStore(); var model = new Model(script, "item.label");
        var engine = AdaptiveEngine(model, calls: 2); engine.RunStore = store; engine.Limits.RunId = "invalid-program";
        var items = JsonNode.Parse("[{\"label\":\"first\"},{\"label\":\"second\",\"different\":[]},{\"label\":\"third\",\"extra\":{}}]")!.AsArray();
        var result = await AdaptiveRun(engine, items);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Requests.Count);
        Assert.Equal(new[] { "first", "second", "third" }, Result(result).Select(x => x!.ToString()));
        var run = (await store.ReadAsync("tenant", "invalid-program", Ct))!;
        var repair = run.Invocations["/workflow/main/step/map"].Control["mapping_adaptive_1"]!;
        Assert.Equal("program_repair", repair["phase"]!.ToString());
        Assert.Equal(new[] { 0, 1, 2 }, repair["indices"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Contains("\"assigned_count\":3", model.Requests[1].Prompt);
        Assert.Equal(2, run.ModelUsage!.Calls);
    }

    [Fact]
    public async Task InvalidSpecializationRepairsItsWholeAssignmentWithoutRegrouping()
    {
        var model = new Model("item.label", "m.select(item,[['name']])", "item.name", "item.title");
        var store = new InMemoryWorkflowRunStore(); var engine = AdaptiveEngine(model); engine.RunStore = store; engine.Limits.RunId = "specialization";
        var result = await AdaptiveRun(engine, JsonNode.Parse("[{\"label\":\"a\"},{\"name\":\"b\"},{\"title\":\"c\"},{\"name\":\"d\"}]")!.AsArray());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(new[] { "a", "b", "c", "d" }, Result(result).Select(x => x!.ToString()));
        var run = (await store.ReadAsync("tenant", "specialization", Ct))!;
        var controls = run.Invocations["/workflow/main/step/map"].Control;
        Assert.Equal(new[] { "generation", "specialization", "program_repair", "specialization" },
            Enumerable.Range(0, 4).Select(i => controls["mapping_adaptive_" + i]!["phase"]!.ToString()));
        Assert.True(JsonNode.DeepEquals(controls["mapping_adaptive_1"]!["indices"], controls["mapping_adaptive_2"]!["indices"]));
        Assert.Equal(new[] { 1, 3 }, controls["mapping_adaptive_2"]!["indices"]!.AsArray().Select(x => x!.GetValue<int>()));
    }

    [Fact]
    public async Task LateProgramDefectDiscardsItsEarlierApparentSuccesses()
    {
        var model = new Model("m.has(item,'label')?item.label:'fabricated'", "m.has(item,'label')?item.label:item.name");
        var store = new InMemoryWorkflowRunStore(); var engine = AdaptiveEngine(model); engine.RunStore = store; engine.Limits.RunId = "atomic-program";
        var result = await AdaptiveRun(engine, JsonNode.Parse("[{\"label\":\"a\"},{\"name\":\"b\"}]")!.AsArray());
        Assert.True(result.Success, result.Error?.Message);
        var saved = (await store.ReadAsync("tenant", "atomic-program", Ct))!;
        Assert.Equal(new[] { 0, 1 }, saved.Invocations["/workflow/main/step/map"].Control["mapping_adaptive_1"]!["indices"]!.AsArray().Select(i => i!.GetValue<int>()));
    }

    [Fact]
    public async Task ClosedTargetRejectsExtraFieldsRatherThanSilentlyDeletingThem()
    {
        var document = AdaptiveDocument();
        document.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"] =
            JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"label\":{\"type\":\"string\"}},\"required\":[\"label\"],\"additionalProperties\":false}");
        var model = new Model("({label:item.label,unused:item.extra})", "({label:item.label})");
        var result = await AdaptiveRun(AdaptiveEngine(model, calls: 2), JsonNode.Parse("[{\"label\":\"a\",\"extra\":\"retained\"},{\"label\":\"b\",\"extra\":\"retained\"}]")!.AsArray(), document);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Requests.Count);
        Assert.All(Result(result), item => Assert.Equal(new[] { "label" }, item!.AsObject().Select(p => p.Key)));
        Assert.Contains("additional", model.Requests[1].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("statements")]
    [InlineData("time")]
    [InlineData("allocated_memory")]
    [InlineData("materialized_memory")]
    public void SandboxReportsTheExactCumulativeResource(string resource)
    {
        var allowance = new JintSandbox.MappingAllowance(resource == "statements" ? 0 : 1000,
            resource == "time" ? TimeSpan.Zero : TimeSpan.FromSeconds(5), resource == "allocated_memory" ? 1 : 50_000_000);
        allowance.Start();
        if (resource == "time")
            Assert.True(SpinWait.SpinUntil(() => allowance.Snapshot()["elapsed_ms"]!.GetValue<double>() > 0,
                TimeSpan.FromSeconds(1)));
        byte[]? allocation = resource == "allocated_memory" ? new byte[1024] : null;
        if (resource == "materialized_memory") allowance.ImportedBytes = 50_000_001;
        var error = Assert.Throws<WorkflowRuntimeException>(allowance.Check);
        allowance.Stop(); GC.KeepAlive(allocation);
        Assert.Equal(resource, error.Details!["exhausted_resource"]!.ToString());
        var snapshot = error.Details["sandbox"]!.AsObject();
        Assert.All(new[] { "statements", "statement_limit", "elapsed_ms", "time_limit_ms", "allocated_bytes", "materialized_bytes", "output_bytes", "memory_limit_bytes" },
            field => Assert.True(snapshot.ContainsKey(field), field));
        Assert.Equal("CONTRACT_UNSATISFIED", error.Code);
    }

    [Fact]
    public async Task ExhaustedResourceIsDurableAndDoesNotSpendAnotherInferenceCall()
    {
        var store = new InMemoryWorkflowRunStore(); var model = new Model("item.label");
        var engine = AdaptiveEngine(model); engine.RunStore = store; engine.Limits.RunId = "resource"; engine.Limits.MaxExpressionStatements = 1;
        var result = await AdaptiveRun(engine, JsonNode.Parse("[{\"label\":\"a\"},{\"label\":\"b\"}]")!.AsArray());
        Assert.False(result.Success); Assert.Single(model.Requests); Assert.Equal("statements", result.Error!.Details!["exhausted_resource"]!.ToString());
        var saved = (await store.ReadAsync("tenant", "resource", Ct))!;
        Assert.Equal("statements", saved.Invocations["/workflow/main/step/map"].Error!.Details!["exhausted_resource"]!.ToString());
        Assert.DoesNotContain(result.StepResults, s => s.StepId == "consumer");
        var recovered = await new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "resource", saved.Revision,
            new WorkflowCompiler().Compile(AdaptiveDocument()).Workflows["main"], Ct);
        Assert.False(recovered.Success); Assert.Single(model.Requests);
    }
}
