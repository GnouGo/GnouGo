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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityPrerequisites
{

    internal static CapabilityMatchingEvaluation EnforceCapabilityPrerequisiteClosure(
        CapabilityMatchingEvaluation evaluation, CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var matchesByOperationId = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        var operationMatches = new List<CapabilityOperationMatch>(evaluation.OperationMatches.Count);
        var closureIssues = new List<CapabilityMatchingIssue>();
        var resolvedOperationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in evaluation.OperationMatches)
        {
            if (match.Status is not ("matched" or "composed" or "conditional"))
            {
                operationMatches.Add(match);
                continue;
            }

            var preferredProducerIds = GetDeclaredUpstreamOperationIds(match.Operation, matchesByOperationId)
                .SelectMany(operationId => matchesByOperationId.TryGetValue(operationId, out var upstream)
                    ? upstream.CatalogIds
                    : Array.Empty<string>())
                .Where(entries.ContainsKey)
                .ToHashSet(StringComparer.Ordinal);
            var search = SearchArtifactClosure(
                match.CatalogIds,
                catalog.Entries,
                entries,
                preferredProducerIds);
            var minimalSolutions = search.Solutions
                .GroupBy(static solution => solution.Count)
                .OrderBy(static group => group.Key)
                .FirstOrDefault()?
                .OrderBy(static solution => string.Join('\u001f', solution), StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<IReadOnlyList<string>>();
            if (minimalSolutions.Length == 1)
            {
                var selectedIds = minimalSolutions[0];
                var addedProducer = selectedIds.Count > match.CatalogIds.Distinct(StringComparer.Ordinal).Count();
                operationMatches.Add(addedProducer
                    ? match with
                    {
                        Status = match.Status == "conditional" ? "conditional" : selectedIds.Count == 1 ? "matched" : "composed",
                        Reason = "The selected capabilities form the unique minimal prerequisite-closed artifact composition.",
                        CatalogIds = selectedIds,
                        CandidateCatalogIds = Array.Empty<string>(),
                        NormalizationReasonCode = "artifact_closure_resolved"
                    }
                    : match);
                resolvedOperationIds.Add(match.Operation.Id);
                continue;
            }

            var selected = match.CatalogIds.Where(entries.ContainsKey).Select(id => entries[id]).ToArray();
            var missing = GetMissingArtifactRequirements(selected);
            var candidateIds = match.CatalogIds
                .Concat(search.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            var fields = string.Join(", ", missing.Select(static item => item.Field.Path));
            var status = minimalSolutions.Length > 1 ? "ambiguous" : "unavailable";
            var reasonCode = minimalSolutions.Length > 1
                ? "artifact_closure_multiple"
                : search.HitLimit
                    ? "artifact_closure_limit"
                    : search.SawCycle
                        ? "artifact_closure_cycle"
                        : "artifact_closure_unavailable";
            var reason = reasonCode switch
            {
                "artifact_closure_multiple" => $"The selected capability requires operational artifacts at {fields}, and more than one minimal prerequisite-closed producer composition remains valid.",
                "artifact_closure_limit" => $"The selected capability requires operational artifacts at {fields}, but deterministic prerequisite closure exceeded its bounded depth or catalog-ID limit.",
                "artifact_closure_cycle" => $"The selected capability requires operational artifacts at {fields}, but every discovered producer composition contains a dependency cycle.",
                _ => $"The selected capability requires operational artifacts at {fields}, but no discovered acyclic producer composition can supply them."
            };
            var repairedMatch = match with
            {
                Status = status,
                Reason = reason,
                CatalogIds = Array.Empty<string>(),
                CandidateCatalogIds = status == "ambiguous" ? candidateIds : Array.Empty<string>(),
                NormalizationReasonCode = reasonCode
            };
            operationMatches.Add(repairedMatch);
            closureIssues.Add(new CapabilityMatchingIssue(
                match.Operation.Id,
                match.Operation.Description,
                match.Operation.Required,
                status,
                reason,
                repairedMatch.CandidateCatalogIds)
            {
                ReasonCode = reasonCode
            });
        }

        var replacedOperationIds = closureIssues
            .Select(static issue => issue.OperationId)
            .Concat(resolvedOperationIds)
            .ToHashSet(StringComparer.Ordinal);
        var issues = evaluation.Issues
            .Where(issue => !replacedOperationIds.Contains(issue.OperationId))
            .Concat(closureIssues)
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

    internal static ArtifactClosureSearchResult SearchArtifactClosure(
        IReadOnlyList<string> initialCatalogIds, IReadOnlyList<CapabilityCatalogEntry> catalog, IReadOnlyDictionary<string, CapabilityCatalogEntry> entries, IReadOnlySet<string> preferredProducerIds)
    {
        var solutions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var sawCycle = false;
        var hitLimit = false;

        void Explore(IReadOnlyList<string> selectedIds, int depth)
        {
            if (solutions.Count >= 16)
                return;
            var canonicalIds = selectedIds
                .Where(entries.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (canonicalIds.Length > CapabilityArtifactClosureMaxCatalogIds)
            {
                hitLimit = true;
                return;
            }

            var selected = canonicalIds.Select(id => entries[id]).ToArray();
            var missing = GetMissingArtifactRequirements(selected);
            if (missing.Length == 0)
            {
                if (HasArtifactDependencyCycle(selected))
                {
                    sawCycle = true;
                    return;
                }

                var key = string.Join('\u001f', canonicalIds);
                solutions.TryAdd(key, canonicalIds);
                return;
            }
            if (depth >= CapabilitySchemaMaxDepth)
            {
                hitLimit = true;
                return;
            }

            var requirement = missing
                .OrderBy(static item => item.Kind, StringComparer.Ordinal)
                .ThenBy(static item => item.Field.Path, StringComparer.Ordinal)
                .First();
            var producerCandidates = FindCanonicalArtifactProducerCandidates(
                requirement.Kind,
                catalog,
                preferredProducerIds);
            foreach (var producer in producerCandidates)
                candidates.Add(producer.Id);
            foreach (var producer in producerCandidates)
                Explore(canonicalIds.Append(producer.Id).ToArray(), depth + 1);
        }

        Explore(initialCatalogIds, 0);
        return new ArtifactClosureSearchResult(
            solutions.Values.ToArray(),
            candidates.Order(StringComparer.Ordinal).Take(8).ToArray(),
            sawCycle,
            hitLimit);
    }

    internal static CapabilityArtifactRequirement[] GetMissingArtifactRequirements(
        IReadOnlyList<CapabilityCatalogEntry> selected)
        => selected
            .SelectMany(GetRequiredArtifactRequirements)
            .Where(item => !selected.Any(entry => CapabilityProducesArtifactKind(entry, item.Kind)))
            .GroupBy(static item => item.Kind, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

    internal static IReadOnlyList<CapabilityCatalogEntry> FindCanonicalArtifactProducerCandidates(
        string artifactKind, IReadOnlyList<CapabilityCatalogEntry> catalog, IReadOnlySet<string> preferredProducerIds)
    {
        var candidates = catalog
            .Where(static entry => string.Equals(entry.Resolution, "mcp", StringComparison.Ordinal))
            .Where(entry => CapabilityProducesArtifactKind(entry, artifactKind))
            .GroupBy(static entry => (entry.Resolution, entry.Server, entry.Kind, entry.Method))
            .SelectMany(group =>
            {
                var preferred = group.Where(entry => preferredProducerIds.Contains(entry.Id)).ToArray();
                if (preferred.Length > 0)
                    return preferred;
                var wholeTool = group.Where(static entry => entry.RequestBindings.Count == 0).Take(1).ToArray();
                return wholeTool.Length == 1 ? wholeTool : group;
            })
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        var preferredCandidates = candidates.Where(entry => preferredProducerIds.Contains(entry.Id)).ToArray();
        return preferredCandidates.Length > 0 ? preferredCandidates : candidates;
    }

    internal static bool HasArtifactDependencyCycle(IReadOnlyList<CapabilityCatalogEntry> selected)
    {
        var dependencies = selected.ToDictionary(
            static entry => entry.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var consumer in selected)
        {
            foreach (var requirement in GetRequiredArtifactRequirements(consumer))
            {
                foreach (var producer in selected.Where(entry => CapabilityProducesArtifactKind(entry, requirement.Kind)))
                    dependencies[consumer.Id].Add(producer.Id);
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (visited.Contains(id))
                return false;
            if (!visiting.Add(id))
                return true;
            foreach (var dependency in dependencies[id])
            {
                if (Visit(dependency))
                    return true;
            }
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }

        return dependencies.Keys.Any(Visit);
    }

    internal static IReadOnlySet<string> GetDeclaredUpstreamOperationIds(
        CapabilityInventoryOperation operation, IReadOnlyDictionary<string, CapabilityOperationMatch> matches)
    {
        var upstream = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(operation.InputOperationIds);
        while (pending.TryPop(out var operationId))
        {
            if (!upstream.Add(operationId) || !matches.TryGetValue(operationId, out var match))
                continue;
            foreach (var inputOperationId in match.Operation.InputOperationIds)
                pending.Push(inputOperationId);
        }
        return upstream;
    }

    internal static List<McpServerDiscovery> ExpandSelectedOperationalArtifactPrerequisites(
        IReadOnlyList<McpServerDiscovery>? selected, IReadOnlyList<McpServerDiscovery> complete, string userInstruction)
    {
        var result = selected?.Select(CloneDiscovery).ToList() ?? new List<McpServerDiscovery>();
        if (result.Count == 0 || complete.Count == 0)
            return result;

        // A prefilter is allowed to choose a high-level consumer without knowing that
        // one of its required arguments denotes an already materialized resource. Keep
        // every documented producer candidate visible so extraction can compose the
        // prerequisite instead of fabricating a public input or locator.
        for (var pass = 0; pass < CapabilitySchemaMaxDepth; pass++)
        {
            var missingKinds = result
                .SelectMany(static server => server.Tools)
                .SelectMany(GetRequiredArtifactRequirements)
                .Where(item => !SelectedDiscoveryProducesArtifactKind(result, item.Kind))
                .Select(static item => item.Kind)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missingKinds.Length == 0)
                break;

            var added = false;
            foreach (var kind in missingKinds)
            {
                var candidates = complete
                    .SelectMany(server => server.Tools.Select(tool => (Server: server, Tool: tool)))
                    .Where(item => ToolProducesArtifactKind(item.Tool, kind))
                    .Where(item => !ToolRequiresArtifactKind(item.Tool, kind))
                    .OrderBy(static item => item.Server.Name, StringComparer.Ordinal)
                    .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
                foreach (var candidate in candidates)
                    added |= AddToolToDiscovery(result, candidate.Server, candidate.Tool);
            }

            if (!added)
                break;
        }

        return result;
    }

    internal static bool SelectedDiscoveryProducesArtifactKind(
        IReadOnlyList<McpServerDiscovery> selected, string kind)
        => selected.SelectMany(static server => server.Tools)
            .Any(tool => ToolProducesArtifactKind(tool, kind)
                         && !ToolRequiresArtifactKind(tool, kind));

    internal static bool ToolProducesArtifactKind(McpToolInfo tool, string kind)
    {
        var contract = GetValidatedMcpArtifactContract(tool);
        return contract != null
            ? contract.Produces.Any(artifact =>
                string.Equals(artifact.Kind, kind, StringComparison.Ordinal)
                && string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal))
            : false;
    }

    internal static bool ToolRequiresArtifactKind(McpToolInfo tool, string kind)
    {
        var contract = GetValidatedMcpArtifactContract(tool);
        return contract != null
            ? contract.Consumes.Any(artifact =>
                artifact.Required && string.Equals(artifact.Kind, kind, StringComparison.Ordinal))
            : false;
    }

    internal static McpArtifactContract? GetValidatedMcpArtifactContract(
        McpToolInfo tool, string? serverName = null)
    {
        var validation = tool.ArtifactContract;
        if (validation == null)
            return null;
        if (validation.Errors.Count == 0)
            return validation.Contract;

        var identity = string.IsNullOrWhiteSpace(serverName)
            ? tool.Name
            : serverName + "/" + tool.Name;
        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"MCP tool '{identity}' advertises an invalid GnOuGo artifact contract.",
            details: new JsonObject
            {
                ["phase"] = "mcp_artifact_contract",
                ["server"] = serverName,
                ["tool"] = tool.Name,
                ["errors"] = new JsonArray(validation.Errors.Select(static error => (JsonNode)JsonValue.Create(error)!).ToArray())
            });
    }

    internal static McpCapabilityComposition? GetValidatedMcpCompositionContract(
        McpToolInfo tool, string? serverName = null)
    {
        var validation = tool.CompositionContract;
        if (validation == null)
            return null;
        if (validation.Errors.Count == 0 && validation.Contract is { } contract)
        {
            if (contract.Encapsulates.Any(capability =>
                    string.Equals(capability.Kind, "tool", StringComparison.Ordinal)
                    && string.Equals(capability.Method, tool.Name, StringComparison.Ordinal)))
            {
                validation = validation with
                {
                    Errors = ["A composition contract cannot encapsulate the declaring tool itself."]
                };
            }
            else
            {
                return contract;
            }
        }

        var identity = string.IsNullOrWhiteSpace(serverName)
            ? tool.Name
            : serverName + "/" + tool.Name;
        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"MCP tool '{identity}' advertises an invalid GnOuGo composition contract.",
            details: new JsonObject
            {
                ["phase"] = "mcp_composition_contract",
                ["server"] = serverName,
                ["tool"] = tool.Name,
                ["errors"] = new JsonArray(validation.Errors.Select(static error => (JsonNode)JsonValue.Create(error)!).ToArray())
            });
    }

    internal static bool AddToolToDiscovery(
        List<McpServerDiscovery> result, McpServerDiscovery source, McpToolInfo tool)
    {
        var index = result.FindIndex(server => string.Equals(server.Name, source.Name, StringComparison.Ordinal));
        var existing = index >= 0 ? result[index] : null;
        if (existing?.Tools.Any(item => string.Equals(item.Name, tool.Name, StringComparison.Ordinal)) == true)
            return false;

        var tools = (existing?.Tools ?? Array.Empty<McpToolInfo>()).ToList();
        tools.Add(tool);
        var merged = new McpServerDiscovery
        {
            Name = source.Name,
            Description = source.Description,
            CallTimeoutSeconds = source.CallTimeoutSeconds,
            Discovered = source.Discovered,
            Tools = tools,
            Prompts = existing?.Prompts ?? Array.Empty<McpPromptInfo>()
        };
        if (index >= 0)
            result[index] = merged;
        else
            result.Add(merged);
        return true;
    }

    internal static McpServerDiscovery CloneDiscovery(McpServerDiscovery source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        CallTimeoutSeconds = source.CallTimeoutSeconds,
        Discovered = source.Discovered,
        Tools = source.Tools,
        Prompts = source.Prompts
    };
}
