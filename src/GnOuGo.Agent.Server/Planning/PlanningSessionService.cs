using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Owns planning lifetime independently of a browser connection.</summary>
public sealed class PlanningSessionService(
    IPlanningSessionStore store,
    IDbContextFactory<PlanningDbContext> contexts,
    IKeyVaultRecordStore records,
    SecureWorkflowRuntimeFactory runtimeFactory,
    IWorkflowPlanner planner,
    IExchangeRateProvider exchangeRates,
    IOptions<WorkflowPlanningBudgetSettings> budgetSettings,
    IOptions<TypedWorkflowPlanningSettings> settings,
    IOptions<OpenTelemetrySettings> telemetrySettings,
    ILogger<PlanningSessionService> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _interrupts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionSlots = new(2, 2);
    private string Tenant => WorkflowExecutionTenant.Resolve(telemetrySettings);
    private static readonly ActivitySource Activities = new("GnOuGo.Agent.Planning");
    private static readonly Meter Metrics = new("GnOuGo.Agent.Planning");
    private static readonly Histogram<double> PhaseDuration = Metrics.CreateHistogram<double>("gnougo.planning.phase.duration", "s");
    private static readonly Counter<long> Outcomes = Metrics.CreateCounter<long>("gnougo.planning.outcomes");
    private static readonly Histogram<double> QueueDuration = Metrics.CreateHistogram<double>("gnougo.planning.queue.duration", "s");

    public Task<PlanningSnapshot?> GetAsync(string id, CancellationToken ct) => store.LoadAsync(Tenant, id, ct);
    public Task<IReadOnlyList<PlanningSnapshot>> ListAsync(CancellationToken ct) => store.ListAsync(Tenant, ct);

    public async Task<PlanningSnapshot> StartAsync(string name, string prompt, bool reviseExisting, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        name = name.Trim();
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')) throw new ArgumentException("The agent name is invalid.");
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var provider = runtime.Options.DefaultProvider;
        var model = runtime.Options.DefaultModel;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Configure a default model before creating a workflow.");
        await using var agent = await runtime.McpClientFactory.GetClientAsync("GnOuGo.Agent.Mcp", ct);
        var existing = await agent.CallToolAsync("agent_get_by_name", new JsonObject { ["name"] = name }, ct);
        var response = existing.Content as JsonObject;
        var found = response?["success"]?.GetValue<bool>() == true;
        if (reviseExisting && !found) throw new InvalidOperationException("The agent to revise was not found.");
        if (!reviseExisting && found) throw new InvalidOperationException("An agent with that name already exists.");
        if (!found && response?["error_code"]?.GetValue<string>() != "NOT_FOUND") throw new InvalidOperationException("Agent name availability could not be established.");
        var original = response?["agent"]?["workflow"]?.GetValue<string>();
        var options = CreateOptions(provider, model);
        if (reviseExisting)
            options["host_save"] = new JsonObject { ["agent_id"] = response!["agent"]!["id"]!.DeepClone(), ["original_hash"] = PlanningGraphCompiler.Fingerprint(original!) };
        var state = new PlanningSnapshot
        {
            Request = new PlanningRequest { TenantId = Tenant, Name = name, Prompt = prompt.Trim(), ExistingYaml = original, Options = options, MaxConcurrency = settings.Value.MaxConcurrency },
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (!await store.TrySaveAsync(state, expectedRevision: null, ct)) throw new PlanningConflictException("The planning session already exists.");
        _queue.Writer.TryWrite(state.Request.SessionId);
        return state;
    }

    public async Task<PlanningSnapshot> SubmitAsync(string id, PlanningCommand command, CancellationToken ct)
    {
        if (command.Kind == "cancel")
        {
            var observed = await store.LoadAsync(Tenant, id, ct) ?? throw new KeyNotFoundException("Planning session not found.");
            if (observed.Revision != command.ExpectedRevision) throw new PlanningConflictException("The planning session changed. Reload before cancelling.");
            if (observed.Status == PlanningStatus.Saving) throw new PlanningConflictException("The approved revision is being saved.");
            if (_interrupts.TryGetValue(id, out var interrupt)) await interrupt.CancelAsync();
        }
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await store.LoadAsync(Tenant, id, ct) ?? throw new KeyNotFoundException("Planning session not found.");
            if (current.Revision != command.ExpectedRevision) throw new PlanningConflictException("The planning session changed. Reload before submitting.");
            if (command.Kind == "revise")
            {
                if (current.Graph is null || string.IsNullOrWhiteSpace(command.Text) || current.Status is PlanningStatus.Saved or PlanningStatus.Saving or PlanningStatus.Cancelled)
                    throw new PlanningConflictException("This session cannot accept a revision request.");
                current.PendingCommand = new(current.Status, command);
                current.Status = PlanningStatus.Revising;
                current.ApprovedHash = null;
                if (current.WaitingSinceUtc is { } waiting) current.HumanWaitMilliseconds += Math.Max(0, (DateTimeOffset.UtcNow - waiting).TotalMilliseconds);
                current.WaitingSinceUtc = null;
                var revision = current.Revision++;
                current.UpdatedAtUtc = DateTimeOffset.UtcNow;
                if (!await store.TrySaveAsync(current, revision, ct)) throw new PlanningConflictException("A newer revision was saved.");
                _queue.Writer.TryWrite(id);
                return current;
            }
            if (command.Kind == "save") return await SaveAsync(current, command, ct);
            var result = await AdvanceAsync(current, command, ct);
            if (!PlanningStatus.IsTerminal(result.Status) && !PlanningStatus.IsWaiting(result.Status)) _queue.Writer.TryWrite(id);
            return result;
        }
        finally { gate.Release(); }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Durable revisions resume automatically; pending unknown model requests fail closed in the journal.
        foreach (var session in await store.ListAsync(Tenant, stoppingToken))
            if (!PlanningStatus.IsWaiting(session.Status) && !PlanningStatus.IsTerminal(session.Status)) _queue.Writer.TryWrite(session.Request.SessionId);
        var recovery = RecoverPendingAsync(stoppingToken);
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (_running.TryGetValue(id, out var running) && !running.IsCompleted) continue;
                _running[id] = RunToPauseAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await Task.WhenAll(_running.Values);
        await recovery;
    }

    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                foreach (var session in await store.ListAsync(Tenant, ct))
                    if (!PlanningStatus.IsWaiting(session.Status) && !PlanningStatus.IsTerminal(session.Status) &&
                        (!_running.TryGetValue(session.Request.SessionId, out var worker) || worker.IsCompleted))
                        _queue.Writer.TryWrite(session.Request.SessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RunToPauseAsync(string id, CancellationToken ct)
    {
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _interrupts[id] = interrupt;
        ct = interrupt.Token;
        try
        {
            var queued = Stopwatch.StartNew();
            await _sessionSlots.WaitAsync(ct);
            QueueDuration.Record(queued.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("tenant.id", Tenant));
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(ct);
                    PlanningSnapshot? state;
                    try
                    {
                        state = await store.LoadAsync(Tenant, id, ct);
                        if (state is null || PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) return;
                        if (state.PendingCommand is { } pending)
                        {
                            state.Status = pending.PreviousStatus;
                            state.PendingCommand = null;
                            pending.Command.ExpectedRevision = state.Revision;
                            await AdvanceAsync(state, pending.Command, ct);
                        }
                        else if (state.Status == PlanningStatus.Saving)
                            await SaveAsync(state, new PlanningCommand { Kind = "save", ExpectedRevision = state.Revision, ArtifactHash = state.ApprovedHash }, ct);
                        else await AdvanceAsync(state, new PlanningCommand { ExpectedRevision = state.Revision }, ct);
                    }
                    finally { gate.Release(); }
                }
            }
            finally { _sessionSlots.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (PlanningConflictException) { logger.LogInformation("Planning session {SessionId} advanced in another worker.", id); }
        catch (Exception ex)
        {
            logger.LogError("Planning session {SessionId} stopped after {ErrorType}. Its durable revision is retained.", id, ex.GetType().Name);
            var state = await store.LoadAsync(Tenant, id, CancellationToken.None);
            if (state is not null && !PlanningStatus.IsTerminal(state.Status))
            {
                var revision = state.Revision++;
                state.Status = PlanningStatus.Failed;
                state.Diagnostics = [new("PLANNING_HOST_FAILURE", "$", "The planning host could not complete this phase. The encrypted session was retained.")];
                await store.TrySaveAsync(state, revision, CancellationToken.None);
            }
        }
        finally { _interrupts.TryRemove(id, out _); }
    }

    private async Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot current, PlanningCommand command, CancellationToken ct)
    {
        using var activity = Activities.StartActivity("planning.advance");
        activity?.SetTag("tenant.id", Tenant);
        activity?.SetTag("gnougo.planning.phase", current.Status);
        activity?.SetTag("gnougo.planning.revision", current.Revision);
        var sw = Stopwatch.StartNew();
        if (command.Kind == "cancel")
        {
            var cancelled = await planner.AdvanceAsync(current, command, new WorkflowPlanningRuntime(new WorkflowEngine()), ct);
            var finalBudget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, current.Request.SessionId, EfPlanningSessionStore.Author, ct);
            if (finalBudget is not null) cancelled.Usage = JsonSerializer.Deserialize(finalBudget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            if (!await store.TrySaveAsync(cancelled, current.Revision, ct)) throw new PlanningConflictException("The session changed before cancellation was saved.");
            Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"), new KeyValuePair<string, object?>("tenant.id", Tenant));
            return cancelled;
        }
        if (current.ActiveMilliseconds >= 18_000_000 && command.Kind == "advance")
        {
            var previous = current.Revision++;
            current.Status = PlanningStatus.Failed;
            current.Diagnostics = [new(ErrorCodes.LlmBudgetExceeded, "$", "The active planning time budget has been exhausted.")];
            if (!await store.TrySaveAsync(current, previous, ct)) throw new PlanningConflictException("The planning session changed.");
            return current;
        }
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var receipt = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, current.Request.SessionId, EfPlanningSessionStore.Author, ct);
        var initial = receipt is null ? current.Usage : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var money = current.Request.Options["llm_budget"]?["max_estimated_cost"];
        var budget = new LLMUsageBudgetScope(new LLMUsageBudgetLimits
        {
            MaxCalls = 100, MaxTotalTokens = 15_000_000,
            MaxEstimatedCost = new MonetaryAmount(money?["amount"]?.GetValue<decimal>() ?? budgetSettings.Value.Amount, money?["currency"]?.GetValue<string>() ?? budgetSettings.Value.Currency)
        }, initial, sink: new PlanningBudgetSink(records, Tenant, current.Request.SessionId), exchangeRateProvider: exchangeRates);
        var estimator = new ModelMetadataUsageCostEstimator(runtime.Options);
        var journal = new PlanningModelJournal(runtime.LlmClient, contexts, records, Tenant, current.Request.SessionId, current.Revision, budget, estimator);
        var engine = new WorkflowEngine
        {
            LLMClient = journal, McpClientFactory = runtime.McpClientFactory,
            LLMCapabilities = runtime.LlmCapabilityResolver, ModelUsageCostEstimator = estimator,
            ExchangeRateProvider = exchangeRates,
            LlmDefaults = new LlmRuntimeDefaults { Provider = runtime.Options.DefaultProvider, Model = runtime.Options.DefaultModel },
            Limits = new ExecutionLimits { LogStepContent = false, TenantId = Tenant, RunId = current.Request.SessionId }
        };
        var result = await planner.AdvanceAsync(current, command, new WorkflowPlanningRuntime(engine), ct);
        if (result.Revision == current.Revision) return result;
        result.ActiveMilliseconds = current.ActiveMilliseconds + sw.Elapsed.TotalMilliseconds;
        result.Usage = budget.Snapshot;
        if (!await store.TrySaveAsync(result, current.Revision, ct)) throw new PlanningConflictException("A newer planning revision was persisted by another worker.");
        activity?.SetTag("gnougo.planning.outcome", result.Outcome);
        activity?.SetStatus(result.Status == PlanningStatus.Failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        PhaseDuration.Record(sw.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("phase", current.Status), new KeyValuePair<string, object?>("tenant.id", Tenant));
        if (PlanningStatus.IsTerminal(result.Status)) Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome), new KeyValuePair<string, object?>("tenant.id", Tenant));
        return result;
    }

    private async Task<PlanningSnapshot> SaveAsync(PlanningSnapshot state, PlanningCommand command, CancellationToken ct)
    {
        if (state.Status == PlanningStatus.Saved && command.ArtifactHash == state.ApprovedHash) return state;
        if (state.Status is not (PlanningStatus.Approved or PlanningStatus.Saving) || string.IsNullOrEmpty(state.Yaml) || state.ArtifactHash != command.ArtifactHash || state.ApprovedHash != state.ArtifactHash || state.ArtifactHash != PlanningGraphCompiler.Fingerprint(state.Yaml))
            throw new PlanningConflictException("Saving requires approval of this exact validated artifact.");
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var validation = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = runtime.McpClientFactory });
        var errors = await validation.ValidateCatalogAsync(state.Preparation!, ct);
        if (errors.Count != 0)
        {
            var approvedRevision = state.Revision++;
            state.Status = PlanningStatus.Unsupported;
            state.ApprovedHash = null;
            state.Diagnostics = errors.ToList();
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (!await store.TrySaveAsync(state, approvedRevision, ct)) throw new PlanningConflictException("The session changed while its approval was being invalidated.");
            return state;
        }
        if (state.Status == PlanningStatus.Approved)
        {
            var approvedRevision = state.Revision++;
            state.Status = PlanningStatus.Saving;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (!await store.TrySaveAsync(state, approvedRevision, ct)) throw new PlanningConflictException("The approved session changed before saving.");
        }
        await using var agent = await runtime.McpClientFactory.GetClientAsync("GnOuGo.Agent.Mcp", ct);
        var existingCall = await agent.CallToolAsync("agent_get_by_name", new JsonObject { ["name"] = state.Request.Name }, ct);
        var existing = existingCall.Content as JsonObject;
        var found = existing?["success"]?.GetValue<bool>() == true;
        var existingYaml = existing?["agent"]?["workflow"]?.GetValue<string>();
        var host = state.Request.Options["host_save"];
        var id = existing?["agent"]?["id"]?.GetValue<string>();
        if (found && existingYaml == state.Yaml)
        {
            // Reconcile a crash after the writer committed but before the session index advanced.
            state.SavedAgentId = id;
        }
        else
        {
            if (host is not null && (!found || id != host["agent_id"]?.GetValue<string>() || PlanningGraphCompiler.Fingerprint(existingYaml ?? "") != host["original_hash"]?.GetValue<string>()))
                throw new PlanningConflictException("The existing agent changed while this revision was being planned.");
            if (host is null && found) throw new PlanningConflictException("Another agent now uses this name.");
            if (!found && existing?["error_code"]?.GetValue<string>() != "NOT_FOUND") throw new InvalidOperationException("Agent availability could not be established.");
            var request = new JsonObject { ["name"] = state.Request.Name, ["workflow"] = state.Yaml, ["originalPrompt"] = state.Request.Prompt };
            if (host is not null) request["id"] = id;
            var saved = await agent.CallToolAsync(host is null ? "agent_add" : "agent_update", request, ct);
            if (saved.IsError || saved.Content?["success"]?.GetValue<bool>() != true) throw new InvalidOperationException("The validated agent could not be saved.");
            state.SavedAgentId = saved.Content?["agent"]?["id"]?.GetValue<string>();
        }
        var previousRevision = state.Revision;
        state.Revision++;
        state.Status = PlanningStatus.Saved;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!await store.TrySaveAsync(state, previousRevision, ct)) throw new PlanningConflictException("A newer session revision was saved.");
        Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "saved"), new KeyValuePair<string, object?>("tenant.id", Tenant));
        return state;
    }

    private JsonObject CreateOptions(string provider, string model) => new()
    {
        ["planner_version"] = 2,
        ["generator"] = new JsonObject { ["provider"] = provider, ["model"] = model, ["reasoning"] = "medium", ["context"] = "Generate a self-contained chat-agent workflow. Host configuration, credentials and saving the agent are outside its runtime boundary. Preserve every required operation, runtime outcome, and resource cleanup. The .GnOuGo directory is reserved for internal state. Workflow-created files belong under workflows/<purpose-specific-name>; propagate declared materialization outputs to subsequent steps. Unless explicitly requested otherwise, obtain runtime human confirmation before the first external write, with zero writes after rejection. Do not request review of the workflow's own YAML during execution." },
        ["capability_preflight"] = new JsonObject { ["mode"] = "infer" },
        ["intent_clarification"] = new JsonObject { ["max_rounds"] = 3, ["max_questions"] = 15, ["max_questions_per_round"] = 5 },
        ["policy"] = new JsonObject
        {
            ["allowed_step_types"] = new JsonArray("mcp.list", "mcp.call", "llm.call", "set", "emit", "assert.non_null", "template.render", "sequence", "parallel", "loop.sequential", "loop.parallel", "switch", "decision.evaluate", "human.input", "workflow.call"),
            ["denied_step_types"] = new JsonArray("workflow.plan", "workflow.execute"), ["allow_remote_workflow_refs"] = false
        },
        ["limits"] = new JsonObject { ["max_steps_total"] = 300 },
        ["llm_budget"] = new JsonObject { ["max_estimated_cost"] = new JsonObject { ["amount"] = budgetSettings.Value.Amount, ["currency"] = budgetSettings.Value.Currency } }
    };
}
