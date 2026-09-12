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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.DecisionProvenanceValidation;
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class ConditionalActivationValidation
{

    internal static void ValidateConditionalCapabilityActivation(
        WorkflowDocument document, CapabilityPreflightResult preflight, IReadOnlyDictionary<StepDef, ResolvedCapability> owners)
    {
        var groups = preflight.RequiredMcpCapabilities
            .Where(static capability => capability.Activation is not null)
            .GroupBy(static capability => capability.Activation!.Group, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length == 0)
            return;

        var workflowSteps = document.Workflows
            .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Select(step => (Workflow: workflow.Key, Step: step)))
            .ToArray();
        var allSteps = workflowSteps.Select(static item => item.Step).ToArray();
        var allCalls = allSteps.Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)).ToArray();

        foreach (var group in groups)
        {
            var capabilities = group.ToArray();
            var activationModes = capabilities
                .Select(static capability => capability.Activation!.Mode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var activationMode = activationModes.Length == 1 ? activationModes[0] : string.Empty;
            var declaredAllowedValues = capabilities[0].Activation?.AllowedValues
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            var declaredNoEffectValues = capabilities[0].Activation?.NoEffectValues
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            var branchValues = capabilities.Select(static capability => capability.Activation!.BranchValue)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var topologyValid = activationMode switch
            {
                ConditionalExactlyOneActivationMode => branchValues.Length == capabilities.Length,
                ConditionalAllOnValueActivationMode => branchValues.Length == 1 && declaredNoEffectValues.Length > 0,
                _ => false
            };
            if (!topologyValid
                || capabilities.Any(static capability => string.IsNullOrWhiteSpace(capability.Activation?.DecisionOutputPath))
                || capabilities.Select(static capability => capability.Activation!.DecisionOutputPath)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || capabilities.Select(static capability => capability.Activation!.DecisionContractSource)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || capabilities.Select(static capability => capability.Activation!.DecisionProducerCatalogId)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || declaredNoEffectValues.Any(value => branchValues.Contains(value, StringComparer.Ordinal))
                || !branchValues.Concat(declaredNoEffectValues).Order(StringComparer.Ordinal)
                    .SequenceEqual(declaredAllowedValues, StringComparer.Ordinal))
            {
                ThrowInvalidConditionalActivation(group.Key,
                    "The locked conditional capability group is malformed or has an invalid activation topology.",
                    capabilities,
                    validationIssue: "conditional_capability_contract_invalid",
                    repairScope: "capability_contract");
            }

            var matchingCalls = allCalls.Where(call => capabilities.Any(capability => McpStepMatchesCapability(
                call,
                capability.Server!,
                capability.Kind!,
                capability.Method!,
                capability.RequestBindings))).ToArray();
            var mutatingDefault = workflowSteps
                .Where(static item => string.Equals(item.Step.Type, "switch", StringComparison.Ordinal))
                .Select(item => EvaluateMutatingConditionalDefault(
                    item.Workflow,
                    item.Step,
                    matchingCalls,
                    call => preflight.RequiredMcpCapabilities
                        .Where(static capability => capability.ExternalEffectKind is "write" or "lifecycle")
                        .Any(capability => McpStepMatchesCapability(
                            call,
                            capability.Server!,
                            capability.Kind!,
                            capability.Method!,
                            capability.RequestBindings))))
                .FirstOrDefault(static evaluation => evaluation != null);
            if (mutatingDefault != null)
            {
                ThrowInvalidConditionalActivation(
                    group.Key,
                    mutatingDefault.Message,
                    capabilities,
                    mutatingDefault.ValidationIssue,
                    mutatingDefault.RepairScope,
                    mutatingDefault.Workflow,
                    mutatingDefault.SwitchId);
            }

            var evaluations = workflowSteps
                .Where(static item => string.Equals(item.Step.Type, "switch", StringComparison.Ordinal))
                .Select(item =>
                {
                    var candidateCalls = EnumerateConditionalSwitchCalls(item.Step)
                        .Where(call => capabilities.Any(capability => (owners.TryGetValue(call, out var owner) && ReferenceEquals(owner, capability)) && McpStepMatchesCapability(
                            call,
                            capability.Server!,
                            capability.Kind!,
                            capability.Method!,
                            capability.RequestBindings)))
                        .ToArray();
                    return new ConditionalSwitchCandidate(
                        EvaluateConditionalSwitch(
                            document,
                            item.Workflow,
                            item.Step,
                            capabilities,
                            candidateCalls,
                            preflight, owners),
                        candidateCalls);
                })
                .ToArray();
            var validSwitches = evaluations.Where(static candidate => candidate.Evaluation.IsValid).ToArray();
            if (validSwitches.Length != 1)
            {
                var failure = evaluations
                    .Select(static candidate => candidate.Evaluation)
                    .Where(static evaluation => evaluation.ContainedGroupCallCount > 0)
                    .OrderByDescending(static evaluation => evaluation.ContainedGroupCallCount)
                    .ThenByDescending(static evaluation => evaluation.ValidationProgress)
                    .FirstOrDefault();
                ThrowInvalidConditionalActivation(group.Key,
                    validSwitches.Length == 0
                        ? failure?.Message
                          ?? "Conditional capabilities must be placed in the declared cases of one expression-based switch with no mutating default branch."
                        : "Conditional capabilities were associated with more than one switch.",
                    capabilities,
                    validationIssue: validSwitches.Length == 0
                        ? failure?.ValidationIssue ?? "conditional_switch_missing"
                        : "conditional_switch_ambiguous",
                    repairScope: validSwitches.Length == 0
                        ? failure?.RepairScope ?? "leaf_topology"
                        : "workflow_topology",
                    workflow: validSwitches.Length == 0 ? failure?.Workflow : null,
                    switchId: validSwitches.Length == 0 ? failure?.SwitchId : null);
            }

            var selectedConditionalCalls = validSwitches[0].Calls
                .ToHashSet(ReferenceEqualityComparer.Instance);
            var otherCapabilities = preflight.RequiredMcpCapabilities
                .Where(capability => !string.Equals(
                    capability.Activation?.Group,
                    group.Key,
                    StringComparison.Ordinal))
                .ToArray();
            var otherCalls = allCalls
                .Where(call => !selectedConditionalCalls.Contains(call))
                .ToArray();
            var attributedOtherCalls = AttributeCapabilityCalls(otherCapabilities, otherCalls, owners);
            var unownedMatchingCalls = matchingCalls
                .Where(call => !selectedConditionalCalls.Contains(call)
                               && !attributedOtherCalls.Contains(call))
                .ToArray();
            if (unownedMatchingCalls.Length > 0)
            {
                var callOwners = workflowSteps
                    .Where(item => unownedMatchingCalls.Contains(item.Step, ReferenceEqualityComparer.Instance))
                    .Select(static item => item.Workflow)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                ThrowInvalidConditionalActivation(group.Key,
                    "Every conditional capability must occur exactly once, and every physically identical call outside its switch must belong to another locked operation.",
                    capabilities,
                    validationIssue: "conditional_call_cardinality_invalid",
                    repairScope: callOwners.Length == 1 ? "leaf_topology" : "workflow_topology",
                    workflow: callOwners.Length == 1 ? callOwners[0] : null);
            }
        }
    }

    internal static IEnumerable<StepDef> EnumerateConditionalSwitchCalls(StepDef step)
        => (step.Cases ?? [])
            .SelectMany(static @case => EnumerateSteps(@case.Steps))
            .Concat(EnumerateSteps(step.Default ?? []))
            .Where(static candidate => string.Equals(candidate.Type, "mcp.call", StringComparison.Ordinal));

    internal static IReadOnlySet<StepDef> AttributeCapabilityCalls(
        IReadOnlyList<ResolvedCapability> capabilities, IReadOnlyList<StepDef> calls, IReadOnlyDictionary<StepDef, ResolvedCapability> owners)
    {
        var capabilityByCall = Enumerable.Repeat(-1, calls.Count).ToArray();
        for (var capabilityIndex = 0; capabilityIndex < capabilities.Count; capabilityIndex++)
        {
            var visitedCalls = new bool[calls.Count];
            _ = TryAttributeCapabilityCall(
                capabilityIndex,
                capabilities,
                calls,
                capabilityByCall,
                visitedCalls, owners);
        }

        var attributed = new HashSet<StepDef>(ReferenceEqualityComparer.Instance);
        for (var callIndex = 0; callIndex < calls.Count; callIndex++)
        {
            if (capabilityByCall[callIndex] >= 0)
                attributed.Add(calls[callIndex]);
        }
        return attributed;
    }

    internal static bool TryAttributeCapabilityCall(
        int capabilityIndex, IReadOnlyList<ResolvedCapability> capabilities, IReadOnlyList<StepDef> calls, int[] capabilityByCall, bool[] visitedCalls, IReadOnlyDictionary<StepDef, ResolvedCapability> owners)
    {
        var capability = capabilities[capabilityIndex];
        for (var callIndex = 0; callIndex < calls.Count; callIndex++)
        {
            if (visitedCalls[callIndex]
                || (!owners.TryGetValue(calls[callIndex], out var owner) || !ReferenceEquals(owner, capability))
                || !McpStepMatchesCapability(
                    calls[callIndex],
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    capability.RequestBindings))
            {
                continue;
            }

            visitedCalls[callIndex] = true;
            if (capabilityByCall[callIndex] < 0
                || TryAttributeCapabilityCall(
                    capabilityByCall[callIndex],
                    capabilities,
                    calls,
                    capabilityByCall,
                    visitedCalls, owners))
            {
                capabilityByCall[callIndex] = capabilityIndex;
                return true;
            }
        }
        return false;
    }

    internal static ConditionalSwitchEvaluation EvaluateConditionalSwitch(
        WorkflowDocument document, string workflowName, StepDef step, IReadOnlyList<ResolvedCapability> capabilities, IReadOnlyList<StepDef> groupCalls, CapabilityPreflightResult preflight, IReadOnlyDictionary<StepDef, ResolvedCapability> owners)
    {
        var structure = EvaluateConditionalSwitchStructure(
            workflowName,
            step,
            capabilities,
            groupCalls,
            (call, capability) => (owners.TryGetValue(call, out var owner) && ReferenceEquals(owner, capability)) && McpStepMatchesCapability(
                call,
                capability.Server!,
                capability.Kind!,
                capability.Method!,
                capability.RequestBindings),
            static capability => capability.Activation!,
            call => preflight.RequiredMcpCapabilities
                .Where(static capability => capability.ExternalEffectKind is "write" or "lifecycle")
                .Any(capability => McpStepMatchesCapability(
                    call,
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    capability.RequestBindings)));
        if (!structure.IsValid)
            return structure;

        var activation = capabilities[0].Activation!;
        var decisionOperationId = activation.DecisionOperationId;
        var decisionCapabilities = preflight.Capabilities
            .Where(capability => string.Equals(capability.OperationId, decisionOperationId, StringComparison.Ordinal)
                                 && string.Equals(
                                     capability.CatalogId,
                                     activation.DecisionProducerCatalogId,
                                     StringComparison.Ordinal))
            .ToArray();
        if (decisionCapabilities.Length == 0)
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_decision_producer_missing",
                RepairScope = "decision_producer",
                Message = "The declared conditional decision producer is not present in the generated workflow.",
                ValidationProgress = 70
            };
        }

        var decisionSteps = groupCalls
            .Concat(step.Cases!.SelectMany(static @case => EnumerateSteps(@case.Steps)))
            .Concat(EnumerateSteps(step.Default ?? []))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var sources = document.Workflows
            .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Where(candidate => !decisionSteps.Contains(candidate)
                                    && MatchesLocalDecisionIdentity(candidate, activation)
                                    && decisionCapabilities.Any(capability => (owners.TryGetValue(candidate, out var owner) && ReferenceEquals(owner, capability)) && StepMatchesDecisionProducer(
                                        candidate,
                                        capability)))
                .Select(candidate => (Workflow: workflow.Key, Step: candidate)))
            .ToArray();
        if (sources.Length == 0)
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_decision_producer_missing",
                RepairScope = "decision_producer",
                Message = "The conditional switch has no generated step matching its declared decision producer.",
                ValidationProgress = 80
            };
        }

        if (string.Equals(
                activation.DecisionContractSource,
                LocalDecisionContractSource,
                StringComparison.Ordinal)
            && !LocalDecisionConditionsCoverLockedInputs(document, sources, activation, preflight))
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_local_decision_inputs_unproven",
                RepairScope = "decision_producer",
                Message = "The local decision evaluator must contain every locked decision field and derive its boolean conditions from every declared upstream operation.",
                ValidationProgress = 85
            };
        }

        if (!ConditionalDecisionExpressionDependsOnSource(
                document,
                BuildWorkflowArtifactCallerIndex(document),
                sources,
                workflowName,
                step.Expr!,
                [],
                activation))
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_decision_lineage_unproven",
                RepairScope = "main_decision_routing",
                Message = "The switch shape is valid, but its discriminator does not trace unchanged through declared workflow inputs and transparent projections to the locked decision producer.",
                ValidationProgress = 90
            };
        }

        return structure with { ValidationProgress = 100 };
    }

    internal static ConditionalSwitchEvaluation EvaluateConditionalSwitchStructure<TCapability>(
        string workflowName, StepDef step, IReadOnlyList<TCapability> capabilities, IReadOnlyList<StepDef> groupCalls, Func<StepDef, TCapability, bool> matchesCapability, Func<TCapability, McpCapabilityActivation> getActivation, Func<StepDef, bool> isMutatingCall)
    {
        var nestedCalls = (step.Cases ?? [])
            .SelectMany(static @case => EnumerateSteps(@case.Steps))
            .Concat(EnumerateSteps(step.Default ?? []))
            .Where(static candidate => string.Equals(candidate.Type, "mcp.call", StringComparison.Ordinal))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var containedGroupCallCount = groupCalls.Count(nestedCalls.Contains);
        var initial = new ConditionalSwitchEvaluation(
            false,
            "conditional_switch_shape_invalid",
            "leaf_topology",
            "The conditional capability group requires one expression-based switch with literal value cases.",
            workflowName,
            step.Id,
            containedGroupCallCount,
            10);

        if (string.IsNullOrWhiteSpace(step.Expr)
            || step.Cases == null
            || step.Cases.Any(static @case => !string.IsNullOrWhiteSpace(@case.When)))
        {
            return initial;
        }

        var activation = getActivation(capabilities[0]);
        if (!(activation.DecisionContractSource == PlanningDecisionContract.HumanConfirmation
                ? ConfirmationDecisionExpression.TryRead(step.Expr, activation.BranchValue, activation.NoEffectValues.Single(), out _)
                : ConditionalDecisionExpressionMatchesDeclaredPath(step.Expr, activation.DecisionOutputPath)))
        {
            return initial with
            {
                ValidationIssue = "conditional_decision_lineage_unproven",
                RepairScope = "main_decision_routing",
                Message = "The switch discriminator does not preserve the declared decision boundary field name.",
                ValidationProgress = 20
            };
        }

        var matchedCalls = new HashSet<StepDef>(ReferenceEqualityComparer.Instance);
        foreach (var capability in capabilities)
        {
            var capabilityActivation = getActivation(capability);
            var matchingCases = step.Cases.Where(@case =>
                    string.Equals(@case.Value, capabilityActivation.BranchValue, StringComparison.Ordinal)
                    && EnumerateSteps(@case.Steps).Any(call =>
                        string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                        && matchesCapability(call, capability)))
                .ToArray();
            if (matchingCases.Length != 1)
            {
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "Each conditional capability must occur in exactly one case whose literal value matches its declared branch value.",
                    ValidationProgress = 30
                };
            }

            var calls = EnumerateSteps(matchingCases[0].Steps)
                .Where(call => string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                               && matchesCapability(call, capability))
                .ToArray();
            if (calls.Length != 1)
            {
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "Each conditional case must contain exactly one occurrence of its matching capability.",
                    ValidationProgress = 30
                };
            }
            matchedCalls.Add(calls[0]);
        }

        if (matchedCalls.Count != groupCalls.Count || groupCalls.Any(call => !matchedCalls.Contains(call)))
        {
            return initial with
            {
                ValidationIssue = "conditional_branch_placement_invalid",
                Message = "Conditional capability calls must not occur outside their one declared switch case.",
                ValidationProgress = 40
            };
        }

        var activationMode = activation.Mode;
        if (string.Equals(activationMode, ConditionalAllOnValueActivationMode, StringComparison.Ordinal))
        {
            var effectValue = activation.BranchValue;
            var effectCases = step.Cases.Where(@case => string.Equals(
                @case.Value,
                effectValue,
                StringComparison.Ordinal)).ToArray();
            if (effectCases.Length != 1)
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "The ordered conditional composition must have exactly one declared effect case.",
                    ValidationProgress = 40
                };
            var orderedCalls = EnumerateSteps(effectCases[0].Steps)
                .Where(candidate => groupCalls.Contains(candidate, ReferenceEqualityComparer.Instance))
                .ToArray();
            if (orderedCalls.Length != capabilities.Count)
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "The ordered conditional composition does not contain every declared capability exactly once.",
                    ValidationProgress = 40
                };
            for (var index = 0; index < capabilities.Count; index++)
            {
                var capability = capabilities[index];
                if (!matchesCapability(orderedCalls[index], capability))
                {
                    return initial with
                    {
                        ValidationIssue = "conditional_branch_order_invalid",
                        Message = "The ordered conditional composition does not preserve its declared capability order.",
                        ValidationProgress = 40
                    };
                }
            }
        }

        var allowedValues = activation.AllowedValues.ToHashSet(StringComparer.Ordinal);
        if (step.Cases.Any(@case => string.IsNullOrWhiteSpace(@case.Value) || !allowedValues.Contains(@case.Value)))
        {
            return initial with
            {
                ValidationIssue = "conditional_case_value_invalid",
                Message = "Every switch case must use one literal value from the declared decision enum.",
                ValidationProgress = 50
            };
        }
        if (allowedValues.Any(value => step.Cases.Count(@case => string.Equals(
                @case.Value,
                value,
                StringComparison.Ordinal)) != 1))
        {
            return initial with
            {
                ValidationIssue = "conditional_case_coverage_invalid",
                Message = "The switch must contain exactly one literal case for every declared decision value.",
                ValidationProgress = 50
            };
        }

        foreach (var noEffectValue in activation.NoEffectValues)
        {
            var noEffectCases = step.Cases.Where(@case => string.Equals(
                @case.Value,
                noEffectValue,
                StringComparison.Ordinal)).ToArray();
            if (noEffectCases.Length != 1
                || EnumerateSteps(noEffectCases[0].Steps).Any(call =>
                    string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                    && isMutatingCall(call)))
            {
                return initial with
                {
                    ValidationIssue = "conditional_no_effect_branch_mutates",
                    Message = "Every declared no-effect value must have exactly one case containing no write or lifecycle capability.",
                    ValidationProgress = 60
                };
            }
        }

        if (EnumerateSteps(step.Default ?? []).Any(call =>
                string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                && isMutatingCall(call)))
        {
            return initial with
            {
                ValidationIssue = "conditional_default_mutates",
                Message = "The conditional switch default branch must not execute a write or lifecycle capability.",
                ValidationProgress = 60
            };
        }

        return initial with
        {
            IsValid = true,
            ValidationIssue = string.Empty,
            Message = string.Empty,
            ValidationProgress = 70
        };
    }

    internal static ConditionalSwitchEvaluation? EvaluateMutatingConditionalDefault(
        string workflowName, StepDef step, IReadOnlyList<StepDef> groupCalls, Func<StepDef, bool> isMutatingCall)
    {
        var nestedCalls = (step.Cases ?? [])
            .SelectMany(static @case => EnumerateSteps(@case.Steps))
            .Concat(EnumerateSteps(step.Default ?? []))
            .Where(static candidate => string.Equals(candidate.Type, "mcp.call", StringComparison.Ordinal))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var containedGroupCallCount = groupCalls.Count(nestedCalls.Contains);
        if (containedGroupCallCount == 0
            || !EnumerateSteps(step.Default ?? []).Any(call =>
                string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                && isMutatingCall(call)))
        {
            return null;
        }

        return new ConditionalSwitchEvaluation(
            false,
            "conditional_default_mutates",
            "leaf_topology",
            "The conditional switch default branch must not execute a write or lifecycle capability.",
            workflowName,
            step.Id,
            containedGroupCallCount,
            60);
    }

    internal static void ThrowInvalidConditionalActivation(
        string group, string reason, IReadOnlyList<ResolvedCapability> capabilities, string validationIssue, string repairScope, string? workflow = null, string? switchId = null)
        => throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"Generated workflow does not safely implement conditional capability group '{group}': {reason}",
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["reason"] = "conditional_activation_invalid",
                ["validation_issue"] = validationIssue,
                ["repair_scope"] = repairScope,
                ["activation_group"] = group,
                ["workflow"] = workflow,
                ["switch_id"] = switchId,
                ["decision_operation_id"] = capabilities
                    .Select(static capability => capability.Activation?.DecisionOperationId)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["decision_field"] = capabilities
                    .Select(static capability => GetDecisionBoundaryFieldName(
                        capability.Activation?.DecisionOutputPath ?? string.Empty))
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["message"] = reason,
                ["branches"] = new JsonArray(capabilities.Select(capability => (JsonNode)new JsonObject
                {
                    ["branch_value"] = capability.Activation?.BranchValue,
                    ["operation_id"] = string.IsNullOrWhiteSpace(capability.OperationId)
                        ? capability.Id
                        : capability.OperationId,
                    ["catalog_id"] = capability.CatalogId,
                    ["server"] = capability.Server,
                    ["kind"] = capability.Kind,
                    ["method"] = capability.Method,
                    ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings)
                }).ToArray())
            });
}
