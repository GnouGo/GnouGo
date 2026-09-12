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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityPolicy
{

    internal static void ValidateDiscoveredArtifactContracts(
        IReadOnlyList<McpServerDiscovery> discovered)
    {
        foreach (var server in discovered)
        {
            foreach (var tool in server.Tools)
            {
                _ = GetValidatedMcpArtifactContract(tool, server.Name);
                _ = GetValidatedMcpCompositionContract(tool, server.Name);
            }
        }
    }

    internal static IReadOnlyList<CapabilityRequirement> ParseExplicitCapabilityRequirements(JsonArray? requirements)
    {
        if (requirements == null || requirements.Count == 0)
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight explicit mode requires at least one requirement.");

        var parsed = new List<CapabilityRequirement>(requirements.Count);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in requirements)
        {
            if (node is not JsonObject requirement)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Each capability_preflight requirement must be an object.");

            var id = requirement["id"]?.GetValue<string>()?.Trim();
            var description = requirement["description"]?.GetValue<string>()?.Trim();
            var required = requirement["required"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Capability requirement ids must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(description))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Capability requirement '{id}' requires a non-empty description.");

            var alternatives = ParseCapabilityAlternatives(
                requirement["alternatives"] as JsonArray,
                $"Capability requirement '{id}'");

            if (required && alternatives.Count == 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Required capability '{id}' must declare at least one alternative.");

            parsed.Add(new CapabilityRequirement(id, description, required, alternatives));
        }

        return parsed;
    }

    internal static IReadOnlyList<CapabilityConstraint> ParseCapabilityConstraints(JsonArray? constraints)
    {
        if (constraints == null || constraints.Count == 0)
            return Array.Empty<CapabilityConstraint>();

        var parsed = new List<CapabilityConstraint>(constraints.Count);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in constraints)
        {
            if (node is not JsonObject constraint)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Each capability_preflight constraint must be an object.");

            var id = constraint["id"]?.GetValue<string>()?.Trim();
            var description = constraint["description"]?.GetValue<string>()?.Trim();
            var required = constraint["required"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Capability constraint ids must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(description))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Capability constraint '{id}' requires a non-empty description.");

            var denied = ParseCapabilityAlternatives(
                constraint["denied_alternatives"] as JsonArray,
                $"Capability constraint '{id}'");
            parsed.Add(new CapabilityConstraint(id, description, required, denied));
        }

        return parsed;
    }

    internal static IReadOnlyList<CapabilityAlternative> ParseCapabilityAlternatives(JsonArray? nodes, string owner)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<CapabilityAlternative>();

        var alternatives = new List<CapabilityAlternative>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not JsonObject alternative)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} contains an invalid capability alternative.");
            var server = alternative["server"]?.GetValue<string>()?.Trim();
            var kind = alternative["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            var method = alternative["method"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(server) || kind is not ("tool" or "prompt") || string.IsNullOrWhiteSpace(method))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} alternatives require server, tool|prompt kind, and method.");
            var bindings = ParseCapabilityRequestBindings(
                alternative["request_bindings"] as JsonArray,
                $"{owner} alternative '{server}/{method}'");
            if (kind == "prompt" && bindings.Count > 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} prompt alternatives cannot declare request_bindings.");
            alternatives.Add(new CapabilityAlternative(server, kind, method, bindings));
        }

        return alternatives;
    }

    internal static IReadOnlyList<CapabilityRequestBinding> ParseCapabilityRequestBindings(JsonArray? nodes, string owner)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<CapabilityRequestBinding>();

        var result = new List<CapabilityRequestBinding>(nodes.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is not JsonObject binding)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} contains an invalid request binding.");
            var path = binding["path"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !IsValidJsonPointer(path) || !paths.Add(path))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} request binding paths must be unique RFC 6901 JSON Pointers.");
            if (!binding.ContainsKey("value") || !IsJsonScalar(binding["value"]))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} request binding '{path}' must contain a JSON scalar value.");
            result.Add(new CapabilityRequestBinding(path, binding["value"]?.DeepClone()));
        }

        return result;
    }

    internal static IReadOnlyList<ResolvedCapability> ResolveExplicitCapabilities(
        IReadOnlyList<CapabilityRequirement> requirements, IReadOnlyList<McpServerDiscovery> discovered)
    {
        var resolved = new List<ResolvedCapability>(requirements.Count);
        foreach (var requirement in requirements)
        {
            CapabilityAlternative? match = null;
            foreach (var alternative in requirement.Alternatives)
            {
                var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
                if (server == null || !server.Discovered)
                    continue;
                var exists = alternative.Kind == "prompt"
                    ? server.Prompts.Any(prompt => string.Equals(prompt.Name, alternative.Method, StringComparison.Ordinal))
                    : server.Tools.Any(tool => string.Equals(tool.Name, alternative.Method, StringComparison.Ordinal));
                if (exists && AlternativeBindingsMatchSchema(alternative, server))
                {
                    match = alternative;
                    break;
                }
            }

            resolved.Add(match == null
                ? new ResolvedCapability(requirement.Id, requirement.Description, requirement.Required, "unavailable", null, null, null, Array.Empty<CapabilityRequestBinding>())
                : new ResolvedCapability(requirement.Id, requirement.Description, requirement.Required, "mcp", match.Server, match.Kind, match.Method, match.RequestBindings));
        }

        return resolved;
    }

    internal static void ValidateExplicitCapabilityBindings(
        IReadOnlyList<CapabilityRequirement> requirements, IReadOnlyList<CapabilityConstraint> constraints, IReadOnlyList<McpServerDiscovery> discovered)
    {
        var alternatives = requirements.SelectMany(static requirement => requirement.Alternatives)
            .Concat(constraints.SelectMany(static constraint => constraint.DeniedAlternatives));
        foreach (var alternative in alternatives.Where(static item => item.RequestBindings.Count > 0))
        {
            var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
            if (server?.Discovered != true)
                continue;
            if (!AlternativeBindingsMatchSchema(alternative, server))
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.InputValidation,
                    $"Capability alternative '{alternative.Server}/{alternative.Method}' contains request_bindings that are not documented scalar selectors in the discovered input schema.",
                    details: new JsonObject
                    {
                        ["phase"] = "capability_preflight",
                        ["server"] = alternative.Server,
                        ["kind"] = alternative.Kind,
                        ["method"] = alternative.Method,
                        ["request_bindings"] = BuildRequestBindingsJson(alternative.RequestBindings)
                    });
            }
        }
    }

    internal static bool HasExactDiscoveredAlternative(
        CapabilityRequirement requirement, IReadOnlyList<McpServerDiscovery> discovered)
    {
        foreach (var alternative in requirement.Alternatives)
        {
            var server = discovered.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
            if (server?.Discovered != true)
                continue;
            var exists = alternative.Kind == "prompt"
                ? server.Prompts.Any(prompt => string.Equals(prompt.Name, alternative.Method, StringComparison.Ordinal))
                : server.Tools.Any(tool => string.Equals(tool.Name, alternative.Method, StringComparison.Ordinal));
            if (exists && AlternativeBindingsMatchSchema(alternative, server))
                return true;
        }
        return false;
    }
}
