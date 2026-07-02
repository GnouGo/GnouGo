using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Generates a workflow dynamically via LLM under policy constraints.
/// Builds a comprehensive prompt from DslReference (common) + each executor's DslSnippet (step-specific).
/// </summary>
public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private const int DefaultMcpDiscoveryTimeoutSeconds = 30;
    private const int MinMcpDiscoveryTimeoutSeconds = 1;
    private const int MaxMcpDiscoveryTimeoutSeconds = 300;
    private const int McpDiscoveryMaxAttempts = 3;
    private const int McpDiscoveryRetryBaseDelayMilliseconds = 500;
    private const int DefaultPlanRepairMaxAttempts = 3;
    private static readonly JsonSerializerOptions PromptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string[] McpInputContractChecklist =
    {
        "1. Inspect every MCP tool used by this workflow.",
        "2. For each required MCP argument, ensure the workflow has a matching input or a previous step that produces it.",
        "3. If a required MCP argument is missing, add it to skill.inputs and workflow.inputs with the exact MCP schema type.",
        "4. Never satisfy a missing required MCP argument with data.env.*, empty string, fake values, or casts.",
        "5. Never convert a string input to a number just to satisfy an MCP schema.",
        "6. Follow the discovered MCP schema and tool description exactly without adding Flow-specific request conventions.",
        "7. Prefer the exact MCP argument name and type."
    };

    public string StepType => "workflow.plan";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "workflow.plan input, generator, policy or validation sections are malformed or missing required fields."),
        new(ErrorCodes.TemplatePlan, false, "The planning LLM is unavailable or the generated workflow could not be made valid after the configured reprompts."),
        new(ErrorCodes.TemplatePolicy, false, "The generated workflow violates allowed step types, denied step types, or max step limits.")
    };

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan input must be object");

        var mode = GetConfiguredPlanMode(input);

        if (string.Equals(mode, "repair", StringComparison.OrdinalIgnoreCase))
        {
            var result = await ExecuteRepairPlanAsync(ctx, input, ct);
            AttachPlanModeMetadata(result, "repair", null);
            return result;
        }

        if (string.Equals(mode, "pipeline", StringComparison.OrdinalIgnoreCase))
        {
            var result = await ExecutePipelineAsync(ctx, input, ct);
            AttachPlanModeMetadata(result, "pipeline", null);
            return result;
        }

        if (string.Equals(mode, "basic", StringComparison.OrdinalIgnoreCase))
        {
            var result = await ExecuteSinglePlanAsync(ctx, input, ct);
            AttachPlanModeMetadata(result, "basic", null);
            return result;
        }

        if (!string.IsNullOrWhiteSpace(mode) && !string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"workflow.plan mode '{mode}' is not supported. Use auto, basic, pipeline, or repair.");

        var selection = await ClassifyPlanModeAsync(ctx, input, ct);
        if (string.Equals(selection.SelectedMode, "pipeline", StringComparison.OrdinalIgnoreCase))
        {
            var result = await ExecutePipelineAsync(ctx, input, ct);
            AttachPlanModeMetadata(result, "pipeline", selection);
            return result;
        }

        var autoResult = await ExecuteSinglePlanAsync(ctx, input, ct);
        AttachPlanModeMetadata(autoResult, "basic", selection);
        return autoResult;
    }

    private static void AppendMcpInputContractChecklist(StringBuilder sb)
    {
        sb.AppendLine("MCP input contract rules:");
        foreach (var rule in McpInputContractChecklist)
            sb.AppendLine(rule);
    }

    private static void AppendExpressionFunctionRules(StringBuilder sb)
    {
        var builtIns = string.Join(
            ", ",
            BuiltInFunctions.All.Keys
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(static name => $"`{name}`"));

        sb.AppendLine("Expression function rules:");
        sb.AppendLine($"- Built-in expression functions are exactly: {builtIns}.");
        sb.AppendLine("- Call only documented built-ins, or custom functions that you define in a document-level or workflow-level `functions:` block.");
        sb.AppendLine("- Every `functions.<name>(...)` call must refer to a built-in or to a matching `function <name>(...)` declaration in `functions:`.");
        sb.AppendLine("- Every generated custom `function name(...)` declaration MUST be immediately preceded by JSDoc (`/** ... */`).");
        sb.AppendLine("- That JSDoc MUST include one typed `@param {type} name - meaning` tag for every function parameter.");
        sb.AppendLine("- That JSDoc MUST include a typed `@returns {type} - meaning` tag for the function output, including `{void}` when it intentionally returns nothing.");
        sb.AppendLine("- Use semantic JSDoc types such as `{string}`, `{number}`, `{boolean}`, `{object}`, `{Array<string>}`, or a concise object shape when it clarifies the contract.");
        sb.AppendLine("- Do not invent helpers such as `functions.parseRepoUrl`, `functions.clampNumber`, `functions.take`, or `functions.filterUnprocessedIssues`; implement them in `functions:` or replace them with built-ins, structured_output normalization, or explicit workflow steps.");
    }

    private async Task<JsonNode?> ExecuteSinglePlanAsync(
        StepExecutionContext ctx,
        JsonObject input,
        CancellationToken ct,
        ITelemetrySpan? parentSpan = null)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "No LLM client configured");

        var generator = input["generator"] as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires 'generator'");

        var policy = input["policy"] as JsonObject;
        var limits = input["limits"] as JsonObject;
        var validate = input["validate"] as JsonObject;
        var onInvalid = input["on_invalid"] as JsonObject;

        var requestedModel = generator["model"]?.GetValue<string>();
        var requestedProvider = generator["provider"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        model ??= "gpt-4";
        var instruction = generator["instruction"]?.GetValue<string>() ?? "";
        var generatorContext = generator["context"]?.GetValue<string>() ?? "";
        var pipelineLeafName = generator["pipeline_leaf_name"]?.GetValue<string>();

        // Reasoning effort: workflow planning is reasoning-heavy, default to "medium".
        // Authors can override via `generator.reasoning: auto|minimal|low|medium|high|max`.
        var planReasoning = generator["reasoning"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(planReasoning))
            planReasoning = "medium";

        // Determine allowed step types for filtering DSL snippets
        HashSet<string>? allowedTypes = null;
        var constraintsSb = new StringBuilder();
        if (policy != null)
        {
            var allowed = policy["allowed_step_types"] as JsonArray;
            if (allowed != null)
            {
                allowedTypes = allowed.Select(a => a?.GetValue<string>() ?? "").ToHashSet();
                constraintsSb.AppendLine($"Allowed step types: {string.Join(", ", allowedTypes)}");
            }
            var denied = policy["denied_step_types"] as JsonArray;
            if (denied != null)
                constraintsSb.AppendLine($"Denied step types: {string.Join(", ", denied.Select(a => a?.GetValue<string>()))}");
            var allowRemote = policy["allow_remote_workflow_refs"]?.GetValue<bool>() ?? false;
            if (!allowRemote)
                constraintsSb.AppendLine("Remote workflow references (kind: url) are NOT allowed.");
        }
        if (limits != null)
        {
            var maxSteps = limits["max_steps_total"]?.GetValue<int>();
            if (maxSteps.HasValue)
                constraintsSb.AppendLine($"Maximum total steps: {maxSteps.Value}");
        }

        // Collect DSL snippets from all registered executors (filtered by policy)
        var snippets = ctx.Engine.Registry.GetDslSnippets(allowedTypes);
        var stepTypesDoc = string.Join("\n", snippets);
        var stepExceptionsDoc = BuildStepExceptionsDoc(ctx.Engine.Registry, allowedTypes);

        // ── MCP discovery + optional pre-filter ─────────────────────────
        // Check if pre-filtering is requested via generator.prefilter (default: true)
        var prefilterNode = generator["prefilter"];
        bool shouldPrefilter = prefilterNode == null
            || prefilterNode is JsonObject
            || (prefilterNode is JsonValue jv && (!jv.TryGetValue<bool>(out var bv) || bv));

        var prefilterModel = model;
        var prefilterProvider = provider;
        double? prefilterTemperature = null;
        if (shouldPrefilter && prefilterNode is JsonObject pfObj)
        {
            prefilterModel = pfObj["model"]?.GetValue<string>() ?? model;
            prefilterProvider = pfObj["provider"]?.GetValue<string>() ?? provider;
            prefilterTemperature = pfObj["temperature"]?.GetValue<double>();
        }

        var requiredMcpServerNames = ExtractRequiredMcpServerNames(
            instruction,
            generatorContext,
            ctx.Engine.McpClientFactory?.ServerMetadata);

        var generationAttributes = new List<KeyValuePair<string, object?>>
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gen_ai.request.background", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.background_requested", true)
        };
        if (!string.IsNullOrWhiteSpace(pipelineLeafName))
            generationAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", pipelineLeafName));

        using var generationSpan = parentSpan == null
            ? ctx.BeginTelemetrySpan("workflow.plan.generate", "generation", generationAttributes)
            : ctx.BeginTelemetrySpan(parentSpan, "workflow.plan.generate", "generation", generationAttributes);

        var candidateMcpServers = shouldPrefilter
            ? await PrefilterMcpServerMetadataAsync(
                llmClient, ctx.Engine.McpClientFactory, instruction, generatorContext,
                prefilterModel, prefilterProvider, prefilterTemperature, planReasoning, ctx, generationSpan.Span, ct)
            : null;

        candidateMcpServers = MergeRequiredMcpServerMetadata(
            candidateMcpServers,
            ctx.Engine.McpClientFactory?.ServerMetadata,
            requiredMcpServerNames,
            ctx);

        var validateDryRun = validate?["dry_run"]?.GetValue<bool>() ?? false;
        var validationDiscovered = validateDryRun
            ? await DiscoverMcpServersAsync(
                ctx.Engine.McpClientFactory, ctx.Engine.McpCache, ctx.Engine.Logger, ctx, candidateServers: null, generationSpan.Span, ct)
            : null;

        var discovered = SelectDiscoveredServers(validationDiscovered, candidateMcpServers)
            ?? await DiscoverMcpServersAsync(
            ctx.Engine.McpClientFactory, ctx.Engine.McpCache, ctx.Engine.Logger, ctx, candidateMcpServers, generationSpan.Span, ct);

        if (shouldPrefilter && discovered != null && discovered.Count > 0)
        {
            var prefilterSource = discovered;
            discovered = await PrefilterMcpServersAsync(
                llmClient, discovered, instruction, generatorContext,
                prefilterModel, prefilterProvider, prefilterTemperature, planReasoning, ctx, generationSpan.Span, ct);
            discovered = MergeRequiredMcpServerDiscovery(
                discovered,
                prefilterSource,
                requiredMcpServerNames,
                ctx);
        }

        validationDiscovered ??= discovered;

        var mcpServersDoc = discovered != null && discovered.Count > 0
            ? FormatMcpServersDoc(discovered)
            : null;

        // Build the base prompt with full DSL reference
        var basePrompt = new StringBuilder();
        basePrompt.AppendLine("You are a GnOuGo.Flow YAML workflow generator. Return ONLY valid YAML, no explanation or markdown fences.");
        basePrompt.AppendLine("Strict plan validation is mandatory: invalid YAML will be rejected, repaired automatically within the bounded attempt budget, and never returned as a successful plan.");
        basePrompt.AppendLine();
        AppendPromptSection(basePrompt, "dsl_reference", RemoveMarkdownFenceLines(DslReference.CommonReference));
        basePrompt.AppendLine();
        AppendPromptSection(basePrompt, "available_step_types", RemoveMarkdownFenceLines(stepTypesDoc));
        basePrompt.AppendLine();
        AppendPromptSectionStart(basePrompt, "required_root_yaml_shape");
        basePrompt.AppendLine("The generated YAML MUST include all required root keys exactly once: version, name, skill, workflows.");
        basePrompt.AppendLine("The generated YAML MUST include a top-level `skill` block for routing metadata.");
        basePrompt.AppendLine("Root key requirements:");
        basePrompt.AppendLine("- version: non-empty string");
        basePrompt.AppendLine("- name: non-empty string");
        basePrompt.AppendLine("- skill: required object with description, tags, inputs, and outputs for routing and argument extraction");
        basePrompt.AppendLine("- workflows: non-empty object");
        basePrompt.AppendLine("Each workflow entry under workflows MUST define a steps array.");
        basePrompt.AppendLine("If any required key is missing or has the wrong shape, the output is invalid.");
        basePrompt.AppendLine("Minimal valid skeleton:");
        basePrompt.AppendLine("version: \"1.0\"");
        basePrompt.AppendLine("name: \"generated-workflow\"");
        basePrompt.AppendLine("skill:");
        basePrompt.AppendLine("  description: \"Describe when this generated workflow should be used.\"");
        basePrompt.AppendLine("  tags: [generated]");
        basePrompt.AppendLine("  inputs: {}");
        basePrompt.AppendLine("  outputs: {}");
        basePrompt.AppendLine("workflows:");
        basePrompt.AppendLine("  main:");
        basePrompt.AppendLine("    steps: []");
        AppendPromptSectionEnd(basePrompt, "required_root_yaml_shape");

        basePrompt.AppendLine();
        AppendPromptSectionStart(basePrompt, "generation_validation_checklist");
        basePrompt.AppendLine("Before returning YAML, self-check these rules and fix the YAML silently:");
        basePrompt.AppendLine("- Use only exact step types listed in <available_step_types>. Do not invent aliases or legacy names.");
        basePrompt.AppendLine("- Every step has a unique non-empty `id` and a non-empty `type`.");
        basePrompt.AppendLine("- Put common step fields (`id`, `type`, `if`, `input`, `output`, `retry`, `on_error`) at the step level, not inside `input`.");
        basePrompt.AppendLine("- Put executor-specific arguments inside `input` only. For example `llm.call.input.prompt`, `mcp.call.input.server`, `template.render.input.template`.");
        basePrompt.AppendLine("- For non-trivial `set` steps that normalize or reshape data, declare step-level `output_schema` so later `data.steps.<id>.<field>` references have a real contract.");
        basePrompt.AppendLine("- When a `set` field has a closed object/array `output_schema` (`additionalProperties: false`), do not assign opaque custom-function objects directly. Project exactly the declared fields before returning or assigning the value.");
        basePrompt.AppendLine("- If a nullable derived value must feed a later non-null input, refine it first with `assert.non_null` and use the refined step output, or keep the strict call inside a guard/branch that proves the value exists.");
        basePrompt.AppendLine("- Container mappings must use their documented shape: `sequence`/`loop.*` use step-level `steps`; `parallel` uses step-level `branches[].steps`; `switch` uses step-level `cases[].steps` and optional step-level `default`.");
        basePrompt.AppendLine("- Do not reference future steps. Expressions may read `data.inputs.*` and outputs from earlier `data.steps.<id>.*` only.");
        basePrompt.AppendLine("- Do not reference `data.steps.<id>.*` produced only inside `switch` cases, `if`-guarded steps, or loop bodies from later steps unless you first map every possible path to a guaranteed value.");
        basePrompt.AppendLine("- Function arguments are evaluated before the function runs: `coalesce(data.steps.branch_a.value, data.steps.branch_b.value)` is invalid if either branch step may not exist.");
        basePrompt.AppendLine("- After a `switch`, prefer writing a common result object via the same workflow-level output alias in every case/default branch, or add one guaranteed normalization step that receives the whole switch/branch context and emits a stable schema.");
        basePrompt.AppendLine("- A `switch` step output is a path-dependent step snapshot, not a flattened object. Invalid: `data.steps.route.pr_url` when `pr_url` is produced by `set_pr_success` inside a case.");
        basePrompt.AppendLine("- Use documented output shapes exactly: `template.render` text is `data.steps.<id>.text`; `llm.call` text is `data.steps.<id>.text` and structured JSON is `data.steps.<id>.json`; `mcp.call` single-tool response is `data.steps.<id>.response`.");
        basePrompt.AppendLine("- Do not assume nested fields inside opaque MCP `response` unless the tool schema/description explicitly documents them; pass the whole response onward when uncertain.");
        basePrompt.AppendLine("- NEVER invent properties under `data.steps.<id>.response`. Access `response.<field>` only when an `output_schema` or `example_response` explicitly documents that field.");
        basePrompt.AppendLine("- If an MCP response is opaque, use `json(data.steps.<id>.response)` to pass the whole response to another step.");
        basePrompt.AppendLine("- If precise fields are needed from an opaque response, add an `llm.call` normalization step with `structured_output`, then read fields from `data.steps.<normalizer>.json`.");
        AppendMcpInputContractChecklist(basePrompt);
        AppendExpressionFunctionRules(basePrompt);
        basePrompt.AppendLine("- When a field expects a string containing JSON, use a YAML literal block (`|`) or single quotes; do not put unescaped JSON inside a double-quoted YAML string.");
        basePrompt.AppendLine("- Workflow `outputs` should use either the short expression form or the long form with `expr` and `type`. Do not map arbitrary objects there unless using nested expression properties intentionally.");
        basePrompt.AppendLine("- Every generated `skill.outputs.*` and `workflows.*.outputs.*` entry must be strongly typed. Never emit `type: any`, bare `type: object`, or bare `type: array`.");
        basePrompt.AppendLine("- Array outputs must declare `items`; if items are objects, `items.properties` must list the concrete fields. Object outputs must declare non-empty `properties`.");
        basePrompt.AppendLine("- Workflow output expressions must match the declared output contract type exactly. A string output must resolve to a string; a boolean output must resolve to a boolean.");
        basePrompt.AppendLine("- Comparison/predicate expressions such as `${a == b}`, `${a != b}`, `${contains(...)}`, and `${exists(...)}` return boolean. Use them only for boolean outputs or `if`/`switch.when` conditions.");
        basePrompt.AppendLine("- For string outputs such as classification/status/level/severity, return a string-valued field or quoted string literal. Invalid for a string output: `${data.steps.classify.json.classification == 'bug'}`. Valid: `${data.steps.classify.json.classification}`.");
        basePrompt.AppendLine("- If a string output must be derived from an MCP/LLM response, first normalize it with `llm.call` or `mcp.call` `structured_output`, then map `data.steps.<normalizer>.json.<field>` to the workflow output.");
        basePrompt.AppendLine("- For input/output object schemas, never duplicate the YAML key `required`. Use input-level `required: true|false` only as a boolean. Use `required_properties: [field_name]` for required object property names.");
        AppendPromptSectionEnd(basePrompt, "generation_validation_checklist");
        basePrompt.AppendLine();
        AppendWorkflowPlanGenerationGuardrails(basePrompt);
        basePrompt.AppendLine();
        AppendStructuredOutputStrictSchemaRules(basePrompt);

        basePrompt.AppendLine();
        AppendPromptSectionStart(basePrompt, "llm_model_parameters");
        basePrompt.AppendLine("The runtime resolves default provider/model values for LLM-capable steps when they are omitted.");
        basePrompt.AppendLine("The runtime also owns model metadata (token limits, pricing, and capabilities) and removes unsupported optional request parameters before provider calls.");
        basePrompt.AppendLine("When generating `llm.call` or LLM-assisted `mcp.call` steps:");
        basePrompt.AppendLine("- Prefer omitting provider/model when the runtime default should apply.");
        basePrompt.AppendLine("- Do NOT add `temperature` by habit. Include it only when the task explicitly needs a sampling override.");
        basePrompt.AppendLine("- Do NOT add `reasoning` by habit. Include it only when the task explicitly needs a reasoning-effort override.");
        basePrompt.AppendLine("- If a generated workflow includes unsupported optional LLM parameters, the runtime may omit them automatically based on model capabilities.");
        AppendPromptSectionEnd(basePrompt, "llm_model_parameters");


        if (mcpServersDoc != null)
        {
            basePrompt.AppendLine();
            AppendPromptSection(basePrompt, "available_mcp_servers", mcpServersDoc);

            basePrompt.AppendLine();
            AppendPromptSectionStart(basePrompt, "mcp_output_access");
            basePrompt.AppendLine("mcp.call single-tool output shape: `{ status: \"ok\"|\"error\", response: tool-specific JSON }`");
            basePrompt.AppendLine("Access status via `data.steps.<id>.status` and the full tool result via `data.steps.<id>.response`.");
            basePrompt.AppendLine("The `response` value is opaque, tool-specific JSON. Do NOT assume field names inside `response` unless the tool description explicitly documents them.");
            basePrompt.AppendLine("When passing the tool result to a subsequent step, prefer `data.steps.<id>.response` (the whole object) or `json(data.steps.<id>.response)` to serialize it.");
            basePrompt.AppendLine("For batch/auto-discover output: `{ status, results: [{ method, status, response }] }` — access via `data.steps.<id>.results`.");
            basePrompt.AppendLine("For LLM-assisted output: `{ status, selection_mode: \"llm\", text, tool_calls, results, json? }` — structured content is in `data.steps.<id>.json` when `structured_output` is used, or `data.steps.<id>.text` for free-form text.");
            AppendPromptSectionEnd(basePrompt, "mcp_output_access");
        }

        basePrompt.AppendLine();
        AppendPromptSectionStart(basePrompt, "error_handling_and_retries");
        basePrompt.AppendLine("Use `retry` only for transient errors that are explicitly marked retryable by the runtime.");
        basePrompt.AppendLine("Retries run before `on_error` is evaluated.");
        basePrompt.AppendLine("`on_error` is evaluated only after retries are exhausted, or immediately for non-retryable errors.");
        basePrompt.AppendLine("In the current runtime, `on_error` actions are `continue` or `stop`.");
        basePrompt.AppendLine("Inside `on_error.cases[].if`, the error context exposes `error.code`, `error.message`, `error.retryable`, `step.id`, and `step.type`.");
        basePrompt.AppendLine("Prefer `retry` for timeout/network/connectivity failures that may succeed later. Prefer `action: stop` for validation, policy, schema, or syntax problems that will not improve on retry.");
        basePrompt.AppendLine();
        basePrompt.AppendLine("Retry + fallback example for a transient LLM error, as YAML:");
        basePrompt.AppendLine("- id: summarize");
        basePrompt.AppendLine("  type: llm.call");
        basePrompt.AppendLine("  input:");
        basePrompt.AppendLine("    model: gpt-4o-mini");
        basePrompt.AppendLine("    prompt: \"Summarize: ${json(data.inputs)}\"");
        basePrompt.AppendLine("  retry:");
        basePrompt.AppendLine("    max: 3");
        basePrompt.AppendLine("    backoff_ms: 1000");
        basePrompt.AppendLine("    backoff_mult: 2");
        basePrompt.AppendLine("    jitter_ms: 100");
        basePrompt.AppendLine("  on_error:");
        basePrompt.AppendLine("    cases:");
        basePrompt.AppendLine("      - if: \"${error.code == \\\"LLM_TIMEOUT\\\" || error.code == \\\"LLM_NETWORK\\\"}\"");
        basePrompt.AppendLine("        action: continue");
        basePrompt.AppendLine("        set_output:");
        basePrompt.AppendLine("          text: \"Temporary LLM issue after retries\"");
        basePrompt.AppendLine("      - action: stop");
        basePrompt.AppendLine();
        basePrompt.AppendLine("Non-retryable validation example, as YAML:");
        basePrompt.AppendLine("on_error:");
        basePrompt.AppendLine("  cases:");
        basePrompt.AppendLine("    - if: \"${error.code == \\\"INPUT_VALIDATION\\\"}\"");
        basePrompt.AppendLine("      action: stop");
        basePrompt.AppendLine("    - if: \"${error.retryable}\"");
        basePrompt.AppendLine("      action: continue");
        basePrompt.AppendLine("      set_output:");
        basePrompt.AppendLine("        status: \"degraded\"");
        basePrompt.AppendLine("    - action: stop");
        AppendPromptSectionEnd(basePrompt, "error_handling_and_retries");
        basePrompt.AppendLine();
        AppendPromptSection(basePrompt, "step_exceptions_by_type", RemoveMarkdownFenceLines(stepExceptionsDoc));

        if (constraintsSb.Length > 0)
        {
            basePrompt.AppendLine();
            AppendPromptSection(basePrompt, "constraints", constraintsSb.ToString());
        }

        basePrompt.AppendLine();
        AppendUserTaskBlock(basePrompt, instruction, generatorContext);

        var maxAttempts = GetWorkflowPlanRepairMaxAttempts(onInvalid, validate);
        string? lastError = null;
        string? lastInvalidYaml = null;
        string? lastRepairContext = null;
        var forcedMcpServerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "openai");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);

        // ── Thinking: signal planning start ──
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Planning workflow with {model}…"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            string promptText;
            if (lastError == null)
            {
                promptText = basePrompt.ToString();
            }
            else
            {
                // ── Thinking: signal retry ──
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Plan attempt {attempt + 1}/{maxAttempts} — fixing: {(lastError.Length > 100 ? lastError[..100] + "…" : lastError)}"),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                promptText = BuildRepairPrompt(
                    instruction,
                    generatorContext,
                    lastInvalidYaml,
                    lastError,
                    lastRepairContext,
                    constraintsSb.ToString());
            }

            if (ctx.Limits.LogStepContent)
            {
                ctx.AddTelemetryEvent("gen_ai.content.prompt", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.prompt", promptText),
                    new KeyValuePair<string, object?>("prompt.role", "user"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1)
                });
            }

            LLMResponse response;
            generationSpan.SetAttribute("gnougo-flow.plan.attempt", attempt + 1);
            var llmCallAttributes = new List<KeyValuePair<string, object?>>
            {
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
                new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("gen_ai.request.background", true),
                new KeyValuePair<string, object?>("gnougo-flow.plan.background_requested", true),
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1)
            };
            if (!string.IsNullOrWhiteSpace(pipelineLeafName))
                llmCallAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", pipelineLeafName));

            using (var llmCallSpan = ctx.BeginTelemetrySpan(generationSpan.Span, "workflow.plan.generate.llm_call", "generation_llm", llmCallAttributes))
            {
                if (ctx.Limits.LogStepContent)
                    llmCallSpan.AddEvent("gen_ai.content.prompt", new[]
                    {
                        new KeyValuePair<string, object?>("gen_ai.prompt", promptText),
                        new KeyValuePair<string, object?>("prompt.role", "user"),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "generation")
                    });

                try
                {
                    ctx.Engine.Logger.LogInformation(
                        "workflow.plan generation request uses background mode. Provider={Provider}; Model={Model}; Attempt={Attempt}/{MaxAttempts}; Reasoning={Reasoning}",
                        provider ?? "default",
                        model,
                        attempt + 1,
                        maxAttempts,
                        planReasoning ?? "(provider default)");

                    response = await llmClient.CallAsync(new LLMRequest
                    {
                        Provider = provider,
                        Model = model,
                        Prompt = promptText,
                        Reasoning = planReasoning,
                        UseBackgroundMode = true,
                    }, ct);
                    generationSpan.SetAttribute("gen_ai.response.model", model);
                    generationSpan.SetAttribute("gen_ai.response.finish_reason", "stop");
                    AddUsageAttributes(generationSpan, response.Usage, model, provider);
                    if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
                    {
                        generationSpan.AddEvent("gen_ai.content.completion", new[]
                        {
                            new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                            new KeyValuePair<string, object?>("completion.role", "assistant"),
                            new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "generation")
                        });
                    }

                    llmCallSpan.SetAttribute("gen_ai.response.model", model);
                    llmCallSpan.SetAttribute("gen_ai.response.finish_reason", "stop");
                    AddUsageAttributes(llmCallSpan, response.Usage, model, provider);
                    llmCallSpan.SetAttribute("gnougo-flow.plan.generated_yaml_length", response.Text?.Length ?? 0);
                    if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
                    {
                        llmCallSpan.AddEvent("gen_ai.content.completion", new[]
                        {
                            new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                            new KeyValuePair<string, object?>("completion.role", "assistant"),
                            new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "generation")
                        });
                    }
                }
                catch (Exception ex)
                {
                    generationSpan.Fail(ex);
                    llmCallSpan.Fail(ex);
                    throw;
                }
            }

            ctx.SetTelemetryAttribute("gen_ai.response.model", model);
            ctx.SetTelemetryAttribute("gen_ai.response.finish_reason", "stop");
            SetStepUsageTelemetry(ctx, response.Usage, model, provider);

            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                ctx.AddTelemetryEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1)
                });
            }

            try
            {
                var yaml = StripMarkdownFences(response.Text ?? string.Empty);
                WorkflowDocument generatedDoc;
                var validationAttributes = new List<KeyValuePair<string, object?>>
                {
                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt + 1)
                };
                if (!string.IsNullOrWhiteSpace(pipelineLeafName))
                    validationAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", pipelineLeafName));

                using (var validationSpan = ctx.BeginTelemetrySpan(generationSpan.Span, "workflow.plan.validate", "validation", validationAttributes))
                {
                    try
                    {
                        validationSpan.SetAttribute("gnougo-flow.plan.yaml_length", yaml.Length);

                        // Parse + validate minimal required shape before policy/limits/compile checks.
                        generatedDoc = ParseAndValidateGeneratedWorkflow(yaml);
                        validationSpan.SetAttribute("gnougo-flow.plan.workflow_count", generatedDoc.Workflows.Count);

                        await RunStandardPlanValidationSequenceAsync(
                            generatedDoc,
                            policy,
                            limits,
                            validate,
                            validationDiscovered,
                            ctx,
                            validationSpan.Span,
                            ct,
                            validateGeneratedOutputSchemas: string.IsNullOrWhiteSpace(pipelineLeafName));
                    }
                    catch (Exception ex)
                    {
                        var enriched = AttachGeneratedYamlToPlanException(ex, yaml);
                        validationSpan.AddEvent(
                            "gnougo-flow.plan.validation.error",
                            BuildPlanErrorTelemetryAttributes(enriched, attempt + 1, "validation", pipelineLeafName));
                        validationSpan.Fail(enriched);
                        generationSpan.Fail(enriched);
                        throw enriched;
                    }
                }

                // Return the generated workflow as JSON
                var workflowInfo = new JsonObject
                {
                    ["version"] = generatedDoc.Version,
                    ["name"] = generatedDoc.Name
                };
                var wfNames = new JsonArray();
                foreach (var wfName in generatedDoc.Workflows.Keys)
                    wfNames.Add((JsonNode)JsonValue.Create(wfName)!);
                workflowInfo["workflows"] = wfNames;

                generationSpan.Complete();
                return new JsonObject
                {
                    ["workflow"] = workflowInfo,
                    ["yaml"] = yaml,
                    ["meta"] = new JsonObject { ["model"] = model, ["attempt"] = attempt + 1 },
                    ["diagnostics"] = new JsonArray()
                };
            }
            catch (Exception ex) when (attempt < maxAttempts - 1)
            {
                // Capture the error for injection into the next prompt
                ctx.Engine.Logger.LogWarning(ex, "workflow.plan: attempt {Attempt}/{MaxAttempts} failed, reprompting", attempt + 1, maxAttempts);
                var missingMcpServerName = TryExtractMissingMcpServerName(ex.Message);
                if (!string.IsNullOrWhiteSpace(missingMcpServerName))
                    forcedMcpServerNames.Add(missingMcpServerName);

                var repairDiscovered = forcedMcpServerNames.Count == 0
                    ? validationDiscovered
                    : MergeRequiredMcpServerDiscovery(
                        discovered ?? new List<McpServerDiscovery>(),
                        validationDiscovered,
                        forcedMcpServerNames,
                        ctx);

                lastError = BuildStructuredPlanError(ex, attempt + 1);
                lastInvalidYaml = StripMarkdownFences(response.Text ?? string.Empty);
                lastRepairContext = BuildMinimalRepairContext(
                    ctx.Engine.Registry,
                    allowedTypes,
                    lastInvalidYaml,
                    ex,
                    repairDiscovered);
            }
        }

        ctx.Engine.Logger.LogError("workflow.plan: failed to generate valid workflow after {MaxAttempts} attempts", maxAttempts);
        var finalException = new WorkflowRuntimeException(ErrorCodes.TemplatePlan,
            string.IsNullOrWhiteSpace(lastError)
                ? $"Failed to generate valid workflow after {maxAttempts} attempts"
                : $"Failed to generate valid workflow after {maxAttempts} attempts. Last strict validation error: {lastError}");
        generationSpan.Fail(finalException);
        throw finalException;
    }

    private static int GetWorkflowPlanRepairMaxAttempts(JsonObject? onInvalid, JsonObject? validate)
    {
        var configured = TryGetPositiveInteger(validate, "max_repair_attempts")
            ?? TryGetPositiveInteger(onInvalid, "max_attempts")
            ?? DefaultPlanRepairMaxAttempts;

        return Math.Max(1, configured);
    }

    private static int? TryGetPositiveInteger(JsonObject? obj, string propertyName)
    {
        if (obj == null || !obj.TryGetPropertyValue(propertyName, out var node) || node == null)
            return null;

        if (node is JsonValue value && value.TryGetValue<int>(out var parsed) && parsed > 0)
            return parsed;

        return null;
    }

    private static Exception AttachGeneratedYamlToPlanException(Exception ex, string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return ex;

        var details = new JsonObject
        {
            ["generated_yaml"] = yaml,
            ["invalid_yaml"] = yaml
        };

        if (ex is WorkflowRuntimeException workflowEx)
        {
            if (workflowEx.Details is JsonObject existingDetails)
            {
                foreach (var (key, value) in existingDetails)
                {
                    if (!details.ContainsKey(key))
                        details[key] = value?.DeepClone();
                }
            }
            else if (workflowEx.Details != null)
            {
                details["details"] = workflowEx.Details.DeepClone();
            }

            return new WorkflowRuntimeException(
                workflowEx.Code,
                workflowEx.Message,
                workflowEx.Retryable,
                workflowEx,
                details);
        }

        return new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            ex.Message,
            inner: ex,
            details: details);
    }
}
