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

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityInventoryEvidence
{

    internal static IReadOnlyList<CapabilityEvidenceSource> BuildCapabilityEvidenceSources(string instruction, string context)
    {
        var sources = new List<CapabilityEvidenceSource>();
        if (!string.IsNullOrWhiteSpace(instruction)) sources.Add(new("user_request", "user_request", instruction));
        if (!string.IsNullOrWhiteSpace(context)) sources.Add(new("caller_context", "caller_context", context));
        return sources;
    }

    internal static string BuildCapabilityEvidenceSourcesJson(IReadOnlyList<CapabilityEvidenceSource> sources)
        => new JsonArray(sources.Select(static source => (JsonNode)new JsonObject
        {
            ["source_id"] = source.Id,
            ["source_kind"] = source.Kind,
            ["text"] = source.Text
        }).ToArray()).ToJsonString();

    internal static CapabilityEvidenceAnchor? ResolveCapabilityEvidenceReference(
        JsonNode? node, IReadOnlyDictionary<string, CapabilityEvidenceSource> sourcesById, string operationId, string field, int? index, bool allowEmpty, List<CapabilityInventoryContractIssue> issues)
    {
        if (node is not JsonObject evidence)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_shape_invalid",
                operationId,
                field,
                index));
            return null;
        }

        var sourceId = evidence["source_id"] is JsonValue sourceValue
                       && sourceValue.TryGetValue<string>(out var sourceText)
            ? sourceText.Trim()
            : string.Empty;
        var excerpt = evidence["excerpt"] is JsonValue excerptValue
                      && excerptValue.TryGetValue<string>(out var excerptText)
            ? excerptText
            : string.Empty;
        if (sourceId.Length == 0 && string.IsNullOrWhiteSpace(excerpt))
        {
            if (!allowEmpty)
            {
                issues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    operationId,
                    field,
                    index));
            }
            return null;
        }

        var rejectedEvidenceId = BuildCapabilityEvidenceId(sourceId, -1, 0, excerpt);
        if (sourceId.Length == 0 || string.IsNullOrWhiteSpace(excerpt))
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_missing",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }
        if (!sourcesById.TryGetValue(sourceId, out var source))
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "source_unknown",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }

        var canonicalExcerpt = CanonicalizeCapabilityEvidenceText(excerpt);
        if (canonicalExcerpt.Length == 0)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_missing",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }
        if (canonicalExcerpt.Length > CapabilityDescriptionMaxCharacters)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_limit_exceeded",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }

        var canonicalSource = CanonicalizeCapabilityEvidenceText(source.Text);
        var start = canonicalSource.IndexOf(canonicalExcerpt, StringComparison.Ordinal);
        if (start < 0)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "excerpt_not_found",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }

        return new CapabilityEvidenceAnchor(
            BuildCapabilityEvidenceId(sourceId, start, canonicalExcerpt.Length, canonicalExcerpt),
            sourceId,
            start,
            canonicalExcerpt.Length,
            canonicalExcerpt);
    }

    internal static string CanonicalizeCapabilityEvidenceText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var whitespacePending = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    internal static bool CapabilityEvidenceRangesOverlap(
        CapabilityEvidenceAnchor left, CapabilityEvidenceAnchor right)
        => string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
           && left.Start < right.Start + right.Length
           && right.Start < left.Start + left.Length;

    internal static string BuildCapabilityEvidenceId(
        string sourceId, int start, int length, string excerpt)
    {
        var canonical = sourceId + "\n" + start + "\n" + length + "\n"
                        + CanonicalizeCapabilityEvidenceText(excerpt);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return "evidence_" + hash[..24];
    }

    internal static CapabilityInventoryContractIssue NewCapabilityInventoryContractIssue(
        string code, string operationId, string field, int? index, string sourceId = "", string evidenceId = "")
        => new(code, operationId, field, index, sourceId, evidenceId);

    internal static IReadOnlyList<CapabilityInventoryContractIssue> GetCapabilityInventoryContractIssues(
        Exception exception)
        => exception is CapabilityInventoryContractException contractException
            ? contractException.Issues
            : [NewCapabilityInventoryContractIssue(
                "inventory_contract_invalid",
                string.Empty,
                "$",
                null,
                evidenceId: BuildCapabilityEvidenceId(
                    exception.GetType().Name,
                    -1,
                    0,
                    exception.Message))];

    internal static CapabilityInventory BuildInvalidCapabilityInventory(
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
        => new(
            false,
            Array.Empty<CapabilityInventoryOperation>(),
            Array.Empty<CapabilityInventoryConstraint>(),
            [new CapabilityInventoryIncompleteReason(
                "inventory_contract_invalid",
                issues.Count == 0
                    ? "The inventory violated its deterministic contract."
                    : $"The inventory violated its deterministic contract with {issues.Count} issue(s).")]);

    internal static string BuildCapabilityInventoryContractIssuesJson(
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
        => new JsonArray(issues.Select(static issue => (JsonNode)BuildCapabilityInventoryContractIssueJson(issue))
            .ToArray()).ToJsonString();

    internal static JsonObject BuildCapabilityInventoryContractIssueJson(
        CapabilityInventoryContractIssue issue)
        => new()
        {
            ["code"] = issue.Code,
            ["operation_id"] = issue.OperationId,
            ["field"] = issue.Field,
            ["index"] = issue.Index,
            ["source_id"] = issue.SourceId,
            ["evidence_id"] = issue.EvidenceId
        };

    internal static string BuildRejectedCapabilityInventoryCandidate(
        JsonObject? rejectedCandidate, IReadOnlyList<CapabilityInventoryContractIssue> issues)
    {
        if (rejectedCandidate is null)
            return "{}";
        var serialized = rejectedCandidate.ToJsonString();
        if (serialized.Length <= CapabilityInventoryRepairCandidateMaxCharacters)
            return serialized;

        var affectedOperationIds = issues
            .Select(static issue => issue.OperationId)
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var projected = new JsonObject
        {
            ["complete"] = rejectedCandidate["complete"]?.DeepClone(),
            ["external_write_confirmation_policy"] = rejectedCandidate["external_write_confirmation_policy"]?.DeepClone(),
            ["external_write_confirmation_evidence"] = rejectedCandidate["external_write_confirmation_evidence"]?.DeepClone(),
            ["incomplete_reasons"] = rejectedCandidate["incomplete_reasons"]?.DeepClone(),
            ["operations"] = new JsonArray((rejectedCandidate["operations"] as JsonArray)?
                .OfType<JsonObject>()
                .Where(operation => affectedOperationIds.Contains(
                    operation["id"]?.GetValue<string>()?.Trim() ?? string.Empty))
                .Select(static operation => (JsonNode)operation.DeepClone())
                .ToArray() ?? []),
            ["constraints_omitted"] = true
        };
        return projected.ToJsonString();
    }

    internal static void ThrowInvalidCapabilityInventoryContract(
        IReadOnlyList<CapabilityInventoryContractIssue> initialIssues, IReadOnlyList<CapabilityInventoryContractIssue> finalIssues, CapabilityInventory inventory)
    {
        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability inventory inference violated its deterministic evidence contract after one repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_inventory",
                ["classification"] = "model_contract_violation",
                ["repair_attempted"] = true,
                ["attempts"] = 2,
                ["initial_contract_issue_count"] = initialIssues.Count,
                ["contract_issues"] = new JsonArray(finalIssues
                    .Select(static issue => (JsonNode)BuildCapabilityInventoryContractIssueJson(issue))
                    .ToArray()),
                ["operation_count"] = inventory.Operations.Count,
                ["constraint_count"] = inventory.Constraints.Count,
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "retry_or_change_planning_model"
            });
    }

    internal static JsonObject BuildCapabilityEvidenceReferenceJson(CapabilityEvidenceAnchor? evidence)
        => new()
        {
            ["source_id"] = evidence?.SourceId ?? string.Empty,
            ["excerpt"] = evidence?.Excerpt ?? string.Empty
        };

    internal static string SanitizeCapabilityInferenceDiagnostic(string value, int limit)
        => WorkflowTelemetrySourceFormatter.Format(value, limit).Text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
}
