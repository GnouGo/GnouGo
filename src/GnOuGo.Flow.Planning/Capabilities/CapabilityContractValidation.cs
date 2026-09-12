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

using static GnOuGo.Flow.Planning.Capabilities.ArtifactProvenanceValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityArtifactValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCatalogBuilder;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractLocking;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.ConditionalActivationValidation;
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityContractValidation
{

    internal static void ValidateCapabilityConstraints(
        IReadOnlyList<CapabilityConstraint> constraints, IReadOnlyList<McpServerDiscovery> discovered)
    {
        foreach (var constraint in constraints)
        {
            foreach (var alternative in constraint.DeniedAlternatives)
            {
                var server = discovered.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
                var exists = server?.Discovered == true && (alternative.Kind == "prompt"
                    ? server.Prompts.Any(prompt => string.Equals(prompt.Name, alternative.Method, StringComparison.Ordinal))
                    : server.Tools.Any(tool => string.Equals(tool.Name, alternative.Method, StringComparison.Ordinal)));
                if (!exists || !AlternativeBindingsMatchSchema(alternative, server!))
                {
                    throw new WorkflowRuntimeException(
                        ErrorCodes.CapabilityPreflightInferenceFailed,
                        $"Capability constraint '{constraint.Id}' references an exact denied capability that was not discovered.");
                }
            }
        }
    }

    internal static void ValidateResolvedCapabilities(
        IReadOnlyList<ResolvedCapability> capabilities, IReadOnlyList<McpServerDiscovery> discovered, StepExecutionContext ctx, JsonObject input)
    {
        var unavailable = new List<ResolvedCapability>();
        var allowedNativeTypes = ResolveAllowedNativeStepTypes(ctx, input);
        foreach (var capability in capabilities)
        {
            if (!capability.Required)
                continue;

            if (capability.Resolution == "local")
                continue;

            if (capability.Resolution == "native")
            {
                if (string.IsNullOrWhiteSpace(capability.Method) || !allowedNativeTypes.Contains(capability.Method))
                    unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null, RequestBindings = Array.Empty<CapabilityRequestBinding>() });
                continue;
            }

            if (capability.Resolution != "mcp"
                || string.IsNullOrWhiteSpace(capability.Server)
                || capability.Kind is not ("tool" or "prompt")
                || string.IsNullOrWhiteSpace(capability.Method))
            {
                unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null, RequestBindings = Array.Empty<CapabilityRequestBinding>() });
                continue;
            }

            var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, capability.Server, StringComparison.Ordinal));
            var exists = server?.Discovered == true && (capability.Kind == "prompt"
                ? server.Prompts.Any(prompt => string.Equals(prompt.Name, capability.Method, StringComparison.Ordinal))
                : server.Tools.Any(tool => string.Equals(tool.Name, capability.Method, StringComparison.Ordinal)));
            var bindingsValid = capability.Kind != "tool" || server != null && AlternativeBindingsMatchSchema(
                new CapabilityAlternative(capability.Server!, capability.Kind, capability.Method!, capability.RequestBindings), server);
            if (!exists || !bindingsValid)
                unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null, RequestBindings = Array.Empty<CapabilityRequestBinding>() });
        }

        if (unavailable.Count > 0)
            ThrowCapabilityPreflightFailure(
                ErrorCodes.CapabilityPreflightUnavailable,
                "One or more required operations cannot be satisfied by the discovered MCP capabilities or allowed native steps.",
                Array.Empty<string>(),
                unavailable);
    }

    internal static void ThrowCapabilityPreflightFailure(
        string code, string message, IReadOnlyList<string> unavailableServers, IReadOnlyList<ResolvedCapability> unavailableCapabilities, string reason = "no_matching_discovered_capability")
    {
        var serverArray = new JsonArray();
        foreach (var server in unavailableServers)
            serverArray.Add((JsonNode?)JsonValue.Create(server));
        var capabilityArray = new JsonArray();
        foreach (var capability in unavailableCapabilities)
        {
            capabilityArray.Add((JsonNode)new JsonObject
            {
                ["id"] = capability.Id,
                ["description"] = capability.Description,
                ["required"] = capability.Required,
                ["reason"] = reason,
                ["operation_id"] = capability.OperationId,
                ["catalog_id"] = capability.CatalogId,
                ["resolution"] = capability.Resolution,
                ["server"] = capability.Server,
                ["kind"] = capability.Kind,
                ["method"] = capability.Method,
                ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings)
            });
        }

        throw new WorkflowRuntimeException(
            code,
            message,
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["unavailable_servers"] = serverArray,
                ["unavailable_capabilities"] = capabilityArray,
                ["planning_outcome"] = "unsupported",
                ["recommended_action"] = "configure_capability_or_revise_request"
            });
    }

    internal static string FormatResolvedCapabilityReference(ResolvedCapability capability)
    {
        var operationId = capability.OperationId ?? capability.Id;
        if (string.Equals(capability.Resolution, "native", StringComparison.Ordinal))
            return $"{operationId} -> native/{capability.Method}";

        var bindings = capability.RequestBindings.Count == 0
            ? string.Empty
            : $" [{FormatBindingsCompact(capability.RequestBindings)}]";
        return $"{operationId} -> {capability.Server}/{capability.Method}{bindings}";
    }

    internal static JsonObject BuildCapabilityPreflightJson(CapabilityPreflightResult preflight)
    {
        var capabilities = new JsonArray();
        foreach (var capability in preflight.Capabilities)
        {
            capabilities.Add((JsonNode)new JsonObject
            {
                ["id"] = capability.Id,
                ["description"] = capability.Description,
                ["required"] = capability.Required,
                ["resolution"] = capability.Resolution,
                ["operation_id"] = capability.OperationId,
                ["operation_ids"] = BuildStringArrayJson(GetResolvedCapabilityOperationIds(capability)),
                ["catalog_id"] = capability.CatalogId,
                ["input_operation_ids"] = BuildStringArrayJson(capability.InputOperationIds ?? Array.Empty<string>()),
                ["match_status"] = capability.MatchStatus,
                ["execution_kind"] = capability.ExecutionKind,
                ["external_effect_kind"] = capability.ExternalEffectKind,
                ["server"] = capability.Server,
                ["kind"] = capability.Kind,
                ["method"] = capability.Method,
                ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings),
                ["activation"] = capability.Activation == null
                    ? null
                    : new JsonObject
                    {
                        ["mode"] = capability.Activation.Mode,
                        ["group"] = capability.Activation.Group,
                        ["decision_operation_id"] = capability.Activation.DecisionOperationId,
                        ["decision_output_path"] = capability.Activation.DecisionOutputPath,
                        ["allowed_values"] = new JsonArray(capability.Activation.AllowedValues
                            .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                        ["no_effect_values"] = new JsonArray(capability.Activation.NoEffectValues
                            .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                        ["decision_contract_source"] = capability.Activation.DecisionContractSource,
                        ["decision_producer_catalog_id"] = capability.Activation.DecisionProducerCatalogId,
                        ["decision_input_operation_ids"] = new JsonArray(capability.Activation.DecisionInputOperationIds
                            .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                        ["branch_value"] = capability.Activation.BranchValue
                    }
            });
        }

        var constraints = new JsonArray();
        foreach (var constraint in preflight.Constraints)
        {
            var denied = new JsonArray();
            foreach (var alternative in constraint.DeniedAlternatives)
            {
                denied.Add((JsonNode)new JsonObject
                {
                    ["server"] = alternative.Server,
                    ["kind"] = alternative.Kind,
                    ["method"] = alternative.Method,
                    ["request_bindings"] = BuildRequestBindingsJson(alternative.RequestBindings)
                });
            }
            constraints.Add((JsonNode)new JsonObject
            {
                ["id"] = constraint.Id,
                ["description"] = constraint.Description,
                ["required"] = constraint.Required,
                ["denied_alternatives"] = denied
            });
        }

        return new JsonObject
        {
            ["effective_external_write_confirmation_policy"] = preflight.EffectiveExternalWriteConfirmationPolicy,
            ["external_write_confirmation_policy_source"] = preflight.ExternalWriteConfirmationPolicySource,
            ["capabilities"] = capabilities,
            ["constraints"] = constraints
        };
    }

    internal static void ValidateLockedCapabilitiesInDocument(
        WorkflowDocument document, CapabilityPreflightResult preflight, Action<string> validationStage, IReadOnlyDictionary<StepDef, ResolvedCapability> owners)
    {
        if (preflight.RequiredMcpCapabilities.Count == 0
            && preflight.RequiredNativeCapabilities.Count == 0
            && preflight.RequiredLocalOperations.Count == 0
            && preflight.Constraints.All(static constraint => !constraint.Required || constraint.DeniedAlternatives.Count == 0))
            return;

        var steps = document.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
            .ToArray();
        var calls = steps.Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)).ToArray();
        var missing = FindMissingMcpCapabilityInvocations(preflight.RequiredMcpCapabilities, calls)
            .Concat(FindMissingNativeCapabilityInvocations(preflight.RequiredNativeCapabilities, steps)).ToArray();

        if (missing.Length > 0)
        {
            var missingSummary = string.Join(", ", missing.Select(FormatResolvedCapabilityReference));
            ThrowCapabilityPreflightFailure(
                ErrorCodes.CapabilityPreflightUnavailable,
                $"Generated workflow omitted required capabilities locked by capability preflight: {missingSummary}.",
                Array.Empty<string>(),
                missing,
                "generated_required_capability_omitted");
        }

        ValidateNoRedundantArtifactMaterializers(preflight, calls);
        ValidateMcpArtifactDataflow(document, preflight);
        validationStage.Invoke(GnOuGo.Flow.Core.Planning.PlanningValidationStage.ConditionalActivation);
        ValidateConditionalCapabilityActivation(document, preflight, owners);

        var deniedCalls = preflight.Constraints
            .Where(static constraint => constraint.Required)
            .SelectMany(static constraint => constraint.DeniedAlternatives.Select(alternative => (constraint, alternative)))
            .Where(item => calls.Any(step => McpStepMatchesCapability(
                step,
                item.alternative.Server,
                item.alternative.Kind,
                item.alternative.Method,
                item.alternative.RequestBindings)))
            .ToArray();
        if (deniedCalls.Length > 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightUnavailable,
                "Generated workflow invoked one or more capabilities denied by locked task constraints.",
                details: new JsonObject
                {
                    ["phase"] = "capability_preflight",
                    ["violated_constraints"] = new JsonArray(deniedCalls
                        .Select(static item => (JsonNode)new JsonObject
                        {
                            ["id"] = item.constraint.Id,
                            ["description"] = item.constraint.Description,
                            ["server"] = item.alternative.Server,
                            ["kind"] = item.alternative.Kind,
                            ["method"] = item.alternative.Method,
                            ["request_bindings"] = BuildRequestBindingsJson(item.alternative.RequestBindings)
                        }).ToArray())
                });
        }
    }

    internal static bool HasHumanDecisionSource(CapabilityInventory inventory, CapabilityInventoryOperation operation) =>
        inventory.Operations.Any(source => source.Id == operation.DecisionSourceOperationId && source.ExecutionKind == "human_interaction");
}
