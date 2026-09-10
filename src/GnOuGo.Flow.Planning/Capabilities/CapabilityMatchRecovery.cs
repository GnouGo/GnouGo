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

    internal static bool HasRequiredCapabilityMatchingBlocker(CapabilityMatchingEvaluation evaluation)
        => evaluation.Issues.Any(static issue => issue.Required);

    internal static bool IsCapabilityDiscoveryNarrowed(
        IReadOnlyList<McpServerDiscovery> selected, IReadOnlyList<McpServerDiscovery> complete)
    {
        static HashSet<string> Identities(IReadOnlyList<McpServerDiscovery> servers)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in servers)
            {
                foreach (var tool in server.Tools)
                    identities.Add($"{server.Name}\u001ftool\u001f{tool.Name}");
                foreach (var prompt in server.Prompts)
                    identities.Add($"{server.Name}\u001fprompt\u001f{prompt.Name}");
            }
            return identities;
        }

        var selectedIdentities = Identities(selected);
        var completeIdentities = Identities(complete);
        return selectedIdentities.Count < completeIdentities.Count
               && selectedIdentities.IsSubsetOf(completeIdentities);
    }

    internal static CapabilityMatchingEvaluation RemapCapabilityMatchingCatalogIds(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog source, CapabilityCatalog destination)
    {
        static string Identity(CapabilityCatalogEntry entry)
            => string.Join(
                '\u001f',
                entry.Resolution,
                entry.Server ?? string.Empty,
                entry.Kind ?? string.Empty,
                entry.Method,
                CanonicalizeBindings(entry.RequestBindings));

        var sourceEntries = source.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var destinationIds = destination.Entries
            .GroupBy(Identity, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Id,
                StringComparer.Ordinal);
        string? RemapId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return id;
            return sourceEntries.TryGetValue(id, out var entry)
                   && destinationIds.TryGetValue(Identity(entry), out var destinationId)
                ? destinationId
                : id;
        }

        IReadOnlyList<string> RemapIds(IReadOnlyList<string> ids)
            => ids.Select(RemapId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        var operationMatches = evaluation.OperationMatches.Select(match => match with
        {
            CatalogIds = RemapIds(match.CatalogIds),
            CandidateCatalogIds = RemapIds(match.CandidateCatalogIds),
            DecisionProducerCatalogId = RemapId(match.DecisionProducerCatalogId)
        }).ToArray();
        var constraintMatches = evaluation.ConstraintMatches.Select(match => match with
        {
            DeniedCatalogIds = RemapIds(match.DeniedCatalogIds),
            CandidateCatalogIds = RemapIds(match.CandidateCatalogIds)
        }).ToArray();
        var issues = evaluation.Issues.Select(issue => issue with
        {
            CandidateCatalogIds = RemapIds(issue.CandidateCatalogIds)
        }).ToArray();
        return new CapabilityMatchingEvaluation(
            operationMatches,
            constraintMatches,
            issues,
            evaluation.ContractValid);
    }

    internal static HashSet<string> BuildCapabilityMatchingBlockerIdentities(
        CapabilityMatchingEvaluation evaluation)
        => evaluation.Issues
            .Where(static issue => issue.Required)
            .Select(static issue => string.Join(
                '\u001f',
                issue.OperationId,
                issue.Status,
                issue.ValidationIssue,
                issue.ReasonCode))
            .ToHashSet(StringComparer.Ordinal);

    internal static string BuildCapabilityMatchingFingerprint(CapabilityMatchingEvaluation evaluation)
    {
        var canonical = new StringBuilder();
        foreach (var match in evaluation.OperationMatches.OrderBy(static match => match.Operation.Id, StringComparer.Ordinal))
        {
            canonical.Append("operation\u001f")
                .Append(match.Operation.Id).Append('\u001f')
                .Append(match.Status).Append('\u001f')
                .Append(string.Join(',', match.CatalogIds.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(string.Join(',', match.CandidateCatalogIds.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(match.DecisionOperationId).Append('\u001f')
                .Append(match.ConditionalActivationMode).AppendLine();
        }
        foreach (var match in evaluation.ConstraintMatches.OrderBy(static match => match.Constraint.Id, StringComparer.Ordinal))
        {
            canonical.Append("constraint\u001f")
                .Append(match.Constraint.Id).Append('\u001f')
                .Append(match.Status).Append('\u001f')
                .Append(string.Join(',', match.DeniedCatalogIds.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(string.Join(',', match.CandidateCatalogIds.Order(StringComparer.Ordinal))).AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    internal static string BuildCapabilityInventoryFingerprint(CapabilityInventory inventory)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                BuildCapabilityInventoryJson(inventory))))
            .ToLowerInvariant();

    internal static bool TryGetInventoryRewindConstraintIds(
        CapabilityInventory inventory, CapabilityMatchingEvaluation evaluation, out IReadOnlySet<string> constraintIds)
    {
        var exactDenialIds = inventory.Constraints
            .Where(static constraint => constraint.Required
                                        && string.Equals(
                                            constraint.EnforcementKind,
                                            "exact_denial",
                                            StringComparison.Ordinal))
            .Select(static constraint => constraint.Id)
            .ToHashSet(StringComparer.Ordinal);
        constraintIds = evaluation.Issues
            .Where(static issue => issue.Required)
            .Select(static issue => issue.OperationId)
            .Where(exactDenialIds.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return constraintIds.Count > 0;
    }

    internal static bool InventoryRewindPreservesStableContracts(
        CapabilityInventory previous, CapabilityInventory candidate, IReadOnlySet<string> challengedConstraintIds)
    {
        if (!candidate.Complete
            || candidate.IncompleteReasons.Count != 0
            || !string.Equals(
                candidate.ExternalWriteConfirmationPolicy,
                previous.ExternalWriteConfirmationPolicy,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.ExternalWriteConfirmationEvidenceAnchor?.Id,
                previous.ExternalWriteConfirmationEvidenceAnchor?.Id,
                StringComparison.Ordinal)
            || previous.Operations.Count != candidate.Operations.Count
            || previous.Constraints.Count != candidate.Constraints.Count)
        {
            return false;
        }

        var previousWithoutConstraints = previous with
        {
            Constraints = Array.Empty<CapabilityInventoryConstraint>()
        };
        var candidateWithoutConstraints = candidate with
        {
            Constraints = Array.Empty<CapabilityInventoryConstraint>()
        };
        if (!string.Equals(
                BuildCapabilityInventoryJson(previousWithoutConstraints),
                BuildCapabilityInventoryJson(candidateWithoutConstraints),
                StringComparison.Ordinal))
        {
            return false;
        }

        var candidateConstraints = candidate.Constraints.ToDictionary(
            static constraint => constraint.Id,
            StringComparer.Ordinal);
        var changed = false;
        foreach (var constraint in previous.Constraints)
        {
            if (!candidateConstraints.TryGetValue(constraint.Id, out var candidateConstraint)
                || !string.Equals(constraint.Description, candidateConstraint.Description, StringComparison.Ordinal)
                || constraint.Required != candidateConstraint.Required)
            {
                return false;
            }

            if (!challengedConstraintIds.Contains(constraint.Id))
            {
                if (!string.Equals(
                        constraint.EnforcementKind,
                        candidateConstraint.EnforcementKind,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                continue;
            }

            if (!string.Equals(constraint.EnforcementKind, "exact_denial", StringComparison.Ordinal)
                || !string.Equals(
                    candidateConstraint.EnforcementKind,
                    "workflow_policy",
                    StringComparison.Ordinal))
            {
                return false;
            }
            changed = true;
        }

        return changed;
    }

    internal static CapabilityMatchingEvaluation MarkCapabilityMatchingRewindNonImproving(
        CapabilityMatchingEvaluation rewound, CapabilityMatchingEvaluation previous)
    {
        // The rejected response is not the retained candidate. Keep its findings
        // separate instead of replacing concrete blockers with a repair summary.
        return previous with
        {
            ContractValid = false,
            RejectedRewindIssues = rewound.Issues.Count > 0 ? rewound.Issues :
                [new CapabilityMatchingIssue("matching_rewind", "Expanded-catalog repair", true, "invalid",
                    "The expanded-catalog response did not establish a valid capability contract.", Array.Empty<string>())]
        };
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
            // Older failed candidates may contain an attempted canonical producer.
            // Unlock the declared chain as well, without changing successful matches.
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

    internal static string BuildCapabilityInventoryMatchingRewindPrompt(
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources, CapabilityInventory inventory, CapabilityMatchingEvaluation evaluation, IReadOnlySet<string> challengedConstraintIds)
    {
        var issues = new JsonArray(evaluation.Issues
            .Where(issue => challengedConstraintIds.Contains(issue.OperationId))
            .Select(issue => (JsonNode)new JsonObject
            {
                ["constraint_id"] = issue.OperationId,
                ["status"] = issue.Status,
                ["validation_issue"] = issue.ValidationIssue,
                ["reported_status"] = issue.ReportedStatus,
                ["selected_catalog_id_count"] = issue.SelectedCatalogIdCount,
                ["candidate_catalog_id_count"] = issue.CandidateCatalogIdCount,
                ["invalid_fields"] = BuildStringArrayJson(issue.InvalidFields.Take(8).ToArray())
            }).ToArray());
        return $$"""
            You are re-adjudicating one provider-neutral workflow capability inventory from its original evidence after exact-denial matching could not establish a valid contract. Return only the complete inventory JSON required by the supplied schema.

            Preserve every operation and constraint ID, description, required flag, evidence reference, dependency, confirmation policy, and all unrelated classifications exactly. Re-evaluate only enforcement_kind for the challenged constraint IDs. Change exact_denial to workflow_policy only when the original evidence describes a target-, input-, resource-, relationship-, data-, or branch-dependent restriction, or another invariant that must be enforced by workflow structure. Keep exact_denial when the evidence unconditionally prohibits an independently identifiable external capability throughout the document. Do not use provider, server, tool, method, catalog, URL, product, or domain names to decide. Do not add, remove, merge, split, rename, or reorder inventory entries.

            <previous_inventory>
            {{BuildCapabilityInventoryJson(inventory)}}
            </previous_inventory>
            <challenged_constraint_ids>
            {{BuildStringArrayJson(challengedConstraintIds.Order(StringComparer.Ordinal).ToArray()).ToJsonString()}}
            </challenged_constraint_ids>
            <matching_contract_issues>
            {{issues.ToJsonString()}}
            </matching_contract_issues>
            <evidence_sources>
            {{BuildCapabilityEvidenceSourcesJson(evidenceSources)}}
            </evidence_sources>
            """;
    }

    internal static string BuildCapabilityMatchingRepairPrompt(
        CapabilityInventory inventory, CapabilityCatalog catalog, CapabilityMatchingEvaluation previous)
    {
        var dependencyUnlockedOperationIds = GetDependencyUnlockedDecisionOperationIds(previous);
        var lockedOperations = previous.OperationMatches
            .Where(static match => match.Status is "matched" or "composed" or "conditional" or "local")
            .Where(match => !dependencyUnlockedOperationIds.Contains(match.Operation.Id))
            .Select(static match => (JsonNode)new JsonObject
            {
                ["operation_id"] = match.Operation.Id,
                ["status"] = match.Status,
                ["catalog_ids"] = new JsonArray(match.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["decision_operation_id"] = match.DecisionOperationId ?? string.Empty,
                ["conditional_mode"] = match.ConditionalActivationMode
            }).ToArray();
        var lockedConstraints = previous.ConstraintMatches
            .Where(static match => match.Status is "enforced" or "policy_only")
            .Select(static match => (JsonNode)new JsonObject
            {
                ["constraint_id"] = match.Constraint.Id,
                ["status"] = match.Status,
                ["denied_catalog_ids"] = new JsonArray(match.DeniedCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
            }).ToArray();
        var previousMatches = previous.OperationMatches.ToDictionary(static match => match.Operation.Id, StringComparer.Ordinal);
        var issues = previous.Issues.Select(issue => (JsonNode)new JsonObject
        {
            ["operation_id"] = issue.OperationId,
            ["status"] = issue.Status,
            ["reported_status"] = issue.ReportedStatus,
            ["reason"] = issue.Reason,
            ["validation_issue"] = issue.ValidationIssue,
            ["selected_catalog_id_count"] = issue.SelectedCatalogIdCount,
            ["candidate_catalog_id_count"] = issue.CandidateCatalogIdCount,
            ["invalid_fields"] = new JsonArray(issue.InvalidFields
                .Take(8)
                .Select(static field => (JsonNode?)JsonValue.Create(field)).ToArray()),
            ["decision_operation_id"] = previousMatches.TryGetValue(issue.OperationId, out var match)
                ? match.DecisionOperationId ?? match.Operation.DecisionSourceOperationId
                : string.Empty,
            ["grounding_failure_code"] = previousMatches.TryGetValue(issue.OperationId, out match)
                ? match.DecisionGroundingFailureCode ?? string.Empty
                : string.Empty,
            ["candidate_catalog_ids"] = new JsonArray(issue.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }).ToArray();
        return $$"""
            You are a domain-neutral capability matcher repairing a previous matching contract. Return only the requested structured JSON.

            {{(TypedConfirmationMatchingGuidance)}}

            Return every operation and constraint exactly once. Preserve all locked decisions exactly. A decision-source operation and every producer reached through its declared input_operation_ids are deliberately absent from the locked set when coupled to a reported conditional grounding issue: repair that declared chain together with the dependent conditional operation, selecting a better typed producer when the catalog provides one. Never infer an undeclared producer from descriptions or adjacency. Resolve each reported issue from the documented catalog only, and correct every listed validation_issue at its invalid_fields rather than repeating the reported_status. For operations use matched for one sufficient ID, composed for two or more necessary complementary IDs, conditional for either one mutually exclusive selector subset chosen by the locked decision_source_operation_id plus any necessary complementary unconditional prerequisites, or—only when allow_no_effect_outcome=true—one or more necessary capabilities that all execute in catalog_ids order for the single effect value; use local only for local_processing, ambiguous for unresolved user intent, and unavailable only when no sufficient implementation exists. Before retaining unavailable, scan every catalog row again, including selector-specific variants: a variant inherits its whole-tool description, arguments, outputs, artifacts, and composition contract, and workflow waiting, repetition, ordering, aggregation, and termination belong to workflow structure rather than capability sufficiency. For conditional, copy decision_source_operation_id exactly into decision_operation_id and set conditional_mode=exactly_one for selector alternatives or conditional_mode=all_on_value for the ordered effect composition; otherwise leave decision_operation_id and conditional_mode empty. Conditional selector variants share one physical capability and the same selector paths, differing on exactly one selector; prerequisites execute once outside the branch. An all_on_value conditional executes every selected capability in order inside its one effect branch and none in its no-effect branch. A runtime-dependent result is not user ambiguity. A complete_operation wrapper replaces its encapsulated phases. For constraints use enforced only when enforcement_kind=exact_denial and exact denied MCP IDs are established; use policy_only only when enforcement_kind=workflow_policy; use ambiguous only for unresolved exact-denial candidates. Select the smallest sufficient composition and never invent IDs.

            A repaired match must also be prerequisite-closed. Check required arguments and bounded output fields on every selected catalog card. If a capability requires an existing external artifact that is not a semantically compatible workflow runtime input or documented host-internal/default value, include the producer capability whose documented output supplies it. Local processing, URLs, identifiers, and invented strings do not create or prove workspaces, project roots, directories, files, handles, or exact comparison payloads. Ordinary scalar values and identifiers may still be parsed from declared inputs or reused from an already selected upstream read, so never repeat a read in every match merely to resupply them. Retain a complementary producer only for a documented artifact dependency or concrete multi-call prerequisite, and prefer the unique most-specific exact selector over its broader or partial selector entries. A high-level capability is sufficient alone only when its documented contract encapsulates those prerequisites.

            <locked_valid_operations>
            {{new JsonArray(lockedOperations).ToJsonString()}}
            </locked_valid_operations>
            <locked_valid_constraints>
            {{new JsonArray(lockedConstraints).ToJsonString()}}
            </locked_valid_constraints>
            <matching_issues>
            {{new JsonArray(issues).ToJsonString()}}
            </matching_issues>
            <runtime_inventory>
            {{BuildCapabilityInventoryJson(inventory)}}
            </runtime_inventory>
            <capability_catalog>
            {{catalog.Text}}
            </capability_catalog>
            """;
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
        var containsInvalidContract = blocking.Any(static issue => issue.Status == "invalid") || evaluation.RejectedRewindIssues.Count > 0;
        var unsupported = onlyUnsupported && evaluation.RejectedRewindIssues.Count == 0;
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
                ["rejected_matching_issues"] = new JsonArray(evaluation.RejectedRewindIssues.Take(64).Select(issue => (JsonNode)new JsonObject
                {
                    ["operation_id"] = issue.OperationId,
                    ["code"] = issue.ValidationIssue.Length > 0 ? issue.ValidationIssue : "CAPABILITY_REWIND_REJECTED",
                    ["reason"] = "Rejected expanded-catalog repair: " + SanitizeCapabilityInferenceDiagnostic(issue.Reason, 1_000),
                    ["required"] = false
                }).ToArray()),
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
