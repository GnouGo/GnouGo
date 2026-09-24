using System.Text.Json;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;

namespace GnOuGo.Flow.Integrations.Planning;

/// <summary>Encrypted sessions for workflow.plan in runtime hosts.</summary>
public sealed class WorkflowPlanningRuntimeFactory(IKeyVaultRecordStore records, string leaseDirectory) : IPlanningRuntimeFactory
{
    internal const string Author = "GnOuGo.Flow.Planning";
    internal const string Sessions = "flow-planning-sessions-v9";
    private const string Definitions = "flow-planning-definitions-v9";

    public static WorkflowPlanningRuntimeFactory CreateWorkspace(string? keyVaultPath = null, string? leasePath = null, string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var leases = GnOuGoWorkspace.ResolveDatabasePath(leasePath, root, ".GnOuGo/data/flow-planning-v9/leases");
        return new(KeyVaultRecordStoreFactory.CreateWorkspaceStore(keyVaultPath, root), leases);
    }

    public async Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSession initial, CancellationToken ct)
    {
        if (initial.Request.TenantId != (context.Limits.TenantId ?? "default"))
            throw new PlanningConflictException("The planning tenant must match the execution owner.");
        context.Limits.RunId ??= Guid.NewGuid().ToString("N");
        initial.Request.SessionId = PlanningGraphCompiler.Fingerprint(string.Join("\n", context.Limits.RunId,
            context.StageInvocationId ?? context.InvocationId));
        var tenant = initial.Request.TenantId;
        var key = initial.Request.SessionId;
        Directory.CreateDirectory(leaseDirectory);
        FileStream lease;
        try
        {
            lease = new FileStream(Path.Combine(leaseDirectory, PlanningGraphCompiler.Fingerprint(tenant + "\n" + key) + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new PlanningConflictException("This planning session is active in another runtime. Resume with the same run ID after it releases the session.");
        }
        try
        {
            var definition = JsonSerializer.Serialize(initial.Request, PlanningJsonContext.Default.PlanningRequest);
            var savedDefinition = await records.GetAsync(Definitions, tenant, key, Author, ct);
            if (savedDefinition is not null && JsonSerializer.Serialize(JsonSerializer.Deserialize(savedDefinition.Value, PlanningJsonContext.Default.PlanningRequest), PlanningJsonContext.Default.PlanningRequest) != definition)
                throw new PlanningConflictException("The planning request changed for this run ID. Resume the original request or start a new run.");
            var saved = await records.GetAsync(Sessions, tenant, key, Author, ct);
            if (saved is null && await records.GetAsync("flow-planning-sessions-v8", tenant, key, Author, ct) is not null)
                throw new PlanningConflictException(PlanningSessionStorage.IncompatibleMessage);
            var state = saved is null ? initial : PlanningSessionStorage.Read(saved.Value, tenant, key);
            if (state.SchemaVersion != 9 || state.Request.TenantId != tenant || state.Request.SessionId != key || saved is not null && savedDefinition is null)
                throw new PlanningConflictException("The planning session ownership or schema is invalid.");
            if (savedDefinition is null) await records.UpsertAsync(Definitions, tenant, key, definition, Author, ct);
            // A crash after the receipt but before the coordinator checkpoint must not
            // replenish active time. Record timestamps bound the completed call interval.
            if (state.PendingCall is { } pending && await records.GetAsync(WorkflowPlanningModelJournal.Receipts, tenant, pending.Id, Author, ct) is { } completion && completion.UpdatedAt > state.UpdatedAtUtc)
            {
                state.ActiveMilliseconds += (completion.UpdatedAt - state.UpdatedAtUtc).TotalMilliseconds;
                state.UpdatedAtUtc = completion.UpdatedAt;
            }
            var receipt = await records.GetAsync(WorkflowPlanningBudgetSink.Collection, tenant, key, Author, ct);
            var usage = receipt is null ? state.Usage : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)
                ?? throw new PlanningConflictException("The encrypted planning budget is invalid.");
            var limits = (PlanningBudgetOptions.Parse(state.Request.Options) ?? new LLMUsageBudgetLimits { MaxCalls = 8 }) with { MaxElapsed = null };
            if (limits.MaxCalls is null && limits.MaxTotalTokens is null && limits.MaxEstimatedCost is null) limits = limits with { MaxCalls = 8 };
            var inheritedBudget = context.LLMUsageBudget;
            var budget = new LLMUsageBudgetScope(limits, usage, inheritedBudget, new WorkflowPlanningBudgetSink(records, tenant, key),
                exchangeRateProvider: context.Engine.ExchangeRateProvider);
            // The journal meters actual dispatches; replaying a receipt consumes no new allowance.
            context.LLMUsageBudget = null;
            var client = new WorkflowPlanningModelJournal(context, records, state.Request, budget);
            async Task Checkpoint(PlanningSession snapshot, CancellationToken token)
            {
                snapshot.Usage = budget.Snapshot;
                await records.UpsertAsync(Sessions, tenant, key, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSession), Author, token);
            }
            await Checkpoint(state, ct);
            context.SetTelemetryAttribute("gnougo-flow.plan.session_id", key);
            context.SetTelemetryAttribute("gnougo-flow.plan.run_id", context.Limits.RunId);
            return new OwnedSession(state, new WorkflowPlanningRuntime(context, client, Checkpoint), lease, () => context.LLMUsageBudget = inheritedBudget);
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async Task<string> ReadApprovedYamlAsync(StepExecutionContext context, string sessionId, string artifactHash, CancellationToken ct)
    {
        var tenant = context.Limits.TenantId ?? "default";
        var stored = await records.GetAsync(Sessions, tenant, sessionId, Author, ct)
            ?? throw new PlanningConflictException("The approved schema-9 planning session is unavailable for this tenant. Regenerate and approve the workflow.");
        var state = PlanningSessionStorage.Read(stored.Value, tenant, sessionId);
        if (state.SchemaVersion != 9 || state.Request.TenantId != tenant || state.Request.SessionId != sessionId ||
            state.Status != PlanningStatus.Approved || state.ApprovedHash != artifactHash || PlanningArtifactApproval.Hash(state) != artifactHash)
            throw new PlanningConflictException("The artifact does not have a current, tenant-owned approval.");
        PlanningArtifactApproval.Verify(state);
        var runtime = new WorkflowPlanningRuntime(context.Engine, (_, _) => Task.CompletedTask);
        if ((await runtime.ValidateCatalogAsync(state.Catalog!, ct)).Any(d => d.Required))
            throw new PlanningConflictException("Capability contracts changed after approval.");
        return state.Yaml!;
    }

    private sealed record OwnedSession(PlanningSession Session, IPlanningRuntime Runtime, FileStream Lease, Action Restore) : IPlanningRuntimeSession
    {
        public async ValueTask DisposeAsync() { Restore(); await Lease.DisposeAsync(); }
    }
}
