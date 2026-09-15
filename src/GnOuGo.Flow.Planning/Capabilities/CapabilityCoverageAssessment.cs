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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageContext;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityCoverageAssessment
{

    internal static CapabilityCoverageGapAdjudicationReview ParseCapabilityCoverageGapAdjudication(
        JsonObject json, CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, IReadOnlyList<CapabilityCoverageDiagnostic> gaps)
    {
        var issues = new List<CapabilityCoverageContractIssue>();
        var adjudications = new List<CapabilityCoverageGapAdjudication>();
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var targetsById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        var gapsByOperation = gaps.ToDictionary(static gap => gap.OperationId, StringComparer.Ordinal);
        if (json["adjudications"] is not JsonArray nodes)
        {
            return new CapabilityCoverageGapAdjudicationReview(
                adjudications,
                false,
                [new CapabilityCoverageContractIssue("adjudications_shape_invalid", string.Empty, "adjudications", null)]);
        }
        if (nodes.Count != gaps.Count)
            issues.Add(new CapabilityCoverageContractIssue("adjudication_count_mismatch", string.Empty, "adjudications", null));

        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is not JsonObject node)
            {
                issues.Add(new CapabilityCoverageContractIssue("adjudication_shape_invalid", string.Empty, "adjudications", index));
                continue;
            }

            var operationId = ReadCapabilityCoverageString(node, "operation_id");
            if (!gapsByOperation.TryGetValue(operationId, out var gap)
                || !targetsById.TryGetValue(operationId, out var target))
            {
                issues.Add(new CapabilityCoverageContractIssue("operation_unknown", operationId, "operation_id", index));
                continue;
            }
            if (adjudications.Any(item => string.Equals(item.OperationId, operationId, StringComparison.Ordinal)))
            {
                issues.Add(new CapabilityCoverageContractIssue("operation_duplicate", operationId, "operation_id", index));
                continue;
            }

            var itemIssues = new List<CapabilityCoverageContractIssue>();
            var requirementId = ReadCapabilityCoverageString(node, "requirement_id");
            if (!string.Equals(requirementId, gap.UnsupportedRequirementId, StringComparison.Ordinal))
                itemIssues.Add(new CapabilityCoverageContractIssue("requirement_id_mismatch", operationId, "requirement_id", index, RequirementId: requirementId));
            var classification = ReadCapabilityCoverageString(node, "classification").ToLowerInvariant();
            if (classification is not (IntrinsicPrimitiveMissingCoverageClassification or WorkflowStructureOnlyCoverageClassification))
                itemIssues.Add(new CapabilityCoverageContractIssue("classification_invalid", operationId, "classification", index));

            var structuralFacets = ReadCapabilityCoverageIds(
                node["structural_facets"],
                CapabilityCoverageStructuralFacets.Length,
                out var structuralFacetsValid);
            structuralFacetsValid &= structuralFacets.All(facet => CapabilityCoverageStructuralFacets.Contains(facet, StringComparer.Ordinal));
            structuralFacetsValid &= classification switch
            {
                WorkflowStructureOnlyCoverageClassification => structuralFacets.Count > 0,
                IntrinsicPrimitiveMissingCoverageClassification => structuralFacets.Count == 0,
                _ => false
            };
            if (!structuralFacetsValid)
                itemIssues.Add(new CapabilityCoverageContractIssue("structural_facets_invalid", operationId, "structural_facets", index));

            var catalogId = ReadCapabilityCoverageString(node, "catalog_id");
            var catalogExcerpt = CanonicalizeCapabilityEvidenceText(
                ReadCapabilityCoverageString(node, "catalog_excerpt"));
            var catalogKnown = entries.TryGetValue(catalogId, out var entry);
            var catalogSelected = target.CatalogIds.Contains(catalogId, StringComparer.Ordinal);
            var excerptGrounded = catalogKnown
                                  && catalogExcerpt.Length > 0
                                  && catalogExcerpt.Length <= CapabilityDescriptionMaxCharacters
                                  && CanonicalizeCapabilityEvidenceText(BuildCapabilityCoverageCard(entry!, catalog))
                                      .Contains(catalogExcerpt, StringComparison.Ordinal);
            if (!catalogKnown)
                itemIssues.Add(new CapabilityCoverageContractIssue("evidence_catalog_id_unknown", operationId, "catalog_id", index, catalogId, requirementId));
            else if (!catalogSelected)
                itemIssues.Add(new CapabilityCoverageContractIssue("evidence_catalog_id_not_selected", operationId, "catalog_id", index, catalogId, requirementId));
            if (!excerptGrounded)
                itemIssues.Add(new CapabilityCoverageContractIssue(
                    catalogExcerpt.Length == 0 ? "evidence_excerpt_missing" : "evidence_excerpt_not_found",
                    operationId,
                    "catalog_excerpt",
                    index,
                    catalogId,
                    requirementId));

            issues.AddRange(itemIssues);
            adjudications.Add(new CapabilityCoverageGapAdjudication(
                operationId,
                requirementId,
                classification,
                structuralFacets,
                catalogId,
                catalogExcerpt,
                itemIssues.Count == 0));
        }

        foreach (var gap in gaps.Where(gap => adjudications.All(item => !string.Equals(
                     item.OperationId,
                     gap.OperationId,
                     StringComparison.Ordinal))))
        {
            issues.Add(new CapabilityCoverageContractIssue("operation_missing", gap.OperationId, "operation_id", null));
        }

        return new CapabilityCoverageGapAdjudicationReview(
            adjudications,
            issues.Count == 0 && adjudications.Count == gaps.Count,
            issues);
    }

    internal static CapabilityCoverageReview ParseCapabilityCoverageReview(
        JsonObject json, CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var targetById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        if (json["diagnostics"] is not JsonArray nodes)
        {
            return new CapabilityCoverageReview(
                Array.Empty<CapabilityCoverageDiagnostic>(),
                false,
                [new CapabilityCoverageContractIssue(
                    "diagnostics_shape_invalid",
                    string.Empty,
                    "diagnostics",
                    null)]);
        }

        var diagnostics = new List<CapabilityCoverageDiagnostic>();
        var issues = new List<CapabilityCoverageContractIssue>();
        if (nodes.Count != targets.Count)
        {
            issues.Add(new CapabilityCoverageContractIssue(
                "diagnostic_count_mismatch",
                string.Empty,
                "diagnostics",
                null));
        }

        for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            if (nodes[nodeIndex] is not JsonObject node)
            {
                issues.Add(new CapabilityCoverageContractIssue(
                    "diagnostic_shape_invalid",
                    string.Empty,
                    "diagnostics",
                    nodeIndex));
                continue;
            }

            var operationId = ReadCapabilityCoverageString(node, "operation_id");
            if (!targetById.ContainsKey(operationId))
            {
                issues.Add(new CapabilityCoverageContractIssue(
                    "operation_unknown",
                    operationId,
                    "operation_id",
                    nodeIndex));
            }
        }

        foreach (var target in targets)
        {
            var matches = nodes
                .Select(static (node, index) => (Node: node as JsonObject, Index: index))
                .Where(item => item.Node is not null && string.Equals(
                    ReadCapabilityCoverageString(item.Node, "operation_id"),
                    target.Operation.Id,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new CapabilityCoverageContractIssue(
                    matches.Length == 0 ? "operation_missing" : "operation_duplicate",
                    target.Operation.Id,
                    "operation_id",
                    null));
                continue;
            }

            var node = matches[0].Node!;
            var diagnosticIndex = matches[0].Index;
            var diagnosticIssues = new List<CapabilityCoverageContractIssue>();
            var status = ReadCapabilityCoverageString(node, "status").ToLowerInvariant();
            if (status is not ("supported" or "incomplete"))
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "status_invalid",
                    target.Operation.Id,
                    "status",
                    diagnosticIndex));
            }
            var requirementsById = GetCapabilityContractCoverageRequirements(target.Operation)
                .ToDictionary(static requirement => requirement.Id, StringComparer.Ordinal);
            var unsupportedId = ReadCapabilityCoverageString(node, "unsupported_requirement_id");
            var unsupported = requirementsById.TryGetValue(unsupportedId, out var unsupportedRequirement)
                ? unsupportedRequirement.Excerpt
                : string.Empty;
            var weaker = CanonicalizeCapabilityEvidenceText(
                ReadCapabilityCoverageString(node, "supported_weaker_behavior"));
            if (weaker.Length > CapabilityDescriptionMaxCharacters)
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "weaker_behavior_limit_exceeded",
                    target.Operation.Id,
                    "supported_weaker_behavior",
                    diagnosticIndex));
            }

            var candidates = ReadCapabilityCoverageIds(
                node["candidate_catalog_ids"],
                8,
                out var candidatesValid);
            if (!candidatesValid)
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "candidate_catalog_ids_invalid",
                    target.Operation.Id,
                    "candidate_catalog_ids",
                    diagnosticIndex));
            }
            foreach (var candidate in candidates.Where(candidate => !entries.ContainsKey(candidate)))
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "candidate_catalog_id_unknown",
                    target.Operation.Id,
                    "candidate_catalog_ids",
                    diagnosticIndex,
                    candidate));
            }
            foreach (var candidate in candidates.Where(candidate =>
                         entries.ContainsKey(candidate)
                         && !target.CatalogIds.Contains(candidate, StringComparer.Ordinal)))
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "candidate_catalog_id_not_selected",
                    target.Operation.Id,
                    "candidate_catalog_ids",
                    diagnosticIndex,
                    candidate));
            }

            var evidence = new List<CapabilityCoverageEvidence>();
            var evidenceValid = true;
            if (node["evidence"] is JsonArray evidenceArray)
            {
                if (evidenceArray.Count == 0)
                {
                    evidenceValid = false;
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "evidence_missing",
                        target.Operation.Id,
                        "evidence",
                        diagnosticIndex));
                }

                for (var evidenceIndex = 0; evidenceIndex < evidenceArray.Count; evidenceIndex++)
                {
                    if (evidenceArray[evidenceIndex] is not JsonObject evidenceNode)
                    {
                        evidenceValid = false;
                        diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                            "evidence_shape_invalid",
                            target.Operation.Id,
                            "evidence",
                            evidenceIndex));
                        continue;
                    }

                    var catalogId = ReadCapabilityCoverageString(evidenceNode, "catalog_id");
                    var requirementId = ReadCapabilityCoverageString(evidenceNode, "requirement_id");
                    var catalogExcerpt = CanonicalizeCapabilityEvidenceText(
                        ReadCapabilityCoverageString(evidenceNode, "catalog_excerpt"));
                    var requirementValid = requirementsById.TryGetValue(requirementId, out var requirement);
                    var catalogKnown = entries.TryGetValue(catalogId, out var entry);
                    var catalogSelected = target.CatalogIds.Contains(catalogId, StringComparer.Ordinal);
                    var excerptPresent = catalogExcerpt.Length > 0;
                    var excerptGrounded = catalogKnown
                                          && excerptPresent
                                          && CanonicalizeCapabilityEvidenceText(
                                                  BuildCapabilityCoverageCard(entry!, catalog))
                                              .Contains(catalogExcerpt, StringComparison.Ordinal);
                    var valid = catalogKnown
                                && catalogSelected
                                && requirementValid
                                && excerptPresent
                                && catalogExcerpt.Length <= CapabilityDescriptionMaxCharacters
                                && excerptGrounded;
                    evidenceValid &= valid;
                    if (valid)
                    {
                        evidence.Add(new CapabilityCoverageEvidence(
                            catalogId,
                            requirementId,
                            requirementsById[requirementId].Excerpt,
                            catalogExcerpt));
                        continue;
                    }

                    var issueCode = !catalogKnown
                        ? "evidence_catalog_id_unknown"
                        : !catalogSelected
                            ? "evidence_catalog_id_not_selected"
                            : !requirementValid
                                ? "evidence_requirement_id_unknown"
                                : !excerptPresent
                                    ? "evidence_excerpt_missing"
                                    : catalogExcerpt.Length > CapabilityDescriptionMaxCharacters
                                        ? "evidence_excerpt_limit_exceeded"
                                        : "evidence_excerpt_not_found";
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        issueCode,
                        target.Operation.Id,
                        "evidence",
                        evidenceIndex,
                        catalogId,
                        requirementId));
                }
            }
            else
            {
                evidenceValid = false;
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "evidence_shape_invalid",
                    target.Operation.Id,
                    "evidence",
                    diagnosticIndex));
            }

            var coveredRequirements = evidence
                .Select(static item => item.RequirementId)
                .ToHashSet(StringComparer.Ordinal);
            var evidencedCatalogExcerpts = evidence
                .Select(static item => item.CatalogExcerpt)
                .ToHashSet(StringComparer.Ordinal);
            var weakerBehaviorGrounded = weaker.Length == 0
                                         || evidencedCatalogExcerpts.Contains(weaker);
            if (status == "supported")
            {
                if (unsupportedId.Length > 0)
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "unsupported_requirement_forbidden",
                        target.Operation.Id,
                        "unsupported_requirement_id",
                        diagnosticIndex,
                        RequirementId: unsupportedId));
                }
                if (weaker.Length > 0)
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "weaker_behavior_forbidden",
                        target.Operation.Id,
                        "supported_weaker_behavior",
                        diagnosticIndex));
                }
                foreach (var requirementId in requirementsById.Keys.Where(id => !coveredRequirements.Contains(id)))
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "requirement_not_evidenced",
                        target.Operation.Id,
                        "evidence",
                        diagnosticIndex,
                        RequirementId: requirementId));
                }
            }
            else if (status == "incomplete")
            {
                if (!requirementsById.ContainsKey(unsupportedId))
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "unsupported_requirement_id_unknown",
                        target.Operation.Id,
                        "unsupported_requirement_id",
                        diagnosticIndex,
                        RequirementId: unsupportedId));
                }
                if (weaker.Length > 0 && !weakerBehaviorGrounded)
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "weaker_behavior_not_evidenced",
                        target.Operation.Id,
                        "supported_weaker_behavior",
                        diagnosticIndex));
                }
                if (requirementsById.ContainsKey(unsupportedId)
                    && evidence.All(item => !string.Equals(
                        item.RequirementId,
                        unsupportedId,
                        StringComparison.Ordinal)))
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "unsupported_requirement_not_evidenced",
                        target.Operation.Id,
                        "evidence",
                        diagnosticIndex,
                        RequirementId: unsupportedId));
                }
            }

            var shapeValid = diagnosticIssues.Count == 0
                             && candidatesValid
                             && candidates.All(candidate =>
                                 entries.ContainsKey(candidate)
                                 && target.CatalogIds.Contains(candidate, StringComparer.Ordinal))
                             && evidenceValid;
            issues.AddRange(diagnosticIssues);
            diagnostics.Add(new CapabilityCoverageDiagnostic(
                target.Operation.Id,
                status,
                unsupportedId,
                unsupported,
                weaker,
                candidates,
                evidence,
                shapeValid));
        }

        return new CapabilityCoverageReview(
            diagnostics,
            issues.Count == 0 && diagnostics.Count == targets.Count,
            issues);
    }

    internal static string ReadCapabilityCoverageString(JsonObject node, string property)
        => node[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;

    internal static IReadOnlyList<string> ReadCapabilityCoverageIds(
        JsonNode? node, int maximum, out bool valid)
    {
        valid = node is JsonArray;
        if (node is not JsonArray array)
            return Array.Empty<string>();

        valid &= array.Count <= maximum;
        var result = new List<string>(Math.Min(array.Count, maximum));
        foreach (var item in array.Take(maximum))
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                valid = false;
                continue;
            }
            var id = text.Trim();
            if (!result.Contains(id, StringComparer.Ordinal))
                result.Add(id);
        }
        valid &= result.Count == array.Count;
        return result;
    }

    internal static string BuildCapabilityCoverageContractIssuesJson(
        IReadOnlyList<CapabilityCoverageContractIssue> issues)
        => new JsonArray(issues
            .Take(32)
            .Select(static issue => (JsonNode)BuildCapabilityCoverageContractIssueJson(issue))
            .ToArray()).ToJsonString();

    internal static JsonObject BuildCapabilityCoverageContractIssueJson(
        CapabilityCoverageContractIssue issue)
        => new()
        {
            ["code"] = issue.Code,
            ["operation_id"] = issue.OperationId,
            ["field"] = issue.Field,
            ["index"] = issue.Index,
            ["catalog_id"] = issue.CatalogId,
            ["requirement_id"] = issue.RequirementId
        };

    internal static string BuildRejectedCapabilityCoverageCandidate(
        JsonObject? rejectedCandidate, IReadOnlyList<CapabilityCoverageContractIssue> issues)
    {
        if (rejectedCandidate is null)
            return "{}";
        var serialized = rejectedCandidate.ToJsonString();
        if (serialized.Length <= CapabilityInventoryRepairCandidateMaxCharacters)
            return serialized;

        var affectedOperationIds = issues
            .Select(static issue => issue.OperationId)
            .Where(static operationId => operationId.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var collectionProperty = rejectedCandidate.ContainsKey("diagnostics")
            ? "diagnostics"
            : rejectedCandidate.ContainsKey("adjudications")
                ? "adjudications"
                : string.Empty;
        if (collectionProperty.Length == 0)
            return "{}";
        return new JsonObject
        {
            [collectionProperty] = new JsonArray((rejectedCandidate[collectionProperty] as JsonArray)?
                .OfType<JsonObject>()
                .Where(diagnostic => affectedOperationIds.Contains(
                    ReadCapabilityCoverageString(diagnostic, "operation_id")))
                .Take(32)
                .Select(static diagnostic => (JsonNode)diagnostic.DeepClone())
                .ToArray() ?? [])
        }.ToJsonString();
    }
}
