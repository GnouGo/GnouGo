using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Selects one or more workflow candidates from static and dynamic catalogs, executes them,
/// and optionally synthesizes a final answer.
/// </summary>
public sealed class WorkflowRouteExecutor : IStepExecutor
{
    public string StepType => "workflow.route";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "workflow.route input, candidates, selection, args, execution, or combine sections are malformed."),
        new(ErrorCodes.TemplatePlan, false, "The routing LLM is unavailable or did not return a valid selection."),
        new(ErrorCodes.WorkflowFetchNetwork, false, "A dynamic candidate source or selected workflow could not be resolved."),
        new(ErrorCodes.WorkflowCycleDetected, false, "A selected workflow call would exceed route call-depth limits or create a call cycle."),
        new(ErrorCodes.LlmBudgetExceeded, false, "The active host or workflow LLM usage budget was exceeded during routing, argument extraction, or synthesis."),
        new(ErrorCodes.LlmBudgetUnverifiable, false, "The active LLM usage budget could not be verified during routing, argument extraction, or synthesis."),
        new("NO_HITL_PROVIDER", false, "Interactive routed-input completion is enabled but no IHumanInputProvider is configured."),
        new("HUMAN_INPUT_TIMEOUT", false, "The human did not complete the selected workflow inputs before the configured timeout.")
    };

    public string DslSnippet => """
        ### workflow.route — Route to one or more workflow candidates
        ```yaml
        - id: route
          type: workflow.route
          input:
            prompt: "${data.inputs.prompt}"
            history: "${data.inputs.history}"
            candidates:
              - ref: { kind: database, agent: DocumentAgent }
                description: Answers questions over local documents.
              - ref: { kind: database }        # expands to all database agents from the host provider
                tags_any: [git, documents]     # optional dynamic filter
                limit: 20
            selection:
              mode: multiple                   # "single" or "multiple"
              min: 1
              max: 3
            args:
              passthrough: true                # forwards data.inputs to selected workflows
              auto_extract:                    # optional; true or object
                provider: openai               # optional; defaults to runtime provider
                model: gpt-5.4-mini            # optional; defaults to runtime model
              human_input:                     # optional; false by default
                enabled: true
                timeout_ms: 36000000
                max_attempts: 3
              add:
                history: "${data.inputs.history}"
            execution:
              parallel: true
              max_concurrency: 3
            combine:
              strategy: synthesize             # "synthesize", "first", or "raw"
        ```
        Output: `{ selected: [...], results: [...], answer?, text? }`
        Emits `gnougo-flow.step.thinking` progress events before each selected workflow runs.
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.route input must be object");

        var prompt = input["prompt"]?.GetValue<string>() ?? input["task"]?.GetValue<string>() ?? input["query"]?.GetValue<string>() ?? "";
        var candidatesInput = input["candidates"] as JsonArray
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.route requires 'candidates' array");

        if (ctx.CallDepth >= ctx.Limits.MaxCallDepth)
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Max call depth ({ctx.Limits.MaxCallDepth}) exceeded");

        var candidates = await NormalizeCandidatesAsync(ctx, candidatesInput, ct);
        if (candidates.Count == 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.route found no candidates");

        var selectionInput = input["selection"] as JsonObject;
        var mode = selectionInput?["mode"]?.GetValue<string>() ?? "multiple";
        var minSelected = Math.Max(0, selectionInput?["min"]?.GetValue<int>() ?? 1);
        var maxSelected = selectionInput?["max"]?.GetValue<int>() ?? (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase) ? 1 : candidates.Count);
        if (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase))
            maxSelected = 1;
        maxSelected = Math.Clamp(maxSelected, 1, candidates.Count);
        minSelected = Math.Clamp(minSelected, 0, maxSelected);

        var selected = await SelectCandidatesAsync(ctx, input, prompt, candidates, minSelected, maxSelected, ct);
        if (selected.Count < minSelected)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan,
                $"workflow.route selected {selected.Count} candidate(s), below required minimum {minSelected}");

        var argsInput = input["args"] as JsonObject;
        var args = BuildWorkflowArgs(ctx, argsInput);
        var executionInput = input["execution"] as JsonObject;
        var executeInParallel = executionInput?["parallel"]?.GetValue<bool>() ?? true;
        var maxConcurrency = executionInput?["max_concurrency"]?.GetValue<int>() ?? selected.Count;
        maxConcurrency = Math.Clamp(maxConcurrency, 1, Math.Max(1, selected.Count));
        var humanInputConfig = ParseHumanInputConfig(argsInput);

        List<RouteExecutionResult> routeResults;
        if (humanInputConfig.Enabled)
        {
            var prepared = await PrepareSelectedSequentialAsync(
                ctx,
                input,
                selected,
                args,
                argsInput,
                humanInputConfig,
                ct);
            routeResults = executeInParallel
                ? await ExecutePreparedParallelAsync(ctx, prepared, maxConcurrency, ct)
                : await ExecutePreparedSequentialAsync(ctx, prepared, ct);
        }
        else
        {
            routeResults = executeInParallel
                ? await ExecuteSelectedParallelAsync(ctx, input, selected, args, argsInput, maxConcurrency, ct)
                : await ExecuteSelectedSequentialAsync(ctx, input, selected, args, argsInput, ct);
        }

        var output = new JsonObject
        {
            ["selected"] = BuildSelectedArray(selected),
            ["results"] = BuildResultsArray(routeResults)
        };

        var combine = input["combine"] as JsonObject;
        var strategy = combine?["strategy"]?.GetValue<string>() ?? (routeResults.Count == 1 ? "first" : "synthesize");
        var answer = await CombineAsync(ctx, input, prompt, routeResults, strategy, ct);
        if (answer != null)
        {
            output["answer"] = JsonValue.Create(answer);
            output["text"] = JsonValue.Create(answer);
        }

        return output;
    }

    private static async Task<List<RouteCandidate>> NormalizeCandidatesAsync(
        StepExecutionContext ctx,
        JsonArray candidatesInput,
        CancellationToken ct)
    {
        var candidates = new List<RouteCandidate>();

        foreach (var node in candidatesInput)
        {
            if (node is not JsonObject candidateObj)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.route candidates must be objects");

            var refObj = candidateObj["ref"] as JsonObject
                ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.route candidate requires 'ref'");

            var kind = refObj["kind"]?.GetValue<string>() ?? "local";
            var explicitAgent = refObj["agent"]?.GetValue<string>();
            var explicitName = refObj["name"]?.GetValue<string>();
            var isDynamicDatabase = string.Equals(kind, "database", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(explicitAgent)
                && string.IsNullOrWhiteSpace(explicitName);

            if (isDynamicDatabase)
            {
                var provider = ctx.Engine.WorkflowCandidateProvider
                    ?? throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchNetwork,
                        "workflow.route dynamic database candidates require a WorkflowCandidateProvider");

                var dynamicCandidates = await provider.GetCandidatesAsync(new WorkflowRouteCandidateQuery
                {
                    Ref = refObj.DeepClone() as JsonObject ?? new JsonObject(),
                    Kind = kind,
                    TagsAny = ReadStringArray(candidateObj["tags_any"]),
                    TagsAll = ReadStringArray(candidateObj["tags_all"]),
                    ExcludeTags = ReadStringArray(candidateObj["exclude_tags"]),
                    Limit = candidateObj["limit"]?.GetValue<int>()
                }, ct);

                foreach (var dynamicCandidate in dynamicCandidates)
                    candidates.Add(RouteCandidate.FromProvider(dynamicCandidate));

                continue;
            }

            var name = explicitAgent ?? explicitName ?? refObj["url"]?.GetValue<string>() ?? $"candidate-{candidates.Count + 1}";
            var id = $"{kind}:{name}";
            candidates.Add(new RouteCandidate(
                Id: id,
                Name: name,
                Ref: refObj.DeepClone() as JsonObject ?? new JsonObject(),
                Description: candidateObj["description"]?.GetValue<string>(),
                Tags: ReadStringArray(candidateObj["tags"]).ToList(),
                Inputs: candidateObj["inputs"]?.DeepClone(),
                Outputs: candidateObj["outputs"]?.DeepClone(),
                Reason: null,
                Confidence: null));
        }

        return candidates
            .GroupBy(static candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static async Task<List<RouteCandidate>> SelectCandidatesAsync(
        StepExecutionContext ctx,
        JsonObject input,
        string prompt,
        List<RouteCandidate> candidates,
        int minSelected,
        int maxSelected,
        CancellationToken ct)
    {
        if (candidates.Count == 1)
            return candidates;

        var llm = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route requires an LLM client for multi-candidate selection");

        var selectionInput = input["selection"] as JsonObject;
        var requestedProvider = selectionInput?["provider"]?.GetValue<string>();
        var requestedModel = selectionInput?["model"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);

        var selectionPrompt = BuildSelectionPrompt(prompt, input["history"], candidates, minSelected, maxSelected);
        var response = await ctx.CallLLMAsync(llm, new LLMRequest
        {
            Provider = provider,
            Model = model ?? "",
            Prompt = selectionPrompt,
            Temperature = selectionInput?["temperature"]?.GetValue<double>() ?? 0,
            StructuredOutputStrict = false,
            StructuredOutputSchema = BuildSelectionSchema()
        }, "workflow.route.selection", ct);

        var json = response.Json ?? TryParseJsonObject(response.Text)
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route selection did not return JSON");

        var selectedIds = json["selected"] as JsonArray
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route selection JSON requires 'selected' array");

        var byId = candidates.ToDictionary(static c => c.Id, StringComparer.OrdinalIgnoreCase);
        var byName = candidates.ToDictionary(static c => c.Name, StringComparer.OrdinalIgnoreCase);
        var selected = new List<RouteCandidate>();

        foreach (var selectedNode in selectedIds)
        {
            string? id = null;
            string? reason = null;
            double? confidence = null;

            if (selectedNode is JsonValue value)
            {
                id = value.GetValue<string>();
            }
            else if (selectedNode is JsonObject obj)
            {
                id = obj["id"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>() ?? obj["workflow"]?.GetValue<string>();
                reason = obj["reason"]?.GetValue<string>();
                confidence = obj["confidence"]?.GetValue<double>();
            }

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var match = byId.GetValueOrDefault(id) ?? byName.GetValueOrDefault(id);
            if (match is null || selected.Any(s => string.Equals(s.Id, match.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            selected.Add(match with { Reason = reason, Confidence = confidence });
            if (selected.Count >= maxSelected)
                break;
        }

        if (selected.Count == 0 && minSelected > 0)
            selected.Add(candidates[0] with { Reason = "Fallback selection because the router returned no known candidate.", Confidence = 0 });

        return selected;
    }

    private static JsonObject BuildWorkflowArgs(StepExecutionContext ctx, JsonObject? argsInput)
    {
        var passthrough = argsInput?["passthrough"]?.GetValue<bool>() ?? true;
        var args = passthrough
            ? ctx.Data["inputs"]?.DeepClone() as JsonObject ?? new JsonObject()
            : new JsonObject();

        if (argsInput?["add"] is JsonObject add)
        {
            foreach (var (key, value) in add)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    args[key] = value?.DeepClone();
            }
        }

        return args;
    }

    private static async Task<JsonObject> ApplyAutoExtractArgsAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        JsonObject? argsInput,
        RouteCandidate candidate,
        CompiledWorkflow workflow,
        JsonObject args,
        CancellationToken ct)
    {
        var config = ParseAutoExtractConfig(argsInput);
        if (!config.Enabled)
            return args;

        var schema = workflow.Source.Inputs is { Count: > 0 } workflowInputs
            ? JsonSchemaConverter.InputsToJsonSchema(workflowInputs)
            : candidate.Inputs?.DeepClone();
        if (schema is null)
            return args;

        var allowedKeys = ExtractArgumentKeys(schema);
        var mappedArgs = FilterArgsToAllowedKeys(args, allowedKeys);

        var llm = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route args.auto_extract requires an LLM client");

        var (provider, model) = ctx.Engine.ResolveLlmTarget(config.Provider, config.Model);
        var response = await ctx.CallLLMAsync(llm, new LLMRequest
        {
            Provider = provider,
            Model = model ?? "",
            Temperature = config.Temperature ?? 0,
            Prompt = BuildArgumentExtractionPrompt(routeInput, candidate, workflow, mappedArgs, schema),
            StructuredOutputStrict = false,
            StructuredOutputSchema = BuildArgumentExtractionSchema(schema)
        }, "workflow.route.argument_extraction", ct);

        var json = response.Json ?? TryParseJsonObject(response.Text)
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route auto_extract did not return JSON");

        var extracted = json["arguments"] as JsonObject ?? json as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route auto_extract JSON must be an object");
        foreach (var (key, value) in extracted)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;
            if (!allowedKeys.Contains(key))
                continue;

            mappedArgs[key] = value.DeepClone();
        }

        return mappedArgs;
    }

    private static JsonObject FilterArgsToAllowedKeys(JsonObject args, HashSet<string> allowedKeys)
    {
        var filtered = new JsonObject();
        foreach (var (key, value) in args)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null || !allowedKeys.Contains(key))
                continue;

            filtered[key] = value.DeepClone();
        }

        return filtered;
    }

    private static HashSet<string> ExtractArgumentKeys(JsonNode schema)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        AddArgumentKeys(schema, keys);
        return keys;
    }

    private static void AddArgumentKeys(JsonNode? schema, HashSet<string> keys)
    {
        if (schema is not JsonObject obj)
            return;

        if (obj["properties"] is JsonObject properties)
        {
            foreach (var (key, _) in properties)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    keys.Add(key);
            }
        }

        foreach (var unionKey in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (obj[unionKey] is not JsonArray variants)
                continue;

            foreach (var variant in variants)
                AddArgumentKeys(variant, keys);
        }
    }

    private static AutoExtractConfig ParseAutoExtractConfig(JsonObject? argsInput)
    {
        var node = argsInput?["auto_extract"];
        if (node is null)
            return new AutoExtractConfig(false, null, null, null);

        if (node is JsonValue value && value.TryGetValue<bool>(out var enabled))
            return new AutoExtractConfig(enabled, null, null, null);

        if (node is JsonObject obj)
        {
            return new AutoExtractConfig(
                obj["enabled"]?.GetValue<bool>() ?? true,
                obj["provider"]?.GetValue<string>(),
                obj["model"]?.GetValue<string>(),
                obj["temperature"]?.GetValue<double>());
        }

        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.route args.auto_extract must be boolean or object");
    }

    private static HumanInputConfig ParseHumanInputConfig(JsonObject? argsInput)
    {
        var node = argsInput?["human_input"];
        if (node is null)
            return HumanInputConfig.Disabled;

        if (node is JsonValue value && value.TryGetValue<bool>(out var enabled))
        {
            return new HumanInputConfig(
                enabled,
                HumanInputContract.DefaultTimeoutMs,
                HumanInputConfig.DefaultMaxAttempts);
        }

        if (node is not JsonObject obj)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.route args.human_input must be boolean or object");
        }

        var timeoutMs = ReadHumanInputInteger(
            obj,
            "timeout_ms",
            HumanInputContract.DefaultTimeoutMs);
        var maxAttempts = ReadHumanInputInteger(
            obj,
            "max_attempts",
            HumanInputConfig.DefaultMaxAttempts);

        if (timeoutMs < 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.route args.human_input.timeout_ms must be zero or greater");
        }

        if (maxAttempts < 1)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.route args.human_input.max_attempts must be one or greater");
        }

        return new HumanInputConfig(
            ReadHumanInputBoolean(obj, "enabled", defaultValue: true),
            timeoutMs,
            maxAttempts);
    }

    private static bool ReadHumanInputBoolean(JsonObject obj, string propertyName, bool defaultValue)
    {
        if (obj[propertyName] is null)
            return defaultValue;
        if (obj[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result))
            return result;

        throw new WorkflowRuntimeException(
            ErrorCodes.InputValidation,
            $"workflow.route args.human_input.{propertyName} must be a boolean");
    }

    private static int ReadHumanInputInteger(JsonObject obj, string propertyName, int defaultValue)
    {
        if (obj[propertyName] is null)
            return defaultValue;
        if (obj[propertyName] is JsonValue value && value.TryGetValue<int>(out var result))
            return result;

        throw new WorkflowRuntimeException(
            ErrorCodes.InputValidation,
            $"workflow.route args.human_input.{propertyName} must be an integer");
    }

    private static async Task<JsonObject> CompleteRoutedInputsAsync(
        StepExecutionContext ctx,
        RouteCandidate candidate,
        string workflowName,
        WorkflowDef? workflow,
        JsonObject resolvedArgs,
        HumanInputConfig config,
        CancellationToken ct)
    {
        if (!config.Enabled)
            return resolvedArgs;

        var runId = string.IsNullOrWhiteSpace(ctx.Limits.RunId)
            ? Guid.NewGuid().ToString("N")
            : ctx.Limits.RunId;

        for (var attempt = 1; attempt <= config.MaxAttempts; attempt++)
        {
            var issues = FindRoutedInputIssues(workflow, resolvedArgs);
            if (issues.Count == 0)
                return resolvedArgs;

            var provider = ctx.Engine.HumanInputProvider
                ?? throw new WorkflowRuntimeException(
                    "NO_HITL_PROVIDER",
                    $"Routed workflow '{workflowName}' requires additional input, but no IHumanInputProvider is configured.",
                    details: BuildInputValidationDetails(workflowName, issues.SelectMany(static issue => issue.Errors)));

            var request = BuildRoutedInputRequest(
                ctx,
                candidate,
                workflowName,
                resolvedArgs,
                issues,
                runId,
                attempt,
                config);
            var response = await RequestRoutedInputAsync(ctx, provider, request, workflowName, ct);
            MergeHumanInputResponse(resolvedArgs, response, issues);
        }

        ValidateRoutedInputs(workflowName, workflow, resolvedArgs);
        return resolvedArgs;
    }

    private static List<RoutedInputIssue> FindRoutedInputIssues(
        WorkflowDef? workflow,
        JsonObject resolvedArgs)
    {
        var issues = new List<RoutedInputIssue>();
        if (workflow?.Inputs is not { Count: > 0 } definitions)
            return issues;

        foreach (var (name, definition) in definitions)
        {
            var errors = new List<string>();
            var value = resolvedArgs.ContainsKey(name) ? resolvedArgs[name] : null;
            if (definition.Required && value is null)
            {
                errors.Add($"Input '{name}' is required but was not provided.");
            }
            else if (value is not null)
            {
                InputTypeValidator.ValidateNode(value, definition, name, errors, 0);
            }

            if (errors.Count > 0)
                issues.Add(new RoutedInputIssue(name, definition, errors));
        }

        return issues;
    }

    private static HumanInputRequest BuildRoutedInputRequest(
        StepExecutionContext ctx,
        RouteCandidate candidate,
        string workflowName,
        JsonObject resolvedArgs,
        IReadOnlyList<RoutedInputIssue> issues,
        string runId,
        int attempt,
        HumanInputConfig config)
    {
        var fields = issues
            .Select(issue => BuildHumanInputField(issue, resolvedArgs[issue.Name]))
            .ToList();
        var validationErrors = issues.SelectMany(static issue => issue.Errors).ToArray();

        return new HumanInputRequest
        {
            RunId = runId,
            StepId = $"{ctx.Step.Id}:inputs:{SanitizeRunIdPart(candidate.Id)}:{attempt}:{Guid.NewGuid():N}",
            Prompt = $"Additional information is required to run workflow '{workflowName}'.",
            Mode = HumanInputContract.ModeForm,
            Context = new JsonObject
            {
                ["candidate_id"] = candidate.Id,
                ["candidate_name"] = candidate.Name,
                ["workflow"] = workflowName,
                ["attempt"] = attempt,
                ["max_attempts"] = config.MaxAttempts,
                ["requested_inputs"] = ToJsonArray(issues.Select(static issue => issue.Name)),
                ["validation_errors"] = new JsonArray(
                    validationErrors.Select(static error => (JsonNode?)JsonValue.Create(error)).ToArray())
            },
            Fields = fields,
            TimeoutMs = config.TimeoutMs
        };
    }

    private static HumanInputFieldDef BuildHumanInputField(
        RoutedInputIssue issue,
        JsonNode? currentValue)
    {
        var sensitive = IsSensitiveInputName(issue.Name);
        var description = string.IsNullOrWhiteSpace(issue.Definition.Description)
            ? $"Expected {DescribeInputType(issue.Definition)}. {string.Join(" ", issue.Errors)}"
            : $"{issue.Definition.Description} Expected {DescribeInputType(issue.Definition)}. {string.Join(" ", issue.Errors)}";

        return new HumanInputFieldDef
        {
            Name = issue.Name,
            Type = MapHumanInputFieldType(issue.Name, issue.Definition),
            Required = issue.Definition.Required,
            Description = description,
            Default = sensitive ? null : FormatHumanInputDefault(currentValue)
        };
    }

    private static string MapHumanInputFieldType(string name, InputDef definition)
    {
        if (IsSensitiveInputName(name))
            return "secret";

        return definition.Type.Trim().ToLowerInvariant() switch
        {
            "string" => "string",
            "number" => "number",
            "integer" => "integer",
            "boolean" => "boolean",
            _ => "json"
        };
    }

    private static string DescribeInputType(InputDef definition)
        => definition.Type.Trim().ToLowerInvariant() switch
        {
            "array" when definition.Items is not null => $"array of {DescribeInputType(definition.Items)} values",
            "dictionary" when definition.AdditionalProperties is not null => $"dictionary of {DescribeInputType(definition.AdditionalProperties)} values",
            var type => type
        };

    private static bool IsSensitiveInputName(string name)
    {
        var normalized = name.ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal)
               || normalized.Contains("secret", StringComparison.Ordinal)
               || normalized.Contains("token", StringComparison.Ordinal)
               || normalized.Contains("api_key", StringComparison.Ordinal)
               || normalized.Contains("apikey", StringComparison.Ordinal)
               || normalized.EndsWith("_key", StringComparison.Ordinal);
    }

    private static string? FormatHumanInputDefault(JsonNode? value)
    {
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text;
        if (value is JsonValue scalar)
            return scalar.ToJsonString();
        return value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<JsonNode?> RequestRoutedInputAsync(
        StepExecutionContext ctx,
        IHumanInputProvider provider,
        HumanInputRequest request,
        string workflowName,
        CancellationToken ct)
    {
        ctx.AddTelemetryEvent("gnougo-flow.step.waiting_for_human", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.prompt", request.Prompt),
            new KeyValuePair<string, object?>("gnougo-flow.human.request", BuildHumanInputRequestPayload(request).ToJsonString()),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.workflow.name", workflowName)
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutMs > 0)
            timeout.CancelAfter(request.TimeoutMs);

        try
        {
            var response = await provider.RequestInputAsync(request, timeout.Token);
            ctx.AddTelemetryEvent("gnougo-flow.step.human_input_resumed", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.human.run_id", request.RunId),
                new KeyValuePair<string, object?>("gnougo-flow.human.step_id", request.StepId),
                new KeyValuePair<string, object?>("gnougo-flow.workflow_route.workflow.name", workflowName)
            });
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Human input received for workflow '{workflowName}'."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info"),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.source", "workflow.route"),
                new KeyValuePair<string, object?>("gnougo-flow.workflow_route.workflow.name", workflowName)
            });
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkflowRuntimeException(
                "HUMAN_INPUT_TIMEOUT",
                $"workflow.route timed out after {request.TimeoutMs}ms waiting for inputs for workflow '{workflowName}'.");
        }
    }

    private static JsonObject BuildHumanInputRequestPayload(HumanInputRequest request)
        => HumanInputContract.BuildRequestPayload(request);

    private static void MergeHumanInputResponse(
        JsonObject resolvedArgs,
        JsonNode? response,
        IReadOnlyList<RoutedInputIssue> issues)
    {
        if (response is JsonObject responseObject)
        {
            foreach (var issue in issues)
            {
                if (!responseObject.TryGetPropertyValue(issue.Name, out var value))
                    continue;
                resolvedArgs[issue.Name] = NormalizeHumanInputValue(value, issue.Definition);
            }
            return;
        }

        if (issues.Count == 1)
            resolvedArgs[issues[0].Name] = NormalizeHumanInputValue(response, issues[0].Definition);
    }

    private static JsonNode? NormalizeHumanInputValue(JsonNode? value, InputDef definition)
    {
        if (value is null)
            return null;
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
            return value.DeepClone();

        var type = definition.Type.Trim().ToLowerInvariant();
        if (type == "string")
            return JsonValue.Create(text);
        if (type == "integer"
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (type == "number"
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        if (type == "boolean" && TryParseBoolean(text, out var boolean))
            return JsonValue.Create(boolean);

        if (type is "array" or "object" or "dictionary")
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return JsonValue.Create(text);
            }
        }

        return value.DeepClone();
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;
        if (value is "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }
        if (value is "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private static void ValidateRoutedInputs(
        string workflowName,
        WorkflowDef? workflow,
        JsonObject resolvedArgs)
    {
        var inputErrors = InputTypeValidator.Validate(workflow, resolvedArgs);
        if (inputErrors.Count == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.InputValidation,
            $"Input validation failed for routed workflow '{workflowName}': {string.Join("; ", inputErrors)}",
            details: BuildInputValidationDetails(workflowName, inputErrors));
    }

    private static JsonObject BuildInputValidationDetails(
        string workflowName,
        IEnumerable<string> inputErrors)
        => new()
        {
            ["workflow"] = workflowName,
            ["validation_errors"] = new JsonArray(
                inputErrors.Select(static error => (JsonNode)JsonValue.Create(error)!).ToArray())
        };

    private static async Task<List<RouteExecutionResult>> ExecuteSelectedSequentialAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        List<RouteCandidate> selected,
        JsonObject args,
        JsonObject? argsInput,
        CancellationToken ct)
    {
        var results = new List<RouteExecutionResult>();
        foreach (var candidate in selected)
            results.Add(await ExecuteCandidateAsync(ctx, routeInput, candidate, args, argsInput, ct));
        return results;
    }

    private static async Task<List<RouteExecutionResult>> ExecuteSelectedParallelAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        List<RouteCandidate> selected,
        JsonObject args,
        JsonObject? argsInput,
        int maxConcurrency,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = selected.Select(async candidate =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await ExecuteCandidateAsync(ctx, routeInput, candidate, args, argsInput, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<List<PreparedRouteCandidate>> PrepareSelectedSequentialAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        IReadOnlyList<RouteCandidate> selected,
        JsonObject args,
        JsonObject? argsInput,
        HumanInputConfig humanInputConfig,
        CancellationToken ct)
    {
        var prepared = new List<PreparedRouteCandidate>(selected.Count);
        foreach (var candidate in selected)
        {
            prepared.Add(await PrepareCandidateAsync(
                ctx,
                routeInput,
                candidate,
                args,
                argsInput,
                humanInputConfig,
                ct));
        }

        return prepared;
    }

    private static async Task<List<RouteExecutionResult>> ExecutePreparedSequentialAsync(
        StepExecutionContext ctx,
        IReadOnlyList<PreparedRouteCandidate> prepared,
        CancellationToken ct)
    {
        var results = new List<RouteExecutionResult>(prepared.Count);
        foreach (var candidate in prepared)
            results.Add(await ExecutePreparedCandidateAsync(ctx, candidate, ct));
        return results;
    }

    private static async Task<List<RouteExecutionResult>> ExecutePreparedParallelAsync(
        StepExecutionContext ctx,
        IReadOnlyList<PreparedRouteCandidate> prepared,
        int maxConcurrency,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = prepared.Select(async candidate =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await ExecutePreparedCandidateAsync(ctx, candidate, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<RouteExecutionResult> ExecuteCandidateAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        RouteCandidate candidate,
        JsonObject args,
        JsonObject? argsInput,
        CancellationToken ct)
    {
        var prepared = await PrepareCandidateAsync(
            ctx,
            routeInput,
            candidate,
            args,
            argsInput,
            HumanInputConfig.Disabled,
            ct);
        return await ExecutePreparedCandidateAsync(ctx, prepared, ct);
    }

    private static async Task<PreparedRouteCandidate> PrepareCandidateAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        RouteCandidate candidate,
        JsonObject args,
        JsonObject? argsInput,
        HumanInputConfig humanInputConfig,
        CancellationToken ct)
    {
        var kind = candidate.Ref["kind"]?.GetValue<string>() ?? "local";
        var resolution = await ctx.Engine.WorkflowCallResolver.ResolveAsync(new WorkflowCallResolutionContext
        {
            Engine = ctx.Engine,
            Ref = candidate.Ref,
            Kind = kind,
            CallDepth = ctx.CallDepth,
            CallStack = ctx.CallStack,
            ActiveDocument = ctx.ActiveDocument
        }, ct);

        if (!string.IsNullOrWhiteSpace(resolution.CallStackKey) && ctx.CallStack.Contains(resolution.CallStackKey))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Cycle detected: workflow '{resolution.WorkflowName}' already in call stack");

        var childEngine = new WorkflowEngine(ctx.Engine.Registry)
        {
            LLMClient = ctx.Engine.LLMClient,
            ModelUsageCostEstimator = ctx.Engine.ModelUsageCostEstimator,
            LLMUsageBudget = ctx.LLMUsageBudget ?? ctx.Engine.LLMUsageBudget,
            WorkflowFetcher = ctx.Engine.WorkflowFetcher,
            TemplateEngine = ctx.Engine.TemplateEngine,
            McpClientFactory = ctx.Engine.McpClientFactory,
            HumanInputProvider = ctx.Engine.HumanInputProvider,
            Checkpointer = null,
            WorkflowCallResolver = ctx.Engine.WorkflowCallResolver,
            WorkflowCandidateProvider = ctx.Engine.WorkflowCandidateProvider,
            Telemetry = ctx.Engine.Telemetry,
            LlmDefaults = ctx.Engine.LlmDefaults,
            FetchPolicy = ctx.Engine.FetchPolicy,
            Limits = CreateChildLimits(ctx.Limits, candidate),
            Logger = ctx.Engine.Logger,
            McpCache = ctx.Engine.McpCache,
            McpCacheSlidingExpiration = ctx.Engine.McpCacheSlidingExpiration
        };

        var candidateArgs = args.DeepClone() as JsonObject ?? new JsonObject();
        candidateArgs = await ApplyAutoExtractArgsAsync(ctx, routeInput, argsInput, candidate, resolution.Workflow, candidateArgs, ct);
        var resolvedArgs = WorkflowInputDefaults.Apply(resolution.Workflow.Source, candidateArgs);
        resolvedArgs = await CompleteRoutedInputsAsync(
            ctx,
            candidate,
            resolution.WorkflowName,
            resolution.Workflow.Source,
            resolvedArgs,
            humanInputConfig,
            ct);
        ValidateRoutedInputs(resolution.WorkflowName, resolution.Workflow.Source, resolvedArgs);
        EmitRoutedInputsTelemetry(ctx, candidate, resolution.WorkflowName, candidateArgs, resolvedArgs, argsInput);

        var newCallStack = new HashSet<string>(ctx.CallStack);
        if (!string.IsNullOrWhiteSpace(resolution.CallStackKey))
            newCallStack.Add(resolution.CallStackKey);

        return new PreparedRouteCandidate(
            candidate,
            resolution.WorkflowName,
            resolution.Workflow,
            childEngine,
            resolvedArgs,
            newCallStack);
    }

    private static async Task<RouteExecutionResult> ExecutePreparedCandidateAsync(
        StepExecutionContext ctx,
        PreparedRouteCandidate prepared,
        CancellationToken ct)
    {
        var result = await prepared.ChildEngine.ExecuteChildWorkflowAsync(
            prepared.Workflow,
            prepared.ResolvedArgs,
            prepared.ChildEngine.Limits,
            ctx.CallDepth + 1,
            prepared.CallStack,
            ctx.TelemetrySpan,
            ct);

        return new RouteExecutionResult(
            Candidate: prepared.Candidate,
            WorkflowName: prepared.WorkflowName,
            Success: result.Success,
            Outputs: result.Outputs?.DeepClone(),
            Error: result.Error?.Message,
            ErrorCode: result.Error?.Code,
            ErrorType: result.Error?.Type,
            ErrorDetails: result.Error?.Details?.DeepClone(),
            HandledErrors: BuildHandledErrorsArray(result.StepResults),
            StepsExecuted: result.StepResults.Count);
    }

    private static void EmitRoutedInputsTelemetry(
        StepExecutionContext ctx,
        RouteCandidate candidate,
        string workflowName,
        JsonObject candidateArgs,
        JsonObject resolvedArgs,
        JsonObject? argsInput)
    {
        var autoExtractEnabled = ParseAutoExtractConfig(argsInput).Enabled;
        var argumentKeys = string.Join(",", candidateArgs.Select(static kv => kv.Key));
        var resolvedInputKeys = string.Join(",", resolvedArgs.Select(static kv => kv.Key));
        var attributes = new List<KeyValuePair<string, object?>>
        {
            new KeyValuePair<string, object?>("gnougo-flow.step.id", ctx.Step.Id),
            new KeyValuePair<string, object?>("gnougo-flow.step.type", ctx.Step.Type),
            new KeyValuePair<string, object?>("gnougo-flow.step.call_depth", ctx.CallDepth),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.candidate.id", candidate.Id),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.candidate.name", candidate.Name),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.workflow.name", workflowName),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.auto_extract.enabled", autoExtractEnabled),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.arguments.keys", argumentKeys),
            new KeyValuePair<string, object?>("gnougo-flow.workflow_route.resolved_inputs.keys", resolvedInputKeys)
        };

        if (ctx.Limits.LogStepContent)
        {
            attributes.Add(new KeyValuePair<string, object?>(
                "gnougo-flow.workflow_route.arguments",
                WorkflowTelemetryInputAttributes.FormatForTelemetry(candidateArgs)));
            attributes.Add(new KeyValuePair<string, object?>(
                "gnougo-flow.workflow_route.resolved_inputs",
                WorkflowTelemetryInputAttributes.FormatForTelemetry(resolvedArgs)));
        }

        ctx.AddTelemetryEvent("gnougo-flow.workflow_route.inputs_extracted", attributes);

        var message = ctx.Limits.LogStepContent
            ? $"Triggering workflow '{workflowName}' with inputs {WorkflowTelemetryInputAttributes.FormatForTelemetry(resolvedArgs)}"
            : $"Triggering workflow '{workflowName}' with input keys: {resolvedInputKeys}";
        var thinkingAttributes = new List<KeyValuePair<string, object?>>
        {
            new("gnougo-flow.thinking.message", message),
            new("gnougo-flow.thinking.level", "progress"),
            new("gnougo-flow.thinking.source", "workflow.route"),
            new("gnougo-flow.workflow_route.candidate.id", candidate.Id),
            new("gnougo-flow.workflow_route.candidate.name", candidate.Name),
            new("gnougo-flow.workflow_route.workflow.name", workflowName),
            new("gnougo-flow.workflow_route.arguments.keys", argumentKeys),
            new("gnougo-flow.workflow_route.resolved_inputs.keys", resolvedInputKeys)
        };

        if (ctx.Limits.LogStepContent)
        {
            thinkingAttributes.Add(new KeyValuePair<string, object?>(
                "gnougo-flow.workflow_route.resolved_inputs",
                WorkflowTelemetryInputAttributes.FormatForTelemetry(resolvedArgs)));
        }

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", thinkingAttributes);
    }

    private static async Task<string?> CombineAsync(
        StepExecutionContext ctx,
        JsonObject input,
        string prompt,
        List<RouteExecutionResult> results,
        string strategy,
        CancellationToken ct)
    {
        if (string.Equals(strategy, "raw", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(strategy, "first", StringComparison.OrdinalIgnoreCase) || results.Count == 1)
            return ExtractAnswer(results.FirstOrDefault()?.Outputs) ?? results.FirstOrDefault()?.Outputs?.ToJsonString();

        var llm = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route combine strategy 'synthesize' requires an LLM client");

        var combineInput = input["combine"] as JsonObject;
        var requestedProvider = combineInput?["provider"]?.GetValue<string>();
        var requestedModel = combineInput?["model"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);

        var response = await ctx.CallLLMAsync(llm, new LLMRequest
        {
            Provider = provider,
            Model = model ?? "",
            Temperature = combineInput?["temperature"]?.GetValue<double>() ?? 0.2,
            Prompt = BuildSynthesisPrompt(prompt, input["history"], results)
        }, "workflow.route.synthesis", ct);

        return response.Text.Trim();
    }

    private static string BuildSelectionPrompt(
        string prompt,
        JsonNode? history,
        IReadOnlyList<RouteCandidate> candidates,
        int minSelected,
        int maxSelected)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a workflow router. Select the best workflow candidates for the user prompt.");
        sb.AppendLine($"Return JSON only: {{\"selected\":[{{\"id\":\"candidate-id\",\"reason\":\"short reason\",\"confidence\":0.0}}]}}.");
        sb.AppendLine($"Select at least {minSelected} and at most {maxSelected} candidate(s).");
        sb.AppendLine();
        sb.AppendLine("[USER PROMPT]");
        sb.AppendLine(prompt);
        if (history != null)
        {
            sb.AppendLine();
            sb.AppendLine("[RECENT HISTORY]");
            sb.AppendLine(history.ToJsonString());
        }
        sb.AppendLine();
        sb.AppendLine("[CANDIDATES]");
        foreach (var candidate in candidates)
        {
            sb.AppendLine($"- id: {candidate.Id}");
            sb.AppendLine($"  name: {candidate.Name}");
            if (!string.IsNullOrWhiteSpace(candidate.Description))
                sb.AppendLine($"  description: {candidate.Description}");
            if (candidate.Tags.Count > 0)
                sb.AppendLine($"  tags: {string.Join(", ", candidate.Tags)}");
            if (candidate.Inputs != null)
                sb.AppendLine($"  inputs: {candidate.Inputs.ToJsonString()}");
            if (candidate.Outputs != null)
                sb.AppendLine($"  outputs: {candidate.Outputs.ToJsonString()}");
        }
        return sb.ToString();
    }

    private static string BuildSynthesisPrompt(string prompt, JsonNode? history, IReadOnlyList<RouteExecutionResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synthesize a concise final answer for the user from the routed workflow results.");
        sb.AppendLine("Start directly with the answer. Do not mention routing unless it is necessary for clarity.");
        sb.AppendLine();
        sb.AppendLine("[USER PROMPT]");
        sb.AppendLine(prompt);
        if (history != null)
        {
            sb.AppendLine();
            sb.AppendLine("[RECENT HISTORY]");
            sb.AppendLine(history.ToJsonString());
        }
        sb.AppendLine();
        sb.AppendLine("[WORKFLOW RESULTS]");
        sb.AppendLine(BuildResultsArray(results).ToJsonString());
        return sb.ToString();
    }

    private static string BuildArgumentExtractionPrompt(
        JsonObject routeInput,
        RouteCandidate candidate,
        CompiledWorkflow workflow,
        JsonObject currentArgs,
        JsonNode schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You extract workflow input arguments from a user prompt and recent history.");
        sb.AppendLine("Return JSON only in this shape: {\"arguments\":{...}}.");
        sb.AppendLine("The selected workflow's declared YAML inputs are authoritative.");
        sb.AppendLine("Return only keys declared in [EXPECTED WORKFLOW INPUT JSON SCHEMA].");
        sb.AppendLine("Map natural-language data into those exact input names; do not copy parent routing aliases unless the alias is itself a declared input.");
        sb.AppendLine("Only include fields you can infer confidently or that already exist in current arguments.");
        sb.AppendLine("Use defaults from the schema or current arguments when present. Do not invent values for unknown required fields.");
        sb.AppendLine();
        sb.AppendLine("[SELECTED WORKFLOW]");
        sb.AppendLine($"id: {candidate.Id}");
        sb.AppendLine($"name: {candidate.Name}");
        if (!string.IsNullOrWhiteSpace(candidate.Description))
            sb.AppendLine($"description: {candidate.Description}");
        if (candidate.Tags.Count > 0)
            sb.AppendLine($"tags: {string.Join(", ", candidate.Tags)}");
        sb.AppendLine($"workflow_name: {workflow.Name}");
        if (candidate.Inputs != null)
        {
            sb.AppendLine();
            sb.AppendLine("[CANDIDATE SKILL INPUT HINTS]");
            sb.AppendLine(candidate.Inputs.ToJsonString());
        }
        sb.AppendLine();
        sb.AppendLine("[EXPECTED WORKFLOW INPUT JSON SCHEMA]");
        sb.AppendLine(schema.ToJsonString());
        sb.AppendLine();
        sb.AppendLine("[CURRENT ARGUMENTS]");
        sb.AppendLine(currentArgs.ToJsonString());
        sb.AppendLine();
        sb.AppendLine("[USER PROMPT]");
        sb.AppendLine(routeInput["prompt"]?.GetValue<string>() ?? routeInput["task"]?.GetValue<string>() ?? routeInput["query"]?.GetValue<string>() ?? "");
        if (routeInput["history"] != null)
        {
            sb.AppendLine();
            sb.AppendLine("[RECENT HISTORY]");
            sb.AppendLine(routeInput["history"]!.ToJsonString());
        }
        return sb.ToString();
    }

    private static JsonObject BuildSelectionSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("selected"),
        ["properties"] = new JsonObject
        {
            ["selected"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("id"),
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["reason"] = new JsonObject { ["type"] = "string" },
                        ["confidence"] = new JsonObject { ["type"] = "number" }
                    }
                }
            }
        }
    };

    private static JsonObject BuildArgumentExtractionSchema(JsonNode argumentSchema) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("arguments"),
        ["properties"] = new JsonObject
        {
            ["arguments"] = BuildLooseExtractionArgumentSchema(argumentSchema)
        }
    };

    private static JsonNode BuildLooseExtractionArgumentSchema(JsonNode argumentSchema)
    {
        var schema = argumentSchema.DeepClone();
        RemoveRequiredAndCloseObjects(schema);
        return schema;
    }

    private static void RemoveRequiredAndCloseObjects(JsonNode? schema)
    {
        if (schema is JsonObject obj)
        {
            obj.Remove("required");

            var type = obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeText)
                ? typeText
                : null;
            if (string.Equals(type, "object", StringComparison.OrdinalIgnoreCase) || obj["properties"] is JsonObject)
                obj["additionalProperties"] = false;

            if (obj["properties"] is JsonObject properties)
            {
                foreach (var (_, propertySchema) in properties)
                    RemoveRequiredAndCloseObjects(propertySchema);
            }

            foreach (var unionKey in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (obj[unionKey] is not JsonArray variants)
                    continue;

                foreach (var variant in variants)
                    RemoveRequiredAndCloseObjects(variant);
            }

            RemoveRequiredAndCloseObjects(obj["items"]);
            RemoveRequiredAndCloseObjects(obj["additionalProperties"]);
            return;
        }

        if (schema is JsonArray array)
        {
            foreach (var item in array)
                RemoveRequiredAndCloseObjects(item);
        }
    }

    private static JsonArray BuildSelectedArray(IEnumerable<RouteCandidate> selected)
    {
        var array = new JsonArray();
        foreach (var candidate in selected)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["id"] = candidate.Id,
                ["name"] = candidate.Name,
                ["ref"] = candidate.Ref.DeepClone(),
                ["description"] = candidate.Description,
                ["reason"] = candidate.Reason,
                ["confidence"] = candidate.Confidence,
                ["tags"] = ToJsonArray(candidate.Tags)
            });
        }
        return array;
    }

    private static JsonArray BuildResultsArray(IEnumerable<RouteExecutionResult> results)
    {
        var array = new JsonArray();
        foreach (var result in results)
        {
            var item = new JsonObject
            {
                ["id"] = result.Candidate.Id,
                ["name"] = result.Candidate.Name,
                ["ref"] = result.Candidate.Ref.DeepClone(),
                ["workflow"] = result.WorkflowName,
                ["success"] = result.Success,
                ["outputs"] = result.Outputs?.DeepClone(),
                ["error"] = result.Error,
                ["error_code"] = result.ErrorCode,
                ["error_type"] = result.ErrorType,
                ["error_details"] = result.ErrorDetails?.DeepClone(),
                ["handled_errors"] = result.HandledErrors?.DeepClone(),
                ["run"] = new JsonObject
                {
                    ["steps_executed"] = result.StepsExecuted
                }
            };
            array.Add((JsonNode)item);
        }
        return array;
    }

    private static JsonArray? BuildHandledErrorsArray(IEnumerable<StepResult> stepResults)
    {
        var array = new JsonArray();
        foreach (var stepResult in stepResults)
        {
            var error = stepResult.Error;
            if (error is null)
                continue;

            array.Add((JsonNode)new JsonObject
            {
                ["step_id"] = stepResult.StepId,
                ["step_type"] = stepResult.StepType,
                ["status"] = stepResult.Status.ToString(),
                ["code"] = error.Code,
                ["type"] = error.Type,
                ["message"] = error.Message,
                ["details"] = error.Details?.DeepClone()
            });
        }

        return array.Count == 0 ? null : array;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
        => node is JsonArray array
            ? array.Select(static item => item?.GetValue<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .ToArray()
            : Array.Empty<string>();

    private static JsonObject? TryParseJsonObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractAnswer(JsonNode? outputs)
    {
        if (outputs is not JsonObject obj)
            return outputs?.ToJsonString();

        foreach (var propertyName in new[] { "answer", "text", "result", "response" })
        {
            if (!obj.TryGetPropertyValue(propertyName, out var value))
                continue;

            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                return text;

            return value?.ToJsonString() ?? "null";
        }

        return null;
    }

    private static ExecutionLimits CreateChildLimits(ExecutionLimits parent, RouteCandidate candidate)
    {
        var parentRunId = string.IsNullOrWhiteSpace(parent.RunId)
            ? Guid.NewGuid().ToString("N")
            : parent.RunId;

        return new ExecutionLimits
        {
            MaxTotalStepsExecuted = parent.MaxTotalStepsExecuted,
            MaxCallDepth = parent.MaxCallDepth,
            MaxParallelBranches = parent.MaxParallelBranches,
            MaxLoopIterations = parent.MaxLoopIterations,
            MaxExpressionAstNodes = parent.MaxExpressionAstNodes,
            MaxExpressionStatements = parent.MaxExpressionStatements,
            ExpressionTimeoutSeconds = parent.ExpressionTimeoutSeconds,
            ExpressionMemoryLimitBytes = parent.ExpressionMemoryLimitBytes,
            MaxSwitchCases = parent.MaxSwitchCases,
            MaxFunctionCallDepth = parent.MaxFunctionCallDepth,
            LogStepContent = parent.LogStepContent,
            TenantId = parent.TenantId,
            ExecutionId = parent.ExecutionId ?? parent.RunId,
            AgentId = parent.AgentId,
            AgentName = parent.AgentName,
            RunId = $"{parentRunId}:route:{SanitizeRunIdPart(candidate.Id)}:{Guid.NewGuid():N}"
        };
    }

    private static string SanitizeRunIdPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "candidate";

        var sb = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var c in value.Trim())
        {
            if (sb.Length >= 64)
                break;

            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.Length == 0 ? "candidate" : sb.ToString();
    }

    private sealed record RouteCandidate(
        string Id,
        string Name,
        JsonObject Ref,
        string? Description,
        List<string> Tags,
        JsonNode? Inputs,
        JsonNode? Outputs,
        string? Reason,
        double? Confidence)
    {
        public static RouteCandidate FromProvider(WorkflowRouteCandidate candidate)
            => new(
                string.IsNullOrWhiteSpace(candidate.Id) ? $"{candidate.Ref["kind"]?.GetValue<string>() ?? "candidate"}:{candidate.Name}" : candidate.Id,
                candidate.Name,
                candidate.Ref.DeepClone() as JsonObject ?? new JsonObject(),
                candidate.Description,
                candidate.Tags,
                candidate.Inputs?.DeepClone(),
                candidate.Outputs?.DeepClone(),
                null,
                null);
    }

    private sealed record RouteExecutionResult(
        RouteCandidate Candidate,
        string WorkflowName,
        bool Success,
        JsonNode? Outputs,
        string? Error,
        string? ErrorCode,
        string? ErrorType,
        JsonNode? ErrorDetails,
        JsonArray? HandledErrors,
        int StepsExecuted);

    private sealed record PreparedRouteCandidate(
        RouteCandidate Candidate,
        string WorkflowName,
        CompiledWorkflow Workflow,
        WorkflowEngine ChildEngine,
        JsonObject ResolvedArgs,
        HashSet<string> CallStack);

    private sealed record RoutedInputIssue(
        string Name,
        InputDef Definition,
        IReadOnlyList<string> Errors);

    private sealed record AutoExtractConfig(
        bool Enabled,
        string? Provider,
        string? Model,
        double? Temperature);

    private sealed record HumanInputConfig(
        bool Enabled,
        int TimeoutMs,
        int MaxAttempts)
    {
        public const int DefaultMaxAttempts = 3;

        public static HumanInputConfig Disabled { get; } = new(
            false,
            HumanInputContract.DefaultTimeoutMs,
            DefaultMaxAttempts);
    }
}
