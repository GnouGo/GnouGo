using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

internal static partial class WorkflowPlanPipelineQualityAnalyzer
{
    internal const string MainDataflowPhase = "pipeline_main_dataflow_validation";
    internal const string UnprovenExternalArtifactCode = "PIPELINE_MAIN_UNPROVEN_EXTERNAL_ARTIFACT";
    internal const string UnprovenExternalArtifactRootCause = "unproven_external_artifact";

    internal static JsonArray AnalyzeExternalArtifactReadiness(WorkflowDocument doc)
    {
        var diagnostics = new JsonArray();
        if (!doc.Workflows.ContainsKey("main"))
            return diagnostics;

        var scopedStepsByWorkflow = doc.Workflows.ToDictionary(
            static workflow => workflow.Key,
            static workflow => EnumerateScopedSteps(workflow.Value.Steps)
                .Concat(EnumerateScopedSteps(workflow.Value.Finally))
                .Where(static scoped => !string.IsNullOrWhiteSpace(scoped.Step.Id))
                // Mutually exclusive switch/parallel branches may intentionally expose
                // the same logical step id. Only one such branch contributes at runtime;
                // keep a deterministic representative for provenance lookup instead of
                // throwing while building the static quality index.
                .GroupBy(static scoped => scoped.Step.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (workflowName, workflow) in doc.Workflows)
        {
            var scopedSteps = EnumerateScopedSteps(workflow.Steps)
                .Concat(EnumerateScopedSteps(workflow.Finally))
                .ToArray();
            var stepsById = scopedStepsByWorkflow[workflowName]
                .ToDictionary(static pair => pair.Key, static pair => pair.Value.Step, StringComparer.Ordinal);

            foreach (var scopedStep in scopedSteps)
            {
                var step = scopedStep.Step;
                if (!IsExternalArtifactConsumerStepType(step.Type))
                    continue;

                foreach (var assignment in EnumerateJsonStringValues(step.Input, "input"))
                {
                    if (!IsArtifactLocatorField(assignment.Field)
                        || IsArtifactCreationTargetField(assignment.Field)
                        || string.IsNullOrWhiteSpace(assignment.Text)
                        || IsProvenArtifactSource(
                            doc,
                            workflowName,
                            assignment.Text,
                            scopedStepsByWorkflow,
                            scopedStep.Variables,
                            new HashSet<string>(StringComparer.Ordinal),
                            out _))
                    {
                        continue;
                    }

                    var provenance = BuildArtifactProvenance(assignment.Text, stepsById);
                    var diagnostic = BuildUnprovenExternalArtifactDiagnostic(step, assignment.Field, assignment.Text, provenance);
                    diagnostic["workflow"] = workflowName;
                    diagnostics.Add((JsonNode)diagnostic);
                }
            }
        }

        return diagnostics;
    }

    internal static void ValidateExternalArtifactReadiness(WorkflowDocument doc)
    {
        var diagnostics = AnalyzeExternalArtifactReadiness(doc);
        if (diagnostics.Count == 0)
            return;

        var details = BuildMainDataflowQualityDetails(diagnostics);
        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow dataflow quality validation failed. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    internal static JsonObject BuildMainDataflowQualityDetails(JsonArray diagnostics)
    {
        return new JsonObject
        {
            ["ok"] = false,
            ["phase"] = MainDataflowPhase,
            ["summary"] = $"{diagnostics.Count} pipeline main dataflow diagnostic(s)",
            ["diagnostics"] = CloneArray(diagnostics),
            ["root_causes"] = BuildRootCauses(diagnostics),
            ["llm_guidance"] = BuildMainDataflowGuidance()
        };
    }

    internal static JsonArray BuildRootCauses(JsonArray diagnostics)
    {
        var rootCauses = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.OfType<JsonObject>())
        {
            var code = GetString(diagnostic, "code");
            if (!string.Equals(code, UnprovenExternalArtifactCode, StringComparison.Ordinal))
                continue;

            var consumerStep = GetString(diagnostic, "consumer_step") ?? GetString(diagnostic, "step");
            var field = GetString(diagnostic, "field");
            var key = string.Join('\u001f', UnprovenExternalArtifactRootCause, MainDataflowPhase, consumerStep, field, code);
            if (!seen.Add(key))
                continue;

            var message = GetString(diagnostic, "message")
                          ?? "Main synthesized an operational artifact locator before passing it to external work.";
            rootCauses.Add((JsonNode)new JsonObject
            {
                ["category"] = UnprovenExternalArtifactRootCause,
                ["phase"] = MainDataflowPhase,
                ["consumer_step"] = consumerStep,
                ["consumer_field"] = field,
                ["invalid_path"] = field,
                ["code"] = code,
                ["message"] = message,
                ["primary"] = true
            });
        }

        return rootCauses;
    }

    internal static JsonArray BuildMainDataflowGuidance()
        => new(
            (JsonNode)JsonValue.Create("Reprompt main assembly only when the diagnostic is caused by parent dataflow wiring. If main already routes a producer-leaf output, repair that producer output so its artifact provenance remains statically traceable.")!,
            (JsonNode)JsonValue.Create("Do not synthesize operational artifact locators such as project/workspace/root/path/directory/file values in main before external work uses them.")!,
            (JsonNode)JsonValue.Create("Use caller-provided workflow inputs for pre-existing artifacts, or pass a typed output from an upstream external-producing leaf/action that proves the artifact exists.")!);

    internal static bool IsLeafArtifactOutputProven(
        WorkflowDocument document,
        string workflowName,
        string outputName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? failureReason)
    {
        failureReason = null;
        if (!document.Workflows.TryGetValue(workflowName, out var workflow)
            || workflow.Outputs == null
            || !workflow.Outputs.TryGetValue(outputName, out var output)
            || string.IsNullOrWhiteSpace(output.Expr))
        {
            failureReason = $"Workflow output '{workflowName}.{outputName}' is missing or has no expression binding.";
            return false;
        }

        var stepsById = EnumerateScopedSteps(workflow.Steps)
            .Concat(EnumerateScopedSteps(workflow.Finally))
            .Where(static scoped => !string.IsNullOrWhiteSpace(scoped.Step.Id))
            .GroupBy(static scoped => scoped.Step.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Step, StringComparer.Ordinal);
        if (IsProvenLeafArtifactOutputSource(
                output.Expr,
                stepsById,
                new HashSet<string>(StringComparer.Ordinal),
                out var sourceDescription))
        {
            return true;
        }

        failureReason = $"Artifact output '{workflowName}.{outputName}' is bound to '{output.Expr}', which is not an exact caller input, external action response, or transparent set alias."
                        + (string.IsNullOrWhiteSpace(sourceDescription) ? "" : $" Last inspected source: {sourceDescription}.");
        return false;
    }

    private static bool IsProvenLeafArtifactOutputSource(
        string text,
        IReadOnlyDictionary<string, StepDef> stepsById,
        HashSet<string> visitedPaths,
        out string? sourceDescription)
    {
        sourceDescription = null;
        if (TryParseExactDataInputExpression(text, out var inputName, out var inputPath))
        {
            sourceDescription = "caller input `" + inputName
                                + (inputPath.Count == 0 ? "" : "." + string.Join('.', inputPath))
                                + "`";
            return true;
        }

        if (!TryParseExactStepPathExpression(text, out var stepId, out var path)
            || !stepsById.TryGetValue(stepId, out var producer))
        {
            sourceDescription = text.Contains("${", StringComparison.Ordinal)
                ? "opaque expression or template"
                : "literal value";
            return false;
        }

        if (producer.Type is "mcp.call" or "human.input")
        {
            sourceDescription = "external/action step `" + stepId + "`";
            return true;
        }

        if (producer.Type is not ("set" or "assert.non_null"))
        {
            sourceDescription = $"non-transparent step `{stepId}` of type `{producer.Type}`";
            return false;
        }

        var key = stepId + "." + string.Join('.', path);
        if (!visitedPaths.Add(key))
        {
            sourceDescription = "cyclic set alias `" + key + "`";
            return false;
        }

        if (!TryGetJsonNodeAtPath(producer.Input, path, out var producerNode)
            || producerNode is not JsonValue value
            || !value.TryGetValue<string>(out var producerText)
            || string.IsNullOrWhiteSpace(producerText))
        {
            visitedPaths.Remove(key);
            sourceDescription = $"opaque set projection `{key}`";
            return false;
        }

        var proven = IsProvenLeafArtifactOutputSource(producerText, stepsById, visitedPaths, out sourceDescription);
        visitedPaths.Remove(key);
        return proven;
    }

    private static JsonObject BuildUnprovenExternalArtifactDiagnostic(
        StepDef consumer,
        string field,
        string expression,
        ArtifactProvenance provenance)
    {
        var diagnostic = new JsonObject
        {
            ["code"] = UnprovenExternalArtifactCode,
            ["phase"] = MainDataflowPhase,
            ["workflow"] = "main",
            ["step"] = consumer.Id,
            ["consumer_step"] = consumer.Id,
            ["consumer_type"] = consumer.Type,
            ["field"] = field,
            ["request_field"] = field,
            ["invalid_assignment"] = expression,
            ["source_kind"] = provenance.SourceKind,
            ["message"] = $"External step '{consumer.Id}' receives artifact-like field '{field}' from main-synthesized value '{expression}'.",
            ["expected"] = "Pass a caller-provided workflow input, or pass a typed output from an upstream external-producing leaf/action that proves the artifact exists.",
            ["hint"] = "Main may shape simple scalar values, but it should not invent operational artifact locators for external consumers."
        };

        if (!string.IsNullOrWhiteSpace(provenance.ProducerStepId))
            diagnostic["producer_step"] = provenance.ProducerStepId;
        if (!string.IsNullOrWhiteSpace(provenance.ProducerStepType))
            diagnostic["producer_type"] = provenance.ProducerStepType;
        if (!string.IsNullOrWhiteSpace(provenance.ProducerField))
            diagnostic["producer_field"] = provenance.ProducerField;

        return diagnostic;
    }

    private static ArtifactProvenance BuildArtifactProvenance(
        string text,
        IReadOnlyDictionary<string, StepDef> stepsById)
    {
        if (TryParseExactStepPathExpression(text, out var stepId, out var path)
            && stepsById.TryGetValue(stepId, out var producer))
        {
            return new ArtifactProvenance(
                producer.Type is "set" or "assert.non_null"
                    ? "main_set"
                    : "main_support_step",
                stepId,
                producer.Type,
                path.Count == 0 ? null : string.Join('.', path));
        }

        if (TryParseExactStepPathExpression(text, out stepId, out path))
        {
            return new ArtifactProvenance(
                "unknown_step",
                stepId,
                null,
                path.Count == 0 ? null : string.Join('.', path));
        }

        return text.Contains("${", StringComparison.Ordinal)
            ? new ArtifactProvenance("main_template", null, null, null)
            : new ArtifactProvenance("main_literal", null, null, null);
    }

    private static bool IsProvenArtifactSource(
        WorkflowDocument document,
        string workflowName,
        string text,
        IReadOnlyDictionary<string, Dictionary<string, ScopedStep>> stepsByWorkflow,
        IReadOnlyDictionary<string, ArtifactVariableBinding> variables,
        HashSet<string> visitedPaths,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sourceDescription)
    {
        sourceDescription = null;
        if (TryParseExactDataInputExpression(text, out var inputName, out var inputPath))
        {
            if (string.Equals(workflowName, "main", StringComparison.Ordinal))
            {
                sourceDescription = "workflow input `" + inputName
                                    + (inputPath.Count == 0 ? "" : "." + string.Join('.', inputPath))
                                    + "`";
                return true;
            }

            var callSites = FindLocalWorkflowCallSites(document, workflowName, inputName, inputPath).ToArray();
            if (callSites.Length == 0)
            {
                // A reusable leaf may remain uncalled in an intermediate assembled
                // document. Its declared input is still a legitimate caller-provided
                // artifact boundary. Separate pipeline obligation validation decides
                // whether required leaves/capabilities were actually orchestrated.
                sourceDescription = "declared reusable workflow input `" + workflowName + "." + inputName + "`";
                return true;
            }
            var allProven = callSites.All(callSite => IsProvenArtifactSource(
                document,
                callSite.CallerWorkflow,
                callSite.Argument,
                stepsByWorkflow,
                callSite.Variables,
                visitedPaths,
                out _));
            if (allProven)
                sourceDescription = "proven caller argument for `" + workflowName + "." + inputName + "`";
            return allProven;
        }

        if (TryParseExactDataVariableExpression(text, out var variableName, out var variablePath)
            && variables.TryGetValue(variableName, out var binding))
        {
            var variableKey = workflowName + ":variable:" + variableName + "." + string.Join('.', variablePath);
            if (!visitedPaths.Add(variableKey))
                return false;

            var proven = IsProvenLoopItemSource(
                document,
                workflowName,
                binding.Items,
                variablePath,
                stepsByWorkflow,
                binding.ParentVariables,
                visitedPaths,
                out sourceDescription);
            visitedPaths.Remove(variableKey);
            return proven;
        }

        if (!TryParseExactStepPathExpression(text, out var stepId, out var path)
            || !stepsByWorkflow.TryGetValue(workflowName, out var stepsById)
            || !stepsById.TryGetValue(stepId, out var scopedProducer))
        {
            return false;
        }

        var producer = scopedProducer.Step;

        if (producer.Type is "mcp.call" or "human.input")
        {
            sourceDescription = "external/action step `" + stepId + "`";
            return true;
        }

        if (string.Equals(producer.Type, "workflow.call", StringComparison.Ordinal)
            && TryResolveLocalWorkflowCallOutput(document, producer, path, out var targetWorkflow, out var outputExpression))
        {
            var callPath = workflowName + ":" + stepId + ":" + string.Join('.', path);
            if (!visitedPaths.Add(callPath))
                return false;
            var proven = IsProvenArtifactSource(
                document,
                targetWorkflow,
                outputExpression,
                stepsByWorkflow,
                EmptyVariableBindings.Value,
                visitedPaths,
                out sourceDescription);
            visitedPaths.Remove(callPath);
            return proven;
        }

        if (producer.Type is not ("set" or "assert.non_null"))
            return false;

        var setPath = workflowName + ":" + stepId + "." + string.Join('.', path);
        if (!visitedPaths.Add(setPath))
            return false;

        if (!TryGetJsonNodeAtPath(producer.Input, path, out var producerNode)
            || producerNode is not JsonValue value
            || !value.TryGetValue<string>(out var producerText))
        {
            return false;
        }

        var result = IsProvenArtifactSource(
            document,
            workflowName,
            producerText,
            stepsByWorkflow,
            scopedProducer.Variables,
            visitedPaths,
            out sourceDescription);
        visitedPaths.Remove(setPath);
        return result;
    }

    private static bool IsProvenLoopItemSource(
        WorkflowDocument document,
        string workflowName,
        JsonNode? items,
        IReadOnlyList<string> itemPath,
        IReadOnlyDictionary<string, Dictionary<string, ScopedStep>> stepsByWorkflow,
        IReadOnlyDictionary<string, ArtifactVariableBinding> variables,
        HashSet<string> visitedPaths,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sourceDescription)
    {
        sourceDescription = null;
        if (items is JsonArray literalItems)
            return AreProjectedArtifactNodesProven(
                document,
                workflowName,
                literalItems,
                itemPath,
                stepsByWorkflow,
                variables,
                visitedPaths,
                out sourceDescription);

        if (items is not JsonValue value
            || !value.TryGetValue<string>(out var itemsExpression)
            || string.IsNullOrWhiteSpace(itemsExpression))
        {
            return false;
        }

        if (IsProvenArtifactSource(
                document,
                workflowName,
                itemsExpression,
                stepsByWorkflow,
                variables,
                visitedPaths,
                out sourceDescription))
        {
            return true;
        }

        if (!TryParseExactStepPathExpression(itemsExpression, out var stepId, out var stepPath)
            || !stepsByWorkflow.TryGetValue(workflowName, out var stepsById)
            || !stepsById.TryGetValue(stepId, out var scopedProducer)
            || !string.Equals(scopedProducer.Step.Type, "set", StringComparison.Ordinal)
            || !TryGetJsonNodeAtPath(scopedProducer.Step.Input, stepPath, out var projectedItems))
        {
            return false;
        }

        return AreProjectedArtifactNodesProven(
            document,
            workflowName,
            projectedItems,
            itemPath,
            stepsByWorkflow,
            scopedProducer.Variables,
            visitedPaths,
            out sourceDescription);
    }

    private static bool AreProjectedArtifactNodesProven(
        WorkflowDocument document,
        string workflowName,
        JsonNode? node,
        IReadOnlyList<string> path,
        IReadOnlyDictionary<string, Dictionary<string, ScopedStep>> stepsByWorkflow,
        IReadOnlyDictionary<string, ArtifactVariableBinding> variables,
        HashSet<string> visitedPaths,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sourceDescription)
    {
        sourceDescription = null;
        if (node is JsonArray array)
        {
            if (array.Count == 0)
                return false;

            foreach (var item in array)
            {
                if (!AreProjectedArtifactNodesProven(
                        document,
                        workflowName,
                        item,
                        path,
                        stepsByWorkflow,
                        variables,
                        visitedPaths,
                        out sourceDescription))
                {
                    return false;
                }
            }

            sourceDescription ??= "proven loop-item projection";
            return true;
        }

        if (path.Count > 0)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(path[0], out var child))
                return false;
            return AreProjectedArtifactNodesProven(
                document,
                workflowName,
                child,
                path.Skip(1).ToArray(),
                stepsByWorkflow,
                variables,
                visitedPaths,
                out sourceDescription);
        }

