using GnOuGo.Files.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;

namespace GnOuGo.Flow.Integrations.Planning;

/// <summary>Encrypted sessions for workflow.plan in runtime hosts.</summary>
public sealed partial class WorkflowPlanningRuntimeFactory(IKeyVaultRecordStore records, string leaseDirectory, IPlanningHostPolicy? hostPolicy, OwnerRetentionPolicy retention, DurableFileStore? files = null) : IPlanningRuntimeFactory
{
    public WorkflowPlanningRuntimeFactory(IKeyVaultRecordStore records, string leaseDirectory, IPlanningHostPolicy? hostPolicy = null)
        : this(records, leaseDirectory, hostPolicy, OwnerRetentionPolicy.Default) { }

    public WorkflowPlanningRuntimeFactory WithHostPolicy(IPlanningHostPolicy? policy) =>
        new(records, leaseDirectory, policy, retention, files);

    internal const string Author = "GnOuGo.Flow.Planning";
    internal const string Sessions = "flow-planning-sessions-v11";
    private const string Definitions = "flow-planning-definitions-v11";

    public static WorkflowPlanningRuntimeFactory CreateWorkspace(string? keyVaultPath = null, string? leasePath = null, string? baseDirectory = null)
    {
        return CreateWorkspaceWithRetention(OwnerRetentionPolicy.Default, keyVaultPath, leasePath, baseDirectory);
    }

    public static WorkflowPlanningRuntimeFactory CreateWorkspaceWithRetention(OwnerRetentionPolicy retention, string? keyVaultPath = null,
        string? leasePath = null, string? baseDirectory = null, string? filesPath = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var leases = GnOuGoWorkspace.ResolveDatabasePath(leasePath, root, ".GnOuGo/data/flow-planning-v11/leases");
        var vault = KeyVaultRecordStoreFactory.CreateWorkspaceStore(keyVaultPath, root);
        var fileRoot = DurableFileStore.ResolveRoot(filesPath, root);
        return new(vault, leases, null, retention, new(fileRoot, vault, retention.Clock));
    }

