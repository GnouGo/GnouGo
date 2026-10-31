using GnOuGo.Planning.Examples;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class CodeReviewCompositionTests
{
    [Theory]
    [InlineData("approve")]
    [InlineData("findings")]
    [InlineData("failed_checks")]
    [InlineData("denied")]
    [InlineData("unknown")]
    public async Task ObservedUrlFactsAndProducerRevisionDriveOneExactLocalClone(string variant)
    {
        using var fixture = new Execution(variant, exactRevision: true);
        var model = new ReferenceModel(); var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { McpClientFactory = fixture.Factory, LLMClient = model, RunStore = store,
            LLMUsageBudget = new(new() { MaxCalls = 1 }),
            LlmDefaults = new() { Model = "offline" }, Limits = new() { TenantId = "fixture", RunId = Guid.NewGuid().ToString("N") } };
        var actual = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await actual.DiscoverAsync(new(), Ct);
        foreach (var source in await actual.Capabilities.ListSourcesAsync(Ct))
            foreach (var entry in (await actual.Capabilities.ListAsync(source.Id, null, Ct)).Capabilities)
                catalog.Capabilities.Add(await actual.Capabilities.ResolveAsync(entry, Ct));
        catalog.Policy.RequireExternalConfirmation = false;
        var proposal = Proposal(catalog);
        proposal.Plan!.Root.Tasks.Insert(0, new()
        {
            Id = "extract_identifier", Kind = "transform", Mode = "extract", Objective = "Extract the numeric change identifier present in the supplied URL.",
            Inputs = [new("changeUrl", Input("changeUrl"))], ResultType = Obj(("number", new() { Kind = "integer" }))
        });
        proposal.Plan.Root.Tasks.Single(t => t.Id == "inspect").Inputs.Add(new("number", new()
        { Kind = "field", Port = "number", Items = [new() { Kind = "output", Source = "extract_identifier" }] }));
        proposal.Plan.Root.Tasks.Single(t => t.Id == "clone").Inputs.Add(new("revision", Ref("inspect", "revision")));
        var state = RecordedPlanCompilation.CompileStatic(proposal, catalog, new() { TenantId = "fixture",
            Prompt = proposal.Requirements!.Summary, Options = new() { ["compilation_profile"] = "compact-bindings-v15" } });
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(0, state.ModelCalls); Assert.Null(state.ApprovedHash);
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!)).Workflows["main"];
        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["changeUrl"] = Execution.ChangeUrl, ["instructions"] = Execution.Instructions }, Ct);
        var success = variant is "approve" or "findings" or "failed_checks";
        Assert.True(result.Success == success, result.Error?.Message);
        Assert.Equal(1, model.Calls); Assert.Equal(variant == "denied" ? 0 : 1, fixture.CloneCount);
        Assert.Equal(variant is "denied" or "unknown" ? 0 : 1, fixture.CleanupCount);
        if (variant != "denied") Assert.Equal(new[] { "install", "lint", "unit", "integration" }, fixture.Checks);
        if (success)
        {
            Assert.Equal(Execution.Summary, result.Outputs!["summary"]!.ToString());
            Assert.Equal(variant == "approve" ? "APPROVE" : "REQUEST_CHANGES", fixture.SubmittedEvent);
            Assert.Equal(variant == "findings" ? new[] { Execution.InlineText, Execution.GlobalText, Execution.InlineText } : [], fixture.Posted.Select(p => p["text"]!.ToString()));
        }
        else Assert.DoesNotContain("submit", fixture.Calls);
        Assert.Equal(variant == "unknown", Directory.Exists(fixture.Target));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(fixture.Root, "keep.txt")));
        var calls = fixture.Calls.ToArray();
        var saved = (await store.ReadAsync("fixture", engine.Limits.RunId!, Ct))!;
        var resumed = await new WorkflowEngine { RunStore = store }.ResumeAsync("fixture", saved.RunId, saved.Revision, workflow, Ct);
        Assert.Equal(result.Success, resumed.Success); Assert.Equal(calls, fixture.Calls); Assert.Equal(1, model.Calls);
        if (Environment.GetEnvironmentVariable("GNOU_GO_EXTRACTION_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, variant + "-proposal.json"), JsonSerializer.Serialize(proposal, PlanningFixtureJsonContext.Default.TaskPlanFixture), Ct);
            await File.WriteAllTextAsync(Path.Combine(evidence, variant + ".yaml"), state.Yaml, Ct);
            await File.WriteAllTextAsync(Path.Combine(evidence, variant + "-result.json"), new JsonObject
            { ["success"] = result.Success, ["planningCalls"] = 0, ["mappingCalls"] = model.Calls, ["mappingRepairs"] = 0,
                ["cloneCount"] = fixture.CloneCount, ["cleanupCount"] = fixture.CleanupCount,
                ["revision"] = fixture.ExactRevision, ["checks"] = new JsonArray(fixture.Checks.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["feedbackExact"] = success, ["recoveryAdditionalEffects"] = 0 }.ToJsonString(), Ct);
        }
    }

    private sealed class ReferenceModel : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);

        public int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Assert.Equal(1, ++Calls);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({number:m.number(m.text(source.changeUrl,'[?]id=([0-9]+)',1))})" } });
        }
    }
}
