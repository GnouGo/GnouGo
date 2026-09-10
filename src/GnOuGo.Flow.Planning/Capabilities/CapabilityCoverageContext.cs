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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageAssessment;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchAssessment;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityCoverageContext
{

    internal static string BuildCapabilityCoverageGapAdjudicationPrompt(
        CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, IReadOnlyList<CapabilityCoverageDiagnostic> gaps, CapabilityCoverageGapAdjudicationReview? previous, JsonObject? rejectedCandidate)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var targetsById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        var operations = new JsonArray(gaps.Select(gap =>
        {
            var target = targetsById[gap.OperationId];
            return (JsonNode)new JsonObject
            {
                ["operation_id"] = gap.OperationId,
                ["requirement_id"] = gap.UnsupportedRequirementId,
                ["requirement_excerpt"] = gap.UnsupportedRequirement,
                ["prior_weaker_behavior"] = gap.SupportedWeakerBehavior,
                ["selected_catalog_ids"] = new JsonArray(target.CatalogIds
                    .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["selected_cards"] = new JsonArray(target.CatalogIds
                    .Where(entries.ContainsKey)
                    .Select(id => (JsonNode)new JsonObject
                    {
                        ["catalog_id"] = id,
                        ["card"] = BuildCapabilityCoverageCard(entries[id], catalog)
                    }).ToArray()),
                ["prior_evidence"] = new JsonArray(gap.Evidence.Select(static evidence => (JsonNode)new JsonObject
                {
                    ["catalog_id"] = evidence.CatalogId,
                    ["catalog_excerpt"] = evidence.CatalogExcerpt
                }).ToArray())
            };
        }).ToArray());
        var previousNotice = previous is { ContractValid: false }
            ? $$"""
                The previous adjudication violated the deterministic evidence contract. Correct every listed field and return every supplied operation exactly once.
                <previous_contract_issues>
                {{BuildCapabilityCoverageContractIssuesJson(previous.Issues)}}
                </previous_contract_issues>
                <rejected_adjudication_candidate>
                {{BuildRejectedCapabilityCoverageCandidate(rejectedCandidate, previous.Issues)}}
                </rejected_adjudication_candidate>
                """
            : string.Empty;
        return $$"""
            You are a provider-neutral capability coverage gap adjudicator. Return only the requested structured JSON.

            A prior reviewer found a capability-contract gap. Decide whether the selected cards actually omit an intrinsic observable primitive, or whether their documented primitive is sufficient and the only remaining differences belong to workflow structure. Use only the supplied requirement, selected cards, schemas, selectors, outputs, artifact contracts, composition metadata, and prior grounded evidence. Never infer behavior from provider, server, tool, method, product, URL, catalog numbering, operation prose, or domain names.

            Classify intrinsic_primitive_missing only when the required observable state transition itself is absent or a genuinely different primitive is documented. Classify workflow_structure_only when the same intrinsic primitive is documented and every remaining difference is one or more of these structural facets: cardinality, uniqueness, complete-scope or per-item iteration, ordering, conditions, confirmation, finalization, failure/cancellation policy, quality thresholds, caller-specific runtime arguments, input representation, or locally derivable mapping. A generic parameterized capability performs one invocation; workflow structure may invoke it for each item, supply runtime values, order it, guard it, and place it in finalization. Those facts do not make its primitive weaker.

            Return exactly one adjudication per supplied operation_id. Copy requirement_id exactly. For workflow_structure_only, return every applicable structural facet using only the schema enum. For intrinsic_primitive_missing, return an empty structural_facets array. Ground each decision with one exact non-empty catalog_excerpt copied verbatim from one selected card for that operation. Do not paraphrase evidence.

            {{previousNotice}}
            <coverage_gap_operations>
            {{operations.ToJsonString()}}
            </coverage_gap_operations>
            """;
    }

    internal static JsonObject BuildCapabilityCoverageGapAdjudicationSchema(
        CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, IReadOnlyList<CapabilityCoverageDiagnostic> gaps)
    {
        var targetById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        var operationIds = gaps.Select(static gap => (JsonNode?)JsonValue.Create(gap.OperationId)).ToArray();
        var requirementIds = gaps.Select(static gap => (JsonNode?)JsonValue.Create(gap.UnsupportedRequirementId)).ToArray();
        var catalogIds = gaps
            .Where(gap => targetById.ContainsKey(gap.OperationId))
            .SelectMany(gap => targetById[gap.OperationId].CatalogIds)
            .Where(id => catalog.Entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["adjudications"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = gaps.Count,
                    ["maxItems"] = gaps.Count,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["operation_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(operationIds)
                            },
                            ["requirement_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(requirementIds)
                            },
                            ["classification"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(
                                    IntrinsicPrimitiveMissingCoverageClassification,
                                    WorkflowStructureOnlyCoverageClassification)
                            },
                            ["structural_facets"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["maxItems"] = CapabilityCoverageStructuralFacets.Length,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JsonArray(CapabilityCoverageStructuralFacets
                                        .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray())
                                }
                            },
                            ["catalog_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(catalogIds)
                            },
                            ["catalog_excerpt"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["minLength"] = 1,
                                ["maxLength"] = CapabilityDescriptionMaxCharacters
                            }
                        },
                        ["required"] = new JsonArray(
                            "operation_id",
                            "requirement_id",
                            "classification",
                            "structural_facets",
                            "catalog_id",
                            "catalog_excerpt"),
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new JsonArray("adjudications"),
            ["additionalProperties"] = false
        };
    }

    internal static string BuildCapabilityCoverageReviewPrompt(
        CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, CapabilityCoverageReview? previous, JsonObject? rejectedCandidate)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var operations = new JsonArray(targets.Select(match => (JsonNode)new JsonObject
        {
            ["operation_id"] = match.Operation.Id,
            ["description"] = match.Operation.Description,
            ["coverage_requirements"] = new JsonArray(GetCapabilityContractCoverageRequirements(match.Operation)
                .Select(static requirement => (JsonNode)new JsonObject
                {
                    ["requirement_id"] = requirement.Id,
                    ["excerpt"] = requirement.Excerpt
                }).ToArray()),
            ["selected_catalog_ids"] = new JsonArray(match.CatalogIds
                .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["selected_cards"] = new JsonArray(match.CatalogIds
                .Where(entries.ContainsKey)
                .Select(id => (JsonNode)new JsonObject
                {
                    ["catalog_id"] = id,
                    ["card"] = BuildCapabilityCoverageCard(entries[id], catalog)
                }).ToArray())
        }).ToArray());
        var previousNotice = previous is { ContractValid: false }
            ? $$"""
                The previous review violated the deterministic evidence contract. Repair only the operations supplied below. The issue list is authoritative: correct every listed field, return every supplied operation exactly once, and do not repeat operations that are absent from this repair request.
                <previous_contract_issues>
                {{BuildCapabilityCoverageContractIssuesJson(previous.Issues)}}
                </previous_contract_issues>
                <rejected_coverage_candidate>
                {{BuildRejectedCapabilityCoverageCandidate(rejectedCandidate, previous.Issues)}}
                </rejected_coverage_candidate>
                """
            : string.Empty;
        return $$"""
            You are a provider-neutral capability coverage reviewer. Return only the requested structured JSON.

            Independently verify whether the exact selected capability cards document the intrinsic external primitive in every supplied capability-contract coverage requirement. The inventory has a separate workflow-structure class which is not supplied here. Never require a generic parameterized card to repeat caller-specific argument values or instructions, input identifiers or locator representations, locally derivable parameter mapping, cardinality, uniqueness, per-item or complete-scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation policy, or quality thresholds. If a supplied excerpt accidentally retains such structural context, evaluate only its intrinsic primitive; selected-card evidence for that primitive is sufficient. Distinct requested primitives, including alternative create and update effects, must still all be documented by the selected cards. Matching only a general topic or omitting an intrinsic primitive is incomplete. Do not infer behavior from provider, server, tool, method, product, URL, or domain names. Use only documented card text, schemas, selectors, outputs, artifact contracts, and composition metadata.

            Return exactly one diagnostic for every supplied operation_id and no others. Return supported only when every requirement is documented by the selected cards. Return incomplete when any requirement is absent or only a weaker behavior is documented. For incomplete, unsupported_requirement_id must be one exact requirement_id from coverage_requirements. supported_weaker_behavior must be one exact non-empty catalog_excerpt copied from a selected card that states the weaker observable behavior, or be empty when no meaningful relaxation exists. Never use a server, tool, method, selector, or catalog identifier as the weaker behavior. candidate_catalog_ids is advisory only: use an empty array unless one of the supplied selected_catalog_ids is also worth reconsidering. Do not invent or cite an unavailable catalog ID.

            Evidence is mandatory. requirement_id must exactly equal one supplied coverage requirement ID. catalog_id must exactly equal one selected_catalog_id for the same operation. catalog_excerpt must be copied verbatim with the same case and punctuation from that catalog_id's selected card; keep it short and do not paraphrase it. Supported decisions need evidence covering every requirement. Incomplete decisions need evidence for the unsupported requirement showing the selected card's narrower documented behavior. For supported, unsupported_requirement_id and supported_weaker_behavior must both be empty. Never invent an identifier or paraphrase an excerpt.

            {{previousNotice}}
            <coverage_review_operations>
            {{operations.ToJsonString()}}
            </coverage_review_operations>
            """;
    }

    internal static JsonObject BuildCapabilityCoverageReviewSchema(
        CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets)
    {
        var targetIds = targets
            .Select(static target => target.Operation.Id)
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        var requirementIds = targets
            .SelectMany(static target => GetCapabilityContractCoverageRequirements(target.Operation))
            .Select(static requirement => requirement.Id)
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        var selectedCatalogIds = targets
            .SelectMany(static target => target.CatalogIds)
            .Where(id => catalog.Entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["diagnostics"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = targets.Count,
                    ["maxItems"] = targets.Count,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["operation_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(targetIds)
                            },
                            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("supported", "incomplete") },
                            ["unsupported_requirement_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(
                                    new JsonNode?[] { JsonValue.Create(string.Empty) }
                                        .Concat(requirementIds.Select(static value => value?.DeepClone()))
                                        .ToArray())
                            },
                            ["supported_weaker_behavior"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = CapabilityDescriptionMaxCharacters
                            },
                            ["candidate_catalog_ids"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["maxItems"] = 8,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JsonArray(selectedCatalogIds.Select(static value => value?.DeepClone()).ToArray())
                                }
                            },
                            ["evidence"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["minItems"] = 1,
                                ["maxItems"] = Math.Max(1, targets.Sum(static target =>
                                    GetCapabilityContractCoverageRequirements(target.Operation).Count * Math.Max(1, target.CatalogIds.Count))),
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["catalog_id"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["enum"] = new JsonArray(selectedCatalogIds.Select(static value => value?.DeepClone()).ToArray())
                                        },
                                        ["requirement_id"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["enum"] = new JsonArray(requirementIds.Select(static value => value?.DeepClone()).ToArray())
                                        },
                                        ["catalog_excerpt"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["minLength"] = 1,
                                            ["maxLength"] = CapabilityDescriptionMaxCharacters
                                        }
                                    },
                                    ["required"] = new JsonArray("catalog_id", "requirement_id", "catalog_excerpt"),
                                    ["additionalProperties"] = false
                                }
                            }
                        },
                        ["required"] = new JsonArray("operation_id", "status", "unsupported_requirement_id", "supported_weaker_behavior", "candidate_catalog_ids", "evidence"),
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new JsonArray("diagnostics"),
            ["additionalProperties"] = false
        };
    }

    internal static JsonObject BuildCapabilityEvidenceReferenceSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source_id"] = new JsonObject { ["type"] = "string" },
            ["excerpt"] = new JsonObject { ["type"] = "string" }
        },
        ["required"] = new JsonArray("source_id", "excerpt"),
        ["additionalProperties"] = false
    };

    internal static JsonObject BuildCapabilityCoverageEvidenceReferenceSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source_id"] = new JsonObject { ["type"] = "string" },
            ["excerpt"] = new JsonObject { ["type"] = "string" },
            ["enforcement_kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    CapabilityContractCoverageEnforcementKind,
                    WorkflowStructureCoverageEnforcementKind)
            }
        },
        ["required"] = new JsonArray("source_id", "excerpt", "enforcement_kind"),
        ["additionalProperties"] = false
    };

    internal static string BuildCapabilityCoverageCard(
        CapabilityCatalogEntry entry, CapabilityCatalog catalog)
    {
        if (entry.RequestBindings.Count == 0)
            return entry.Card;
        var baseEntry = catalog.Entries.FirstOrDefault(candidate =>
            candidate.RequestBindings.Count == 0
            && string.Equals(candidate.Resolution, entry.Resolution, StringComparison.Ordinal)
            && string.Equals(candidate.Server, entry.Server, StringComparison.Ordinal)
            && string.Equals(candidate.Kind, entry.Kind, StringComparison.Ordinal)
            && string.Equals(candidate.Method, entry.Method, StringComparison.Ordinal));
        return baseEntry is null ? entry.Card : baseEntry.Card + Environment.NewLine + entry.Card;
    }

    internal static string BuildCapabilityCoverageRematchPrompt(
        CapabilityInventory inventory, CapabilityCatalog catalog, CapabilityMatchingEvaluation evaluation, IReadOnlyList<CapabilityCoverageDiagnostic> gaps)
    {
        var affectedIds = gaps.Select(static gap => gap.OperationId).ToHashSet(StringComparer.Ordinal);
        var current = new JsonArray(evaluation.OperationMatches.Select(match => (JsonNode)new JsonObject
        {
            ["operation_id"] = match.Operation.Id,
            ["status"] = match.Status,
            ["catalog_ids"] = new JsonArray(match.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(match.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["decision_operation_id"] = match.DecisionOperationId ?? string.Empty,
            ["conditional_mode"] = match.ConditionalActivationMode,
            ["reason"] = match.Reason,
            ["locked_unless_affected"] = !affectedIds.Contains(match.Operation.Id)
        }).ToArray());
        var diagnostics = new JsonArray(gaps.Select(static gap => (JsonNode)new JsonObject
        {
            ["operation_id"] = gap.OperationId,
            ["unsupported_requirement_id"] = gap.UnsupportedRequirementId,
            ["unsupported_requirement"] = gap.UnsupportedRequirement,
            ["supported_weaker_behavior"] = gap.SupportedWeakerBehavior,
            ["candidate_catalog_ids"] = new JsonArray(gap.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }).ToArray());
        var responseScope = "Return only operation_matches for the exact operation IDs in coverage_gaps. Every other operation and every constraint is retained by the host; do not return them.";
        return $$"""
            You are repairing one provider-neutral capability matching contract after an evidence-qualified coverage review. {{responseScope}}

            Change only operation IDs listed in coverage_gaps. For each affected operation, select the smallest documented capability or prerequisite-closed composition that fully implements every capability_contract coverage requirement; workflow_structure requirements are enforced later and must not be demanded from a capability card. Do not retain the previous selection merely because it implements a weaker intrinsic behavior. Use unavailable when the catalog contains no sufficient implementation. For a repaired conditional operation, preserve its decision_operation_id and use conditional_mode=exactly_one for selector alternatives or conditional_mode=all_on_value for an ordered composition with a declared no-effect outcome; use an empty conditional_mode otherwise. Never infer behavior from provider, server, tool, method, product, URL, or domain names.

            <runtime_inventory>
            {{BuildCapabilityInventoryJson(inventory)}}
            </runtime_inventory>
            <current_matching>
            {{current.ToJsonString()}}
            </current_matching>
            <coverage_gaps>
            {{diagnostics.ToJsonString()}}
            </coverage_gaps>
            <capability_catalog>
            {{catalog.Text}}
            </capability_catalog>
            """;
    }
    internal static JsonObject BuildTypedCoverageRematchSchema(CapabilityInventory inventory, CapabilityCatalog catalog, IReadOnlySet<string> affected)
        => ScopeTypedCoverageRematchSchema(BuildTypedCapabilityMatchingSchema(inventory, catalog), affected);

    internal static JsonObject ScopeTypedCoverageRematchSchema(JsonObject schema, IReadOnlySet<string> affected)
    {
        schema = schema.DeepClone().AsObject();
        var operations = schema["properties"]!["operation_matches"]!.AsObject();
        var entries = operations["properties"]!.AsObject();
        if (affected.Count == 0 || affected.Any(id => !entries.ContainsKey(id)))
            throw new InvalidOperationException("Coverage rematch requires known affected operation identifiers.");
        foreach (var id in entries.Select(p => p.Key).Except(affected).ToArray()) entries.Remove(id);
        operations["required"] = new JsonArray(entries.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        schema["properties"]!.AsObject().Remove("constraint_matches");
        schema["required"] = new JsonArray("operation_matches");
        return schema;
    }

    internal static JsonObject MergeTypedCoverageRematch(JsonObject patch, JsonObject baseline, IReadOnlySet<string> affected)
    {
        if (patch.Count != 1 || patch["operation_matches"] is not JsonObject operations ||
            affected.Count == 0 || !operations.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(affected))
            throw new InvalidOperationException("Coverage rematch must contain only operation_matches with exactly the affected operation identifiers.");
        var result = baseline.DeepClone().AsObject(); var retained = result["operation_matches"]!.AsObject();
        foreach (var (id, value) in operations)
        {
            if (!retained.ContainsKey(id) || value is not JsonObject match ||
                !match.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "status", "reason", "catalog_ids", "candidate_catalog_ids", "decision_operation_id", "conditional_mode" }))
                throw new InvalidOperationException("Coverage rematch operation '" + id + "' contains missing, unknown or unsupported fields.");
            retained[id] = match.DeepClone();
        }
        return result; // Reparse this exact merged contract; discarded model fields cannot taint validity.
    }
}
