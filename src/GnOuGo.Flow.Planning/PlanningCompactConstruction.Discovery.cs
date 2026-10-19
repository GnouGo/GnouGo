using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private sealed record ProviderRequest(string SourceId, string? Cursor, bool Available = true, string? Unavailable = null, int ResultCount = 0);

    private static List<ProviderRequest> ProviderRequestOptions(PlanningSession state)
    {
        var result = new List<ProviderRequest>();
        var objective = State(state).CurrentStep!.Objective;
        foreach (var source in state.Discovery.Sources)
        {
            var pages = state.Discovery.Pages.Where(p => p.SourceId == source.Id && p.Query == objective && p.ProducedArtifactKind is null).ToArray();
            if (pages.Length == 0) result.Add(new(source.Id, null));
            else
            {
                var continuations = pages.Where(p => p.NextCursor is not null && pages.All(next => next.Cursor != p.NextCursor))
                    .Select(p => new ProviderRequest(source.Id, p.NextCursor, ResultCount: pages.Sum(page => page.Capabilities.Count))).Distinct().ToArray();
                if (continuations.Length > 0) result.AddRange(continuations);
                else result.Add(new(source.Id, null, Available: false, Unavailable: pages[^1].UnavailableReason,
                    ResultCount: pages.Sum(page => page.Capabilities.Count)));
            }
        }
        return result;
    }

    private static string ProviderRequestVersion(PlanningSession state) => PlanningGraphCompiler.Fingerprint(
        State(state).CurrentStep!.Objective + "\n" + new JsonArray(ProviderRequestOptions(state).Select(option => (JsonNode)new JsonObject
            { ["sourceId"] = option.SourceId, ["cursor"] = option.Cursor, ["available"] = option.Available,
                ["sourceVersion"] = PlanningGraphCompiler.Fingerprint(state.Discovery.Sources.Single(s => s.Id == option.SourceId).Description),
                ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount }).ToArray()).ToJsonString());

    internal static string ProviderRequestAddress(PlanningSession state, int offset) =>
        "page:provider-requests:" + ProviderRequestVersion(state) + ":" + offset.ToString(CultureInfo.InvariantCulture);

    internal static JsonObject ProviderRequestPage(PlanningSession state, int offset)
    {
        var options = ProviderRequestOptions(state);
        if (offset < 0 || offset > 0 && offset >= options.Count)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The provider-action page is outside the current exact continuation directory.");
        return new JsonObject
        {
            ["version"] = ProviderRequestVersion(state), ["offset"] = offset, ["total"] = options.Count,
            ["items"] = new JsonArray(options.Skip(offset).Take(2).Select(option =>
            {
                var source = state.Discovery.Sources.Single(s => s.Id == option.SourceId);
                var description = NavigationDescription(source.Description);
                return (JsonNode)new JsonObject { ["sourceId"] = source.Id, ["cursor"] = option.Cursor,
                    ["available"] = option.Available, ["unavailable"] = option.Unavailable, ["resultCount"] = option.ResultCount,
                    ["description"] = description, ["descriptionComplete"] = description.Length == source.Description.Length,
                    ["descriptionEvidence"] = "source-description:" + Uri.EscapeDataString(source.Id) + "@" + PlanningGraphCompiler.Fingerprint(source.Description) + ":0" };
            }).ToArray()),
            ["next"] = offset + 2 < options.Count ? ProviderRequestAddress(state, offset + 2) : null
        };
    }

    private static JsonObject? ProviderEvidencePage(PlanningSession state)
    {
        foreach (var (address, value) in state.Construction!.Evidence ?? new JsonObject())
            if (address.StartsWith("page:provider-requests:", StringComparison.Ordinal) && value is JsonObject page &&
                page["offset"] is JsonValue position && position.TryGetValue<int>(out var offset) && offset >= 0 &&
                address == ProviderRequestAddress(state, offset) && JsonNode.DeepEquals(page, ProviderRequestPage(state, offset))) return page;
        return null;
    }

    private static JsonObject? PresentedProviderRequests(PlanningSession state)
    {
        if (ProviderEvidencePage(state) is { } page) return page;
        if (state.Construction!.Evidence is { Count: > 0 } || ContractDescriptors(state).Count > 0) return null;
        return ProviderRequestPage(state, 0);
    }

    private static JsonObject CompactDiscoverySchema(PlanningSession state)
    {
        var requests = PresentedProviderRequests(state)?["items"]?.AsArray() ?? [];
        var alternatives = new JsonArray(requests.Where(request => request!["available"]!.GetValue<bool>()).Select(request => (JsonNode)PlanningSchemas.Object(
            ("sourceId", PlanningSchemas.Enum(request!["sourceId"]!.ToString())),
            ("cursor", request["cursor"] is { } cursor ? PlanningSchemas.Enum(cursor.ToString()) : PlanningSchemas.Type("null")),
            ("query", PlanningSchemas.Type("null")), ("operationIds", PlanningSchemas.Type("null")))).ToArray());
        return PlanningSchemas.Object(
            ("operationIds", PlanningSchemas.Nullable(Strings(1, 2))),
            ("discoveryRequests", alternatives.Count == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(
                PlanningSchemas.Array(alternatives.Count == 1 ? alternatives[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = alternatives }, 1, 2))),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1, 4))),
            ("reselectStep", CanReselectDiscovery(state) ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
    }

    internal static bool CanReselectDiscovery(PlanningSession state)
    {
        var construction = state.Construction!; var step = State(state).CurrentStep;
        if (construction.Version != 2 || step is not { Kind: "operation", DiscoveryComplete: false } || construction.Candidate is not null ||
            state.EditablePaths is not null || state.Request.Options["automatic_correction"]?.ToString() == "false" ||
            construction.DraftCorrections >= construction.Limits.MaxCorrectionsPerDraft) return false;
        var diagnosed = state.Diagnostics.Any(d => d.Required);
        if (!diagnosed && PlanningModelCalls.RemainingRepairs(state) == 0) return false;
        return diagnosed || state.Discovery.Sources.Count == 0 || ContractDescriptors(state).Count > 0 ||
            state.Discovery.Pages.Any(p => p.Query == step.Objective) || (construction.Evidence?.Select(pair => pair.Key).Any(key =>
                key.StartsWith("page:operations:", StringComparison.Ordinal) || key.StartsWith("page:sources:", StringComparison.Ordinal) ||
                key.StartsWith("description:", StringComparison.Ordinal) || key.StartsWith("source-description:", StringComparison.Ordinal)) ?? false);
    }

    private static bool LoadProviderRequestEvidence(PlanningSession state, string address, out JsonNode? value)
    {
        value = null;
        if (!address.StartsWith("page:provider-requests:", StringComparison.Ordinal)) return false;
        if (State(state).CurrentStep is null) return true;
        if (address.Split(':') is not ["page", "provider-requests", _, var cursor] ||
            !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0 ||
            address != ProviderRequestAddress(state, offset)) return true;
        value = ProviderRequestPage(state, offset);
        return true;
    }
}