    public async Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSession initial, CancellationToken ct)
    {
        PlanningTokenThresholds.RejectReserved(initial.Request.Options);
        if (initial.Request.TenantId != (context.Limits.TenantId ?? "default"))
            throw new PlanningConflictException("The planning tenant must match the execution owner.");
        context.Limits.RunId ??= Guid.NewGuid().ToString("N");
        initial.Request.SessionId = PlanningGraphCompiler.Fingerprint(string.Join("\n", context.Limits.RunId,
            context.StageInvocationId ?? context.InvocationId));
        var tenant = initial.Request.TenantId;
        var key = initial.Request.SessionId;
        var lease = TryOwner(tenant, key) ?? throw new PlanningConflictException("This planning session is active in another runtime.");
        var inheritedBudget = context.LLMUsageBudget;
        IPlanningRuntimeModel? capturedModel = null;
        try
        {
            if (await records.GetAsync(Expired, tenant, key, Author, ct) is not null) throw new PlanningConflictException("owner_expired");
            var savedDefinition = await records.GetAsync(Definitions, tenant, key, Author, ct);
            var retainedDocument = savedDefinition is null ? null : JsonNode.Parse(savedDefinition.Value)!.AsObject();
            JsonNode? creationCheckpoint = null;
            var hasCreationCheckpoint = retainedDocument?.Remove("creationCheckpoint", out creationCheckpoint) == true;
            var retainedDefinition = retainedDocument?.Deserialize(PlanningJsonContext.Default.PlanningRequest);
            var definition = JsonSerializer.Serialize(initial.Request, PlanningJsonContext.Default.PlanningRequest);
            if (savedDefinition is not null && JsonSerializer.Serialize(retainedDefinition, PlanningJsonContext.Default.PlanningRequest) != definition)
                throw new PlanningConflictException("The planning request changed for this run ID. Resume the original request or start a new run.");
            var saved = await records.GetAsync(Sessions, tenant, key, Author, ct);
            // Capturing profiles must not alter the authored definition or the
            // caller's options. Apply them only to the first durable session copy.
            var state = saved is null
                ? JsonSerializer.Deserialize(JsonSerializer.Serialize(initial, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!
                : PlanningSessionStorage.Read(saved.Value, tenant, key);
            if (saved is null)
            {
                if (savedDefinition is null)
                {
                    if (hostPolicy is not null) await hostPolicy.CaptureAsync(state, initial, ct);
                    PlanningSessionCreation.Capture(state, initial);
                    state.Retention = retention.Capture().ForPlanning(state, retention.Clock.GetUtcNow(), true);
                }
                else if (hasCreationCheckpoint)
                {
                    // This host-owned snapshot precedes the first session write. It
                    // preserves creation limits even if caller defaults later change.
                    if (creationCheckpoint is not JsonObject checkpoint || checkpoint["version"] is not JsonValue version ||
                        !version.TryGetValue<int>(out var number) || number != 1 || checkpoint["session"] is not JsonObject creation)
                        throw new PlanningConflictException("The planning creation checkpoint is invalid.");
                    state = PlanningSessionStorage.Read(creation.ToJsonString(), tenant, key);
                    if (await records.GetAsync(WorkflowPlanningBudgetSink.Collection, tenant, key, Author, ct) is not null)
                        throw new PlanningConflictException("The active planning session checkpoint is missing. Its creation snapshot cannot reset retained usage.");
                    // A trusted host can resolve phase allocations before the first
                    // session write. Authenticate that capture before allowing its
                    // generation/phase fields to differ from the authored definition.
                    if (hostPolicy is not null) await hostPolicy.VerifyCreationAsync(state, ct);
                    foreach (var option in retainedDefinition!.Options)
                        if (!(hostPolicy is not null && option.Key == "construction_limits") &&
                            (!state.Request.Options.TryGetPropertyValue(option.Key, out var captured) || !JsonNode.DeepEquals(option.Value, captured)))
                            throw new PlanningConflictException("The planning creation checkpoint no longer matches its authored options.");
                    var capturedRequest = JsonSerializer.SerializeToNode(state.Request, PlanningJsonContext.Default.PlanningRequest)!.AsObject();
                    // Capture adds only private options. All authored request fields,
                    // including generation and policy limits, must still agree.
                    capturedRequest["options"] = retainedDefinition!.Options.DeepClone();
                    if (hostPolicy is not null)
                        capturedRequest["generation"] = JsonSerializer.SerializeToNode(retainedDefinition.Generation, PlanningJsonContext.Default.PlanningGenerationOptions);
                    if (!JsonNode.DeepEquals(capturedRequest, JsonNode.Parse(definition)))
                        throw new PlanningConflictException("The planning creation checkpoint no longer matches its authored request.");
                }
                else throw new PlanningConflictException("The planning creation checkpoint is missing. Start a fresh session; retained records are unchanged.");
            }
            if (state.SchemaVersion != 11 || state.Request.TenantId != tenant || state.Request.SessionId != key || saved is not null && savedDefinition is null)
                throw new PlanningConflictException("The planning session ownership or schema is invalid.");
            if (state.Retention is not { IsValid: true } capturedRetention || capturedRetention.IsDue(retention.Clock.GetUtcNow()))
                throw new PlanningConflictException("owner_expired");
            if (savedDefinition is null)
            {
                var durableDefinition = JsonNode.Parse(definition)!.AsObject();
                durableDefinition["creationCheckpoint"] = new JsonObject
                {
                    ["version"] = 1,
                    ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)
                };
                await records.UpsertAsync(Definitions, tenant, key, durableDefinition.ToJsonString(), Author, ct);
            }
            if (saved is null && files is not null)
                await files.TransferReferencesAsync(tenant, new(FileOwnerKind.Execution, context.Limits.RunId),
                    new(FileOwnerKind.WorkflowPlanning, key), context.Data, ct);
            // A crash after the receipt but before the coordinator checkpoint must not
            // replenish active time. Record timestamps bound the completed call interval.
            if (state.PendingCall is { } pending && await records.GetAsync(WorkflowPlanningModelJournal.Receipts, tenant, pending.Id, Author, ct) is { } completion && completion.UpdatedAt > state.UpdatedAtUtc)
            {
                state.ActiveMilliseconds += (completion.UpdatedAt - state.UpdatedAtUtc).TotalMilliseconds;
                state.UpdatedAtUtc = completion.UpdatedAt;
            }
            if (hostPolicy is not null) await hostPolicy.VerifyAsync(state, ct);
            else PlanningTokenThresholds.RejectReserved(state.Request.Options);
            if (hostPolicy is not null) capturedModel = await hostPolicy.CreateModelAsync(state, ct);
            var receipt = await records.GetAsync(WorkflowPlanningBudgetSink.Collection, tenant, key, Author, ct);
            var usage = receipt is null ? state.Usage : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)
                ?? throw new PlanningConflictException("The encrypted planning budget is invalid.");
            var parentBudget = inheritedBudget;
            if (state.Request.Options[PlanningApplicationBudget.OptionName] is JsonObject { } application &&
                application["mode"]?.ToString() == "limited" && application["applicationOwnsLimits"]?.GetValue<bool>() == false &&
                application["planningLimits"] is { } applicationLimits)
            {
                var capturedLimits = PlanningBudgetOptions.Parse(new JsonObject { ["llm_budget"] = applicationLimits.DeepClone() })! with { MaxElapsed = null };
                var applicationUsage = await records.GetAsync(ApplicationBudgetSink.Collection, tenant, key, Author, ct);
                var retainedUsage = applicationUsage is null ? null : JsonSerializer.Deserialize(applicationUsage.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)
                    ?? throw new PlanningConflictException("The encrypted application planning budget is invalid.");
                var hardSessionLimits = PlanningApplicationBudget.EnforceSessionLimits(state.Request);
                parentBudget = new LLMUsageBudgetScope(!hardSessionLimits && PlanningTokenThresholds.Advisory(state.Request), capturedLimits, retainedUsage,
                    inheritedBudget, new ApplicationBudgetSink(records, tenant, key), exchangeRateProvider: context.Engine.ExchangeRateProvider,
                    advisoryOptimization: !hardSessionLimits && PlanningTokenThresholds.OptimizationAdvisory(state.Request)) { PolicyOrigin = "captured_application_planning_limits", AdvisoryFinancialForecasts = application["financialForecastMode"]?.ToString() == "experimental_warning" };
            }
            var explicitLimits = PlanningApplicationBudget.ExplicitWorkflowLimits(state.Request);
            var advisorySession = !explicitLimits && !PlanningApplicationBudget.EnforceSessionLimits(state.Request);
            var configuredLimits = PlanningBudgetOptions.Parse(state.Request.Options);
            var limits = (configuredLimits ?? new LLMUsageBudgetLimits()) with
                { MaxElapsed = explicitLimits ? configuredLimits?.MaxElapsed : null,
                  MaxCalls = explicitLimits ? configuredLimits?.MaxCalls : Math.Min(configuredLimits?.MaxCalls ?? state.Request.MaxModelCalls, state.Request.MaxModelCalls) };
            var budget = PlanningApplicationBudget.TrackingOnly(state.Request)
                ? LLMUsageBudgetScope.CreatePlanningTrackingOnly(state.Request, usage, parentBudget, new WorkflowPlanningBudgetSink(records, tenant, key),
                    exchangeRateProvider: context.Engine.ExchangeRateProvider)
                : new LLMUsageBudgetScope(advisorySession && PlanningTokenThresholds.Advisory(state.Request), limits, usage, parentBudget,
                new WorkflowPlanningBudgetSink(records, tenant, key), exchangeRateProvider: context.Engine.ExchangeRateProvider,
                advisoryOptimization: advisorySession && PlanningTokenThresholds.OptimizationAdvisory(state.Request))
                    { AdvisoryFinancialForecasts = PlanningApplicationBudget.AdvisoryFinancialForecasts(state.Request) };
            // The journal meters actual dispatches; replaying a receipt consumes no new allowance.
            context.LLMUsageBudget = null;
            var client = new WorkflowPlanningModelJournal(context, records, state.Request, budget,
                PlanningDispatchDeadline.Resolve(state.Request, state.ActiveMilliseconds), capturedModel, requireCapturedMetadata: hostPolicy is not null);
            if (state.PendingCall is { } retainedCall)
            {
                await client.RecoverSavedResponseAsync(retainedCall.Request, ct);
                if (await records.GetAsync(WorkflowPlanningModelJournal.Receipts, tenant, retainedCall.Id, Author, ct) is { } recovered && recovered.UpdatedAt > state.UpdatedAtUtc)
                {
                    state.ActiveMilliseconds += (recovered.UpdatedAt - state.UpdatedAtUtc).TotalMilliseconds;
                    state.UpdatedAtUtc = recovered.UpdatedAt;
                }
            }
            var committedProgress = PlanningRetentionCheckpoint.From(state);
            var retained = state.Retention;
            async Task Checkpoint(PlanningSession snapshot, CancellationToken token)
            {
                if (retained is { Phase: not OwnerRetention.Active } && retained.IsDue(retention.Clock.GetUtcNow()))
                    throw new PlanningConflictException("owner_expired");
                snapshot.Usage = budget.Snapshot;
                var progress = PlanningRetentionCheckpoint.From(snapshot);
                snapshot.Retention = retained?.ForPlanning(snapshot, retention.Clock.GetUtcNow(), progress != committedProgress);
                await records.UpsertAsync(Sessions, tenant, key, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSession), Author, token);
                retained = snapshot.Retention;
                committedProgress = progress;
            }
            await Checkpoint(state, ct);
            context.SetTelemetryAttribute("gnougo-flow.plan.session_id", key);
            context.SetTelemetryAttribute("gnougo-flow.plan.run_id", context.Limits.RunId);
            return new OwnedSession(state, new WorkflowPlanningRuntime(context, client, Checkpoint, capturedModel?.Capabilities, new PlanningCapabilitySnapshotJournal(records, PlanningSnapshotOwner.Workflow)), lease, () => context.LLMUsageBudget = inheritedBudget, capturedModel);
        }
        catch { context.LLMUsageBudget = inheritedBudget; if (capturedModel is not null) await capturedModel.DisposeAsync(); await lease.DisposeAsync(); throw; }
    }

    public async Task<string> ReadApprovedYamlAsync(StepExecutionContext context, string sessionId, string artifactHash, CancellationToken ct)
    {
        var tenant = context.Limits.TenantId ?? "default";
        var stored = await records.GetAsync(Sessions, tenant, sessionId, Author, ct)
            ?? throw new PlanningConflictException("The approved format-11 planning session is unavailable for this tenant. Regenerate and approve the workflow.");
        var state = PlanningSessionStorage.Read(stored.Value, tenant, sessionId);
        if (state.Retention is not { IsValid: true } capturedRetention || capturedRetention.IsDue(retention.Clock.GetUtcNow()) ||
            await records.GetAsync(Expired, tenant, sessionId, Author, ct) is not null)
            throw new PlanningConflictException("owner_expired");
        if (hostPolicy is not null) await hostPolicy.VerifyAsync(state, ct);
        else PlanningTokenThresholds.RejectReserved(state.Request.Options);
        if (state.SchemaVersion != 11 || state.Request.TenantId != tenant || state.Request.SessionId != sessionId ||
            state.Status != PlanningStatus.Approved || state.ApprovedHash != artifactHash || PlanningArtifactApproval.Hash(state) != artifactHash)
            throw new PlanningConflictException("The artifact does not have a current, tenant-owned approval.");
        PlanningArtifactApproval.Verify(state);
        var runtime = new WorkflowPlanningRuntime(context.Engine, (_, _) => Task.CompletedTask);
        if ((await runtime.ValidateCatalogAsync(state.Catalog!, ct)).Any(d => d.Required))
            throw new PlanningConflictException("Capability contracts changed after approval.");
        var current = await records.GetAsync(Sessions, tenant, sessionId, Author, ct);
        if (capturedRetention.IsDue(retention.Clock.GetUtcNow()) || current?.Value != stored.Value ||
            await records.GetAsync(Expired, tenant, sessionId, Author, ct) is not null)
            throw new PlanningConflictException("The approved planning session expired or changed during validation.");
        if (files is not null && context.Limits.RunId is { } run)
            await files.TransferReferencesAsync(tenant, new(FileOwnerKind.WorkflowPlanning, sessionId), new(FileOwnerKind.Execution, run),
                JsonSerializer.SerializeToNode(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!), WorkflowRunJsonContext.Default.WorkflowDocument), ct);
        return state.Yaml!;
    }

    private sealed class ApplicationBudgetSink(IKeyVaultRecordStore store, string tenant, string session) : ILLMUsageBudgetSink
    {
        internal const string Collection = "flow-planning-application-budgets-v1";
        public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
            => await store.UpsertAsync(Collection, tenant, session,
                JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), Author, ct);
    }

    private sealed record OwnedSession(PlanningSession Session, IPlanningRuntime Runtime, FileStream Lease, Action Restore, IPlanningRuntimeModel? Model) : IPlanningRuntimeSession
    {
        public async ValueTask DisposeAsync() { Restore(); try { if (Model is not null) await Model.DisposeAsync(); } finally { await Lease.DisposeAsync(); } }
    }
}
