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

    public Task<PlanningSession?> GetAsync(string id, CancellationToken ct) => store.LoadAsync(Tenant, id, ct);
    public Task<IReadOnlyList<PlanningSession>> ListAsync(CancellationToken ct) => store.ListAsync(Tenant, ct);

    public async Task<PlanningSession> StartAsync(string name, string prompt, bool reviseExisting, CancellationToken ct, JsonObject? failureEvidence = null)
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
        var state = new PlanningSession
        {
            Request = new PlanningRequest
            {
                TenantId = Tenant,
                Name = name,
                Prompt = prompt.Trim(),
                Baseline = original is null ? null : PlanningGraphImporter.ImportBaseline(original),
                FailureEvidence = failureEvidence?.DeepClone().AsObject(),
                Options = options,
                Policy = AgentPlanningPolicy.Create(),
                MaxModelCalls = settings.Value.MaxModelCalls,
                MaxRepairAttempts = settings.Value.MaxRepairAttempts,
                Generation = new() { Reasoning = settings.Value.Reasoning, MaxInputTokensPerRequest = settings.Value.MaxInputTokensPerRequest, MaxOutputTokens = settings.Value.MaxOutputTokens }
            },
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        PlanningGenerationPolicy.Validate(state.Request.Generation);
        if (!await store.TrySaveAsync(state, expectedRevision: null, ct)) throw new PlanningConflictException("The planning session already exists.");
        _queue.Writer.TryWrite(state.Request.SessionId);
        return state;
    }

    public async Task<PlanningSession> SubmitAsync(string id, PlanningCommand command, CancellationToken ct)
    {
        if (command.Kind == "cancel" && _interrupts.TryGetValue(id, out var interrupt)) await interrupt.CancelAsync();
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await store.LoadAsync(Tenant, id, ct) ?? throw new KeyNotFoundException("Planning session not found.");
            if (current.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed; reload its current revision.");
            var result = command.Kind == "save" ? await SaveAsync(current, command, ct) : await AdvanceAsync(current, command, ct);
            if (!PlanningStatus.IsWaiting(result.Status) && !PlanningStatus.IsTerminal(result.Status)) _queue.Writer.TryWrite(id);
            return result;
        }
        finally { gate.Release(); }
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.BackgroundProcessingEnabled) return;
        foreach (var state in await store.ListAsync(Tenant, stoppingToken))
            if (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status)) _queue.Writer.TryWrite(state.Request.SessionId);
        var recovery = RecoverAsync(stoppingToken);
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (_running.TryGetValue(id, out var running) && !running.IsCompleted) continue;
                _running[id] = RunAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await Task.WhenAll(_running.Values); await recovery;
    }
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                foreach (var state in await store.ListAsync(Tenant, ct))
                    if (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status)) _queue.Writer.TryWrite(state.Request.SessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task RunAsync(string id, CancellationToken stoppingToken)
    {
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _interrupts[id] = interrupt;
        var ct = interrupt.Token;
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
                    try
                    {
                        var state = await store.LoadAsync(Tenant, id, ct);
                        if (state is null || PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) return;
                        if (state.Status == PlanningStatus.Saving)
                            await SaveAsync(state, new() { Kind = "save", ExpectedRevision = state.Revision, ArtifactHash = state.ApprovedHash }, ct);
                        else await AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, ct);
                    }
                    finally { gate.Release(); }
                }
            }
            finally { _sessionSlots.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (PlanningConflictException) { logger.LogInformation("Planning session {SessionId} changed in another worker.", id); }
        catch (Exception ex)
        {
            logger.LogError("Planning session {SessionId} stopped after {ErrorType}.", id, ex.GetType().Name);
            var state = await store.LoadAsync(Tenant, id, CancellationToken.None);
            if (state is not null && !PlanningStatus.IsTerminal(state.Status))
            {
                var revision = state.Revision++;
                state.Status = PlanningStatus.Failed; state.ApprovedHash = null;
                state.Diagnostics = [new("PLANNING_HOST_FAILURE", "$", "The host could not complete planning. The encrypted session was retained.")];
                await store.TrySaveAsync(state, revision, CancellationToken.None);
            }
        }
        finally { _interrupts.TryRemove(id, out _); }
    }

    private async Task<PlanningSession> AdvanceAsync(PlanningSession current, PlanningCommand command, CancellationToken ct)
    {
        using var activity = Activities.StartActivity("planning.advance");
        activity?.SetTag("tenant.id", Tenant); activity?.SetTag("gnougo.planning.session_id", current.Request.SessionId);
        var clock = Stopwatch.StartNew();
        if (current.PendingCall is { } pending && await records.GetAsync(PlanningModelJournal.Collection, Tenant, current.Request.SessionId + ":" + pending.Id, EfPlanningSessionStore.Author, ct) is { } completion && completion.UpdatedAt > current.UpdatedAtUtc)
        {
            current.ActiveMilliseconds += (completion.UpdatedAt - current.UpdatedAtUtc).TotalMilliseconds;
            current.UpdatedAtUtc = completion.UpdatedAt;
        }

        if (command.Kind is "cancel" or "edit_intent" or "revise" or "configure_generation" or "answer")
        {
            var recordedUsage = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, current.Request.SessionId, EfPlanningSessionStore.Author, ct);
            if (recordedUsage is not null) current.Usage = JsonSerializer.Deserialize(recordedUsage.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            var result = await planner.AdvanceAsync(current, command, new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask), ct);
            if (!await store.TrySaveAsync(result, current.Revision, ct)) throw new PlanningConflictException("A newer revision was saved.");
            return result;
        }
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var receipt = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, current.Request.SessionId, EfPlanningSessionStore.Author, ct);
        var initial = receipt is null ? current.Usage : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var configured = PlanningBudgetOptions.Parse(current.Request.Options);
        var budget = new LLMUsageBudgetScope(new LLMUsageBudgetLimits
        {
            MaxCalls = Math.Min(configured?.MaxCalls ?? settings.Value.MaxModelCalls, settings.Value.MaxModelCalls),
            MaxTotalTokens = Math.Min(configured?.MaxTotalTokens ?? settings.Value.MaxTotalTokens, settings.Value.MaxTotalTokens),
            MaxEstimatedCost = configured?.MaxEstimatedCost ?? new MonetaryAmount(budgetSettings.Value.Amount, budgetSettings.Value.Currency)
        }, initial, sink: new PlanningBudgetSink(records, Tenant, current.Request.SessionId), exchangeRateProvider: exchangeRates);
        var estimator = new ModelMetadataUsageCostEstimator(runtime.Options);
        var journal = new PlanningModelJournal(runtime.LlmClient, contexts, records, Tenant, current.Request.SessionId, budget, estimator, current.Request.Generation);
        var engine = new WorkflowEngine
        {
            LLMClient = journal, McpClientFactory = runtime.McpClientFactory, LLMCapabilities = runtime.LlmCapabilityResolver,
            ModelUsageCostEstimator = estimator, ExchangeRateProvider = exchangeRates,
            LlmDefaults = new() { Provider = runtime.Options.DefaultProvider, Model = runtime.Options.DefaultModel },
            Limits = new() { LogStepContent = false, TenantId = Tenant, RunId = current.Request.SessionId }
        };
        var revision = current.Revision;
        var adapter = new WorkflowPlanningRuntime(engine, async (state, token) =>
        {
            if (state.Revision <= revision) state.Revision = revision + 1;
            state.Usage = budget.Snapshot;
            state.ActiveMilliseconds = current.ActiveMilliseconds + clock.Elapsed.TotalMilliseconds;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (!await store.TrySaveAsync(state, revision, token)) throw new PlanningConflictException("A newer planning revision was saved.");
            revision = state.Revision;
        });
        var updated = await planner.AdvanceAsync(current, command, adapter, ct);
        activity?.SetTag("gnougo.planning.status", updated.Status);
        activity?.SetTag("gnougo.planning.calls", updated.ModelCalls);
        activity?.SetTag("gnougo.planning.repairs", updated.RepairAttempts);
        activity?.SetTag("gnougo.planning.diagnostics", string.Join(",", updated.Diagnostics.Select(d => d.Code).Distinct()));
        PhaseDuration.Record(clock.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("tenant.id", Tenant));
        return updated;
    }
    private async Task<PlanningSession> SaveAsync(PlanningSession state, PlanningCommand command, CancellationToken ct)
    {
        if (state.Status == PlanningStatus.Saved && command.ArtifactHash == state.ApprovedHash) return state;
        if (state.Status is not (PlanningStatus.Approved or PlanningStatus.Saving) || string.IsNullOrEmpty(state.Yaml) || PlanningArtifactApproval.Hash(state) != command.ArtifactHash || state.ApprovedHash != PlanningArtifactApproval.Hash(state))
            throw new PlanningConflictException("Saving requires approval of this exact validated artifact.");
        PlanningArtifactApproval.Verify(state);
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var validation = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = runtime.McpClientFactory }, (_, _) => Task.CompletedTask);
        var errors = await validation.ValidateCatalogAsync(state.Catalog!, ct);
        if (errors.Count == 0) errors = await validation.ValidateAsync(new(state.Yaml!, state.Request, state.Catalog!, PlanningGraphCompiler.CapabilityBindings(state.Graph!)), ct);
        if (errors.Count != 0)
        {
            var approvedRevision = state.Revision++;
            state.Status = PlanningStatus.Stopped;
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
        ["generator"] = new JsonObject { ["provider"] = provider, ["model"] = model },
        ["llm_budget"] = new JsonObject { ["max_calls"] = settings.Value.MaxModelCalls, ["max_total_tokens"] = settings.Value.MaxTotalTokens,
            ["max_elapsed_ms"] = settings.Value.MaxActiveMilliseconds,
            ["max_estimated_cost"] = new JsonObject { ["amount"] = budgetSettings.Value.Amount, ["currency"] = budgetSettings.Value.Currency } }
    };
}
