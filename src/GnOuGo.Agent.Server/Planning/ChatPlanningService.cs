using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Routes durable planner sessions to their originating conversation; never replays the enclosing workflow.</summary>
public sealed class ChatPlanningService(IKeyVaultRecordStore records, SecureWorkflowRuntimeFactory runtimeFactory,
    PlanningSessionService designer, IOptions<OpenTelemetrySettings> telemetry, IHostApplicationLifetime lifetime, ILogger<ChatPlanningService> logger)
{
    private readonly IKeyVaultRecordStore _records = records;
    private const string Origins = "agent-chat-planning-origins-v1";
    private const string Sessions = "flow-planning-sessions-v8";
    private const string Author = "GnOuGo.Agent.Server.Planning";
    private string Tenant => WorkflowExecutionTenant.Resolve(telemetry);
    private readonly ConcurrentDictionary<string, Waiter> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<PlanningSession>> _recoveries = new(StringComparer.Ordinal);
    private WorkflowPlanningRuntimeFactory Factory() => new(_records, GnOuGoWorkspace.ResolveDatabasePath(null, AppContext.BaseDirectory, ".GnOuGo/data/flow-planning-v8/leases"));

    public Bridge Attach(string conversationId, Action<PlanningSessionDto> changed) => new(this, conversationId, changed);

    public async Task<IReadOnlyList<ChatSessionDto>> ConversationsAsync(CancellationToken ct)
    {
        // A workflow waiting for a planner decision has no completed chat turn yet.
        // Discover its conversation from the encrypted origin journal, including on a fresh browser.
        var conversations = new Dictionary<string, ChatSessionDto>(StringComparer.Ordinal);
        foreach (var record in await _records.ListAsync(Origins, Tenant, Author, ct))
        {
            var origin = JsonSerializer.Deserialize(record.Value, ChatPlanningJsonContext.Default.ChatPlanningOrigin)!;
            if (origin.Request.TenantId != Tenant || origin.Request.SessionId != record.Key) continue;
            var time = record.UpdatedAt.ToUnixTimeMilliseconds();
            if (conversations.TryGetValue(origin.ConversationId, out var current) && current.UpdatedAtUnixMs >= time) continue;
            var title = origin.Request.Prompt.Trim();
            if (title.Length > 80) title = title[..80] + "…";
            conversations[origin.ConversationId] = new(origin.ConversationId, title, time,
                [new("user", origin.Request.Prompt, MessageId: "planning-" + record.Key)], ConversationId: origin.ConversationId);
        }
        return conversations.Values.OrderByDescending(c => c.UpdatedAtUnixMs).ToArray();
    }

    public async Task RegisterDesignerAsync(string conversationId, PlanningSession session, CancellationToken ct)
    {
        if (session.Request.TenantId != Tenant) throw new PlanningConflictException("Planning tenant mismatch.");
        var origin = new ChatPlanningOrigin(conversationId, false, "", "", 0, [], session.Request);
        await _records.UpsertAsync(Origins, Tenant, session.Request.SessionId, JsonSerializer.Serialize(origin, ChatPlanningJsonContext.Default.ChatPlanningOrigin), Author, ct);
    }

    private async Task<ChatPlanningOrigin> OriginAsync(string conversation, string id, CancellationToken ct)
    {
        var record = await _records.GetAsync(Origins, Tenant, id, Author, ct) ?? throw new KeyNotFoundException("Chat planning session not found.");
        var origin = JsonSerializer.Deserialize(record.Value, ChatPlanningJsonContext.Default.ChatPlanningOrigin)!;
        if (origin.ConversationId != conversation || origin.Request.TenantId != Tenant || origin.Request.SessionId != id)
            throw new KeyNotFoundException("Chat planning session not found.");
        return origin;
    }

    public async Task<IReadOnlyList<PlanningSessionDto>> ListAsync(string conversationId, CancellationToken ct)
    {
        var result = new List<PlanningSessionDto>();
        foreach (var record in await _records.ListAsync(Origins, Tenant, Author, ct))
        {
            var origin = JsonSerializer.Deserialize(record.Value, ChatPlanningJsonContext.Default.ChatPlanningOrigin)!;
            if (origin.ConversationId != conversationId || origin.Request.TenantId != Tenant || origin.Request.SessionId != record.Key) continue;
            var state = origin.Workflow ? await LoadAsync(record.Key, ct) : await designer.GetAsync(record.Key, ct);
            if (state is null) continue;
            result.Add(PlanningEndpoints.ToDto(state) with { WorkflowSession = origin.Workflow });
            if (origin.Workflow && !_owners.ContainsKey(record.Key) && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status))
                _ = StartRecovery(origin, new() { ExpectedRevision = state.Revision });
        }
        return result;
    }

    private async Task<PlanningSession?> LoadAsync(string id, CancellationToken ct)
    {
        var record = await _records.GetAsync(Sessions, Tenant, id, Author, ct);
        return record is null ? null : PlanningSessionHistory.Read(record.Value, Tenant, id, true, record.UpdatedAt).Session;
    }

    public async Task<PlanningSessionDto> SubmitAsync(string conversationId, string id, PlanningCommand command, CancellationToken ct)
    {
        // Planner-only transport cannot approve or execute a workflow.
        if (command.Kind is not ("answer_decision" or "configure_mode" or "cancel")) throw new ArgumentException("Unsupported chat planning command.");
        var origin = await OriginAsync(conversationId, id, ct);
        if (!origin.Workflow) return PlanningEndpoints.ToDto(await designer.SubmitAsync(id, command, ct));
        var gate = _gates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_waiting.TryGetValue(id, out var waiter))
            {
                // Validate without mutation before handing the command to the live workflow owner.
                await new TypedWorkflowPlanner().AdvanceAsync(waiter.Session, command, new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask), ct);
                if (!waiter.Command.TrySetResult(command)) throw new PlanningConflictException("An answer is already being saved.");
                return PlanningEndpoints.ToDto(await waiter.Saved.Task.WaitAsync(ct));
            }
            if (_recoveries.TryGetValue(id, out var recovery) && !recovery.IsCompleted) throw new PlanningConflictException("Planning is already resuming; reload its current revision.");
            if (_owners.ContainsKey(id)) throw new PlanningConflictException("Planning is advancing; reload before changing it.");
            return PlanningEndpoints.ToDto(await StartRecovery(origin, command).WaitAsync(ct));
        }
        finally { gate.Release(); }
    }

    private Task<PlanningSession> StartRecovery(ChatPlanningOrigin origin, PlanningCommand command)
    {
        var id = origin.Request.SessionId;
        lock (_recoveries)
        {
            if (_recoveries.TryGetValue(id, out var running) && !running.IsCompleted) return running;
            return _recoveries[id] = RecoverAsync(origin, command, lifetime.ApplicationStopping);
        }
    }

    private async Task<PlanningSession> RecoverAsync(ChatPlanningOrigin origin, PlanningCommand command, CancellationToken ct)
    {
        try
        {
            await using var runtime = await runtimeFactory.CreateAsync(ct);
            var engine = new WorkflowEngine { LLMClient = runtime.LlmClient, LLMCapabilities = runtime.LlmCapabilityResolver,
                McpClientFactory = runtime.McpClientFactory, ModelUsageCostEstimator = new ModelMetadataUsageCostEstimator(runtime.Options), PlanningPolicy = AgentPlanningPolicy.Create() };
            var context = new StepExecutionContext { Engine = engine, Data = new(), Step = new() { Source = new StepDef { Id = origin.StepId, Type = "workflow.plan" } },
                Limits = new() { TenantId = Tenant, RunId = origin.RunId }, CallDepth = origin.CallDepth, CallStack = new(origin.CallStack, StringComparer.Ordinal) };
            await using var owned = await Factory().OpenAsync(context, new() { Request = JsonSerializer.Deserialize(JsonSerializer.Serialize(origin.Request, PlanningJsonContext.Default.PlanningRequest), PlanningJsonContext.Default.PlanningRequest)! }, ct);
            if (owned.Session.Request.SessionId != origin.Request.SessionId) throw new PlanningConflictException("Planning owner identity changed.");
            var planner = new TypedWorkflowPlanner();
            var state = await planner.AdvanceAsync(owned.Session, command, owned.Runtime, ct);
            while (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status))
                state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, owned.Runtime, ct);
            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Chat planner recovery stopped for {SessionId}: {ErrorType}", origin.Request.SessionId, ex.GetType().Name);
            throw;
        }
    }

    private sealed class Waiter(PlanningSession session)
    {
        internal PlanningSession Session { get; } = session;
        internal TaskCompletionSource<PlanningCommand> Command { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<PlanningSession> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Bridge(ChatPlanningService owner, string conversation, Action<PlanningSessionDto> changed) : IPlanningDecisionProvider, IPlanningRuntimeFactory
    {
        public async Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSession initial, CancellationToken ct)
        {
            if (initial.Request.TenantId != owner.Tenant) throw new PlanningConflictException("Planning tenant mismatch.");
            var owned = await owner.Factory().OpenAsync(context, initial, ct);
            try
            {
                var origin = new ChatPlanningOrigin(conversation, true, context.Limits.RunId!, context.Step.Id, context.CallDepth, [.. context.CallStack], initial.Request);
                var existing = await owner._records.GetAsync(Origins, owner.Tenant, initial.Request.SessionId, Author, ct);
                if (existing is null) await owner._records.UpsertAsync(Origins, owner.Tenant, initial.Request.SessionId, JsonSerializer.Serialize(origin, ChatPlanningJsonContext.Default.ChatPlanningOrigin), Author, ct);
                else await owner.OriginAsync(conversation, initial.Request.SessionId, ct);
                owner._owners[initial.Request.SessionId] = 0;
                changed(PlanningEndpoints.ToDto(owned.Session));
                return new LinkedSession(owned, () =>
                {
                    owner._owners.TryRemove(initial.Request.SessionId, out _);
                    if (owner._waiting.TryRemove(initial.Request.SessionId, out var waiter)) waiter.Saved.TrySetException(new PlanningConflictException("The workflow owner disconnected. Reload the saved planning session."));
                });
            }
            catch { await owned.DisposeAsync(); throw; }
        }
        public Task<string> ReadApprovedYamlAsync(StepExecutionContext context, string sessionId, string artifactHash, CancellationToken ct)
            => owner.Factory().ReadApprovedYamlAsync(context, sessionId, artifactHash, ct);
        public async Task<PlanningCommand> RequestAsync(PlanningSession session, CancellationToken ct)
        {
            var waiter = new Waiter(session);
            if (!owner._waiting.TryAdd(session.Request.SessionId, waiter)) throw new PlanningConflictException("This planner already has a waiting owner.");
            changed(PlanningEndpoints.ToDto(session));
            try { return await waiter.Command.Task.WaitAsync(ct); }
            catch { owner._waiting.TryRemove(session.Request.SessionId, out _); waiter.Saved.TrySetCanceled(ct); throw; }
        }
        public Task CheckpointedAsync(PlanningSession session, CancellationToken ct)
        {
            if (owner._waiting.TryGetValue(session.Request.SessionId, out var waiter) && waiter.Command.Task.IsCompletedSuccessfully && session.Revision > waiter.Command.Task.Result.ExpectedRevision)
            { owner._waiting.TryRemove(session.Request.SessionId, out _); waiter.Saved.TrySetResult(session); }
            changed(PlanningEndpoints.ToDto(session));
            return Task.CompletedTask;
        }
    }
    private sealed record LinkedSession(IPlanningRuntimeSession Inner, Action Closed) : IPlanningRuntimeSession
    {
        public PlanningSession Session => Inner.Session;
        public IPlanningRuntime Runtime => Inner.Runtime;
        public async ValueTask DisposeAsync() { try { await Inner.DisposeAsync(); } finally { Closed(); } }
    }
}

internal sealed record ChatPlanningOrigin(string ConversationId, bool Workflow, string RunId, string StepId, int CallDepth, List<string> CallStack, PlanningRequest Request);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ChatPlanningOrigin))]
internal partial class ChatPlanningJsonContext : JsonSerializerContext;
