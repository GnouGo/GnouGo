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

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class PreparationTelemetry
{

    internal static string BuildPlannerTargetKey(string? provider, string model)
        => $"{provider?.Trim().ToLowerInvariant() ?? "(default)"}\n{model.Trim().ToLowerInvariant()}";

    internal static void RecordPlannerStructuredOutputProof(
        StepExecutionContext ctx, string? provider, string model, JsonNode? responseJson, JsonNode schema)
    {
        if (responseJson == null || PlanningContractValidation.ValidateInstance(responseJson, schema).Count != 0)
            return;

        PlannerStructuredOutputEvidenceByEngine
            .GetOrCreateValue(ctx.Engine)
            .Add(BuildPlannerTargetKey(provider, model));
    }

    internal static void AddUsageAttributes(TelemetrySpanScope span, JsonNode? usage, string model, string? provider)
    {
        if (usage is not JsonObject usageObject)
            return;

        var inputTokens = ReadUsageLong(usageObject, "prompt_tokens", "input_tokens");
        var outputTokens = ReadUsageLong(usageObject, "completion_tokens", "output_tokens");
        var totalTokens = ReadUsageLong(usageObject, "total_tokens", null);

        if (inputTokens.HasValue)
            span.SetAttribute("gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue)
            span.SetAttribute("gen_ai.usage.output_tokens", outputTokens.Value);
        if (totalTokens.HasValue)
            span.SetAttribute("gen_ai.usage.total_tokens", totalTokens.Value);

        var estimatedCost = EstimateUsageCost(
            span.ModelUsageCostEstimator,
            model,
            provider,
            inputTokens,
            outputTokens);
        if (estimatedCost.HasValue)
            span.SetAttribute("gen_ai.usage.cost", (double)estimatedCost.Value);
    }

    internal static decimal? EstimateUsageCost(
        IModelUsageCostEstimator? estimator, string model, string? provider, long? inputTokens, long? outputTokens)
    {
        if (inputTokens is null && outputTokens is null)
            return null;

        return estimator?.EstimateCost(model, inputTokens ?? 0, outputTokens ?? 0, provider);
    }

    internal static long? ReadUsageLong(JsonObject usageObject, string primaryKey, string? secondaryKey)
    {
        if (usageObject.TryGetPropertyValue(primaryKey, out var primary) && primary != null)
            return CoerceLong(primary);
        if (secondaryKey != null && usageObject.TryGetPropertyValue(secondaryKey, out var secondary) && secondary != null)
            return CoerceLong(secondary);
        return null;
    }

    internal static long? CoerceLong(JsonNode value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out var parsedLong))
                return parsedLong;
            if (jsonValue.TryGetValue<int>(out var parsedInt))
                return parsedInt;
            if (jsonValue.TryGetValue<double>(out var parsedDouble))
                return (long)parsedDouble;
            if (jsonValue.TryGetValue<string>(out var parsedString)
                && long.TryParse(parsedString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedText))
                return parsedText;
        }

        return null;
    }

    internal static void RecordCapabilityInventoryContractTelemetry(
        TelemetrySpanScope span, string stage, IReadOnlyList<CapabilityInventoryContractIssue> issues)
    {
        span.SetAttribute($"gnougo-flow.plan.capability_inventory.{stage}_contract_issue_count", issues.Count);
        span.SetAttribute(
            $"gnougo-flow.plan.capability_inventory.{stage}_contract_issue_codes",
            string.Join(',', issues.Select(static issue => issue.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)));
        foreach (var issue in issues)
        {
            span.AddEvent("gnougo-flow.plan.capability_inventory.contract_issue", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.code", issue.Code),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.operation_id", issue.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.field", issue.Field),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.index", issue.Index),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.source_id", issue.SourceId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.evidence_id", issue.EvidenceId)
            });
        }
    }

    internal static void RecordConditionalGroundingTelemetry(
        ITelemetrySpan span, CapabilityMatchingEvaluation evaluation, string attempt)
    {
        var conditionalMatches = evaluation.OperationMatches
            .Where(static match => !string.IsNullOrWhiteSpace(match.DecisionOperationId)
                                   || !string.IsNullOrWhiteSpace(match.Operation.DecisionSourceOperationId))
            .ToArray();
        span.SetAttribute($"gnougo-flow.plan.capability_matching.{attempt}.conditional_count", conditionalMatches.Length);
        span.SetAttribute(
            $"gnougo-flow.plan.capability_matching.{attempt}.conditional_grounded_count",
            conditionalMatches.Count(static match => !string.IsNullOrWhiteSpace(match.DecisionOutputPath)));
        span.SetAttribute(
            $"gnougo-flow.plan.capability_matching.{attempt}.conditional_contract_gap_count",
            conditionalMatches.Count(static match => !string.IsNullOrWhiteSpace(match.DecisionGroundingFailureCode)));

        foreach (var match in conditionalMatches.Take(32))
        {
            var decisionOperationId = match.DecisionOperationId ?? match.Operation.DecisionSourceOperationId;
            var decisionMatch = evaluation.OperationMatches.FirstOrDefault(candidate => string.Equals(
                candidate.Operation.Id,
                decisionOperationId,
                StringComparison.Ordinal));
            span.AddEvent("gnougo-flow.plan.capability_matching.conditional_grounding", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", SanitizeCapabilityInferenceDiagnostic(match.Operation.Id, 160)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_operation_id", SanitizeCapabilityInferenceDiagnostic(decisionOperationId, 160)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_catalog_ids", string.Join(',', decisionMatch?.CatalogIds.Take(8) ?? Array.Empty<string>())),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.branch_catalog_ids", string.Join(',', match.CatalogIds.Take(8))),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_output_path", match.DecisionOutputPath ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.allowed_values", string.Join('|', match.DecisionAllowedValues ?? Array.Empty<string>())),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.no_effect_values", string.Join('|', match.DecisionNoEffectValues ?? Array.Empty<string>())),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.contract_source", match.DecisionContractSource ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.producer_catalog_id", match.DecisionProducerCatalogId ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.grounding_failure_code", match.DecisionGroundingFailureCode ?? string.Empty)
            });
        }
    }

    internal static void RecordCapabilityCoverageTelemetry(
        StepExecutionContext ctx, IReadOnlyList<CapabilityCoverageDiagnostic> diagnostics, string stage)
    {
        foreach (var diagnostic in diagnostics)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.capability_coverage.review", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.operation_id", diagnostic.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.status", diagnostic.Status),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.unsupported_requirement_id", diagnostic.UnsupportedRequirementId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.candidate_catalog_ids", string.Join(',', diagnostic.CandidateCatalogIds)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.evidence_qualified", diagnostic.EvidenceQualified)
            });
        }
    }

    internal static void RecordCapabilityCoverageContractTelemetry(
        TelemetrySpanScope span, string stage, IReadOnlyList<CapabilityCoverageContractIssue> issues)
    {
        span.SetAttribute($"gnougo-flow.plan.capability_coverage.{stage}_contract_issue_count", issues.Count);
        span.SetAttribute(
            $"gnougo-flow.plan.capability_coverage.{stage}_contract_issue_codes",
            string.Join(',', issues.Select(static issue => issue.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)));
        foreach (var issue in issues.Take(32))
        {
            span.AddEvent("gnougo-flow.plan.capability_coverage.contract_issue", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.code", issue.Code),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.operation_id", issue.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.field", issue.Field),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.index", issue.Index),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.catalog_id", issue.CatalogId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.requirement_id", issue.RequirementId)
            });
        }
    }

    internal static void RecordCapabilityMatchingNormalizationTelemetry(
        TelemetrySpanScope span, CapabilityMatchingEvaluation evaluation, string attempt)
    {
        var normalized = evaluation.OperationMatches
            .Where(static match => !string.IsNullOrWhiteSpace(match.NormalizationReasonCode)
                                   || !string.IsNullOrWhiteSpace(match.DecisionOutputPathNormalizationReasonCode))
            .ToArray();
        span.SetAttribute(
            $"gnougo-flow.plan.capability_matching.{attempt}.normalization_count",
            normalized.Sum(static match =>
                (string.IsNullOrWhiteSpace(match.NormalizationReasonCode) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(match.DecisionOutputPathNormalizationReasonCode) ? 0 : 1)));
        foreach (var match in normalized)
        {
            if (!string.IsNullOrWhiteSpace(match.NormalizationReasonCode))
            {
                AddCapabilityMatchingNormalizationTelemetryEvent(
                    span,
                    match,
                    attempt,
                    match.NormalizationReasonCode);
            }
            if (!string.IsNullOrWhiteSpace(match.DecisionOutputPathNormalizationReasonCode))
            {
                AddCapabilityMatchingNormalizationTelemetryEvent(
                    span,
                    match,
                    attempt,
                    match.DecisionOutputPathNormalizationReasonCode);
            }
        }
    }

    internal static void RecordCapabilityMatchingFailureTelemetry(
        TelemetrySpanScope span, CapabilityMatchingEvaluation evaluation, bool repairAttempted)
    {
        var blocking = evaluation.Issues.Where(static issue => issue.Required).ToArray();
        if (evaluation.ContractValid && blocking.Length == 0)
            return;
        span.SetAttribute("gnougo-flow.plan.capability_matching.repair_exhausted", repairAttempted);
        span.SetAttribute("gnougo-flow.plan.capability_matching.blocking_issue_count", blocking.Length);
        span.SetAttribute("gnougo-flow.plan.capability_matching.invalid_issue_count",
            blocking.Count(static issue => issue.Status == "invalid"));
        if (repairAttempted)
        {
            span.AddEvent("gnougo-flow.plan.capability_matching.failure", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", "model_repair_exhausted"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.repair_attempted", true),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.blocking_issue_count", blocking.Length)
            });
        }
        foreach (var issue in blocking.Where(static issue => issue.ReasonCode.Length > 0))
        {
            span.AddEvent("gnougo-flow.plan.capability_matching.failure", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", issue.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.status", issue.Status),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", issue.ReasonCode),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.repair_attempted", repairAttempted)
            });
        }
    }
}
