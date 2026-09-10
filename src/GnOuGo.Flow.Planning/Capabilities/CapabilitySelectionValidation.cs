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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityDecisionGrounding;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilitySelectionValidation
{

    internal static CapabilityMatchingEvaluation NormalizeCapabilityCompositionMatches(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var normalizedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operationMatches = evaluation.OperationMatches.Select(match =>
        {
            var referenced = match.CatalogIds.Concat(match.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            if (referenced.Length == 0)
                return match;

            var wrappers = referenced
                .Where(static entry => entry.CompositionContract is
                {
                    Kind: McpCapabilityCompositionConventions.CompleteOperationKind,
                    Encapsulates.Count: > 0
                })
                .ToArray();
            if (wrappers.Length != 1)
                return match;

            var wrapper = wrappers[0];
            var requiredKinds = GetRequiredArtifactRequirements(wrapper)
                .Select(static requirement => requirement.Kind)
                .ToHashSet(StringComparer.Ordinal);
            var retainedProducers = referenced
                .Where(candidate => !string.Equals(candidate.Id, wrapper.Id, StringComparison.Ordinal))
                .Where(candidate => requiredKinds.Any(kind => CapabilityProducesArtifactKind(candidate, kind)))
                .ToArray();
            var unrelated = referenced
                .Where(candidate => !string.Equals(candidate.Id, wrapper.Id, StringComparison.Ordinal))
                .Where(candidate => retainedProducers.All(producer => !string.Equals(producer.Id, candidate.Id, StringComparison.Ordinal)))
                .Where(candidate => !string.Equals(candidate.Resolution, "mcp", StringComparison.Ordinal)
                                    || !string.Equals(candidate.Server, wrapper.Server, StringComparison.Ordinal)
                                    || !wrapper.CompositionContract!.Encapsulates.Any(encapsulated =>
                                        string.Equals(encapsulated.Kind, candidate.Kind, StringComparison.Ordinal)
                                        && string.Equals(encapsulated.Method, candidate.Method, StringComparison.Ordinal)))
                .ToArray();
            if (unrelated.Length > 0 && match.Status is not ("ambiguous" or "invalid"))
                return match;

            var normalizedIds = new[] { wrapper.Id }
                .Concat(retainedProducers.Select(static producer => producer.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            normalizedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = normalizedIds.Length == 1 ? "matched" : "composed",
                Reason = "The selected complete-operation capability encapsulates the referenced lower-level phases.",
                CatalogIds = normalizedIds,
                CandidateCatalogIds = Array.Empty<string>()
            };
        }).ToArray();

        if (normalizedOperationIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues
            .Where(issue => !normalizedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operationMatches.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operationMatches,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    internal static CapabilityMatchingEvaluation NormalizeLocalProcessingMatches(
        CapabilityMatchingEvaluation evaluation)
    {
        var normalizedOperationIds = evaluation.OperationMatches
            .Where(static match => string.Equals(
                match.Operation.ExecutionKind,
                "local_processing",
                StringComparison.Ordinal))
            .Select(static match => match.Operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (normalizedOperationIds.Count == 0)
            return evaluation;

        var operations = evaluation.OperationMatches.Select(match =>
            normalizedOperationIds.Contains(match.Operation.Id)
                ? match with
                {
                    Status = "local",
                    Reason = "The locked inventory classifies this operation as provider-neutral local processing.",
                    CatalogIds = Array.Empty<string>(),
                    CandidateCatalogIds = Array.Empty<string>(),
                    DecisionOperationId = null
                }
                : match).ToArray();
        var issues = evaluation.Issues
            .Where(issue => !normalizedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operations.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operations,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    internal static IReadOnlyList<string> RemoveStructurallyRedundantSelectorAncestorEntries(
        IReadOnlyList<string> selected, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, out bool normalized)
    {
        normalized = false;
        var referenced = selected
            .Distinct(StringComparer.Ordinal)
            .Where(entries.ContainsKey)
            .Select(id => entries[id])
            .ToArray();
        var redundantAncestorIds = referenced
            .GroupBy(static entry => (entry.Resolution, entry.Server, entry.Kind, entry.Method))
            .SelectMany(static group => group
                .Where(entry => group.Any(candidate => IsStrictSelectorAncestor(entry, candidate)))
                .Select(static entry => entry.Id))
            .ToHashSet(StringComparer.Ordinal);
        if (redundantAncestorIds.Count == 0)
            return selected;

        var result = selected.Where(id => !redundantAncestorIds.Contains(id)).ToArray();
        normalized = result.Length != selected.Count;
        return result;
    }

    internal static bool IsStrictSelectorAncestor(
        CapabilityCatalogEntry possibleAncestor, CapabilityCatalogEntry possibleDescendant)
    {
        if (possibleAncestor.RequestBindings.Count >= possibleDescendant.RequestBindings.Count)
            return false;

        var descendantBindings = possibleDescendant.RequestBindings.ToDictionary(
            static binding => binding.Path,
            static binding => binding.Value,
            StringComparer.Ordinal);
        return possibleAncestor.RequestBindings.All(binding =>
            descendantBindings.TryGetValue(binding.Path, out var descendantValue)
            && JsonNode.DeepEquals(binding.Value, descendantValue));
    }

    internal static CapabilityMatchingEvaluation NormalizeConditionalSelectorMatches(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog, CapabilityInventory inventory)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var changedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedIssues = new List<CapabilityMatchingIssue>();
        var operationMatches = evaluation.OperationMatches.Select(match =>
        {
            if (match.Status is "matched" or "conditional" or "local")
                return match;
            if (match.Operation.DecisionSourceOperationId.Length == 0)
                return match;

            var referencedIds = match.CatalogIds.Concat(match.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (referencedIds.Length == 1
                && entries.TryGetValue(referencedIds[0], out var soleEntry)
                && soleEntry.RequestBindings.Count > 0
                && !HasCompatibleConditionalSelectorSibling(
                    soleEntry,
                    entries.Values,
                    match.Operation.AllowNoEffectOutcome))
            {
                changedOperationIds.Add(match.Operation.Id);
                const string reason = "Only one exact selector capability remains, so the discovered catalog cannot implement the declared set of runtime alternatives.";
                normalizedIssues.Add(new CapabilityMatchingIssue(
                    match.Operation.Id,
                    match.Operation.Description,
                    match.Operation.Required,
                    "unavailable",
                    reason,
                    referencedIds));
                return match with
                {
                    Status = "unavailable",
                    Reason = reason,
                    CatalogIds = Array.Empty<string>(),
                    CandidateCatalogIds = Array.Empty<string>(),
                    DecisionOperationId = null,
                    ConditionalActivationMode = string.Empty,
                    NormalizationReasonCode = "conditional_selector_set_insufficient"
                };
            }
            if (referencedIds.Length < 2 || referencedIds.Any(id => !entries.ContainsKey(id)))
                return match;
            var canonicalReferencedIds = RemoveStructurallyRedundantSelectorAncestorEntries(
                referencedIds,
                entries,
                out var selectorAncestorRemoved);
            var canonicalReferenced = canonicalReferencedIds.Select(id => entries[id]).ToArray();
            if (canonicalReferenced.Length < 2)
            {
                if (!selectorAncestorRemoved)
                    return match;

                var soleCanonicalEntry = canonicalReferenced.Single();
                if (HasCompatibleConditionalSelectorSibling(
                        soleCanonicalEntry,
                        entries.Values,
                        match.Operation.AllowNoEffectOutcome))
                    return match;

                changedOperationIds.Add(match.Operation.Id);
                const string reason = "The referenced selector entries collapse to one logical capability, so they cannot implement the declared set of runtime alternatives.";
                normalizedIssues.Add(new CapabilityMatchingIssue(
                    match.Operation.Id,
                    match.Operation.Description,
                    match.Operation.Required,
                    "unavailable",
                    reason,
                    canonicalReferencedIds.Take(8).ToArray()));
                return match with
                {
                    Status = "unavailable",
                    Reason = reason,
                    CatalogIds = Array.Empty<string>(),
                    CandidateCatalogIds = Array.Empty<string>(),
                    DecisionOperationId = null,
                    ConditionalActivationMode = string.Empty,
                    NormalizationReasonCode = "selector_ancestor_chain_insufficient"
                };
            }
            if (!TryBuildConditionalActivation(
                    canonicalReferenced,
                    match.Operation.AllowNoEffectOutcome,
                    match.ConditionalActivationMode,
                    out _,
                    out var conditionalActivationMode))
                return match;

            var decisionOperationId = match.Operation.DecisionSourceOperationId;
            if (inventory.Operations.All(operation => !string.Equals(
                    operation.Id,
                    decisionOperationId,
                    StringComparison.Ordinal)))
                return match;

            var conditionalCandidate = match with
            {
                Status = "conditional",
                CatalogIds = canonicalReferenced.Select(static entry => entry.Id).ToArray(),
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = decisionOperationId,
                ConditionalActivationMode = conditionalActivationMode
            };
            if (!TryGroundConditionalDecision(
                    evaluation,
                    conditionalCandidate,
                    entries,
                    out var decisionOutputPath,
                    out var decisionAllowedValues,
                    out var decisionNoEffectValues,
                    out var decisionContractSource,
                    out var decisionProducerCatalogId,
                    out var decisionProducerOperationId,
                    out var failureCode))
            {
                // Multiple read selectors are safe to keep as an unconditional composition
                // when no declared runtime discriminator proves that they are alternatives.
                if (string.Equals(match.Operation.ExternalEffectKind, "read", StringComparison.Ordinal))
                    return match;

                changedOperationIds.Add(match.Operation.Id);
                const string reason = "Conditional activation has no provider-neutral decision contract that covers every effect branch and declared no-effect outcome.";
                normalizedIssues.Add(new CapabilityMatchingIssue(
                    match.Operation.Id,
                    match.Operation.Description,
                    match.Operation.Required,
                    "contract_gap",
                    reason,
                    canonicalReferenced.Select(static entry => entry.Id).Take(8).ToArray())
                {
                    ReasonCode = failureCode
                });
                return match with
                {
                    Status = "invalid",
                    Reason = reason,
                    DecisionOperationId = decisionOperationId,
                    DecisionGroundingFailureCode = failureCode
                };
            }

            changedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = "conditional",
                Reason = "The exact selector subset contains mutually exclusive runtime branches selected by an earlier workflow result; complementary selected capabilities remain unconditional prerequisites.",
                CatalogIds = canonicalReferenced.Select(static entry => entry.Id).ToArray(),
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = decisionProducerOperationId,
                DecisionOutputPath = decisionOutputPath,
                DecisionAllowedValues = decisionAllowedValues,
                DecisionNoEffectValues = decisionNoEffectValues,
                DecisionContractSource = decisionContractSource,
                DecisionProducerCatalogId = decisionProducerCatalogId,
                DecisionGroundingFailureCode = null,
                ConditionalActivationMode = conditionalActivationMode,
                NormalizationReasonCode = string.Equals(
                    decisionProducerOperationId,
                    decisionOperationId,
                    StringComparison.Ordinal)
                    ? string.Equals(
                        conditionalActivationMode,
                        ConditionalAllOnValueActivationMode,
                        StringComparison.Ordinal)
                        ? "conditional_composition_canonicalized"
                        : selectorAncestorRemoved || match.CandidateCatalogIds.Count > 0
                            ? "conditional_selector_family_canonicalized"
                            : match.NormalizationReasonCode
                    : "conditional_decision_source_canonicalized"
            };
        }).ToArray();

        if (changedOperationIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues
            .Where(issue => !changedOperationIds.Contains(issue.OperationId))
            .Concat(normalizedIssues)
            .ToArray();
        var contractValid = operationMatches.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status is not ("invalid" or "contract_gap"));
        return CanonicalizeSharedStructuredDecisionOutputPaths(evaluation with
        {
            OperationMatches = operationMatches,
            Issues = issues,
            ContractValid = contractValid
        });
    }

    internal static bool HasCompatibleConditionalSelectorSibling(
        CapabilityCatalogEntry entry, IEnumerable<CapabilityCatalogEntry> candidates, bool allowNoEffectOutcome)
        => candidates.Any(candidate =>
            !string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal)
            && string.Equals(candidate.Resolution, entry.Resolution, StringComparison.Ordinal)
            && string.Equals(candidate.Server, entry.Server, StringComparison.Ordinal)
            && string.Equals(candidate.Kind, entry.Kind, StringComparison.Ordinal)
            && string.Equals(candidate.Method, entry.Method, StringComparison.Ordinal)
            && candidate.RequestBindings.Count > 0
            && TryBuildConditionalActivation(
                [entry, candidate],
                allowNoEffectOutcome,
                string.Empty,
                out _,
                out _));

    internal static CapabilityMatchingEvaluation NormalizePlatformSafetyMatches(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog)
    {
        var confirmation = catalog.Entries.FirstOrDefault(static entry =>
            string.Equals(entry.Resolution, "native", StringComparison.Ordinal)
            && string.Equals(entry.Method, "human.input", StringComparison.Ordinal));
        if (confirmation == null)
            return evaluation;

        var normalizedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operations = evaluation.OperationMatches.Select(match =>
        {
            var isPlatformConfirmation = match.Operation.Id.StartsWith(
                                             "platform_confirm_external_write",
                                             StringComparison.Ordinal)
                                         && string.Equals(
                                             match.Operation.Description,
                                             PlatformExternalWriteConfirmationOperationDescription,
                                             StringComparison.Ordinal);
            var isDeclaredHumanInteraction = string.Equals(
                match.Operation.ExecutionKind,
                "human_interaction",
                StringComparison.Ordinal);
            if (!isPlatformConfirmation && !isDeclaredHumanInteraction)
                return match;

            normalizedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = "matched",
                Reason = isPlatformConfirmation
                    ? "The platform-owned external-write safety gate uses the registered native human.input step."
                    : "The locked inventory classifies this operation as provider-neutral human interaction implemented by the registered native human.input step.",
                CatalogIds = [confirmation.Id],
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = null
            };
        }).ToArray();

        var normalizedConstraintIds = new HashSet<string>(StringComparer.Ordinal);
        var constraints = evaluation.ConstraintMatches.Select(match =>
        {
            if (!match.Constraint.Id.StartsWith("platform_external_write_after_confirmation", StringComparison.Ordinal)
                || !string.Equals(
                    match.Constraint.Description,
                    PlatformExternalWriteConfirmationConstraintDescription,
                    StringComparison.Ordinal))
            {
                return match;
            }

            normalizedConstraintIds.Add(match.Constraint.Id);
            return match with
            {
                Status = "policy_only",
                Reason = "The platform-owned confirmation ordering rule is enforced by workflow topology.",
                DeniedCatalogIds = Array.Empty<string>(),
                CandidateCatalogIds = Array.Empty<string>()
            };
        }).ToArray();

        if (normalizedOperationIds.Count == 0 && normalizedConstraintIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues.Where(issue =>
                !normalizedOperationIds.Contains(issue.OperationId)
                && !normalizedConstraintIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operations.All(static match => match.Status != "invalid")
                            && constraints.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operations,
            ConstraintMatches = constraints,
            Issues = issues,
            ContractValid = contractValid
        };
    }
}
