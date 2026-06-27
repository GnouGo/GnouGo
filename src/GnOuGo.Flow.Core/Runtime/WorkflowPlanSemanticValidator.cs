using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed record McpToolOutputContract(
    string ServerName,
    string ToolName,
    JsonNode? InputSchema,
    JsonNode? OutputSchema,
    JsonNode? ExampleResponse);

internal sealed class WorkflowSemanticValidationError
{
    public string Code { get; init; } = "SEMANTIC_MAPPING_ERROR";
    public string? WorkflowName { get; init; }
    public string? StepId { get; init; }
    public string Field { get; init; } = "";
    public string InvalidPath { get; init; } = "";
    public IReadOnlyList<string> AllowedPaths { get; init; } = Array.Empty<string>();
    public string Suggestion { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class WorkflowSemanticValidationException : Exception
{
    public IReadOnlyList<WorkflowSemanticValidationError> Errors { get; }

    public WorkflowSemanticValidationException(IReadOnlyList<WorkflowSemanticValidationError> errors)
        : base(WorkflowPlanSemanticValidator.FormatErrors(errors))
    {
        Errors = errors;
    }
}

/// <summary>
/// Performs static semantic checks that are specific to generated plans.
/// It validates references to previous step outputs such as
/// <c>${data.steps.fetch.response.title}</c> against known step output contracts.
/// </summary>
internal static class WorkflowPlanSemanticValidator
{
    private const string McpRequestExpressionTypeMismatchCode = "MCP_REQUEST_EXPR_TYPE_MISMATCH";

    private static readonly Regex ExpressionRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex DataStepsPathRegex = new(
        @"\bdata\.steps\.([A-Za-z_][A-Za-z0-9_-]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)",
        RegexOptions.Compiled);
    private static readonly Regex DataVariablePathRegex = new(
        @"\bdata\.(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)",
        RegexOptions.Compiled);
    private static readonly Regex NamespacedFunctionCallRegex = new(
        @"\bfunctions\.([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex FunctionDeclarationRegex = new(
        @"\bfunction\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\((?<params>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex FunctionParameterIdentifierRegex = new(
        @"[A-Za-z_$][A-Za-z0-9_$]*",
        RegexOptions.Compiled);
    private static readonly Regex WindowsAbsolutePathRegex = new(
        @"^[A-Za-z]:(?:[\\/]|$)",
        RegexOptions.Compiled);
    private static readonly Regex DiagnosticAbsolutePathFieldRegex = new(
        @"(?:^|[.\[])(?<field>repositoryRootAbsolute|rootPathAbsolute|fullPathAbsolute|filePathAbsolute|workingDirectoryAbsolute|repositoryRoot|rootPath|fullPath|filePath|workingDirectory|defaultWorkingDirectory|allowedWorkingRoots|allowedRoots|originalPath)(?:\b|[\]\)])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LoopResultsFunctionCallRegex = new(
        @"\bfunctions\.(?<function>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*data\.steps\.(?<loop>[A-Za-z_][A-Za-z0-9_-]*)\.results\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex JsFunctionCallbackParameterRegex = new(
        @"\.(?:filter|map|forEach|some|every|find)\s*\(\s*(?:async\s+)?function(?:\s+[A-Za-z_$][A-Za-z0-9_$]*)?\s*\(\s*(?<param>[A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);
    private static readonly Regex JsArrowCallbackParameterRegex = new(
        @"\.(?:filter|map|forEach|some|every|find)\s*\(\s*(?:async\s+)?(?:\(\s*(?<param>[A-Za-z_$][A-Za-z0-9_$]*)(?:\s*,[^)]*)?\s*\)|(?<param>[A-Za-z_$][A-Za-z0-9_$]*))\s*=>",
        RegexOptions.Compiled);
    private static readonly Regex JsMemberAccessRegex = new(
        @"\b(?<var>[A-Za-z_$][A-Za-z0-9_$]*)\s*\??\.\s*(?<member>[A-Za-z_$][A-Za-z0-9_$]*)\b",
        RegexOptions.Compiled);
    private static readonly Regex JsDocParamRegex = new(
        @"@param\s+\{(?<type>[^}\r\n]+)\}\s+(?<name>\[?[A-Za-z_$][A-Za-z0-9_$]*(?:=[^\]\s]+)?\]?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsDocReturnsRegex = new(
        @"@returns?\s+\{(?<type>[^}\r\n]+)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> KnownMcpCallInputFields = new(StringComparer.Ordinal)
    {
        "server", "kind", "method", "methods", "request", "request_template", "template_data",
        "timeout_ms", "prompt", "provider", "model", "temperature", "tools", "prompts",
        "structured_output", "raise_on_error", "raiseOnError", "error_policy",
        "detect_result_errors", "detectResultErrors"
    };

    public static void Validate(
        WorkflowDocument document,
        IReadOnlyList<McpToolOutputContract>? mcpToolContracts = null) =>
        ValidateWithStepContracts(document, mcpToolContracts, BuiltInStepContracts.All);

    public static void ValidateWithStepContracts(
        WorkflowDocument document,
        IReadOnlyList<McpToolOutputContract>? mcpToolContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        var errors = new List<WorkflowSemanticValidationError>();
        var mcpContracts = BuildMcpContractLookup(mcpToolContracts);
        ValidateFunctionJsDoc(document.Functions, workflowName: null, errors);
        var globalFunctionNames = BuildAllowedFunctionNames(document.Functions);
        var globalFunctionDefinitions = BuildFunctionDefinitions(document.Functions, "functions");

        foreach (var (workflowName, workflow) in document.Workflows)
        {
            var allStepIds = CollectStepIds(workflow.Steps).ToHashSet(StringComparer.Ordinal);
            var knownEmptyStringReferences = CollectKnownEmptySetStringReferences(workflow.Steps);
            var knownContracts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var symbols = WorkflowSymbolTable.Create(workflowName, workflow.Inputs, allStepIds);
            ValidateFunctionJsDoc(workflow.Functions, workflowName, errors);
            var allowedFunctionNames = BuildAllowedFunctionNames(workflow.Functions, globalFunctionNames);
            var functionDefinitions = BuildFunctionDefinitions(workflow.Functions, $"workflows.{workflowName}.functions", globalFunctionDefinitions);
            ValidateLoopResultsFunctionCalls(workflow.Steps, workflowName, functionDefinitions, errors);
            ValidateStepList(
                workflow.Steps,
                workflowName,
                document.Workflows,
                workflow.Inputs,
                knownContracts,
                symbols,
                allStepIds,
                allowedFunctionNames,
                knownEmptyStringReferences,
                mcpContracts,
                stepContracts,
                errors);

            if (workflow.Outputs != null)
            {
                foreach (var (outputName, outputDef) in workflow.Outputs)
                    ValidateOutputDef(
                        outputDef,
                        workflowName,
                        $"outputs.{outputName}",
                        workflow.Inputs,
                        knownContracts,
                        symbols,
                        allStepIds,
                        allowedFunctionNames,
                        errors);
            }
        }

        if (errors.Count > 0)
            throw new WorkflowSemanticValidationException(errors);
    }

    public static int NormalizeMcpCallInputRequests(WorkflowDocument document, IReadOnlyList<McpToolOutputContract>? mcpToolContracts = null)
    {
        var mcpContracts = BuildMcpContractLookup(mcpToolContracts);
        var changes = 0;

        foreach (var workflow in document.Workflows.Values)
            changes += NormalizeMcpCallInputRequests(workflow.Steps, mcpContracts);

        return changes;
    }

    internal static string FormatErrors(IReadOnlyList<WorkflowSemanticValidationError> errors)
    {
        var root = new JsonObject
        {
            ["error"] = "Generated workflow semantic validation failed",
            ["errors"] = new JsonArray(errors.Select(error => (JsonNode)new JsonObject
            {
                ["code"] = error.Code,
                ["workflow"] = error.WorkflowName,
                ["step"] = error.StepId,
                ["field"] = error.Field,
                ["invalid_path"] = error.InvalidPath,
                ["allowed_paths"] = new JsonArray(error.AllowedPaths.Select(path => (JsonNode)JsonValue.Create(path)).ToArray()),
                ["suggestion"] = error.Suggestion,
                ["message"] = error.Message
            }).ToArray())
        };

        return root.ToJsonString();
    }

    private static Dictionary<(string ServerName, string ToolName), McpToolOutputContract> BuildMcpContractLookup(IReadOnlyList<McpToolOutputContract>? contracts)
    {
        var lookup = new Dictionary<(string ServerName, string ToolName), McpToolOutputContract>();
        if (contracts == null)
            return lookup;

        foreach (var contract in contracts)
            lookup[(contract.ServerName, contract.ToolName)] = contract;
        return lookup;
    }

    private static HashSet<string> BuildAllowedFunctionNames(string? script, HashSet<string>? inherited = null)
    {
        var names = inherited == null
            ? new HashSet<string>(BuiltInFunctions.All.Keys, StringComparer.Ordinal)
            : new HashSet<string>(inherited, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(script))
            return names;

        foreach (var declaration in EnumerateFunctionDeclarations(script))
            names.Add(declaration.Name);

        return names;
    }

    private sealed record FunctionDeclaration(string Name, IReadOnlyList<string> Parameters, int Index, int SignatureEndIndex);

    private sealed record FunctionDefinition(string Name, IReadOnlyList<string> Parameters, string Body, string SourcePath);

    private static IEnumerable<FunctionDeclaration> EnumerateFunctionDeclarations(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            yield break;

        foreach (Match match in FunctionDeclarationRegex.Matches(script))
        {
            var name = match.Groups["name"].Value;
            var parameters = ParseFunctionParameters(match.Groups["params"].Value);
            yield return new FunctionDeclaration(name, parameters, match.Index, match.Index + match.Length);
        }
    }

    private static Dictionary<string, FunctionDefinition> BuildFunctionDefinitions(
        string? script,
        string sourcePath,
        IReadOnlyDictionary<string, FunctionDefinition>? inherited = null)
    {
        var definitions = inherited == null
            ? new Dictionary<string, FunctionDefinition>(StringComparer.Ordinal)
            : new Dictionary<string, FunctionDefinition>(inherited, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(script))
            return definitions;

        foreach (var declaration in EnumerateFunctionDeclarations(script))
        {
            var body = TryExtractFunctionBody(script, declaration.SignatureEndIndex);
            if (body == null)
                continue;

            definitions[declaration.Name] = new FunctionDefinition(
                declaration.Name,
                declaration.Parameters,
                body,
                $"{sourcePath}.{declaration.Name}");
        }

        return definitions;
    }

    private static string? TryExtractFunctionBody(string script, int signatureEndIndex)
    {
        var openBraceIndex = script.IndexOf('{', signatureEndIndex);
        if (openBraceIndex < 0)
            return null;

        var depth = 0;
        for (var i = openBraceIndex; i < script.Length; i++)
        {
            var ch = script[i];
            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch != '}')
                continue;

            depth--;
            if (depth == 0)
                return script.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
        }

        return script[(openBraceIndex + 1)..];
    }

    private static IReadOnlyList<string> ParseFunctionParameters(string rawParameters)
    {
        if (string.IsNullOrWhiteSpace(rawParameters))
            return Array.Empty<string>();

        return rawParameters
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeFunctionParameter)
            .Where(static parameter => !string.IsNullOrWhiteSpace(parameter))
            .ToArray();
    }

    private static string NormalizeFunctionParameter(string parameter)
    {
        var candidate = parameter.Trim();
        if (candidate.StartsWith("...", StringComparison.Ordinal))
            candidate = candidate[3..].TrimStart();

        var defaultIndex = candidate.IndexOf('=');
        if (defaultIndex >= 0)
            candidate = candidate[..defaultIndex].TrimEnd();

        var match = FunctionParameterIdentifierRegex.Match(candidate);
        return match.Success ? match.Value : candidate;
    }

    private static void ValidateFunctionJsDoc(
        string? script,
        string? workflowName,
        List<WorkflowSemanticValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(script))
            return;

        foreach (var declaration in EnumerateFunctionDeclarations(script))
        {
            var field = $"functions.{declaration.Name}";
            var invalidPath = workflowName == null
                ? $"functions.{declaration.Name}"
                : $"workflows.{workflowName}.functions.{declaration.Name}";
            var jsDoc = FindLeadingJsDoc(script, declaration.Index);

            if (string.IsNullOrWhiteSpace(jsDoc))
            {
                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = "FUNCTION_JSDOC_MISSING",
                    WorkflowName = workflowName,
                    Field = field,
                    InvalidPath = invalidPath,
                    AllowedPaths = Array.Empty<string>(),
                    Suggestion = BuildFunctionJsDocSuggestion(declaration),
                    Message = $"Custom function `{declaration.Name}` must be immediately preceded by JSDoc documenting its input and output contract."
                });
                continue;
            }

            var documentedParameters = ParseJsDocParamTypes(jsDoc);
            foreach (var parameter in declaration.Parameters)
            {
                if (documentedParameters.TryGetValue(parameter, out var parameterType)
                    && !string.IsNullOrWhiteSpace(parameterType))
                {
                    continue;
                }

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = "FUNCTION_JSDOC_PARAM_MISSING",
                    WorkflowName = workflowName,
                    Field = field,
                    InvalidPath = $"{invalidPath}.{parameter}",
                    AllowedPaths = Array.Empty<string>(),
                    Suggestion = $"Add `@param {{type}} {parameter} - ...` to the JSDoc for function `{declaration.Name}`.",
                    Message = $"JSDoc for custom function `{declaration.Name}` must document parameter `{parameter}` with an explicit type."
                });
            }

            if (!HasTypedJsDocReturn(jsDoc))
            {
                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = "FUNCTION_JSDOC_RETURNS_MISSING",
                    WorkflowName = workflowName,
                    Field = field,
                    InvalidPath = $"{invalidPath}.return",
                    AllowedPaths = Array.Empty<string>(),
                    Suggestion = $"Add `@returns {{type}} - ...` to the JSDoc for function `{declaration.Name}`.",
                    Message = $"JSDoc for custom function `{declaration.Name}` must document the return value with an explicit type."
                });
            }
        }
    }

    private static string? FindLeadingJsDoc(string script, int functionIndex)
    {
        var end = functionIndex;
        while (end > 0 && char.IsWhiteSpace(script[end - 1]))
            end--;

        if (end < 2 || script[end - 1] != '/' || script[end - 2] != '*')
            return null;

        var start = script.LastIndexOf("/*", end - 1, StringComparison.Ordinal);
        if (start < 0 || start + 2 >= script.Length || script[start + 2] != '*')
            return null;

        var candidate = script[start..end].Trim();
        return candidate.StartsWith("/**", StringComparison.Ordinal) && candidate.EndsWith("*/", StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static Dictionary<string, string> ParseJsDocParamTypes(string jsDoc)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in JsDocParamRegex.Matches(jsDoc))
        {
            var name = NormalizeJsDocParameterName(match.Groups["name"].Value);
            var type = match.Groups["type"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !parameters.ContainsKey(name))
                parameters[name] = type;
        }

        return parameters;
    }

    private static string NormalizeJsDocParameterName(string name)
    {
        var candidate = name.Trim();
        if (candidate.StartsWith("[", StringComparison.Ordinal))
            candidate = candidate[1..];
        if (candidate.EndsWith("]", StringComparison.Ordinal))
            candidate = candidate[..^1];

        var defaultIndex = candidate.IndexOf('=');
        if (defaultIndex >= 0)
            candidate = candidate[..defaultIndex];

        return candidate.Trim();
    }

    private static bool HasTypedJsDocReturn(string jsDoc)
    {
        var match = JsDocReturnsRegex.Match(jsDoc);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["type"].Value);
    }

    private static string BuildFunctionJsDocSuggestion(FunctionDeclaration declaration)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Add a JSDoc block immediately before `function {declaration.Name}(...)`.");
        sb.AppendLine("Example:");
        sb.AppendLine("/**");
        sb.AppendLine($" * Describe what `{declaration.Name}` computes and any constraints.");
        foreach (var parameter in declaration.Parameters)
            sb.AppendLine($" * @param {{type}} {parameter} - Describe this input.");
        sb.AppendLine(" * @returns {type} Describe the returned value.");
        sb.AppendLine(" */");
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> CollectStepIds(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Id))
                yield return step.Id;

            if (step.Steps != null)
                foreach (var id in CollectStepIds(step.Steps))
                    yield return id;

            if (step.Branches != null)
                foreach (var id in step.Branches.SelectMany(branch => CollectStepIds(branch.Steps)))
                    yield return id;

            if (step.Cases != null)
                foreach (var id in step.Cases.SelectMany(@case => CollectStepIds(@case.Steps)))
                    yield return id;

            if (step.Default != null)
                foreach (var id in CollectStepIds(step.Default))
                    yield return id;
        }
    }

    private static IReadOnlySet<string> CollectKnownEmptySetStringReferences(IEnumerable<StepDef> steps)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        AddKnownEmptySetStringReferences(steps, result);
        return result;
    }

    private static void AddKnownEmptySetStringReferences(
        IEnumerable<StepDef> steps,
        HashSet<string> result)
    {
        foreach (var step in steps)
        {
            if (string.Equals(step.Type, "set", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(step.Id)
                && step.Input != null)
            {
                AddKnownEmptyStringReferences(step.Input, $"data.steps.{step.Id}", result);
            }

            if (step.Steps != null)
                AddKnownEmptySetStringReferences(step.Steps, result);

            if (step.Branches != null)
                foreach (var branch in step.Branches)
                    AddKnownEmptySetStringReferences(branch.Steps, result);

            if (step.Cases != null)
                foreach (var @case in step.Cases)
                    AddKnownEmptySetStringReferences(@case.Steps, result);

            if (step.Default != null)
                AddKnownEmptySetStringReferences(step.Default, result);
        }
    }

    private static void AddKnownEmptyStringReferences(
        JsonNode? node,
        string path,
        HashSet<string> result)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length == 0:
                result.Add(path);
                break;
            case JsonObject obj:
                foreach (var (propertyName, propertyValue) in obj)
                    AddKnownEmptyStringReferences(propertyValue, $"{path}.{propertyName}", result);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    AddKnownEmptyStringReferences(array[i], $"{path}[{i}]", result);
                break;
        }
    }

    private static void ValidateLoopResultsFunctionCalls(
        IReadOnlyList<StepDef> steps,
        string workflowName,
        IReadOnlyDictionary<string, FunctionDefinition> functionDefinitions,
        List<WorkflowSemanticValidationError> errors)
    {
        var loopResultFields = BuildLoopResultFields(steps);
        if (loopResultFields.Count == 0 || functionDefinitions.Count == 0)
            return;

        ValidateLoopResultsFunctionCallsInSteps(steps, workflowName, functionDefinitions, loopResultFields, errors);
    }

    private static Dictionary<string, HashSet<string>> BuildLoopResultFields(IReadOnlyList<StepDef> steps)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        AddLoopResultFields(steps, result);
        return result;
    }

    private static void AddLoopResultFields(
        IReadOnlyList<StepDef> steps,
        Dictionary<string, HashSet<string>> result)
    {
        foreach (var step in steps)
        {
            if (step.Type is "loop.sequential" or "loop.parallel"
                && !string.IsNullOrWhiteSpace(step.Id)
                && step.Steps != null)
            {
                result[step.Id] = CollectStepIds(step.Steps).ToHashSet(StringComparer.Ordinal);
            }

            if (step.Steps != null)
                AddLoopResultFields(step.Steps, result);

            if (step.Branches != null)
                foreach (var branch in step.Branches)
                    AddLoopResultFields(branch.Steps, result);

            if (step.Cases != null)
                foreach (var @case in step.Cases)
                    AddLoopResultFields(@case.Steps, result);

            if (step.Default != null)
                AddLoopResultFields(step.Default, result);
        }
    }

    private static void ValidateLoopResultsFunctionCallsInSteps(
        IReadOnlyList<StepDef> steps,
        string workflowName,
        IReadOnlyDictionary<string, FunctionDefinition> functionDefinitions,
        IReadOnlyDictionary<string, HashSet<string>> loopResultFields,
        List<WorkflowSemanticValidationError> errors)
    {
        foreach (var step in steps)
        {
            ValidateLoopResultsFunctionCallsInText(step.If, workflowName, step.Id, "if", functionDefinitions, loopResultFields, errors);
            ValidateLoopResultsFunctionCallsInText(step.Expr, workflowName, step.Id, "expr", functionDefinitions, loopResultFields, errors);
            ValidateLoopResultsFunctionCallsInText(step.Output, workflowName, step.Id, "output", functionDefinitions, loopResultFields, errors);
            ValidateLoopResultsFunctionCallsInJson(step.Input, workflowName, step.Id, "input", functionDefinitions, loopResultFields, errors);

            if (step.OnError != null)
            {
                for (var i = 0; i < step.OnError.Cases.Count; i++)
                {
                    var onErrorCase = step.OnError.Cases[i];
                    ValidateLoopResultsFunctionCallsInText(onErrorCase.If, workflowName, step.Id, $"on_error.cases[{i}].if", functionDefinitions, loopResultFields, errors);
                    ValidateLoopResultsFunctionCallsInJson(onErrorCase.SetOutput, workflowName, step.Id, $"on_error.cases[{i}].set_output", functionDefinitions, loopResultFields, errors);
                }
            }

            if (step.Steps != null)
                ValidateLoopResultsFunctionCallsInSteps(step.Steps, workflowName, functionDefinitions, loopResultFields, errors);

            if (step.Branches != null)
                foreach (var branch in step.Branches)
                    ValidateLoopResultsFunctionCallsInSteps(branch.Steps, workflowName, functionDefinitions, loopResultFields, errors);

            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                {
                    ValidateLoopResultsFunctionCallsInText(@case.When, workflowName, step.Id, "cases.when", functionDefinitions, loopResultFields, errors);
                    ValidateLoopResultsFunctionCallsInSteps(@case.Steps, workflowName, functionDefinitions, loopResultFields, errors);
                }
            }

            if (step.Default != null)
                ValidateLoopResultsFunctionCallsInSteps(step.Default, workflowName, functionDefinitions, loopResultFields, errors);
        }
    }

    private static void ValidateLoopResultsFunctionCallsInJson(
        JsonNode? node,
        string workflowName,
        string? stepId,
        string field,
        IReadOnlyDictionary<string, FunctionDefinition> functionDefinitions,
        IReadOnlyDictionary<string, HashSet<string>> loopResultFields,
        List<WorkflowSemanticValidationError> errors)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                ValidateLoopResultsFunctionCallsInText(text, workflowName, stepId, field, functionDefinitions, loopResultFields, errors);
                break;
            case JsonObject obj:
                foreach (var (key, child) in obj)
                    ValidateLoopResultsFunctionCallsInJson(child, workflowName, stepId, $"{field}.{key}", functionDefinitions, loopResultFields, errors);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    ValidateLoopResultsFunctionCallsInJson(array[i], workflowName, stepId, $"{field}[{i}]", functionDefinitions, loopResultFields, errors);
                break;
        }
    }

    private static void ValidateLoopResultsFunctionCallsInText(
        string? text,
        string workflowName,
        string? stepId,
        string field,
        IReadOnlyDictionary<string, FunctionDefinition> functionDefinitions,
        IReadOnlyDictionary<string, HashSet<string>> loopResultFields,
        List<WorkflowSemanticValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in LoopResultsFunctionCallRegex.Matches(text))
        {
            var functionName = match.Groups["function"].Value;
            var loopStepId = match.Groups["loop"].Value;
            if (!functionDefinitions.TryGetValue(functionName, out var definition)
                || !loopResultFields.TryGetValue(loopStepId, out var allowedFields)
                || allowedFields.Count == 0)
            {
                continue;
            }

            var invalidAccess = FindInvalidDirectLoopResultMemberAccess(definition.Body, allowedFields);
            if (invalidAccess == null)
                continue;

            var firstAllowed = allowedFields.OrderBy(static fieldName => fieldName, StringComparer.Ordinal).First();
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "LOOP_RESULTS_FUNCTION_FIELD_ACCESS",
                WorkflowName = workflowName,
                StepId = stepId,
                Field = field,
                InvalidPath = $"{definition.SourcePath}.{invalidAccess.Value.Parameter}.{invalidAccess.Value.Member}",
                AllowedPaths = allowedFields
                    .OrderBy(static fieldName => fieldName, StringComparer.Ordinal)
                    .Select(static fieldName => $"iteration.{fieldName}")
                    .ToArray(),
                Suggestion = $"Loop result items are per-iteration step output snapshots. Read fields under the child step id, for example `iteration.{firstAllowed}.<field>`, or flatten the loop output with a typed set step before filtering.",
                Message = $"Function `{functionName}` is called with `data.steps.{loopStepId}.results` but reads `{invalidAccess.Value.Parameter}.{invalidAccess.Value.Member}` directly. Loop result items expose child step outputs such as `{firstAllowed}`, not direct item fields."
            });
        }
    }

    private static (string Parameter, string Member)? FindInvalidDirectLoopResultMemberAccess(
        string body,
        IReadOnlySet<string> allowedTopLevelFields)
    {
        var callbackParameters = EnumerateCallbackParameters(body).ToHashSet(StringComparer.Ordinal);
        if (callbackParameters.Count == 0)
            return null;

        foreach (Match match in JsMemberAccessRegex.Matches(body))
        {
            var variableName = match.Groups["var"].Value;
            if (!callbackParameters.Contains(variableName))
                continue;

            var memberName = match.Groups["member"].Value;
            if (allowedTopLevelFields.Contains(memberName))
                continue;

            return (variableName, memberName);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCallbackParameters(string body)
    {
        foreach (Match match in JsFunctionCallbackParameterRegex.Matches(body))
            yield return match.Groups["param"].Value;

        foreach (Match match in JsArrowCallbackParameterRegex.Matches(body))
            yield return match.Groups["param"].Value;
    }

    private static void ValidateStepList(
        IReadOnlyList<StepDef> steps,
        string workflowName,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        Dictionary<string, JsonNode?> knownContracts,
        WorkflowSymbolTable symbols,
        HashSet<string> allStepIds,
        HashSet<string> allowedFunctionNames,
        IReadOnlySet<string> knownEmptyStringReferences,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        foreach (var step in steps)
            ValidateStep(step, workflowName, workflows, workflowInputs, knownContracts, symbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
    }

    private static int NormalizeMcpCallInputRequests(
        IReadOnlyList<StepDef> steps,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        var changes = 0;
        foreach (var step in steps)
        {
            changes += NormalizeMcpCallInputRequest(step, mcpContracts);

            if (step.Steps != null)
                changes += NormalizeMcpCallInputRequests(step.Steps, mcpContracts);

            if (step.Branches != null)
                foreach (var branch in step.Branches)
                    changes += NormalizeMcpCallInputRequests(branch.Steps, mcpContracts);

            if (step.Cases != null)
                foreach (var @case in step.Cases)
                    changes += NormalizeMcpCallInputRequests(@case.Steps, mcpContracts);

            if (step.Default != null)
                changes += NormalizeMcpCallInputRequests(step.Default, mcpContracts);
        }

        return changes;
    }

    private static int NormalizeMcpCallInputRequest(
        StepDef step,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        if (!string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)
            || step.Input is not JsonObject input)
        {
            return 0;
        }

        var kind = TryGetInputString(step, "kind") ?? "tool";
        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return 0;

        var serverName = TryGetInputString(step, "server");
        var methodName = TryGetInputString(step, "method");
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(methodName))
            return 0;

        if (!mcpContracts.TryGetValue((serverName, methodName), out var contract)
            || contract.InputSchema is not JsonObject inputSchema
            || input["request"] is not JsonObject requestObject)
        {
            return 0;
        }

        return NormalizeJsonNodeAgainstSchema(requestObject, inputSchema);
    }

    private static void ValidateStep(
        StepDef step,
        string workflowName,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        Dictionary<string, JsonNode?> knownContracts,
        WorkflowSymbolTable symbols,
        HashSet<string> allStepIds,
        HashSet<string> allowedFunctionNames,
        IReadOnlySet<string> knownEmptyStringReferences,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        ValidateString(step.If, workflowName, step.Id, "if", symbols, allowedFunctionNames, errors);
        ValidateString(step.Expr, workflowName, step.Id, "expr", symbols, allowedFunctionNames, errors);
        ValidateJson(step.Input, workflowName, step.Id, "input", symbols, allowedFunctionNames, errors);
        var nonNullReferences = StepExpressionTypeValidator.InferNonNullReferencesFromGuard(step.If);
        ValidateMcpCallInputRequest(step, workflowName, workflowInputs, symbols, nonNullReferences, knownEmptyStringReferences, mcpContracts, errors);
        ValidateLocalWorkflowCallInput(step, workflowName, workflows, workflowInputs, symbols, nonNullReferences, knownEmptyStringReferences, errors);
        ValidateSetOutputSchema(step, workflowName, symbols, nonNullReferences, errors);
        if (stepContracts.TryGetValue(step.Type, out var stepContract))
        {
            foreach (var mismatch in StepExpressionTypeValidator.ValidateInput(
                         step.Input,
                         stepContract.InputType,
                         symbols.WorkflowInputs,
                         symbols.StepOutputs,
                         symbols.DataVariables,
                         nonNullReferences))
            {
                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = ErrorCodes.ExprTypeMismatch,
                    WorkflowName = workflowName,
                    StepId = step.Id,
                    Field = mismatch.Field,
                    InvalidPath = mismatch.Expression,
                    AllowedPaths = Array.Empty<string>(),
                    Suggestion = $"Provide a {mismatch.ExpectedType} value or reference an expression with a compatible output type.",
                    Message = mismatch.Message
                });
            }
        }
        AddExpressionTypeMismatch(
            StepExpressionTypeValidator.ValidateExpression(
                step.If,
                "if",
                FlowTypeDescriptor.Boolean,
                symbols.WorkflowInputs,
                symbols.StepOutputs,
                symbols.DataVariables),
            workflowName,
            step.Id,
            errors);

        var stepIsConditional = !string.IsNullOrWhiteSpace(step.If);
        FlowTypeDescriptor? resolvedStepOutputType = null;

        if (step.OnError != null)
        {
            for (var i = 0; i < step.OnError.Cases.Count; i++)
            {
                var onErrorCase = step.OnError.Cases[i];
                ValidateString(onErrorCase.If, workflowName, step.Id, $"on_error.cases[{i}].if", symbols, allowedFunctionNames, errors);
                ValidateJson(onErrorCase.SetOutput, workflowName, step.Id, $"on_error.cases[{i}].set_output", symbols, allowedFunctionNames, errors);
            }
        }

        if (step.Type == "parallel" && step.Branches != null)
        {
            var branchProducedContracts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var branch in step.Branches)
            {
                var branchKnown = CloneContracts(knownContracts);
                var branchSymbols = symbols.Clone();
                ValidateStepList(branch.Steps, workflowName, workflows, workflowInputs, branchKnown, branchSymbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
                foreach (var produced in branchKnown.Where(kv => !knownContracts.ContainsKey(kv.Key)))
                    branchProducedContracts[produced.Key] = produced.Value?.DeepClone();
            }

            if (!stepIsConditional)
            {
                foreach (var produced in branchProducedContracts)
                {
                    knownContracts[produced.Key] = produced.Value?.DeepClone();
                    symbols.SetStepOutput(produced.Key, FlowTypeDescriptorConverter.FromJsonSchema(produced.Value));
                }
            }
        }
        else if (step.Type == "switch")
        {
            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                {
                    ValidateString(@case.When, workflowName, step.Id, "cases.when", symbols, allowedFunctionNames, errors);
                    AddExpressionTypeMismatch(
                        StepExpressionTypeValidator.ValidateExpression(
                            @case.When,
                            "cases.when",
                            FlowTypeDescriptor.Boolean,
                            symbols.WorkflowInputs,
                            symbols.StepOutputs,
                            symbols.DataVariables),
                        workflowName,
                        step.Id,
                        errors);

                    // Only one switch branch runs at runtime, so branch-local step outputs are not
                    // guaranteed mappings after the switch. Validate each branch independently.
                    var caseKnown = CloneContracts(knownContracts);
                    var caseSymbols = symbols.Clone();
                    ValidateStepList(@case.Steps, workflowName, workflows, workflowInputs, caseKnown, caseSymbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
                }
            }

            if (step.Default != null)
            {
                var defaultKnown = CloneContracts(knownContracts);
                var defaultSymbols = symbols.Clone();
                ValidateStepList(step.Default, workflowName, workflows, workflowInputs, defaultKnown, defaultSymbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
            }
        }
        else if (step.Type is "loop.sequential" or "loop.parallel")
        {
            if (step.Steps != null)
            {
                // Loop bodies may execute zero times, so their inner step outputs are not guaranteed
                // mappings after the loop. References inside the loop are still validated in order.
                var loopKnown = CloneContracts(knownContracts);
                var loopSymbols = symbols.Clone();
                AddLoopScopedDataVariables(step, loopSymbols, symbols);
                ValidateStepList(step.Steps, workflowName, workflows, workflowInputs, loopKnown, loopSymbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
                resolvedStepOutputType = BuildLoopOutputType(loopSymbols);
            }
        }
        else
        {
            if (step.Steps != null)
            {
                if (stepIsConditional)
                {
                    var conditionalKnown = CloneContracts(knownContracts);
                    var conditionalSymbols = symbols.Clone();
                    ValidateStepList(step.Steps, workflowName, workflows, workflowInputs, conditionalKnown, conditionalSymbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
                }
                else
                {
                    ValidateStepList(step.Steps, workflowName, workflows, workflowInputs, knownContracts, symbols, allStepIds, allowedFunctionNames, knownEmptyStringReferences, mcpContracts, stepContracts, errors);
                }
            }
        }

        if (!stepIsConditional && !string.IsNullOrWhiteSpace(step.Id))
        {
            var outputType = resolvedStepOutputType ?? BuildStepOutputType(
                step,
                workflows,
                symbols,
                mcpContracts,
                stepContracts);
            var outputSchema = FlowTypeDescriptorConverter.ToRuntimeJsonSchema(outputType);
            knownContracts[step.Id] = outputSchema;
            symbols.SetStepOutput(step.Id, outputType);
            if (!string.IsNullOrWhiteSpace(step.Output))
                symbols.SetDataVariable(step.Output, outputType);
        }
    }

    private static void ValidateSetOutputSchema(
        StepDef step,
        string workflowName,
        WorkflowSymbolTable symbols,
        IReadOnlySet<string>? nonNullReferences,
        List<WorkflowSemanticValidationError> errors)
    {
        if (step.OutputSchema == null)
            return;

        if (!string.Equals(step.Type, "set", StringComparison.Ordinal))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "OUTPUT_SCHEMA_UNSUPPORTED",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "output_schema",
                InvalidPath = "output_schema",
                AllowedPaths = Array.Empty<string>(),
                Suggestion = "Remove output_schema or move the reshaping into a set step.",
                Message = "output_schema is currently supported only on set steps."
            });
            return;
        }

        if (step.OutputSchema is not JsonObject outputSchema)
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "SET_OUTPUT_SCHEMA_INVALID",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "output_schema",
                InvalidPath = "output_schema",
                AllowedPaths = Array.Empty<string>(),
                Suggestion = "Provide a valid object-root JSON Schema for the set step output.",
                Message = "set output_schema must be a JSON Schema object."
            });
            return;
        }

        foreach (var schemaError in JsonSchemaContractValidator.ValidateSchema(outputSchema, strictProfile: false))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "SET_OUTPUT_SCHEMA_INVALID",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "output_schema",
                InvalidPath = "output_schema",
                AllowedPaths = Array.Empty<string>(),
                Suggestion = "Provide a valid object-root JSON Schema for the set step output.",
                Message = $"set output_schema is invalid: {schemaError}"
            });
        }

        if (!DeclaresJsonObjectRoot(outputSchema))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "SET_OUTPUT_SCHEMA_INVALID",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "output_schema",
                InvalidPath = "output_schema.type",
                AllowedPaths = Array.Empty<string>(),
                Suggestion = "Declare `output_schema.type: object` for set output schemas.",
                Message = "set output_schema root must declare type: object."
            });
        }

        if (step.Input == null)
            return;

        var outputType = FlowTypeDescriptorConverter.FromJsonSchema(outputSchema);
        foreach (var mismatch in StepExpressionTypeValidator.ValidateInput(
                     step.Input,
                     outputType,
                     symbols.WorkflowInputs,
                     symbols.StepOutputs,
                     symbols.DataVariables,
                     nonNullReferences))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = ErrorCodes.ExprTypeMismatch,
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = $"input{mismatch.Field["input".Length..]}",
                InvalidPath = mismatch.Expression,
                AllowedPaths = Array.Empty<string>(),
                Suggestion = $"Align the set input value with output_schema, or update output_schema to declare a compatible {mismatch.ActualType} field.",
                Message = mismatch.Message.Replace("step contract", "set output_schema", StringComparison.Ordinal)
            });
        }

        var schemaErrors = new List<SchemaValidationError>();
        ValidateJsonNodeAgainstSchema(step.Input, outputSchema, "", schemaErrors);
        AddRequiredStringLiteralErrors(
            step.Input,
            outputSchema,
            "SET_REQUIRED_STRING_EMPTY",
            workflowName,
            step.Id,
            "input",
            "input",
            EnumerateAllowedPaths("input", outputSchema).Take(64).ToArray(),
            "Provide real values for required set string fields; do not use empty string placeholders.",
            "set input",
            errors);
        foreach (var schemaError in schemaErrors)
        {
            var invalidPath = string.IsNullOrEmpty(schemaError.Path)
                ? "input"
                : $"input.{schemaError.Path}";

            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "SET_OUTPUT_SCHEMA_MISMATCH",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "input",
                InvalidPath = invalidPath,
                AllowedPaths = EnumerateAllowedPaths("input", outputSchema).Take(64).ToArray(),
                Suggestion = "Make set.input match output_schema exactly, including required fields and additionalProperties.",
                Message = $"set input does not satisfy output_schema: {schemaError.Message}"
            });
        }
    }

    private static bool DeclaresJsonObjectRoot(JsonObject schemaObject)
    {
        if (schemaObject["type"] is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var typeName))
        {
            return string.Equals(typeName, "object", StringComparison.Ordinal);
        }

        return schemaObject["type"] is JsonArray types
            && types.Any(type => type is JsonValue value
                && value.TryGetValue<string>(out var name)
                && string.Equals(name, "object", StringComparison.Ordinal));
    }

    private static Dictionary<string, JsonNode?> CloneContracts(Dictionary<string, JsonNode?> source)
    {
        var clone = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
            clone[key] = value?.DeepClone();
        return clone;
    }

    private static void ValidateOutputDef(
        OutputDef outputDef,
        string workflowName,
        string field,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        Dictionary<string, JsonNode?> knownContracts,
        WorkflowSymbolTable symbols,
        HashSet<string> allStepIds,
        HashSet<string> allowedFunctionNames,
        List<WorkflowSemanticValidationError> errors)
    {
        ValidateString(outputDef.Expr, workflowName, null, field, symbols, allowedFunctionNames, errors);
        AddExpressionTypeMismatch(
            StepExpressionTypeValidator.ValidateExpression(
                outputDef.Expr,
                field,
                StepExpressionTypeValidator.OutputDefType(outputDef),
                symbols.WorkflowInputs,
                symbols.StepOutputs,
                symbols.DataVariables),
            workflowName,
            null,
            errors);

        if (outputDef.Properties != null)
        {
            foreach (var (propertyName, propertyDef) in outputDef.Properties)
                ValidateOutputDef(propertyDef, workflowName, $"{field}.properties.{propertyName}", workflowInputs, knownContracts, symbols, allStepIds, allowedFunctionNames, errors);
        }

        if (outputDef.Items != null)
            ValidateOutputDef(outputDef.Items, workflowName, $"{field}.items", workflowInputs, knownContracts, symbols, allStepIds, allowedFunctionNames, errors);

        if (outputDef.AdditionalProperties != null)
            ValidateOutputDef(outputDef.AdditionalProperties, workflowName, $"{field}.additional_properties", workflowInputs, knownContracts, symbols, allStepIds, allowedFunctionNames, errors);
    }

    private static void AddExpressionTypeMismatch(
        StepExpressionTypeMismatch? mismatch,
        string workflowName,
        string? stepId,
        List<WorkflowSemanticValidationError> errors)
    {
        if (mismatch == null)
            return;

        errors.Add(new WorkflowSemanticValidationError
        {
            Code = ErrorCodes.ExprTypeMismatch,
            WorkflowName = workflowName,
            StepId = stepId,
            Field = mismatch.Field,
            InvalidPath = mismatch.Expression,
            AllowedPaths = Array.Empty<string>(),
            Suggestion = BuildExpressionTypeMismatchSuggestion(mismatch),
            Message = mismatch.Message
        });
    }

    private static string BuildExpressionTypeMismatchSuggestion(StepExpressionTypeMismatch mismatch)
    {
        if (mismatch.ExpectedType.Contains("string", StringComparison.OrdinalIgnoreCase)
            && mismatch.Message.Contains("resolves to boolean", StringComparison.OrdinalIgnoreCase))
        {
            return "A comparison/predicate expression returns boolean. For string outputs, return a string-valued field/literal instead, or normalize an MCP/LLM response with structured_output and map data.steps.<normalizer>.json.<field>.";
        }

        return $"Provide a {mismatch.ExpectedType} value or reference an expression with a compatible output type.";
    }

    private static void ValidateJson(
        JsonNode? node,
        string workflowName,
        string? stepId,
        string field,
        WorkflowSymbolTable symbols,
        HashSet<string> allowedFunctionNames,
        List<WorkflowSemanticValidationError> errors)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                ValidateString(text, workflowName, stepId, field, symbols, allowedFunctionNames, errors);
                break;
            case JsonObject obj:
                foreach (var (key, child) in obj)
                    ValidateJson(child, workflowName, stepId, $"{field}.{key}", symbols, allowedFunctionNames, errors);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    ValidateJson(array[i], workflowName, stepId, $"{field}[{i}]", symbols, allowedFunctionNames, errors);
                break;
        }
    }

    private static void ValidateString(
        string? text,
        string workflowName,
        string? stepId,
        string field,
        WorkflowSymbolTable symbols,
        HashSet<string> allowedFunctionNames,
        List<WorkflowSemanticValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("${", StringComparison.Ordinal))
            return;

        foreach (Match expressionMatch in ExpressionRegex.Matches(text))
        {
            var expression = expressionMatch.Groups[1].Value;
            ValidateFunctionCalls(expression, workflowName, stepId, field, allowedFunctionNames, errors);

            if (expression.Contains("data.steps.", StringComparison.Ordinal))
            {
                foreach (Match pathMatch in DataStepsPathRegex.Matches(expression))
                {
                    var referencedStepId = pathMatch.Groups[1].Value;
                    var propertyPath = SplitPath(pathMatch.Groups["path"].Value);
                    var invalidPath = "data.steps." + referencedStepId + (propertyPath.Count == 0 ? "" : "." + string.Join('.', propertyPath));

                    if (!symbols.TryGetStepOutput(referencedStepId, out var schema))
                    {
                        var existsLater = symbols.AllStepIds.Contains(referencedStepId);
                        errors.Add(new WorkflowSemanticValidationError
                        {
                            Code = existsLater ? "STEP_REFERENCE_NOT_AVAILABLE" : "STEP_REFERENCE_UNKNOWN",
                            WorkflowName = workflowName,
                            StepId = stepId,
                            Field = field,
                            InvalidPath = invalidPath,
                            AllowedPaths = symbols.AvailableStepReferences(),
                            Suggestion = existsLater
                                ? $"Move this reference after step '{referencedStepId}' has executed, or move the producing step earlier."
                                : "Reference an existing previous step id, or add the missing producing step before this expression.",
                            Message = existsLater
                                ? $"Step '{referencedStepId}' exists but is not available at this point in execution."
                                : $"Step '{referencedStepId}' does not exist in this workflow."
                        });
                        continue;
                    }

                    var validation = ValidateSchemaPath(schema, propertyPath);
                    if (validation.IsValid)
                        continue;

                    errors.Add(new WorkflowSemanticValidationError
                    {
                        Code = validation.IsOpaqueResponse ? "OPAQUE_RESPONSE_DEEP_ACCESS" : "STEP_OUTPUT_PROPERTY_UNKNOWN",
                        WorkflowName = workflowName,
                        StepId = stepId,
                        Field = field,
                        InvalidPath = invalidPath,
                        AllowedPaths = symbols.AllowedPaths(referencedStepId),
                        Suggestion = validation.IsOpaqueResponse
                            ? BuildOpaqueOutputSuggestion(invalidPath, referencedStepId)
                            : $"Use one of the allowed paths for step '{referencedStepId}', or add a normalization step that produces the desired property with structured_output.",
                        Message = validation.Message
                    });
                }
            }

            foreach (Match pathMatch in DataVariablePathRegex.Matches(expression))
            {
                var variableName = pathMatch.Groups["name"].Value;
                if (variableName is "inputs" or "steps")
                    continue;
                if (!symbols.TryGetDataVariable(variableName, out var variableType))
                    continue;

                var propertyPath = SplitPath(pathMatch.Groups["path"].Value);
                var invalidPath = "data." + variableName + (propertyPath.Count == 0 ? "" : "." + string.Join('.', propertyPath));
                var validation = ValidateSchemaPath(variableType, propertyPath);
                if (validation.IsValid)
                    continue;

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = validation.IsOpaqueResponse ? "OPAQUE_DATA_VARIABLE_DEEP_ACCESS" : "DATA_VARIABLE_PROPERTY_UNKNOWN",
                    WorkflowName = workflowName,
                    StepId = stepId,
                    Field = field,
                    InvalidPath = invalidPath,
                    AllowedPaths = symbols.AllowedDataVariablePaths(variableName),
                    Suggestion = $"Use one of the allowed paths for data variable '{variableName}', or normalize the value before accessing nested fields.",
                    Message = validation.Message.Replace("Output path", "Data variable path", StringComparison.Ordinal)
                });
            }
        }
    }

    private static void ValidateFunctionCalls(
        string expression,
        string workflowName,
        string? stepId,
        string field,
        HashSet<string> allowedFunctionNames,
        List<WorkflowSemanticValidationError> errors)
    {
        foreach (Match match in NamespacedFunctionCallRegex.Matches(expression))
        {
            var functionName = match.Groups[1].Value;
            if (allowedFunctionNames.Contains(functionName))
                continue;

            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "EXPRESSION_FUNCTION_UNKNOWN",
                WorkflowName = workflowName,
                StepId = stepId,
                Field = field,
                InvalidPath = $"functions.{functionName}",
                AllowedPaths = allowedFunctionNames
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Select(name => "functions." + name)
                    .ToArray(),
                Suggestion = $"Define `function {functionName}(...)` in a document-level or workflow-level `functions:` block, or replace `functions.{functionName}(...)` with documented built-in functions and normal workflow steps.",
                Message = $"Expression calls unknown function `functions.{functionName}`."
            });
        }
    }

    private static IReadOnlyList<string> SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<string>();

        return path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string BuildOpaqueOutputSuggestion(string invalidPath, string referencedStepId)
    {
        var responseIndex = invalidPath.IndexOf(".response", StringComparison.Ordinal);
        if (responseIndex >= 0)
        {
            var responsePath = invalidPath[..responseIndex] + ".response";
            return $"Do not invent fields under '{responsePath}'. Use json({responsePath}), or add an llm.call normalization step with structured_output before accessing named fields.";
        }

        var stepPath = $"data.steps.{referencedStepId}";
        return $"Do not invent fields under opaque output '{stepPath}'. Pass the whole value onward, or add an llm.call normalization step with structured_output before accessing named fields.";
    }

    private static void ValidateMcpCallInputRequest(
        StepDef step,
        string workflowName,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        WorkflowSymbolTable symbols,
        IReadOnlySet<string>? nonNullReferences,
        IReadOnlySet<string> knownEmptyStringReferences,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        if (!string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)
            || step.Input is not JsonObject input)
        {
            return;
        }

        ValidateMcpCallInputEnvelope(step, workflowName, input, errors);

        var kind = TryGetInputString(step, "kind") ?? "tool";
        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return;

        var serverName = TryGetInputString(step, "server");
        if (string.IsNullOrWhiteSpace(serverName))
            return;

        var methodTargets = GetLiteralMcpMethodTargets(step);
        foreach (var (methodName, methodField) in methodTargets)
        {
            if (!ValidateMcpCallTargetExists(step, workflowName, serverName, methodName, methodField, mcpContracts, errors))
                continue;

            if (!mcpContracts.TryGetValue((serverName, methodName), out var contract)
                || contract.InputSchema is not JsonObject inputSchema)
            {
                continue;
            }

            // Templates and whole-object expressions cannot be checked statically. Their
            // fully rendered JsonNode is validated immediately before the runtime transport.
            if (input["request_template"] != null)
                continue;

            var requestNode = input["request"];
            var requestValue = requestNode ?? new JsonObject();
            foreach (var mismatch in StepExpressionTypeValidator.ValidateInput(
                         requestValue,
                         FlowTypeDescriptorConverter.FromJsonSchema(inputSchema),
                         symbols.WorkflowInputs,
                         symbols.StepOutputs,
                         symbols.DataVariables,
                         nonNullReferences))
            {
                var suffix = mismatch.Field.StartsWith("input", StringComparison.Ordinal)
                    ? mismatch.Field["input".Length..]
                    : $".{mismatch.Field}";
                var field = $"input.request{suffix}";

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = McpRequestExpressionTypeMismatchCode,
                    WorkflowName = workflowName,
                    StepId = step.Id,
                    Field = field,
                    InvalidPath = mismatch.Expression,
                    AllowedPaths = EnumerateAllowedPaths("input.request", inputSchema).Take(64).ToArray(),
                    Suggestion = BuildMcpRequestExpressionTypeSuggestion(mismatch, serverName, methodName, field),
                    Message = $"mcp.call request field '{field}' for '{serverName}/{methodName}' resolves to {mismatch.ActualType}, but the MCP input_schema requires {mismatch.ExpectedType}."
                });
            }

            var schemaErrors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(requestValue, inputSchema, "", schemaErrors);
            if (requestValue is JsonObject requestObject)
                ValidateMcpRequestCompatibilityConventions(requestObject, inputSchema, "", schemaErrors);
            AddRequiredStringLiteralErrors(
                requestValue,
                inputSchema,
                "MCP_REQUIRED_STRING_EMPTY",
                workflowName,
                step.Id,
                "input.request",
                "input.request",
                EnumerateAllowedPaths("input.request", inputSchema).Take(64).ToArray(),
                $"Provide real values for required MCP string fields for '{serverName}/{methodName}'; do not use empty string placeholders.",
                "mcp.call request",
                errors);
            AddRequiredStringKnownEmptyReferenceErrors(
                requestValue,
                inputSchema,
                "MCP_REQUIRED_STRING_EMPTY",
                workflowName,
                step.Id,
                "input.request",
                "input.request",
                EnumerateAllowedPaths("input.request", inputSchema).Take(64).ToArray(),
                $"Provide real values for required MCP string fields for '{serverName}/{methodName}'; do not pass known-empty placeholders.",
                "mcp.call request",
                knownEmptyStringReferences,
                errors);
            if (schemaErrors.Count == 0)
                continue;

            foreach (var schemaError in schemaErrors)
            {
                var invalidPath = string.IsNullOrEmpty(schemaError.Path)
                    ? "input.request"
                    : $"input.request.{schemaError.Path}";

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = "MCP_REQUEST_SCHEMA_INVALID",
                    WorkflowName = workflowName,
                    StepId = step.Id,
                    Field = "input.request",
                    InvalidPath = invalidPath,
                    AllowedPaths = EnumerateAllowedPaths("input.request", inputSchema).Take(64).ToArray(),
                    Suggestion = $"Align `mcp.call` request with MCP tool schema for server '{serverName}' method '{methodName}'.",
                    Message = $"mcp.call request for '{serverName}/{methodName}' is invalid: {schemaError.Message}"
                });
            }
        }
    }

    private static string BuildMcpRequestExpressionTypeSuggestion(
        StepExpressionTypeMismatch mismatch,
        string serverName,
        string methodName,
        string field)
    {
        if (mismatch.ActualType.Split(" or ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static type => string.Equals(type, "null", StringComparison.Ordinal)))
        {
            return $"Do not pass a nullable expression into required MCP field '{field}' for '{serverName}/{methodName}'. Make the upstream structured_output non-null, add a step guard that proves the exact expression is non-null, or normalize to a guaranteed {mismatch.ExpectedType} before the mcp.call.";
        }

        return $"Align MCP field '{field}' for '{serverName}/{methodName}' with the discovered input_schema, or add a normalization step that produces a {mismatch.ExpectedType} value.";
    }

    private static void ValidateLocalWorkflowCallInput(
        StepDef step,
        string workflowName,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        WorkflowSymbolTable symbols,
        IReadOnlySet<string>? nonNullReferences,
        IReadOnlySet<string> knownEmptyStringReferences,
        List<WorkflowSemanticValidationError> errors)
    {
        if (!string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            || step.Input is not JsonObject input
            || input["ref"] is not JsonObject refObject)
        {
            return;
        }

        var kind = TryGetLiteralString(refObject["kind"]) ?? "local";
        if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase))
            return;

        var targetName = TryGetLiteralString(refObject["name"]);
        if (string.IsNullOrWhiteSpace(targetName) || !workflows.TryGetValue(targetName, out var targetWorkflow))
            return;

        var argsNode = input["args"] ?? new JsonObject();
        if (IsDynamicExpressionString(argsNode))
            return;

        var inputType = StepExpressionTypeValidator.InputsObjectType(targetWorkflow.Inputs);
        var inputSchema = FlowTypeDescriptorConverter.ToRuntimeJsonSchema(inputType);
        foreach (var mismatch in StepExpressionTypeValidator.ValidateInput(
                     argsNode,
                     inputType,
                     symbols.WorkflowInputs,
                     symbols.StepOutputs,
                     symbols.DataVariables,
                     nonNullReferences))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = ErrorCodes.ExprTypeMismatch,
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = $"input.args{mismatch.Field["input".Length..]}",
                InvalidPath = mismatch.Expression,
                AllowedPaths = Array.Empty<string>(),
                Suggestion = $"Pass arguments compatible with local workflow '{targetName}' input contract.",
                Message = mismatch.Message.Replace("input", "workflow.call input.args", StringComparison.Ordinal)
            });
        }

        var schemaErrors = new List<SchemaValidationError>();
        ValidateJsonNodeAgainstSchema(argsNode, inputSchema, "", schemaErrors);
        AddRequiredStringLiteralErrors(
            argsNode,
            inputSchema,
            "WORKFLOW_CALL_REQUIRED_STRING_EMPTY",
            workflowName,
            step.Id,
            "input.args",
            "input.args",
            EnumerateAllowedPaths("input.args", inputSchema).Take(64).ToArray(),
            $"Pass values compatible with local workflow '{targetName}' input string constraints. Required string inputs must be non-empty.",
            $"workflow.call args for local workflow '{targetName}'",
            errors);
        AddRequiredStringKnownEmptyReferenceErrors(
            argsNode,
            inputSchema,
            "WORKFLOW_CALL_REQUIRED_STRING_EMPTY",
            workflowName,
            step.Id,
            "input.args",
            "input.args",
            EnumerateAllowedPaths("input.args", inputSchema).Take(64).ToArray(),
            $"Pass real values for required local workflow string inputs for '{targetName}'; do not pass known-empty placeholders.",
            $"workflow.call args for local workflow '{targetName}'",
            knownEmptyStringReferences,
            errors);
        if (schemaErrors.Count == 0)
            return;

        foreach (var schemaError in schemaErrors)
        {
            var invalidPath = string.IsNullOrEmpty(schemaError.Path)
                ? "input.args"
                : $"input.args.{schemaError.Path}";

            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "WORKFLOW_CALL_ARGS_INVALID",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "input.args",
                InvalidPath = invalidPath,
                AllowedPaths = EnumerateAllowedPaths("input.args", inputSchema).Take(64).ToArray(),
                Suggestion = $"Align workflow.call args with local workflow '{targetName}' inputs.",
                Message = $"workflow.call args for local workflow '{targetName}' are invalid: {schemaError.Message}"
            });
        }
    }

    private static string? TryGetLiteralString(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;

        return string.IsNullOrWhiteSpace(text) || text.Contains("$" + "{", StringComparison.Ordinal)
            ? null
            : text;
    }

    private static void ValidateMcpCallInputEnvelope(
        StepDef step,
        string workflowName,
        JsonObject input,
        List<WorkflowSemanticValidationError> errors)
    {
        foreach (var fieldName in input.Select(static property => property.Key))
        {
            if (KnownMcpCallInputFields.Contains(fieldName))
                continue;

            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "MCP_CALL_INPUT_FIELD_UNKNOWN",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = $"input.{fieldName}",
                InvalidPath = $"input.{fieldName}",
                AllowedPaths = KnownMcpCallInputFields.OrderBy(static name => name, StringComparer.Ordinal).Select(static name => $"input.{name}").ToArray(),
                Suggestion = $"Move tool argument '{fieldName}' under `input.request`, or remove it if it is not part of the tool request.",
                Message = $"mcp.call input contains unknown top-level field '{fieldName}'. Tool arguments must be nested under `input.request`."
            });
        }

        if (input.ContainsKey("method") && input.ContainsKey("methods"))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "MCP_CALL_SELECTION_CONFLICT",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "input.methods",
                InvalidPath = "input.method+input.methods",
                AllowedPaths = new[] { "input.method", "input.methods" },
                Suggestion = "Use either `method` for one capability or `methods` for a batch, but not both.",
                Message = "mcp.call input cannot define both 'method' and 'methods'."
            });
        }

        if (input.ContainsKey("request") && input.ContainsKey("request_template"))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "MCP_CALL_REQUEST_CONFLICT",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "input.request_template",
                InvalidPath = "input.request+input.request_template",
                AllowedPaths = new[] { "input.request", "input.request_template" },
                Suggestion = "Use either a structured `request` or a rendered `request_template`, but not both.",
                Message = "mcp.call input cannot define both 'request' and 'request_template'."
            });
        }

        if (input["error_policy"] is JsonObject errorPolicy)
        {
            foreach (var fieldName in errorPolicy.Select(static property => property.Key))
            {
                if (fieldName is "detect_result_errors" or "detectResultErrors")
                    continue;

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = "MCP_CALL_INPUT_FIELD_UNKNOWN",
                    WorkflowName = workflowName,
                    StepId = step.Id,
                    Field = $"input.error_policy.{fieldName}",
                    InvalidPath = $"input.error_policy.{fieldName}",
                    AllowedPaths = new[] { "input.error_policy.detect_result_errors" },
                    Suggestion = "Remove the unsupported MCP error-policy field.",
                    Message = $"mcp.call error_policy contains unknown field '{fieldName}'."
                });
            }
        }
    }

    private static IReadOnlyList<(string MethodName, string Field)> GetLiteralMcpMethodTargets(StepDef step)
    {
        if (step.Input is not JsonObject input)
            return Array.Empty<(string, string)>();

        // Runtime batch selection takes precedence whenever `methods` is present.
        if (input.ContainsKey("methods"))
        {
            if (input["methods"] is not JsonArray methods)
                return Array.Empty<(string, string)>();

            var targets = new List<(string, string)>();
            for (var i = 0; i < methods.Count; i++)
            {
                if (methods[i] is JsonValue value
                    && value.TryGetValue<string>(out var methodName)
                    && !string.IsNullOrWhiteSpace(methodName)
                    && !methodName.Contains("${", StringComparison.Ordinal))
                {
                    targets.Add((methodName, $"input.methods[{i}]"));
                }
            }

            return targets;
        }

        var singleMethod = TryGetInputString(step, "method");
        return string.IsNullOrWhiteSpace(singleMethod)
            ? Array.Empty<(string, string)>()
            : new[] { (singleMethod, "input.method") };
    }

    private static bool ValidateMcpCallTargetExists(
        StepDef step,
        string workflowName,
        string serverName,
        string methodName,
        string methodField,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        if (mcpContracts.Count == 0)
            return true;

        var knownServers = mcpContracts.Keys
            .Select(key => key.ServerName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (!knownServers.Contains(serverName, StringComparer.Ordinal))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "MCP_SERVER_UNKNOWN",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "input.server",
                InvalidPath = $"input.server:{serverName}",
                AllowedPaths = knownServers.Select(name => $"mcp.server:{name}").ToArray(),
                Suggestion = "Use one of the MCP servers discovered for this plan, or add an `mcp.list` discovery step before calling it.",
                Message = $"mcp.call references unknown MCP server '{serverName}'."
            });
            return false;
        }

        if (mcpContracts.ContainsKey((serverName, methodName)))
            return true;

        var knownMethods = mcpContracts.Keys
            .Where(key => string.Equals(key.ServerName, serverName, StringComparison.Ordinal))
            .Select(key => key.ToolName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        errors.Add(new WorkflowSemanticValidationError
        {
            Code = "MCP_METHOD_UNKNOWN",
            WorkflowName = workflowName,
            StepId = step.Id,
            Field = methodField,
            InvalidPath = $"{methodField}:{methodName}",
            AllowedPaths = knownMethods.Select(name => $"mcp.server:{serverName}.method:{name}").ToArray(),
            Suggestion = $"Use one of the tools discovered for MCP server '{serverName}', or inspect the server with `mcp.list` before calling it.",
            Message = $"mcp.call references unknown MCP method '{methodName}' on server '{serverName}'."
        });
        return false;
    }

    private sealed record SchemaValidationError(string Path, string Message);

    internal static IReadOnlyList<string> ValidateResolvedMcpRequest(JsonNode? request, JsonNode? inputSchema)
    {
        var errors = JsonSchemaContractValidator.ValidateInstance(request ?? new JsonObject(), inputSchema).ToList();
        if (request is JsonObject requestObject && inputSchema is JsonObject schemaObject)
        {
            var conventionErrors = new List<SchemaValidationError>();
            ValidateMcpRequestCompatibilityConventions(requestObject, schemaObject, "", conventionErrors);
            errors.AddRange(conventionErrors.Select(static error =>
                string.IsNullOrEmpty(error.Path) ? error.Message : $"{error.Path}: {error.Message}"));
        }

        return errors;
    }

    private static void AddRequiredStringLiteralErrors(
        JsonNode? value,
        JsonObject schema,
        string code,
        string workflowName,
        string? stepId,
        string field,
        string invalidPathPrefix,
        IReadOnlyList<string> allowedPaths,
        string suggestion,
        string subject,
        List<WorkflowSemanticValidationError> errors)
    {
        var emptyStringErrors = new List<SchemaValidationError>();
        ValidateRequiredStringLiteralsNotEmpty(value, schema, "", emptyStringErrors);
        foreach (var emptyStringError in emptyStringErrors)
        {
            var invalidPath = string.IsNullOrEmpty(emptyStringError.Path)
                ? invalidPathPrefix
                : $"{invalidPathPrefix}.{emptyStringError.Path}";

            errors.Add(new WorkflowSemanticValidationError
            {
                Code = code,
                WorkflowName = workflowName,
                StepId = stepId,
                Field = field,
                InvalidPath = invalidPath,
                AllowedPaths = allowedPaths,
                Suggestion = suggestion,
                Message = $"{subject} is invalid: {emptyStringError.Message}"
            });
        }
    }

    private static void AddRequiredStringKnownEmptyReferenceErrors(
        JsonNode? value,
        JsonObject schema,
        string code,
        string workflowName,
        string? stepId,
        string field,
        string invalidPathPrefix,
        IReadOnlyList<string> allowedPaths,
        string suggestion,
        string subject,
        IReadOnlySet<string> knownEmptyStringReferences,
        List<WorkflowSemanticValidationError> errors)
    {
        if (knownEmptyStringReferences.Count == 0)
            return;

        var emptyStringErrors = new List<SchemaValidationError>();
        ValidateRequiredStringReferencesNotKnownEmpty(value, schema, "", emptyStringErrors, knownEmptyStringReferences);
        foreach (var emptyStringError in emptyStringErrors)
        {
            var invalidPath = string.IsNullOrEmpty(emptyStringError.Path)
                ? invalidPathPrefix
                : $"{invalidPathPrefix}.{emptyStringError.Path}";

            errors.Add(new WorkflowSemanticValidationError
            {
                Code = code,
                WorkflowName = workflowName,
                StepId = stepId,
                Field = field,
                InvalidPath = invalidPath,
                AllowedPaths = allowedPaths,
                Suggestion = suggestion,
                Message = $"{subject} is invalid: {emptyStringError.Message}"
            });
        }
    }

    private static void ValidateRequiredStringReferencesNotKnownEmpty(
        JsonNode? value,
        JsonNode? schema,
        string path,
        List<SchemaValidationError> errors,
        IReadOnlySet<string> knownEmptyStringReferences)
    {
        if (schema is not JsonObject schemaObject)
            return;

        if (schemaObject["allOf"] is JsonArray allOf)
        {
            foreach (var variant in allOf)
                ValidateRequiredStringReferencesNotKnownEmpty(value, variant, path, errors, knownEmptyStringReferences);
        }

        if (schemaObject["anyOf"] is JsonArray anyOf)
        {
            foreach (var variant in anyOf)
                ValidateRequiredStringReferencesNotKnownEmpty(value, variant, path, errors, knownEmptyStringReferences);
        }

        if (schemaObject["oneOf"] is JsonArray oneOf)
        {
            foreach (var variant in oneOf)
                ValidateRequiredStringReferencesNotKnownEmpty(value, variant, path, errors, knownEmptyStringReferences);
        }

        var typeName = ReadApplicableSchemaType(schemaObject, value);
        switch (typeName)
        {
            case "object":
                ValidateRequiredObjectStringReferencesNotKnownEmpty(value, schemaObject, path, errors, knownEmptyStringReferences);
                break;
            case "array":
                if (value is JsonArray array && schemaObject["items"] != null)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        var itemPath = string.IsNullOrEmpty(path) ? $"[{i}]" : $"{path}[{i}]";
                        ValidateRequiredStringReferencesNotKnownEmpty(array[i], schemaObject["items"], itemPath, errors, knownEmptyStringReferences);
                    }
                }
                break;
        }
    }

    private static void ValidateRequiredObjectStringReferencesNotKnownEmpty(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors,
        IReadOnlySet<string> knownEmptyStringReferences)
    {
        if (value is not JsonObject obj)
            return;

        var properties = schema["properties"] as JsonObject;
        var requiredNames = ReadRequiredPropertyNames(schema);
        if (properties != null)
        {
            foreach (var requiredName in requiredNames)
            {
                if (!properties.TryGetPropertyValue(requiredName, out var propertySchema)
                    || !SchemaAllowsString(propertySchema)
                    || !obj.TryGetPropertyValue(requiredName, out var propertyValue)
                    || propertyValue == null
                    || !TryFindKnownEmptyStringReference(propertyValue, knownEmptyStringReferences, out var reference))
                {
                    continue;
                }

                var requiredPath = string.IsNullOrEmpty(path) ? requiredName : $"{path}.{requiredName}";
                errors.Add(new SchemaValidationError(
                    requiredPath,
                    $"required string property '{requiredPath}' references known-empty value '{reference}'"));
            }
        }

        foreach (var (propertyName, propertyValue) in obj)
        {
            JsonNode? propertySchema = null;
            if (properties != null)
                properties.TryGetPropertyValue(propertyName, out propertySchema);

            propertySchema ??= schema["additionalProperties"] as JsonObject;
            if (propertySchema == null)
                continue;

            var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
            ValidateRequiredStringReferencesNotKnownEmpty(propertyValue, propertySchema, propertyPath, errors, knownEmptyStringReferences);
        }
    }

    private static bool TryFindKnownEmptyStringReference(
        JsonNode? value,
        IReadOnlySet<string> knownEmptyStringReferences,
        out string reference)
    {
        reference = "";
        if (value is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
            || !text.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Match expressionMatch in ExpressionRegex.Matches(text))
        {
            var expression = expressionMatch.Groups[1].Value;
            foreach (Match pathMatch in DataStepsPathRegex.Matches(expression))
            {
                var referencedStepId = pathMatch.Groups[1].Value;
                var propertyPath = SplitPath(pathMatch.Groups["path"].Value);
                var candidate = "data.steps." + referencedStepId + (propertyPath.Count == 0 ? "" : "." + string.Join('.', propertyPath));
                if (!knownEmptyStringReferences.Contains(candidate))
                    continue;

                reference = candidate;
                return true;
            }
        }

        return false;
    }

    private static void ValidateRequiredStringLiteralsNotEmpty(
        JsonNode? value,
        JsonNode? schema,
        string path,
        List<SchemaValidationError> errors,
        bool allowDynamicExpressions = true)
    {
        if (schema is not JsonObject schemaObject)
            return;

        if (schemaObject["allOf"] is JsonArray allOf)
        {
            foreach (var variant in allOf)
                ValidateRequiredStringLiteralsNotEmpty(value, variant, path, errors, allowDynamicExpressions);
        }

        if (schemaObject["anyOf"] is JsonArray anyOf)
        {
            foreach (var variant in anyOf)
                ValidateRequiredStringLiteralsNotEmpty(value, variant, path, errors, allowDynamicExpressions);
        }

        if (schemaObject["oneOf"] is JsonArray oneOf)
        {
            foreach (var variant in oneOf)
                ValidateRequiredStringLiteralsNotEmpty(value, variant, path, errors, allowDynamicExpressions);
        }

        if (allowDynamicExpressions && IsDynamicExpressionString(value))
            return;

        var typeName = ReadApplicableSchemaType(schemaObject, value);
        switch (typeName)
        {
            case "object":
                ValidateRequiredObjectStringLiteralsNotEmpty(value, schemaObject, path, errors, allowDynamicExpressions);
                break;
            case "array":
                if (value is JsonArray array && schemaObject["items"] != null)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        var itemPath = string.IsNullOrEmpty(path) ? $"[{i}]" : $"{path}[{i}]";
                        ValidateRequiredStringLiteralsNotEmpty(array[i], schemaObject["items"], itemPath, errors, allowDynamicExpressions);
                    }
                }
                break;
        }
    }

    private static void ValidateRequiredObjectStringLiteralsNotEmpty(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors,
        bool allowDynamicExpressions)
    {
        if (value is not JsonObject obj)
            return;

        var properties = schema["properties"] as JsonObject;
        var requiredNames = ReadRequiredPropertyNames(schema);
        if (properties != null)
        {
            foreach (var requiredName in requiredNames)
            {
                if (!properties.TryGetPropertyValue(requiredName, out var propertySchema)
                    || !SchemaAllowsString(propertySchema)
                    || !obj.TryGetPropertyValue(requiredName, out var propertyValue)
                    || propertyValue == null
                    || !IsLiteralEmptyString(propertyValue))
                {
                    continue;
                }

                var requiredPath = string.IsNullOrEmpty(path) ? requiredName : $"{path}.{requiredName}";
                errors.Add(new SchemaValidationError(
                    requiredPath,
                    $"required string property '{requiredPath}' must not be an empty string literal"));
            }
        }

        foreach (var (propertyName, propertyValue) in obj)
        {
            JsonNode? propertySchema = null;
            if (properties != null)
                properties.TryGetPropertyValue(propertyName, out propertySchema);

            propertySchema ??= schema["additionalProperties"] as JsonObject;
            if (propertySchema == null)
                continue;

            var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
            ValidateRequiredStringLiteralsNotEmpty(propertyValue, propertySchema, propertyPath, errors, allowDynamicExpressions);
        }
    }

    private static bool TryReadPositiveInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (TryReadNonNegativeInteger(node, out value))
            return value > 0;

        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return value > 0;
        }

        return false;
    }

    private static IReadOnlyList<string> ReadRequiredPropertyNames(JsonObject schema)
    {
        var names = ReadStringArray(schema["required"]);
        names.AddRange(ReadStringArray(schema["required_properties"]));

        return names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static List<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            return new List<string>();

        return array
            .Select(static node => node is JsonValue value && value.TryGetValue<string>(out var name) ? name : null)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToList();
    }

    private static bool SchemaAllowsString(JsonNode? schema)
    {
        if (schema is not JsonObject schemaObject)
            return false;

        if (schemaObject["type"] is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var typeName)
            && string.Equals(typeName, "string", StringComparison.Ordinal))
        {
            return true;
        }

        if (schemaObject["type"] is JsonArray typeArray
            && typeArray.Any(static node => node is JsonValue value
                && value.TryGetValue<string>(out var itemType)
                && string.Equals(itemType, "string", StringComparison.Ordinal)))
        {
            return true;
        }

        if (schemaObject.ContainsKey("minLength")
            || schemaObject.ContainsKey("maxLength")
            || schemaObject.ContainsKey("pattern"))
        {
            return true;
        }

        return SchemaVariantAllowsString(schemaObject["anyOf"])
            || SchemaVariantAllowsString(schemaObject["oneOf"])
            || SchemaVariantAllowsString(schemaObject["allOf"]);
    }

    private static bool SchemaVariantAllowsString(JsonNode? variants)
    {
        return variants is JsonArray array && array.Any(SchemaAllowsString);
    }

    private static bool IsLiteralEmptyString(JsonNode? value)
    {
        return value is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && text.Length == 0;
    }

    private static void ValidateMcpRequestCompatibilityConventions(
        JsonObject request,
        JsonObject inputSchema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (inputSchema["properties"] is not JsonObject properties)
            return;

        foreach (var (propertyName, propertySchema) in properties)
        {
            var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
            if (!request.TryGetPropertyValue(propertyName, out var value) || value is null)
            {
                if (IsExplicitPaginationNumberProperty(propertyName, propertySchema))
                {
                    errors.Add(new SchemaValidationError(
                        propertyPath,
                        "missing explicit numeric pagination property; send an unquoted number such as 30 instead of omitting it"));
                }

                continue;
            }

            ValidateMcpWorkspacePathConvention(propertyName, propertySchema, value, propertyPath, errors);

            if (value is JsonObject childObject && propertySchema is JsonObject childSchema)
                ValidateMcpRequestCompatibilityConventions(childObject, childSchema, propertyPath, errors);

            if (value is JsonArray array && propertySchema is JsonObject arraySchema && arraySchema["items"] is JsonObject itemSchema)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject itemObject)
                        ValidateMcpRequestCompatibilityConventions(itemObject, itemSchema, $"{propertyPath}[{i}]", errors);
                }
            }
        }
    }

    private static void ValidateMcpWorkspacePathConvention(
        string propertyName,
        JsonNode? propertySchema,
        JsonNode value,
        string propertyPath,
        List<SchemaValidationError> errors)
    {
        if (!IsMcpWorkspacePathProperty(propertyName, propertySchema))
            return;

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            ValidateMcpWorkspacePathString(propertyName, propertyPath, text, errors);
            return;
        }

        if (value is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
                    ValidateMcpWorkspacePathString(propertyName, $"{propertyPath}[{i}]", itemText, errors);
            }
        }
    }

    private static void ValidateMcpWorkspacePathString(
        string propertyName,
        string propertyPath,
        string text,
        List<SchemaValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (IsDynamicExpressionString(JsonValue.Create(text)))
        {
            if (TryFindDiagnosticAbsolutePathFieldReference(text, out var fieldName))
            {
                errors.Add(new SchemaValidationError(
                    propertyPath,
                    $"workspace path expression references diagnostic absolute path field '{fieldName}'; use a relative sibling such as repositoryRootRelative/rootPathRelative/relativePath, reuse the original relative request value, or omit/null optional projectRoot"));
            }

            return;
        }

        if (IsJsonPathListProperty(propertyName) && TryParseJsonStringArray(text, out var pathItems))
        {
            for (var i = 0; i < pathItems.Count; i++)
                ValidateLiteralWorkspacePath($"{propertyPath}[{i}]", pathItems[i], errors);
            return;
        }

        ValidateLiteralWorkspacePath(propertyPath, text, errors);
    }

    private static void ValidateLiteralWorkspacePath(
        string propertyPath,
        string text,
        List<SchemaValidationError> errors)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return;

        if (LooksLikeAbsoluteWorkspacePath(trimmed))
        {
            errors.Add(new SchemaValidationError(
                propertyPath,
                $"workspace path '{trimmed}' must be relative; do not use /workspace/..., /Users/..., drive-qualified, file URI, or home-relative paths in MCP requests"));
            return;
        }

        if (ContainsParentTraversalSegment(trimmed))
        {
            errors.Add(new SchemaValidationError(
                propertyPath,
                $"workspace path '{trimmed}' must not contain parent traversal segments"));
        }
    }

    private static bool IsMcpWorkspacePathProperty(string propertyName, JsonNode? propertySchema)
    {
        if (propertyName.Contains("url", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("uri", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (propertyName.Equals("projectRoot", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("targetDirectory", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("filePath", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("directoryPath", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("relativePath", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("contextFilesJson", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("pathsJson", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("path", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("paths", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return propertySchema is JsonObject schema
            && schema["description"] is JsonValue descriptionValue
            && descriptionValue.TryGetValue<string>(out var description)
            && description.Contains("path", StringComparison.OrdinalIgnoreCase)
            && (description.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                || description.Contains("relative", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJsonPathListProperty(string propertyName)
        => propertyName.EndsWith("Json", StringComparison.OrdinalIgnoreCase)
           || propertyName.Equals("contextFilesJson", StringComparison.OrdinalIgnoreCase)
           || propertyName.Equals("pathsJson", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJsonStringArray(string text, out IReadOnlyList<string> values)
    {
        values = [];
        try
        {
            var parsed = JsonNode.Parse(text);
            if (parsed is not JsonArray array)
                return false;

            var result = new List<string>();
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var stringValue))
                    result.Add(stringValue);
            }

            values = result;
            return result.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeAbsoluteWorkspacePath(string text)
    {
        if (text.StartsWith("/", StringComparison.Ordinal)
            || text.StartsWith("\\", StringComparison.Ordinal)
            || text.StartsWith("~/", StringComparison.Ordinal)
            || text.StartsWith("~\\", StringComparison.Ordinal)
            || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return WindowsAbsolutePathRegex.IsMatch(text);
    }

    private static bool ContainsParentTraversalSegment(string text)
    {
        return text
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == "..");
    }

    private static bool TryFindDiagnosticAbsolutePathFieldReference(string expression, out string fieldName)
    {
        var match = DiagnosticAbsolutePathFieldRegex.Match(expression);
        if (match.Success)
        {
            fieldName = match.Groups["field"].Value;
            return true;
        }

        fieldName = string.Empty;
        return false;
    }

    private static bool IsExplicitPaginationNumberProperty(string propertyName, JsonNode? propertySchema)
    {
        if (!string.Equals(propertyName, "perPage", StringComparison.Ordinal))
            return false;

        if (propertySchema is not JsonObject schemaObject)
            return false;

        var typeName = ReadSchemaType(schemaObject);
        return string.Equals(typeName, "number", StringComparison.Ordinal)
            || string.Equals(typeName, "integer", StringComparison.Ordinal);
    }

    private static void ValidateJsonNodeAgainstSchema(
        JsonNode? value,
        JsonNode? schema,
        string path,
        List<SchemaValidationError> errors,
        bool allowDynamicExpressions = true)
    {
        if (schema is not JsonObject schemaObject)
            return;

        if (schemaObject["allOf"] is JsonArray allOf)
        {
            foreach (var variant in allOf)
                ValidateJsonNodeAgainstSchema(value, variant, path, errors, allowDynamicExpressions);
        }

        if (schemaObject["anyOf"] is JsonArray anyOf)
        {
            if (!MatchesAnySchemaVariant(value, anyOf, allowDynamicExpressions))
            {
                errors.Add(new SchemaValidationError(path, "value does not match any allowed schema variant"));
                return;
            }
        }

        if (schemaObject["oneOf"] is JsonArray oneOf)
        {
            if (!MatchesExactlyOneSchemaVariant(value, oneOf, allowDynamicExpressions))
            {
                errors.Add(new SchemaValidationError(path, "value must match exactly one allowed schema variant"));
                return;
            }
        }

        if (allowDynamicExpressions && IsDynamicExpressionString(value))
            return;

        ValidateConstAndEnum(value, schemaObject, path, errors);

        var typeName = ReadApplicableSchemaType(schemaObject, value);
        switch (typeName)
        {
            case "object":
                ValidateObjectAgainstSchema(value, schemaObject, path, errors, allowDynamicExpressions);
                break;
            case "array":
                ValidateArrayAgainstSchema(value, schemaObject, path, errors, allowDynamicExpressions);
                break;
            case "string":
                ValidatePrimitiveType(value, "string", path, errors, allowDynamicExpressions);
                ValidateStringConstraints(value, schemaObject, path, errors);
                break;
            case "number":
                ValidatePrimitiveType(value, "number", path, errors, allowDynamicExpressions);
                ValidateNumericConstraints(value, schemaObject, path, errors);
                break;
            case "integer":
                ValidatePrimitiveType(value, "integer", path, errors, allowDynamicExpressions);
                ValidateNumericConstraints(value, schemaObject, path, errors);
                break;
            case "boolean":
                ValidatePrimitiveType(value, "boolean", path, errors, allowDynamicExpressions);
                break;
            case "null":
                ValidatePrimitiveType(value, "null", path, errors, allowDynamicExpressions);
                break;
            default:
                break;
        }
    }

    private static bool MatchesAnySchemaVariant(JsonNode? value, JsonArray variants, bool allowDynamicExpressions)
    {
        foreach (var variant in variants)
        {
            var variantErrors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(value, variant, string.Empty, variantErrors, allowDynamicExpressions);
            if (variantErrors.Count == 0)
                return true;
        }

        return false;
    }

    private static bool MatchesExactlyOneSchemaVariant(JsonNode? value, JsonArray variants, bool allowDynamicExpressions)
    {
        var matchCount = 0;
        foreach (var variant in variants)
        {
            var variantErrors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(value, variant, string.Empty, variantErrors, allowDynamicExpressions);
            if (variantErrors.Count == 0)
                matchCount++;
        }

        return matchCount == 1;
    }

    private static void ValidateObjectAgainstSchema(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors,
        bool allowDynamicExpressions)
    {
        if (allowDynamicExpressions && IsDynamicExpressionString(value))
            return;

        if (value is not JsonObject obj)
        {
            errors.Add(new SchemaValidationError(path, "expected object"));
            return;
        }

        var properties = schema["properties"] as JsonObject;
        ValidateCountConstraint(obj.Count, schema, "minProperties", "maxProperties", path, "properties", errors);
        var required = ReadRequiredPropertyNames(schema);
        if (required.Count > 0)
        {
            foreach (var requiredName in required)
            {
                if (!obj.ContainsKey(requiredName))
                {
                    var requiredPath = string.IsNullOrEmpty(path) ? requiredName : $"{path}.{requiredName}";
                    errors.Add(new SchemaValidationError(requiredPath, "missing required property"));
                }
            }
        }

        foreach (var (propertyName, propertyValue) in obj)
        {
            if (properties != null && properties.TryGetPropertyValue(propertyName, out var propertySchema) && propertySchema != null)
            {
                var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
                ValidateJsonNodeAgainstSchema(propertyValue, propertySchema, propertyPath, errors, allowDynamicExpressions);
                continue;
            }

            if (!AllowsAdditionalProperties(schema))
            {
                var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
                errors.Add(new SchemaValidationError(propertyPath, "property is not allowed by schema"));
                continue;
            }

            if (schema["additionalProperties"] is JsonObject additionalSchema)
            {
                var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
                ValidateJsonNodeAgainstSchema(propertyValue, additionalSchema, propertyPath, errors, allowDynamicExpressions);
            }
        }
    }

    private static void ValidateArrayAgainstSchema(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors,
        bool allowDynamicExpressions)
    {
        if (allowDynamicExpressions && IsDynamicExpressionString(value))
            return;

        if (value is not JsonArray array)
        {
            errors.Add(new SchemaValidationError(path, "expected array"));
            return;
        }

        ValidateCountConstraint(array.Count, schema, "minItems", "maxItems", path, "items", errors);

        if (schema["uniqueItems"] is JsonValue uniqueValue
            && uniqueValue.TryGetValue<bool>(out var uniqueItems)
            && uniqueItems)
        {
            for (var i = 0; i < array.Count; i++)
            {
                for (var j = i + 1; j < array.Count; j++)
                {
                    if (JsonNode.DeepEquals(array[i], array[j]))
                        errors.Add(new SchemaValidationError(path, $"array items at indexes {i} and {j} must be unique"));
                }
            }
        }

        if (schema["items"] == null)
            return;

        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = string.IsNullOrEmpty(path) ? $"[{i}]" : $"{path}[{i}]";
            ValidateJsonNodeAgainstSchema(array[i], schema["items"], itemPath, errors, allowDynamicExpressions);
        }
    }

    private static void ValidatePrimitiveType(
        JsonNode? value,
        string expectedType,
        string path,
        List<SchemaValidationError> errors,
        bool allowDynamicExpressions)
    {
        if (allowDynamicExpressions && IsDynamicExpressionString(value))
            return;

        if (value is null)
        {
            if (!string.Equals(expectedType, "null", StringComparison.Ordinal))
                errors.Add(new SchemaValidationError(path, $"expected {expectedType} but got null"));
            return;
        }

        if (value is not JsonValue jsonValue)
        {
            errors.Add(new SchemaValidationError(path, $"expected {expectedType}"));
            return;
        }

        var valid = expectedType switch
        {
            "string" => jsonValue.TryGetValue<string>(out _),
            "number" => IsJsonNumber(jsonValue),
            "integer" => IsJsonInteger(jsonValue),
            "boolean" => jsonValue.TryGetValue<bool>(out _),
            "null" => false,
            _ => true
        };

        if (!valid)
            errors.Add(new SchemaValidationError(path, $"expected {expectedType}"));
    }

    private static bool IsJsonNumber(JsonValue jsonValue) =>
        jsonValue.TryGetValue<double>(out _)
        || jsonValue.TryGetValue<float>(out _)
        || jsonValue.TryGetValue<decimal>(out _)
        || jsonValue.TryGetValue<long>(out _)
        || jsonValue.TryGetValue<int>(out _)
        || jsonValue.TryGetValue<short>(out _)
        || jsonValue.TryGetValue<byte>(out _);

    private static bool IsJsonInteger(JsonValue jsonValue) =>
        TryReadDecimal(jsonValue, out var number) && decimal.Truncate(number) == number;

    private static void ValidateConstAndEnum(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (schema.TryGetPropertyValue("const", out var constant)
            && !JsonNode.DeepEquals(value, constant))
        {
            errors.Add(new SchemaValidationError(path, $"value must equal {constant?.ToJsonString() ?? "null"}"));
        }

        if (schema["enum"] is not JsonArray allowedValues)
            return;

        if (!allowedValues.Any(allowed => JsonNode.DeepEquals(value, allowed)))
        {
            var allowedText = string.Join(", ", allowedValues.Select(static allowed => allowed?.ToJsonString() ?? "null"));
            errors.Add(new SchemaValidationError(path, $"value must be one of: {allowedText}"));
        }
    }

    private static void ValidateStringConstraints(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || text == null)
            return;

        ValidateCountConstraint(text.Length, schema, "minLength", "maxLength", path, "characters", errors);

        if (schema["pattern"] is not JsonValue patternValue
            || !patternValue.TryGetValue<string>(out var pattern)
            || string.IsNullOrEmpty(pattern))
        {
            return;
        }

        try
        {
            if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                errors.Add(new SchemaValidationError(path, $"string does not match required pattern '{pattern}'"));
        }
        catch (ArgumentException)
        {
            errors.Add(new SchemaValidationError(path, $"tool schema contains invalid regex pattern '{pattern}'"));
        }
        catch (RegexMatchTimeoutException)
        {
            errors.Add(new SchemaValidationError(path, $"tool schema regex pattern '{pattern}' timed out"));
        }
    }

    private static void ValidateNumericConstraints(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (!TryReadDecimal(value, out var number))
            return;

        if (TryReadSchemaDecimal(schema, "minimum", out var minimum) && number < minimum)
            errors.Add(new SchemaValidationError(path, $"number must be greater than or equal to {minimum}"));
        if (TryReadSchemaDecimal(schema, "maximum", out var maximum) && number > maximum)
            errors.Add(new SchemaValidationError(path, $"number must be less than or equal to {maximum}"));
        if (TryReadSchemaDecimal(schema, "exclusiveMinimum", out var exclusiveMinimum) && number <= exclusiveMinimum)
            errors.Add(new SchemaValidationError(path, $"number must be greater than {exclusiveMinimum}"));
        if (TryReadSchemaDecimal(schema, "exclusiveMaximum", out var exclusiveMaximum) && number >= exclusiveMaximum)
            errors.Add(new SchemaValidationError(path, $"number must be less than {exclusiveMaximum}"));
        if (TryReadSchemaDecimal(schema, "multipleOf", out var multipleOf)
            && multipleOf > 0
            && number % multipleOf != 0)
        {
            errors.Add(new SchemaValidationError(path, $"number must be a multiple of {multipleOf}"));
        }
    }

    private static void ValidateCountConstraint(
        int count,
        JsonObject schema,
        string minimumProperty,
        string maximumProperty,
        string path,
        string unit,
        List<SchemaValidationError> errors)
    {
        if (TryReadNonNegativeInteger(schema[minimumProperty], out var minimum) && count < minimum)
            errors.Add(new SchemaValidationError(path, $"must contain at least {minimum} {unit}"));
        if (TryReadNonNegativeInteger(schema[maximumProperty], out var maximum) && count > maximum)
            errors.Add(new SchemaValidationError(path, $"must contain at most {maximum} {unit}"));
    }

    private static bool TryReadNonNegativeInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;

        if (jsonValue.TryGetValue<long>(out value))
            return value >= 0;
        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return value >= 0;
        }

        return false;
    }

    private static bool TryReadSchemaDecimal(JsonObject schema, string propertyName, out decimal value) =>
        TryReadDecimal(schema[propertyName], out value);

    private static bool TryReadDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;

        if (jsonValue.TryGetValue<decimal>(out value))
            return true;
        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            value = longValue;
            return true;
        }
        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }
        if (jsonValue.TryGetValue<short>(out var shortValue))
        {
            value = shortValue;
            return true;
        }
        if (jsonValue.TryGetValue<float>(out var floatValue)
            && !float.IsNaN(floatValue)
            && !float.IsInfinity(floatValue))
        {
            value = (decimal)floatValue;
            return true;
        }
        if (jsonValue.TryGetValue<double>(out var doubleValue)
            && !double.IsNaN(doubleValue)
            && !double.IsInfinity(doubleValue))
        {
            try
            {
                value = (decimal)doubleValue;
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        return false;
    }

    private static int NormalizeJsonNodeAgainstSchema(JsonNode? value, JsonNode? schema)
    {
        if (schema is not JsonObject schemaObject || value == null)
            return 0;

        if (schemaObject["anyOf"] is JsonArray anyOf)
            return NormalizeAgainstSingleMatchingVariant(value, anyOf);

        if (schemaObject["oneOf"] is JsonArray oneOf)
            return NormalizeAgainstSingleMatchingVariant(value, oneOf);

        var typeName = ReadSchemaType(schemaObject);
        return typeName switch
        {
            "object" => NormalizeObjectAgainstSchema(value, schemaObject),
            "array" => NormalizeArrayAgainstSchema(value, schemaObject),
            _ => 0
        };
    }

    private static int NormalizeAgainstSingleMatchingVariant(JsonNode value, JsonArray variants)
    {
        JsonObject? selectedVariant = null;
        foreach (var variant in variants)
        {
            if (variant is not JsonObject variantObject)
                continue;

            var errors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(value, variantObject, string.Empty, errors);
            if (errors.Count == 0)
                return 0;

            var clone = value.DeepClone();
            var changes = NormalizeJsonNodeAgainstSchema(clone, variantObject);
            if (changes == 0)
                continue;

            errors.Clear();
            ValidateJsonNodeAgainstSchema(clone, variantObject, string.Empty, errors);
            if (errors.Count == 0)
            {
                if (selectedVariant != null)
                    return 0;

                selectedVariant = variantObject;
            }
        }

        return selectedVariant == null ? 0 : NormalizeJsonNodeAgainstSchema(value, selectedVariant);
    }

    private static int NormalizeObjectAgainstSchema(JsonNode value, JsonObject schema)
    {
        if (IsDynamicExpressionString(value) || value is not JsonObject obj)
            return 0;

        var changes = 0;
        var properties = schema["properties"] as JsonObject;
        foreach (var propertyName in obj.Select(kv => kv.Key).ToArray())
        {
            var propertyValue = obj[propertyName];
            JsonNode? propertySchema = null;

            if (properties != null)
                properties.TryGetPropertyValue(propertyName, out propertySchema);

            propertySchema ??= schema["additionalProperties"] as JsonObject;
            if (propertySchema == null)
                continue;

            changes += NormalizeJsonNodeAgainstSchema(propertyValue, propertySchema);
            if (TryCoerceJsonValue(propertyValue, propertySchema, out var coerced))
            {
                obj[propertyName] = coerced;
                changes++;
            }
        }

        return changes;
    }

    private static int NormalizeArrayAgainstSchema(JsonNode value, JsonObject schema)
    {
        if (IsDynamicExpressionString(value) || value is not JsonArray array || schema["items"] == null)
            return 0;

        var itemSchema = schema["items"];
        var changes = 0;
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            changes += NormalizeJsonNodeAgainstSchema(item, itemSchema);
            if (TryCoerceJsonValue(item, itemSchema, out var coerced))
            {
                array[i] = coerced;
                changes++;
            }
        }

        return changes;
    }

    private static bool TryCoerceJsonValue(JsonNode? value, JsonNode? schema, out JsonNode? coerced)
    {
        coerced = null;
        if (schema is not JsonObject schemaObject || value is not JsonValue jsonValue || IsDynamicExpressionString(value))
            return false;

        var typeName = ReadSchemaType(schemaObject);
        if (typeName == "number" && jsonValue.TryGetValue<string>(out var numberText)
            && double.TryParse(numberText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            coerced = JsonValue.Create(number);
            return true;
        }

        if (typeName == "integer" && jsonValue.TryGetValue<string>(out var integerText)
            && long.TryParse(integerText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer))
        {
            coerced = JsonValue.Create(integer);
            return true;
        }

        if (typeName == "boolean" && jsonValue.TryGetValue<string>(out var booleanText)
            && bool.TryParse(booleanText, out var boolean))
        {
            coerced = JsonValue.Create(boolean);
            return true;
        }

        return false;
    }

    private static bool IsDynamicExpressionString(JsonNode? value)
    {
        return value is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && text != null
            && text.Contains("${", StringComparison.Ordinal);
    }

    private static string? ReadSchemaType(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var singleType))
            return singleType;

        if (schema["type"] is JsonArray typeArray)
        {
            var first = typeArray
                .Select(node => node is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null)
                .FirstOrDefault(parsed => !string.IsNullOrWhiteSpace(parsed) && !string.Equals(parsed, "null", StringComparison.Ordinal));
            return first;
        }

        return null;
    }

    private static string? ReadApplicableSchemaType(JsonObject schema, JsonNode? value)
    {
        if (schema["type"] is not JsonArray typeArray)
        {
            var declaredType = ReadSchemaType(schema);
            if (declaredType != null)
                return declaredType;

            if (schema.ContainsKey("properties") || schema.ContainsKey("required")
                || schema.ContainsKey("additionalProperties") || schema.ContainsKey("minProperties")
                || schema.ContainsKey("maxProperties"))
                return "object";
            if (schema.ContainsKey("items") || schema.ContainsKey("minItems")
                || schema.ContainsKey("maxItems") || schema.ContainsKey("uniqueItems"))
                return "array";
            if (schema.ContainsKey("minLength") || schema.ContainsKey("maxLength") || schema.ContainsKey("pattern"))
                return "string";
            if (schema.ContainsKey("minimum") || schema.ContainsKey("maximum")
                || schema.ContainsKey("exclusiveMinimum") || schema.ContainsKey("exclusiveMaximum")
                || schema.ContainsKey("multipleOf"))
                return "number";

            return null;
        }

        foreach (var typeNode in typeArray)
        {
            if (typeNode is JsonValue typeValue
                && typeValue.TryGetValue<string>(out var typeName)
                && typeName != null
                && ValueMatchesJsonType(value, typeName))
            {
                return typeName;
            }
        }

        return typeArray
            .Select(static node => node is JsonValue candidate && candidate.TryGetValue<string>(out var parsed) ? parsed : null)
            .FirstOrDefault(static parsed => !string.IsNullOrWhiteSpace(parsed));
    }

    private static bool ValueMatchesJsonType(JsonNode? value, string typeName)
    {
        if (value == null)
            return string.Equals(typeName, "null", StringComparison.Ordinal);
        if (value is JsonObject)
            return string.Equals(typeName, "object", StringComparison.Ordinal);
        if (value is JsonArray)
            return string.Equals(typeName, "array", StringComparison.Ordinal);
        if (value is not JsonValue jsonValue)
            return false;

        return typeName switch
        {
            "string" => jsonValue.TryGetValue<string>(out _),
            "number" => IsJsonNumber(jsonValue),
            "integer" => IsJsonInteger(jsonValue),
            "boolean" => jsonValue.TryGetValue<bool>(out _),
            _ => false
        };
    }

    private sealed record SchemaPathValidationResult(bool IsValid, string Message, bool IsOpaqueResponse = false);

    private static SchemaPathValidationResult ValidateSchemaPath(FlowTypeDescriptor? schema, IReadOnlyList<string> path)
    {
        if (path.Count == 0)
            return new SchemaPathValidationResult(true, "");

        if (schema == null)
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' is opaque and has no known object schema.");

        if (schema.IsOpaque)
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' crosses an opaque value with no known schema.", IsOpaqueResponse: true);

        if (schema.Kind == FlowTypeKind.Union)
        {
            var nonNullVariants = schema.Variants.Where(static variant => variant.Kind != FlowTypeKind.Null).ToArray();
            if (nonNullVariants.Length == 0)
                return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' crosses a null value.");

            var variantResults = nonNullVariants.Select(variant => ValidateSchemaPath(variant, path)).ToArray();
            if (variantResults.Any(static result => result.IsValid))
                return new SchemaPathValidationResult(true, "");

            return variantResults.FirstOrDefault(static result => result.IsOpaqueResponse)
                ?? variantResults.First();
        }

        if (schema.Kind == FlowTypeKind.Array)
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' tries to read object properties from an array output.");

        if (schema.Kind is not (FlowTypeKind.Object or FlowTypeKind.Dictionary))
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' tries to read object properties from a {schema.Describe()} output.");

        var segment = path[0];
        if (!schema.Properties.TryGetValue(segment, out var childSchema))
        {
            if (schema.AllowsAdditionalProperties)
                return new SchemaPathValidationResult(true, "");

            return new SchemaPathValidationResult(false, $"Property '{segment}' is not defined by the output schema.");
        }

        if (path.Count == 1)
            return new SchemaPathValidationResult(true, "");

        return ValidateSchemaPath(childSchema.Type, path.Skip(1).ToArray());
    }

    private static bool AllowsAdditionalProperties(JsonObject schemaObject)
    {
        if (!schemaObject.TryGetPropertyValue("additionalProperties", out var additional) || additional == null)
            return true;

        if (additional is JsonValue value && value.TryGetValue<bool>(out var allowed))
            return allowed;

        return additional is JsonObject;
    }

    private static bool IsOpaqueSchema(JsonObject schemaObject)
    {
        return schemaObject.TryGetPropertyValue("x-gnougo-opaque", out var opaqueNode)
            && opaqueNode is JsonValue opaqueValue
            && opaqueValue.TryGetValue<bool>(out var opaque)
            && opaque;
    }

    private static IEnumerable<string> EnumerateAllowedPaths(string prefix, JsonNode? schema, int depth = 0)
    {
        yield return prefix;

        if (depth >= 4 || schema is not JsonObject schemaObject || IsOpaqueSchema(schemaObject))
            yield break;

        if (schemaObject["properties"] is not JsonObject properties)
            yield break;

        foreach (var (propertyName, childSchema) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var childPrefix = prefix + "." + propertyName;
            yield return childPrefix;

            if (childSchema is JsonObject childObject && !IsOpaqueSchema(childObject))
            {
                foreach (var nestedPath in EnumerateAllowedPaths(childPrefix, childObject, depth + 1).Skip(1))
                    yield return nestedPath;
            }
        }
    }

    private static void AddLoopScopedDataVariables(
        StepDef step,
        WorkflowSymbolTable loopSymbols,
        WorkflowSymbolTable outerSymbols)
    {
        var itemType = InferLoopItemType(step, outerSymbols);
        if (itemType != null)
        {
            var itemVar = step.ItemVar ?? "item";
            var indexVar = step.IndexVar ?? "i";
            loopSymbols.SetDataVariable(itemVar, itemType);
            loopSymbols.SetDataVariable(indexVar, FlowTypeDescriptor.Integer);
            loopSymbols.SetDataVariable("_loop", LoopContextType(itemType));
            loopSymbols.SetDataVariable("loop", LoopContextType(itemType));
            return;
        }

        if (string.Equals(step.Type, "loop.sequential", StringComparison.Ordinal))
        {
            var loopContext = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
            {
                ["index"] = new(FlowTypeDescriptor.Integer, Required: true)
            });
            loopSymbols.SetDataVariable("_loop", loopContext);
            loopSymbols.SetDataVariable("loop", loopContext);
        }
    }

    private static FlowTypeDescriptor? InferLoopItemType(StepDef step, WorkflowSymbolTable symbols)
    {
        if (step.Input is not JsonObject input)
            return null;

        JsonNode? itemsNode = null;
        if (input.TryGetPropertyValue("items", out var items) && items != null)
            itemsNode = items;
        else if (string.Equals(step.Type, "loop.sequential", StringComparison.Ordinal)
                 && input.TryGetPropertyValue("over", out var over) && over != null)
            itemsNode = over;

        if (itemsNode == null)
            return null;

        var itemsType = StepExpressionTypeValidator.InferValueType(
            itemsNode,
            symbols.WorkflowInputs,
            symbols.StepOutputs,
            symbols.DataVariables);

        return ExtractArrayItemType(itemsType);
    }

    private static FlowTypeDescriptor ExtractArrayItemType(FlowTypeDescriptor? descriptor)
    {
        if (descriptor == null || descriptor.IsOpaque)
            return FlowTypeDescriptor.Any;

        if (descriptor.Kind == FlowTypeKind.Array)
            return descriptor.Items ?? FlowTypeDescriptor.Any;

        if (descriptor.Kind == FlowTypeKind.Union)
        {
            var itemTypes = descriptor.Variants
                .Where(static variant => variant.Kind == FlowTypeKind.Array)
                .Select(static variant => variant.Items ?? FlowTypeDescriptor.Any)
                .ToArray();

            return itemTypes.Length == 0
                ? FlowTypeDescriptor.Any
                : FlowTypeDescriptor.Union(itemTypes);
        }

        return FlowTypeDescriptor.Any;
    }

    private static FlowTypeDescriptor LoopContextType(FlowTypeDescriptor itemType) =>
        FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
        {
            ["index"] = new(FlowTypeDescriptor.Integer, Required: true),
            ["item"] = new(itemType, Required: true)
        });

    private static FlowTypeDescriptor BuildLoopOutputType(WorkflowSymbolTable loopSymbols)
    {
        var iterationStepOutputs = loopSymbols.StepOutputs.ToDictionary(
            static pair => pair.Key,
            static pair => new FlowPropertyDescriptor(pair.Value, Required: true),
            StringComparer.Ordinal);

        return FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
        {
            ["results"] = new(FlowTypeDescriptor.Array(FlowTypeDescriptor.Object(iterationStepOutputs)), Required: true),
            ["count"] = new(FlowTypeDescriptor.Integer, Required: true)
        });
    }

    private static FlowTypeDescriptor BuildStepOutputType(
        StepDef step,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        WorkflowSymbolTable symbols,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        return StepOutputTypeResolver.Resolve(
            step,
            workflows,
            symbols,
            mcpContracts,
            stepContracts);
    }

    private static string? TryGetInputString(StepDef step, string propertyName)
    {
        if (step.Input is not JsonObject input || !input.TryGetPropertyValue(propertyName, out var node) || node == null)
            return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && !text.Contains("${", StringComparison.Ordinal))
            return text;

        return null;
    }

    private static bool TryGetString(JsonObject obj, string propertyName, out string value)
    {
        value = "";
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue jsonValue)
            return false;

        if (!jsonValue.TryGetValue<string>(out var parsed) || parsed == null)
            return false;

        value = parsed;
        return true;
    }
}
