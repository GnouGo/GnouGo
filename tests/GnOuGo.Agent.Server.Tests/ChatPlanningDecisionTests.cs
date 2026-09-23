using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ChatPlanningDecisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningSession Initial() => new() { Request = new() { TenantId = "planning-tests", Prompt = "Return a greeting", Options = new() { ["generator"] = new JsonObject { ["model"] = "fixture" } } } };
    private static StepExecutionContext Context(Model model) => new() { Engine = new() { LLMClient = model, LLMCapabilities = model }, Data = new(),
        Limits = new() { TenantId = "planning-tests", RunId = Guid.NewGuid().ToString("N") }, Step = new() { Source = new StepDef { Id = "plan", Type = "workflow.plan" } } };
    private static ChatPlanningService Service(PlanningPersistenceTests.StoreFixture fixture, PlanningSessionService designer, Model model)
    {
        var options = new LLMOptions { DefaultProvider = "fixture", DefaultModel = "fixture" };
        var factory = new SecureWorkflowRuntimeFactory(SmartFlowTestFactory.CreateRuntimeOptionsStore(options), new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options),
            llmClientOverride: model, mcpClientFactoryOverride: new InMemoryMcpClientFactory(), llmCapabilityResolver: model);
        return new(fixture.Records, factory, designer, Options.Create(new OpenTelemetrySettings { TenantId = "planning-tests" }), new Lifetime(), NullLogger<ChatPlanningService>.Instance);
    }
    [Fact]
    public async Task LiveAnswerIsAcknowledgedOnlyAfterOwnerCheckpointAndCannotCrossConversations()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model();
        using var designer = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var service = Service(fixture, designer, model); var bridge = service.Attach("chat", _ => { });
        await using var owned = await bridge.OpenAsync(Context(model), Initial(), Ct);
        var planner = new TypedWorkflowPlanner();
        var state = await planner.AdvanceAsync(owned.Session, new(), owned.Runtime, Ct);
        Assert.Equal(PlanningStatus.WaitingForDecision, state.Status);
        var ownerCommand = bridge.RequestAsync(state, Ct);
        var dto = Assert.Single(await service.ListAsync("chat", Ct));
        Assert.NotNull(dto.PendingDecision); Assert.Empty(await service.ListAsync("other", Ct));
        var command = new PlanningCommand { Kind = "answer_decision", ExpectedRevision = state.Revision, DecisionAnswer = new(state.PendingDecision!.Id, "brief") };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SubmitAsync("other", state.Request.SessionId, command, Ct));
        var submitted = service.SubmitAsync("chat", state.Request.SessionId, command, Ct);
        var delivered = await ownerCommand;
        Assert.False(submitted.IsCompleted);
        state = await planner.AdvanceAsync(state, delivered, owned.Runtime, Ct);
        await bridge.CheckpointedAsync(state, Ct);
        Assert.Single((await submitted).Decisions!); Assert.Equal(1, model.Calls);
        var stored = await fixture.Records.GetAsync("flow-planning-sessions-v8", "planning-tests", state.Request.SessionId, "test", Ct);
        Assert.Contains("PRIVATE_DECISION_CONTEXT", stored!.Value);
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_DECISION_CONTEXT", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRecoversDecisionAndCancellationWithoutReplayingWorkflow(bool answer)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model();
        using var designer = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var first = Service(fixture, designer, model); string id; long revision;
        await using (var owned = await first.Attach("chat", _ => { }).OpenAsync(Context(model), Initial(), Ct))
        {
            var state = await new TypedWorkflowPlanner().AdvanceAsync(owned.Session, new(), owned.Runtime, Ct);
            id = state.Request.SessionId; revision = state.Revision;
        }
        var reopened = Service(fixture, designer, model);
        var conversation = Assert.Single(await reopened.ConversationsAsync(Ct));
        Assert.Equal("chat", conversation.Id); Assert.Equal("Return a greeting", Assert.Single(conversation.Messages).Content);
        var pending = Assert.Single(await reopened.ListAsync("chat", Ct)).PendingDecision; Assert.NotNull(pending);
        var cancelled = await reopened.SubmitAsync("chat", id, new() { Kind = answer ? "answer_decision" : "cancel", ExpectedRevision = revision,
            DecisionAnswer = answer ? new(pending.Id, "brief") : null }, Ct);
        Assert.Equal(answer ? PlanningStatus.FinalReview : PlanningStatus.Cancelled, cancelled.Status); Assert.Equal(answer ? 2 : 1, model.Calls);
        Assert.Null(cancelled.ApprovedHash);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.SubmitAsync("chat", id, new() { Kind = "approve", ExpectedRevision = cancelled.Revision }, Ct));
    }
    [Fact]
    public async Task DesignerDecisionAndAnswerRoundTripThroughEncryptedEfIndex()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model, LLMCapabilities = model }, (_, _) => Task.CompletedTask);
        var planner = new TypedWorkflowPlanner();
        var pending = await planner.AdvanceAsync(Initial(), new(), runtime, Ct);
        Assert.Equal(PlanningStatus.WaitingForDecision, pending.Status);
        Assert.True(await fixture.Store.TrySaveAsync(pending, null, Ct));
        var restored = (await fixture.Store.LoadAsync("planning-tests", pending.Request.SessionId, Ct))!;
        Assert.Equal(pending.PendingDecision!.Id, restored.PendingDecision!.Id);
        var answered = await planner.AdvanceAsync(restored, new() { Kind = "answer_decision", ExpectedRevision = restored.Revision, DecisionAnswer = new(restored.PendingDecision.Id, "brief") }, runtime, Ct);
        Assert.True(await fixture.Store.TrySaveAsync(answered, restored.Revision, Ct));
        Assert.False(await fixture.Store.TrySaveAsync(answered, restored.Revision, Ct));
        var resumed = (await fixture.Store.LoadAsync("planning-tests", pending.Request.SessionId, Ct))!;
        resumed = await planner.AdvanceAsync(resumed, new() { ExpectedRevision = resumed.Revision }, runtime, Ct);
        Assert.Equal("brief", resumed.SemanticPlan!.Summary); Assert.Equal(1, model.Calls); Assert.Single(resumed.Decisions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedBusinessClarificationRecoversInOriginatingChat(bool accept)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model { ScopeRevision = true };
        using var designer = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var first = Service(fixture, designer, model); string id; long revision;
        var initial = Initial(); initial.SemanticPlan = new() { Actions = [new() { Id = "greet", Kind = "calculate", Purpose = "Return a greeting" }] };
        initial.Diagnostics = [new("NONE_OF_THE_ABOVE", "/actions/greet", "Requested presentation is unavailable")];
        await using (var owned = await first.Attach("scope-chat", _ => { }).OpenAsync(Context(model), initial, Ct))
        {
            var state = await new TypedWorkflowPlanner().AdvanceAsync(owned.Session, new(), owned.Runtime, Ct);
            Assert.True(state.Status == PlanningStatus.Clarification, state.Status + ": " + JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)); Assert.NotNull(state.PendingRepair);
            id = state.Request.SessionId; revision = state.Revision;
        }
        var reopened = Service(fixture, designer, model);
        var waiting = Assert.Single(await reopened.ListAsync("scope-chat", Ct));
        Assert.Single(waiting.Questions); Assert.NotNull(waiting.PendingRepair); Assert.Empty(await reopened.ListAsync("other", Ct));
        var command = new PlanningCommand { Kind = "answer", ExpectedRevision = revision, Answers = new() { ["accept_scope_revision"] = accept } };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reopened.SubmitAsync("other", id, command, Ct));
        var result = await reopened.SubmitAsync("scope-chat", id, command, Ct);
        Assert.Equal(accept ? PlanningStatus.FinalReview : PlanningStatus.Stopped, result.Status);
        Assert.Single(result.Clarifications); Assert.Null(result.ApprovedHash); Assert.Equal(accept ? 2 : 1, model.Calls);
        await Assert.ThrowsAsync<PlanningConflictException>(() => reopened.SubmitAsync("scope-chat", id, command, Ct));
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class Model : ILLMClient, ILLMCapabilityResolver
    {
        public int Calls;
        internal bool ScopeRevision;
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (ScopeRevision && request.Prompt.StartsWith("Replan this business", StringComparison.Ordinal))
            {
                var proposal = JsonSerializer.SerializeToNode(new SemanticPlan { Actions = [new() { Id = "greet", Kind = "calculate", Purpose = "Return a supported greeting" }],
                    Questions = [new("scope", "Accept the supported presentation?", new() { Type = "boolean" })] }, PlanningJsonContext.Default.SemanticPlan)!;
                proposal["questions"]![0]!["answerType"]!.AsObject().Remove("items");
                proposal["questions"]![0]!["answerType"]!.AsObject().Remove("fields");
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["result"] = new JsonObject { ["actions"] = proposal["actions"]!.DeepClone(), ["questions"] = proposal["questions"]!.DeepClone() }, ["decision"] = null }, Usage = new JsonObject { ["total_tokens"] = 50 } });
            }
            if (request.StructuredOutputSchema?["$defs"]?["decisionResult"]?["properties"]?["operations"] is not null)
                return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""
                    {"result":{"summary":"Greeting","inputs":[],"operations":[{"id":"greet","semanticAction":"greet","businessOutputs":[],"purpose":"Return a greeting","after":[],"when":null,"implementation":{"kind":"calculate","value":{"kind":"string","text":"Hello"}}}],"outputs":[{"name":"message","value":{"kind":"result","source":"greet","path":[]}}],"subflows":[],"blockedActions":[]},"decision":null}
                    """), Usage = new JsonObject { ["total_tokens"] = 50 } });
            JsonObject Option(string id, bool preferred) => new() { ["id"] = id, ["label"] = id, ["preferred"] = preferred, ["reason"] = "Audience preference",
                ["result"] = JsonSerializer.SerializeToNode(new SemanticPlan { Summary = id, Actions = [new() { Id = "greet", Kind = "calculate", Purpose = "Return a " + id + " greeting" }] }, PlanningJsonContext.Default.SemanticPlan) };
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["result"] = null, ["decision"] = new JsonObject { ["question"] = "Which tone?", ["context"] = "PRIVATE_DECISION_CONTEXT",
                ["evidence"] = "Return a greeting", ["allowCustomAnswer"] = true, ["options"] = new JsonArray(Option("brief", true), Option("full", false)) } },
                Usage = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 30, ["total_tokens"] = 50 } });
        }
    }
}
