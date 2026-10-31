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
        var resolved = cached ?? await runtime.Capabilities.ResolveAsync(summary, ct);
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
    }
    internal static async Task DiscoverPageAsync(PlanningSession state, IPlanningRuntime runtime, string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
    {
        CapabilityPage page;
        try
        {
            page = await runtime.Capabilities.ListAsync(source, cursor, ct, query, producedArtifactKind);
            if (page.SourceId != source || page.Cursor != cursor || page.ProducedArtifactKind != producedArtifactKind ||
                producedArtifactKind is not null && page.Capabilities.Any(c => c.ArtifactContract?.Produces.Any(a => a.Kind == producedArtifactKind) != true) || page.Query is not null && page.Query != query || page.Capabilities.Any(c => c.SourceId != source) ||
                page.Capabilities.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != page.Capabilities.Count)
                page = new(source, cursor, [], null, "The source returned an ambiguous discovery contract.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { page = new(source, cursor, [], null, "This source is unavailable; its capabilities have not been inspected."); }
        page = page with { Query = query, ProducedArtifactKind = producedArtifactKind };
        state.Discovery.Pages.Add(page);
        if (page.UnavailableReason is { } reason) state.Discovery.Limitations.Add(source + ": " + reason);
    }

}
