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
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class ArtifactProvenanceValidation
{
    internal static IEnumerable<StepDef> ArtifactChildren(StepDef node) => (node.Steps ?? []).Concat(node.Default ?? [])
        .Concat((node.Cases ?? []).SelectMany(c => c.Steps)).Concat((node.Branches ?? []).SelectMany(b => b.Steps));

    internal static bool TryResolveLoopArtifact(WorkflowDocument document, string workflow, string? consumer, string path, Func<JsonNode?, string?, ArtifactResolution> resolve, out ArtifactResolution resolution)
    {
        resolution = ArtifactResolution.Unproven;
        if (consumer is null || !document.Workflows.TryGetValue(workflow, out var definition)) return false;
        var ancestors = new List<StepDef>();
        bool Find(IEnumerable<StepDef> steps)
        {
            foreach (var step in steps)
            {
                if (step.Id == consumer) return true;
                ancestors.Add(step);
                if (Find(ArtifactChildren(step))) return true;
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            return false;
        }
        if (!Find(definition.Steps.Concat(definition.Finally))) return false;
        foreach (var loop in ancestors.AsEnumerable().Reverse().Where(s => s.Type is "loop.sequential" or "loop.parallel"))
        {
            var prefix = "data." + (loop.ItemVar ?? "item");
            if (path == "data._loop.item" || path.StartsWith("data._loop.item.", StringComparison.Ordinal)) prefix = "data._loop.item";
            if (path != prefix && !path.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
            var projection = path == prefix ? [] : path[(prefix.Length + 1)..].Split('.');
            if (projection.Any(p => !IsExactArtifactPathSegment(p))) return true;
            var items = loop.Input?["items"];
            if (items is JsonArray array && array.Count > 0)
            {
                var producers = new HashSet<PlannedArtifactProducer>(); var callerInput = false;
                foreach (var item in array)
                {
                    var result = resolve(ResolveArtifactCallerArgument(item, projection), loop.Id);
                    if (!result.Proven) return true;
                    producers.UnionWith(result.Producers); callerInput |= result.UsesCallerInput;
                }
                resolution = new(true, producers, callerInput);
                return true;
            }
            if (items is not JsonValue scalar || !scalar.TryGetValue<string>(out var expression)) return true;
            var source = TrimWorkflowExpression(expression).Split('.');
            if (source is not ["data", "steps", var sourceId, "results"] || projection.Length == 0) return true;
            var producerLoop = EnumerateSteps(definition.Steps.Concat(definition.Finally)).FirstOrDefault(s => s.Id == sourceId);
            if (producerLoop?.Type is not ("loop.sequential" or "loop.parallel")) return true;
            // Each iteration preserves its child result envelopes. Trace the same declared
            // child producer for every item, without treating an arbitrary projection as evidence.
            var producer = producerLoop.Steps?.FirstOrDefault(s => s.Id == projection[0]);
            if (producer is null) return true;
            resolution = resolve(AppendExactArtifactExpressionPath("${data.steps." + producer.Id + "}", projection.Skip(1).ToArray()), producer.Id);
            return true;
        }
        return false;
    }

    internal static void ValidateMcpArtifactDataflow(
        WorkflowDocument document, CapabilityPreflightResult preflight)
    {
        var tools = preflight.DiscoveredServers
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Tool: tool)))
            .ToDictionary(
                static item => (item.Server, item.Tool.Name),
                static item => item.Tool,
                EqualityComparer<(string Server, string Name)>.Default);
        if (tools.Count == 0)
            return;

        var stepsByWorkflow = document.Workflows.ToDictionary(
            static workflow => workflow.Key,
            static workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Where(static step => !string.IsNullOrWhiteSpace(step.Id))
                .GroupBy(static step => step.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal),
            StringComparer.Ordinal);
        var workflowCallers = BuildWorkflowArtifactCallerIndex(document);
        var producers = new List<PlannedArtifactProducer>();
        var consumers = new List<(string Workflow, StepDef Step, McpConsumedArtifact Artifact, JsonNode? Value)>();

        foreach (var (workflowName, workflowSteps) in stepsByWorkflow)
        {
            foreach (var step in workflowSteps.Values.Where(static item =>
                         string.Equals(item.Type, "mcp.call", StringComparison.Ordinal)))
            {
                var server = ReadMcpCallInputString(step, "server");
                var kind = ReadMcpCallInputString(step, "kind") ?? "tool";
                var method = ReadMcpCallInputString(step, "method");
                if (!string.Equals(kind, "tool", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(server)
                    || string.IsNullOrWhiteSpace(method)
                    || !tools.TryGetValue((server, method), out var tool))
                {
                    continue;
                }

                var contract = GetValidatedMcpArtifactContract(tool, server);
                if (contract == null)
                    continue;

                producers.AddRange(contract.Produces
                    .Where(static artifact => string.Equals(
                        artifact.Mode,
                        McpArtifactContractConventions.MaterializeMode,
                        StringComparison.Ordinal))
                    .Select(artifact => new PlannedArtifactProducer(
                        workflowName,
                        step.Id,
                        artifact.Kind,
                        artifact.Pointer, artifact.Encoding)));
                foreach (var artifact in contract.Consumes.Where(static artifact => artifact.Required))
                {
                    consumers.Add((
                        workflowName,
                        step,
                        artifact,
                        ResolveInstancePointer(step.Input?["request"], artifact.Pointer)));
                }
            }
        }

        if (consumers.Count == 0)
            return;

        var diagnostics = new JsonArray();
        foreach (var consumer in consumers)
        {
            var resolution = ResolveArtifactValue(
                document,
                stepsByWorkflow,
                workflowCallers,
                producers,
                consumer.Workflow,
                consumer.Value,
                consumer.Artifact.Kind,
                new HashSet<string>(StringComparer.Ordinal),
                consumer.Step.Id);
            if (resolution.Proven)
                continue;

            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "MCP_ARTIFACT_PROVENANCE_UNPROVEN",
                ["workflow"] = consumer.Workflow,
                ["consumer_step"] = consumer.Step.Id,
                ["artifact_kind"] = consumer.Artifact.Kind,
                ["request_pointer"] = consumer.Artifact.Pointer,
                ["value"] = consumer.Value?.DeepClone(),
                ["caller_bindings"] = BuildArtifactCallerBindingDiagnostics(
                    workflowCallers,
                    consumer.Workflow,
                    consumer.Value),
                ["expected"] = "An exact compatible producer response value, optionally routed through workflow inputs/outputs, a transparent set alias, or an exact assert.non_null refinement, or an exact caller-provided artifact input."
            });
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["phase"] = "mcp_artifact_dataflow",
            ["reason"] = "unproven_artifact_provenance",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Route the exact field declared by a compatible MCP artifact producer to the consumer request pointer.")!,
                (JsonNode)JsonValue.Create("Do not invent, concatenate, cast, normalize, or otherwise transform artifact values.")!,
                (JsonNode)JsonValue.Create("A same-path assert.non_null step preserves provenance while refining nullability; route its exact output field without renaming it.")!,
                (JsonNode)JsonValue.Create("Reuse one producer value for every compatible downstream consumer when the task has one source artifact.")!)
        };
        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Generated workflow contains an MCP artifact consumer whose required value has no compatible, unchanged provenance. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    internal static JsonArray BuildArtifactCallerBindingDiagnostics(
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers, string workflowName, JsonNode? value)
    {
        var diagnostics = new JsonArray();
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var expression))
        {
            return diagnostics;
        }

        var path = TrimWorkflowExpression(expression);
        const string inputPrefix = "data.inputs.";
        if (!path.StartsWith(inputPrefix, StringComparison.Ordinal))
            return diagnostics;

        var inputPath = path[inputPrefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (inputPath.Length == 0
            || !workflowCallers.TryGetValue(workflowName, out var callers))
        {
            return diagnostics;
        }

        foreach (var caller in callers
                     .OrderBy(static item => item.Workflow, StringComparer.Ordinal)
                     .ThenBy(static item => item.Call.Id, StringComparer.Ordinal))
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["caller_workflow"] = caller.Workflow,
                ["caller_step"] = caller.Call.Id,
                ["argument_path"] = string.Join('.', inputPath),
                ["argument_value"] = ResolveArtifactCallerArgument(
                    caller.Call.Input?["args"],
                    inputPath)?.DeepClone()
            });
        }

        return diagnostics;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>>
        BuildWorkflowArtifactCallerIndex(WorkflowDocument document)
    {
        var callers = new Dictionary<string, List<(string Workflow, StepDef Call)>>(StringComparer.Ordinal);
        foreach (var (workflowName, workflow) in document.Workflows)
        {
            foreach (var call in EnumerateSteps(workflow.Steps)
                         .Concat(EnumerateSteps(workflow.Finally))
                         .Where(static step => string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)))
            {
                var target = ReadWorkflowCallRefNameFromInput(call);
                if (string.IsNullOrWhiteSpace(target))
                    continue;
                if (!callers.TryGetValue(target, out var targetCallers))
                    callers[target] = targetCallers = [];
                targetCallers.Add((workflowName, call));
            }
        }

        return callers.ToDictionary(
            static item => item.Key,
            static item => (IReadOnlyList<(string Workflow, StepDef Call)>)item.Value,
            StringComparer.Ordinal);
    }

    internal static ArtifactResolution ResolveArtifactValue(
        WorkflowDocument document, IReadOnlyDictionary<string, Dictionary<string, StepDef>> stepsByWorkflow, IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers, IReadOnlyList<PlannedArtifactProducer> producers, string workflowName, JsonNode? value, string artifactKind, HashSet<string> visited, string? consumerStepId = null)
    {
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var expression)
            || string.IsNullOrWhiteSpace(expression))
        {
            return ArtifactResolution.Unproven;
        }

        var visitKey = workflowName + "\u001f" + consumerStepId + "\u001f" + artifactKind + "\u001f" + expression;
        if (!visited.Add(visitKey))
            return ArtifactResolution.Unproven;
        try
        {
            var path = TrimWorkflowExpression(expression);
            ArtifactResolution ResolveProjected(JsonNode? projected, string? context) => ResolveArtifactValue(document, stepsByWorkflow, workflowCallers, producers, workflowName, projected, artifactKind, visited, context);
            if (TryResolveLoopArtifact(document, workflowName, consumerStepId, path, ResolveProjected, out var loopArtifact)) return loopArtifact;
            if (Expressions.ArtifactCollectionExpression.TryRead(path, out var loopId, out var projection))
            {
                if (!stepsByWorkflow.TryGetValue(workflowName, out var collectionSteps) || !collectionSteps.TryGetValue(loopId, out var collection) ||
                    collection.Type is not ("loop.sequential" or "loop.parallel") || collection.If is not null || projection.Count < 3 || projection[1] != "response")
                    return ArtifactResolution.Unproven;
                var child = collection.Steps?.SingleOrDefault(s => s.Id == projection[0]);
                if (child is null || child.Type != "mcp.call" || child.If is not null || child.OnError?.Cases.Any(h => h.Action == "continue") == true)
                    return ArtifactResolution.Unproven;
                var source = producers.SingleOrDefault(p => p.Workflow == workflowName && p.StepId == child.Id && p.Kind == artifactKind && p.Encoding == "json_array" &&
                    projection.Skip(2).SequenceEqual(DecodeArtifactPointer(p.Pointer), StringComparer.Ordinal));
                return source is null ? ArtifactResolution.Unproven : new(true, new HashSet<PlannedArtifactProducer> { source }, false);
            }
            const string inputPrefix = "data.inputs.";
            if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
            {
                var inputPath = path[inputPrefix.Length..]
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (inputPath.Length == 0)
                    return ArtifactResolution.Unproven;

                if (!workflowCallers.TryGetValue(workflowName, out var callers) || callers.Count == 0)
                {
                    return new ArtifactResolution(
                        true,
                        new HashSet<PlannedArtifactProducer>(),
                        true);
                }

                var combined = new HashSet<PlannedArtifactProducer>();
                var usesCallerInput = false;
                foreach (var caller in callers)
                {
                    var argument = ResolveArtifactCallerArgument(caller.Call.Input?["args"], inputPath);
                    var resolved = ResolveArtifactValue(
                        document,
                        stepsByWorkflow,
                        workflowCallers,
                        producers,
                        caller.Workflow,
                        argument,
                        artifactKind,
                        visited,
                        caller.Call.Id);
                    if (!resolved.Proven)
                        return ArtifactResolution.Unproven;
                    combined.UnionWith(resolved.Producers);
                    usesCallerInput |= resolved.UsesCallerInput;
                }

                return new ArtifactResolution(true, combined, usesCallerInput);
            }

            const string stepPrefix = "data.steps.";
            if (!path.StartsWith(stepPrefix, StringComparison.Ordinal)
                || !stepsByWorkflow.TryGetValue(workflowName, out var workflowSteps))
            {
                return ArtifactResolution.Unproven;
            }

            var stepPath = path[stepPrefix.Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stepPath.Length < 2 || !workflowSteps.TryGetValue(stepPath[0], out var sourceStep))
                return ArtifactResolution.Unproven;
            var remainingPath = stepPath.Skip(1).ToArray();

            if (sourceStep.Type is "sequence" or "switch")
            {
                var child = ArtifactChildren(sourceStep).FirstOrDefault(s => s.Id == remainingPath[0]);
                return child is null ? ArtifactResolution.Unproven : ResolveProjected(AppendExactArtifactExpressionPath("${data.steps." + child.Id + "}", remainingPath.Skip(1).ToArray()), child.Id);
            }

            var matchingProducer = producers.FirstOrDefault(producer =>
                string.Equals(producer.Workflow, workflowName, StringComparison.Ordinal)
                && string.Equals(producer.StepId, sourceStep.Id, StringComparison.Ordinal)
                && string.Equals(producer.Kind, artifactKind, StringComparison.Ordinal)
                && remainingPath.SequenceEqual(
                    new[] { "response" }.Concat(DecodeArtifactPointer(producer.Pointer)),
                    StringComparer.Ordinal));
            if (matchingProducer != null)
            {
                return new ArtifactResolution(
                    true,
                    new HashSet<PlannedArtifactProducer> { matchingProducer },
                    false);
            }

            if (sourceStep.Type is "set" or "assert.non_null")
            {
                return ResolveArtifactValue(
                    document,
                    stepsByWorkflow,
                    workflowCallers,
                    producers,
                    workflowName,
                    ResolveInstancePath(sourceStep.Input, remainingPath),
                    artifactKind,
                    visited,
                    sourceStep.Id);
            }

            if (!string.Equals(sourceStep.Type, "workflow.call", StringComparison.Ordinal))
                return ArtifactResolution.Unproven;
            var targetWorkflow = ReadWorkflowCallRefNameFromInput(sourceStep);
            if (string.IsNullOrWhiteSpace(targetWorkflow)
                || !document.Workflows.TryGetValue(targetWorkflow, out var target)
                || remainingPath.Length == 0)
            {
                return ArtifactResolution.Unproven;
            }

            var outputIndex = string.Equals(remainingPath[0], "outputs", StringComparison.Ordinal) ? 1 : 0;
            if (remainingPath.Length < outputIndex + 1
                || target.Outputs == null
                || !target.Outputs.TryGetValue(remainingPath[outputIndex], out var output))
            {
                return ArtifactResolution.Unproven;
            }

            var targetExpression = AppendExactArtifactExpressionPath(
                output.Expr,
                remainingPath.Skip(outputIndex + 1).ToArray());
            if (targetExpression == null)
                return ArtifactResolution.Unproven;

            return ResolveArtifactValue(
                document,
                stepsByWorkflow,
                workflowCallers,
                producers,
                targetWorkflow,
                targetExpression,
                artifactKind,
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    internal static JsonNode? AppendExactArtifactExpressionPath(
        string expression, IReadOnlyList<string> nestedPath)
    {
        if (nestedPath.Count == 0)
            return JsonValue.Create(expression);

        var text = expression.Trim();
        if (!text.StartsWith("${", StringComparison.Ordinal) || !text.EndsWith('}'))
            return null;

        var path = text[2..^1].Trim();
        var existingSegments = path.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existingSegments.Length == 0
            || existingSegments.Any(static segment => !IsExactArtifactPathSegment(segment))
            || nestedPath.Any(static segment => !IsExactArtifactPathSegment(segment)))
        {
            return null;
        }

        return JsonValue.Create("${" + path + "." + string.Join('.', nestedPath) + "}");
    }

    internal static JsonNode? ResolveArtifactCallerArgument(
        JsonNode? arguments, IReadOnlyList<string> path)
    {
        var current = arguments;
        for (var index = 0; index < path.Count; index++)
        {
            if (current is JsonObject obj && obj[path[index]] is { } child)
            {
                current = child;
                continue;
            }

            if (current is JsonValue value
                && value.TryGetValue<string>(out var expression))
            {
                return AppendExactArtifactExpressionPath(expression, path.Skip(index).ToArray());
            }

            return null;
        }

        return current;
    }

    internal static bool IsExactArtifactPathSegment(string segment)
        => segment.Length > 0
           && segment.All(static character => char.IsAsciiLetterOrDigit(character)
                                              || character is '_' or '-');

    internal static JsonNode? ResolveInstancePointer(JsonNode? root, string pointer)
        => ResolveInstancePath(root, DecodeArtifactPointer(pointer));

    internal static JsonNode? ResolveInstancePath(JsonNode? root, IReadOnlyList<string> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (current == null)
                return null;
        }

        return current;
    }

    internal static IReadOnlyList<string> DecodeArtifactPointer(string pointer)
        => pointer[1..]
            .Split('/', StringSplitOptions.None)
            .Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
}
