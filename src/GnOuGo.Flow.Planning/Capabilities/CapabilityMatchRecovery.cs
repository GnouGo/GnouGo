using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core;
using Expressions = GnOuGo.Flow.Core.Expressions;
using Parsing = GnOuGo.Flow.Core.Parsing;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

using static GnOuGo.Flow.Planning.Capabilities.CapabilityCatalogBuilder;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityMatchRecovery
{

    internal static CapabilityMatchingEvaluation BuildMalformedCapabilityMatchingEvaluation(
        CapabilityInventory inventory, string reason)
    {
        var sanitized = SanitizeCapabilityInferenceDiagnostic(reason, 1_000);
        if (sanitized.Length == 0)
            sanitized = "The matching response was not a valid structured object.";
        var operationMatches = inventory.Operations.Select(operation =>
            new CapabilityOperationMatch(operation, "invalid", sanitized, Array.Empty<string>(), Array.Empty<string>())).ToArray();
        var constraintMatches = inventory.Constraints.Select(constraint =>
            new CapabilityConstraintMatch(constraint, "invalid", sanitized, Array.Empty<string>(), Array.Empty<string>())).ToArray();
        var issues = inventory.Operations.Select(operation =>
                new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, "invalid", sanitized, Array.Empty<string>())
                {
                    ValidationIssue = "matching_response_malformed",
                    InvalidFields = ["$"]
                })
            .Concat(inventory.Constraints.Select(constraint =>
                new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, "invalid", sanitized, Array.Empty<string>())))
            .ToArray();
        return new CapabilityMatchingEvaluation(operationMatches, constraintMatches, issues, false);
    }

    internal static string ReadMatchingString(JsonObject node, string property)
        => node[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

    internal static IReadOnlyList<string> ReadMatchingIds(JsonNode? node, int maximum, out bool valid)
    {
        valid = node is JsonArray;
        if (node is not JsonArray values || values.Count > maximum)
        {
            valid = false;
            return Array.Empty<string>();
        }
        var result = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var id)
                || string.IsNullOrWhiteSpace(id))
            {
                valid = false;
                continue;
            }

            var normalized = id.Trim();
            if (seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    internal static CapabilityMatchingShapeDiagnostic BuildInvalidMatchingDiagnostic(
        string status, bool selectedArrayValid, bool candidateArrayValid, bool selectedIdsKnown, bool candidateIdsKnown, bool reasonPresent, int selectedCount, int candidateCount, string decisionOperationId, string conditionalMode, bool conditionalTopologyValid, CapabilityInventoryOperation operation)
    {
        static CapabilityMatchingShapeDiagnostic Diagnostic(string code, string reason, params string[] fields)
            => new(code, reason, fields);

        if (status is not ("matched" or "composed" or "conditional" or "local" or "ambiguous" or "unavailable"))
            return Diagnostic("operation_status_invalid", "The operation match returned an unsupported status.", "status");
        if (!selectedArrayValid)
            return Diagnostic("catalog_ids_invalid", "The operation match returned malformed or excessive selected catalog IDs.", "catalog_ids");
        if (!candidateArrayValid)
            return Diagnostic("candidate_catalog_ids_invalid", "The operation match returned malformed or excessive candidate catalog IDs.", "candidate_catalog_ids");
        if (!selectedIdsKnown || !candidateIdsKnown)
            return Diagnostic(
                "catalog_id_unknown",
                "The operation match referenced one or more unknown catalog IDs.",
                !selectedIdsKnown ? "catalog_ids" : "candidate_catalog_ids");
        if (!reasonPresent)
            return Diagnostic("reason_missing", "The operation match omitted its required bounded reason.", "reason");
        if (operation.ExecutionKind == "local_processing" && status != "local"
            || operation.ExecutionKind != "local_processing" && status == "local")
        {
            return Diagnostic(
                "local_status_invalid",
                "The operation status is inconsistent with its locked local-processing classification.",
                "status",
                "catalog_ids",
                "candidate_catalog_ids");
        }

        var decisionExpected = status == "conditional";
        var decisionValid = decisionExpected
            ? decisionOperationId.Length > 0
              && string.Equals(decisionOperationId, operation.DecisionSourceOperationId, StringComparison.Ordinal)
            : decisionOperationId.Length == 0;
        if (!decisionValid)
            return Diagnostic("decision_reference_invalid", "The operation match returned an invalid conditional decision reference.", "decision_operation_id");

        var conditionalModeRecognized = conditionalMode is ConditionalExactlyOneActivationMode or ConditionalAllOnValueActivationMode;
        if (decisionExpected ? !conditionalModeRecognized : conditionalMode.Length > 0)
            return Diagnostic("conditional_mode_invalid", "The operation match returned an invalid conditional activation mode.", "conditional_mode");

        var cardinalityValid = status switch
        {
            "matched" => selectedCount == 1 && candidateCount == 0,
            "composed" => selectedCount >= 2 && candidateCount == 0,
            "conditional" => selectedCount >= 1 && candidateCount == 0,
            "local" or "unavailable" => selectedCount == 0 && candidateCount == 0,
            "ambiguous" => selectedCount == 0 && candidateCount > 0,
            _ => false
        };
        if (!cardinalityValid)
        {
            return Diagnostic(
                "selection_cardinality_invalid",
                "The operation status and selected or candidate catalog ID counts are inconsistent.",
                "status",
                "catalog_ids",
                "candidate_catalog_ids");
        }
        if (status == "conditional" && !conditionalTopologyValid)
        {
            return Diagnostic(
                "conditional_topology_invalid",
                "The selected capabilities do not form the declared conditional activation topology.",
                "catalog_ids",
                "conditional_mode");
        }

        return Diagnostic("matching_shape_invalid", "The operation match violated its structured matching contract.", "operation_match");
    }

    internal static CapabilityMatchingShapeDiagnostic BuildInvalidConstraintMatchingDiagnostic(
        string status, bool deniedArrayValid, bool candidateArrayValid, bool deniedIdsKnown, bool candidateIdsKnown, bool reasonPresent, int deniedCount, int candidateCount, CapabilityInventoryConstraint constraint, bool normalizedNativePolicyOnly)
    {
        static CapabilityMatchingShapeDiagnostic Diagnostic(string code, string reason, params string[] fields)
            => new(code, reason, fields);

        if (status is not ("enforced" or "policy_only" or "ambiguous"))
            return Diagnostic("constraint_status_invalid", "The constraint match returned an unsupported status.", "status");
        if (!deniedArrayValid)
            return Diagnostic("denied_catalog_ids_invalid", "The constraint match returned malformed or excessive denied catalog IDs.", "denied_catalog_ids");
        if (!candidateArrayValid)
            return Diagnostic("candidate_catalog_ids_invalid", "The constraint match returned malformed or excessive candidate catalog IDs.", "candidate_catalog_ids");
        if (!deniedIdsKnown || !candidateIdsKnown)
        {
            return Diagnostic(
                "constraint_catalog_id_unknown",
                "The constraint match referenced one or more unknown or non-MCP catalog IDs.",
                !deniedIdsKnown ? "denied_catalog_ids" : "candidate_catalog_ids");
        }
        if (!reasonPresent)
            return Diagnostic("constraint_reason_missing", "The constraint match omitted its required bounded reason.", "reason");
        if (string.Equals(constraint.EnforcementKind, "exact_denial", StringComparison.Ordinal)
            && status == "policy_only"
            && !normalizedNativePolicyOnly)
        {
            return Diagnostic(
                "constraint_enforcement_kind_mismatch",
                "An exact-denial constraint cannot use policy_only; return enforced with exact denied MCP catalog IDs or ambiguous with concrete candidate MCP catalog IDs.",
                "status",
                "denied_catalog_ids",
                "candidate_catalog_ids");
        }

        var cardinalityValid = status switch
        {
            "enforced" => deniedCount > 0 && candidateCount == 0,
            "policy_only" => deniedCount == 0 && candidateCount == 0,
            "ambiguous" => deniedCount == 0 && candidateCount > 0,
            _ => false
        };
        return cardinalityValid
            ? Diagnostic("constraint_matching_shape_invalid", "The constraint match violated its structured matching contract.", "constraint_match")
            : Diagnostic(
                "constraint_selection_cardinality_invalid",
                "The constraint status and denied or candidate catalog ID counts are inconsistent.",
                "status",
                "denied_catalog_ids",
                "candidate_catalog_ids");
    }

    internal static bool RequiresCapabilityMatchingRepair(CapabilityMatchingEvaluation evaluation)
        => !evaluation.ContractValid || evaluation.Issues.Any(static issue => issue.Required);

    internal static CapabilityMatchingEvaluation PreserveValidCapabilityMatches(
        CapabilityMatchingEvaluation initial, CapabilityMatchingEvaluation repaired)
    {
        var dependencyUnlockedOperationIds = GetDependencyUnlockedDecisionOperationIds(initial);
        var lockedOperationIds = initial.OperationMatches
            .Where(static match => match.Status is "matched" or "composed" or "conditional" or "local")
            .Where(match => !dependencyUnlockedOperationIds.Contains(match.Operation.Id))
            .Select(static match => match.Operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        var lockedConstraintIds = initial.ConstraintMatches
            .Where(static match => match.Status is "enforced" or "policy_only")
            .Select(static match => match.Constraint.Id)
            .ToHashSet(StringComparer.Ordinal);
        var initialOperations = initial.OperationMatches.ToDictionary(static match => match.Operation.Id, StringComparer.Ordinal);
        var initialConstraints = initial.ConstraintMatches.ToDictionary(static match => match.Constraint.Id, StringComparer.Ordinal);
        var operations = repaired.OperationMatches
            .Select(match => lockedOperationIds.Contains(match.Operation.Id) ? initialOperations[match.Operation.Id] : match)
            .ToArray();
        var constraints = repaired.ConstraintMatches
            .Select(match => lockedConstraintIds.Contains(match.Constraint.Id) ? initialConstraints[match.Constraint.Id] : match)
            .ToArray();
        var issues = repaired.Issues
            .Where(issue => !lockedOperationIds.Contains(issue.OperationId) && !lockedConstraintIds.Contains(issue.OperationId))
            .ToArray();
        var mergedContractValid = operations.All(static match => match.Status != "invalid")
                                  && constraints.All(static match => match.Status != "invalid")
                                  && issues.All(static issue => issue.Status != "invalid");
        return new CapabilityMatchingEvaluation(operations, constraints, issues, mergedContractValid);
    }

    internal static HashSet<string> GetDependencyUnlockedDecisionOperationIds(
        CapabilityMatchingEvaluation evaluation)
    {
        var matches = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        var pending = new Stack<string>(evaluation.OperationMatches
            .Where(static match => string.Equals(match.Status, "invalid", StringComparison.Ordinal)
                                   && !string.IsNullOrWhiteSpace(match.DecisionGroundingFailureCode))
            // Include the declared dependency chain without changing successful unrelated matches.
            .SelectMany(static match => new[] { match.Operation.DecisionSourceOperationId, match.DecisionOperationId })
            .Where(static operationId => !string.IsNullOrWhiteSpace(operationId))!);
        var unlocked = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var operationId))
        {
            if (!unlocked.Add(operationId) || !matches.TryGetValue(operationId, out var match))
                continue;

            if (!string.IsNullOrWhiteSpace(match.Operation.DecisionSourceOperationId))
                pending.Push(match.Operation.DecisionSourceOperationId);
            foreach (var inputOperationId in match.Operation.InputOperationIds)
                pending.Push(inputOperationId);
        }

        return unlocked;
    }

    internal static void ThrowForUnresolvedCapabilityMatches(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog, bool repairAttempted)
    {
        var blocking = evaluation.Issues.Where(static issue => issue.Required).Take(64).ToArray();
        if (evaluation.ContractValid && blocking.Length == 0)
            return;

        if (blocking.Length == 0)
        {
            blocking = [new CapabilityMatchingIssue("matching_contract", "Capability matching contract", true, "invalid",
                "The matching response remained malformed after validation.", Array.Empty<string>())];
        }
        var entryMap = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var issueNodes = new JsonArray(blocking.Select(issue => (JsonNode)new JsonObject
        {
            ["operation_id"] = SanitizeCapabilityInferenceDiagnostic(issue.OperationId, 160),
            ["description"] = SanitizeCapabilityInferenceDiagnostic(issue.Description, 1_000),
            ["required"] = issue.Required,
            ["status"] = issue.Status,
            ["reason"] = SanitizeCapabilityInferenceDiagnostic(issue.Reason, 1_000),
            ["decision_operation_id"] = evaluation.OperationMatches.FirstOrDefault(match => match.Operation.Id == issue.OperationId)?.Operation.DecisionSourceOperationId,
            ["hint"] = issue.ReasonCode == "conditional_decision_source_unavailable"
                ? "Declare a runtime observation that supplies the decision data, then derive the condition from that result. A resource locator or materialized directory does not establish its contents. Preserve all requested decision outcomes."
                : null,
            ["validation_issue"] = issue.ValidationIssue.Length > 0 ? issue.ValidationIssue : null,
            ["reported_status"] = issue.ReportedStatus.Length > 0 ? issue.ReportedStatus : null,
            ["selected_catalog_id_count"] = issue.SelectedCatalogIdCount,
            ["candidate_catalog_id_count"] = issue.CandidateCatalogIdCount,
            ["invalid_fields"] = new JsonArray(issue.InvalidFields
                .Take(8)
                .Select(static field => (JsonNode?)JsonValue.Create(
                    SanitizeCapabilityInferenceDiagnostic(field, 80))).ToArray()),
            ["reason_code"] = issue.ReasonCode.Length > 0
                ? issue.ReasonCode
                : repairAttempted && issue.Status == "invalid"
                    ? "model_repair_exhausted"
                    : null,
            ["candidate_capabilities"] = new JsonArray(issue.CandidateCatalogIds
                .Where(entryMap.ContainsKey)
                .Take(8)
                .Select(id => (JsonNode)BuildCapabilityCandidateCard(entryMap[id])).ToArray())
        }).ToArray());
        var onlyUnavailable = blocking.All(static issue => issue.Status == "unavailable");
        var onlyContractGaps = blocking.All(static issue => issue.Status == "contract_gap");
        var onlyUnsupported = blocking.All(static issue => issue.Status is "unavailable" or "contract_gap");
        var containsInvalidContract = blocking.Any(static issue => issue.Status == "invalid");
        var unsupported = onlyUnsupported;
        var contractGapOperationIds = blocking
            .Where(static issue => issue.Status == "contract_gap")
            .Select(static issue => issue.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var unavailable = unsupported
            ? evaluation.OperationMatches.Where(match => match.Operation.Required
                                                         && (match.Status == "unavailable"
                                                             || contractGapOperationIds.Contains(match.Operation.Id)))
                .Select(static match => (JsonNode)new JsonObject
                {
                    ["id"] = match.Operation.Id,
                    ["description"] = match.Operation.Description,
                    ["required"] = true,
                    ["reason"] = match.Status == "unavailable"
                        ? "no_matching_discovered_capability"
                        : "conditional_decision_contract_gap",
                    ["grounding_failure_code"] = match.DecisionGroundingFailureCode
                }).ToArray()
            : Array.Empty<JsonNode>();
        throw new WorkflowRuntimeException(
            unsupported ? ErrorCodes.CapabilityPreflightUnavailable : ErrorCodes.CapabilityPreflightInferenceFailed,
            onlyContractGaps
                ? "One or more conditional runtime operations have no safe provider-neutral decision contract."
                : onlyUnavailable
                    ? "One or more required runtime operations have no matching discovered capability."
                    : unsupported
                        ? "One or more required runtime operations have no matching capability or safe provider-neutral decision contract."
                    : "Capability matching remained ambiguous or invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_matching",
                ["reason"] = onlyContractGaps ? "conditional_decision_contract_gap" : null,
                ["reason_code"] = repairAttempted ? "model_repair_exhausted" : null,
                ["classification"] = containsInvalidContract ? "model_contract_violation" : null,
                ["repair_attempted"] = repairAttempted,
                ["attempts"] = repairAttempted ? 2 : 1,
                ["clarification_rounds"] = 0,
                ["clarification_questions"] = 0,
                ["matching_issues"] = issueNodes,
                ["unavailable_capabilities"] = new JsonArray(unavailable),
                ["planning_outcome"] = unsupported ? "unsupported" : "cannot_plan_safely",
                ["recommended_action"] = onlyContractGaps
                    ? "configure_decision_contract_or_enable_structured_projection"
                    : onlyUnavailable
                        ? "configure_capability_or_revise_request"
                        : unsupported
                            ? "configure_capability_or_decision_contract_or_revise_request"
                        : containsInvalidContract
                            ? "retry_or_change_planning_model"
                            : "clarify_or_abandon"
            });
    }
}
