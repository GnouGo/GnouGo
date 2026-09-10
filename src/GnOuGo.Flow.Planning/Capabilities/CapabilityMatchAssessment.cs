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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchRecovery;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatching;
using static GnOuGo.Flow.Planning.Capabilities.CapabilitySelectionValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityMatchAssessment
{

    internal static JsonObject BuildCapabilityMatchingSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["operation_matches"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["operation_id"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("matched", "composed", "conditional", "local", "ambiguous", "unavailable") },
                        ["catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["candidate_catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["decision_operation_id"] = new JsonObject { ["type"] = "string" },
                        ["conditional_mode"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(string.Empty, ConditionalExactlyOneActivationMode, ConditionalAllOnValueActivationMode)
                        },
                        ["reason"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("operation_id", "status", "catalog_ids", "candidate_catalog_ids", "decision_operation_id", "conditional_mode", "reason"),
                    ["additionalProperties"] = false
                }
            },
            ["constraint_matches"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["constraint_id"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("enforced", "policy_only", "ambiguous") },
                        ["denied_catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["candidate_catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["reason"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("constraint_id", "status", "denied_catalog_ids", "candidate_catalog_ids", "reason"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("operation_matches", "constraint_matches"),
        ["additionalProperties"] = false
    };

    internal static CapabilityMatchingEvaluation ParseCapabilityMatchingEvaluation(
        JsonObject json, CapabilityInventory inventory, CapabilityCatalog catalog)
    {
        if (json["operation_matches"] is JsonObject keyedOperations)
        {
            json = json.DeepClone().AsObject();
            JsonArray Expand(JsonObject items, string field) => new(items.Select(p =>
            {
                var value = p.Value?.DeepClone() as JsonObject ?? new JsonObject(); value[field] = p.Key; return (JsonNode)value;
            }).ToArray());
            json["operation_matches"] = Expand(keyedOperations, "operation_id");
            if (json["constraint_matches"] is JsonObject keyedConstraints) json["constraint_matches"] = Expand(keyedConstraints, "constraint_id");
        }
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var operationIds = inventory.Operations.Select(static operation => operation.Id).ToArray();
        var constraintIds = inventory.Constraints.Select(static constraint => constraint.Id).ToArray();
        string ReadOperationId(JsonObject node)
            => ReadMatchingString(node, "operation_id");
        string ReadConstraintId(JsonObject node)
            => ReadMatchingString(node, "constraint_id");
        var issues = new List<CapabilityMatchingIssue>();
        var contractValid = true;
        var operationNodes = json["operation_matches"] as JsonArray;
        if (operationNodes == null)
        {
            operationNodes = new JsonArray();
            contractValid = false;
        }
        var operationObjects = operationNodes.OfType<JsonObject>().ToArray();
        if (operationObjects.Length != operationNodes.Count)
            contractValid = false;
        foreach (var unknown in operationObjects
                     .Select(ReadOperationId)
                     .Where(id => id.Length > 0 && inventory.Operations.All(operation => !string.Equals(operation.Id, id, StringComparison.Ordinal)))
                     .Distinct(StringComparer.Ordinal))
        {
            contractValid = false;
            issues.Add(new CapabilityMatchingIssue(unknown, "Unknown operation identifier.", true, "invalid",
                "The matching response referenced an operation that was not present in the locked inventory.", Array.Empty<string>()));
        }

        var operationMatches = new List<CapabilityOperationMatch>(inventory.Operations.Count);
        foreach (var operation in inventory.Operations)
        {
            var nodes = operationObjects.Where(node => string.Equals(ReadOperationId(node), operation.Id, StringComparison.Ordinal)).ToArray();
            if (nodes.Length != 1)
            {
                contractValid = false;
                var reason = nodes.Length == 0
                    ? "The matching response omitted this locked operation."
                    : "The matching response returned this locked operation more than once.";
                operationMatches.Add(new CapabilityOperationMatch(operation, "invalid", reason, Array.Empty<string>(), Array.Empty<string>()));
                issues.Add(new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, "invalid", reason, Array.Empty<string>())
                {
                    ValidationIssue = "operation_occurrence_invalid",
                    InvalidFields = ["operation_id"]
                });
                continue;
            }

            var node = nodes[0];
            var status = ReadMatchingString(node, "status").ToLowerInvariant();
            var selected = ReadMatchingIds(node["catalog_ids"], 32, out var selectedValid);
            var candidates = ReadMatchingIds(node["candidate_catalog_ids"], 8, out var candidatesValid);
            var reportedStatus = status;
            var reportedSelectedCount = selected.Count;
            var reportedCandidateCount = candidates.Count;
            var reportedSelectedValid = selectedValid;
            var reportedCandidatesValid = candidatesValid;
            var reportedSelectedIdsKnown = selected.All(entries.ContainsKey);
            var reportedCandidateIdsKnown = candidates.All(entries.ContainsKey);
            var decisionOperationId = ReadMatchingString(node, "decision_operation_id");
            var requestedConditionalMode = ReadMatchingString(node, "conditional_mode");
            var reasonText = SanitizeCapabilityInferenceDiagnostic(ReadMatchingString(node, "reason"), 1_000);
            var validStatus = status is "matched" or "composed" or "conditional" or "local" or "ambiguous" or "unavailable";
            string? normalizationReasonCode = null;

            // Structured model responses occasionally repeat advisory candidates in
            // catalog_ids, or place an explicit ambiguous set in catalog_ids. Preserve
            // the declared semantic status while normalizing that bounded field-placement
            // error; unknown IDs and every other malformed shape still fail closed.
            if (status == "matched" && selected.Count == 0 && candidates.Count == 1)
            {
                selected = candidates;
                candidates = Array.Empty<string>();
                selectedValid = candidatesValid;
                candidatesValid = true;
            }
            else if (selected.Count == 0
                     && (status == "composed" && candidates.Count >= 2
                         || status == "conditional" && candidates.Count >= 1))
            {
                selected = candidates;
                candidates = Array.Empty<string>();
                selectedValid = candidatesValid;
                candidatesValid = true;
            }
            else if (status is ("matched" or "composed" or "conditional") && selected.Count > 0)
            {
                candidates = Array.Empty<string>();
                candidatesValid = true;
            }
            else if (status == "ambiguous" && selected.Count > 0)
            {
                var mergedCandidates = selected.Concat(candidates)
                    .Distinct(StringComparer.Ordinal)
                    .Take(9)
                    .ToArray();
                if (mergedCandidates.Length <= 8)
                {
                    selected = Array.Empty<string>();
                    candidates = mergedCandidates;
                    selectedValid = true;
                    candidatesValid = true;
                }
            }

            var knownSelected = selected.All(entries.ContainsKey);
            var knownCandidates = candidates.All(entries.ContainsKey);
            if (status == "matched" && selected.Count == 0 && candidates.Count > 1 && knownCandidates)
            {
                var originalCandidates = candidates;
                candidates = RemoveStructurallyRedundantSelectorAncestorEntries(
                    candidates,
                    entries,
                    out var candidateSelectorCanonicalized);
                if (candidateSelectorCanonicalized)
                {
                    normalizationReasonCode = originalCandidates
                        .Except(candidates, StringComparer.Ordinal)
                        .Any(id => entries[id].RequestBindings.Count > 0)
                        ? "selector_ancestor_chain_canonicalized"
                        : "selector_base_variant_canonicalized";
                }
                if (candidates.Count == 1)
                {
                    selected = candidates;
                    candidates = Array.Empty<string>();
                    selectedValid = candidatesValid;
                    candidatesValid = true;
                    knownSelected = true;
                }
            }
            if (status == "matched" && knownSelected)
            {
                var originalSelected = selected;
                selected = RemoveStructurallyRedundantSelectorAncestorEntries(
                    selected,
                    entries,
                    out var selectorCanonicalized);
                if (selectorCanonicalized)
                {
                    normalizationReasonCode = originalSelected
                        .Except(selected, StringComparer.Ordinal)
                        .Any(id => entries[id].RequestBindings.Count > 0)
                        ? "selector_ancestor_chain_canonicalized"
                        : "selector_base_variant_canonicalized";
                }
            }
            if (status == "matched"
                && selected.Count > 1
                && knownSelected
                && IsDeclaredArtifactComposition(selected.Select(id => entries[id]).ToArray()))
            {
                status = "composed";
            }
            else if (status == "matched"
                     && selected.Count == 0
                     && candidates.Count > 1
                     && knownCandidates
                     && IsDeclaredArtifactComposition(candidates.Select(id => entries[id]).ToArray()))
            {
                selected = candidates;
                candidates = Array.Empty<string>();
                selectedValid = candidatesValid;
                candidatesValid = true;
                knownSelected = true;
                knownCandidates = true;
                status = "composed";
            }
            var shapeValid = validStatus
                             && reportedSelectedValid
                             && reportedCandidatesValid
                             && reportedSelectedIdsKnown
                             && reportedCandidateIdsKnown
                             && selectedValid
                             && candidatesValid
                             && knownSelected
                             && knownCandidates
                             && reasonText.Length > 0;
            var conditionalActivationMode = string.Empty;
            var conditionalTopologyValid = status == "conditional"
                                           && selected.All(entries.ContainsKey)
                                           && TryBuildConditionalActivation(
                                               selected.Select(id => entries[id]).ToArray(),
                                               operation.AllowNoEffectOutcome,
                                               requestedConditionalMode,
                                               out _,
                                               out conditionalActivationMode);
            shapeValid = shapeValid && status switch
            {
                "matched" => selected.Count == 1 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                "composed" => selected.Count >= 2 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                "conditional" => selected.Count >= 1
                                  && candidates.Count == 0
                                  && decisionOperationId.Length > 0
                                 && string.Equals(
                                     decisionOperationId,
                                     operation.DecisionSourceOperationId,
                                     StringComparison.Ordinal)
                                 && inventory.Operations.Any(candidate => string.Equals(candidate.Id, decisionOperationId, StringComparison.Ordinal))
                                  && conditionalTopologyValid,
                "local" => selected.Count == 0 && candidates.Count == 0 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0 && operation.ExecutionKind == "local_processing",
                "ambiguous" => selected.Count == 0 && candidates.Count > 0 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                "unavailable" => selected.Count == 0 && candidates.Count == 0 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                _ => false
            };
            if (operation.DecisionSourceOperationId.Length > 0 && status is "matched" or "composed") shapeValid = false;
            var humanRoutingInvalid = status == "conditional" && HasHumanDecisionSource(inventory, operation)
                && requestedConditionalMode != ConditionalAllOnValueActivationMode;
            if (humanRoutingInvalid) shapeValid = false;
            if (operation.ExecutionKind != "local_processing" && status == "local"
                || operation.ExecutionKind == "local_processing" && status != "local")
                shapeValid = false;

            if (!shapeValid)
            {
                contractValid = false;
                var diagnostic = humanRoutingInvalid
                    ? new CapabilityMatchingShapeDiagnostic("confirmation_activation_mode_invalid", TypedConfirmationMatchingGuidance, ["conditional_mode", "catalog_ids"])
                    : BuildInvalidMatchingDiagnostic(
                    reportedStatus,
                    reportedSelectedValid,
                    reportedCandidatesValid,
                    reportedSelectedIdsKnown,
                    reportedCandidateIdsKnown,
                    reasonText.Length > 0,
                    selected.Count,
                    candidates.Count,
                    decisionOperationId,
                    requestedConditionalMode,
                    conditionalTopologyValid,
                    operation);
                status = "invalid";
                reasonText = diagnostic.Reason;
                var issue = new CapabilityMatchingIssue(
                    operation.Id,
                    operation.Description,
                    operation.Required,
                    status,
                    reasonText,
                    selected.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray())
                {
                    ValidationIssue = diagnostic.Code,
                    ReportedStatus = reportedStatus,
                    SelectedCatalogIdCount = reportedSelectedCount,
                    CandidateCatalogIdCount = reportedCandidateCount,
                    InvalidFields = diagnostic.InvalidFields
                };
                issues.Add(issue);
            }
            else if (string.Equals(
                         conditionalActivationMode,
                         ConditionalAllOnValueActivationMode,
                         StringComparison.Ordinal))
            {
                normalizationReasonCode = "conditional_composition_canonicalized";
            }
            operationMatches.Add(new CapabilityOperationMatch(operation, status, reasonText, selected, candidates,
                decisionOperationId.Length > 0 ? decisionOperationId : null)
            {
                NormalizationReasonCode = normalizationReasonCode,
                ConditionalActivationMode = shapeValid ? conditionalActivationMode : requestedConditionalMode
            });
            if (status is "ambiguous" or "unavailable")
                issues.Add(new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, status, reasonText,
                    status == "ambiguous" ? candidates : selected.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray()));
        }

        var constraintNodes = json["constraint_matches"] as JsonArray;
        if (constraintNodes == null)
        {
            constraintNodes = new JsonArray();
            contractValid = false;
        }
        var constraintObjects = constraintNodes.OfType<JsonObject>().ToArray();
        if (constraintObjects.Length != constraintNodes.Count)
            contractValid = false;
        var constraintMatches = new List<CapabilityConstraintMatch>(inventory.Constraints.Count);
        foreach (var constraint in inventory.Constraints)
        {
            var nodes = constraintObjects.Where(node => string.Equals(ReadConstraintId(node), constraint.Id, StringComparison.Ordinal)).ToArray();
            if (nodes.Length != 1)
            {
                if (nodes.Length == 0
                    && string.Equals(constraint.EnforcementKind, "workflow_policy", StringComparison.Ordinal))
                {
                    constraintMatches.Add(new CapabilityConstraintMatch(
                        constraint,
                        "policy_only",
                        "The omitted match is normalized to policy_only because this locked conditional, ordering, or coverage rule cannot be represented as an unconditional exact capability denial.",
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                    continue;
                }

                contractValid = false;
                var reason = nodes.Length == 0
                    ? "The matching response omitted this locked constraint."
                    : "The matching response returned this locked constraint more than once.";
                constraintMatches.Add(new CapabilityConstraintMatch(constraint, "invalid", reason, Array.Empty<string>(), Array.Empty<string>()));
                issues.Add(new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, "invalid", reason, Array.Empty<string>()));
                continue;
            }

            var node = nodes[0];
            var status = ReadMatchingString(node, "status").ToLowerInvariant();
            var reportedStatus = status;
            var denied = ReadMatchingIds(node["denied_catalog_ids"], 64, out var deniedValid);
            var candidates = ReadMatchingIds(node["candidate_catalog_ids"], 8, out var candidatesValid);
            var reasonText = SanitizeCapabilityInferenceDiagnostic(ReadMatchingString(node, "reason"), 1_000);
            var referencedIds = denied.Concat(candidates).ToArray();
            var referencesOnlyKnownNativeCapabilities = deniedValid
                                                       && candidatesValid
                                                       && referencedIds.Length > 0
                                                       && referencedIds.All(id => entries.TryGetValue(id, out var entry)
                                                                                  && entry.Resolution == "native");
            var normalizedNativePolicyOnly = referencesOnlyKnownNativeCapabilities;
            if (normalizedNativePolicyOnly)
            {
                // Constraint denial contracts intentionally lock only exact MCP alternatives.
                // Native orchestration restrictions remain provider-neutral policy text even
                // when the inventory over-classified one as exact_denial. A native catalog ID
                // can never become an MCP denied alternative.
                status = "policy_only";
                denied = Array.Empty<string>();
                candidates = Array.Empty<string>();
                reasonText = "The constraint is preserved as an orchestration policy because native Flow steps are not exact denied MCP alternatives.";
            }
            if (string.Equals(constraint.EnforcementKind, "workflow_policy", StringComparison.Ordinal))
            {
                // Exact denied alternatives are unconditional document-wide bans. They cannot
                // represent a capability that is allowed after a gate, before a deadline, or
                // only under another condition without rejecting the valid guarded call too.
                status = "policy_only";
                denied = Array.Empty<string>();
                candidates = Array.Empty<string>();
                reasonText = "The constraint is preserved as a conditional or ordering policy because an exact denial would prohibit valid guarded use of the capability.";
            }
            var validStatus = status is "enforced" or "policy_only" or "ambiguous";
            var knownDenied = denied.All(id => entries.TryGetValue(id, out var entry) && entry.Resolution == "mcp");
            var knownCandidates = candidates.All(id => entries.TryGetValue(id, out var entry) && entry.Resolution == "mcp");
            var shapeValid = validStatus && deniedValid && candidatesValid && knownDenied && knownCandidates && reasonText.Length > 0;
            shapeValid = shapeValid && status switch
            {
                "enforced" => denied.Count > 0,
                "policy_only" => denied.Count == 0
                                 && candidates.Count == 0
                                 && (normalizedNativePolicyOnly || string.Equals(
                                     constraint.EnforcementKind,
                                     "workflow_policy",
                                     StringComparison.Ordinal)),
                "ambiguous" => denied.Count == 0 && candidates.Count > 0,
                _ => false
            };
            if (!shapeValid)
            {
                var diagnostic = BuildInvalidConstraintMatchingDiagnostic(
                    status,
                    deniedValid,
                    candidatesValid,
                    knownDenied,
                    knownCandidates,
                    reasonText.Length > 0,
                    denied.Count,
                    candidates.Count,
                    constraint,
                    normalizedNativePolicyOnly);
                contractValid = false;
                status = "invalid";
                reasonText = diagnostic.Reason;
                issues.Add(new CapabilityMatchingIssue(
                    constraint.Id,
                    constraint.Description,
                    constraint.Required,
                    status,
                    reasonText,
                    denied.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray())
                {
                    ValidationIssue = diagnostic.Code,
                    ReportedStatus = reportedStatus,
                    SelectedCatalogIdCount = denied.Count,
                    CandidateCatalogIdCount = candidates.Count,
                    InvalidFields = diagnostic.InvalidFields
                });
            }
            constraintMatches.Add(new CapabilityConstraintMatch(constraint, status, reasonText, denied, candidates));
            if (status == "ambiguous")
                issues.Add(new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, status, reasonText,
                    candidates));
        }

        var expectedConstraintIds = inventory.Constraints.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in constraintObjects.Select(ReadConstraintId)
                     .Where(id => id.Length > 0 && !expectedConstraintIds.Contains(id)).Distinct(StringComparer.Ordinal))
        {
            contractValid = false;
            issues.Add(new CapabilityMatchingIssue(unknown, "Unknown constraint identifier.", true, "invalid",
                "The matching response referenced a constraint that was not present in the locked inventory.", Array.Empty<string>()));
        }

        return GroundConditionalCapabilityMatches(
            new CapabilityMatchingEvaluation(operationMatches, constraintMatches, issues, contractValid),
            catalog);
    }

    internal static JsonObject BuildTypedCapabilityMatchingSchema(CapabilityInventory inventory, CapabilityCatalog catalog)
    {
        var schema = BuildCapabilityMatchingSchema();
        var operation = schema["properties"]!["operation_matches"]!["items"]!.DeepClone().AsObject();
        var constraint = schema["properties"]!["constraint_matches"]!["items"]!.DeepClone().AsObject();
        var ids = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(catalog.Entries.Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray()) };
        schema["$defs"] = new JsonObject { ["catalogId"] = ids };
        JsonObject Entry(JsonObject template, string idField)
        {
            var entry = template.DeepClone().AsObject(); entry["properties"]!.AsObject().Remove(idField);
            entry["required"] = new JsonArray(entry["required"]!.AsArray().Where(n => n?.ToString() != idField).Select(n => n!.DeepClone()).ToArray());
            foreach (var pair in entry["properties"]!.AsObject().Where(p => p.Key.EndsWith("catalog_ids", StringComparison.Ordinal)))
                pair.Value!["items"] = new JsonObject { ["$ref"] = "#/$defs/catalogId" };
            return entry;
        }
        static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
        static JsonObject Object(JsonObject properties) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
            ["additionalProperties"] = false
        };
        var operations = new JsonObject();
        foreach (var item in inventory.Operations)
        {
            var statuses = item.ExecutionKind == "local_processing" ? new[] { "local" }
                : item.DecisionSourceOperationId.Length == 0 ? new[] { "matched", "composed", "unavailable" } : new[] { "conditional", "unavailable" };
            var variants = new JsonArray();
            foreach (var status in statuses)
            {
                var entry = Entry(operation, "operation_id"); var properties = entry["properties"]!.AsObject();
                properties["status"] = Enum(status);
                properties["decision_operation_id"] = Enum(status == "conditional" ? item.DecisionSourceOperationId : "");
                properties["conditional_mode"] = status != "conditional" ? Enum("") : HasHumanDecisionSource(inventory, item) ? Enum("all_on_value")
                    : item.AllowNoEffectOutcome ? Enum("exactly_one", "all_on_value") : Enum("exactly_one");
                properties["candidate_catalog_ids"]!["maxItems"] = 0;
                var selected = properties["catalog_ids"]!;
                selected["minItems"] = status is "matched" or "conditional" ? 1 : status == "composed" ? 2 : 0;
                selected["maxItems"] = status == "matched" ? 1 : status is "composed" or "conditional" ? 16 : 0;
                variants.Add((JsonNode)entry);
            }
            operations[item.Id] = variants.Count == 1 ? variants[0]!.DeepClone() : new JsonObject { ["anyOf"] = variants };
        }

        schema["properties"]!["operation_matches"] = Object(operations);
        schema["properties"]!["constraint_matches"] = Object(new JsonObject(inventory.Constraints.Select(c =>
            new KeyValuePair<string, JsonNode?>(c.Id, Entry(constraint, "constraint_id")))));
        return schema;
    }

    internal static JsonObject TypedMatchingCandidate(CapabilityMatchingEvaluation evaluation) => new()
    {
        ["operation_matches"] = new JsonObject(evaluation.OperationMatches.Select(m => new KeyValuePair<string, JsonNode?>(m.Operation.Id, new JsonObject
        {
            ["status"] = m.Status,
            ["reason"] = m.Reason,
            ["catalog_ids"] = new JsonArray(m.CatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(m.CandidateCatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["decision_operation_id"] = m.DecisionOperationId ?? "",
            ["conditional_mode"] = m.ConditionalActivationMode
        }))),
        ["constraint_matches"] = new JsonObject(evaluation.ConstraintMatches.Select(m => new KeyValuePair<string, JsonNode?>(m.Constraint.Id, new JsonObject
        {
            ["status"] = m.Status,
            ["reason"] = m.Reason,
            ["denied_catalog_ids"] = new JsonArray(m.DeniedCatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(m.CandidateCatalogIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        })))
    };
}
