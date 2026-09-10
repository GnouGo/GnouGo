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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractLocking;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityDecisionGrounding
{

    internal static CapabilityMatchingEvaluation CanonicalizeSharedStructuredDecisionOutputPaths(
        CapabilityMatchingEvaluation evaluation)
    {
        var collisions = evaluation.OperationMatches
            .Where(static match => string.Equals(
                                       match.DecisionContractSource,
                                       StructuredDecisionContractSource,
                                       StringComparison.Ordinal)
                                   && !string.IsNullOrWhiteSpace(match.DecisionOperationId)
                                   && !string.IsNullOrWhiteSpace(match.DecisionProducerCatalogId)
                                   && !string.IsNullOrWhiteSpace(match.DecisionOutputPath))
            .GroupBy(static match => (
                DecisionOperationId: match.DecisionOperationId!,
                ProducerCatalogId: match.DecisionProducerCatalogId!,
                OutputPath: match.DecisionOutputPath!))
            .Where(static group => group.Select(match => match.Operation.Id)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any())
            .SelectMany(static group => group)
            .Select(static match => match.Operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (collisions.Count == 0)
            return evaluation;

        var matches = evaluation.OperationMatches.Select(match =>
        {
            if (!collisions.Contains(match.Operation.Id))
                return match;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match.Operation.Id)))
                .ToLowerInvariant()[..16];
            return match with
            {
                DecisionOutputPath = $"/json/conditional_decision_{hash}",
                DecisionOutputPathNormalizationReasonCode = "conditional_decision_output_path_canonicalized"
            };
        }).ToArray();
        return evaluation with { OperationMatches = matches };
    }

    internal static bool TryGroundConditionalDecision(
        CapabilityMatchingEvaluation evaluation, CapabilityOperationMatch conditionalMatch, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, out string decisionOutputPath, out IReadOnlyList<string> allowedValues, out IReadOnlyList<string> noEffectValues, out string decisionContractSource, out string decisionProducerCatalogId, out string decisionProducerOperationId, out string failureCode)
    {
        decisionOutputPath = string.Empty;
        allowedValues = Array.Empty<string>();
        noEffectValues = Array.Empty<string>();
        decisionContractSource = string.Empty;
        decisionProducerCatalogId = string.Empty;
        decisionProducerOperationId = string.Empty;
        failureCode = string.Empty;
        var decisionOperationId = conditionalMatch.DecisionOperationId
                                  ?? conditionalMatch.Operation.DecisionSourceOperationId;
        if (string.IsNullOrWhiteSpace(decisionOperationId))
        {
            failureCode = "decision_source_missing";
            return false;
        }
        decisionProducerOperationId = decisionOperationId;

        if (!TryBuildConditionalActivation(
                conditionalMatch.CatalogIds.Where(entries.ContainsKey).Select(id => entries[id]).ToArray(),
                conditionalMatch.Operation.AllowNoEffectOutcome,
                conditionalMatch.ConditionalActivationMode,
                out var branches,
                out _))
        {
            failureCode = "conditional_branch_topology_invalid";
            return false;
        }

        var branchValues = branches.Values
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var decisionMatches = ResolveConditionalDecisionProducerCandidates(evaluation, decisionOperationId);
        if (decisionMatches.Count == 1)
            decisionProducerOperationId = decisionMatches[0].Operation.Id;
        var lockedDecisionMatch = evaluation.OperationMatches.FirstOrDefault(match => string.Equals(
            match.Operation.Id,
            decisionOperationId,
            StringComparison.Ordinal));
        var lockedDecisionIsPhysical = lockedDecisionMatch is { CatalogIds.Count: > 0 };

        if (lockedDecisionMatch?.Operation.ExecutionKind == "human_interaction")
        {
            var confirmations = lockedDecisionMatch.CatalogIds.Where(entries.ContainsKey).Select(id => entries[id])
                .Where(entry => entry.Resolution == "native" && entry.Method == "human.input").ToArray();
            if (confirmations.Length != 1 || conditionalMatch.ConditionalActivationMode != ConditionalAllOnValueActivationMode
                || !conditionalMatch.Operation.AllowNoEffectOutcome || branchValues.Length != 1)
            {
                failureCode = "confirmation_decision_contract_invalid"; return false;
            }
            return SetConditionalDecisionGrounding(new ConditionalDecisionGrounding(decisionOperationId, confirmations[0].Id,
                "/response", branchValues.Concat(new[] { SynthesizedNoEffectDecisionValue }).ToArray(),
                new[] { SynthesizedNoEffectDecisionValue }, PlanningDecisionContract.HumanConfirmation),
                out decisionOutputPath, out allowedValues, out noEffectValues, out decisionContractSource,
                out decisionProducerCatalogId, out decisionProducerOperationId);
        }

        // A declared reducer combines its inputs. Selecting one convenient enum or
        // model-capable ancestor would discard the other inputs (including human input).
        if (lockedDecisionMatch is { Status: "local" })
        {
            if (!TryResolveLocalDecisionInputs(evaluation, decisionOperationId, out var inputs))
            {
                failureCode = "conditional_decision_source_unavailable";
                return false;
            }
            {
                if (TryCreateLocalDecisionGrounding(evaluation, conditionalMatch, entries, branchValues, out var reducer))
                    return SetConditionalDecisionGrounding(reducer, out decisionOutputPath, out allowedValues, out noEffectValues,
                        out decisionContractSource, out decisionProducerCatalogId, out decisionProducerOperationId);
                failureCode = "conditional_decision_source_ambiguous";
                return false;
            }
        }

        IReadOnlyList<ConditionalDecisionGrounding> lockedProjected = Array.Empty<ConditionalDecisionGrounding>();
        if (decisionMatches.Count > 0)
        {
            var typed = decisionMatches
                .SelectMany(match => FindTypedConditionalDecisionGroundings(
                    match,
                    entries,
                    branchValues,
                    conditionalMatch.Operation.AllowNoEffectOutcome))
                .GroupBy(static grounding => (
                    grounding.OperationId,
                    grounding.CatalogId,
                    grounding.OutputPath,
                    grounding.ContractSource))
                .Select(static group => group.First())
                .Take(2)
                .ToArray();
            if (typed.Length == 1)
            {
                return SetConditionalDecisionGrounding(
                    typed[0],
                    out decisionOutputPath,
                    out allowedValues,
                    out noEffectValues,
                    out decisionContractSource,
                    out decisionProducerCatalogId,
                    out decisionProducerOperationId);
            }
            if (typed.Length > 1)
            {
                if (TryCreateLocalDecisionGrounding(
                        evaluation,
                        conditionalMatch,
                        entries,
                        branchValues,
                        out var localGrounding))
                {
                    return SetConditionalDecisionGrounding(
                        localGrounding,
                        out decisionOutputPath,
                        out allowedValues,
                        out noEffectValues,
                        out decisionContractSource,
                        out decisionProducerCatalogId,
                        out decisionProducerOperationId);
                }
                failureCode = "conditional_decision_source_ambiguous";
                return false;
            }

            if (lockedDecisionIsPhysical)
            {
                lockedProjected = FindProjectedConditionalDecisionGroundings(
                    lockedDecisionMatch!,
                    entries,
                    branchValues,
                    conditionalMatch.Operation.AllowNoEffectOutcome);
                if (lockedProjected.Count > 1)
                {
                    failureCode = "conditional_decision_source_ambiguous";
                    return false;
                }
            }
        }

        {
            if (lockedProjected.Count == 1)
                return SetConditionalDecisionGrounding(lockedProjected[0], out decisionOutputPath, out allowedValues, out noEffectValues,
                    out decisionContractSource, out decisionProducerCatalogId, out decisionProducerOperationId);
            if (TryCreateLocalDecisionGrounding(evaluation, conditionalMatch, entries, branchValues, out var exactReducer))
                return SetConditionalDecisionGrounding(exactReducer, out decisionOutputPath, out allowedValues, out noEffectValues,
                    out decisionContractSource, out decisionProducerCatalogId, out decisionProducerOperationId);
            failureCode = "declared_decision_contract_unresolved"; return false;
        }

    }

    internal static bool TryCreateLocalDecisionGrounding(
        CapabilityMatchingEvaluation evaluation, CapabilityOperationMatch conditionalMatch, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, IReadOnlyList<string> branchValues, out ConditionalDecisionGrounding grounding)
    {
        grounding = null!;
        var decisionOperationId = conditionalMatch.DecisionOperationId
                                  ?? conditionalMatch.Operation.DecisionSourceOperationId;
        var decisionMatch = evaluation.OperationMatches.FirstOrDefault(match =>
            string.Equals(match.Operation.Id, decisionOperationId, StringComparison.Ordinal));
        if (decisionMatch is null
            || !string.Equals(decisionMatch.Status, "local", StringComparison.Ordinal)
            || !string.Equals(decisionMatch.Operation.ExecutionKind, "local_processing", StringComparison.Ordinal))
        {
            return false;
        }

        // A declared local predicate may transform one result; it is not an alias of that result.
        // V2 keeps that exact operation as a native evaluator even with a single input.
        if (!TryResolveLocalDecisionInputs(evaluation, decisionOperationId!, out var upstreamIds) || upstreamIds.Count < (1)) return false;

        var evaluator = entries.Values.SingleOrDefault(static entry =>
            string.Equals(entry.Resolution, "native", StringComparison.Ordinal)
            && string.Equals(entry.Method, LocalDecisionStepType, StringComparison.Ordinal));
        if (evaluator is null)
            return false;

        var noEffectValues = conditionalMatch.Operation.AllowNoEffectOutcome
            ? new[] { CreateUniqueNoEffectDecisionValue(branchValues) }
            : Array.Empty<string>();
        var allowedValues = branchValues.Concat(noEffectValues)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conditionalMatch.Operation.Id)))
            .ToLowerInvariant()[..16];
        grounding = new ConditionalDecisionGrounding(
            decisionMatch.Operation.Id,
            evaluator.Id,
            $"/conditional_decision_{hash}",
            allowedValues,
            noEffectValues,
            LocalDecisionContractSource);
        return true;
    }

    internal static bool TryResolveLocalDecisionInputs(CapabilityMatchingEvaluation evaluation, string operationId, out IReadOnlyList<string> inputs)
    {
        var matches = evaluation.OperationMatches.ToDictionary(match => match.Operation.Id, StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id, HashSet<string> ancestors)
        {
            if (!ancestors.Add(id) || !matches.TryGetValue(id, out var current) || current.Status is "invalid" or "ambiguous" or "unavailable") return false;
            try
            {
                if (current.Status != "local")
                {
                    if (current.CatalogIds.Count == 0) return false;
                    roots.Add(id); return true;
                }
                return current.Operation.ExecutionKind == "local_processing" && current.Operation.InputOperationIds.Count > 0 &&
                    current.Operation.InputOperationIds.All(input => Visit(input, ancestors));
            }
            finally { ancestors.Remove(id); }
        }
        var valid = Visit(operationId, new(StringComparer.Ordinal));
        inputs = valid ? roots.Order(StringComparer.Ordinal).ToArray() : [];
        return valid;
    }

    internal static IReadOnlyList<ConditionalDecisionGrounding> FindConditionalDecisionSemanticRootGroundings(
        IEnumerable<CapabilityOperationMatch> decisionMatches, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, IReadOnlyList<string> branchValues, bool allowNoEffectOutcome)
    {
        var projected = decisionMatches
            .SelectMany(match => FindProjectedConditionalDecisionGroundings(
                match,
                entries,
                branchValues,
                allowNoEffectOutcome))
            .GroupBy(static grounding => (
                grounding.OperationId,
                grounding.CatalogId,
                grounding.OutputPath,
                grounding.ContractSource))
            .Select(static group => group.First())
            .Take(32)
            .ToArray();
        if (projected.Length <= 1)
            return projected;

        var artifactRoots = projected
            .Where(grounding => entries.TryGetValue(grounding.CatalogId, out var entry)
                                && entry.ArtifactContract?.Consumes.Any(static artifact => artifact.Required) == true)
            .Select(grounding => new
            {
                Grounding = grounding,
                RequiredKinds = entries[grounding.CatalogId].ArtifactContract!.Consumes
                    .Where(static artifact => artifact.Required)
                    .Select(static artifact => artifact.Kind)
                    .ToHashSet(StringComparer.Ordinal)
            })
            .ToArray();
        var maximalRoots = artifactRoots
            .Where(candidate => !artifactRoots.Any(other =>
                !ReferenceEquals(candidate, other)
                && other.RequiredKinds.IsProperSupersetOf(candidate.RequiredKinds)))
            .Select(static candidate => candidate.Grounding)
            .Take(2)
            .ToArray();
        return maximalRoots.Length == 1 ? maximalRoots : projected.Take(2).ToArray();
    }

    internal static IReadOnlyList<ConditionalDecisionGrounding> FindTypedConditionalDecisionGroundings(
        CapabilityOperationMatch decisionMatch, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, IReadOnlyList<string> branchValues, bool allowNoEffectOutcome)
        => decisionMatch.CatalogIds
            .Where(entries.ContainsKey)
            .SelectMany(id => entries[id].Outputs.Select(field => (CatalogId: id, Field: field)))
            .Where(candidate => ConditionalDecisionEnumCoversBranches(
                candidate.Field.EnumValues,
                branchValues,
                allowNoEffectOutcome))
            .GroupBy(static candidate => (candidate.CatalogId, candidate.Field.Path))
            .Select(group =>
            {
                var candidate = group.First();
                var declaredValues = candidate.Field.EnumValues
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new ConditionalDecisionGrounding(
                    decisionMatch.Operation.Id,
                    candidate.CatalogId,
                    candidate.Field.Path,
                    declaredValues,
                    declaredValues.Except(branchValues, StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    CapabilityDecisionContractSource);
            })
            .Take(2)
            .ToArray();

    internal static IReadOnlyList<ConditionalDecisionGrounding> FindProjectedConditionalDecisionGroundings(
        CapabilityOperationMatch decisionMatch, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, IReadOnlyList<string> branchValues, bool allowNoEffectOutcome)
    {
        if (!string.Equals(
                decisionMatch.Operation.ExecutionKind,
                "external_effect",
                StringComparison.Ordinal))
        {
            return Array.Empty<ConditionalDecisionGrounding>();
        }

        var selected = decisionMatch.CatalogIds
            .Distinct(StringComparer.Ordinal)
            .Where(entries.ContainsKey)
            .Select(id => entries[id])
            .ToArray();
        if (selected.Length == 0)
            return Array.Empty<ConditionalDecisionGrounding>();

        var prerequisiteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in selected)
        {
            foreach (var requirement in GetRequiredArtifactRequirements(consumer))
            {
                foreach (var producer in selected.Where(entry => CapabilityProducesArtifactKind(entry, requirement.Kind)))
                    prerequisiteIds.Add(producer.Id);
            }
        }

        var roots = selected
            .Where(entry => !prerequisiteIds.Contains(entry.Id))
            .Where(static entry => !IsArtifactMaterializer(entry))
            .Where(SupportsStructuredDecisionProjection)
            .Take(2)
            .ToArray();
        if (roots.Length == 0
            && selected.Length == 1
            && !IsArtifactMaterializer(selected[0])
            && SupportsStructuredDecisionProjection(selected[0]))
            roots = selected;

        var synthesizedNoEffectValues = allowNoEffectOutcome
            ? new[] { CreateUniqueNoEffectDecisionValue(branchValues) }
            : Array.Empty<string>();
        var allowed = branchValues.Concat(synthesizedNoEffectValues)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return roots.Select(root => new ConditionalDecisionGrounding(
                decisionMatch.Operation.Id,
                root.Id,
                "/json/decision",
                allowed,
                synthesizedNoEffectValues,
                StructuredDecisionContractSource))
            .ToArray();
    }

    internal static bool SetConditionalDecisionGrounding(
        ConditionalDecisionGrounding grounding, out string decisionOutputPath, out IReadOnlyList<string> allowedValues, out IReadOnlyList<string> noEffectValues, out string decisionContractSource, out string decisionProducerCatalogId, out string decisionProducerOperationId)
    {
        decisionOutputPath = grounding.OutputPath;
        allowedValues = grounding.AllowedValues;
        noEffectValues = grounding.NoEffectValues;
        decisionContractSource = grounding.ContractSource;
        decisionProducerCatalogId = grounding.CatalogId;
        decisionProducerOperationId = grounding.OperationId;
        return true;
    }

    internal static IReadOnlyList<CapabilityOperationMatch> ResolveConditionalDecisionProducerCandidates(
        CapabilityMatchingEvaluation evaluation, string decisionOperationId)
    {
        var matches = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        return ResolveConditionalDecisionProducerCandidates(
            matches,
            decisionOperationId,
            new HashSet<string>(StringComparer.Ordinal))
            .GroupBy(static match => match.Operation.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(32)
            .ToArray();
    }

    internal static IReadOnlyList<CapabilityOperationMatch> ResolveConditionalDecisionProducerCandidates(
        IReadOnlyDictionary<string, CapabilityOperationMatch> matches, string operationId, HashSet<string> visited)
    {
        if (!visited.Add(operationId) || !matches.TryGetValue(operationId, out var current))
            return Array.Empty<CapabilityOperationMatch>();
        if (current.CatalogIds.Count > 0)
            return [current];
        if (!string.Equals(current.Status, "local", StringComparison.Ordinal)
            || !string.Equals(current.Operation.ExecutionKind, "local_processing", StringComparison.Ordinal))
        {
            return Array.Empty<CapabilityOperationMatch>();
        }

        IReadOnlyList<string> declaredDependencies = current.Operation.InputOperationIds.Count > 0
            ? current.Operation.InputOperationIds
            : string.IsNullOrWhiteSpace(current.Operation.DecisionSourceOperationId)
                ? Array.Empty<string>()
                : [current.Operation.DecisionSourceOperationId];
        if (declaredDependencies.Count == 0)
            return Array.Empty<CapabilityOperationMatch>();

        return declaredDependencies
            .SelectMany(dependencyId => ResolveConditionalDecisionProducerCandidates(
                matches,
                dependencyId,
                new HashSet<string>(visited, StringComparer.Ordinal)))
            .GroupBy(static match => match.Operation.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(32)
            .ToArray();
    }

    internal static bool ConditionalDecisionEnumCoversBranches(
        IReadOnlyList<string> enumValues, IReadOnlyList<string> branchValues, bool allowNoEffectOutcome)
    {
        var declared = enumValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (declared.Length == 0 || branchValues.Any(value => !declared.Contains(value, StringComparer.Ordinal)))
            return false;

        return allowNoEffectOutcome
            ? declared.Length > branchValues.Count
            : declared.SequenceEqual(branchValues.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
