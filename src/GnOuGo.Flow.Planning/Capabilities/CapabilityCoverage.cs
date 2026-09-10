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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageContext;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageAssessment;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityDecisionGrounding;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchAssessment;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchRecovery;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;
using static GnOuGo.Flow.Planning.Capabilities.CapabilitySelectionValidation;
using static GnOuGo.Flow.Planning.Capabilities.PreparationTelemetry;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityCoverage
{

    internal static bool SupportsStructuredDecisionProjection(CapabilityCatalogEntry entry)
        => string.Equals(entry.Resolution, "mcp", StringComparison.Ordinal)
           || string.Equals(entry.Resolution, "native", StringComparison.Ordinal)
           && string.Equals(entry.Method, "llm.call", StringComparison.Ordinal);

    internal static string CreateUniqueNoEffectDecisionValue(IReadOnlyList<string> branchValues)
    {
        var candidate = SynthesizedNoEffectDecisionValue;
        var suffix = 2;
        while (branchValues.Contains(candidate, StringComparer.Ordinal))
            candidate = $"{SynthesizedNoEffectDecisionValue}_{suffix++}";
        return candidate;
    }

    internal static async Task<CapabilityMatchingEvaluation> ReviewCapabilityCoverageAndRematchAsync(
        StepExecutionContext ctx, JsonObject input, ILLMClient llmClient, CapabilityInventory inventory, CapabilityCatalog catalog, CapabilityMatchingEvaluation evaluation, string? provider, string model, string reasoning, TelemetrySpanScope inferenceSpan, CancellationToken ct)
    {
        var reviewTargets = evaluation.OperationMatches
            .Where(static match => match.Operation.Required
                                   && GetCapabilityContractCoverageRequirements(match.Operation).Count > 0
                                   && match.Status is "matched" or "composed" or "conditional")
            .ToArray();
        if (reviewTargets.Length == 0)
        {
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.reviewed_count", 0);
            return evaluation;
        }

        var review = await RequestCapabilityCoverageReviewAsync(
            ctx,
            llmClient,
            catalog,
            reviewTargets,
            provider,
            model,
            reasoning,
            inferenceSpan,
            ct);
        var gaps = review.Diagnostics
            .Where(static diagnostic => string.Equals(diagnostic.Status, "incomplete", StringComparison.Ordinal))
            .ToArray();
        RecordCapabilityCoverageTelemetry(ctx, review.Diagnostics, "initial");
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.reviewed_count", reviewTargets.Length);
        var initiallyIncompleteCount = gaps.Length;
        if (gaps.Length > 0)
        {
            gaps = await RetainIntrinsicCapabilityCoverageGapsAsync(
                ctx,
                llmClient,
                catalog,
                reviewTargets,
                gaps,
                provider,
                model,
                reasoning,
                inferenceSpan,
                "initial",
                ct);
        }
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.incomplete_count", initiallyIncompleteCount);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.intrinsic_gap_count", gaps.Length);
        if (gaps.Length == 0)
            return evaluation;

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                $"Capability coverage review found {gaps.Length} incomplete operation match(es); performing one targeted rematch."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
        });

        var affectedOperationIds = gaps
            .Select(static gap => gap.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var rematchResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
        {
            Provider = provider,
            Model = model,
            Prompt = BuildCapabilityCoverageRematchPrompt(inventory, catalog, evaluation, gaps),
            Reasoning = reasoning,
            UseBackgroundMode = true,
            StructuredOutputSchema = BuildTypedCoverageRematchSchema(inventory, catalog, affectedOperationIds),
            StructuredOutputStrict = true
        }, "workflow.plan.capability_coverage_rematch", ct);
        AddUsageAttributes(inferenceSpan, rematchResponse.Usage, model, provider);

        CapabilityMatchingEvaluation rematched;
        try
        {
            var candidate = ParseStructuredObject(rematchResponse, "capability coverage rematch");
            candidate = MergeTypedCoverageRematch(candidate, TypedMatchingCandidate(evaluation), affectedOperationIds);
            rematched = ParseCapabilityMatchingEvaluation(
                candidate,
                inventory,
                catalog);
            rematched = NormalizeLocalProcessingMatches(rematched);
            rematched = NormalizeCapabilityCompositionMatches(rematched, catalog);
            rematched = NormalizeConditionalSelectorMatches(rematched, catalog, inventory);
            var userInstruction = input["raw_prompt"]?.GetValue<string>()
                                  ?? (input["generator"] as JsonObject)?["raw_prompt"]?.GetValue<string>()
                                  ?? (input["generator"] as JsonObject)?["instruction"]?.GetValue<string>()
                                  ?? string.Empty;
            rematched = EnforceCapabilityPrerequisiteClosure(rematched, catalog);
            rematched = NormalizePlatformSafetyMatches(rematched, catalog);
            rematched = PreserveUnaffectedCapabilityMatches(evaluation, rematched, affectedOperationIds);
            rematched = CanonicalizeSharedStructuredDecisionOutputPaths(rematched);
            RecordCapabilityMatchingNormalizationTelemetry(inferenceSpan, rematched, "coverage_rematch");
            RecordConditionalGroundingTelemetry(inferenceSpan.Span, rematched, "coverage_rematch");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability coverage rematching returned an invalid contract.",
                inner: ex,
                details: new JsonObject
                {
                    ["phase"] = "capability_coverage_rematch",
                    ["classification"] = "contract_violation"
                });
        }

        if (!rematched.ContractValid)
            ThrowForUnresolvedCapabilityMatches(rematched, catalog, repairAttempted: true);

        var unresolvedAffected = rematched.OperationMatches
            .Where(match => affectedOperationIds.Contains(match.Operation.Id)
                            && match.Status is not ("matched" or "composed" or "conditional"))
            .ToArray();
        if (unresolvedAffected.Length == 0)
        {
            var rematchedTargets = rematched.OperationMatches
                .Where(match => affectedOperationIds.Contains(match.Operation.Id))
                .ToArray();
            review = await RequestCapabilityCoverageReviewAsync(
                ctx,
                llmClient,
                catalog,
                rematchedTargets,
                provider,
                model,
                reasoning,
                inferenceSpan,
                ct);
            gaps = review.Diagnostics
                .Where(static diagnostic => string.Equals(diagnostic.Status, "incomplete", StringComparison.Ordinal))
                .ToArray();
            RecordCapabilityCoverageTelemetry(ctx, review.Diagnostics, "rematch");
            if (gaps.Length > 0)
            {
                gaps = await RetainIntrinsicCapabilityCoverageGapsAsync(
                    ctx,
                    llmClient,
                    catalog,
                    rematchedTargets,
                    gaps,
                    provider,
                    model,
                    reasoning,
                    inferenceSpan,
                    "rematch",
                    ct);
            }
        }

        if (gaps.Length > 0 || unresolvedAffected.Length > 0)
        {
            await RequestCapabilityRelaxationOrThrowAsync(
                ctx,
                gaps,
                unresolvedAffected,
                evaluation,
                ct);
        }

        return rematched;
    }

    internal static async Task<CapabilityCoverageDiagnostic[]> RetainIntrinsicCapabilityCoverageGapsAsync(
        StepExecutionContext ctx, ILLMClient llmClient, CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, IReadOnlyList<CapabilityCoverageDiagnostic> gaps, string? provider, string model, string reasoning, TelemetrySpanScope inferenceSpan, string stage, CancellationToken ct)
    {
        var adjudication = await RequestCapabilityCoverageGapAdjudicationAsync(
            ctx,
            llmClient,
            catalog,
            targets,
            gaps,
            provider,
            model,
            reasoning,
            inferenceSpan,
            ct);
        var adjudicationByOperation = adjudication.Adjudications
            .ToDictionary(static item => item.OperationId, StringComparer.Ordinal);
        foreach (var item in adjudication.Adjudications)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.capability_coverage.gap_adjudication", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.operation_id", item.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.requirement_id", item.RequirementId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.catalog_id", item.CatalogId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.classification", item.Classification),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.structural_facets", string.Join(',', item.StructuralFacets)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.reason_code",
                    string.Equals(item.Classification, WorkflowStructureOnlyCoverageClassification, StringComparison.Ordinal)
                        ? "capability_coverage_workflow_structure_canonicalized"
                        : "capability_coverage_intrinsic_gap_confirmed")
            });
        }

        return gaps
            .Where(gap => adjudicationByOperation.TryGetValue(gap.OperationId, out var item)
                          && string.Equals(
                              item.Classification,
                              IntrinsicPrimitiveMissingCoverageClassification,
                              StringComparison.Ordinal))
            .ToArray();
    }

    internal static IReadOnlyList<CapabilityEvidenceAnchor> GetCapabilityContractCoverageRequirements(
        CapabilityInventoryOperation operation)
        => operation.CoverageRequirementEvidence
            .Where(requirement => !operation.WorkflowStructureCoverageRequirementIds.Contains(requirement.Id))
            .ToArray();

    internal static async Task<CapabilityCoverageReview> RequestCapabilityCoverageReviewAsync(
        StepExecutionContext ctx, ILLMClient llmClient, CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, string? provider, string model, string reasoning, TelemetrySpanScope inferenceSpan, CancellationToken ct)
    {
        var accepted = new Dictionary<string, CapabilityCoverageDiagnostic>(StringComparer.Ordinal);
        IReadOnlyList<CapabilityOperationMatch> pendingTargets = targets;
        CapabilityCoverageReview? lastReview = null;
        JsonObject? lastCandidate = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityCoverageReviewPrompt(
                    catalog,
                    pendingTargets,
                    lastReview,
                    lastCandidate),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildCapabilityCoverageReviewSchema(catalog, pendingTargets),
                StructuredOutputStrict = true
            }, attempt == 1
                ? "workflow.plan.capability_coverage_review"
                : "workflow.plan.capability_coverage_review_repair", ct);
            AddUsageAttributes(inferenceSpan, response.Usage, model, provider);
            try
            {
                lastCandidate = ParseStructuredObject(response, "capability coverage review");
                var review = ParseCapabilityCoverageReview(
                    lastCandidate,
                    catalog,
                    pendingTargets);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "initial" : "repair",
                    review.Issues);
                foreach (var diagnostic in review.Diagnostics.Where(static diagnostic => diagnostic.EvidenceQualified))
                    accepted[diagnostic.OperationId] = diagnostic;
                if (review.ContractValid)
                {
                    var diagnostics = targets
                        .Select(target => accepted[target.Operation.Id])
                        .ToArray();
                    return new CapabilityCoverageReview(
                        diagnostics,
                        true,
                        Array.Empty<CapabilityCoverageContractIssue>());
                }
                lastReview = review;
                pendingTargets = targets
                    .Where(target => !accepted.ContainsKey(target.Operation.Id))
                    .ToArray();
                if (pendingTargets.Count == 0)
                    pendingTargets = targets;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var issue = new CapabilityCoverageContractIssue(
                    "structured_response_invalid",
                    string.Empty,
                    "$",
                    null,
                    RequirementId: BuildCapabilityEvidenceId(
                        ex.GetType().Name,
                        -1,
                        0,
                        ex.Message));
                lastReview = new CapabilityCoverageReview(
                    Array.Empty<CapabilityCoverageDiagnostic>(),
                    false,
                    [issue]);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "initial" : "repair",
                    lastReview.Issues);
                pendingTargets = targets;
            }
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability coverage review remained invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_coverage_review",
                ["classification"] = "model_contract_violation",
                ["attempts"] = 2,
                ["contract_issues"] = new JsonArray((lastReview?.Issues
                        ?? Array.Empty<CapabilityCoverageContractIssue>())
                    .Select(static issue => (JsonNode)BuildCapabilityCoverageContractIssueJson(issue))
                    .ToArray()),
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "retry_or_change_planning_model"
            });
    }

    internal static async Task<CapabilityCoverageGapAdjudicationReview> RequestCapabilityCoverageGapAdjudicationAsync(
        StepExecutionContext ctx, ILLMClient llmClient, CapabilityCatalog catalog, IReadOnlyList<CapabilityOperationMatch> targets, IReadOnlyList<CapabilityCoverageDiagnostic> gaps, string? provider, string model, string reasoning, TelemetrySpanScope inferenceSpan, CancellationToken ct)
    {
        CapabilityCoverageGapAdjudicationReview? lastReview = null;
        JsonObject? lastCandidate = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityCoverageGapAdjudicationPrompt(
                    catalog,
                    targets,
                    gaps,
                    lastReview,
                    lastCandidate),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildCapabilityCoverageGapAdjudicationSchema(catalog, targets, gaps),
                StructuredOutputStrict = true
            }, attempt == 1
                ? "workflow.plan.capability_coverage_gap_adjudication"
                : "workflow.plan.capability_coverage_gap_adjudication_repair", ct);
            AddUsageAttributes(inferenceSpan, response.Usage, model, provider);
            try
            {
                lastCandidate = ParseStructuredObject(response, "capability coverage gap adjudication");
                lastReview = ParseCapabilityCoverageGapAdjudication(
                    lastCandidate,
                    catalog,
                    targets,
                    gaps);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "gap_adjudication_initial" : "gap_adjudication_repair",
                    lastReview.Issues);
                if (lastReview.ContractValid)
                    return lastReview;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastReview = new CapabilityCoverageGapAdjudicationReview(
                    Array.Empty<CapabilityCoverageGapAdjudication>(),
                    false,
                    [new CapabilityCoverageContractIssue(
                        "structured_response_invalid",
                        string.Empty,
                        "$",
                        null,
                        RequirementId: BuildCapabilityEvidenceId(
                            ex.GetType().Name,
                            -1,
                            0,
                            ex.Message))]);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "gap_adjudication_initial" : "gap_adjudication_repair",
                    lastReview.Issues);
            }
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability coverage gap adjudication remained invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_coverage_gap_adjudication",
                ["classification"] = "model_contract_violation",
                ["attempts"] = 2,
                ["contract_issues"] = new JsonArray((lastReview?.Issues
                        ?? Array.Empty<CapabilityCoverageContractIssue>())
                    .Select(static issue => (JsonNode)BuildCapabilityCoverageContractIssueJson(issue))
                    .ToArray()),
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "retry_or_change_planning_model"
            });
    }

    internal static CapabilityMatchingEvaluation PreserveUnaffectedCapabilityMatches(
        CapabilityMatchingEvaluation current, CapabilityMatchingEvaluation rematched, IReadOnlySet<string> affectedOperationIds)
    {
        var currentOperations = current.OperationMatches.ToDictionary(static match => match.Operation.Id, StringComparer.Ordinal);
        var operations = rematched.OperationMatches
            .Select(match => affectedOperationIds.Contains(match.Operation.Id)
                ? match
                : currentOperations[match.Operation.Id])
            .ToArray();
        var affectedIssues = rematched.Issues
            .Where(issue => affectedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var preservedIssues = current.Issues
            .Where(issue => !affectedOperationIds.Contains(issue.OperationId))
            .ToArray();
        return new CapabilityMatchingEvaluation(
            operations,
            current.ConstraintMatches,
            preservedIssues.Concat(affectedIssues).ToArray(),
            rematched.ContractValid);
    }

    internal static Task RequestCapabilityRelaxationOrThrowAsync(
        StepExecutionContext ctx, IReadOnlyList<CapabilityCoverageDiagnostic> gaps, IReadOnlyList<CapabilityOperationMatch> unresolvedAffected, CapabilityMatchingEvaluation originalEvaluation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        throw BuildCapabilityCoverageUnavailable(gaps, unresolvedAffected);
    }

    internal static WorkflowRuntimeException BuildCapabilityCoverageUnavailable(
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps, IReadOnlyList<CapabilityOperationMatch> unresolvedAffected)
    {
        var details = new JsonObject
        {
            ["phase"] = "capability_coverage_review",
            ["reason"] = "incomplete_effect_coverage",
            ["planning_outcome"] = "cannot_plan_safely",
            ["recommended_action"] = "install_or_expose_a_sufficient_capability_or_explicitly_relax_the_requirement",
            ["coverage_gaps"] = new JsonArray(gaps.Select(static gap => (JsonNode)new JsonObject
            {
                ["operation_id"] = gap.OperationId,
                ["unsupported_requirement_id"] = gap.UnsupportedRequirementId,
                ["unsupported_requirement"] = gap.UnsupportedRequirement,
                ["supported_weaker_behavior"] = gap.SupportedWeakerBehavior,
                ["candidate_catalog_ids"] = new JsonArray(gap.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["evidence_qualified"] = gap.EvidenceQualified
            }).ToArray()),
            ["unresolved_operation_ids"] = new JsonArray(unresolvedAffected
                .Select(static match => (JsonNode?)JsonValue.Create(match.Operation.Id)).ToArray())
        };
        return new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            "A required operation is only partially supported by the available capability catalog. The requested guarantee was preserved and planning stopped.",
            details: details);
    }

    internal static bool IsDeclaredArtifactComposition(IReadOnlyList<CapabilityCatalogEntry> selected)
    {
        if (selected.Count < 2)
            return false;

        var adjacency = Enumerable.Range(0, selected.Count)
            .Select(static _ => new HashSet<int>())
            .ToArray();
        for (var consumerIndex = 0; consumerIndex < selected.Count; consumerIndex++)
        {
            foreach (var requirement in GetRequiredArtifactRequirements(selected[consumerIndex]))
            {
                var producers = Enumerable.Range(0, selected.Count)
                    .Where(index => index != consumerIndex
                                    && CapabilityProducesArtifactKind(selected[index], requirement.Kind))
                    .Take(2)
                    .ToArray();
                if (producers.Length > 1)
                    return false;
                if (producers.Length != 1)
                    continue;

                adjacency[consumerIndex].Add(producers[0]);
                adjacency[producers[0]].Add(consumerIndex);
            }
        }

        if (adjacency.Any(static neighbors => neighbors.Count == 0))
            return false;

        var visited = new HashSet<int> { 0 };
        var pending = new Queue<int>();
        pending.Enqueue(0);
        while (pending.TryDequeue(out var current))
        {
            foreach (var neighbor in adjacency[current])
            {
                if (visited.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }

        return visited.Count == selected.Count;
    }

    internal static JsonObject BuildCapabilityCandidateCard(CapabilityCatalogEntry entry) => new()
    {
        ["catalog_id"] = entry.Id,
        ["resolution"] = entry.Resolution,
        ["server"] = entry.Server,
        ["kind"] = entry.Kind,
        ["method"] = entry.Method,
        ["request_bindings"] = BuildRequestBindingsJson(entry.RequestBindings)
    };
}
