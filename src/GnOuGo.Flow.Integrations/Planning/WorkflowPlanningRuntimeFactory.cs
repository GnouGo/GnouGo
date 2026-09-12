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
    internal const string Sessions = "flow-planning-snapshots-v5";
    private const string Definitions = "flow-planning-definitions-v5";

    public static WorkflowPlanningRuntimeFactory CreateWorkspace(string? keyVaultPath = null, string? leasePath = null, string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var leases = GnOuGoWorkspace.ResolveDatabasePath(leasePath, root, ".GnOuGo/data/flow-planning-v5/leases");
        return new(KeyVaultRecordStoreFactory.CreateWorkspaceStore(keyVaultPath, root), leases);
    }

    public async Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSnapshot initial, CancellationToken ct)
    {
        if (initial.Request.TenantId != (context.Limits.TenantId ?? "default"))
            throw new PlanningConflictException("The planning tenant must match the execution owner.");
        context.Limits.RunId ??= Guid.NewGuid().ToString("N");
        initial.Request.SessionId = PlanningGraphCompiler.Fingerprint(string.Join("\n", context.Limits.RunId,
            context.Step.Source.Id, context.CallDepth, string.Join("\n", context.CallStack.Order(StringComparer.Ordinal))));
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
            if (savedDefinition is not null && savedDefinition.Value != definition)
                throw new PlanningConflictException("The planning request changed for this run ID. Resume the original request or start a new run.");
            var saved = await records.GetAsync(Sessions, tenant, key, Author, ct);
            var state = saved is null ? initial : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.PlanningSnapshot)
                ?? throw new PlanningConflictException("The encrypted planning session is invalid.");
            if (state.SchemaVersion != 5 || state.Request.TenantId != tenant || state.Request.SessionId != key || saved is not null && savedDefinition is null)
                throw new PlanningConflictException("The planning session ownership or schema is invalid.");
            if (savedDefinition is null) await records.UpsertAsync(Definitions, tenant, key, definition, Author, ct);
            var receipt = await records.GetAsync(WorkflowPlanningBudgetSink.Collection, tenant, key, Author, ct);
            var usage = receipt is null ? state.Usage : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)
                ?? throw new PlanningConflictException("The encrypted planning budget is invalid.");
            var limits = (PlanningBudgetOptions.Parse(state.Request.Options) ?? new LLMUsageBudgetLimits { MaxCalls = 100 }) with { MaxElapsed = null };
            if (limits.MaxCalls is null && limits.MaxTotalTokens is null && limits.MaxEstimatedCost is null) limits = limits with { MaxCalls = 100 };
            var inheritedBudget = context.LLMUsageBudget;
            var budget = new LLMUsageBudgetScope(limits, usage, inheritedBudget, new WorkflowPlanningBudgetSink(records, tenant, key),
                exchangeRateProvider: context.Engine.ExchangeRateProvider);
            // The journal meters actual dispatches; replaying a receipt consumes no new allowance.
            context.LLMUsageBudget = null;
            var client = new WorkflowPlanningModelJournal(context, records, state.Request, budget);
            var observed = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            async Task Checkpoint(PlanningSnapshot snapshot, CancellationToken token)
            {
                snapshot.Usage = budget.Snapshot;
                await records.UpsertAsync(Sessions, tenant, key, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), Author, token);
                PlanningConvergenceTelemetry.Observe(observed, snapshot, (name, tags) => context.AddTelemetryEvent(name, tags));
                observed = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            }
            await Checkpoint(state, ct);
            context.SetTelemetryAttribute("gnougo-flow.plan.session_id", key);
            context.SetTelemetryAttribute("gnougo-flow.plan.run_id", context.Limits.RunId);
            return new Session(state, new WorkflowPlanningRuntime(context, client, Checkpoint), lease, () => context.LLMUsageBudget = inheritedBudget);
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private sealed record Session(PlanningSnapshot Snapshot, IPlanningRuntime Runtime, FileStream Lease, Action Restore) : IPlanningRuntimeSession
    {
        public async ValueTask DisposeAsync() { Restore(); await Lease.DisposeAsync(); }
    }
}
