using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private async Task<JsonNode?> ExecutePipelineAsync(StepExecutionContext ctx, JsonObject input, CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "No LLM client configured");

        var generator = input["generator"] as JsonObject ?? new JsonObject();

        var rawPrompt = input["raw_prompt"]?.GetValue<string>()
            ?? generator["raw_prompt"]?.GetValue<string>()
            ?? generator["instruction"]?.GetValue<string>()
            ?? "";
        if (string.IsNullOrWhiteSpace(rawPrompt))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan pipeline mode requires 'raw_prompt' or generator.instruction");

        NormalizePipelineMainPolicy(input, ctx);

        var requestedModel = generator["model"]?.GetValue<string>();
        var requestedProvider = generator["provider"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        model ??= "gpt-4";
        var reasoning = generator["reasoning"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(reasoning))
            reasoning = "medium";

        ctx.SetTelemetryAttribute("gnougo-flow.plan.mode", "pipeline");
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "openai");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Preparing workflow generation prompt through pipeline mode."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var normalizedMarkdown = await NormalizeUserPromptAsync(
            llmClient, rawPrompt, provider, model, reasoning, ctx, ct);
        var annotatedMarkdown = await MarkExtractableBlocksAsync(
            llmClient, normalizedMarkdown, provider, model, reasoning, ctx, ct);

        WorkflowPipelineExtraction extraction;
        using (var extractionSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.extract_subworkflow_specs", "extract_subworkflow_specs"))
        {
            try
            {
                extraction = ExtractSubworkflowSpecs(annotatedMarkdown);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.validation_error_count", extraction.ValidationErrors.Count);
            }
            catch (Exception ex)
            {
                extractionSpan.Fail(ex);
                throw;
            }
        }
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count);
        if (extraction.ValidationErrors.Count > 0)
        {
            var details = new JsonObject
            {
                ["validation"] = BuildValidationJson(extraction.ValidationErrors)
            };
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                "workflow.plan pipeline extraction failed: " + string.Join("; ", extraction.ValidationErrors),
                details: details);
        }

        GeneratedLeafWorkflow[] generatedLeaves;
        using (var generationSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.generate_subworkflows", "generate_subworkflows", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count)
        }))
        {
            try
            {
                var tasks = extraction.Subworkflows
                    .Select(spec => GenerateLeafWorkflowAsync(ctx, input, generator, spec, ct))
                    .ToArray();
                generatedLeaves = await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                generationSpan.Fail(ex);
                throw;
            }
        }

        string finalYaml;
        WorkflowDocument finalDoc;
        using (var mainSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.assemble_main_workflow", "assemble_main_workflow", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.subworkflow_count", generatedLeaves.Length)
        }))
        {
            try
            {
                mainSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly.kind", "llm_main_workflow");
                var configuredMainInputs = BuildConfiguredMainInputContract(input, generator);
                var generatedLeafInputs = BuildGeneratedMainInputContract(generatedLeaves);
                var baseMainAssemblyPrompt = BuildMainAssemblyPrompt(
                    input, generator, normalizedMarkdown, extraction, generatedLeaves, configuredMainInputs, generatedLeafInputs);
                var maxAssemblyAttempts = GetPipelineGenerationMaxAttempts(input);
                var validate = input["validate"] as JsonObject;
                var requiresMcpValidationContracts = (validate?["compile"]?.GetValue<bool>() ?? true)
                    || (validate?["dry_run"]?.GetValue<bool>() ?? false);
                var validationDiscovered = requiresMcpValidationContracts
                    ? await DiscoverMcpServersAsync(
                        ctx.Engine.McpClientFactory,
                        ctx.Engine.McpCache,
                        ctx.Engine.Logger,
                        ctx,
                        candidateServers: null,
                        mainSpan.Span,
                        ct)
                    : null;
                string? previousAssemblyResponse = null;
                string? previousAssemblyError = null;
                Exception? lastAssemblyException = null;
                string? assembledYaml = null;
                WorkflowDocument? assembledDocument = null;

                for (var attempt = 1; attempt <= maxAssemblyAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var mainAssemblyPrompt = previousAssemblyError == null
                        ? baseMainAssemblyPrompt
                        : BuildMainAssemblyRepairPrompt(baseMainAssemblyPrompt, previousAssemblyResponse, previousAssemblyError);

                    using var attemptSpan = ctx.BeginTelemetrySpan(
                        mainSpan.Span,
                        "workflow.plan.pipeline.assemble_main_workflow.attempt",
                        "assemble_main_workflow_attempt",
                        new[]
                        {
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAssemblyAttempts)
                        });

                    if (ctx.Limits.LogStepContent)
                    {
                        attemptSpan.AddEvent("gnougo-flow.plan.pipeline.assembly.input", new[]
                        {
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.main_workflow_prompt", extraction.MainWorkflowPrompt),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_workflows", string.Join(",", generatedLeaves.Select(static leaf => leaf.Name))),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.main_inputs", SerializeYamlMapping(configuredMainInputs)),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.assembly.note", "Configured metadata and inputs are authoritative. Otherwise the LLM infers the public contract while final YAML composition remains deterministic.")
                        });
                        mainSpan.AddEvent("gnougo-flow.plan.pipeline.assembly.input", new[]
                        {
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.main_workflow_prompt", extraction.MainWorkflowPrompt),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_workflows", string.Join(",", generatedLeaves.Select(static leaf => leaf.Name))),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.main_inputs", SerializeYamlMapping(configuredMainInputs)),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.assembly.note", "Configured metadata and inputs are authoritative. Otherwise the LLM infers the public contract while final YAML composition remains deterministic."),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
                        });
                        attemptSpan.AddEvent("gen_ai.content.prompt", new[]
                        {
                            new KeyValuePair<string, object?>("gen_ai.prompt", mainAssemblyPrompt),
                            new KeyValuePair<string, object?>("prompt.role", "user"),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "assemble_main_workflow"),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
                        });
                        mainSpan.AddEvent("gen_ai.content.prompt", new[]
                        {
                            new KeyValuePair<string, object?>("gen_ai.prompt", mainAssemblyPrompt),
                            new KeyValuePair<string, object?>("prompt.role", "user"),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "assemble_main_workflow"),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
                        });
                    }

                    try
                    {
                        var mainResponse = await llmClient.CallAsync(new LLMRequest
                        {
                            Provider = provider,
                            Model = model,
                            Prompt = mainAssemblyPrompt,
                            Reasoning = reasoning,
                            UseBackgroundMode = true
                        }, ct);
                        previousAssemblyResponse = mainResponse.Text;
                        attemptSpan.SetAttribute("gen_ai.operation.name", "chat");
                        attemptSpan.SetAttribute("gen_ai.system", provider ?? "openai");
                        attemptSpan.SetAttribute("gen_ai.request.model", model);
                        attemptSpan.SetAttribute("gen_ai.response.model", model);
                        attemptSpan.SetAttribute("gen_ai.response.finish_reason", "stop");
                        AddUsageAttributes(attemptSpan, mainResponse.Usage, model, provider);

                        if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(mainResponse.Text))
                        {
                            attemptSpan.AddEvent("gen_ai.content.completion", new[]
                            {
                                new KeyValuePair<string, object?>("gen_ai.completion", mainResponse.Text),
                                new KeyValuePair<string, object?>("completion.role", "assistant"),
                                new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "assemble_main_workflow"),
                                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
                            });
                            mainSpan.AddEvent("gen_ai.content.completion", new[]
                            {
                                new KeyValuePair<string, object?>("gen_ai.completion", mainResponse.Text),
                                new KeyValuePair<string, object?>("completion.role", "assistant"),
                                new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "assemble_main_workflow"),
                                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
                            });
                        }

                        var assembly = ParseGeneratedMainAssembly(mainResponse.Text ?? string.Empty);
                        var mainInputs = ResolveMainInputContract(configuredMainInputs, assembly, generatedLeafInputs);
                        ForceMainWorkflowInputs(assembly.MainWorkflowNode, mainInputs);
                        EnsureMainWorkflowOutputs(assembly.MainWorkflowNode, extraction.Subworkflows);
                        ValidateDeclaredMainInputReferences(assembly.MainWorkflowNode, mainInputs);

                        assembledYaml = ComposePipelineWorkflowYaml(input, generator, extraction, generatedLeaves, assembly, mainInputs);
                        using (var validationSpan = ctx.BeginTelemetrySpan(
                            attemptSpan.Span,
                            "workflow.plan.validate",
                            "validation",
                            new[]
                            {
                                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "assemble_main_workflow"),
                                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.final_validation", true)
                            }))
                        {
                            try
                            {
                                validationSpan.SetAttribute("gnougo-flow.plan.yaml_length", assembledYaml.Length);
                                assembledDocument = ParseAndValidateGeneratedWorkflow(assembledYaml);
                                validationSpan.SetAttribute("gnougo-flow.plan.workflow_count", assembledDocument.Workflows.Count);
                                EnforcePipelineWorkflowHierarchy(
                                    assembledDocument,
                                    generatedLeaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal));
                                await RunStandardPlanValidationSequenceAsync(
                                    assembledDocument,
                                    input["policy"] as JsonObject,
                                    input["limits"] as JsonObject,
                                    validate,
                                    validationDiscovered,
                                    ctx,
                                    validationSpan.Span,
                                    ct);
                            }
                            catch (Exception ex)
                            {
                                var enriched = AttachGeneratedYamlToPlanException(ex, assembledYaml);
                                validationSpan.AddEvent(
                                    "gnougo-flow.plan.validation.error",
                                    BuildPlanErrorTelemetryAttributes(enriched, attempt, "validation"));
                                validationSpan.Fail(enriched);
                                throw enriched;
                            }
                        }
                        attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "succeeded");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (attempt < maxAssemblyAttempts)
                    {
                        lastAssemblyException = ex;
                        previousAssemblyError = BuildStructuredPlanError(ex, attempt);
                        attemptSpan.AddEvent(
                            "gnougo-flow.plan.pipeline.main_assembly.error",
                            BuildPlanErrorTelemetryAttributes(ex, attempt, "assemble_main_workflow"));
                        attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "retrying");
                        attemptSpan.Fail(ex);
                        ctx.Engine.Logger.LogWarning(
                            ex,
                            "workflow.plan pipeline main assembly attempt {Attempt}/{MaxAttempts} failed, reprompting",
                            attempt,
                            maxAssemblyAttempts);
                        ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.main_assembly_retry", new[]
                        {
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAssemblyAttempts),
                            new KeyValuePair<string, object?>("error.type", ex.GetType().Name),
                            new KeyValuePair<string, object?>("error.message", ex.Message)
                        });
                    }
                    catch (Exception ex)
                    {
                        lastAssemblyException = ex;
                        attemptSpan.AddEvent(
                            "gnougo-flow.plan.pipeline.main_assembly.error",
                            BuildPlanErrorTelemetryAttributes(ex, attempt, "assemble_main_workflow"));
                        attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "failed");
                        attemptSpan.Fail(ex);
                        break;
                    }
                }

                if (assembledYaml == null || assembledDocument == null)
                {
                    throw new WorkflowRuntimeException(
                        ErrorCodes.TemplatePlan,
                        $"Pipeline main workflow assembly failed after {maxAssemblyAttempts} attempt(s): {lastAssemblyException?.Message ?? "unknown error"}",
                        inner: lastAssemblyException);
                }

                finalYaml = assembledYaml;
                finalDoc = assembledDocument;
                mainSpan.SetAttribute("gnougo-flow.plan.yaml_length", finalYaml.Length);
                mainSpan.SetAttribute("gnougo-flow.plan.workflow_count", finalDoc.Workflows.Count);

                if (ctx.Limits.LogStepContent)
                {
                    mainSpan.AddEvent("gnougo-flow.plan.pipeline.assembly.output", new[]
                    {
                        new KeyValuePair<string, object?>("gnougo-flow.plan.yaml", finalYaml),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.workflow_count", finalDoc.Workflows.Count)
                    });
                }
            }
            catch (Exception ex)
            {
                mainSpan.Fail(ex);
                throw;
            }
        }

        var workflowInfo = new JsonObject
        {
            ["version"] = finalDoc.Version,
            ["name"] = finalDoc.Name
        };
        var wfNames = new JsonArray();
        foreach (var wfName in finalDoc.Workflows.Keys)
            wfNames.Add((JsonNode)JsonValue.Create(wfName)!);
        workflowInfo["workflows"] = wfNames;

        return new JsonObject
        {
            ["workflow"] = workflowInfo,
            ["yaml"] = finalYaml,
            ["meta"] = new JsonObject
            {
                ["model"] = model,
                ["mode"] = "pipeline",
                ["leaf_subworkflow_count"] = generatedLeaves.Length
            },
            ["diagnostics"] = new JsonArray(),
            ["pipeline"] = new JsonObject
            {
                ["normalized_markdown"] = normalizedMarkdown,
                ["annotated_markdown"] = annotatedMarkdown,
                ["specs"] = BuildExtractionJson(extraction)
            }
        };
    }

    private static async Task<string> NormalizeUserPromptAsync(
        ILLMClient llmClient,
        string rawPrompt,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var prompt = $$"""
            You are preparing a raw user automation prompt for GnOuGo workflow generation.
            Return ONLY clean Markdown. Do not wrap the result in code fences.

            Behavior:
            - Correct spelling and grammar.
            - Rewrite the raw prompt as clean Markdown.
            - Preserve the exact business meaning.
            - Do not invent requirements.
            - Do not remove requirements.
            - Do not change the user intent.
            - Keep all important business rules.
            - Keep input parameters, defaults, conditions, loops, security rules, reporting rules, and cleanup rules.
            - Make the result easier to read and easier to transform into workflows.

            <raw_prompt>
            {{rawPrompt}}
            </raw_prompt>
            """;

        return await ExecutePipelineLlmTextPhaseAsync(
            llmClient, "normalize_user_prompt", prompt, provider, model, reasoning, ctx, ct);
    }

    private static async Task<string> MarkExtractableBlocksAsync(
        ILLMClient llmClient,
        string normalizedMarkdown,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var prompt = $$"""
            You annotate normalized automation Markdown for GnOuGo workflow generation.
            Return ONLY annotated Markdown. Do not wrap the result in code fences.

            Identify only the parts that contain significant algorithmic logic and wrap them in exactly this block syntax:

            :::subworkflow name="snake_case_name"
            goal: Short goal.
            inputs:
              input_name: type
            outputs:
              output_name: type
            extract_reason: Why this deserves a sub-workflow.
            content:
              Markdown description of the logic to implement.
            :::

            A part is algorithmic if it contains:
            - a loop;
            - a conditional decision;
            - a multi-step sequence with state;
            - tool orchestration;
            - retry or error handling;
            - branching logic;
            - file or report generation;
            - cleanup logic;
            - a reusable technical operation.

            Do not extract:
            - simple one-line or few actions;
            - global style rules;
            - constants;
            - footer text;
            - wording rules;
            - tiny isolated actions that do not deserve a workflow.

            Keep extracted blocks focused:
            - Do not create one large block that mixes several responsibilities.
            - Avoid blocks with high cyclomatic complexity: too many branches, nested conditionals, nested loops, retry paths, cleanup paths, or state transitions.
            - When one algorithmic section has several independent decision paths or phases, split it into multiple self-contained leaf subworkflow blocks.
            - Prefer cohesive blocks that a workflow generator can implement without needing to reason about unrelated branches.
            - Do not over-split into trivial one-line operations; split only when the reduced complexity improves workflow generation quality.

            Rules for subworkflow blocks:
            - The name must use snake_case.
            - Each block must describe exactly one responsibility.
            - Each block must be self-contained.
            - Each block must be detailed enough to generate a workflow later.
            - Each block must be a leaf workflow.
            - The block content must not mention calling another subworkflow.
            - The block content must not contain another :::subworkflow block.
            - Inputs and outputs must be explicit and typed.
            - Keep global rules outside subworkflow blocks when they apply to the whole automation.

            At the end of the Markdown, add:

            ## Main workflow orchestration

            In that section, explain how the main workflow calls the leaf subworkflows in order.
            The architecture must have only one hierarchy level:
            - Only the main workflow can call subworkflows.
            - Every subworkflow is a leaf workflow.
            - A subworkflow must never call another subworkflow.
            - A subworkflow must never depend on another subworkflow.
            - The final YAML will contain the main workflow and all leaf subworkflows in the same local YAML file.
            - The main workflow calls leaf workflows with local workflow.call.

            <normalized_markdown>
            {{normalizedMarkdown}}
            </normalized_markdown>
            """;

        return await ExecutePipelineLlmTextPhaseAsync(
            llmClient, "mark_extractable_blocks", prompt, provider, model, reasoning, ctx, ct);
    }

    private static async Task<string> ExecutePipelineLlmTextPhaseAsync(
        ILLMClient llmClient,
        string phase,
        string prompt,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        using var span = ctx.BeginTelemetrySpan($"workflow.plan.pipeline.{phase}", phase, new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gen_ai.request.background", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.background_requested", true)
        });

        if (ctx.Limits.LogStepContent)
        {
            span.AddEvent("gen_ai.content.prompt", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase)
            });
        }

        try
        {
            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                UseBackgroundMode = true
            }, ct);

            span.SetAttribute("gen_ai.response.model", model);
            span.SetAttribute("gen_ai.response.finish_reason", "stop");
            AddUsageAttributes(span, response.Usage, model, provider);

            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                span.AddEvent("gen_ai.content.completion", new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase)
                });
            }

            var text = StripMarkdownFences(response.Text).Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"workflow.plan pipeline phase '{phase}' returned empty text.");

            return text;
        }
        catch (Exception ex)
        {
            span.Fail(ex);
            throw;
        }
    }

    private static WorkflowPipelineExtraction ExtractSubworkflowSpecs(string annotatedMarkdown)
    {
        var normalized = annotatedMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var matches = SubworkflowBlockRegex().Matches(normalized);
        var specs = new List<WorkflowPipelineSubworkflowSpec>();
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        var markerCount = SubworkflowMarkerRegex().Matches(normalized).Count;
        if (markerCount != matches.Count)
            errors.Add("Nested or malformed :::subworkflow block found.");

        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value.Trim();
            var body = match.Groups["body"].Value;
            if (!SnakeCaseNameRegex().IsMatch(name))
                errors.Add($"Subworkflow name '{name}' must use snake_case.");
            if (!names.Add(name))
                errors.Add($"Duplicate subworkflow name '{name}'.");

            var parsed = ParseSubworkflowBlock(name, body, errors);
            specs.Add(parsed);
        }

        if (!normalized.Contains("## Main workflow orchestration", StringComparison.OrdinalIgnoreCase))
            errors.Add("Annotated markdown must include a '## Main workflow orchestration' section.");

        var mainWorkflowPrompt = ExtractMainWorkflowPrompt(normalized, specs);
        return new WorkflowPipelineExtraction(specs, mainWorkflowPrompt, errors);
    }

    private static WorkflowPipelineSubworkflowSpec ParseSubworkflowBlock(string name, string body, List<string> errors)
    {
        if (SubworkflowMarkerRegex().IsMatch(body))
            errors.Add($"Subworkflow '{name}' contains a nested :::subworkflow block.");

        var goal = "";
        var extractReason = "";
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var content = new StringBuilder();
        var section = "";

        foreach (var rawLine in body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("goal:", StringComparison.Ordinal))
            {
                goal = trimmed["goal:".Length..].Trim();
                section = "";
                continue;
            }

            if (trimmed == "inputs:")
            {
                section = "inputs";
                continue;
            }

            if (trimmed == "outputs:")
            {
                section = "outputs";
                continue;
            }

            if (trimmed.StartsWith("extract_reason:", StringComparison.Ordinal))
            {
                extractReason = trimmed["extract_reason:".Length..].Trim();
                section = "";
                continue;
            }

            if (trimmed.StartsWith("content:", StringComparison.Ordinal))
            {
                section = "content";
                var inlineContent = trimmed["content:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(inlineContent))
                    content.AppendLine(inlineContent);
                continue;
            }

            if (section == "inputs" || section == "outputs")
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                var separatorIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (separatorIndex <= 0)
                {
                    errors.Add($"Subworkflow '{name}' has an invalid {section} line: '{trimmed}'.");
                    continue;
                }

                var key = trimmed[..separatorIndex].Trim();
                var type = trimmed[(separatorIndex + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(type))
                {
                    errors.Add($"Subworkflow '{name}' has an untyped {section} entry: '{trimmed}'.");
                    continue;
                }

                if (!IdentifierRegex().IsMatch(key))
                    errors.Add($"Subworkflow '{name}' {section} entry '{key}' must be an identifier.");

                if (section == "inputs")
                    inputs[key] = NormalizeWorkflowSchemaType(type);
                else
                    outputs[key] = NormalizeWorkflowSchemaType(type);
                continue;
            }

            if (section == "content")
                content.AppendLine(RemoveSubworkflowContentIndent(rawLine));
        }

        var contentText = content.ToString().Trim();
        if (string.IsNullOrWhiteSpace(goal))
            errors.Add($"Subworkflow '{name}' is missing goal.");
        if (string.IsNullOrWhiteSpace(extractReason))
            errors.Add($"Subworkflow '{name}' is missing extract_reason.");
        if (string.IsNullOrWhiteSpace(contentText))
            errors.Add($"Subworkflow '{name}' is missing content.");
        if (SubworkflowCallMentionRegex().IsMatch(contentText))
            errors.Add($"Subworkflow '{name}' appears to call another subworkflow.");

        return new WorkflowPipelineSubworkflowSpec(
            name,
            goal,
            inputs,
            outputs,
            extractReason,
            contentText,
            BuildSubworkflowGenerationPrompt(name, goal, inputs, outputs, contentText));
    }

    private static string ExtractMainWorkflowPrompt(string annotatedMarkdown, IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var marker = MainWorkflowOrchestrationRegex().Match(annotatedMarkdown);
        if (marker.Success)
            return annotatedMarkdown[marker.Index..].Trim();

        var order = specs.Count == 0
            ? "No leaf subworkflows were extracted."
            : string.Join(", ", specs.Select(static spec => spec.Name));
        return "Build a main workflow that calls these leaf subworkflows in order with local workflow.call: " + order;
    }

    private static string BuildSubworkflowGenerationPrompt(
        string name,
        string goal,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> outputs,
        string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate exactly one leaf GnOuGo workflow named `{name}`.");
        sb.AppendLine($"Goal: {goal}");
        sb.AppendLine();
        sb.AppendLine("Leaf workflow constraints:");
        sb.AppendLine("- Generate a complete YAML document with version, name, skill, and workflows.");
        sb.AppendLine($"- The document must contain exactly one workflow, preferably named `{name}`.");
        sb.AppendLine("- The workflow must be a leaf workflow.");
        sb.AppendLine("- Do not use workflow.call.");
        sb.AppendLine("- Do not use workflow.plan.");
        sb.AppendLine("- Do not depend on another subworkflow.");
        sb.AppendLine("- Treat the declared input/output contract as a draft when MCP tools require additional arguments.");
        AppendMcpInputContractChecklist(sb);
        sb.AppendLine("- Workflow outputs must match their declared contract type exactly. A string output must resolve to a string; a boolean output must resolve to a boolean.");
        sb.AppendLine("- Comparison/predicate expressions such as `${a == b}`, `${a != b}`, `${contains(...)}`, and `${exists(...)}` return boolean. Use them only for boolean outputs or `if`/`switch.when` conditions.");
        sb.AppendLine("- For string outputs such as classification/status/level/severity, return a string-valued field or quoted string literal. Invalid for a string output: `${data.steps.classify.json.classification == 'bug'}`. Valid: `${data.steps.classify.json.classification}`.");
        sb.AppendLine("- If a string output must be derived from an MCP/LLM response, first normalize it with `llm.call` or `mcp.call` `structured_output`, then map `data.steps.<normalizer>.json.<field>` to the workflow output.");
        sb.AppendLine("- If a step has an `if`, later unconditional steps must not reference that step directly. Either give the later step the same guard or create guaranteed branch outputs/default values first.");
        sb.AppendLine("- Function arguments are evaluated before the function runs. Do not hide unavailable step references inside `coalesce`, ternaries, or helper calls.");
        sb.AppendLine("- For MCP schemas, required numeric/integer/boolean request fields must be literal YAML scalars when the schema or validator requires explicit values; do not use expressions, casts, empty strings, or `data.env.*` fallbacks.");
        sb.AppendLine("- Any schema with `type: object` MUST be strongly typed with a non-empty `properties` mapping. Never generate a bare `type: object` input, output, item, or nested property.");
        sb.AppendLine("- Never duplicate the YAML key `required` in an object schema. Use `required: true|false` only for input-level requiredness, and use `required_properties: [field_name]` for required object property names.");
        sb.AppendLine();
        AppendContractSection(sb, "Inputs", inputs);
        AppendContractSection(sb, "Outputs", outputs);
        sb.AppendLine();
        sb.AppendLine("Content to implement:");
        sb.AppendLine(content);
        return sb.ToString().TrimEnd();
    }

    private static void AppendContractSection(StringBuilder sb, string title, IReadOnlyDictionary<string, string> contract)
    {
        sb.AppendLine($"{title}:");
        if (contract.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var (name, type) in contract)
            sb.AppendLine($"- {name}: {type}");
    }

    private static JsonObject BuildLeafPlanInput(
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineSubworkflowSpec spec,
        string? previousError,
        string? previousYaml,
        string? previousPrompt,
        string? previousRepairContext)
    {
        var leafGenerator = generator.DeepClone() as JsonObject ?? new JsonObject();
        leafGenerator.Remove("mode");
        leafGenerator.Remove("raw_prompt");
        leafGenerator["instruction"] = string.IsNullOrWhiteSpace(previousError)
            ? spec.GenerationPrompt
            : BuildLeafRepairPrompt(spec.GenerationPrompt, previousPrompt, previousYaml, previousError, previousRepairContext);
        leafGenerator["context"] = "";
        leafGenerator["pipeline_leaf_name"] = spec.Name;

        var leafInput = new JsonObject
        {
            ["generator"] = leafGenerator,
            ["policy"] = BuildLeafPolicy(pipelineInput["policy"] as JsonObject)
        };

        if (pipelineInput["limits"] is JsonObject limits)
            leafInput["limits"] = limits.DeepClone();
        var leafValidate = pipelineInput["validate"]?.DeepClone() as JsonObject ?? new JsonObject();
        leafValidate["compile"] = true;
        leafInput["validate"] = leafValidate;
        leafInput["on_invalid"] = new JsonObject { ["action"] = "fail", ["max_attempts"] = 1 };

        return leafInput;
    }

    private async Task<GeneratedLeafWorkflow> GenerateLeafWorkflowAsync(
        StepExecutionContext parentCtx,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineSubworkflowSpec spec,
        CancellationToken ct)
    {
        var maxAttempts = GetPipelineGenerationMaxAttempts(pipelineInput);
        Exception? lastException = null;
        string? previousError = null;
        string? previousYaml = null;
        string? previousPrompt = null;
        string? previousRepairContext = null;
        var previousErrors = new List<string>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var leafAttemptSpan = parentCtx.BeginTelemetrySpan("workflow.plan.pipeline.generate_leaf", "generate_leaf", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", spec.Name),
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAttempts)
            });

            try
            {
                var leafInput = BuildLeafPlanInput(pipelineInput, generator, spec, previousError, previousYaml, previousPrompt, previousRepairContext);
                var leafPrompt = ((leafInput["generator"] as JsonObject)?["instruction"] as JsonValue)?.GetValue<string>();
                var leafCtx = new StepExecutionContext
                {
                    Step = parentCtx.Step,
                    Data = parentCtx.Data,
                    Engine = parentCtx.Engine,
                    Limits = parentCtx.Limits,
                    CallDepth = parentCtx.CallDepth,
                    CallStack = new HashSet<string>(parentCtx.CallStack),
                    TelemetrySpan = parentCtx.TelemetrySpan
                };

                var result = await ExecuteSinglePlanAsync(leafCtx, leafInput, ct, leafAttemptSpan.Span) as JsonObject
                    ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' generation did not return an object.");
                var yaml = result["yaml"]?.GetValue<string>()
                    ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' generation did not return YAML.");

                leafAttemptSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_status", "succeeded");
                try
                {
                    return PrepareGeneratedLeaf(spec, yaml);
                }
                catch
                {
                    previousYaml = yaml;
                    previousPrompt = leafPrompt;
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                previousPrompt ??= spec.GenerationPrompt;
                previousYaml ??= TryExtractGeneratedYamlFromException(ex);
                previousError = FormatLeafGenerationError(spec.Name, attempt, ex);
                previousErrors.Add(previousError);
                if (previousErrors.Count > 8)
                    previousErrors.RemoveAt(0);
                previousRepairContext = await BuildPipelineLeafRepairContextAsync(
                    parentCtx,
                    pipelineInput,
                    previousYaml,
                    ex,
                    leafAttemptSpan.Span,
                    ct);
                previousRepairContext = MergeLeafCumulativeRepairContext(previousErrors, previousRepairContext);
                leafAttemptSpan.AddEvent(
                    "gnougo-flow.plan.pipeline.leaf_generation.error",
                    BuildPlanErrorTelemetryAttributes(ex, attempt, "generate_leaf", spec.Name));
                leafAttemptSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_status", "retrying");
                leafAttemptSpan.Fail(ex);
                parentCtx.AddTelemetryEvent("gnougo-flow.plan.pipeline.leaf_retry", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", spec.Name),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().Name),
                    new KeyValuePair<string, object?>("error.message", ex.Message)
                });
            }
            catch (Exception ex)
            {
                lastException = ex;
                leafAttemptSpan.AddEvent(
                    "gnougo-flow.plan.pipeline.leaf_generation.error",
                    BuildPlanErrorTelemetryAttributes(ex, attempt, "generate_leaf", spec.Name));
                leafAttemptSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_status", "failed");
                leafAttemptSpan.Fail(ex);
                break;
            }
        }

        if (lastException is WorkflowRuntimeException workflowEx)
        {
            throw new WorkflowRuntimeException(
                workflowEx.Code,
                $"Leaf workflow '{spec.Name}' failed after {maxAttempts} generation attempt(s): {workflowEx.Message}",
                workflowEx.Retryable,
                workflowEx,
                workflowEx.Details?.DeepClone());
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' failed after {maxAttempts} generation attempt(s): {lastException?.Message ?? "unknown error"}",
            inner: lastException);
    }

    private static int GetPipelineGenerationMaxAttempts(JsonObject pipelineInput)
    {
        var configured = (pipelineInput["on_invalid"] as JsonObject)?["max_attempts"]?.GetValue<int>() ?? 3;
        return Math.Max(1, configured);
    }

    private static string FormatLeafGenerationError(string leafName, int attempt, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Leaf workflow: {leafName}");
        sb.AppendLine($"Failed attempt: {attempt}");
        sb.AppendLine($"Error type: {ex.GetType().Name}");
        if (ex is WorkflowRuntimeException workflowEx)
            sb.AppendLine($"Error code: {workflowEx.Code}");
        sb.AppendLine($"Structured error: {BuildStructuredPlanError(ex, attempt)}");
        sb.AppendLine("Error message:");
        sb.AppendLine(ex.Message);
        return sb.ToString().TrimEnd();
    }

    private static string MergeLeafCumulativeRepairContext(
        IReadOnlyList<string> previousErrors,
        string? latestRepairContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cumulative leaf retry requirements:");
        sb.AppendLine("- Preserve all fixes made for earlier validation failures; do not regress one MCP request or output while fixing another.");
        sb.AppendLine("- Re-check every mcp.call in the leaf against its discovered input_schema, not only the step named in the latest error.");
        sb.AppendLine("- If a required MCP request field is numeric/integer/boolean, emit an explicit YAML scalar of that type when the validator requires it.");
        sb.AppendLine("- Never satisfy missing MCP arguments with `data.env.*`, empty strings, fake values, casts, or string-to-number conversions.");
        sb.AppendLine("- Do not reference an `if`-guarded step from an unconditional later step unless a guaranteed value has first been produced on every path.");
        sb.AppendLine("- Workflow outputs must resolve to their declared type on every path.");

        if (previousErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("All previous failed attempts for this leaf:");
            for (var i = 0; i < previousErrors.Count; i++)
            {
                sb.AppendLine($"<leaf_failure_{i + 1}>");
                sb.AppendLine(previousErrors[i]);
                sb.AppendLine($"</leaf_failure_{i + 1}>");
            }
        }

        if (!string.IsNullOrWhiteSpace(latestRepairContext))
        {
            sb.AppendLine();
            sb.AppendLine(latestRepairContext.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    private static string? TryExtractGeneratedYamlFromException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is not WorkflowRuntimeException workflowEx || workflowEx.Details is not JsonObject details)
                continue;

            var yaml = GetStringProperty(details, "generated_yaml")
                ?? GetStringProperty(details, "invalid_yaml")
                ?? GetStringProperty(details, "yaml");
            if (!string.IsNullOrWhiteSpace(yaml))
                return yaml;
        }

        return null;
    }

    private static string BuildLeafRepairPrompt(
        string generationPrompt,
        string? previousPrompt,
        string? previousYaml,
        string previousError,
        string? additionalRepairContext)
    {
        var repairContext = new StringBuilder();
        repairContext.AppendLine("Previous generated YAML for this leaf workflow failed validation.");
        repairContext.AppendLine("Regenerate only this leaf workflow and fix the YAML below.");

        if (!string.IsNullOrWhiteSpace(previousPrompt))
        {
            repairContext.AppendLine();
            repairContext.AppendLine("The previous prompt sent for this leaf attempt is included below so you can preserve the same task and constraints.");
            repairContext.AppendLine("<previous_prompt>");
            repairContext.AppendLine(previousPrompt.Trim());
            repairContext.AppendLine("</previous_prompt>");
        }

        if (!string.IsNullOrWhiteSpace(additionalRepairContext))
        {
            repairContext.AppendLine();
            repairContext.AppendLine("Additional validation repair context:");
            repairContext.AppendLine(additionalRepairContext.Trim());
        }

        return BuildRepairPrompt(
            generationPrompt,
            context: null,
            invalidYaml: previousYaml,
            structuredError: previousError,
            repairContext: repairContext.ToString(),
            constraints: "This is a pipeline leaf repair. Generate exactly one leaf workflow. Do not use workflow.call or workflow.plan.");
    }

    private static async Task<string?> BuildPipelineLeafRepairContextAsync(
        StepExecutionContext parentCtx,
        JsonObject pipelineInput,
        string? previousYaml,
        Exception exception,
        ITelemetrySpan? parentSpan,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previousYaml))
            return null;

        try
        {
            var leafPolicy = BuildLeafPolicy(pipelineInput["policy"] as JsonObject);
            var allowedTypes = ExtractAllowedStepTypes(leafPolicy);
            var discovered = previousYaml.Contains("mcp.call", StringComparison.Ordinal)
                ? await DiscoverMcpServersAsync(
                    parentCtx.Engine.McpClientFactory,
                    parentCtx.Engine.McpCache,
                    parentCtx.Engine.Logger,
                    parentCtx,
                    candidateServers: null,
                    parentSpan,
                    ct)
                : null;

            return BuildMinimalRepairContext(
                parentCtx.Engine.Registry,
                allowedTypes,
                previousYaml,
                exception,
                discovered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            parentCtx.Engine.Logger.LogDebug(ex, "workflow.plan pipeline: failed to build leaf repair context");
            return null;
        }
    }

    private static HashSet<string>? ExtractAllowedStepTypes(JsonObject policy)
    {
        if (policy["allowed_step_types"] is not JsonArray allowed)
            return null;

        return allowed
            .Select(static node => node?.GetValue<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void NormalizePipelineMainPolicy(JsonObject pipelineInput, StepExecutionContext ctx)
    {
        var policy = pipelineInput["policy"] as JsonObject;
        if (policy == null)
        {
            policy = new JsonObject();
            pipelineInput["policy"] = policy;
        }

        if (policy["denied_step_types"] is JsonArray denied
            && RemoveStepType(denied, "workflow.call"))
        {
            ctx.Engine.Logger.LogWarning(
                "workflow.plan pipeline mode requires workflow.call in the generated main workflow; removing workflow.call from denied_step_types for the pipeline parent workflow.");
            ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.policy.warning", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.policy.change", "removed_denied_step_type"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.step_type", "workflow.call"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.reason", "pipeline main workflow calls local leaf workflows")
            });
        }

        if (policy["allowed_step_types"] is JsonArray allowed
            && !ContainsStepType(allowed, "workflow.call"))
        {
            allowed.Add((JsonNode)JsonValue.Create("workflow.call")!);
            ctx.Engine.Logger.LogWarning(
                "workflow.plan pipeline mode requires workflow.call in the generated main workflow; adding workflow.call to allowed_step_types for the pipeline parent workflow.");
            ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.policy.warning", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.policy.change", "added_allowed_step_type"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.step_type", "workflow.call"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.reason", "pipeline main workflow calls local leaf workflows")
            });
        }
    }

    private static bool ContainsStepType(JsonArray array, string stepType)
        => array.Any(node => string.Equals(node?.GetValue<string>(), stepType, StringComparison.Ordinal));

    private static bool RemoveStepType(JsonArray array, string stepType)
    {
        var removed = false;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(array[i]?.GetValue<string>(), stepType, StringComparison.Ordinal))
                continue;

            array.RemoveAt(i);
            removed = true;
        }

        return removed;
    }

    private static JsonObject BuildLeafPolicy(JsonObject? sourcePolicy)
    {
        var policy = sourcePolicy?.DeepClone() as JsonObject ?? new JsonObject();

        if (policy["allowed_step_types"] is JsonArray allowed)
        {
            for (var i = allowed.Count - 1; i >= 0; i--)
            {
                var value = allowed[i]?.GetValue<string>();
                if (string.Equals(value, "workflow.call", StringComparison.Ordinal)
                    || string.Equals(value, "workflow.plan", StringComparison.Ordinal))
                {
                    allowed.RemoveAt(i);
                }
            }
        }

        var denied = policy["denied_step_types"] as JsonArray;
        if (denied == null)
        {
            denied = new JsonArray();
            policy["denied_step_types"] = denied;
        }

        AddDeniedStepType(denied, "workflow.call");
        AddDeniedStepType(denied, "workflow.plan");
        policy["allow_remote_workflow_refs"] = false;

        return policy;
    }

    private static void AddDeniedStepType(JsonArray denied, string stepType)
    {
        if (denied.Any(node => string.Equals(node?.GetValue<string>(), stepType, StringComparison.Ordinal)))
            return;

        denied.Add((JsonNode)JsonValue.Create(stepType)!);
    }

    private static GeneratedLeafWorkflow PrepareGeneratedLeaf(WorkflowPipelineSubworkflowSpec spec, string yaml)
    {
        var doc = ParseAndValidateGeneratedWorkflow(yaml);
        if (doc.Workflows.Count != 1)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' must generate exactly one workflow.");

        var workflowName = doc.Workflows.Keys.Single();
        var workflow = doc.Workflows[workflowName];
        EnforceStrongObjectSchemas(spec.Name, doc);
        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            if (step.Type is "workflow.call" or "workflow.plan")
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, $"Leaf workflow '{spec.Name}' must not contain step type '{step.Type}'.");
        }

        var workflowNode = ExtractSingleWorkflowNode(yaml, workflowName);
        return new GeneratedLeafWorkflow(spec.Name, workflowName, doc, yaml, workflowNode);
    }

    private static void EnforceStrongObjectSchemas(string leafName, WorkflowDocument doc)
    {
        var errors = new List<string>();

        if (doc.Skill?.Inputs != null)
        {
            foreach (var (name, input) in doc.Skill.Inputs)
                ValidateStrongObjectInputSchema(input, $"skill.inputs.{name}", errors);
        }

        if (doc.Skill?.Outputs != null)
        {
            foreach (var (name, output) in doc.Skill.Outputs)
                ValidateStrongObjectOutputSchema(output, $"skill.outputs.{name}", errors);
        }

        foreach (var (workflowName, workflow) in doc.Workflows)
        {
            if (workflow.Inputs != null)
            {
                foreach (var (name, input) in workflow.Inputs)
                    ValidateStrongObjectInputSchema(input, $"workflows.{workflowName}.inputs.{name}", errors);
            }

            if (workflow.Outputs != null)
            {
                foreach (var (name, output) in workflow.Outputs)
                    ValidateStrongObjectOutputSchema(output, $"workflows.{workflowName}.outputs.{name}", errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Leaf workflow '{leafName}' uses weak object schemas: {string.Join("; ", errors)}");
        }
    }

    private static void ValidateStrongObjectInputSchema(InputDef schema, string path, List<string> errors)
    {
        if (string.Equals(schema.Type, "object", StringComparison.OrdinalIgnoreCase)
            && (schema.Properties == null || schema.Properties.Count == 0))
        {
            errors.Add($"{path} has type object without properties");
        }

        if (schema.Items != null)
            ValidateStrongObjectInputSchema(schema.Items, path + ".items", errors);

        if (schema.Properties != null)
        {
            foreach (var (name, child) in schema.Properties)
                ValidateStrongObjectInputSchema(child, $"{path}.properties.{name}", errors);
        }

        if (schema.AdditionalProperties != null)
            ValidateStrongObjectInputSchema(schema.AdditionalProperties, path + ".additional_properties", errors);
    }

    private static void ValidateStrongObjectOutputSchema(OutputDef schema, string path, List<string> errors)
    {
        if (string.Equals(schema.Type, "object", StringComparison.OrdinalIgnoreCase)
            && (schema.Properties == null || schema.Properties.Count == 0))
        {
            errors.Add($"{path} has type object without properties");
        }

        if (schema.Items != null)
            ValidateStrongObjectOutputSchema(schema.Items, path + ".items", errors);

        if (schema.Properties != null)
        {
            foreach (var (name, child) in schema.Properties)
                ValidateStrongObjectOutputSchema(child, $"{path}.properties.{name}", errors);
        }

        if (schema.AdditionalProperties != null)
            ValidateStrongObjectOutputSchema(schema.AdditionalProperties, path + ".additional_properties", errors);
    }

    private static string ComposePipelineWorkflowYaml(
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        GeneratedMainAssembly? assembly = null,
        IReadOnlyDictionary<string, JsonNode?>? mainInputs = null)
    {
        var documentName = ResolveConfiguredPipelineDocumentName(pipelineInput, generator)
            ?? assembly?.DocumentName
            ?? "generated-pipeline-workflow";
        mainInputs ??= BuildMainInputContract(pipelineInput, generator, extraction.Subworkflows);
        var mainWorkflowNode = assembly?.MainWorkflowNode ?? BuildMainWorkflowNode(extraction.Subworkflows, mainInputs);

        var workflowsNode = new YamlMappingNode();
        AddYaml(workflowsNode, "main", mainWorkflowNode);
        foreach (var leaf in leaves)
            AddYaml(workflowsNode, leaf.Name, leaf.WorkflowNode);

        var root = new YamlMappingNode();
        AddYaml(root, "version", Scalar("1"));
        AddYaml(root, "name", Scalar(documentName));
        AddYaml(root, "skill", BuildPipelineSkillNode(documentName, pipelineInput, generator, extraction, mainInputs, assembly?.SkillNode));
        AddYaml(root, "entrypoint", Scalar("main"));
        AddYaml(root, "workflows", workflowsNode);

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static string BuildMainAssemblyPrompt(
        JsonObject pipelineInput,
        JsonObject generator,
        string normalizedMarkdown,
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        IReadOnlyDictionary<string, JsonNode?> configuredMainInputs,
        IReadOnlyDictionary<string, JsonNode?> generatedLeafInputs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are assembling the parent `main` workflow for a GnOuGo.Flow pipeline.");
        sb.AppendLine("Return ONLY one YAML mapping with `document` and `main` keys. Do not return version, entrypoint, workflows, or leaf workflow definitions.");
        sb.AppendLine();
        sb.AppendLine("Hard rules:");
        sb.AppendLine("- The main workflow may call leaf workflows using local `workflow.call` only.");
        sb.AppendLine("- The main workflow must never use `workflow.plan`.");
        sb.AppendLine("- Leaf workflows must never call other workflows.");
        sb.AppendLine("- Preserve the orchestration algorithm from the normalized prompt and the Main workflow orchestration section.");
        sb.AppendLine("- Use conditionals, switches, loops, or parallel branches when the orchestration requires them.");
        sb.AppendLine("- Do not inline leaf logic. Call the leaf workflow that owns each responsibility.");
        sb.AppendLine("- Pass leaf arguments from declared `data.inputs.<name>`, earlier step outputs, loop variables, derived values, or constants.");
        sb.AppendLine("- Every `data.inputs.<name>` reference MUST have an identically named declaration in `main.inputs`.");
        sb.AppendLine("- Leaf input names are call arguments, not automatically public main inputs.");
        sb.AppendLine("- `generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.");
        sb.AppendLine("- If `leaf_input_candidates_yaml` or `leaf_subworkflow_specs_json` disagree with `generated_leaf_contracts_yaml`, follow `generated_leaf_contracts_yaml`.");
        sb.AppendLine("- Map public user input names to differently named leaf arguments when their meanings match.");
        sb.AppendLine("- Do not expose loop variables, intermediate values, paths, identifiers, flags, or leaf-only implementation details as public inputs unless the user explicitly requested them.");
        if (configuredMainInputs.Count > 0)
        {
            sb.AppendLine("- `authoritative_main_inputs_yaml` is exact: preserve every name and schema and do not add or remove inputs.");
            sb.AppendLine("- Structured configured document and skill metadata are authoritative; repeat them in `document` without changing their meaning.");
        }
        else
        {
            sb.AppendLine("- Infer the public main inputs from the user's normalized request, preserving names, descriptions, required flags, and defaults exactly.");
            sb.AppendLine("- Infer document name, skill description, tags, and public output schemas from the user's request.");
        }
        sb.AppendLine();
        AppendPromptSection(sb, "configured_document_name", ResolveConfiguredPipelineDocumentName(pipelineInput, generator) ?? "");
        AppendPromptSection(sb, "configured_skill_description",
            GetStringProperty(pipelineInput["skill"] as JsonObject, "description")
            ?? GetStringProperty(generator["skill"] as JsonObject, "description")
            ?? GetStringProperty(pipelineInput, "description")
            ?? GetStringProperty(generator, "description")
            ?? "");
        AppendPromptSection(sb, "configured_skill_yaml", SerializeConfiguredSkill(pipelineInput, generator));
        AppendPromptSection(sb, "normalized_markdown", normalizedMarkdown);
        AppendPromptSection(sb, "main_workflow_orchestration", extraction.MainWorkflowPrompt);
        AppendPromptSection(sb, "authoritative_main_inputs_yaml", SerializeYamlMapping(configuredMainInputs));
        AppendPromptSection(sb, "leaf_input_candidates_yaml", SerializeYamlMapping(generatedLeafInputs));
        AppendPromptSection(sb, "generated_leaf_contracts_yaml", BuildGeneratedLeafContractsYaml(leaves));
        AppendPromptSection(sb, "leaf_subworkflow_specs_json", BuildExtractionJson(extraction).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AppendPromptSection(sb, "generated_leaf_workflows_yaml", string.Join("\n---\n", leaves.Select(leaf => leaf.Yaml)));
        sb.AppendLine();
        sb.AppendLine("Output shape example:");
        sb.AppendLine("document:");
        sb.AppendLine("  name: example_pipeline");
        sb.AppendLine("  skill:");
        sb.AppendLine("    description: Process the user's query.");
        sb.AppendLine("    tags: [example, pipeline]");
        sb.AppendLine("    inputs:");
        sb.AppendLine("      user_query: string");
        sb.AppendLine("    outputs:");
        sb.AppendLine("      result: string");
        sb.AppendLine("main:");
        sb.AppendLine("  inputs:");
        sb.AppendLine("    user_query: string");
        sb.AppendLine("  steps:");
        sb.AppendLine("    - id: call_example_leaf");
        sb.AppendLine("      type: workflow.call");
        sb.AppendLine("      input:");
        sb.AppendLine("        ref:");
        sb.AppendLine("          kind: local");
        sb.AppendLine("          name: example_leaf");
        sb.AppendLine("        args:");
        sb.AppendLine("          query: ${data.inputs.user_query}");
        sb.AppendLine("  outputs:");
        sb.AppendLine("    result: ${data.steps.call_example_leaf.outputs.result}");
        return sb.ToString();
    }

    private static string BuildMainAssemblyRepairPrompt(
        string basePrompt,
        string? previousResponse,
        string structuredError)
    {
        var sb = new StringBuilder(basePrompt.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("The previous main workflow assembly failed final validation.");
        sb.AppendLine("Return a complete corrected `document` and `main` YAML mapping that still follows every rule above.");
        sb.AppendLine("Fix the reported error without changing the user's public contract or orchestration intent.");
        if (!string.IsNullOrWhiteSpace(previousResponse))
            AppendPromptSection(sb, "invalid_main_assembly_yaml", StripMarkdownFences(previousResponse));
        AppendPromptSection(sb, "main_assembly_validation_error", structuredError);
        return sb.ToString();
    }

    private static GeneratedMainAssembly ParseGeneratedMainAssembly(string yaml)
    {
        var root = LoadYamlRoot(StripMarkdownFences(yaml));
        if (root.GetMapping("document") is { } document && root.GetMapping("main") is { } wrappedMain)
        {
            return new GeneratedMainAssembly(
                wrappedMain,
                document.GetScalar("name"),
                document.GetMapping("skill"));
        }

        if (root.GetMapping("workflows") is { } workflows
            && workflows.Children.TryGetValue(Scalar("main"), out var nestedMain)
            && nestedMain is YamlMappingNode nestedMainMap)
        {
            return new GeneratedMainAssembly(nestedMainMap, root.GetScalar("name"), root.GetMapping("skill"));
        }

        if (root.Children.TryGetValue(Scalar("main"), out var main)
            && main is YamlMappingNode mainMap)
        {
            return new GeneratedMainAssembly(mainMap, root.GetScalar("name"), root.GetMapping("skill"));
        }

        return new GeneratedMainAssembly(root, null, null);
    }

    private static void ForceMainWorkflowInputs(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, JsonNode?> mainInputs)
    {
        if (ContainsYamlKey(mainWorkflowNode, "inputs"))
            mainWorkflowNode.Children.Remove(Scalar("inputs"));

        var inputs = new YamlMappingNode();
        foreach (var (name, schema) in mainInputs)
            AddYaml(inputs, name, JsonToYaml(schema));
        AddYaml(mainWorkflowNode, "inputs", inputs);
    }

    private static void EnsureMainWorkflowOutputs(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        if (ContainsYamlKey(mainWorkflowNode, "outputs"))
            return;

        var outputs = new YamlMappingNode();
        foreach (var spec in specs)
            AddYaml(outputs, $"{spec.Name}_outputs", Scalar($"${{data.steps.call_{spec.Name}.outputs}}"));
        AddYaml(mainWorkflowNode, "outputs", outputs);
    }

    private static string SerializeYamlMapping(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var map = new YamlMappingNode();
        foreach (var (name, value) in values)
            AddYaml(map, name, JsonToYaml(value));

        var stream = new YamlStream(new YamlDocument(map));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static string SerializeConfiguredSkill(JsonObject pipelineInput, JsonObject generator)
    {
        var skill = pipelineInput["skill"] as JsonObject ?? generator["skill"] as JsonObject;
        if (skill == null)
            return "{}";

        var stream = new YamlStream(new YamlDocument(JsonToYaml(skill)));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static string BuildGeneratedLeafContractsYaml(IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var root = new YamlMappingNode();
        foreach (var leaf in leaves)
        {
            var contract = new YamlMappingNode();
            AddYaml(contract, "workflow", Scalar(leaf.GeneratedWorkflowName));
            AddYaml(contract, "inputs", leaf.WorkflowNode.GetMapping("inputs") ?? new YamlMappingNode());
            AddYaml(contract, "outputs", leaf.WorkflowNode.GetMapping("outputs") ?? new YamlMappingNode());
            AddYaml(root, leaf.Name, contract);
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static string? ResolveConfiguredPipelineDocumentName(JsonObject pipelineInput, JsonObject generator)
        => GetStringProperty(pipelineInput, "name")
            ?? GetStringProperty(pipelineInput, "workflow_name")
            ?? GetStringProperty(pipelineInput, "document_name")
            ?? GetStringProperty(generator, "name")
            ?? GetStringProperty(generator, "workflow_name")
            ?? GetStringProperty(generator, "document_name");

    private static YamlMappingNode BuildPipelineSkillNode(
        string documentName,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineExtraction extraction,
        IReadOnlyDictionary<string, JsonNode?> mainInputs,
        YamlMappingNode? generatedSkill = null)
    {
        var pipelineSkill = pipelineInput["skill"] as JsonObject;
        var generatorSkill = generator["skill"] as JsonObject;
        var skill = new YamlMappingNode();
        AddYaml(skill, "description", Scalar(
            GetStringProperty(pipelineSkill, "description")
            ?? GetStringProperty(generatorSkill, "description")
            ?? GetStringProperty(pipelineInput, "description")
            ?? GetStringProperty(generator, "description")
            ?? generatedSkill?.GetScalar("description")
            ?? BuildGeneratedPipelineSkillDescription(documentName, pipelineInput, generator, extraction)));

        AddYaml(skill, "tags", BuildPipelineSkillTags(pipelineSkill, generatorSkill, generatedSkill));

        var inputs = new YamlMappingNode();
        foreach (var (name, schema) in mainInputs)
            AddYaml(inputs, name, JsonToYaml(schema));
        AddYaml(skill, "inputs", inputs);

        var outputs = new YamlMappingNode();
        AddSchemaMap(outputs, generatorSkill?["outputs"] as JsonObject);
        AddSchemaMap(outputs, generator["outputs"] as JsonObject);
        AddSchemaMap(outputs, pipelineSkill?["outputs"] as JsonObject);
        AddSchemaMap(outputs, pipelineInput["outputs"] as JsonObject);
        if (outputs.Children.Count == 0 && generatedSkill?.GetMapping("outputs") is { } generatedOutputs)
        {
            foreach (var output in generatedOutputs.Children)
            {
                if (output.Key is YamlScalarNode key && !string.IsNullOrWhiteSpace(key.Value))
                    AddYaml(outputs, key.Value, output.Value);
                else
                    outputs.Add(CloneYamlNode(output.Key), CloneYamlNode(output.Value));
            }
        }

        if (outputs.Children.Count == 0)
        {
            foreach (var spec in extraction.Subworkflows)
            {
                var output = new YamlMappingNode();
                AddYaml(output, "type", Scalar("object"));
                AddYaml(output, "description", Scalar($"Outputs from the {spec.Name} leaf workflow."));
                AddYaml(outputs, $"{spec.Name}_outputs", output);
            }
        }
        AddYaml(skill, "outputs", outputs);

        return skill;
    }

    private static string BuildGeneratedPipelineSkillDescription(
        string documentName,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineExtraction extraction)
    {
        var source = GetStringProperty(pipelineInput, "raw_prompt")
            ?? GetStringProperty(generator, "raw_prompt")
            ?? GetStringProperty(generator, "instruction")
            ?? extraction.MainWorkflowPrompt
            ?? string.Join("; ", extraction.Subworkflows.Select(static spec => spec.Goal));
        source = StripMarkdownFences(source)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !line.StartsWith("#", StringComparison.Ordinal))
            ?? $"Generated pipeline workflow for {documentName}.";
        return source.Length <= 180 ? source : source[..177] + "...";
    }

    private static YamlSequenceNode BuildPipelineSkillTags(
        JsonObject? pipelineSkill,
        JsonObject? generatorSkill,
        YamlMappingNode? generatedSkill)
    {
        if (TryBuildTags(pipelineSkill?["tags"], out var pipelineTags))
            return pipelineTags;

        if (TryBuildTags(generatorSkill?["tags"], out var generatorTags))
            return generatorTags;

        if (generatedSkill?.GetSequence("tags") is { } generatedTags)
        {
            var tags = generatedTags.Children
                .OfType<YamlScalarNode>()
                .Select(static tag => Scalar(tag.Value ?? ""))
                .Where(static tag => !string.IsNullOrWhiteSpace(tag.Value))
                .ToArray();
            if (tags.Length > 0)
                return new YamlSequenceNode(tags);
        }

        return new YamlSequenceNode(Scalar("generated"), Scalar("pipeline"));
    }

    private static bool TryBuildTags(JsonNode? node, out YamlSequenceNode tags)
    {
        tags = new YamlSequenceNode();
        if (node is not JsonArray array)
            return false;

        foreach (var item in array)
        {
            var tag = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(tag))
                tags.Add(Scalar(tag));
        }

        return tags.Children.Count > 0;
    }

    private static YamlMappingNode BuildMainWorkflowNode(
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs,
        IReadOnlyDictionary<string, JsonNode?> mainInputs)
    {
        var main = new YamlMappingNode();

        var inputs = new YamlMappingNode();
        foreach (var (name, schema) in mainInputs)
            AddYaml(inputs, name, JsonToYaml(schema));
        AddYaml(main, "inputs", inputs);

        var steps = new YamlSequenceNode();
        var availableOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var input = new YamlMappingNode();
            var refNode = new YamlMappingNode();
            AddYaml(refNode, "kind", Scalar("local"));
            AddYaml(refNode, "name", Scalar(spec.Name));
            AddYaml(input, "ref", refNode);

            var args = new YamlMappingNode();
            foreach (var inputName in spec.Inputs.Keys)
            {
                var expr = availableOutputs.TryGetValue(inputName, out var producerName)
                    ? $"${{data.steps.call_{producerName}.outputs.{inputName}}}"
                    : $"${{data.inputs.{inputName}}}";
                AddYaml(args, inputName, Scalar(expr));
            }
            AddYaml(input, "args", args);

            var step = new YamlMappingNode();
            AddYaml(step, "id", Scalar($"call_{spec.Name}"));
            AddYaml(step, "type", Scalar("workflow.call"));
            AddYaml(step, "input", input);
            steps.Add(step);

            foreach (var outputName in spec.Outputs.Keys)
                availableOutputs[outputName] = spec.Name;
        }
        AddYaml(main, "steps", steps);

        var outputs = new YamlMappingNode();
        foreach (var spec in specs)
            AddYaml(outputs, $"{spec.Name}_outputs", Scalar($"${{data.steps.call_{spec.Name}.outputs}}"));
        AddYaml(main, "outputs", outputs);

        return main;
    }

    private static Dictionary<string, JsonNode?> BuildMainInputContract(
        JsonObject pipelineInput,
        JsonObject generator,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var inputs = BuildConfiguredMainInputContract(pipelineInput, generator);
        if (inputs.Count > 0)
            return inputs;

        foreach (var (name, type) in BuildGeneratedMainInputContract(specs))
            inputs[name] = JsonValue.Create(type);

        return inputs;
    }

    private static Dictionary<string, JsonNode?> BuildConfiguredMainInputContract(
        JsonObject pipelineInput,
        JsonObject generator)
    {
        var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var pipelineSkill = pipelineInput["skill"] as JsonObject;
        var generatorSkill = generator["skill"] as JsonObject;

        AddSchemaMap(inputs, generatorSkill?["inputs"] as JsonObject, overwrite: true);
        AddSchemaMap(inputs, generator["inputs"] as JsonObject, overwrite: true);
        AddSchemaMap(inputs, pipelineSkill?["inputs"] as JsonObject, overwrite: true);
        AddSchemaMap(inputs, pipelineInput["inputs"] as JsonObject, overwrite: true);

        return inputs;
    }

    private static Dictionary<string, JsonNode?> ResolveMainInputContract(
        IReadOnlyDictionary<string, JsonNode?> configuredInputs,
        GeneratedMainAssembly assembly,
        IReadOnlyDictionary<string, JsonNode?> generatedLeafInputs)
    {
        if (configuredInputs.Count > 0)
            return configuredInputs.ToDictionary(static pair => pair.Key, static pair => pair.Value?.DeepClone(), StringComparer.Ordinal);

        var generated = ReadYamlSchemaMap(assembly.MainWorkflowNode.GetMapping("inputs"));
        if (generated.Count == 0)
            generated = ReadYamlSchemaMap(assembly.SkillNode?.GetMapping("inputs"));

        if (generated.Count > 0)
            return generated;

        return generatedLeafInputs.ToDictionary(static pair => pair.Key, static pair => pair.Value?.DeepClone(), StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonNode?> ReadYamlSchemaMap(YamlMappingNode? inputMap)
    {
        var schemas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (inputMap != null)
        {
            foreach (var (keyNode, schemaNode) in inputMap.Children)
            {
                var name = (keyNode as YamlScalarNode)?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    schemas[name] = WorkflowParser.YamlToJson(schemaNode);
            }
        }

        return schemas;
    }

    private static void ValidateDeclaredMainInputReferences(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, JsonNode?> mainInputs)
    {
        var stream = new YamlStream(new YamlDocument(mainWorkflowNode));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        var undeclared = DataInputReferenceRegex().Matches(writer.ToString())
            .Select(static match => match.Groups["name"].Value)
            .Where(name => !mainInputs.ContainsKey(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (undeclared.Length == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow references undeclared inputs: " + string.Join(", ", undeclared));
    }

    private static Dictionary<string, string> BuildGeneratedMainInputContract(IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var availableOutputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            foreach (var (name, type) in spec.Inputs)
            {
                if (availableOutputs.Contains(name))
                    continue;

                if (!inputs.TryGetValue(name, out var existing))
                {
                    inputs[name] = type;
                    continue;
                }

                if (!string.Equals(existing, type, StringComparison.OrdinalIgnoreCase))
                    inputs[name] = "any";
            }

            foreach (var outputName in spec.Outputs.Keys)
                availableOutputs.Add(outputName);
        }

        return inputs;
    }

    private static Dictionary<string, JsonNode?> BuildGeneratedMainInputContract(IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var availableOutputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var leafInputs = ReadYamlSchemaMap(leaf.WorkflowNode.GetMapping("inputs"));
            foreach (var (name, schema) in leafInputs)
            {
                if (availableOutputs.Contains(name))
                    continue;

                if (!inputs.TryGetValue(name, out var existing))
                {
                    inputs[name] = schema?.DeepClone();
                    continue;
                }

                if (!JsonNode.DeepEquals(existing, schema))
                    inputs[name] = JsonValue.Create("any");
            }

            foreach (var outputName in ReadYamlSchemaMap(leaf.WorkflowNode.GetMapping("outputs")).Keys)
                availableOutputs.Add(outputName);
        }

        return inputs;
    }

    private static void AddSchemaMap(
        Dictionary<string, JsonNode?> target,
        JsonObject? source,
        bool overwrite)
    {
        if (source == null)
            return;

        foreach (var (name, schema) in source)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!overwrite && target.ContainsKey(name))
                continue;

            target[name] = schema?.DeepClone();
        }
    }

    private static void AddSchemaMap(YamlMappingNode target, JsonObject? source)
    {
        if (source == null)
            return;

        foreach (var (name, schema) in source)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (ContainsYamlKey(target, name))
                target.Children.Remove(Scalar(name));

            AddYaml(target, name, JsonToYaml(schema));
        }
    }

    private static bool ContainsYamlKey(YamlMappingNode node, string key)
        => node.Children.ContainsKey(Scalar(key));

    private static YamlNode JsonToYaml(JsonNode? node)
    {
        if (node == null)
            return Scalar("any");

        if (node is JsonObject obj)
        {
            var map = new YamlMappingNode();
            foreach (var (key, childNode) in obj)
                AddYaml(map, key, JsonToYaml(childNode));
            return map;
        }

        if (node is JsonArray array)
        {
            var sequence = new YamlSequenceNode();
            foreach (var item in array)
                sequence.Add(JsonToYaml(item));
            return sequence;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
                return Scalar(stringValue);
            if (value.TryGetValue<bool>(out var boolValue))
                return Scalar(boolValue ? "true" : "false");
            if (value.TryGetValue<int>(out var intValue))
                return Scalar(intValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (value.TryGetValue<long>(out var longValue))
                return Scalar(longValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (value.TryGetValue<double>(out var doubleValue))
                return Scalar(doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (value.TryGetValue<JsonElement>(out var element))
                return Scalar(element.ToString());
        }

        return Scalar(node.ToJsonString());
    }

    private static string? GetStringProperty(JsonObject? obj, string name)
    {
        if (obj == null || obj[name] is not JsonValue value)
            return null;

        return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private static void EnforcePipelineWorkflowHierarchy(WorkflowDocument doc, IReadOnlySet<string> leafNames)
    {
        if (!doc.Workflows.ContainsKey("main"))
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Pipeline final YAML must contain a main workflow.");

        foreach (var (workflowName, workflow) in doc.Workflows)
        {
            foreach (var step in EnumerateSteps(workflow.Steps))
            {
                if (step.Type == "workflow.plan")
                    throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, "Pipeline final YAML must not contain workflow.plan.");

                if (workflowName != "main" && step.Type == "workflow.call")
                    throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, $"Leaf workflow '{workflowName}' must not contain workflow.call.");

                if (workflowName == "main" && step.Type == "workflow.call")
                {
                    var refObj = (step.Input as JsonObject)?["ref"] as JsonObject;
                    var kind = refObj?["kind"]?.GetValue<string>() ?? "local";
                    var targetName = refObj?["name"]?.GetValue<string>();
                    if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(targetName))
                        throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, "Pipeline main workflow may only use local workflow.call references.");
                    if (!leafNames.Contains(targetName))
                        throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, $"Pipeline main workflow calls unknown leaf workflow '{targetName}'.");
                }
            }
        }
    }

    private static YamlMappingNode ExtractSingleWorkflowNode(string yaml, string workflowName)
    {
        var root = LoadYamlRoot(yaml);
        var workflows = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated leaf YAML is missing workflows.");
        if (!workflows.Children.TryGetValue(new YamlScalarNode(workflowName), out var workflowNode)
            || workflowNode is not YamlMappingNode workflowMapping)
        {
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Generated leaf YAML does not contain workflow '{workflowName}'.");
        }

        return workflowMapping;
    }

    private static YamlMappingNode LoadYamlRoot(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root
            ? root
            : throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated YAML root must be a mapping.");
    }

    private static JsonObject BuildExtractionJson(WorkflowPipelineExtraction extraction)
    {
        var subworkflows = new JsonArray();
        foreach (var spec in extraction.Subworkflows)
            subworkflows.Add((JsonNode)BuildSpecJson(spec));

        return new JsonObject
        {
            ["subworkflows"] = subworkflows,
            ["main_workflow_prompt"] = extraction.MainWorkflowPrompt,
            ["validation"] = BuildValidationJson(extraction.ValidationErrors)
        };
    }

    private static JsonObject BuildSpecJson(WorkflowPipelineSubworkflowSpec spec)
    {
        return new JsonObject
        {
            ["name"] = spec.Name,
            ["goal"] = spec.Goal,
            ["inputs"] = BuildStringMapJson(spec.Inputs),
            ["outputs"] = BuildStringMapJson(spec.Outputs),
            ["extract_reason"] = spec.ExtractReason,
            ["content"] = spec.Content,
            ["generation_prompt"] = spec.GenerationPrompt
        };
    }

    private static JsonObject BuildStringMapJson(IReadOnlyDictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
            obj[key] = value;
        return obj;
    }

    private static JsonObject BuildValidationJson(IReadOnlyList<string> errors)
    {
        var array = new JsonArray();
        foreach (var error in errors)
            array.Add((JsonNode)JsonValue.Create(error)!);
        return new JsonObject { ["errors"] = array };
    }

    private static string RemoveSubworkflowContentIndent(string line)
    {
        if (line.StartsWith("  ", StringComparison.Ordinal))
            return line[2..];
        if (line.StartsWith('\t'))
            return line[1..];
        return line;
    }

    private static string NormalizeWorkflowSchemaType(string type)
    {
        var normalized = type.Trim().ToLowerInvariant();
        return normalized switch
        {
            "str" or "text" => "string",
            "int" or "integer" or "float" or "double" or "decimal" => "number",
            "bool" => "boolean",
            "list" => "array",
            "map" => "dictionary",
            "json" => "object",
            "any" or "string" or "number" or "boolean" or "array" or "object" or "dictionary" => normalized,
            _ => "any"
        };
    }

    private static void AddYaml(YamlMappingNode node, string key, YamlNode value)
        => node.Children.Add(Scalar(key), CloneYamlNode(value));

    private static YamlNode CloneYamlNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return new YamlScalarNode(scalar.Value)
                {
                    Style = scalar.Style
                };

            case YamlSequenceNode sequence:
            {
                var clone = new YamlSequenceNode
                {
                    Style = sequence.Style
                };
                foreach (var child in sequence.Children)
                    clone.Add(CloneYamlNode(child));
                return clone;
            }

            case YamlMappingNode mapping:
            {
                var clone = new YamlMappingNode
                {
                    Style = mapping.Style
                };
                foreach (var (key, value) in mapping.Children)
                    clone.Add(CloneYamlNode(key), CloneYamlNode(value));
                return clone;
            }

            default:
                throw new WorkflowRuntimeException(
                    ErrorCodes.TemplatePlan,
                    $"Unsupported YAML node type during pipeline assembly: {node.GetType().Name}");
        }
    }

    private static YamlScalarNode Scalar(string value) => new(value);

    private sealed record WorkflowPipelineExtraction(
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> Subworkflows,
        string MainWorkflowPrompt,
        IReadOnlyList<string> ValidationErrors);

    private sealed record GeneratedMainAssembly(
        YamlMappingNode MainWorkflowNode,
        string? DocumentName,
        YamlMappingNode? SkillNode);

    [GeneratedRegex(@"\bdata\.inputs\.(?<name>[A-Za-z_][A-Za-z0-9_-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex DataInputReferenceRegex();

    private sealed record WorkflowPipelineSubworkflowSpec(
        string Name,
        string Goal,
        IReadOnlyDictionary<string, string> Inputs,
        IReadOnlyDictionary<string, string> Outputs,
        string ExtractReason,
        string Content,
        string GenerationPrompt);

    private sealed record GeneratedLeafWorkflow(
        string Name,
        string GeneratedWorkflowName,
        WorkflowDocument Document,
        string Yaml,
        YamlMappingNode WorkflowNode);

    [GeneratedRegex(@"(?ms)^:::subworkflow\s+name=""(?<name>[a-z0-9_]+)""\s*\n(?<body>.*?)^:::\s*$")]
    private static partial Regex SubworkflowBlockRegex();

    [GeneratedRegex(@"(?m)^:::subworkflow\b")]
    private static partial Regex SubworkflowMarkerRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCaseNameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(?i)\bworkflow\.call\b|\bcall(?:s|ing)?\s+(?:a|an|the|another\s+)?(?:leaf\s+)?sub-?workflow\b|\bsub-?workflow\s+call\b")]
    private static partial Regex SubworkflowCallMentionRegex();

    [GeneratedRegex(@"(?im)^##\s+Main workflow orchestration\s*$")]
    private static partial Regex MainWorkflowOrchestrationRegex();
}
