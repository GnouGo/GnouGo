using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal const string ReviewEvidenceBatchSemantics =
        "Repeated exact addresses are retrieved once within this batch; prior successful reads are not new evidence.";

    internal static void ConstrainReviewEvidence(PlanningSession state, JsonObject context, JsonObject schema)
    {
        if (!UsesDecisionView(state) || schema["properties"]?["action"]?["anyOf"] is not JsonArray actions) return;
        var requestAction = actions.OfType<JsonObject>().SingleOrDefault(action =>
            action["properties"]?["kind"]?["enum"]?[0]?.ToString() == "evidenceRequests");
        // The existing single report follow-up deliberately has no evidence action.
        if (requestAction is null) return;
        var addresses = StepOriginalEvidence(state);
        addresses.UnionWith(PrerequisiteAddresses(state, (context["requirements"] as JsonArray ?? [])
            .Select(row => row!["id"]!.ToString())).Where(IsOriginalAddress));
        if (UsesIncrementalReview(state))
            addresses.UnionWith(NavigationEvidence((context["reusedPropositions"] as JsonArray ?? [])
                .OfType<JsonObject>().Select(row => row["sourceKey"])).Intersect(
                PrerequisiteAddresses(state, state.Construction!.Requirements.Select(requirement => requirement.Id))
                    .Where(IsOriginalAddress), StringComparer.Ordinal));
        addresses.UnionWith(NavigationEvidence(new[]
        {
            "requirementsDirectory", "nextRequirements", "checkpointEvidence", "scopeEvidence", "interfacesDirectory",
            "reusableInterfaces", "taskEvidence", "groupEvidence", "scopeInterface", "interfaces", "inputDeclarations", "operations",
            "companionOperations", "rebuild", "continuation", "ancestorControls"
        }.Select(name => context[name]).Append(context["taskEvidence"]?["scopeOwner"])
            .Concat((context["evidence"] as JsonObject ?? []).Select(item => item.Value))));
        // Displayed host links are the only navigation authority. Retained originals
        // remain in the context; neither a different batch nor a new window makes
        // a previously supplied member into fresh evidence for this candidate.
        addresses.ExceptWith((state.Construction!.Evidence ?? []).Select(item => item.Key));
        addresses.RemoveWhere(address => state.Construction.EvidenceRequests.Contains(
            PlanningConstruction.EvidenceBatchFingerprint(state, [address]), StringComparer.Ordinal));
        if (addresses.Count == 0) actions.Remove(requestAction);
        else
        {
            var offered = PlanningSchemas.Array(PlanningSchemas.Enum(addresses.Order(StringComparer.Ordinal).ToArray()),
                1, HasEvidenceAdmissionLimit(state) ? 1 : 8);
            if (State(state).PresentationVersion is 10 or 11) offered["description"] = ReviewEvidenceBatchSemantics;
            requestAction["properties"]!["addresses"] = offered;
        }
    }

    internal static bool CoalesceReviewEvidenceIssued(JsonNode? schema) =>
        schema?["properties"]?["action"]?["anyOf"] is JsonArray actions && actions.Any(action =>
            action?["properties"]?["kind"]?["enum"]?[0]?.ToString() == "evidenceRequests" &&
            action?["properties"]?["addresses"]?["description"]?.ToString() == ReviewEvidenceBatchSemantics);
}
