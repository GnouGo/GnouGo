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
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityArtifactValidation
{

    internal static void ValidateNoRedundantArtifactMaterializers(
        CapabilityPreflightResult preflight, IReadOnlyList<StepDef> calls)
    {
        var materializers = preflight.DiscoveredServers
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Tool: tool)))
            .Where(item => GetValidatedMcpArtifactContract(item.Tool, item.Server)?.Produces.Any(artifact =>
                string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal)) == true)
            .ToDictionary(
                static item => (item.Server, item.Tool.Name),
                static item => item.Tool,
                EqualityComparer<(string Server, string Name)>.Default);
        if (materializers.Count == 0)
            return;

        var remainingAllowances = preflight.RequiredMcpCapabilities
            .Where(capability => materializers.ContainsKey((capability.Server!, capability.Method!)))
            .ToList();
        var redundant = new List<JsonObject>();
        foreach (var call in calls)
        {
            var server = ReadMcpCallInputString(call, "server");
            var kind = ReadMcpCallInputString(call, "kind") ?? "tool";
            if (string.IsNullOrWhiteSpace(server) || !string.Equals(kind, "tool", StringComparison.Ordinal))
                continue;

            var methods = new List<string>();
            var method = ReadMcpCallInputString(call, "method");
            if (!string.IsNullOrWhiteSpace(method))
                methods.Add(method);
            if (call.Input?["methods"] is JsonArray methodArray)
            {
                methods.AddRange(methodArray
                    .OfType<JsonValue>()
                    .Select(static value => value.TryGetValue<string>(out var candidate) ? candidate : null)
                    .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Select(static candidate => candidate!));
            }

            foreach (var candidate in methods.Distinct(StringComparer.Ordinal))
            {
                if (!materializers.ContainsKey((server, candidate)))
                    continue;

                var allowanceIndex = remainingAllowances.FindIndex(capability =>
                    string.Equals(capability.Server, server, StringComparison.Ordinal)
                    && string.Equals(capability.Method, candidate, StringComparison.Ordinal)
                    && McpStepMatchesCapability(
                        call,
                        capability.Server!,
                        capability.Kind!,
                        capability.Method!,
                        capability.RequestBindings));
                if (allowanceIndex >= 0)
                {
                    remainingAllowances.RemoveAt(allowanceIndex);
                    continue;
                }

                redundant.Add(new JsonObject
                {
                    ["step_id"] = call.Id,
                    ["server"] = server,
                    ["method"] = candidate
                });
            }
        }

        if (redundant.Count == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightRedundantArtifactProducer,
            "Generated workflow contains an MCP artifact materializer with no corresponding locked capability occurrence.",
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["reason"] = "redundant_artifact_materializer",
                ["redundant_calls"] = new JsonArray(redundant.Select(static item => (JsonNode)item).ToArray())
            });
    }

    internal static IReadOnlyList<ResolvedCapability> FindMissingMcpCapabilityInvocations(
        IReadOnlyList<ResolvedCapability> required, IReadOnlyList<StepDef> calls)
    {
        var remaining = calls.ToList();
        var missing = new List<ResolvedCapability>();
        foreach (var capability in required)
        {
            var index = remaining.FindIndex(step => McpStepMatchesCapability(
                step, capability.Server!, capability.Kind!, capability.Method!, capability.RequestBindings));
            if (index < 0)
                missing.Add(capability);
            else
                remaining.RemoveAt(index);
        }
        return missing;
    }

    internal static IReadOnlyList<ResolvedCapability> FindMissingNativeCapabilityInvocations(
        IReadOnlyList<ResolvedCapability> required, IReadOnlyList<StepDef> steps)
    {
        var remaining = steps.ToList();
        var missing = new List<ResolvedCapability>();
        foreach (var capability in required)
        {
            var index = remaining.FindIndex(step => string.Equals(step.Type, capability.Method, StringComparison.Ordinal));
            if (index < 0)
                missing.Add(capability);
            else
                remaining.RemoveAt(index);
        }
        return missing;
    }

    internal static bool McpStepMatchesCapability(
        StepDef step, string server, string kind, string method, IReadOnlyList<CapabilityRequestBinding> bindings)
    {
        if (!string.Equals(ReadMcpCallInputString(step, "server"), server, StringComparison.Ordinal)
            || !string.Equals(ReadMcpCallInputString(step, "kind") ?? "tool", kind, StringComparison.Ordinal))
            return false;
        var methodMatches = string.Equals(ReadMcpCallInputString(step, "method"), method, StringComparison.Ordinal)
                            || step.Input?["methods"] is JsonArray methods
                            && methods.Any(node => node is JsonValue value
                                                   && value.TryGetValue<string>(out var candidate)
                                                   && string.Equals(candidate, method, StringComparison.Ordinal));
        return methodMatches && RequestContainsLiteralBindings(step.Input?["request"], bindings);
    }
}
