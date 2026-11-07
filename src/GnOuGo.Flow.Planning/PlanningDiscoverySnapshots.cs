using GnOuGo.Flow.Core.Expressions;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning;

internal static class PlanningDiscoverySnapshots
{
    internal static async Task<CapabilityPage> ReadAsync(PlanningSession state, IPlanningRuntime runtime,
        PlanningConstructionDiscoveryContinuation action, PlanningDiscoveryRequest request, CancellationToken ct)
    {
        var query = request.Query!;
        if (runtime.Capabilities is not ICapabilitySnapshotCatalog catalog)
        {
            try { return await runtime.Capabilities.ListAsync(request.SourceId, request.Cursor, ct, query); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PlanningConflictException) { throw; }
            catch (WorkflowRuntimeException) { throw; }
            catch (Exception ex) { throw CapabilityDiscovery.ProviderFailure(ex); }
        }
        CapabilitySourceSnapshot snapshot;
        string version;
        if (action.Snapshots?.GetValueOrDefault(request.SourceId) is { } reference)
        {
            var journal = runtime.CapabilitySnapshots ?? throw new PlanningConflictException("The snapshot journal is required to restore this discovery action.");
            try { snapshot = await journal.ReadAsync(reference, state.Request.TenantId, state.Request.SessionId, action.Id!, request.SourceId, ct); }
            catch (PlanningConflictException) { throw new WorkflowRuntimeException("DISCOVERY_SNAPSHOT_INVALID", "The retained catalog snapshot could not be verified; no source was refetched."); }
            version = reference.Version;
        }
        else if (action.InlineSnapshots?.GetValueOrDefault(request.SourceId) is { } inline)
        {
            snapshot = inline;
            version = Version(snapshot);
        }
        else
        {
            try { snapshot = await catalog.CaptureAsync(request.SourceId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PlanningConflictException) { throw; }
            catch (WorkflowRuntimeException) { throw; }
            catch (Exception ex) { throw CapabilityDiscovery.ProviderFailure(ex); }
            if (snapshot.SourceId != request.SourceId || string.IsNullOrWhiteSpace(snapshot.ConfigurationFingerprint))
                throw new PlanningConflictException("The captured source snapshot has another owner.");
            version = Version(snapshot);
            if (runtime.CapabilitySnapshots is { } journal)
            {
                reference = await journal.PublishAsync(state.Request.TenantId, state.Request.SessionId, action.Id!, snapshot, ct);
                (action.Snapshots ??= new(StringComparer.Ordinal))[request.SourceId] = reference;
            }
            else (action.InlineSnapshots ??= new(StringComparer.Ordinal))[request.SourceId] = snapshot;
            await runtime.CheckpointAsync(state, ct);
        }
        if (snapshot.SourceId != request.SourceId || snapshot.Capabilities.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Capabilities.Count)
            throw new PlanningConflictException("The retained source snapshot is invalid.");
        try { return catalog.ReadPage(snapshot, request.Cursor, query) with { SnapshotVersion = version }; }
        // A pinned snapshot must never silently reinterpret a mismatched cursor.
        catch (ArgumentException) { throw new WorkflowRuntimeException("DISCOVERY_CURSOR_INCOMPATIBLE", "The cursor does not belong to the retained catalog snapshot."); }
    }

    internal static string Version(CapabilitySourceSnapshot snapshot) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(snapshot, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot));
}
