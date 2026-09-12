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
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class DecisionProvenanceValidation
{

    internal static bool LocalDecisionConditionsCoverLockedInputs(
        WorkflowDocument document, IReadOnlyList<(string Workflow, StepDef Step)> evaluators, McpCapabilityActivation activation, CapabilityPreflightResult preflight)
    {
        if (evaluators.Count != 1
            || evaluators[0].Step.Input?["decisions"] is not JsonObject decisions)
        {
            return false;
        }

        var expectedFields = preflight.RequiredMcpCapabilities
            .Where(capability => string.Equals(
                                     capability.Activation?.DecisionContractSource,
                                     LocalDecisionContractSource,
                                     StringComparison.Ordinal)
                                 && string.Equals(
                                     capability.Activation?.DecisionOperationId,
                                     activation.DecisionOperationId,
                                     StringComparison.Ordinal))
            .Select(capability => GetDecisionBoundaryFieldName(capability.Activation!.DecisionOutputPath))
            .Where(static field => field.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!decisions.Select(static item => item.Key).Order(StringComparer.Ordinal)
            .SequenceEqual(expectedFields, StringComparer.Ordinal))
        {
            return false;
        }

        var conditions = decisions
            .SelectMany(static item => (item.Value as JsonObject)?["cases"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(static item => item["when"])
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var expression) ? expression : null)
            .Where(static expression => !string.IsNullOrWhiteSpace(expression))
            .Select(static expression => expression!)
            .ToArray();
        if (conditions.Length == 0)
            return false;

        var workflowCallers = BuildWorkflowArtifactCallerIndex(document);
        foreach (var operationId in activation.DecisionInputOperationIds.Distinct(StringComparer.Ordinal))
        {
            var upstreamCapabilities = preflight.Capabilities
                .Where(capability => capability.Required
                                     && GetResolvedCapabilityOperationIds(capability)
                                         .Contains(operationId, StringComparer.Ordinal)
                                     && capability.Resolution is "mcp" or "native")
                .ToArray();
            if (upstreamCapabilities.Length == 0)
                return false;
            var upstreamSteps = document.Workflows
                .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                    .Concat(EnumerateSteps(workflow.Value.Finally))
                    .Where(step => upstreamCapabilities.Any(capability => StepMatchesDecisionProducer(step, capability)))
                    .Select(step => (Workflow: workflow.Key, Step: step)))
                .ToArray();
            if (upstreamSteps.Length == 0
                || !conditions.Any(condition => LocalDecisionExpressionDependsOnSources(
                    document,
                    workflowCallers,
                    upstreamSteps,
                    evaluators[0].Workflow,
                    condition,
                    new HashSet<string>(StringComparer.Ordinal))))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool LocalDecisionExpressionDependsOnSources(
        WorkflowDocument document, IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers, IReadOnlyList<(string Workflow, StepDef Step)> sources, string workflowName, string expression, HashSet<string> visited)
    {
        IEnumerable<string> References(Acornima.Ast.Node node)
        {
            if (node is Acornima.Ast.MemberExpression member && Path(member) is { } path && (path.StartsWith("data.steps.", StringComparison.Ordinal) || path.StartsWith("data.inputs.", StringComparison.Ordinal))) { yield return path; yield break; }
            foreach (var child in node.ChildNodes) foreach (var reference in References(child)) yield return reference;
        }
        string? Path(Acornima.Ast.Node node)
        {
            if (node is Acornima.Ast.Identifier identifier) return identifier.Name == "data" ? "data" : null;
            if (node is not Acornima.Ast.MemberExpression member || Path(member.Object) is not { } parent) return null;
            var name = !member.Computed && member.Property is Acornima.Ast.Identifier property ? property.Name
                : member.Computed && member.Property is Acornima.Ast.StringLiteral literal ? literal.Value : null;
            return name is not null && IsExactArtifactPathSegment(name) ? parent + "." + name : null;
        }
        try
        {
            var segments = Expressions.ExpressionSegments.Read(expression);
            var expressions = segments.Count > 0 ? segments.Select(segment => segment.Expression) : [expression];
            foreach (var code in expressions)
                foreach (var reference in References(new Acornima.Parser().ParseExpression(code)).Distinct(StringComparer.Ordinal))
                    if (LocalDecisionReferenceDependsOnSources(document, workflowCallers, sources, workflowName, reference, [], visited)) return true;
        }
        catch (Exception ex) when (ex is Acornima.ParseErrorException or Expressions.ExpressionParseException) { return false; }

        return false;
    }

    internal static bool LocalDecisionReferenceDependsOnSources(
        WorkflowDocument document, IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers, IReadOnlyList<(string Workflow, StepDef Step)> sources, string workflowName, string reference, IReadOnlyList<string> appendedPath, HashSet<string> visited)
    {
        var path = TrimWorkflowExpression(reference);
        if (appendedPath.Count > 0)
            path += "." + string.Join('.', appendedPath);
        var visitKey = workflowName + "\u001f" + path;
        if (!visited.Add(visitKey))
            return false;

        try
        {
            const string inputPrefix = "data.inputs.";
            if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
            {
                var inputPath = path[inputPrefix.Length..]
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (inputPath.Length == 0
                    || !workflowCallers.TryGetValue(workflowName, out var callers)
                    || callers.Count == 0)
                {
                    return false;
                }

                return callers.All(caller =>
                    caller.Call.Input?["args"]?[inputPath[0]] is JsonValue argument
                    && argument.TryGetValue<string>(out var argumentExpression)
                    && !string.IsNullOrWhiteSpace(argumentExpression)
                    && LocalDecisionReferenceDependsOnSources(
                        document,
                        workflowCallers,
                        sources,
                        caller.Workflow,
                        argumentExpression,
                        inputPath.Skip(1).ToArray(),
                        visited));
            }

            const string stepPrefix = "data.steps.";
            if (!path.StartsWith(stepPrefix, StringComparison.Ordinal)
                || !document.Workflows.TryGetValue(workflowName, out var workflow))
            {
                return false;
            }

            var stepPath = path[stepPrefix.Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stepPath.Length == 0)
                return false;
            var sourceStep = EnumerateSteps(workflow.Steps)
                .Concat(EnumerateSteps(workflow.Finally))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, stepPath[0], StringComparison.Ordinal)
                                             || string.Equals(candidate.Output, stepPath[0], StringComparison.Ordinal));
            if (sourceStep == null)
                return false;
            if (sources.Any(source => string.Equals(source.Workflow, workflowName, StringComparison.Ordinal)
                                      && ReferenceEquals(source.Step, sourceStep)))
            {
                return true;
            }

            var remainingPath = stepPath.Skip(1).ToArray();
            if (sourceStep.Type is "loop.sequential" or "loop.parallel" && (remainingPath.Length == 0 || remainingPath[0] == "results"))
                return EnumerateSteps(sourceStep.Steps ?? []).Any(child => sources.Any(source => source.Workflow == workflowName && ReferenceEquals(source.Step, child)));
            if (remainingPath.Length == 0) return false;
            if (sourceStep.Type is "set" or "assert.non_null")
            {
                var value = ResolveInstancePath(sourceStep.Input, remainingPath);
                return value is JsonValue setValue
                       && setValue.TryGetValue<string>(out var setExpression)
                       && !string.IsNullOrWhiteSpace(setExpression)
                       && LocalDecisionExpressionDependsOnSources(
                           document,
                           workflowCallers,
                           sources,
                           workflowName,
                           setExpression,
                           visited);
            }

            if (!string.Equals(sourceStep.Type, "workflow.call", StringComparison.Ordinal))
                return sourceStep.Input is not null
                       && EnumerateStringValues(sourceStep.Input).Any(value =>
                           LocalDecisionExpressionDependsOnSources(
                               document,
                               workflowCallers,
                               sources,
                               workflowName,
                               value,
                               visited));

            var targetName = ReadWorkflowCallRefNameFromInput(sourceStep);
            if (string.IsNullOrWhiteSpace(targetName)
                || !document.Workflows.TryGetValue(targetName, out var target))
            {
                return false;
            }

            var outputIndex = string.Equals(remainingPath[0], "outputs", StringComparison.Ordinal) ? 1 : 0;
            if (remainingPath.Length <= outputIndex
                || target.Outputs == null
                || !target.Outputs.TryGetValue(remainingPath[outputIndex], out var output))
            {
                return false;
            }

            return LocalDecisionReferenceDependsOnSources(
                document,
                workflowCallers,
                sources,
                targetName,
                output.Expr,
                remainingPath.Skip(outputIndex + 1).ToArray(),
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    internal static IEnumerable<string> EnumerateStringValues(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            yield return text;
            yield break;
        }
        if (node is JsonObject obj)
        {
            foreach (var child in obj.Select(static item => item.Value).Where(static item => item is not null))
                foreach (var nestedText in EnumerateStringValues(child!))
                    yield return nestedText;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static item => item is not null))
                foreach (var nestedText in EnumerateStringValues(child!))
                    yield return nestedText;
        }
    }

    internal static bool StepMatchesDecisionProducer(StepDef step, ResolvedCapability capability)
    {
        if (string.Equals(capability.Resolution, "native", StringComparison.Ordinal))
            return string.Equals(step.Type, capability.Method, StringComparison.Ordinal);

        return string.Equals(capability.Resolution, "mcp", StringComparison.Ordinal)
               && McpStepMatchesCapability(
                   step,
                   capability.Server!,
                   capability.Kind!,
                   capability.Method!,
                   capability.RequestBindings);
    }

    internal static bool MatchesLocalDecisionIdentity(StepDef step, McpCapabilityActivation activation)
        => activation.DecisionContractSource != LocalDecisionContractSource ||
            step.Type == LocalDecisionStepType && step.Input is JsonObject input &&
            input["decisions"] is JsonObject decisions && decisions.ContainsKey(GetDecisionBoundaryFieldName(activation.DecisionOutputPath));

    internal static bool ConditionalDecisionExpressionMatchesDeclaredPath(
        string expression, string decisionOutputPath)
    {
        if (string.IsNullOrWhiteSpace(decisionOutputPath)
            || !decisionOutputPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = string.Join('.', decisionOutputPath.Split('/').Skip(1)
            .Select(DecodeJsonPointerToken)
            .Where(static segment => segment.Length > 0));
        if (suffix.Length == 0)
            return false;

        var path = TrimWorkflowExpression(expression);
        var boundaryField = GetDecisionBoundaryFieldName(decisionOutputPath);
        return string.Equals(path, suffix, StringComparison.Ordinal)
               || path.EndsWith('.' + suffix, StringComparison.Ordinal)
               || boundaryField.Length > 0
               && (string.Equals(path, "data.inputs." + boundaryField, StringComparison.Ordinal)
                   || path.EndsWith('.' + boundaryField, StringComparison.Ordinal));
    }

    internal static bool ConditionalDecisionExpressionDependsOnSource(
        WorkflowDocument document, IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers, IReadOnlyList<(string Workflow, StepDef Step)> sources, string workflowName, string expression, IReadOnlyList<string> appendedPath, McpCapabilityActivation activation, HashSet<string>? visited = null)
    {
        if (activation.DecisionContractSource == PlanningDecisionContract.HumanConfirmation && appendedPath.Count == 0
            && ConfirmationDecisionExpression.TryRead(expression, activation.BranchValue, activation.NoEffectValues.Single(), out var confirmationReference))
            expression = confirmationReference;
        var path = TrimWorkflowExpression(expression);
        if (appendedPath.Count > 0)
            path += "." + string.Join('.', appendedPath);
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        var visitKey = workflowName + "\u001f" + path;
        if (!visited.Add(visitKey))
            return false;

        try
        {
            const string inputPrefix = "data.inputs.";
            if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
            {
                var inputPath = path[inputPrefix.Length..]
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (inputPath.Length == 0
                    || !workflowCallers.TryGetValue(workflowName, out var callers)
                    || callers.Count == 0)
                {
                    return false;
                }

                return callers.All(caller =>
                    caller.Call.Input?["args"]?[inputPath[0]] is JsonValue argument
                    && argument.TryGetValue<string>(out var argumentExpression)
                    && !string.IsNullOrWhiteSpace(argumentExpression)
                    && ConditionalDecisionExpressionDependsOnSource(
                        document,
                        workflowCallers,
                        sources,
                        caller.Workflow,
                        argumentExpression,
                        inputPath.Skip(1).ToArray(),
                        activation,
                        visited));
            }

            const string stepPrefix = "data.steps.";
            if (!path.StartsWith(stepPrefix, StringComparison.Ordinal)
                || !document.Workflows.TryGetValue(workflowName, out var workflow))
            {
                return false;
            }

            var stepPath = path[stepPrefix.Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stepPath.Length < 2)
                return false;
            var sourceStep = EnumerateSteps(workflow.Steps)
                .Concat(EnumerateSteps(workflow.Finally))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, stepPath[0], StringComparison.Ordinal)
                                             || string.Equals(candidate.Output, stepPath[0], StringComparison.Ordinal));
            if (sourceStep == null)
                return false;
            var remainingPath = stepPath.Skip(1).ToArray();
            // Sequence and switch executors retain child results under their child IDs.
            // Follow only declared direct children, never arbitrary payload fields or helpers.
            while (sourceStep.Type is "sequence" or "switch" && remainingPath.Length > 1)
            {
                var children = ArtifactChildren(sourceStep).Where(child => child.Id == remainingPath[0] || child.Output == remainingPath[0]).ToArray();
                if (children.Length != 1) return false;
                sourceStep = children[0];
                remainingPath = remainingPath.Skip(1).ToArray();
            }
            if (sources.Any(source => string.Equals(source.Workflow, workflowName, StringComparison.Ordinal)
                                      && ReferenceEquals(source.Step, sourceStep)))
            {
                var declaredPath = activation.DecisionOutputPath.Split('/')
                    .Skip(1)
                    .Select(DecodeJsonPointerToken)
                    .Where(static token => token.Length > 0)
                    .ToArray();
                if (string.Equals(
                        activation.DecisionContractSource,
                        CapabilityDecisionContractSource,
                        StringComparison.Ordinal))
                {
                    declaredPath = ["response", .. declaredPath];
                }
                return remainingPath.SequenceEqual(declaredPath, StringComparer.Ordinal)
                       && DecisionSourceStepDeclaresContract(sourceStep, activation);
            }

            if (sourceStep.Type is "set" or "assert.non_null")
            {
                var value = ResolveInstancePath(sourceStep.Input, remainingPath);
                return value is JsonValue setValue
                       && setValue.TryGetValue<string>(out var setExpression)
                       && !string.IsNullOrWhiteSpace(setExpression)
                       && ConditionalDecisionExpressionDependsOnSource(
                           document,
                           workflowCallers,
                           sources,
                           workflowName,
                           setExpression,
                           [],
                           activation,
                           visited);
            }

            if (!string.Equals(sourceStep.Type, "workflow.call", StringComparison.Ordinal))
                return false;
            var targetName = ReadWorkflowCallRefNameFromInput(sourceStep);
            if (string.IsNullOrWhiteSpace(targetName)
                || !document.Workflows.TryGetValue(targetName, out var target))
            {
                return false;
            }

            var outputIndex = string.Equals(remainingPath[0], "outputs", StringComparison.Ordinal) ? 1 : 0;
            if (remainingPath.Length <= outputIndex
                || target.Outputs == null
                || !target.Outputs.TryGetValue(remainingPath[outputIndex], out var output))
            {
                return false;
            }

            return ConditionalDecisionExpressionDependsOnSource(
                document,
                workflowCallers,
                sources,
                targetName,
                output.Expr,
                remainingPath.Skip(outputIndex + 1).ToArray(),
                activation,
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    internal static bool DecisionSourceStepDeclaresContract(
        StepDef sourceStep, McpCapabilityActivation activation)
    {
        if (activation.DecisionContractSource == PlanningDecisionContract.HumanConfirmation)
            return sourceStep.Type == "human.input" && sourceStep.Input?["mode"]?.GetValue<string>() == HumanInputContract.ModeConfirm
                && sourceStep.OnError is null;

        if (string.Equals(
                activation.DecisionContractSource,
                LocalDecisionContractSource,
                StringComparison.Ordinal))
        {
            return LocalDecisionSourceStepDeclaresContract(sourceStep, activation);
        }

        if (!string.Equals(
                activation.DecisionContractSource,
                StructuredDecisionContractSource,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (sourceStep.Input is not JsonObject input
            || input["structured_output"] is not JsonObject structuredOutput
            || structuredOutput["strict"] is not JsonValue strictValue
            || !strictValue.TryGetValue<bool>(out var strict)
            || !strict
            || structuredOutput["schema_inline"] is not JsonObject schema)
        {
            return false;
        }

        var pointerTokens = activation.DecisionOutputPath.Split('/')
            .Skip(1)
            .Select(DecodeJsonPointerToken)
            .ToArray();
        if (pointerTokens.Length < 2
            || !string.Equals(pointerTokens[0], "json", StringComparison.Ordinal))
        {
            return false;
        }

        JsonNode? current = schema;
        foreach (var token in pointerTokens.Skip(1))
        {
            if (current is not JsonObject currentObject
                || currentObject["properties"] is not JsonObject properties
                || !properties.TryGetPropertyValue(token, out current)
                || currentObject["required"] is not JsonArray required
                || !required.OfType<JsonValue>().Any(value =>
                    value.TryGetValue<string>(out var requiredName)
                    && string.Equals(requiredName, token, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return DecisionBoundarySchemaMatches(current, activation.AllowedValues);
    }

    internal static bool LocalDecisionSourceStepDeclaresContract(
        StepDef sourceStep, McpCapabilityActivation activation)
    {
        if (!string.Equals(sourceStep.Type, LocalDecisionStepType, StringComparison.Ordinal)
            || sourceStep.Input is not JsonObject input
            || input.Count != 1
            || input["decisions"] is not JsonObject decisions)
        {
            return false;
        }

        var fieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
        if (fieldName.Length == 0
            || decisions[fieldName] is not JsonObject contract
            || contract.Select(static item => item.Key)
                .Any(static key => key is not ("allowed_values" or "cases" or "default"))
            || contract["allowed_values"] is not JsonArray allowedNodes
            || allowedNodes.Count != activation.AllowedValues.Count)
        {
            return false;
        }

        var allowedValues = allowedNodes
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) ? text : null)
            .ToArray();
        if (allowedValues.Any(static value => string.IsNullOrWhiteSpace(value))
            || !allowedValues!
                .Select(static value => value!)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(activation.AllowedValues.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || contract["cases"] is not JsonArray cases
            || cases.Count == 0)
        {
            return false;
        }

        var expectedCaseValues = activation.AllowedValues
            .Except(activation.NoEffectValues, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var caseValues = new List<string>(cases.Count);
        foreach (var caseNode in cases)
        {
            if (caseNode is not JsonObject decisionCase
                || decisionCase.Count != 2
                || decisionCase["when"] is not JsonValue whenValue
                || !whenValue.TryGetValue<string>(out var whenExpression)
                || string.IsNullOrWhiteSpace(whenExpression)
                || !whenExpression.Contains("${", StringComparison.Ordinal)
                || decisionCase["value"] is not JsonValue valueNode
                || !valueNode.TryGetValue<string>(out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            caseValues.Add(value);
        }
        if (!caseValues.Order(StringComparer.Ordinal)
            .SequenceEqual(expectedCaseValues, StringComparer.Ordinal))
        {
            return false;
        }

        if (activation.NoEffectValues.Count == 0)
            return !contract.ContainsKey("default");
        return activation.NoEffectValues.Count == 1
               && contract["default"] is JsonValue defaultNode
               && defaultNode.TryGetValue<string>(out var defaultValue)
               && string.Equals(defaultValue, activation.NoEffectValues[0], StringComparison.Ordinal);
    }

    internal static bool DecisionBoundarySchemaMatches(
        JsonNode? schema, IReadOnlyList<string> allowedValues)
    {
        if (schema is not JsonObject obj
            || !string.Equals(GetStringProperty(obj, "type"), "string", StringComparison.Ordinal)
            || obj["enum"] is not JsonArray enumValues)
        {
            return false;
        }

        var actual = enumValues.OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(static value => value != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return actual.SequenceEqual(
            allowedValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    internal static string GetDecisionBoundaryFieldName(string pointer)
    {
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            return string.Empty;
        var token = pointer.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(token) ? string.Empty : DecodeJsonPointerToken(token);
    }
}
