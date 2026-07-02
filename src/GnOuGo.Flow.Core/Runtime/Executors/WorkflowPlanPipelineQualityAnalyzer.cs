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
        if (!doc.Workflows.TryGetValue("main", out var main))
            return diagnostics;

        var steps = EnumerateSteps(main.Steps).ToArray();
        var stepsById = new Dictionary<string, StepDef>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Id))
                stepsById.TryAdd(step.Id, step);
        }

        foreach (var step in steps)
        {
            if (!IsExternalArtifactConsumerStepType(step.Type))
                continue;

            foreach (var assignment in EnumerateJsonStringValues(step.Input, "input"))
            {
                if (!IsArtifactLocatorField(assignment.Field)
                    || string.IsNullOrWhiteSpace(assignment.Text)
                    || IsProvenArtifactSource(assignment.Text, stepsById, new HashSet<string>(StringComparer.Ordinal), out _))
                {
                    continue;
                }

                var provenance = BuildArtifactProvenance(assignment.Text, stepsById);
                diagnostics.Add((JsonNode)BuildUnprovenExternalArtifactDiagnostic(step, assignment.Field, assignment.Text, provenance));
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
            (JsonNode)JsonValue.Create("Reprompt only main assembly when the diagnostic is about main dataflow wiring.")!,
            (JsonNode)JsonValue.Create("Do not synthesize operational artifact locators such as project/workspace/root/path/directory/file values in main before external work uses them.")!,
            (JsonNode)JsonValue.Create("Use caller-provided workflow inputs for pre-existing artifacts, or pass a typed output from an upstream external-producing leaf/action that proves the artifact exists.")!);

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
                string.Equals(producer.Type, "set", StringComparison.Ordinal)
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
        string text,
        IReadOnlyDictionary<string, StepDef> stepsById,
        HashSet<string> visitedSetPaths,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sourceDescription)
    {
        sourceDescription = null;
        if (TryParseExactDataInputExpression(text, out var inputName))
        {
            sourceDescription = "workflow input `" + inputName + "`";
            return true;
        }

        if (!TryParseExactStepPathExpression(text, out var stepId, out var path)
            || !stepsById.TryGetValue(stepId, out var producer))
        {
            return false;
        }

        if (IsExternalArtifactProducerStepType(producer.Type))
        {
            sourceDescription = "external/action step `" + stepId + "`";
            return true;
        }

        if (!string.Equals(producer.Type, "set", StringComparison.Ordinal))
            return false;

        var setPath = stepId + "." + string.Join('.', path);
        if (!visitedSetPaths.Add(setPath))
            return false;

        if (!TryGetJsonNodeAtPath(producer.Input, path, out var producerNode)
            || producerNode is not JsonValue value
            || !value.TryGetValue<string>(out var producerText))
        {
            return false;
        }

        return IsProvenArtifactSource(producerText, stepsById, visitedSetPaths, out sourceDescription);
    }

    private static bool IsExternalArtifactConsumerStepType(string type)
        => type is "mcp.call" or "llm.call" or "workflow.execute" or "workflow.route";

    private static bool IsExternalArtifactProducerStepType(string type)
        => type is "mcp.call" or "workflow.call" or "human.input";

    private static bool IsArtifactLocatorField(string field)
    {
        var target = GetLeafFieldName(field);
        var tokens = TokenizeName(target);
        if (tokens.Count == 0 || tokens.Any(IsUrlLikeToken))
            return false;

        if (tokens.Any(static token => token is
                "path" or
                "paths" or
                "root" or
                "directory" or
                "directories" or
                "dir" or
                "dirs" or
                "folder" or
                "folders" or
                "workspace" or
                "workdir" or
                "cwd" or
                "file" or
                "files" or
                "filename" or
                "filenames"))
        {
            return true;
        }

        return tokens.Contains("project", StringComparer.Ordinal)
               && tokens.Contains("root", StringComparer.Ordinal);
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
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? inputName)
    {
        inputName = null;
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

        inputName = string.Join('.', segments);
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

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameTokenRegex();
}
