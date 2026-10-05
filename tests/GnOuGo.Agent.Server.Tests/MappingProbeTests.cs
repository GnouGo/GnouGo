using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MappingProbeTests
{
    [Theory]
    [InlineData("scalar")]
    [InlineData("extended")]
    [InlineData("each")]
    public async Task CompiledFixturesAreReviewableWithoutPlanningInference(string variant)
    {
        var (runtime, catalog, producer) = await Catalog();
        var state = MappingLiveEvaluation.Prepare(catalog, producer, "fixture", variant);
        PlanningArtifactApproval.Verify(state);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains("mapping.dynamic", state.Yaml);
        var run = new JsonObject { ["session"] = System.Text.Json.JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) };
        await Assert.ThrowsAsync<PlanningConflictException>(() => LiveWorkflowEvaluation.ApproveAsync(run,
            new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash() }, runtime, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MeasuredWrapperPreservesCapabilitiesAndUnknownAllowanceStopsBeforeDispatch(bool known)
    {
        var (_, catalog, producer) = await Catalog();
        var state = MappingLiveEvaluation.Prepare(catalog, producer, "fixture", "each");
        var client = new Client(known); var measured = new MappingLiveEvaluation.Measured(client, "measured");
        Assert.Equal(known ? 12000 : (int?)null, await measured.InputTokenAllowanceAsync("deployment", "pinned", 8192, TestContext.Current.CancellationToken));
        Assert.Equal("pinned", client.Model); Assert.Equal(8192, client.Output);
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("fixture", new() { Tools = [new() { Name = "observe", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"index":{"type":["integer","null"]}},"additionalProperties":false}""") }],
            ToolHandlers = new() { ["observe"] = _ => new() { Content = JsonValue.Create("displayName: observed") } } });
        var engine = new WorkflowEngine { LLMClient = measured, McpClientFactory = factory, HumanInputProvider = new GnOuGo.Planning.Examples.PlanningCorpus.Human(true), LlmDefaults = new() { Model = "pinned" } };
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await engine.ExecuteAsync(doc.Workflows["main"], new JsonObject { ["indices"] = new JsonArray(0, 1) }, TestContext.Current.CancellationToken);
        Assert.True(known == result.Success, result.Error?.Message);
        if (!known) { Assert.Equal("LLM_BUDGET_UNVERIFIABLE", result.Error!.Code); Assert.Equal(0, measured.Calls); }
        else { Assert.Equal(1, measured.Calls); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"rows":[{"name":"observed"},{"name":"observed"}]}"""), result.Outputs!["result"])); }
    }

    [Fact]
    public void LargeProbeChecksEveryOriginalValueAndWarmReuse()
    {
        var samples = MappingLiveEvaluation.Samples();
        var cold = samples.Single(s => s.Id == "each-cold");
        Assert.True(KeyVaultBenchmarkModel.ExecutionInputEstimate(cold.Source) > 96000);
        Assert.Equal(80, JsonNode.Parse(cold.Source)!.AsArray().Count);
        Assert.Equal(80, JsonNode.Parse(cold.Expected!)!["rows"]!.AsArray().Count);
        Assert.Equal(cold.Source, samples.Single(s => s.Id == "each-warm").Source);
    }

    private static async Task<(WorkflowPlanningRuntime, PlanningCatalog, string)> Catalog()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("fixture", new() { Tools = [new() { Name = "observe", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"index":{"type":["integer","null"]}},"additionalProperties":false}""") }] });
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), TestContext.Current.CancellationToken);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(TestContext.Current.CancellationToken))
            foreach (var cap in (await runtime.Capabilities.ListAsync(source.Id, null, TestContext.Current.CancellationToken)).Capabilities)
                catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(cap, TestContext.Current.CancellationToken));
        return (runtime, catalog, catalog.Capabilities.Single(c => c.Method == "observe").Id);
    }
    private sealed class Client(bool known) : ILLMClient, ILLMCapabilityResolver
    {
        public string? Model; public int Output;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int output, CancellationToken ct)
        { Model = model; Output = output; return Task.FromResult<int?>(known ? 12000 : null); }
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({name:m.text(source.observation,'displayName: (.+)')})" } });
    }
}
