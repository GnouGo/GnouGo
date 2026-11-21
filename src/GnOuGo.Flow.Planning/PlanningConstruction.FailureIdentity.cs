using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal const string SelectedOperationRule = "selected_block_operation";

    internal static string FailureIdentity(PlanningSession state, PlanningConstructionCandidate candidate, string? planHash = null)
    {
        var hash = planHash ?? candidate.Hash;
        var compact = state.Construction!.Compact!;
        var identity = new JsonObject
        {
            ["baseline"] = candidate.BaseHash, ["plan"] = hash,
            ["scope"] = candidate.Extension.Scope,
            ["openScopes"] = Set(candidate.OpenScopes),
            ["closeScopes"] = Set(candidate.Extension.CloseScopes),
            ["complete"] = candidate.Extension.Complete,
            ["requirements"] = Set(compact.CurrentStep!.RequirementIds),
            ["associatedRequirements"] = Set(candidate.Extension.RequirementIds),
            ["closeRequirements"] = Set(compact.CurrentStep.CloseRequirementIds),
            ["commitments"] = new JsonArray(compact.PendingCommitments.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (JsonNode)new JsonObject
                {
                    ["requirement"] = item.Key,
                    ["bindings"] = new JsonArray(item.Value.Select(binding =>
                            (Operation: binding.Operation, Port: binding.Result?.Port, Output: binding.Result?.Output))
                        .Distinct().OrderBy(binding => binding.Operation, StringComparer.Ordinal)
                        .ThenBy(binding => binding.Port, StringComparer.Ordinal).ThenBy(binding => binding.Output, StringComparer.Ordinal)
                        .Select(binding => (JsonNode)new JsonObject
                        {
                            ["operation"] = binding.Operation, ["port"] = binding.Port, ["output"] = binding.Output
                        }).ToArray())
                }).ToArray())
        };
        identity["continuationBinding"] = Node(candidate.Extension.ContinuationBinding, PlanningJsonContext.Default.PlanningConstructionContinuationBinding);
        identity["boundaryTransitions"] = Node(candidate.Extension.BoundaryTransitions, PlanningJsonContext.Default.ListPlanningConstructionBoundaryTransition);
        return "candidate:v1:" + PlanningGraphCompiler.Fingerprint(identity.ToJsonString());

        static JsonArray Set(IEnumerable<string> values) =>
            new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    internal static bool PreviouslyRejected(PlanningSession state, PlanningConstructionCandidate candidate, string? planHash = null)
    {
        var rejected = state.Construction!.FailedCandidates;
        var identity = FailureIdentity(state, candidate, planHash);
        return rejected.Contains(identity, StringComparer.Ordinal) ||
            rejected.Contains(SelectionFailureIdentity(state, identity), StringComparer.Ordinal);
    }

    internal static void RecordRejection(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        var identity = FailureIdentity(state, candidate);
        if (candidate.Diagnostics.Count > 0 && candidate.Diagnostics.All(finding =>
            finding.Code == PlanningCommitments.MissingOperation && finding.Rule == SelectedOperationRule))
            identity = SelectionFailureIdentity(state, identity);
        if (!state.Construction!.FailedCandidates.Contains(identity, StringComparer.Ordinal))
            state.Construction.FailedCandidates.Add(identity);
    }

    private static string SelectionFailureIdentity(PlanningSession state, string identity)
    {
        var operations = state.Construction!.Compact!.CurrentStep!.OperationIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return identity + ":selection:" + PlanningGraphCompiler.Fingerprint(new JsonArray(operations
            .Select(operation => (JsonNode?)JsonValue.Create(operation)).ToArray()).ToJsonString());
    }
}
