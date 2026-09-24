using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ChatPlanningClarificationTests
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
        using var designer = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var service = Service(fixture, designer, model); var bridge = service.Attach("chat", _ => { });
        await using var owned = await bridge.OpenAsync(Context(model), Initial(), Ct);
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(owned.Session, new(), owned.Runtime, Ct);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        var ownerCommand = bridge.RequestAsync(state, Ct);
        var dto = Assert.Single(await service.ListAsync("chat", Ct));
        Assert.Single(dto.Questions); Assert.Empty(await service.ListAsync("other", Ct));
        var command = new PlanningCommand { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["tone"] = "brief" } };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SubmitAsync("other", state.Request.SessionId, command, Ct));
        var submitted = service.SubmitAsync("chat", state.Request.SessionId, command, Ct);
        var delivered = await ownerCommand;
        Assert.False(submitted.IsCompleted);
        state = await planner.AdvanceAsync(state, delivered, owned.Runtime, Ct);
        await bridge.CheckpointedAsync(state, Ct);
        Assert.Single((await submitted).Clarifications); Assert.Equal(1, model.Calls);
        var stored = await fixture.Records.GetAsync("flow-planning-sessions-v9", "planning-tests", state.Request.SessionId, "test", Ct);
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
        using var designer = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var first = Service(fixture, designer, model); string id; long revision;
        await using (var owned = await first.Attach("chat", _ => { }).OpenAsync(Context(model), Initial(), Ct))
        {
            var state = await new HybridWorkflowPlanner().AdvanceAsync(owned.Session, new(), owned.Runtime, Ct);
            id = state.Request.SessionId; revision = state.Revision;
        }
        var reopened = Service(fixture, designer, model);
        var conversation = Assert.Single(await reopened.ConversationsAsync(Ct));
        Assert.Equal("chat", conversation.Id); Assert.Equal("Return a greeting", Assert.Single(conversation.Messages).Content);
        var pending = Assert.Single(await reopened.ListAsync("chat", Ct)); Assert.Single(pending.Questions);
        var cancelled = await reopened.SubmitAsync("chat", id, new() { Kind = answer ? "answer" : "cancel", ExpectedRevision = revision,
            Answers = answer ? new() { ["tone"] = "brief" } : null }, Ct);
        Assert.Equal(answer ? PlanningStatus.FinalReview : PlanningStatus.Cancelled, cancelled.Status); Assert.Equal(answer ? 2 : 1, model.Calls);
        Assert.Null(cancelled.ApprovedHash);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.SubmitAsync("chat", id, new() { Kind = "approve", ExpectedRevision = cancelled.Revision }, Ct));
    }
    [Fact]
    public async Task DesignerDecisionAndAnswerRoundTripThroughEncryptedEfIndex()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model, LLMCapabilities = model }, (_, _) => Task.CompletedTask);
        var planner = new HybridWorkflowPlanner();
        var pending = await planner.AdvanceAsync(Initial(), new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Clarification, pending.Status);
        Assert.True(await fixture.Store.TrySaveAsync(pending, null, Ct));
        var restored = (await fixture.Store.LoadAsync("planning-tests", pending.Request.SessionId, Ct))!;
        Assert.Equal(pending.GetQuestions()[0].Id, restored.GetQuestions()[0].Id);
        var answered = await planner.AdvanceAsync(restored, new() { Kind = "answer", ExpectedRevision = restored.Revision, Answers = new() { ["tone"] = "brief" } }, runtime, Ct);
        Assert.True(await fixture.Store.TrySaveAsync(answered, restored.Revision, Ct));
        Assert.False(await fixture.Store.TrySaveAsync(answered, restored.Revision, Ct));
        var resumed = (await fixture.Store.LoadAsync("planning-tests", pending.Request.SessionId, Ct))!;
        resumed = await planner.AdvanceAsync(resumed, new() { ExpectedRevision = resumed.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, resumed.Status); Assert.Equal(2, model.Calls); Assert.Single(resumed.Answers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedBusinessClarificationRecoversInOriginatingChat(bool accept)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model { ScopeRevision = true };
        using var designer = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var first = Service(fixture, designer, model); string id; long revision;
        var initial = Initial();
        initial.Diagnostics = [new("NONE_OF_THE_ABOVE", "/actions/greet", "Requested presentation is unavailable")];
        await using (var owned = await first.Attach("scope-chat", _ => { }).OpenAsync(Context(model), initial, Ct))
        {
            var state = await new HybridWorkflowPlanner().AdvanceAsync(owned.Session, new(), owned.Runtime, Ct);
            Assert.True(state.Status == PlanningStatus.Clarification, state.Status + ": " + JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)); Assert.Single(state.GetQuestions());
            id = state.Request.SessionId; revision = state.Revision;
        }
        var reopened = Service(fixture, designer, model);
        var waiting = Assert.Single(await reopened.ListAsync("scope-chat", Ct));
        Assert.Single(waiting.Questions);  Assert.Empty(await reopened.ListAsync("other", Ct));
        var command = new PlanningCommand { Kind = "answer", ExpectedRevision = revision, Answers = new() { ["accept_scope_revision"] = accept } };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reopened.SubmitAsync("other", id, command, Ct));
        // Exercise the HTTP mapping as well as owner recovery: dropping Answers here must fail this test.
        var builder = WebApplication.CreateSlimBuilder();
        // Do not inherit the host's development/CI Kestrel endpoints or configuration overlays.
        builder.Configuration.Sources.Clear(); builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0)); builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ChatJsonContext.Default));
        builder.Services.AddSingleton(reopened); builder.Services.AddSingleton(designer);
        await using var app = builder.Build(); app.MapPlanningEndpoints(); await app.StartAsync(Ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await http.PostAsJsonAsync("/api/chat/conversations/scope-chat/planning/" + id + "/commands",
            new PlanningCommandDto("answer", revision, Answers: command.Answers), ChatJsonContext.Default.PlanningCommandDto, Ct);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync(ChatJsonContext.Default.PlanningSessionDto, Ct))!;
        Assert.Equal(PlanningStatus.FinalReview, result.Status);
        Assert.Single(result.Clarifications); Assert.Null(result.ApprovedHash); Assert.Equal(2, model.Calls);
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
            var proposal = new PlanningProposal { Requirements = GnOuGo.Planning.Examples.PlanningCorpus.Requirements("local"),
                Graph = GnOuGo.Planning.Examples.PlanningCorpus.Graph("local", new()) };
            if (Calls == 1)
            {
                proposal.Graph = null;
                proposal.Requirements.Questions = [ScopeRevision
                    ? new("accept_scope_revision", "Include the detailed presentation?", new() { Type = "boolean" })
                    : new("tone", "PRIVATE_DECISION_CONTEXT: Which tone?", new() { Enum = ["brief", "full"] })];
            }
            return Task.FromResult(new LLMResponse
            {
                Json = GnOuGo.Planning.Examples.PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()),
                Usage = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 30, ["total_tokens"] = 50 }
            });
        }
    }
}
