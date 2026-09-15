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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityDecisionGrounding;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityMatching
{

    internal static void AddCapabilityMatchingNormalizationTelemetryEvent(
        TelemetrySpanScope span, CapabilityOperationMatch match, string attempt, string reasonCode)
    {
        if (string.Equals(
                reasonCode,
                "conditional_local_decision_contract_synthesized",
                StringComparison.Ordinal))
        {
            span.AddEvent("gnougo-flow.plan.capability_matching.normalization", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", match.Operation.Id),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", reasonCode),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_operation_id", match.DecisionOperationId ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.contract_source", LocalDecisionContractSource)
            });
            return;
        }

        span.AddEvent("gnougo-flow.plan.capability_matching.normalization", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.attempt", attempt),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", match.Operation.Id),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", reasonCode),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.selected_count", match.CatalogIds.Count),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.selected_catalog_ids", string.Join(',', match.CatalogIds.Take(8))),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_operation_id", match.DecisionOperationId ?? string.Empty),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.producer_catalog_id", match.DecisionProducerCatalogId ?? string.Empty),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.contract_source", match.DecisionContractSource ?? string.Empty)
        });
    }

    internal static CapabilityClarificationConfig ParseCapabilityClarificationConfig(JsonObject? preflight)
    {
        if (preflight?["clarification"] is null)
            return new CapabilityClarificationConfig(false, HumanInputContract.DefaultTimeoutMs);
        if (preflight["clarification"] is not JsonObject clarification)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight.clarification must be an object.");
        }

        var enabled = clarification["enabled"] switch
        {
            null => false,
            JsonValue value when value.TryGetValue<bool>(out var parsed) => parsed,
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight.clarification.enabled must be a boolean.")
        };
        var timeoutMs = clarification["timeout_ms"] switch
        {
            null => HumanInputContract.DefaultTimeoutMs,
            JsonValue value when value.TryGetValue<int>(out var parsed) && parsed > 0 => parsed,
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight.clarification.timeout_ms must be a positive 32-bit integer.")
        };
        return new CapabilityClarificationConfig(enabled, timeoutMs);
    }

    internal static CapabilityMatchingEvaluation GroundConditionalCapabilityMatches(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var issues = evaluation.Issues.ToList();
        var matches = evaluation.OperationMatches.Select(match =>
        {
            if (!string.Equals(match.Status, "conditional", StringComparison.Ordinal))
                return match;
            var declaredDecisionOperationId = match.DecisionOperationId
                                              ?? match.Operation.DecisionSourceOperationId;

            if (TryGroundConditionalDecision(
                    evaluation,
                    match,
                    entries,
                    out var decisionOutputPath,
                    out var decisionAllowedValues,
                    out var decisionNoEffectValues,
                    out var decisionContractSource,
                    out var decisionProducerCatalogId,
                    out var decisionProducerOperationId,
                    out var decisionGroundingFailureCode))
            {
                return match with
                {
                    DecisionOperationId = decisionProducerOperationId,
                    DecisionOutputPath = decisionOutputPath,
                    DecisionAllowedValues = decisionAllowedValues,
                    DecisionNoEffectValues = decisionNoEffectValues,
                    DecisionContractSource = decisionContractSource,
                    DecisionProducerCatalogId = decisionProducerCatalogId,
                    DecisionGroundingFailureCode = null,
                    NormalizationReasonCode = string.Equals(
                        decisionContractSource,
                        LocalDecisionContractSource,
                        StringComparison.Ordinal)
                        ? "conditional_local_decision_contract_synthesized"
                        : string.Equals(
                            decisionProducerOperationId,
                            declaredDecisionOperationId,
                            StringComparison.Ordinal)
                            ? match.NormalizationReasonCode
                            : "conditional_decision_source_canonicalized"
                };
            }

            if (string.Equals(match.Operation.ExternalEffectKind, "read", StringComparison.Ordinal))
            {
                return match with
                {
                    Status = "composed",
                    Reason = "No provider-neutral enum output proves that the selected read variants are mutually exclusive, so every selected read remains an unconditional required call.",
                    DecisionOperationId = null,
                    DecisionOutputPath = null,
                    DecisionAllowedValues = null,
                    DecisionNoEffectValues = null,
                    DecisionContractSource = null,
                    DecisionProducerCatalogId = null,
                    DecisionGroundingFailureCode = decisionGroundingFailureCode
                };
            }

            var reason = "Conditional activation has no provider-neutral decision contract that covers every effect branch and declared no-effect outcome.";
            issues.Add(new CapabilityMatchingIssue(
                match.Operation.Id,
                match.Operation.Description,
                match.Operation.Required,
                "contract_gap",
                reason,
                match.CatalogIds)
            {
                ReasonCode = decisionGroundingFailureCode
            });
            return match with
            {
                Status = "invalid",
                Reason = reason,
                // A failed lookup has not proved that an ancestor can replace the
                // declared decision. Keep the original edge available to repair.
                DecisionOperationId = declaredDecisionOperationId,
                DecisionOutputPath = null,
                DecisionAllowedValues = null,
                DecisionNoEffectValues = null,
                DecisionContractSource = null,
                DecisionProducerCatalogId = null,
                DecisionGroundingFailureCode = decisionGroundingFailureCode
            };
        }).ToArray();

        return CanonicalizeSharedStructuredDecisionOutputPaths(evaluation with
        {
            OperationMatches = matches,
            Issues = issues,
            ContractValid = evaluation.ContractValid && matches.All(static match => match.Status != "invalid")
        });
    }

    internal static IReadOnlyList<string> GetMaterializedArtifactKinds(CapabilityCatalogEntry entry)
        => entry.ArtifactContract?.Produces
               .Where(static artifact => string.Equals(
                   artifact.Mode,
                   McpArtifactContractConventions.MaterializeMode,
                   StringComparison.Ordinal))
               .Select(static artifact => artifact.Kind)
               .Distinct(StringComparer.Ordinal)
               .ToArray()
           ?? Array.Empty<string>();

    internal static bool TryReadComplete(JsonObject json, out bool complete)
    {
        complete = false;
        return json["complete"] is JsonValue value && value.TryGetValue(out complete);
    }
}
