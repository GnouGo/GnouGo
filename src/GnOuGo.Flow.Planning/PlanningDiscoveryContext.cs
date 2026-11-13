using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Exact contract resolution and durable provider-page reads for the current step.
internal static class PlanningDiscoveryContext
{
    internal static async Task ResolveAsync(PlanningSession state, IPlanningRuntime runtime, CapabilitySummary summary, CancellationToken ct)
    {
        var cached = state.Discovery.Resolved.SingleOrDefault(c => c.Id == summary.Id && c.Version == summary.Version);
        PlanningCapability resolved;
        try { resolved = cached ?? await runtime.Capabilities.ResolveAsync(summary, ct); }
        catch (PlanningConflictException ex) when (ex.Category is "catalog_changed" or "invalid_contract")
        { throw RequiredContractFailure(ex.Category); }
        catch (CapabilityDiscoveryException ex)
        { throw RequiredContractFailure(ex.Category); }
        if (resolved.Id != summary.Id || resolved.Version != summary.Version ||
            summary.ArtifactContract is not null && !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(summary.ArtifactContract, PlanningJsonContext.Default.McpArtifactContract),
                JsonSerializer.SerializeToNode(resolved.ArtifactContract, PlanningJsonContext.Default.McpArtifactContract)) ||
            PlanningContractValidation.ValidateSchema(resolved.InputSchema).Count > 0 ||
            resolved.OutputSchema.Count > 0 && PlanningContractValidation.ValidateSchema(resolved.OutputSchema).Count > 0 ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(TaskOperations.Describe(resolved), PlanningJsonContext.Default.PlanningOperation),
                JsonSerializer.SerializeToNode(summary.Operation, PlanningJsonContext.Default.PlanningOperation)) &&
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(PlanningCapabilityArguments.Editable(resolved), PlanningJsonContext.Default.PlanningOperation),
                JsonSerializer.SerializeToNode(summary.Operation, PlanningJsonContext.Default.PlanningOperation)))
            throw new PlanningConflictException("The operation contract changed after discovery.");
        if (cached is null) state.Discovery.Resolved.Add(resolved);
        state.Discovery.ResolutionFailures?.RemoveAll(f => f.Id == summary.Id && f.Version == summary.Version);

        GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException RequiredContractFailure(string category)
        {
            category = category is "catalog_changed" or "invalid_contract" or "access_denied" or "timeout" or "cursor_incompatible"
                ? category : "transport_unavailable";
            if (!state.Discovery.Sources.Any(s => s.Id == summary.SourceId))
                return new("DISCOVERY_OWNERSHIP_INVALID", "The selected contract does not belong to an owned metadata source.");
            var step = state.Construction!.Compact!.CurrentStep!;
            var focus = PlanningDiscoveryFocus.Choices(state)[0];
            var page = state.Discovery.Pages.LastOrDefault(p => p.SourceId == summary.SourceId && p.Capabilities.Any(c => c.Id == summary.Id && c.Version == summary.Version));
            var failure = new CapabilityDiscoveryFailure(Guid.NewGuid().ToString("N"), summary.SourceId, focus.Id, focus.Query, page?.Cursor,
                category, "The selected operation contract could not be verified; no implementation binding was accepted.",
                page?.SnapshotVersion, state.RequestMeasurements?.LastOrDefault()?.RequestId, null, DateTimeOffset.UtcNow)
                { StepId = step.Id, CapabilityId = summary.Id, OperationId = summary.Operation?.Id, ContractVersion = summary.Version };
            (state.Discovery.Failures ??= []).Add(failure);
            PlanningInspection.Record(state, PlanningCompactConstruction.DiscoveryPurpose, "blocked", failure.Message, failure.RequestId,
                new(failure.SourceId, failure.FocusId, null, failure.SnapshotVersion, failure.Cursor, category, failure.Id));
            var code = category switch { "catalog_changed" => "DISCOVERY_REQUIRED_CONTRACT_CHANGED", "invalid_contract" => "DISCOVERY_REQUIRED_CONTRACT_INVALID", _ => "DISCOVERY_REQUIRED_CONTRACT_UNAVAILABLE" };
            return new(code, failure.Message + " Explicit metadata recovery is required before a new binding decision.");
        }
    }
    internal static async Task DiscoverPageAsync(PlanningSession state, IPlanningRuntime runtime, string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null,
        PlanningConstructionDiscoveryContinuation? action = null, PlanningDiscoveryRequest? selection = null)
    {
        CapabilityPage page;
        var focused = action?.Id is not null;
        try
        {
            page = focused ? await PlanningDiscoverySnapshots.ReadAsync(state, runtime, action!, selection!, ct)
                : await runtime.Capabilities.ListAsync(source, cursor, ct, query, producedArtifactKind);
            if (focused && (page.SourceId != source || page.Capabilities.Any(c => c.SourceId != source)))
                throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("DISCOVERY_OWNERSHIP_INVALID", "The metadata page belongs to another source.");
            if (page.SourceId != source || page.Cursor != cursor || page.ProducedArtifactKind != producedArtifactKind ||
                producedArtifactKind is not null && page.Capabilities.Any(c => c.ArtifactContract?.Produces.Any(a => a.Kind == producedArtifactKind) != true) || page.Query is not null && page.Query != query || page.Capabilities.Any(c => c.SourceId != source) ||
                page.Capabilities.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != page.Capabilities.Count)
                page = new(source, cursor, [], null, "The source returned an ambiguous discovery contract.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PlanningConflictException) when (focused) { throw; }
        catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException) when (focused) { throw; }
        catch (CapabilityDiscoveryException ex) when (focused)
        { page = new(source, cursor, [], null, ex.Message) { FailureId = RecordFailure(ex.Category), FailureCategory = ex.Category }; }
        catch (Exception ex) when (!focused)
        {
            var failure = Capabilities.CapabilityDiscovery.ProviderFailure(ex);
            page = new(source, cursor, [], null, failure.Message) { FailureCategory = failure.Category };
        }
        if (focused && page.UnavailableReason is not null && page.FailureId is null)
            page = page with { FailureId = RecordFailure(page.FailureCategory ?? "invalid_contract"), FailureCategory = page.FailureCategory ?? "invalid_contract" };
        page = page with { Query = query, ProducedArtifactKind = producedArtifactKind, FocusId = selection?.FocusId, ActionId = action?.Id };
        state.Discovery.Pages.Add(page);
        if (page.UnavailableReason is { } reason) state.Discovery.Limitations.Add(source + ": " + reason);
        if (focused)
            PlanningInspection.Record(state, PlanningCompactConstruction.DiscoveryPurpose, page.UnavailableReason is null ? "succeeded" : "warning",
                page.UnavailableReason is null ? "Metadata page retained; contract validation and business review remain separate." : "Exploratory source unavailable — continuing with other sources.",
                state.RequestMeasurements?.LastOrDefault()?.RequestId,
                new(source, selection?.FocusId, selection?.Justification, page.SnapshotVersion, cursor,
                    page.FailureId is { } failure ? state.Discovery.Failures!.Single(f => f.Id == failure).Category : "received", page.FailureId));

        string RecordFailure(string category)
        {
            var failure = new CapabilityDiscoveryFailure(Guid.NewGuid().ToString("N"), source, selection?.FocusId, query!, cursor,
                category, "The source metadata is unavailable; incomplete inspection proves no absence.",
                action?.Snapshots?.GetValueOrDefault(source)?.Version, state.RequestMeasurements?.LastOrDefault()?.RequestId,
                selection?.Justification, DateTimeOffset.UtcNow) { StepId = state.Construction?.Compact?.CurrentStep?.Id };
            (state.Discovery.Failures ??= []).Add(failure);
            return failure.Id;
        }
    }

}
