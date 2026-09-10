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

using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatching;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityInventoryValidation
{

    internal static bool IsInventoryClarificationEligible(CapabilityInventory inventory)
        => !inventory.Complete
           && inventory.IncompleteReasons.Count > 0
           && inventory.IncompleteReasons.All(static reason =>
               !string.Equals(reason.Id, "inventory_contract_invalid", StringComparison.Ordinal));

    internal static bool CapabilityProducesArtifactKind(CapabilityCatalogEntry entry, string kind)
        => entry.ArtifactContract != null
            ? entry.ArtifactContract.Produces.Any(artifact =>
                string.Equals(artifact.Kind, kind, StringComparison.Ordinal)
                && string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal))
            : false;

    internal static IReadOnlyList<CapabilityArtifactRequirement> GetRequiredArtifactRequirements(
        CapabilityCatalogEntry entry)
        => entry.ArtifactContract != null
            ? entry.ArtifactContract.Consumes
                .Where(static artifact => artifact.Required)
                .Select(static artifact => new CapabilityArtifactRequirement(
                    new CapabilitySchemaField(
                        artifact.Pointer,
                        "string",
                        $"Required MCP-declared artifact of kind {artifact.Kind}.",
                        Array.Empty<string>()),
                    artifact.Kind))
                .ToArray()
            : [];

    internal static JsonObject ParseStructuredObject(LLMResponse response, string phase)
        => response.Json as JsonObject ?? throw new InvalidOperationException($"Capability {phase} returned no structured object.");

    internal static HashSet<string> ResolveAllowedNativeStepTypes(StepExecutionContext ctx, JsonObject? input)
    {
        var available = ctx.Engine.Registry.RegisteredTypes.ToHashSet(StringComparer.Ordinal);
        var policy = input?["policy"] as JsonObject;
        if (policy?["allowed_step_types"] is JsonArray allowed)
            available.IntersectWith(allowed.Select(static node => node?.GetValue<string>() ?? string.Empty));
        if (policy?["denied_step_types"] is JsonArray denied)
            available.ExceptWith(denied.Select(static node => node?.GetValue<string>() ?? string.Empty));
        available.Remove("workflow.plan");
        available.Remove("workflow.execute");
        return available;
    }

    internal static CapabilityInventory ParseCapabilityInventory(
        JsonObject json, IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
    {
        if (!TryReadComplete(json, out var complete))
            throw new InvalidOperationException("Capability inventory is missing its completeness decision.");
        var operationNodes = json["operations"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing operations.");
        var constraintNodes = json["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing constraints.");
        var sourcesById = evidenceSources.ToDictionary(static source => source.Id, StringComparer.Ordinal);
        var contractIssues = new List<CapabilityInventoryContractIssue>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var operations = operationNodes.Select(node =>
        {
            var (id, description, required) = ParseInventoryItem(node, identifiers, "operation");
            var executionKind = (node as JsonObject)?["execution_kind"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                ?? "external_effect";
            if (executionKind is not ("external_effect" or "human_interaction" or "local_processing"))
                throw new InvalidOperationException($"Capability inventory operation '{id}' has invalid execution_kind '{executionKind}'.");
            var externalEffectKind = (node as JsonObject)?["external_effect_kind"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                     ?? (executionKind == "external_effect" ? "execute" : "none");
            if (externalEffectKind is not ("read" or "write" or "execute" or "lifecycle" or "none")
                || executionKind != "external_effect" && externalEffectKind != "none"
                || executionKind == "external_effect" && externalEffectKind == "none")
            {
                throw new InvalidOperationException($"Capability inventory operation '{id}' has incompatible external_effect_kind '{externalEffectKind}'.");
            }
            var decisionSourceOperationId = (node as JsonObject)?["decision_source_operation_id"]?.GetValue<string>()?.Trim()
                                            ?? string.Empty;
            if (decisionSourceOperationId.Length > 160)
                throw new InvalidOperationException($"Capability inventory operation '{id}' has an invalid decision_source_operation_id.");
            var inputOperationIds = ((node as JsonObject)?["input_operation_ids"] as JsonArray)?
                .Select(static input => input?.GetValue<string>()?.Trim() ?? string.Empty)
                .Where(static input => input.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            if (inputOperationIds.Any(static input => input.Length > 160))
                throw new InvalidOperationException($"Capability inventory operation '{id}' has an invalid input_operation_ids entry.");
            var hasCoverageRequirements = node is JsonObject operationObject
                                          && operationObject.ContainsKey("coverage_requirements");
            var coverageNodes = (node as JsonObject)?["coverage_requirements"] as JsonArray;
            var coverageEvidence = new List<CapabilityEvidenceAnchor>();
            var workflowStructureCoverageRequirementIds = new HashSet<string>(StringComparer.Ordinal);
            if (hasCoverageRequirements && coverageNodes is null)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_shape_invalid",
                    id,
                    "coverage_requirements",
                    null));
            }
            else if (coverageNodes is not null)
            {
                if (coverageNodes.Count > 8)
                {
                    contractIssues.Add(NewCapabilityInventoryContractIssue(
                        "evidence_limit_exceeded",
                        id,
                        "coverage_requirements",
                        null));
                }

                for (var evidenceIndex = 0; evidenceIndex < Math.Min(coverageNodes.Count, 8); evidenceIndex++)
                {
                    var enforcementKind = (coverageNodes[evidenceIndex] as JsonObject)?["enforcement_kind"]?
                        .GetValue<string>()?.Trim().ToLowerInvariant()
                        ?? CapabilityContractCoverageEnforcementKind;
                    if (enforcementKind is not (CapabilityContractCoverageEnforcementKind or WorkflowStructureCoverageEnforcementKind))
                    {
                        contractIssues.Add(NewCapabilityInventoryContractIssue(
                            "coverage_enforcement_kind_invalid",
                            id,
                            "coverage_requirements",
                            evidenceIndex));
                        continue;
                    }
                    var anchor = ResolveCapabilityEvidenceReference(
                        coverageNodes[evidenceIndex],
                        sourcesById,
                        id,
                        "coverage_requirements",
                        evidenceIndex,
                        allowEmpty: false,
                        contractIssues);
                    if (anchor is not null
                        && coverageEvidence.All(existing => !string.Equals(
                            existing.Id,
                            anchor.Id,
                            StringComparison.Ordinal)))
                    {
                        coverageEvidence.Add(anchor);
                        if (string.Equals(
                                enforcementKind,
                                WorkflowStructureCoverageEnforcementKind,
                                StringComparison.Ordinal))
                        {
                            workflowStructureCoverageRequirementIds.Add(anchor.Id);
                        }
                    }
                }
            }

            if (executionKind == "local_processing" && coverageEvidence.Count > 0)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_forbidden_for_local_operation",
                    id,
                    "coverage_requirements",
                    null));
                coverageEvidence.Clear();
            }
            else if (hasCoverageRequirements
                     && executionKind is "external_effect" or "human_interaction"
                     && coverageEvidence.Count == 0
                     && !contractIssues.Any(issue => string.Equals(issue.OperationId, id, StringComparison.Ordinal)
                                                     && string.Equals(issue.Field, "coverage_requirements", StringComparison.Ordinal)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    id,
                    "coverage_requirements",
                    null));
            }

            var optionalityEvidenceAnchor = (node as JsonObject)?.ContainsKey("optionality_evidence") == true
                ? ResolveCapabilityEvidenceReference(
                    (node as JsonObject)?["optionality_evidence"],
                    sourcesById,
                    id,
                    "optionality_evidence",
                    null,
                    allowEmpty: true,
                    contractIssues)
                : null;
            if (required && optionalityEvidenceAnchor is not null)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_forbidden",
                    id,
                    "optionality_evidence",
                    null,
                    optionalityEvidenceAnchor.SourceId,
                    optionalityEvidenceAnchor.Id));
                optionalityEvidenceAnchor = null;
            }
            else if (!required && optionalityEvidenceAnchor is null
                     && !contractIssues.Any(issue => string.Equals(issue.OperationId, id, StringComparison.Ordinal)
                                                     && string.Equals(issue.Field, "optionality_evidence", StringComparison.Ordinal)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    id,
                    "optionality_evidence",
                    null));
            }
            var optionalityEvidence = optionalityEvidenceAnchor?.Excerpt ?? string.Empty;
            var allowNoEffectOutcome = (node as JsonObject)?["allow_no_effect_outcome"]?.GetValue<bool>() ?? false;
            if (allowNoEffectOutcome && decisionSourceOperationId.Length == 0)
                throw new InvalidOperationException($"Capability inventory operation '{id}' cannot allow a no-effect outcome without a decision source.");
            var hasNoEffectOutcomeEvidence = node is JsonObject noEffectOperationObject
                                             && noEffectOperationObject.ContainsKey("no_effect_outcome_evidence");
            var noEffectOutcomeEvidenceAnchor = hasNoEffectOutcomeEvidence
                ? ResolveCapabilityEvidenceReference(
                    (node as JsonObject)?["no_effect_outcome_evidence"],
                    sourcesById,
                    id,
                    "no_effect_outcome_evidence",
                    null,
                    allowEmpty: true,
                    contractIssues)
                : null;
            if (allowNoEffectOutcome
                && hasNoEffectOutcomeEvidence
                && noEffectOutcomeEvidenceAnchor is null
                && !contractIssues.Any(issue => string.Equals(issue.OperationId, id, StringComparison.Ordinal)
                                                && string.Equals(issue.Field, "no_effect_outcome_evidence", StringComparison.Ordinal)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    id,
                    "no_effect_outcome_evidence",
                    null));
            }
            else if (!allowNoEffectOutcome && noEffectOutcomeEvidenceAnchor is not null)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_forbidden",
                    id,
                    "no_effect_outcome_evidence",
                    null,
                    noEffectOutcomeEvidenceAnchor.SourceId,
                    noEffectOutcomeEvidenceAnchor.Id));
                noEffectOutcomeEvidenceAnchor = null;
            }
            else if (allowNoEffectOutcome
                     && noEffectOutcomeEvidenceAnchor is not null
                     && !coverageEvidence.Any(requirement =>
                         workflowStructureCoverageRequirementIds.Contains(requirement.Id)
                         && CapabilityEvidenceRangesOverlap(requirement, noEffectOutcomeEvidenceAnchor)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "no_effect_evidence_not_workflow_structure",
                    id,
                    "no_effect_outcome_evidence",
                    null,
                    noEffectOutcomeEvidenceAnchor.SourceId,
                    noEffectOutcomeEvidenceAnchor.Id));
                noEffectOutcomeEvidenceAnchor = null;
            }
            var intentOrigin = (node as JsonObject)?["intent_origin"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                               ?? "requested_effect";
            if (intentOrigin is not ("requested_effect" or "derived_failure_handling"))
                throw new InvalidOperationException($"Capability inventory operation '{id}' has invalid intent_origin '{intentOrigin}'.");
            var derivationSourceOperationId = (node as JsonObject)?["derivation_source_operation_id"]?.GetValue<string>()?.Trim()
                                              ?? string.Empty;
            if (derivationSourceOperationId.Length > 160
                || intentOrigin == "requested_effect" && derivationSourceOperationId.Length > 0
                || intentOrigin == "derived_failure_handling" && derivationSourceOperationId.Length == 0)
            {
                throw new InvalidOperationException($"Capability inventory operation '{id}' has an incompatible derivation_source_operation_id.");
            }
            return new CapabilityInventoryOperation(
                id,
                description,
                required,
                executionKind,
                externalEffectKind,
                decisionSourceOperationId,
                intentOrigin,
                derivationSourceOperationId,
                allowNoEffectOutcome,
                optionalityEvidence)
            {
                InputOperationIds = inputOperationIds,
                CoverageRequirements = coverageEvidence.Select(static evidence => evidence.Excerpt).ToArray(),
                CoverageRequirementEvidence = coverageEvidence,
                WorkflowStructureCoverageRequirementIds = workflowStructureCoverageRequirementIds,
                OptionalityEvidenceAnchor = optionalityEvidenceAnchor,
                NoEffectOutcomeEvidenceAnchor = noEffectOutcomeEvidenceAnchor
            };
        }).ToArray();
        var constraints = constraintNodes.Select(node =>
        {
            var (id, description, required) = ParseInventoryItem(node, identifiers, "constraint");
            var enforcementKind = (node as JsonObject)?["enforcement_kind"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                  ?? "exact_denial";
            if (enforcementKind is not ("exact_denial" or "workflow_policy"))
                throw new InvalidOperationException($"Capability inventory constraint '{id}' has invalid enforcement_kind '{enforcementKind}'.");
            return new CapabilityInventoryConstraint(id, description, required, enforcementKind);
        }).ToArray();
        var operationIndexes = operations
            .Select(static (operation, index) => (operation.Id, Index: index))
            .ToDictionary(static item => item.Id, static item => item.Index, StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (operation.DecisionSourceOperationId.Length > 0
                && (string.Equals(operation.Id, operation.DecisionSourceOperationId, StringComparison.Ordinal)
                || operations.All(candidate => !string.Equals(
                    candidate.Id,
                    operation.DecisionSourceOperationId,
                    StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    $"Capability inventory operation '{operation.Id}' references an unknown or self decision source '{operation.DecisionSourceOperationId}'.");
            }
            if (operation.DerivationSourceOperationId.Length > 0
                && (string.Equals(operation.Id, operation.DerivationSourceOperationId, StringComparison.Ordinal)
                    || operations.All(candidate => !string.Equals(
                        candidate.Id,
                        operation.DerivationSourceOperationId,
                        StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    $"Capability inventory operation '{operation.Id}' references an unknown or self derivation source '{operation.DerivationSourceOperationId}'.");
            }
            foreach (var inputOperationId in operation.InputOperationIds)
            {
                if (!operationIndexes.TryGetValue(inputOperationId, out var inputIndex)
                    || inputIndex >= operationIndexes[operation.Id])
                {
                    throw new InvalidOperationException(
                        $"Capability inventory operation '{operation.Id}' references an unknown, self, or later input operation '{inputOperationId}'.");
                }
            }
        }
        var confirmationPolicy = json["external_write_confirmation_policy"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                 ?? "unspecified";
        if (confirmationPolicy is not ("required" or "forbidden" or "unspecified"))
            throw new InvalidOperationException("Capability inventory has an invalid external-write confirmation policy contract.");
        var confirmationEvidenceAnchor = json.ContainsKey("external_write_confirmation_evidence")
            ? ResolveCapabilityEvidenceReference(
                json["external_write_confirmation_evidence"],
                sourcesById,
                string.Empty,
                "external_write_confirmation_evidence",
                null,
                allowEmpty: true,
                contractIssues)
            : null;
        if (confirmationPolicy == "unspecified" && confirmationEvidenceAnchor is not null)
        {
            contractIssues.Add(NewCapabilityInventoryContractIssue(
                "evidence_forbidden",
                string.Empty,
                "external_write_confirmation_evidence",
                null,
                confirmationEvidenceAnchor.SourceId,
                confirmationEvidenceAnchor.Id));
            confirmationEvidenceAnchor = null;
        }
        else if (confirmationPolicy != "unspecified" && confirmationEvidenceAnchor is null
                 && !contractIssues.Any(static issue => issue.OperationId.Length == 0
                                                        && issue.Field == "external_write_confirmation_evidence"))
            contractIssues.Add(NewCapabilityInventoryContractIssue(
                "evidence_missing",
                string.Empty,
                "external_write_confirmation_evidence",
                null));

        if (contractIssues.Count > 0)
            throw new CapabilityInventoryContractException(contractIssues);

        var confirmationEvidence = confirmationEvidenceAnchor?.Excerpt ?? string.Empty;

        var reasons = ParseCapabilityInventoryReasons(json["incomplete_reasons"] as JsonArray);
        if (complete && reasons.Count > 0)
            throw new InvalidOperationException("A complete capability inventory cannot contain incomplete reasons.");
        return new CapabilityInventory(
            complete,
            operations,
            constraints,
            reasons,
            confirmationPolicy,
            confirmationEvidence)
        {
            ExternalWriteConfirmationEvidenceAnchor = confirmationEvidenceAnchor
        };
    }

    internal static CapabilityInventory RemovePlannerBoundaryArtifacts(
        CapabilityInventory inventory, IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
    {
        // Typed planning uses validated source-addressed intent classifications.
        // English keyword filters can silently drop explicit requirements in other
        // languages. Preserve those effects and let contract matching fail closed.
        {
            var missing = inventory.Operations.Where(operation => operation.IntentOrigin == "requested_effect" && operation.ExecutionKind is "external_effect" or "human_interaction" && operation.CoverageRequirementEvidence.Count == 0).ToArray();
            if (missing.Length != 0) throw new InvalidOperationException("Typed capability inventory requires source-addressed evidence for every external or human operation: " + string.Join(", ", missing.Select(operation => operation.Id)));
            return inventory with { Operations = inventory.Operations.Where(operation => operation.IntentOrigin != "derived_failure_handling").ToArray() };
        }
    }

    internal static CapabilityInventory ApplyDefaultExternalWriteConfirmation(
        CapabilityInventory inventory)
    {
        if (!inventory.Complete
            || string.Equals(
                inventory.ExternalWriteConfirmationPolicy,
                "forbidden",
                StringComparison.Ordinal)
            || !inventory.Operations.Any(static operation => operation.ExecutionKind == "external_effect"
                && operation.ExternalEffectKind == "write"))
        {
            return inventory;
        }

        var identifiers = inventory.Operations.Select(static operation => operation.Id)
            .Concat(inventory.Constraints.Select(static constraint => constraint.Id))
            .ToHashSet(StringComparer.Ordinal);
        var operationId = CreateUniqueInventoryId("platform_confirm_external_write", identifiers);
        identifiers.Add(operationId);
        var constraintId = CreateUniqueInventoryId("platform_external_write_after_confirmation", identifiers);
        return inventory with
        {
            Operations = inventory.Operations.Concat([
                new CapabilityInventoryOperation(
                    operationId,
                    PlatformExternalWriteConfirmationOperationDescription,
                    true,
                    "human_interaction",
                    "none")
            ]).ToArray(),
            Constraints = inventory.Constraints.Concat([
                new CapabilityInventoryConstraint(
                    constraintId,
                    PlatformExternalWriteConfirmationConstraintDescription,
                    true,
                    "workflow_policy")
            ]).ToArray()
        };
    }

    internal static (string Policy, string Source) ResolveEffectiveExternalWriteConfirmationPolicy(
        CapabilityInventory inventory, IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
    {
        if (inventory.ExternalWriteConfirmationPolicy is "required" or "forbidden")
        {
            var sourceKind = inventory.ExternalWriteConfirmationEvidenceAnchor is { } anchor
                ? evidenceSources.FirstOrDefault(source => string.Equals(source.Id, anchor.SourceId, StringComparison.Ordinal))?.Kind
                : null;
            return (inventory.ExternalWriteConfirmationPolicy, sourceKind switch
            {
                "clarification" => "clarification",
                "caller_context" => "caller",
                "user_request" => "explicit_request",
                _ => "validated_evidence"
            });
        }

        var hasExternalWrite = inventory.Operations.Any(static operation =>
            operation.ExecutionKind == "external_effect" && operation.ExternalEffectKind == "write");
        return hasExternalWrite ? ("required", "platform_default") : ("unspecified", "none");
    }

    internal static (string Policy, string Source) ResolveEffectiveExternalWriteConfirmationPolicy(IReadOnlyList<ResolvedCapability> capabilities, string mode)
        => capabilities.Any(c => c.Required && c.Resolution == "native" && c.Method == "human.input") ? ("required", "locked_contract") : ("unspecified", "none");

    internal static string CreateUniqueInventoryId(string preferred, IReadOnlySet<string> identifiers)
    {
        if (!identifiers.Contains(preferred))
            return preferred;
        for (var suffix = 2; suffix < 1_000; suffix++)
        {
            var candidate = $"{preferred}_{suffix}";
            if (!identifiers.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Capability inventory contains too many colliding platform policy identifiers.");
    }

    internal static (string Id, string Description, bool Required) ParseInventoryItem(
        JsonNode? node, HashSet<string> identifiers, string kind)
    {
        if (node is not JsonObject item)
            throw new InvalidOperationException($"Capability inventory {kind} must be an object.");
        var id = item["id"]?.GetValue<string>()?.Trim();
        var description = item["description"]?.GetValue<string>()?.Trim();
        var required = item["required"]?.GetValue<bool>() ?? true;
        if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id) || string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Capability inventory ids must be unique and descriptions must be non-empty.");
        return (id, description, required);
    }

    internal static IReadOnlyList<CapabilityInventoryIncompleteReason> ParseCapabilityInventoryReasons(JsonArray? nodes)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<CapabilityInventoryIncompleteReason>();
        if (nodes.Count > 16)
            throw new InvalidOperationException("Capability inventory returned too many incomplete reasons.");

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new List<CapabilityInventoryIncompleteReason>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not JsonObject item)
                throw new InvalidOperationException("Capability inventory incomplete reasons must be objects.");
            var id = item["id"]?.GetValue<string>()?.Trim();
            var description = item["description"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 || !identifiers.Add(id)
                || string.IsNullOrWhiteSpace(description) || description.Length > 1_000)
                throw new InvalidOperationException("Capability inventory incomplete reasons must have unique bounded ids and non-empty bounded descriptions.");
            reasons.Add(new CapabilityInventoryIncompleteReason(id, description));
        }
        return reasons;
    }

    internal static string BuildCapabilityInventoryJson(CapabilityInventory inventory)
    {
        var operations = new JsonArray(inventory.Operations.Select(static operation => (JsonNode)new JsonObject
        {
            ["id"] = operation.Id,
            ["description"] = operation.Description,
            ["required"] = operation.Required,
            ["execution_kind"] = operation.ExecutionKind,
            ["external_effect_kind"] = operation.ExternalEffectKind,
            ["input_operation_ids"] = new JsonArray(operation.InputOperationIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["coverage_requirements"] = new JsonArray(operation.CoverageRequirementEvidence
                .Select(evidence =>
                {
                    var reference = BuildCapabilityEvidenceReferenceJson(evidence);
                    reference["enforcement_kind"] = operation.WorkflowStructureCoverageRequirementIds.Contains(evidence.Id)
                        ? WorkflowStructureCoverageEnforcementKind
                        : CapabilityContractCoverageEnforcementKind;
                    return (JsonNode)reference;
                }).ToArray()),
            ["optionality_evidence"] = BuildCapabilityEvidenceReferenceJson(operation.OptionalityEvidenceAnchor),
            ["decision_source_operation_id"] = operation.DecisionSourceOperationId,
            ["allow_no_effect_outcome"] = operation.AllowNoEffectOutcome,
            ["no_effect_outcome_evidence"] = BuildCapabilityEvidenceReferenceJson(
                operation.NoEffectOutcomeEvidenceAnchor),
            ["intent_origin"] = operation.IntentOrigin,
            ["derivation_source_operation_id"] = operation.DerivationSourceOperationId
        }).ToArray());
        var constraints = new JsonArray(inventory.Constraints.Select(static constraint => (JsonNode)new JsonObject
        {
            ["id"] = constraint.Id,
            ["description"] = constraint.Description,
            ["required"] = constraint.Required,
            ["enforcement_kind"] = constraint.EnforcementKind
        }).ToArray());
        var reasons = new JsonArray(inventory.IncompleteReasons.Select(static reason => (JsonNode)new JsonObject
        {
            ["id"] = reason.Id,
            ["description"] = reason.Description
        }).ToArray());
        return new JsonObject
        {
            ["complete"] = inventory.Complete,
            ["external_write_confirmation_policy"] = inventory.ExternalWriteConfirmationPolicy,
            ["external_write_confirmation_evidence"] = BuildCapabilityEvidenceReferenceJson(
                inventory.ExternalWriteConfirmationEvidenceAnchor),
            ["incomplete_reasons"] = reasons,
            ["operations"] = operations,
            ["constraints"] = constraints
        }.ToJsonString();
    }

    internal static void ThrowIncompleteCapabilityInventory(CapabilityInventory inventory)
    {
        IReadOnlyList<CapabilityInventoryIncompleteReason> reasons = inventory.IncompleteReasons.Count > 0
            ? inventory.IncompleteReasons
            : [new CapabilityInventoryIncompleteReason(
                "inventory_uncertain",
                "The inventory remained incomplete, but the inference model did not identify a specific user clarification.")];
        var reasonArray = new JsonArray(reasons.Select(static reason => (JsonNode)new JsonObject
        {
            ["id"] = SanitizeCapabilityInferenceDiagnostic(reason.Id, 160),
            ["description"] = SanitizeCapabilityInferenceDiagnostic(reason.Description, 1_000)
        }).ToArray());

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability inference could not produce a complete runtime operation inventory after one repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_inventory",
                ["repair_attempted"] = true,
                ["attempts"] = 2,
                ["operation_count"] = inventory.Operations.Count,
                ["constraint_count"] = inventory.Constraints.Count,
                ["incomplete_reasons"] = reasonArray,
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "clarify_or_abandon"
            });
    }

    internal static IReadOnlyList<CapabilityArtifactRequirement> GetRequiredArtifactRequirements(McpToolInfo tool)
    {
        var contract = GetValidatedMcpArtifactContract(tool);
        return contract != null
            ? contract.Consumes
                .Where(static artifact => artifact.Required)
                .Select(static artifact => new CapabilityArtifactRequirement(
                    new CapabilitySchemaField(
                        artifact.Pointer,
                        "string",
                        $"Required MCP-declared artifact of kind {artifact.Kind}.",
                        Array.Empty<string>()),
                    artifact.Kind))
                .ToArray()
            : [];
    }
}
