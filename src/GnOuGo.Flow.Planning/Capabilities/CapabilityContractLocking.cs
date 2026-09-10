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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatching;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityContractLocking
{

    internal static (IReadOnlyList<ResolvedCapability>, IReadOnlyList<CapabilityConstraint>) ResolveCapabilityMatches(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var retainedMaterializerOccurrences = FindRetainedMaterializerOccurrences(evaluation, entries);
        var retainedSharedWriteOccurrences = FindRetainedSharedWriteOccurrences(evaluation, entries);
        var resolved = new List<ResolvedCapability>();
        foreach (var match in evaluation.OperationMatches)
        {
            if (match.Status == "local")
            {
                var localDecisionConsumers = evaluation.OperationMatches
                    .Where(candidate => string.Equals(
                                            candidate.DecisionContractSource,
                                            LocalDecisionContractSource,
                                            StringComparison.Ordinal)
                                        && string.Equals(
                                            candidate.DecisionOperationId,
                                            match.Operation.Id,
                                            StringComparison.Ordinal))
                    .ToArray();
                if (localDecisionConsumers.Length > 0)
                {
                    var producerCatalogIds = localDecisionConsumers
                        .Select(static candidate => candidate.DecisionProducerCatalogId)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (producerCatalogIds.Length != 1
                        || !entries.TryGetValue(producerCatalogIds[0]!, out var producer)
                        || !string.Equals(producer.Resolution, "native", StringComparison.Ordinal)
                        || !string.Equals(producer.Method, LocalDecisionStepType, StringComparison.Ordinal))
                    {
                        throw new WorkflowRuntimeException(
                            ErrorCodes.CapabilityPreflightUnavailable,
                            "The synthesized local decision operation has no single policy-allowed evaluator.");
                    }

                    resolved.Add(new ResolvedCapability(
                        match.Operation.Id,
                        match.Operation.Description,
                        match.Operation.Required,
                        producer.Resolution,
                        producer.Server,
                        producer.Kind,
                        producer.Method,
                        producer.RequestBindings,
                        match.Operation.Id,
                        producer.Id,
                        "matched",
                        match.Operation.ExecutionKind,
                        match.Operation.ExternalEffectKind,
                        CapabilityDescription: producer.Description)
                    {
                        InputOperationIds = TryResolveLocalDecisionInputs(evaluation, match.Operation.Id, out var reducerInputs) ? reducerInputs : []
                    });
                    continue;
                }

                resolved.Add(new ResolvedCapability(match.Operation.Id, match.Operation.Description, match.Operation.Required,
                    "local", null, null, null, Array.Empty<CapabilityRequestBinding>(), match.Operation.Id, null, match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind)
                {
                    InputOperationIds = match.Operation.InputOperationIds
                });
                continue;
            }
            if (match.Status == "unavailable")
            {
                resolved.Add(new ResolvedCapability(match.Operation.Id, match.Operation.Description, match.Operation.Required,
                    "unavailable", null, null, null, Array.Empty<CapabilityRequestBinding>(), match.Operation.Id, null, match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind));
                continue;
            }
            IReadOnlyDictionary<string, string> conditionalBranches = new Dictionary<string, string>(StringComparer.Ordinal);
            var conditionalActivationMode = string.Empty;
            if (match.Status == "conditional"
                && !TryBuildConditionalActivation(
                    match.CatalogIds.Select(id => entries[id]).ToArray(),
                    match.Operation.AllowNoEffectOutcome,
                    match.ConditionalActivationMode,
                    out conditionalBranches,
                    out conditionalActivationMode))
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    $"Conditional capability operation '{match.Operation.Id}' does not contain valid mutually exclusive selector variants.");
            }

            foreach (var catalogId in match.CatalogIds)
            {
                var entry = entries[catalogId];
                if (IsArtifactMaterializer(entry)
                    && !retainedMaterializerOccurrences.Contains((match.Operation.Id, catalogId)))
                {
                    continue;
                }
                if (string.Equals(match.Operation.ExternalEffectKind, "write", StringComparison.Ordinal)
                    && !retainedSharedWriteOccurrences.Contains((match.Operation.Id, catalogId)))
                {
                    continue;
                }
                var id = match.CatalogIds.Count == 1 ? match.Operation.Id : $"{match.Operation.Id}::{catalogId}";
                var isConditionalBranch = match.Status == "conditional" && conditionalBranches.ContainsKey(catalogId);
                var activation = isConditionalBranch
                    ? new McpCapabilityActivation(
                        conditionalActivationMode,
                        match.Operation.Id,
                        match.DecisionOperationId!,
                        conditionalBranches[catalogId])
                    {
                        DecisionOutputPath = match.DecisionOutputPath ?? string.Empty,
                        AllowedValues = match.DecisionAllowedValues ?? Array.Empty<string>(),
                        NoEffectValues = match.DecisionNoEffectValues ?? Array.Empty<string>(),
                        DecisionContractSource = match.DecisionContractSource ?? CapabilityDecisionContractSource,
                        DecisionProducerCatalogId = match.DecisionProducerCatalogId ?? string.Empty,
                        DecisionInputOperationIds = match.DecisionContractSource == LocalDecisionContractSource && TryResolveLocalDecisionInputs(evaluation, match.DecisionOperationId!, out var decisionInputs)
                            ? decisionInputs : evaluation.OperationMatches
                            .FirstOrDefault(candidate => string.Equals(
                                candidate.Operation.Id,
                                match.DecisionOperationId,
                                StringComparison.Ordinal))?
                            .Operation.InputOperationIds ?? Array.Empty<string>()
                    }
                    : null;
                resolved.Add(new ResolvedCapability(id, match.Operation.Description, match.Operation.Required,
                    entry.Resolution, entry.Server, entry.Kind, entry.Method, entry.RequestBindings,
                    match.Operation.Id, catalogId, match.Status == "conditional" && !isConditionalBranch ? "composed" : match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind, activation, entry.Description)
                {
                    InputOperationIds = match.Operation.InputOperationIds
                });
            }
        }

        var constraints = new List<CapabilityConstraint>(evaluation.ConstraintMatches.Count);
        foreach (var match in evaluation.ConstraintMatches)
        {
            var alternatives = match.Status == "enforced"
                ? match.DeniedCatalogIds.Select(id => entries[id])
                    .Select(static entry => new CapabilityAlternative(entry.Server!, entry.Kind!, entry.Method, entry.RequestBindings)).ToArray()
                : Array.Empty<CapabilityAlternative>();
            constraints.Add(new CapabilityConstraint(match.Constraint.Id, match.Constraint.Description, match.Constraint.Required, alternatives));
        }
        return (CoalescePlatformConfirmationCapabilities(resolved), constraints);
    }

    internal static IReadOnlyList<ResolvedCapability> CoalescePlatformConfirmationCapabilities(
        IReadOnlyList<ResolvedCapability> capabilities)
    {
        var platformConfirmations = capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
                                        && capability.OperationId?.StartsWith(
                                            "platform_confirm_external_write",
                                            StringComparison.Ordinal) == true
                                        && string.Equals(
                                            capability.Description,
                                            PlatformExternalWriteConfirmationOperationDescription,
                                            StringComparison.Ordinal))
            .ToArray();
        if (platformConfirmations.Length != 1)
            return capabilities;

        var platformConfirmation = platformConfirmations[0];
        var compatibleExisting = capabilities
            .Where(capability => !ReferenceEquals(capability, platformConfirmation)
                                 && capability.Required
                                 && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
                                 && string.Equals(capability.Method, platformConfirmation.Method, StringComparison.Ordinal)
                                 && string.Equals(capability.CatalogId, platformConfirmation.CatalogId, StringComparison.Ordinal)
                                 && capability.Activation == null
                                 && (string.Equals(capability.ExecutionKind, "human_interaction", StringComparison.Ordinal)
                                     || string.Equals(capability.ExternalEffectKind, "write", StringComparison.Ordinal)))
            .ToArray();
        if (compatibleExisting.Length != 1)
            return capabilities;

        var existing = compatibleExisting[0];
        var operationIds = GetResolvedCapabilityOperationIds(existing)
            .Concat(GetResolvedCapabilityOperationIds(platformConfirmation))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var coalesced = existing with { OperationIds = operationIds };
        return capabilities
            .Where(capability => !ReferenceEquals(capability, existing)
                                 && !ReferenceEquals(capability, platformConfirmation))
            .Append(coalesced)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetResolvedCapabilityOperationIds(ResolvedCapability capability)
        => capability.OperationIds is { Count: > 0 }
            ? capability.OperationIds
            : !string.IsNullOrWhiteSpace(capability.OperationId)
                ? [capability.OperationId]
                : [capability.Id];

    internal static HashSet<(string OperationId, string CatalogId)> FindRetainedSharedWriteOccurrences(
        CapabilityMatchingEvaluation evaluation, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries)
    {
        var occurrences = new List<SharedWriteOccurrence>();
        foreach (var match in evaluation.OperationMatches.Where(static match =>
                     match.Status is "matched" or "composed" or "conditional"
                     && string.Equals(match.Operation.ExternalEffectKind, "write", StringComparison.Ordinal)))
        {
            var selectedIds = match.CatalogIds.Where(entries.ContainsKey).ToArray();
            IReadOnlyDictionary<string, string> conditionalBranches = new Dictionary<string, string>(StringComparer.Ordinal);
            if (match.Status == "conditional")
                TryBuildConditionalActivation(
                    selectedIds.Select(id => entries[id]).ToArray(),
                    match.Operation.AllowNoEffectOutcome,
                    match.ConditionalActivationMode,
                    out conditionalBranches,
                    out _);

            foreach (var catalogId in selectedIds)
            {
                // A single-capability operation owns its invocation. A selector variant
                // in an exactly-one conditional group also owns its terminal invocation.
                // The same catalog entry repeated only as an unconditional member of a
                // larger composition is a shared prerequisite and must reuse that owner.
                occurrences.Add(new SharedWriteOccurrence(
                    match.Operation.Id,
                    catalogId,
                    selectedIds.Length == 1 || conditionalBranches.ContainsKey(catalogId)));
            }
        }

        return SelectRetainedSharedWriteOccurrences(occurrences);
    }

    internal static HashSet<(string OperationId, string CatalogId)> SelectRetainedSharedWriteOccurrences(
        IReadOnlyList<SharedWriteOccurrence> occurrences)
    {
        var retained = new HashSet<(string OperationId, string CatalogId)>();
        foreach (var group in occurrences.GroupBy(static value => value.CatalogId, StringComparer.Ordinal))
        {
            var ownedSources = group.Where(static value => value.IsOwnedSource).ToArray();
            if (ownedSources.Length == 0)
            {
                foreach (var value in group)
                    retained.Add((value.OperationId, value.CatalogId));
                continue;
            }

            foreach (var owner in ownedSources)
                retained.Add((owner.OperationId, owner.CatalogId));
        }
        return retained;
    }

    internal static HashSet<(string OperationId, string CatalogId)> FindRetainedMaterializerOccurrences(
        CapabilityMatchingEvaluation evaluation, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries)
    {
        var occurrences = new Dictionary<string, List<ArtifactMaterializerOccurrence>>(StringComparer.Ordinal);
        foreach (var match in evaluation.OperationMatches.Where(static match => match.Status is "matched" or "composed" or "conditional"))
        {
            var selected = match.CatalogIds
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            foreach (var materializer in selected.Where(IsArtifactMaterializer))
            {
                if (!occurrences.TryGetValue(materializer.Id, out var values))
                {
                    values = [];
                    occurrences[materializer.Id] = values;
                }
                // A standalone match proves an independently requested materialization.
                // Inside a larger composition the same catalog entry may be a repeated
                // prerequisite or an unrelated model-selected extra; retain it there only
                // when no standalone owner or stronger declared data-flow owner exists.
                values.Add(new ArtifactMaterializerOccurrence(
                    match.Operation.Id,
                    selected.Length == 1,
                    selected.SelectMany(GetRequiredArtifactRequirements)
                        .Select(static requirement => requirement.Kind)
                        .ToHashSet(StringComparer.Ordinal)));
            }
        }

        var matchesByOperationId = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        var decisionProducerOperationIds = evaluation.OperationMatches
            .Where(static match => string.Equals(match.Status, "conditional", StringComparison.Ordinal)
                                   && !string.IsNullOrWhiteSpace(match.DecisionOutputPath))
            .Select(static match => match.DecisionOperationId)
            .Where(static operationId => !string.IsNullOrWhiteSpace(operationId))
            .Select(static operationId => operationId!)
            .ToHashSet(StringComparer.Ordinal);
        var retained = new HashSet<(string OperationId, string CatalogId)>();
        foreach (var (catalogId, catalogOccurrences) in occurrences)
        {
            var distinctOccurrences = catalogOccurrences
                .GroupBy(static occurrence => occurrence.OperationId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            var explicitSources = distinctOccurrences
                .Where(static occurrence => occurrence.IsOwnedSource)
                .ToArray();
            if (explicitSources.Length > 0)
            {
                foreach (var source in explicitSources)
                    retained.Add((source.OperationId, catalogId));
                continue;
            }

            // Prerequisite closure can repeat one physical materializer on multiple
            // downstream operations. Prefer roots proven by the declared operation
            // data-flow graph, then a uniquely grounded decision producer, then one
            // unique maximal artifact consumer. Parallel roots remain independent
            // when contracts do not prove that they share one materialization.
            var operationIds = distinctOccurrences
                .Select(static occurrence => occurrence.OperationId)
                .ToHashSet(StringComparer.Ordinal);
            var rootOccurrences = distinctOccurrences.Where(occurrence =>
            {
                if (!matchesByOperationId.TryGetValue(occurrence.OperationId, out var match))
                    return false;
                var upstreamOperationIds = GetDeclaredUpstreamOperationIds(
                    match.Operation,
                    matchesByOperationId);
                return !upstreamOperationIds.Any(operationIds.Contains);
            }).ToArray();
            if (rootOccurrences.Length == 1)
            {
                retained.Add((rootOccurrences[0].OperationId, catalogId));
                continue;
            }

            var groundedDecisionSources = rootOccurrences
                .Where(occurrence => decisionProducerOperationIds.Contains(occurrence.OperationId))
                .ToArray();
            if (groundedDecisionSources.Length == 1)
            {
                retained.Add((groundedDecisionSources[0].OperationId, catalogId));
                continue;
            }

            var maximalConsumers = rootOccurrences
                .Where(candidate => !rootOccurrences.Any(other =>
                    !ReferenceEquals(candidate, other)
                    && other.RequiredArtifactKinds.IsProperSupersetOf(candidate.RequiredArtifactKinds)))
                .ToArray();
            if (maximalConsumers.Length == 1)
            {
                retained.Add((maximalConsumers[0].OperationId, catalogId));
                continue;
            }

            foreach (var root in rootOccurrences)
                retained.Add((root.OperationId, catalogId));
        }
        return retained;
    }

    internal static bool IsArtifactMaterializer(CapabilityCatalogEntry entry)
        => GetMaterializedArtifactKinds(entry).Count > 0;
}
