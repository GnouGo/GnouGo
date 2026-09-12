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

    internal static IReadOnlyList<CapabilityEvidenceAnchor> GetCapabilityContractCoverageRequirements(
        CapabilityInventoryOperation operation)
        => operation.CoverageRequirementEvidence
            .Where(requirement => !operation.WorkflowStructureCoverageRequirementIds.Contains(requirement.Id))
            .ToArray();

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