        return node is JsonValue value
               && value.TryGetValue<string>(out var expression)
               && IsProvenArtifactSource(
                   document,
                   workflowName,
                   expression,
                   stepsByWorkflow,
                   variables,
                   visitedPaths,
                   out sourceDescription);
    }

    private static IEnumerable<(string CallerWorkflow, string Argument, IReadOnlyDictionary<string, ArtifactVariableBinding> Variables)> FindLocalWorkflowCallSites(
        WorkflowDocument document,
        string targetWorkflow,
        string inputName,
        IReadOnlyList<string> inputPath)
    {
        foreach (var (callerWorkflow, workflow) in document.Workflows)
        foreach (var scopedStep in EnumerateScopedSteps(workflow.Steps).Concat(EnumerateScopedSteps(workflow.Finally)))
        {
            var step = scopedStep.Step;
            if (!TryGetLocalWorkflowCallTarget(step, out var target)
                || !string.Equals(target, targetWorkflow, StringComparison.Ordinal)
                || step.Input?["args"] is not JsonObject args
                || !args.TryGetPropertyValue(inputName, out var argumentNode)
                || argumentNode == null)
            {
                continue;
            }

            if (inputPath.Count == 0
                && argumentNode is JsonValue directValue
                && directValue.TryGetValue<string>(out var directArgument)
                && !string.IsNullOrWhiteSpace(directArgument))
            {
                yield return (callerWorkflow, directArgument, scopedStep.Variables);
                continue;
            }

            if (argumentNode is JsonValue expressionValue
                && expressionValue.TryGetValue<string>(out var expression)
                && TryAppendExactExpressionPath(expression, inputPath, out var nestedExpression))
            {
                yield return (callerWorkflow, nestedExpression, scopedStep.Variables);
                continue;
            }

            if (TryGetJsonNodeAtPath(argumentNode, inputPath, out var nestedNode)
                && nestedNode is JsonValue nestedValue
                && nestedValue.TryGetValue<string>(out var nestedArgument)
                && !string.IsNullOrWhiteSpace(nestedArgument))
            {
                yield return (callerWorkflow, nestedArgument, scopedStep.Variables);
            }
        }
    }

    private static bool TryAppendExactExpressionPath(
        string expression,
        IReadOnlyList<string> path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? nestedExpression)
    {
        nestedExpression = null;
        if (path.Count == 0 || !TryExtractExactExpressionBody(expression, out var body))
            return false;

        nestedExpression = "${" + body + "." + string.Join('.', path) + "}";
        return true;
    }

    private static bool TryResolveLocalWorkflowCallOutput(
        WorkflowDocument document,
        StepDef call,
        IReadOnlyList<string> path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? targetWorkflow,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? outputExpression)
    {
        outputExpression = null;
        if (!TryGetLocalWorkflowCallTarget(call, out targetWorkflow)
            || !document.Workflows.TryGetValue(targetWorkflow, out var workflow))
        {
            return false;
        }

        var outputPath = path.Count > 0 && string.Equals(path[0], "outputs", StringComparison.Ordinal)
            ? path.Skip(1).ToArray()
            : path.ToArray();
        if (outputPath.Length == 0
            || workflow.Outputs == null
            || !workflow.Outputs.TryGetValue(outputPath[0], out var output)
            || string.IsNullOrWhiteSpace(output.Expr))
        {
            return false;
        }

        if (outputPath.Length == 1)
        {
            outputExpression = output.Expr;
            return true;
        }

        return TryAppendExactExpressionPath(
            output.Expr,
            outputPath.Skip(1).ToArray(),
            out outputExpression);
    }

    private static bool TryGetLocalWorkflowCallTarget(
        StepDef step,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? targetWorkflow)
    {
        targetWorkflow = null;
        if (!string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            || step.Input?["ref"] is not JsonObject reference
            || !string.Equals(reference["kind"]?.GetValue<string>(), "local", StringComparison.Ordinal))
        {
            return false;
        }

        targetWorkflow = reference["name"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(targetWorkflow);
    }

    private static bool IsExternalArtifactConsumerStepType(string type)
        => type is "mcp.call" or "llm.call" or "workflow.execute" or "workflow.route";

    private static bool IsArtifactLocatorField(string field)
    {
        var target = GetLeafFieldName(field);
        var tokens = TokenizeName(target);
        if (tokens.Count == 0 || tokens.Any(IsUrlLikeToken))
            return false;

        if (tokens.Any(static token => token is
                "root" or
                "directory" or
                "directories" or
                "dir" or
                "dirs" or
                "folder" or
                "folders" or
                "workspace" or
                "workdir" or
                "cwd"))
        {
            return true;
        }

        return tokens.Contains("project", StringComparer.Ordinal)
               && tokens.Contains("root", StringComparer.Ordinal);
    }

    internal static bool IsOperationalArtifactLocatorField(string field)
        => IsArtifactLocatorField(field) && !IsArtifactCreationTargetField(field);

    private static bool IsArtifactCreationTargetField(string field)
    {
        var target = GetLeafFieldName(field);
        return Regex.IsMatch(
            target,
            @"^(target|destination|output|temporary|temp)(path|file|directory|folder|root)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsUrlLikeToken(string token)
        => token is "url" or "uri" or "link" or "href" or "endpoint" or "host" or "domain";

    private static string GetLeafFieldName(string field)
    {
        var trimmed = field.Trim();
        var dotIndex = trimmed.LastIndexOf('.');
        if (dotIndex >= 0)
            trimmed = trimmed[(dotIndex + 1)..];

        var bracketIndex = trimmed.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex >= 0)
            trimmed = trimmed[..bracketIndex];

        return trimmed;
    }

    private static IReadOnlyList<string> TokenizeName(string name)
        => NameTokenRegex()
            .Matches(name)
            .Select(static match => match.Value.ToLowerInvariant())
            .Where(static token => token.Length > 0)
            .ToArray();

    private static IEnumerable<(string Field, string Text)> EnumerateJsonStringValues(JsonNode? node, string field)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return (field, text);
                break;

            case JsonObject obj:
                foreach (var (name, child) in obj)
                foreach (var item in EnumerateJsonStringValues(child, field + "." + name))
                    yield return item;
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                foreach (var item in EnumerateJsonStringValues(array[i], $"{field}[{i}]"))
                    yield return item;
                break;
        }
    }

    private static IEnumerable<StepDef> EnumerateSteps(IReadOnlyList<StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Steps != null)
            {
                foreach (var child in EnumerateSteps(step.Steps))
                    yield return child;
            }

            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                foreach (var child in EnumerateSteps(branch.Steps))
                    yield return child;
            }

            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                foreach (var child in EnumerateSteps(@case.Steps))
                    yield return child;
            }

            if (step.Default != null)
            {
                foreach (var child in EnumerateSteps(step.Default))
                    yield return child;
            }
        }
    }

    private static IEnumerable<ScopedStep> EnumerateScopedSteps(
        IReadOnlyList<StepDef> steps,
        IReadOnlyDictionary<string, ArtifactVariableBinding>? inheritedVariables = null)
    {
        var variables = inheritedVariables ?? EmptyVariableBindings.Value;
        foreach (var step in steps)
        {
            yield return new ScopedStep(step, variables);

            var childVariables = variables;
            if (step.Type is "loop.sequential" or "loop.parallel"
                && step.Input?["items"] is { } items)
            {
                var itemVariable = string.IsNullOrWhiteSpace(step.ItemVar) ? "item" : step.ItemVar;
                var expanded = new Dictionary<string, ArtifactVariableBinding>(variables, StringComparer.Ordinal)
                {
                    [itemVariable] = new ArtifactVariableBinding(items, variables)
                };
                childVariables = expanded;
            }

            if (step.Steps != null)
            {
                foreach (var child in EnumerateScopedSteps(step.Steps, childVariables))
                    yield return child;
            }

            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                foreach (var child in EnumerateScopedSteps(branch.Steps, variables))
                    yield return child;
            }

            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                foreach (var child in EnumerateScopedSteps(@case.Steps, variables))
                    yield return child;
            }

            if (step.Default != null)
            {
                foreach (var child in EnumerateScopedSteps(step.Default, variables))
                    yield return child;
            }
        }
    }

    private static bool TryGetJsonNodeAtPath(JsonNode? node, IReadOnlyList<string> path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonNode? result)
    {
        result = node;
        foreach (var segment in path)
        {
            if (result is not JsonObject obj || !obj.TryGetPropertyValue(segment, out result))
            {
                result = null;
                return false;
            }
        }

        return result != null;
    }

    private static bool TryParseExactDataInputExpression(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? inputName,
        out IReadOnlyList<string> path)
    {
        inputName = null;
        path = Array.Empty<string>();
        if (!TryExtractExactExpressionBody(expression, out var body)
            || !body.StartsWith("data.inputs.", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = body["data.inputs.".Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return false;

        for (var i = 0; i < segments.Length; i++)
        {
            if (!IsIdentifierLikePathSegment(segments[i]))
                return false;
        }

        inputName = segments[0];
        path = segments.Skip(1).ToArray();
        return true;
    }

    private static bool TryParseExactStepPathExpression(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? stepId,
        out IReadOnlyList<string> path)
    {
        stepId = null;
        path = Array.Empty<string>();
        if (!TryExtractExactExpressionBody(expression, out var body)
            || !body.StartsWith("data.steps.", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = body["data.steps.".Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || !IsIdentifierLikePathSegment(segments[0]))
            return false;

        for (var i = 1; i < segments.Length; i++)
        {
            if (!IsIdentifierLikePathSegment(segments[i]))
                return false;
        }

        stepId = segments[0];
        path = segments.Skip(1).ToArray();
        return true;
    }

    private static bool TryParseExactDataVariableExpression(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? variableName,
        out IReadOnlyList<string> path)
    {
        variableName = null;
        path = Array.Empty<string>();
        if (!TryExtractExactExpressionBody(expression, out var body)
            || !body.StartsWith("data.", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = body["data.".Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0
            || segments[0] is "inputs" or "steps" or "env" or "_loop"
            || segments.Any(segment => !IsIdentifierLikePathSegment(segment)))
        {
            return false;
        }

        variableName = segments[0];
        path = segments.Skip(1).ToArray();
        return true;
    }

    private static bool TryExtractExactExpressionBody(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? body)
    {
        body = null;
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("${", StringComparison.Ordinal) || !trimmed.EndsWith('}'))
            return false;

        body = trimmed[2..^1].Trim();
        return body.Length > 0 && !body.Contains("${", StringComparison.Ordinal);
    }

    private static bool IsIdentifierLikePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var first = value[0];
        if (!char.IsLetter(first) && first != '_')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-')
                return false;
        }

        return true;
    }

    private static JsonArray CloneArray(JsonArray source)
    {
        var clone = new JsonArray();
        foreach (var item in source)
            clone.Add(item?.DeepClone());
        return clone;
    }

    private static string? GetString(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed record ArtifactProvenance(
        string SourceKind,
        string? ProducerStepId,
        string? ProducerStepType,
        string? ProducerField);

    private sealed record ScopedStep(
        StepDef Step,
        IReadOnlyDictionary<string, ArtifactVariableBinding> Variables);

    private sealed record ArtifactVariableBinding(
        JsonNode Items,
        IReadOnlyDictionary<string, ArtifactVariableBinding> ParentVariables);

    private static class EmptyVariableBindings
    {
        internal static readonly IReadOnlyDictionary<string, ArtifactVariableBinding> Value =
            new Dictionary<string, ArtifactVariableBinding>(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameTokenRegex();
}
