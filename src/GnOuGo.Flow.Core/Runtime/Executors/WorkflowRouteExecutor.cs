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
        new(ErrorCodes.WorkflowCycleDetected, false, "A selected workflow call would exceed route call-depth limits or create a call cycle.")
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
              add:
                history: "${data.inputs.history}"
            execution:
              parallel: true
              max_concurrency: 3
            combine:
              strategy: synthesize             # "synthesize", "first", or "raw"
        ```
        Output: `{ selected: [...], results: [...], answer?, text? }`
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

        var routeResults = executeInParallel
            ? await ExecuteSelectedParallelAsync(ctx, input, selected, args, argsInput, maxConcurrency, ct)
            : await ExecuteSelectedSequentialAsync(ctx, input, selected, args, argsInput, ct);

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
        var response = await llm.CallAsync(new LLMRequest
        {
            Provider = provider,
            Model = model ?? "",
            Prompt = selectionPrompt,
            Temperature = selectionInput?["temperature"]?.GetValue<double>() ?? 0,
            StructuredOutputStrict = false,
            StructuredOutputSchema = BuildSelectionSchema()
        }, ct);

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

        var schema = candidate.Inputs?.DeepClone()
            ?? (workflow.Source.Inputs is { Count: > 0 } workflowInputs
                ? JsonSchemaConverter.InputsToJsonSchema(workflowInputs)
                : null);
        if (schema is null)
            return args;

        var llm = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route args.auto_extract requires an LLM client");

        var (provider, model) = ctx.Engine.ResolveLlmTarget(config.Provider, config.Model);
        var response = await llm.CallAsync(new LLMRequest
        {
            Provider = provider,
            Model = model ?? "",
            Temperature = config.Temperature ?? 0,
            Prompt = BuildArgumentExtractionPrompt(routeInput, candidate, workflow, args, schema),
            StructuredOutputStrict = false,
            StructuredOutputSchema = BuildArgumentExtractionSchema()
        }, ct);

        var json = response.Json ?? TryParseJsonObject(response.Text)
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route auto_extract did not return JSON");

        var extracted = json["arguments"] as JsonObject ?? json as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "workflow.route auto_extract JSON must be an object");
        foreach (var (key, value) in extracted)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;

            args[key] = value.DeepClone();
        }

        return args;
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

    private static async Task<RouteExecutionResult> ExecuteCandidateAsync(
        StepExecutionContext ctx,
        JsonObject routeInput,
        RouteCandidate candidate,
        JsonObject args,
        JsonObject? argsInput,
        CancellationToken ct)
    {
        var kind = candidate.Ref["kind"]?.GetValue<string>() ?? "local";
        var resolution = await ctx.Engine.WorkflowCallResolver.ResolveAsync(new WorkflowCallResolutionContext
        {
            Engine = ctx.Engine,
            Ref = candidate.Ref,
            Kind = kind,
            CallDepth = ctx.CallDepth,
            CallStack = ctx.CallStack
        }, ct);

        if (!string.IsNullOrWhiteSpace(resolution.CallStackKey) && ctx.CallStack.Contains(resolution.CallStackKey))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Cycle detected: workflow '{resolution.WorkflowName}' already in call stack");

        var childEngine = new WorkflowEngine(ctx.Engine.Registry)
        {
            LLMClient = ctx.Engine.LLMClient,
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
            McpCache = ctx.Engine.McpCache
        };

        var candidateArgs = args.DeepClone() as JsonObject ?? new JsonObject();
        candidateArgs = await ApplyAutoExtractArgsAsync(ctx, routeInput, argsInput, candidate, resolution.Workflow, candidateArgs, ct);
        var resolvedArgs = WorkflowInputDefaults.Apply(resolution.Workflow.Source, candidateArgs);
        var newCallStack = new HashSet<string>(ctx.CallStack);
        if (!string.IsNullOrWhiteSpace(resolution.CallStackKey))
            newCallStack.Add(resolution.CallStackKey);
        var result = await childEngine.ExecuteChildWorkflowAsync(
            resolution.Workflow,
            resolvedArgs,
            childEngine.Limits,
            ctx.CallDepth + 1,
            newCallStack,
            ctx.TelemetrySpan,
            ct);

        return new RouteExecutionResult(
            Candidate: candidate,
            WorkflowName: resolution.WorkflowName,
            Success: result.Success,
            Outputs: result.Outputs?.DeepClone(),
            Error: result.Error?.Message,
            StepsExecuted: result.StepResults.Count);
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

        var response = await llm.CallAsync(new LLMRequest
        {
            Provider = provider,
            Model = model ?? "",
            Temperature = combineInput?["temperature"]?.GetValue<double>() ?? 0.2,
            Prompt = BuildSynthesisPrompt(prompt, input["history"], results)
        }, ct);

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
            sb.AppendLine("[SKILL INPUTS]");
            sb.AppendLine(candidate.Inputs.ToJsonString());
        }
        sb.AppendLine();
        sb.AppendLine("[WORKFLOW INPUT JSON SCHEMA]");
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

    private static JsonObject BuildArgumentExtractionSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("arguments"),
        ["properties"] = new JsonObject
        {
            ["arguments"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = true
            }
        }
    };

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
            array.Add((JsonNode)new JsonObject
            {
                ["id"] = result.Candidate.Id,
                ["name"] = result.Candidate.Name,
                ["workflow"] = result.WorkflowName,
                ["success"] = result.Success,
                ["outputs"] = result.Outputs?.DeepClone(),
                ["error"] = result.Error,
                ["run"] = new JsonObject
                {
                    ["steps_executed"] = result.StepsExecuted
                }
            });
        }
        return array;
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

        return obj["answer"]?.GetValue<string>()
            ?? obj["text"]?.GetValue<string>()
            ?? obj["result"]?.GetValue<string>()
            ?? obj["response"]?.GetValue<string>();
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
        int StepsExecuted);

    private sealed record AutoExtractConfig(
        bool Enabled,
        string? Provider,
        string? Model,
        double? Temperature);
}
