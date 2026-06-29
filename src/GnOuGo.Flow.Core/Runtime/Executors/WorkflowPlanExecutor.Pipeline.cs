using System.Globalization;
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
    private static readonly string[] PipelineMainSupportStepTypes =
    [
        "workflow.call",
        "set",
        "sequence",
        "switch",
        "parallel",
        "loop.sequential",
        "loop.parallel"
    ];

    private sealed record PipelineMcpContext(
        IReadOnlyList<McpServerDiscovery> Servers,
        string? ServersDoc)
    {
        public static PipelineMcpContext Empty { get; } = new(Array.Empty<McpServerDiscovery>(), null);
    }

    private sealed record PipelineLeafResourceManifest(
        IReadOnlyList<PipelineLeafResourceProducer> Producers,
        IReadOnlyList<PipelineLeafResourceConsumer> Consumers,
        IReadOnlyList<PipelineLeafResourceLink> Links,
        IReadOnlyList<PipelineLeafResourceConsumer> MissingConsumers,
        IReadOnlyList<PipelineLeafResourceNearMiss> NearMisses);

    private sealed record PipelineLeafResourceProducer(
        string Leaf,
        string Output,
        string Kind,
        string ReferencePattern,
        string? Description);

    private sealed record PipelineLeafResourceConsumer(
        string Leaf,
        string Input,
        string Kind,
        string? Description);

    private sealed record PipelineLeafResourceLink(
        string ConsumerLeaf,
        string ConsumerInput,
        string Kind,
        string ProducerLeaf,
        string ProducerOutput,
        string ReferencePattern);

    private sealed record PipelineLeafResourceNearMiss(
        string Leaf,
        string Output,
        string Kind,
        string ReferencePattern,
        string? Description,
        string Reason);

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
        var useStructuredExtraction = await ShouldUseStructuredPipelineExtractionAsync(ctx, provider, model, ct);

        ctx.SetTelemetryAttribute("gnougo-flow.plan.mode", "pipeline");
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "openai");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.structured_extraction", useStructuredExtraction);

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Preparing workflow generation prompt through pipeline mode."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var normalizedMarkdown = await NormalizeUserPromptAsync(
            llmClient, rawPrompt, provider, model, reasoning, ctx, ct);

        var globalMcpContext = await BuildPipelineGlobalMcpContextAsync(
            llmClient, generator, normalizedMarkdown, rawPrompt, model, provider, reasoning, ctx, ct);

        var (annotatedMarkdown, extraction) = await MarkAndExtractSubworkflowSpecsAsync(
            llmClient, normalizedMarkdown, globalMcpContext, input, provider, model, reasoning, useStructuredExtraction, ctx, ct);

        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count);

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
                mainSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly.kind", "llm_orchestration_graph");
                var configuredMainInputs = BuildConfiguredMainInputContract(input, generator);
                var leafResourceManifest = BuildPipelineLeafResourceManifest(generatedLeaves);
                ValidatePipelineLeafResourceManifest(leafResourceManifest, configuredMainInputs);
                mainSpan.SetAttribute("gnougo-flow.plan.pipeline.resource_producer_count", leafResourceManifest.Producers.Count);
                mainSpan.SetAttribute("gnougo-flow.plan.pipeline.resource_consumer_count", leafResourceManifest.Consumers.Count);
                mainSpan.SetAttribute("gnougo-flow.plan.pipeline.resource_link_count", leafResourceManifest.Links.Count);
                var generatedLeafInputs = BuildGeneratedMainInputContract(generatedLeaves);
                var baseMainAssemblyPrompt = BuildMainAssemblyPrompt(
                    input, generator, normalizedMarkdown, extraction, generatedLeaves, leafResourceManifest, configuredMainInputs, generatedLeafInputs, ctx.Engine.Registry);
                var maxAssemblyAttempts = GetPipelineGenerationMaxAttempts(input);
                var validate = input["validate"] as JsonObject;
                var validationDiscovered = await DiscoverMcpServersAsync(
                    ctx.Engine.McpClientFactory,
                    ctx.Engine.McpCache,
                    ctx.Engine.Logger,
                    ctx,
                    candidateServers: null,
                    mainSpan.Span,
                    ct);
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

                        var assembly = ParseGeneratedMainAssembly(mainResponse.Text ?? string.Empty, generatedLeaves);
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
                                ValidatePipelineLeafCallArguments(assembledDocument, generatedLeaves);
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
            - Make implicit logic explicit when it follows directly from the prompt.
            - Separate external inputs from values that can be derived deterministically by the workflow.
            - When a required value is not provided and cannot be derived, list it as a missing external input instead of inventing a placeholder.
            - When a value is only needed as an implementation detail, such as a target/destination path, directory, identifier, or temporary filename, list it as an internal implementation value to derive inside the responsible workflow step or leaf.
            - Do not turn internal implementation values into public inputs unless the raw prompt explicitly asks the user to provide them.
            - Do not use fake placeholder values such as UNKNOWN_OWNER, UNKNOWN_REPO, TODO, or example-only paths.
            - Make the result easier to read and easier to transform into workflows.

            Preferred Markdown shape:
            ## Normalized Request
            - Clean statement of the requested automation.

            ## Explicit Requirements
            - User-provided rules, constraints, inputs, outputs, and acceptance criteria.

            ## Derived Values
            - Values that can be computed from explicit inputs or previous step outputs.

            ## Missing External Inputs
            - Values that must be provided by the caller because they are required but not derivable.

            ## Internal Implementation Values
            - Values the workflow should create internally, not expose as required public inputs.

            <raw_prompt>
            {{rawPrompt}}
            </raw_prompt>
            """;

        return await ExecutePipelineLlmTextPhaseAsync(
            llmClient, "normalize_user_prompt", prompt, provider, model, reasoning, ctx, ct);
    }

    private static async Task<bool> ShouldUseStructuredPipelineExtractionAsync(
        StepExecutionContext ctx,
        string? provider,
        string model,
        CancellationToken ct)
    {
        if (ctx.Engine.LLMCapabilities == null)
            return false;

        try
        {
            return await ctx.Engine.LLMCapabilities.SupportsStructuredOutputAsync(provider, model, ct) == true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Engine.Logger.LogWarning(
                ex,
                "workflow.plan pipeline: failed to resolve structured-output capability for provider '{Provider}' model '{Model}', falling back to annotated Markdown extraction",
                provider ?? "(default)",
                model);
            return false;
        }
    }

    private static async Task<PipelineMcpContext> BuildPipelineGlobalMcpContextAsync(
        ILLMClient llmClient,
        JsonObject generator,
        string normalizedMarkdown,
        string rawPrompt,
        string model,
        string? provider,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var prefilterNode = generator["prefilter"];
        var shouldPrefilter = prefilterNode == null
            || prefilterNode is JsonObject
            || (prefilterNode is JsonValue jv && (!jv.TryGetValue<bool>(out var bv) || bv));
        if (!shouldPrefilter)
            return PipelineMcpContext.Empty;

        if (ctx.Engine.McpClientFactory?.ServerMetadata == null || ctx.Engine.McpClientFactory.ServerMetadata.Count == 0)
            return PipelineMcpContext.Empty;

        var prefilterModel = model;
        var prefilterProvider = provider;
        double? prefilterTemperature = null;
        if (prefilterNode is JsonObject pfObj)
        {
            prefilterModel = pfObj["model"]?.GetValue<string>() ?? model;
            prefilterProvider = pfObj["provider"]?.GetValue<string>() ?? provider;
            prefilterTemperature = pfObj["temperature"]?.GetValue<double>();
        }

        using var mcpContextSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.global_mcp_context", "global_mcp_context", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", prefilterProvider ?? "unknown"),
            new KeyValuePair<string, object?>("gen_ai.request.model", prefilterModel)
        });

        try
        {
            var requiredMcpServerNames = ExtractRequiredMcpServerNames(
                normalizedMarkdown,
                rawPrompt,
                ctx.Engine.McpClientFactory.ServerMetadata);

            var candidateMcpServers = await PrefilterMcpServerMetadataAsync(
                llmClient,
                ctx.Engine.McpClientFactory,
                normalizedMarkdown,
                rawPrompt,
                prefilterModel,
                prefilterProvider,
                prefilterTemperature,
                reasoning,
                ctx,
                mcpContextSpan.Span,
                ct);

            candidateMcpServers = MergeRequiredMcpServerMetadata(
                candidateMcpServers,
                ctx.Engine.McpClientFactory.ServerMetadata,
                requiredMcpServerNames,
                ctx);

            var discovered = await DiscoverMcpServersAsync(
                ctx.Engine.McpClientFactory,
                ctx.Engine.McpCache,
                ctx.Engine.Logger,
                ctx,
                candidateMcpServers,
                mcpContextSpan.Span,
                ct);

            if (discovered is { Count: > 0 })
            {
                var prefilterSource = discovered;
                discovered = await PrefilterMcpServersAsync(
                    llmClient,
                    discovered,
                    normalizedMarkdown,
                    rawPrompt,
                    prefilterModel,
                    prefilterProvider,
                    prefilterTemperature,
                    reasoning,
                    ctx,
                    mcpContextSpan.Span,
                    ct);
                discovered = MergeRequiredMcpServerDiscovery(
                    discovered,
                    prefilterSource,
                    requiredMcpServerNames,
                    ctx);
            }

            if (discovered == null || discovered.Count == 0)
            {
                mcpContextSpan.SetAttribute("mcp.servers_selected", 0);
                mcpContextSpan.SetAttribute("mcp.tools_selected", 0);
                return PipelineMcpContext.Empty;
            }

            mcpContextSpan.SetAttribute("mcp.servers_selected", discovered.Count);
            mcpContextSpan.SetAttribute("mcp.tools_selected", discovered.Sum(static server => server.Tools.Count));
            return new PipelineMcpContext(discovered, FormatMcpServersDoc(discovered));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mcpContextSpan.Fail(ex);
            ctx.Engine.Logger.LogWarning(ex, "workflow.plan pipeline: failed to build global MCP context");
            return PipelineMcpContext.Empty;
        }
    }

    private static async Task<(string AnnotatedMarkdown, WorkflowPipelineExtraction Extraction)> MarkAndExtractSubworkflowSpecsAsync(
        ILLMClient llmClient,
        string normalizedMarkdown,
        PipelineMcpContext pipelineMcpContext,
        JsonObject pipelineInput,
        string? provider,
        string model,
        string? reasoning,
        bool useStructuredExtraction,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var maxAttempts = GetPipelineGenerationMaxAttempts(pipelineInput);
        string? previousAnnotatedMarkdown = null;
        IReadOnlyList<string>? previousValidationErrors = null;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var prompt = previousValidationErrors == null
                ? BuildMarkExtractableBlocksPrompt(normalizedMarkdown, pipelineMcpContext.ServersDoc, useStructuredExtraction)
                : BuildMarkExtractableBlocksRepairPrompt(
                    normalizedMarkdown,
                    pipelineMcpContext.ServersDoc,
                    previousAnnotatedMarkdown,
                    previousValidationErrors,
                    useStructuredExtraction);

            string annotatedMarkdown;
            StructuredPipelineExtractionMetadata structuredMetadata;
            IReadOnlyList<string> responseValidationErrors;
            try
            {
                if (useStructuredExtraction)
                {
                    var response = await ExecutePipelineLlmStructuredPhaseAsync(
                        llmClient,
                        "mark_extractable_blocks",
                        prompt,
                        provider,
                        model,
                        reasoning,
                        ctx,
                        ct,
                        attempt,
                        maxAttempts,
                        BuildMarkExtractableBlocksStructuredOutputSchema());

                    (annotatedMarkdown, structuredMetadata, responseValidationErrors) =
                        ParseMarkExtractableBlocksResponse(response, allowAnnotatedMarkdownFallback: false);
                }
                else
                {
                    annotatedMarkdown = await ExecutePipelineLlmTextPhaseAsync(
                        llmClient,
                        "mark_extractable_blocks",
                        prompt,
                        provider,
                        model,
                        reasoning,
                        ctx,
                        ct,
                        attempt,
                        maxAttempts);
                    structuredMetadata = StructuredPipelineExtractionMetadata.Empty;
                    responseValidationErrors = Array.Empty<string>();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                previousAnnotatedMarkdown = null;
                previousValidationErrors = new[]
                {
                    $"mark_extractable_blocks failed before extraction validation: {ex.Message}"
                };
                AddPipelineExtractionRetryTelemetry(ctx, attempt, maxAttempts, ex);
                continue;
            }

            using var extractionSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.extract_subworkflow_specs", "extract_subworkflow_specs", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAttempts)
            });

            try
            {
                var extraction = ExtractSubworkflowSpecs(annotatedMarkdown);
                extraction = EnrichSubworkflowSpecsWithStructuredMetadata(extraction, structuredMetadata, pipelineMcpContext, responseValidationErrors);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.validation_error_count", extraction.ValidationErrors.Count);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.planned_tool_count", extraction.Subworkflows.Sum(static spec => spec.PlannedTools.Count));

                if (extraction.ValidationErrors.Count == 0)
                {
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "succeeded");
                    return (annotatedMarkdown, extraction);
                }

                var validationException = BuildPipelineExtractionException(extraction.ValidationErrors, annotatedMarkdown);
                extractionSpan.AddEvent(
                    "gnougo-flow.plan.pipeline.extraction.validation_error",
                    BuildPlanErrorTelemetryAttributes(validationException, attempt, "extract_subworkflow_specs"));

                if (attempt >= maxAttempts)
                {
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "failed");
                    extractionSpan.Fail(validationException);
                    throw validationException;
                }

                lastException = validationException;
                previousAnnotatedMarkdown = annotatedMarkdown;
                previousValidationErrors = extraction.ValidationErrors;
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "retrying");
                extractionSpan.Fail(validationException);
                AddPipelineExtractionRetryTelemetry(ctx, attempt, maxAttempts, validationException);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                previousAnnotatedMarkdown = annotatedMarkdown;
                previousValidationErrors = new[] { ex.Message };
                extractionSpan.AddEvent(
                    "gnougo-flow.plan.pipeline.extraction.error",
                    BuildPlanErrorTelemetryAttributes(ex, attempt, "extract_subworkflow_specs"));
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "retrying");
                extractionSpan.Fail(ex);
                AddPipelineExtractionRetryTelemetry(ctx, attempt, maxAttempts, ex);
            }
            catch (Exception ex)
            {
                extractionSpan.AddEvent(
                    "gnougo-flow.plan.pipeline.extraction.error",
                    BuildPlanErrorTelemetryAttributes(ex, attempt, "extract_subworkflow_specs"));
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "failed");
                extractionSpan.Fail(ex);
                throw;
            }
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"workflow.plan pipeline extraction failed after {maxAttempts} attempt(s): {lastException?.Message ?? "unknown error"}",
            inner: lastException);
    }

    private static string BuildMarkExtractableBlocksPrompt(
        string normalizedMarkdown,
        string? pipelineMcpServersDoc,
        bool useStructuredExtraction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You annotate normalized automation Markdown for GnOuGo workflow generation.");
        if (useStructuredExtraction)
        {
            sb.AppendLine("Return ONLY JSON matching the requested structured output schema. Do not wrap the result in code fences.");
            sb.AppendLine("Put the complete annotated Markdown in `annotated_markdown`.");
            sb.AppendLine("Put one structured metadata entry in `subworkflows` for every annotated subworkflow block.");
        }
        else
        {
            sb.AppendLine("Return ONLY annotated Markdown. Do not wrap the result in code fences.");
        }
        sb.AppendLine();
        sb.Append($$"""

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
            - If MCP tools are relevant, use the global MCP tool context to make subworkflow inputs/outputs complete and coherent.
            - For a subworkflow that calls or prepares a tool call, include required request variables from the tool schema as inputs only when they must come from the caller or an upstream block.
            - If a required tool variable can be derived internally from semantic inputs, keep it inside the block content instead of exposing it as a public subworkflow input.
            - If a later block needs a documented tool response field, expose that field as an output of the producing block using the documented type.
            - Do not copy every MCP field into every block; include only the variables needed for that block boundary.
            - Do not use placeholders for missing required variables. If they are not derivable, make them explicit inputs.
            """);

        if (useStructuredExtraction)
        {
            sb.Append("""
            - Structured subworkflow metadata must repeat inputs and outputs as strongly typed entries with descriptions.
            - Structured output fields may declare object properties and array item types when later workflow steps need field-level access.
            - Structured `planned_tools` must list every MCP server tool or prompt this leaf is expected to call directly.
            - Mark planned tools as required when omitting that MCP call would violate the leaf goal.
            - For each relevant MCP tool or prompt, add a structured planned_tools entry with the exact server name, kind, method name, purpose, consumed fields, and produced fields.
            - Do not invent planned tools. Only use MCP servers, tools, and prompts documented in the global MCP tool context.
            - If no MCP tool or prompt is required for a leaf, use an empty planned_tools array.

            """);
        }

        sb.Append($$"""

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
            """);

        if (!string.IsNullOrWhiteSpace(pipelineMcpServersDoc))
        {
            sb.AppendLine();
            sb.AppendLine();
            AppendPromptSectionStart(sb, "pipeline_available_mcp_servers");
            sb.AppendLine("These MCP capabilities were selected from the complete pipeline request before subworkflow extraction.");
            sb.AppendLine("Use this context only to choose extraction boundaries and explicit input/output variables for leaf contracts.");
            sb.AppendLine("Tool schemas, output schemas, example responses, and capability cards are authoritative.");
            sb.AppendLine("Do not invent MCP servers, tools, request fields, response fields, or path conventions.");
            sb.AppendLine();
            sb.AppendLine(pipelineMcpServersDoc.Trim());
            AppendPromptSectionEnd(sb, "pipeline_available_mcp_servers");
        }

        return sb.ToString();
    }

    private static string BuildMarkExtractableBlocksRepairPrompt(
        string normalizedMarkdown,
        string? pipelineMcpServersDoc,
        string? previousAnnotatedMarkdown,
        IReadOnlyList<string> validationErrors,
        bool useStructuredExtraction)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildMarkExtractableBlocksPrompt(normalizedMarkdown, pipelineMcpServersDoc, useStructuredExtraction).TrimEnd());
        sb.AppendLine();
        sb.AppendLine("The previous `mark_extractable_blocks` response failed extraction validation.");
        sb.AppendLine(useStructuredExtraction
            ? "Return a complete corrected structured JSON response. Keep the original user intent and fix only the annotation and metadata shape."
            : "Return a complete corrected annotated Markdown document. Keep the original user intent and fix only the annotation shape.");
        sb.AppendLine();
        AppendPromptSectionStart(sb, "validation_errors");
        foreach (var error in validationErrors)
            sb.AppendLine("- " + error);
        AppendPromptSectionEnd(sb, "validation_errors");
        sb.AppendLine();
        AppendPromptSectionStart(sb, "correction_checklist");
        sb.AppendLine("- Every extracted block must open with exactly `:::subworkflow name=\"snake_case_name\"` and close with exactly `:::`.");
        sb.AppendLine("- Never nest `:::subworkflow` blocks.");
        sb.AppendLine("- Each block must include non-empty `goal:`, `inputs:`, `outputs:`, `extract_reason:`, and `content:` sections.");
        sb.AppendLine("- Each input and output line must be `identifier: type`; use explicit simple types such as string, number, boolean, array, object, or dictionary.");
        sb.AppendLine("- Block names and input/output names must be identifiers; block names must be snake_case and unique.");
        sb.AppendLine("- Block content must describe leaf logic only and must not mention calling another subworkflow.");
        if (useStructuredExtraction)
        {
            sb.AppendLine("- Every structured subworkflow metadata entry must match an annotated block name exactly.");
            sb.AppendLine("- Structured inputs and outputs must use names declared in the matching annotated block.");
            sb.AppendLine("- Structured planned_tools must use exact MCP server/tool/prompt names from the global MCP context.");
        }
        sb.AppendLine("- The document must include `## Main workflow orchestration` after the leaf blocks.");
        AppendPromptSectionEnd(sb, "correction_checklist");

        if (!string.IsNullOrWhiteSpace(previousAnnotatedMarkdown))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "invalid_annotated_markdown", previousAnnotatedMarkdown);
        }

        sb.AppendLine();
        sb.AppendLine(useStructuredExtraction
            ? "Fix the validation errors above and return ONLY the corrected JSON response."
            : "Fix the validation errors above and return ONLY the corrected annotated Markdown.");
        return sb.ToString();
    }

    private static WorkflowRuntimeException BuildPipelineExtractionException(
        IReadOnlyList<string> validationErrors,
        string? annotatedMarkdown)
    {
        var details = new JsonObject
        {
            ["validation"] = BuildValidationJson(validationErrors)
        };

        if (!string.IsNullOrWhiteSpace(annotatedMarkdown))
            details["invalid_annotated_markdown"] = annotatedMarkdown;

        return new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "workflow.plan pipeline extraction failed: " + string.Join("; ", validationErrors),
            details: details);
    }

    private static void AddPipelineExtractionRetryTelemetry(
        StepExecutionContext ctx,
        int attempt,
        int maxAttempts,
        Exception ex)
    {
        ctx.Engine.Logger.LogWarning(
            ex,
            "workflow.plan pipeline mark_extractable_blocks/extraction attempt {Attempt}/{MaxAttempts} failed, reprompting",
            attempt,
            maxAttempts);

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Pipeline extraction attempt {attempt}/{maxAttempts} failed; retrying mark_extractable_blocks with validation feedback."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
        });

        ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.extractable_blocks_retry", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
            new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAttempts),
            new KeyValuePair<string, object?>("error.type", ex.GetType().Name),
            new KeyValuePair<string, object?>("error.message", ex.Message)
        });
    }

    private static async Task<string> ExecutePipelineLlmTextPhaseAsync(
        ILLMClient llmClient,
        string phase,
        string prompt,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct,
        int? attempt = null,
        int? maxAttempts = null)
    {
        var spanAttributes = new List<KeyValuePair<string, object?>>
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gen_ai.request.background", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.background_requested", true)
        };
        if (attempt.HasValue)
            spanAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt.Value));
        if (maxAttempts.HasValue)
            spanAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAttempts.Value));

        using var span = ctx.BeginTelemetrySpan($"workflow.plan.pipeline.{phase}", phase, spanAttributes);

        if (ctx.Limits.LogStepContent)
        {
            var promptAttributes = new List<KeyValuePair<string, object?>>
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase)
            };
            if (attempt.HasValue)
                promptAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt.Value));
            span.AddEvent("gen_ai.content.prompt", promptAttributes);
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
                var completionAttributes = new List<KeyValuePair<string, object?>>
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase)
                };
                if (attempt.HasValue)
                    completionAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt.Value));
                span.AddEvent("gen_ai.content.completion", completionAttributes);
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

    private static async Task<LLMResponse> ExecutePipelineLlmStructuredPhaseAsync(
        ILLMClient llmClient,
        string phase,
        string prompt,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct,
        int? attempt,
        int? maxAttempts,
        JsonNode structuredOutputSchema)
    {
        var spanAttributes = new List<KeyValuePair<string, object?>>
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gen_ai.request.background", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.background_requested", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.structured_output", true)
        };
        if (attempt.HasValue)
            spanAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt.Value));
        if (maxAttempts.HasValue)
            spanAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAttempts.Value));

        using var span = ctx.BeginTelemetrySpan($"workflow.plan.pipeline.{phase}", phase, spanAttributes);

        if (ctx.Limits.LogStepContent)
        {
            var promptAttributes = new List<KeyValuePair<string, object?>>
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase)
            };
            if (attempt.HasValue)
                promptAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt.Value));
            span.AddEvent("gen_ai.content.prompt", promptAttributes);
        }

        try
        {
            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = structuredOutputSchema,
                StructuredOutputStrict = true
            }, ct);

            span.SetAttribute("gen_ai.response.model", model);
            span.SetAttribute("gen_ai.response.finish_reason", "stop");
            AddUsageAttributes(span, response.Usage, model, provider);

            if (ctx.Limits.LogStepContent)
            {
                var completion = !string.IsNullOrWhiteSpace(response.Text)
                    ? response.Text
                    : response.Json?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                if (!string.IsNullOrWhiteSpace(completion))
                {
                    var completionAttributes = new List<KeyValuePair<string, object?>>
                    {
                        new KeyValuePair<string, object?>("gen_ai.completion", completion),
                        new KeyValuePair<string, object?>("completion.role", "assistant"),
                        new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase)
                    };
                    if (attempt.HasValue)
                        completionAttributes.Add(new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt.Value));
                    span.AddEvent("gen_ai.content.completion", completionAttributes);
                }
            }

            if (response.Json == null && string.IsNullOrWhiteSpace(response.Text))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"workflow.plan pipeline phase '{phase}' returned empty structured output.");

            return response;
        }
        catch (Exception ex)
        {
            span.Fail(ex);
            throw;
        }
    }

    private static JsonNode BuildMarkExtractableBlocksStructuredOutputSchema() => JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["annotated_markdown", "subworkflows", "main_orchestration"],
          "properties": {
            "annotated_markdown": { "type": "string" },
            "main_orchestration": { "type": "string" },
            "subworkflows": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "goal", "description", "inputs", "outputs", "extract_reason", "content", "planned_tools"],
                "properties": {
                  "name": { "type": "string" },
                  "goal": { "type": "string" },
                  "description": { "type": "string" },
                  "inputs": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["name", "type", "description", "required", "item_type", "properties"],
                      "properties": {
                        "name": { "type": "string" },
                        "type": { "type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"] },
                        "description": { "type": "string" },
                        "required": { "type": "boolean" },
                        "item_type": { "type": "string" },
                        "properties": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["name", "type", "description", "required", "item_type"],
                            "properties": {
                              "name": { "type": "string" },
                              "type": { "type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"] },
                              "description": { "type": "string" },
                              "required": { "type": "boolean" },
                              "item_type": { "type": "string" }
                            }
                          }
                        }
                      }
                    }
                  },
                  "outputs": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["name", "type", "description", "required", "item_type", "properties"],
                      "properties": {
                        "name": { "type": "string" },
                        "type": { "type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"] },
                        "description": { "type": "string" },
                        "required": { "type": "boolean" },
                        "item_type": { "type": "string" },
                        "properties": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["name", "type", "description", "required", "item_type"],
                            "properties": {
                              "name": { "type": "string" },
                              "type": { "type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"] },
                              "description": { "type": "string" },
                              "required": { "type": "boolean" },
                              "item_type": { "type": "string" }
                            }
                          }
                        }
                      }
                    }
                  },
                  "extract_reason": { "type": "string" },
                  "content": { "type": "string" },
                  "planned_tools": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["server", "kind", "method", "required", "purpose", "consumes", "produces"],
                      "properties": {
                        "server": { "type": "string" },
                        "kind": { "type": "string", "enum": ["tool", "prompt"] },
                        "method": { "type": "string" },
                        "required": { "type": "boolean" },
                        "purpose": { "type": "string" },
                        "consumes": { "type": "array", "items": { "type": "string" } },
                        "produces": { "type": "array", "items": { "type": "string" } }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """)!;

    private static (string AnnotatedMarkdown, StructuredPipelineExtractionMetadata Metadata, IReadOnlyList<string> ValidationErrors)
        ParseMarkExtractableBlocksResponse(LLMResponse response, bool allowAnnotatedMarkdownFallback)
    {
        var validationErrors = new List<string>();
        JsonNode? root = response.Json?.DeepClone();
        var text = StripMarkdownFences(response.Text).Trim();

        if (root == null && LooksLikeJsonObject(text))
        {
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                validationErrors.Add($"mark_extractable_blocks returned invalid structured JSON: {ex.Message}");
            }
        }

        if (root is not JsonObject obj)
        {
            if (allowAnnotatedMarkdownFallback && !string.IsNullOrWhiteSpace(text))
                return (text, StructuredPipelineExtractionMetadata.Empty, validationErrors);

            validationErrors.Add("mark_extractable_blocks response must be structured JSON with annotated_markdown.");
            return ("", StructuredPipelineExtractionMetadata.Empty, validationErrors);
        }

        var annotatedMarkdown = GetStringProperty(obj, "annotated_markdown") ?? "";
        if (string.IsNullOrWhiteSpace(annotatedMarkdown))
            validationErrors.Add("Structured mark_extractable_blocks response must include non-empty annotated_markdown.");

        var subworkflows = new Dictionary<string, StructuredPipelineSubworkflowMetadata>(StringComparer.Ordinal);
        if (obj["subworkflows"] is not JsonArray subworkflowArray)
        {
            validationErrors.Add("Structured mark_extractable_blocks response must include subworkflows array.");
        }
        else
        {
            foreach (var node in subworkflowArray)
            {
                if (node is not JsonObject subworkflow)
                {
                    validationErrors.Add("Structured subworkflow metadata entry must be an object.");
                    continue;
                }

                var name = GetStringProperty(subworkflow, "name") ?? "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationErrors.Add("Structured subworkflow metadata entry is missing name.");
                    continue;
                }

                if (!subworkflows.TryAdd(name, ParseStructuredSubworkflowMetadata(subworkflow, validationErrors)))
                    validationErrors.Add($"Duplicate structured subworkflow metadata for '{name}'.");
            }
        }

        var metadata = new StructuredPipelineExtractionMetadata(
            subworkflows,
            GetStringProperty(obj, "main_orchestration"),
            IsStructuredResponse: true);
        return (annotatedMarkdown, metadata, validationErrors);
    }

    private static bool LooksLikeJsonObject(string text)
        => text.StartsWith('{') && text.EndsWith('}');

    private static StructuredPipelineSubworkflowMetadata ParseStructuredSubworkflowMetadata(
        JsonObject subworkflow,
        List<string> validationErrors)
    {
        var name = GetStringProperty(subworkflow, "name") ?? "";
        return new StructuredPipelineSubworkflowMetadata(
            name,
            GetStringProperty(subworkflow, "description"),
            ParseStructuredContractFields(subworkflow["inputs"] as JsonArray, name, "inputs", validationErrors),
            ParseStructuredContractFields(subworkflow["outputs"] as JsonArray, name, "outputs", validationErrors),
            ParseStructuredPlannedTools(subworkflow["planned_tools"] as JsonArray, name, validationErrors));
    }

    private static IReadOnlyDictionary<string, JsonNode?> ParseStructuredContractFields(
        JsonArray? fields,
        string subworkflowName,
        string section,
        List<string> validationErrors)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (fields == null)
            return result;

        foreach (var node in fields)
        {
            if (node is not JsonObject field)
            {
                validationErrors.Add($"Structured subworkflow '{subworkflowName}' {section} entry must be an object.");
                continue;
            }

            var name = GetStringProperty(field, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                validationErrors.Add($"Structured subworkflow '{subworkflowName}' has unnamed {section} entry.");
                continue;
            }

            if (!IdentifierRegex().IsMatch(name))
                validationErrors.Add($"Structured subworkflow '{subworkflowName}' {section} entry '{name}' must be an identifier.");

            result[name] = BuildStructuredFieldSchema(field);
        }

        return result;
    }

    private static JsonObject BuildStructuredFieldSchema(JsonObject field)
    {
        var type = NormalizeWorkflowSchemaType(GetStringProperty(field, "type") ?? "any");
        var schema = new JsonObject
        {
            ["type"] = type
        };

        if (GetStringProperty(field, "description") is { } description)
            schema["description"] = description;

        if (field["required"] is JsonValue requiredValue
            && requiredValue.TryGetValue<bool>(out var required))
        {
            schema["required"] = required;
        }

        var properties = BuildStructuredFieldProperties(field["properties"] as JsonArray);
        if (properties.Count > 0)
        {
            if (string.Equals(type, "array", StringComparison.Ordinal))
            {
                var itemType = NormalizeWorkflowSchemaType(GetStringProperty(field, "item_type") ?? "object");
                var items = new JsonObject
                {
                    ["type"] = string.Equals(itemType, "any", StringComparison.Ordinal) ? "object" : itemType
                };
                if (string.Equals(items["type"]?.GetValue<string>(), "object", StringComparison.Ordinal))
                    AddStructuredObjectProperties(items, properties);
                schema["items"] = items;
            }
            else
            {
                AddStructuredObjectProperties(schema, properties);
            }
        }
        else if (string.Equals(type, "array", StringComparison.Ordinal))
        {
            var itemType = NormalizeWorkflowSchemaType(GetStringProperty(field, "item_type") ?? "any");
            if (!string.Equals(itemType, "any", StringComparison.Ordinal))
            {
                schema["items"] = new JsonObject
                {
                    ["type"] = itemType
                };
            }
        }

        return schema;
    }

    private static List<(string Name, JsonObject Schema, bool Required)> BuildStructuredFieldProperties(JsonArray? properties)
    {
        var result = new List<(string Name, JsonObject Schema, bool Required)>();
        if (properties == null)
            return result;

        foreach (var node in properties)
        {
            if (node is not JsonObject property)
                continue;

            var name = GetStringProperty(property, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var type = NormalizeWorkflowSchemaType(GetStringProperty(property, "type") ?? "any");
            var schema = new JsonObject
            {
                ["type"] = type
            };
            if (GetStringProperty(property, "description") is { } description)
                schema["description"] = description;

            var itemType = NormalizeWorkflowSchemaType(GetStringProperty(property, "item_type") ?? "any");
            if (string.Equals(type, "array", StringComparison.Ordinal)
                && !string.Equals(itemType, "any", StringComparison.Ordinal))
            {
                schema["items"] = new JsonObject { ["type"] = itemType };
            }

            var required = property["required"] is JsonValue requiredValue
                           && requiredValue.TryGetValue<bool>(out var requiredBool)
                           && requiredBool;
            result.Add((name, schema, required));
        }

        return result;
    }

    private static void AddStructuredObjectProperties(
        JsonObject schema,
        IReadOnlyList<(string Name, JsonObject Schema, bool Required)> properties)
    {
        var propertiesObject = new JsonObject();
        var requiredProperties = new JsonArray();
        foreach (var (name, propertySchema, required) in properties)
        {
            propertiesObject[name] = propertySchema.DeepClone();
            if (required)
                requiredProperties.Add((JsonNode)JsonValue.Create(name)!);
        }

        schema["properties"] = propertiesObject;
        if (requiredProperties.Count > 0)
            schema["required_properties"] = requiredProperties;
    }

    private static IReadOnlyList<PipelinePlannedTool> ParseStructuredPlannedTools(
        JsonArray? tools,
        string subworkflowName,
        List<string> validationErrors)
    {
        var result = new List<PipelinePlannedTool>();
        if (tools == null)
            return result;

        foreach (var node in tools)
        {
            if (node is not JsonObject tool)
            {
                validationErrors.Add($"Structured subworkflow '{subworkflowName}' planned_tools entry must be an object.");
                continue;
            }

            var server = GetStringProperty(tool, "server") ?? "";
            var kind = GetStringProperty(tool, "kind") ?? "tool";
            var method = GetStringProperty(tool, "method") ?? "";
            var required = tool["required"] is JsonValue requiredValue
                           && requiredValue.TryGetValue<bool>(out var requiredBool)
                           && requiredBool;

            result.Add(new PipelinePlannedTool(
                server,
                kind,
                method,
                required,
                GetStringProperty(tool, "purpose"),
                GetStringArray(tool["consumes"] as JsonArray),
                GetStringArray(tool["produces"] as JsonArray)));
        }

        return result;
    }

    private static IReadOnlyList<string> GetStringArray(JsonArray? array)
    {
        if (array == null)
            return Array.Empty<string>();

        return array
            .Select(static node => node?.GetValue<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
    }

    private static WorkflowPipelineExtraction EnrichSubworkflowSpecsWithStructuredMetadata(
        WorkflowPipelineExtraction extraction,
        StructuredPipelineExtractionMetadata metadata,
        PipelineMcpContext pipelineMcpContext,
        IReadOnlyList<string> responseValidationErrors)
    {
        var validationErrors = extraction.ValidationErrors.Concat(responseValidationErrors).ToList();
        var enriched = new List<WorkflowPipelineSubworkflowSpec>();

        if (metadata.IsStructuredResponse)
        {
            var extractedNames = extraction.Subworkflows
                .Select(static spec => spec.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var metadataName in metadata.Subworkflows.Keys)
            {
                if (!extractedNames.Contains(metadataName))
                    validationErrors.Add($"Structured metadata references unknown subworkflow '{metadataName}'.");
            }
        }

        foreach (var spec in extraction.Subworkflows)
        {
            metadata.Subworkflows.TryGetValue(spec.Name, out var structured);
            if (metadata.IsStructuredResponse && structured == null)
                validationErrors.Add($"Structured metadata is missing subworkflow '{spec.Name}'.");

            if (structured != null)
                ValidateStructuredContractNames(spec, structured, validationErrors);

            var inputSchemas = MergeStructuredSchemas(spec.InputSchemas, structured?.Inputs);
            var outputSchemas = MergeStructuredSchemas(spec.OutputSchemas, structured?.Outputs);
            var plannedTools = structured?.PlannedTools ?? Array.Empty<PipelinePlannedTool>();
            ValidatePlannedToolsAgainstMcpContext(spec.Name, plannedTools, pipelineMcpContext, validationErrors);

            enriched.Add(spec with
            {
                Description = structured?.Description,
                InputSchemas = inputSchemas,
                OutputSchemas = outputSchemas,
                PlannedTools = plannedTools,
                GenerationPrompt = BuildSubworkflowGenerationPrompt(
                    spec.Name,
                    spec.Goal,
                    structured?.Description,
                    spec.Inputs,
                    spec.Outputs,
                    inputSchemas,
                    outputSchemas,
                    plannedTools,
                    spec.Content)
            });
        }

        return extraction with
        {
            Subworkflows = enriched,
            ValidationErrors = validationErrors
        };
    }

    private static IReadOnlyDictionary<string, JsonNode?> MergeStructuredSchemas(
        IReadOnlyDictionary<string, JsonNode?> fallback,
        IReadOnlyDictionary<string, JsonNode?>? structured)
    {
        if (structured == null || structured.Count == 0)
            return fallback;

        var merged = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, schema) in fallback)
            merged[name] = schema?.DeepClone();
        foreach (var (name, schema) in structured)
            merged[name] = schema?.DeepClone();
        return merged;
    }

    private static void ValidateStructuredContractNames(
        WorkflowPipelineSubworkflowSpec spec,
        StructuredPipelineSubworkflowMetadata structured,
        List<string> validationErrors)
    {
        foreach (var inputName in structured.Inputs.Keys)
        {
            if (!spec.Inputs.ContainsKey(inputName))
                validationErrors.Add($"Structured metadata for subworkflow '{spec.Name}' input '{inputName}' is not declared in the annotated Markdown inputs.");
        }

        foreach (var outputName in structured.Outputs.Keys)
        {
            if (!spec.Outputs.ContainsKey(outputName))
                validationErrors.Add($"Structured metadata for subworkflow '{spec.Name}' output '{outputName}' is not declared in the annotated Markdown outputs.");
        }
    }

    private static void ValidatePlannedToolsAgainstMcpContext(
        string subworkflowName,
        IReadOnlyList<PipelinePlannedTool> plannedTools,
        PipelineMcpContext pipelineMcpContext,
        List<string> validationErrors)
    {
        var canValidateCapabilities = pipelineMcpContext.Servers.Count > 0;
        foreach (var plannedTool in plannedTools)
        {
            if (string.IsNullOrWhiteSpace(plannedTool.Server))
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned tool is missing server.");
            if (string.IsNullOrWhiteSpace(plannedTool.Method))
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned tool is missing method.");
            if (plannedTool.Kind is not ("tool" or "prompt"))
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned tool '{plannedTool.Server}/{plannedTool.Method}' has invalid kind '{plannedTool.Kind}'.");

            if (!canValidateCapabilities)
                continue;

            var server = pipelineMcpContext.Servers.FirstOrDefault(server =>
                string.Equals(server.Name, plannedTool.Server, StringComparison.Ordinal));
            if (server == null)
            {
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned tool references unknown MCP server '{plannedTool.Server}'.");
                continue;
            }

            if (!server.Discovered)
            {
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned tool '{plannedTool.Server}/{plannedTool.Method}' cannot be verified because MCP server discovery was unavailable.");
                continue;
            }

            var exists = string.Equals(plannedTool.Kind, "prompt", StringComparison.Ordinal)
                ? server.Prompts.Any(prompt => string.Equals(prompt.Name, plannedTool.Method, StringComparison.Ordinal))
                : server.Tools.Any(tool => string.Equals(tool.Name, plannedTool.Method, StringComparison.Ordinal));
            if (!exists)
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned {plannedTool.Kind} '{plannedTool.Server}/{plannedTool.Method}' was not found in discovered MCP capabilities.");
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

        var inputSchemas = BuildSchemaMapFromSimpleTypes(inputs);
        var outputSchemas = BuildSchemaMapFromSimpleTypes(outputs);

        return new WorkflowPipelineSubworkflowSpec(
            name,
            goal,
            Description: null,
            inputs,
            outputs,
            inputSchemas,
            outputSchemas,
            Array.Empty<PipelinePlannedTool>(),
            extractReason,
            contentText,
            BuildSubworkflowGenerationPrompt(
                name,
                goal,
                description: null,
                inputs,
                outputs,
                inputSchemas,
                outputSchemas,
                Array.Empty<PipelinePlannedTool>(),
                contentText));
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
        string? description,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyDictionary<string, JsonNode?> inputSchemas,
        IReadOnlyDictionary<string, JsonNode?> outputSchemas,
        IReadOnlyList<PipelinePlannedTool> plannedTools,
        string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate exactly one leaf GnOuGo workflow named `{name}`.");
        sb.AppendLine($"Goal: {goal}");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"Description: {description}");
        sb.AppendLine();
        sb.AppendLine("Leaf workflow constraints:");
        sb.AppendLine("- Generate a complete YAML document with version, name, skill, and workflows.");
        sb.AppendLine($"- The document must contain exactly one workflow, preferably named `{name}`.");
        sb.AppendLine("- The workflow must be a leaf workflow.");
        sb.AppendLine("- Do not use workflow.call.");
        sb.AppendLine("- Do not use workflow.plan.");
        sb.AppendLine("- Do not depend on another subworkflow.");
        sb.AppendLine("- Treat the declared input/output contract as a draft when MCP tools require additional arguments.");
        sb.AppendLine("- Do not add required leaf inputs for generated internal target/destination paths or directories. Derive those inside the leaf from declared semantic inputs with a `set` step, then pass the derived value to internal tool requests and workflow outputs.");
        AppendMcpInputContractChecklist(sb);
        AppendExpressionFunctionRules(sb);
        sb.AppendLine("- Workflow outputs must match their declared contract type exactly. A string output must resolve to a string; a boolean output must resolve to a boolean.");
        sb.AppendLine("- Comparison/predicate expressions such as `${a == b}`, `${a != b}`, `${contains(...)}`, and `${exists(...)}` return boolean. Use them only for boolean outputs or `if`/`switch.when` conditions.");
        sb.AppendLine("- For string outputs such as classification/status/level/severity, return a string-valued field or quoted string literal. Invalid for a string output: `${data.steps.classify.json.classification == 'bug'}`. Valid: `${data.steps.classify.json.classification}`.");
        sb.AppendLine("- If a string output must be derived from an MCP/LLM response, first normalize it with `llm.call` or `mcp.call` `structured_output`, then map `data.steps.<normalizer>.json.<field>` to the workflow output.");
        AppendStructuredOutputStrictSchemaRules(sb);
        if (plannedTools.Count > 0)
        {
            sb.AppendLine("- Required planned MCP tools must appear as explicit direct `mcp.call` steps in this leaf.");
            sb.AppendLine("- For planned MCP tools, use exact `input.server`, `input.kind`, and literal `input.method`/`input.methods`; do not satisfy required planned tools through LLM-assisted MCP selection.");
        }
        sb.AppendLine("- If a step has an `if`, later unconditional steps must not reference that step directly. Either give the later step the same guard or create guaranteed branch outputs/default values first.");
        sb.AppendLine("- Function arguments are evaluated before the function runs. Do not hide unavailable step references inside `coalesce`, ternaries, or helper calls.");
        sb.AppendLine("- For MCP schemas, required numeric/integer/boolean request fields must be literal YAML scalars when the schema or validator requires explicit values; do not use expressions, casts, empty strings, or `data.env.*` fallbacks.");
        sb.AppendLine("- For MCP schemas, if a workspace-relative path/root/directory is documented as already existing, pass an exact data reference from a previous producer output or workflow input documented as that resource; do not construct it with literals or templates.");
        sb.AppendLine("- Any schema with `type: object` MUST be strongly typed with a non-empty `properties` mapping. Never generate a bare `type: object` input, output, item, or nested property.");
        sb.AppendLine("- For closed set output_schema objects or arrays, project exact declared fields before assigning custom-function results; do not pass opaque source objects through.");
        sb.AppendLine("- Any workflow output with `type: array` MUST be strongly typed with an `items` schema. Never generate an array output as a bare expression or bare `type: array` without `items`.");
        sb.AppendLine("- Array output `items` must use a concrete type. If items are objects, include every property the parent may need under `items.properties`.");
        sb.AppendLine("- Never duplicate the YAML key `required` in an object schema. Use `required: true|false` only for input-level requiredness, and use `required_properties: [field_name]` for required object property names.");
        sb.AppendLine();
        AppendContractSection(sb, "Inputs", inputs);
        AppendContractSection(sb, "Outputs", outputs);
        AppendStructuredContractSection(sb, "Structured input schemas", inputSchemas);
        AppendStructuredContractSection(sb, "Structured output schemas", outputSchemas);
        AppendPlannedToolsSection(sb, plannedTools);
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

    private static void AppendStructuredContractSection(
        StringBuilder sb,
        string title,
        IReadOnlyDictionary<string, JsonNode?> schemas)
    {
        sb.AppendLine();
        sb.AppendLine($"{title}:");
        if (schemas.Count == 0)
        {
            sb.AppendLine("{}");
            return;
        }

        sb.AppendLine(SerializeYamlMapping(schemas));
    }

    private static void AppendPlannedToolsSection(StringBuilder sb, IReadOnlyList<PipelinePlannedTool> plannedTools)
    {
        sb.AppendLine();
        sb.AppendLine("Planned MCP tools:");
        if (plannedTools.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var plannedTool in plannedTools)
        {
            var required = plannedTool.Required ? "required" : "optional";
            sb.AppendLine($"- {plannedTool.Server}/{plannedTool.Method} ({plannedTool.Kind}, {required})");
            if (!string.IsNullOrWhiteSpace(plannedTool.Purpose))
                sb.AppendLine($"  purpose: {plannedTool.Purpose}");
            if (plannedTool.Consumes.Count > 0)
                sb.AppendLine($"  consumes: {string.Join(", ", plannedTool.Consumes)}");
            if (plannedTool.Produces.Count > 0)
                sb.AppendLine($"  produces: {string.Join(", ", plannedTool.Produces)}");
        }
    }

    private static IReadOnlyDictionary<string, JsonNode?> BuildSchemaMapFromSimpleTypes(IReadOnlyDictionary<string, string> contract)
    {
        var schemas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, type) in contract)
        {
            schemas[name] = new JsonObject
            {
                ["type"] = NormalizeWorkflowSchemaType(type)
            };
        }

        return schemas;
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
        leafValidate["mode"] = "strict";
        leafValidate["compile"] = true;
        leafValidate["repair"] = "auto";
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
                var leafInput = BuildLeafPlanInput(
                    pipelineInput,
                    generator,
                    spec,
                    previousError,
                    previousYaml,
                    previousPrompt,
                    previousRepairContext);
                var leafPrompt = ((leafInput["generator"] as JsonObject)?["instruction"] as JsonValue)?.GetValue<string>();
                var leafCtx = new StepExecutionContext
                {
                    Step = parentCtx.Step,
                    Data = parentCtx.Data,
                    Engine = parentCtx.Engine,
                    Limits = parentCtx.Limits,
                    CallDepth = parentCtx.CallDepth,
                    CallStack = new HashSet<string>(parentCtx.CallStack),
                    ExecutionScope = parentCtx.ExecutionScope,
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
        var configured = TryGetPositiveInteger(pipelineInput["validate"] as JsonObject, "max_repair_attempts")
            ?? TryGetPositiveInteger(pipelineInput["on_invalid"] as JsonObject, "max_attempts")
            ?? DefaultPlanRepairMaxAttempts;
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
        sb.AppendLine("- If a required MCP request field is string/number/boolean, do not pass a nullable structured_output field into it; make the source non-null, refine it with assert.non_null, add an exact non-null step guard, or skip the mcp.call.");
        sb.AppendLine("- If a required MCP path/root/directory must already exist, pass an exact data reference from a previous producer output or workflow input documented as that resource; do not invent the path.");
        sb.AppendLine("- Do not keep generated target/destination path inputs in the leaf public contract. If a tool needs a target path to create, add a leaf-internal `set` step that derives it from declared semantic inputs.");
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

        if (policy["denied_step_types"] is JsonArray denied)
        {
            foreach (var stepType in PipelineMainSupportStepTypes)
            {
                if (!RemoveStepType(denied, stepType))
                    continue;

                ctx.Engine.Logger.LogWarning(
                    "workflow.plan pipeline mode may require step type {StepType} in the generated main workflow; removing it from denied_step_types for the pipeline parent workflow.",
                    stepType);
                ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.policy.warning", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.plan.policy.change", "removed_denied_step_type"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.step_type", stepType),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.reason", "pipeline main workflow calls leaf workflows and may use support nodes for orchestration/data shaping")
                });
            }
        }

        if (policy["allowed_step_types"] is JsonArray allowed)
        {
            foreach (var stepType in PipelineMainSupportStepTypes)
            {
                if (ContainsStepType(allowed, stepType))
                    continue;

                allowed.Add((JsonNode)JsonValue.Create(stepType)!);
                ctx.Engine.Logger.LogWarning(
                    "workflow.plan pipeline mode may require step type {StepType} in the generated main workflow; adding it to allowed_step_types for the pipeline parent workflow.",
                    stepType);
                ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.policy.warning", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.plan.policy.change", "added_allowed_step_type"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.step_type", stepType),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.reason", "pipeline main workflow calls leaf workflows and may use support nodes for orchestration/data shaping")
                });
            }
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
        EnforceStrongArrayOutputSchemas(spec.Name, spec, workflowName, doc);
        EnforceLeafDoesNotExposeGeneratedImplementationInputs(spec.Name, spec, workflowName, workflow);
        EnforcePlannedMcpToolsUsed(spec, workflow);
        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            if (step.Type is "workflow.call" or "workflow.plan")
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, $"Leaf workflow '{spec.Name}' must not contain step type '{step.Type}'.");
        }

        return new GeneratedLeafWorkflow(spec.Name, workflowName, doc, yaml);
    }

    private static void EnforcePlannedMcpToolsUsed(WorkflowPipelineSubworkflowSpec spec, WorkflowDef workflow)
    {
        var requiredTools = spec.PlannedTools
            .Where(static tool => tool.Required)
            .ToArray();
        if (requiredTools.Length == 0)
            return;

        var missing = requiredTools
            .Where(plannedTool => !WorkflowContainsPlannedMcpToolCall(workflow, plannedTool))
            .Select(static plannedTool => $"{plannedTool.Server}/{plannedTool.Method} ({plannedTool.Kind})")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' did not use required planned MCP tool(s): {string.Join(", ", missing)}. Add explicit direct mcp.call step(s) with matching input.server, input.kind, and literal input.method or input.methods.");
    }

    private static bool WorkflowContainsPlannedMcpToolCall(WorkflowDef workflow, PipelinePlannedTool plannedTool)
    {
        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            if (step.Type != "mcp.call" || step.Input is not JsonObject input)
                continue;

            var server = GetStringProperty(input, "server");
            if (!string.Equals(server, plannedTool.Server, StringComparison.Ordinal))
                continue;

            var kind = GetStringProperty(input, "kind") ?? "tool";
            if (!string.Equals(kind, plannedTool.Kind, StringComparison.Ordinal))
                continue;

            if (StringNodeEquals(input["method"], plannedTool.Method))
                return true;

            if (input["methods"] is JsonArray methods
                && methods.Any(method => StringNodeEquals(method, plannedTool.Method)))
                return true;
        }

        return false;
    }

    private static bool StringNodeEquals(JsonNode? node, string expected)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var actual))
            return false;

        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static void EnforceLeafDoesNotExposeGeneratedImplementationInputs(
        string leafName,
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDef workflow)
    {
        if (workflow.Inputs == null || workflow.Inputs.Count == 0)
            return;

        var leakedInputs = workflow.Inputs
            .Where(pair => !spec.Inputs.ContainsKey(pair.Key)
                           && pair.Value.Required
                           && LooksLikeGeneratedImplementationPathInput(pair.Key, pair.Value))
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (leakedInputs.Length == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{leafName}' exposes generated implementation input(s) not declared by its extracted leaf contract: {string.Join(", ", leakedInputs)}. Derive path/target/working-directory implementation values inside workflows.{workflowName} from declared leaf inputs, then pass the derived value to internal steps and outputs.");
    }

    private static bool LooksLikeGeneratedImplementationPathInput(string inputName, InputDef input)
    {
        if (!string.Equals(NormalizeWorkflowSchemaType(input.Type), "string", StringComparison.Ordinal))
            return false;

        var text = NormalizePipelineInputText(inputName + " " + input.Description);
        if (!HasPipelinePathSignal(text))
            return false;

        if (HasPipelineExistingResourceSignal(text))
            return false;

        return HasPipelineCreationTargetSignal(text)
               || LooksLikePipelineTargetPathName(inputName);
    }

    private static bool HasPipelinePathSignal(string text) =>
        text.Contains("path", StringComparison.Ordinal)
        || text.Contains("directory", StringComparison.Ordinal)
        || text.Contains("dir", StringComparison.Ordinal)
        || text.Contains("folder", StringComparison.Ordinal)
        || text.Contains("root", StringComparison.Ordinal)
        || text.Contains("workspace", StringComparison.Ordinal)
        || text.Contains("chemin", StringComparison.Ordinal)
        || text.Contains("dossier", StringComparison.Ordinal)
        || text.Contains("racine", StringComparison.Ordinal);

    private static bool HasPipelineExistingResourceSignal(string text) =>
        !HasPipelineNonExistingTargetSignal(text)
        && (text.Contains("existing", StringComparison.Ordinal)
            || text.Contains("already exists", StringComparison.Ordinal)
            || text.Contains("must exist", StringComparison.Ordinal)
            || text.Contains("previous step", StringComparison.Ordinal)
            || text.Contains("previous leaf", StringComparison.Ordinal)
            || text.Contains("previous producer", StringComparison.Ordinal)
            || text.Contains("existant", StringComparison.Ordinal)
            || text.Contains("existe deja", StringComparison.Ordinal));

    private static bool HasPipelineCreationTargetSignal(string text) =>
        text.Contains("target", StringComparison.Ordinal)
        || text.Contains("destination", StringComparison.Ordinal)
        || text.Contains("output", StringComparison.Ordinal)
        || text.Contains("empty", StringComparison.Ordinal)
        || HasPipelineNonExistingTargetSignal(text)
        || text.Contains("will be created", StringComparison.Ordinal)
        || text.Contains("create", StringComparison.Ordinal)
        || text.Contains("created", StringComparison.Ordinal)
        || text.Contains("where", StringComparison.Ordinal)
        || text.Contains("into", StringComparison.Ordinal)
        || text.Contains("destination", StringComparison.Ordinal)
        || text.Contains("cible", StringComparison.Ordinal)
        || text.Contains("creer", StringComparison.Ordinal)
        || text.Contains("cree", StringComparison.Ordinal);

    private static bool HasPipelineNonExistingTargetSignal(string text) =>
        text.Contains("non-existing", StringComparison.Ordinal)
        || text.Contains("non existing", StringComparison.Ordinal)
        || text.Contains("does not exist", StringComparison.Ordinal);

    private static bool LooksLikePipelineTargetPathName(string inputName)
    {
        var text = NormalizePipelineInputText(inputName);
        return (text.Contains("target", StringComparison.Ordinal)
                || text.Contains("destination", StringComparison.Ordinal)
                || text.Contains("output", StringComparison.Ordinal))
               && HasPipelinePathSignal(text);
    }

    private static string NormalizePipelineInputText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var chars = decomposed
            .Where(static c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c)
            .ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC);
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

    private static void EnforceStrongArrayOutputSchemas(
        string leafName,
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument doc)
    {
        var errors = new List<string>();

        if (doc.Skill?.Outputs != null)
        {
            foreach (var (name, output) in doc.Skill.Outputs)
                ValidateStrongArrayOutputSchema(output, $"skill.outputs.{name}", errors);
        }

        if (doc.Workflows.TryGetValue(workflowName, out var workflow) && workflow.Outputs != null)
        {
            foreach (var (name, output) in workflow.Outputs)
                ValidateStrongArrayOutputSchema(output, $"workflows.{workflowName}.outputs.{name}", errors);

            foreach (var (name, type) in spec.Outputs)
            {
                if (!string.Equals(NormalizeWorkflowSchemaType(type), "array", StringComparison.Ordinal))
                    continue;

                if (!workflow.Outputs.TryGetValue(name, out var output))
                {
                    errors.Add($"workflows.{workflowName}.outputs.{name} is missing but was declared as an array output in the extracted leaf contract");
                    continue;
                }

                if (!string.Equals(NormalizeWorkflowSchemaType(output.Type), "array", StringComparison.Ordinal))
                {
                    errors.Add($"workflows.{workflowName}.outputs.{name} was declared as an array output in the extracted leaf contract but the generated workflow output is not typed as array");
                    continue;
                }

                if (output.Items == null)
                    errors.Add($"workflows.{workflowName}.outputs.{name} has type array without items");
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Leaf workflow '{leafName}' uses weak array output schemas: {string.Join("; ", errors)}");
        }
    }

    private static void ValidateStrongArrayOutputSchema(OutputDef schema, string path, List<string> errors)
    {
        var type = NormalizeWorkflowSchemaType(schema.Type);
        if (string.Equals(type, "array", StringComparison.Ordinal))
        {
            if (schema.Items == null)
            {
                errors.Add($"{path} has type array without items");
            }
            else
            {
                var itemType = NormalizeWorkflowSchemaType(schema.Items.Type);
                if (string.Equals(itemType, "any", StringComparison.Ordinal))
                    errors.Add($"{path}.items has type any; choose a concrete item schema");

                ValidateStrongArrayOutputSchema(schema.Items, path + ".items", errors);
            }
        }

        if (schema.Properties != null)
        {
            foreach (var (name, child) in schema.Properties)
                ValidateStrongArrayOutputSchema(child, $"{path}.properties.{name}", errors);
        }

        if (schema.AdditionalProperties != null)
            ValidateStrongArrayOutputSchema(schema.AdditionalProperties, path + ".additional_properties", errors);
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
            AddYaml(workflowsNode, leaf.Name, ExtractSingleWorkflowNode(leaf.Yaml, leaf.GeneratedWorkflowName));

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
        PipelineLeafResourceManifest leafResourceManifest,
        IReadOnlyDictionary<string, JsonNode?> configuredMainInputs,
        IReadOnlyDictionary<string, JsonNode?> generatedLeafInputs,
        StepExecutorRegistry registry)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are assembling the parent `main` workflow graph for a GnOuGo.Flow pipeline.");
        sb.AppendLine("Return ONLY one YAML mapping with `document` and `graph` keys. Do not return version, entrypoint, workflows, a full `main` workflow, or leaf workflow definitions.");
        sb.AppendLine();
        sb.AppendLine("Hard rules:");
        sb.AppendLine("- Return a compact orchestration graph. The runtime will render the real `main` workflow and graft validated leaf workflows before final validation.");
        sb.AppendLine("- Graph call nodes must use `leaf: <leaf_name>` and `args`; do not write raw workflow.call refs.");
        sb.AppendLine("- Non-call support nodes may use normal step `type` and `input` when the main orchestration needs derived values, guards, switches, loops, or parallel branches.");
        sb.AppendLine("- The main workflow must never use `workflow.plan`, and graph nodes must not inline leaf logic.");
        sb.AppendLine("- Leaf workflows must never call other workflows.");
        sb.AppendLine("- Preserve the orchestration algorithm from the normalized prompt and the Main workflow orchestration section.");
        sb.AppendLine("- Use conditionals, switches, loops, or parallel branches when the orchestration requires them.");
        sb.AppendLine("- For container support nodes (`sequence`, `switch`, `parallel`, loops), nested graph nodes are allowed in `steps`, `branches[].steps`, `cases[].steps`, and `default`.");
        sb.AppendLine("- Pass leaf arguments from declared `data.inputs.<name>`, earlier step outputs, loop variables, derived values, or constants.");
        sb.AppendLine("- Every `data.inputs.<name>` reference MUST have an identically named declaration in `graph.inputs` or `document.skill.inputs`.");
        sb.AppendLine("- Leaf input names are call arguments, not automatically public main inputs.");
        sb.AppendLine("- `generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.");
        sb.AppendLine("- If `leaf_input_candidates_yaml` or `leaf_manifest_json` disagree with `generated_leaf_contracts_yaml`, follow `generated_leaf_contracts_yaml`.");
        sb.AppendLine("- Map public user input names to differently named leaf arguments when their meanings match.");
        sb.AppendLine("- Do not expose loop variables, intermediate values, paths, identifiers, flags, or leaf-only implementation details as public inputs unless the user explicitly requested them.");
        sb.AppendLine("- Use `set` support nodes for data shaping in the main graph: renaming fields, building objects/arrays, constants, and safe type conversions.");
        sb.AppendLine("- Keep exact JSON values intact when passing arrays, objects, numbers, or booleans. Do not stringify a structured leaf output unless a downstream leaf explicitly wants a string.");
        sb.AppendLine("- If a leaf call is inside a switch, loop, parallel branch, or conditional path, do not reference that leaf call step from outside that container/path. Put dependent work in the same path, or expose the container step itself as the output.");
        sb.AppendLine("- `leaf_resource_links_yaml` is authoritative for leaf inputs that require existing resources. For those inputs, pass an exact matching producer output reference; never construct the value with a literal, template, or fallback.");
        sb.AppendLine("- If a resource producer and consumer are inside a loop or switch branch, keep the consumer in the same runtime path after the producer and replace `<producer_call_id>` in the reference pattern with the actual producer call step id.");
        sb.AppendLine("- If `leaf_resource_links_yaml.missing_consumers` lists a consumer, do not invent a path. The producer leaf contract must be fixed before the main graph can validly satisfy that input.");
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
        AppendMainGraphDslContext(sb);
        AppendMainGraphSupportStepDslSnippets(sb, registry);
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
        AppendPromptSection(sb, "leaf_resource_links_yaml", BuildPipelineLeafResourceManifestYaml(leafResourceManifest));
        AppendPromptSection(sb, "leaf_manifest_json", BuildGeneratedLeafManifestJson(leaves, extraction).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
        sb.AppendLine("graph:");
        sb.AppendLine("  inputs:");
        sb.AppendLine("    user_query: string");
        sb.AppendLine("  steps:");
        sb.AppendLine("    - id: call_example_leaf");
        sb.AppendLine("      leaf: example_leaf");
        sb.AppendLine("      args:");
        sb.AppendLine("        query: ${data.inputs.user_query}");
        sb.AppendLine("  outputs:");
        sb.AppendLine("    result: ${data.steps.call_example_leaf.outputs.result}");
        return sb.ToString();
    }

    private static void AppendMainGraphSupportStepDslSnippets(StringBuilder sb, StepExecutorRegistry registry)
    {
        var allowedSupportTypes = PipelineMainSupportStepTypes
            .Where(static stepType => !string.Equals(stepType, "workflow.call", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var snippets = registry.GetDslSnippets(allowedSupportTypes)
            .Select(static snippet => snippet.Trim())
            .Where(static snippet => snippet.Length > 0)
            .ToArray();

        if (snippets.Length == 0)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("The snippets below are the real registered GnOuGo.Flow DSL references for support steps allowed in the compact main graph.");
        builder.AppendLine("Adapt each YAML example to graph nodes: keep `id`, `type`, `input`, `steps`, `branches`, `cases`, `default`, `item_var`, `index_var`, `if`, `retry`, `on_error`, and `output` exactly as the executor expects.");
        builder.AppendLine("For leaf calls, do not use the workflow.call snippet shape; use compact `leaf: <leaf_name>` plus `args`, and the runtime will render workflow.call.");
        builder.AppendLine("If a snippet uses `template.render` as a placeholder child step, replace that child with another allowed support node or a compact leaf call; never emit template.render, llm.call, mcp.call, human.input, workflow.call, or workflow.plan in the main graph.");
        builder.AppendLine();
        builder.AppendLine(string.Join("\n\n", snippets));

        AppendPromptSection(sb, "main_graph_allowed_support_step_dsl_snippets", builder.ToString().TrimEnd());
    }

    private static void AppendMainGraphDslContext(StringBuilder sb)
    {
        AppendPromptSection(sb, "main_graph_dsl_context", """
        The response is a compact graph, not a full GnOuGo workflow document.

        Graph root:
        graph:
          inputs:              # public main inputs; mapping name -> schema
            user_query: string # schema can be scalar type or a schema object
          steps: []            # ordered graph nodes
          outputs:             # public main outputs
            result: ${data.steps.call_leaf.outputs.result}

        Schemas:
        - Scalar schemas are allowed: string, number, boolean, object, array.
        - Object schemas may use type, description, required, default, properties, items, required_properties.
        - Array schemas must include `items` when item fields or item types matter. For leaf array outputs, use the `items` schema from `generated_leaf_contracts_yaml`.
        - Do not duplicate the YAML key `required`; use `required: true|false` for input requiredness and `required_properties` for object properties.

        Expressions:
        - Use ${data.inputs.<name>} only for names declared in graph.inputs or document.skill.inputs.
        - Use ${data.steps.<id>.<field>} only for earlier steps that always ran on the current path.
        - Leaf call outputs are under ${data.steps.<call_id>.outputs.<leaf_output_name>}.
        - set step outputs are under ${data.steps.<set_id>.<field>}.
        - For non-trivial set steps, declare step-level output_schema so downstream set fields have a strong contract.
        - If a custom function feeds a closed set output_schema, project exact declared fields; do not pass through whole source objects with possible extra properties.
        - Loop variables are data.<item_var>, data.<index_var>, data._loop.index, and data._loop.item.
        - When looping over a leaf array output, only read item fields declared under that output's `items.properties` schema in `generated_leaf_contracts_yaml`.
        - Do not hide unavailable/future step references inside coalesce, ternaries, or helper calls.
        - Useful built-ins: string(), toString(), toNumber(), json(), fromJson(), pick(), omit(), len(), length(), lower(), upper(), trim(), contains(), startsWith(), endsWith(), replace(), substring(), coalesce().
        - Exact expressions preserve the resolved JSON value. Use `${data.steps.call_leaf.outputs.items}` when the downstream leaf expects an array/object/number/boolean.
        - String templates produce strings. Use `"prefix-${data.inputs.id}"` only when the downstream leaf expects a string.
        - Predicates such as `${a == b}`, `${contains(...)}`, and `${exists(...)}` are booleans. Use them for `if`/`when` or boolean args/outputs only.
        - If a previous value may not exist because of an `if`, switch case, loop with zero iterations, or parallel branch isolation, do not reference it from a later unconditional node.

        Leaf call graph node:
        - id: call_leaf
          leaf: leaf_name
          args:
            leaf_input: ${data.inputs.public_input}
        Optional common fields on graph nodes: if, retry, on_error, output.
        Do not emit raw `type: workflow.call`; the runtime renders leaf nodes as local workflow.call steps.

        Support graph nodes may use only these DSL step types in the main graph:
        - set: derive constants or mappings.
        - sequence: run nested steps sequentially.
        - switch: choose one branch with cases[].steps and optional default.
        - parallel: run branches[].steps concurrently.
        - loop.sequential or loop.parallel: iterate with input.items or input.over and nested steps.

        Support node outputs and safe references:
        - set output: `${data.steps.<set_id>.<field>}`.
        - workflow.call output: `${data.steps.<call_id>.outputs.<leaf_output>}`.
        - sequence output: object keyed by nested step id; nested steps also execute in order on the same path.
        - parallel output: `${data.steps.<parallel_id>.branches}` is an array of branch step-output objects. Do not reference branch child step ids outside the branch.
        - loop output: `${data.steps.<loop_id>.results}` is an array of per-iteration step-output objects and `${data.steps.<loop_id>.count}` is the number of iterations. Do not reference loop child step ids after the loop.
        - loop result item shape: each element of `${data.steps.<loop_id>.results}` is a per-iteration step-output object. If a loop child step `build_issue_result` produced fields, read them as `iteration.build_issue_result.<field>` when flattening/filtering, not `iteration.<field>`.
        - To flatten loop results, add a `set` support node with an `output_schema` after the loop or use a typed child `set` inside the loop and map/filter through that child step id.
        - switch output is path-dependent. Do not reference case/default child step ids after the switch unless the reference remains inside that same case/default path.
        - For final graph.outputs after containers, prefer returning the whole safe container output, such as `${data.steps.process_items.results}` or `${data.steps.fanout.branches}`, rather than deep paths that may not exist on every path.

        set shape:
        - id: derive_values
          type: set
          input:
            normalized_query: ${data.inputs.user_query}
            fixed_limit: 20
            selected_fields:
              query: ${data.inputs.user_query}
              limit: 20

        sequence shape:
        - id: prepare
          type: sequence
          steps:
            - id: derive_values
              type: set
              input:
                normalized_query: ${data.inputs.user_query}

        switch shape:
        - id: route
          type: switch
          cases:
            - when: ${data.inputs.use_fast_path}
              steps:
                - id: call_fast_leaf
                  leaf: fast_leaf
                  args:
                    query: ${data.inputs.user_query}
          default:
            - id: call_default_leaf
              leaf: default_leaf
              args:
                query: ${data.inputs.user_query}

        parallel shape:
        - id: fanout
          type: parallel
          input:
            max_concurrency: 3
          branches:
            - steps:
                - id: call_first_leaf
                  leaf: first_leaf
                  args:
                    query: ${data.inputs.user_query}
            - steps:
                - id: call_second_leaf
                  leaf: second_leaf
                  args:
                    query: ${data.inputs.user_query}

        loop shape:
        - id: process_items
          type: loop.sequential
          input:
            items: ${data.steps.call_list_items.outputs.items}
          item_var: item
          index_var: index
          steps:
            - id: call_item_leaf
              leaf: item_leaf
              args:
                item: ${data.item}
                index: ${data.index}

        Main graph boundaries:
        - Keep business/tool/LLM work inside leaf workflows. The main graph should only orchestrate, derive values, branch, loop, and call leaves.
        - If a value is required by a generated leaf input contract, pass it in the leaf args or derive it in an earlier support step.
        - Do not add MCP, LLM, template, human-input, workflow.plan, or raw workflow.call support nodes to the main graph.
        """);
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
        sb.AppendLine("Return a complete corrected `document` and `graph` YAML mapping that still follows every rule above.");
        sb.AppendLine("Fix the reported error without changing the user's public contract or orchestration intent.");
        if (!string.IsNullOrWhiteSpace(previousResponse))
            AppendPromptSection(sb, "invalid_main_assembly_yaml", StripMarkdownFences(previousResponse));
        AppendPromptSection(sb, "main_assembly_validation_error", structuredError);
        return sb.ToString();
    }

    private static GeneratedMainAssembly ParseGeneratedMainAssembly(string yaml, IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var root = LoadYamlRoot(StripMarkdownFences(yaml));
        if (root.GetMapping("graph") is { } graph)
        {
            var graphDocument = root.GetMapping("document");
            return new GeneratedMainAssembly(
                BuildMainWorkflowNodeFromGraph(graph, leaves),
                graphDocument?.GetScalar("name"),
                CloneYamlMappingNodeOrNull(graphDocument?.GetMapping("skill")));
        }

        if (root.GetMapping("document") is { } document && root.GetMapping("main") is { } wrappedMain)
        {
            return new GeneratedMainAssembly(
                CloneYamlMappingNode(wrappedMain),
                document.GetScalar("name"),
                CloneYamlMappingNodeOrNull(document.GetMapping("skill")));
        }

        if (root.GetMapping("workflows") is { } workflows
            && workflows.Children.TryGetValue(Scalar("main"), out var nestedMain)
            && nestedMain is YamlMappingNode nestedMainMap)
        {
            return new GeneratedMainAssembly(
                CloneYamlMappingNode(nestedMainMap),
                root.GetScalar("name"),
                CloneYamlMappingNodeOrNull(root.GetMapping("skill")));
        }

        if (root.Children.TryGetValue(Scalar("main"), out var main)
            && main is YamlMappingNode mainMap)
        {
            return new GeneratedMainAssembly(
                CloneYamlMappingNode(mainMap),
                root.GetScalar("name"),
                CloneYamlMappingNodeOrNull(root.GetMapping("skill")));
        }

        return new GeneratedMainAssembly(CloneYamlMappingNode(root), null, null);
    }

    private static YamlMappingNode BuildMainWorkflowNodeFromGraph(
        YamlMappingNode graph,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var leafNames = leaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal);
        var main = new YamlMappingNode();

        if (graph.GetMapping("inputs") is { } inputs)
            AddYaml(main, "inputs", inputs);

        var sourceSteps = graph.GetSequence("steps") ?? graph.GetSequence("nodes");
        if (sourceSteps == null)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Pipeline orchestration graph must include steps or nodes.");

        AddYaml(main, "steps", RenderGraphStepSequence(sourceSteps, leafNames));

        if (graph.GetMapping("outputs") is { } outputs)
            AddYaml(main, "outputs", outputs);

        return main;
    }

    private static YamlSequenceNode RenderGraphStepSequence(YamlSequenceNode sourceSteps, IReadOnlySet<string> leafNames)
    {
        var rendered = new YamlSequenceNode();
        foreach (var sourceStep in sourceSteps.Children)
        {
            if (sourceStep is not YamlMappingNode step)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Pipeline orchestration graph steps must be mappings.");
            rendered.Add(RenderGraphStep(step, leafNames));
        }

        return rendered;
    }

    private static YamlMappingNode RenderGraphStep(YamlMappingNode graphStep, IReadOnlySet<string> leafNames)
    {
        var leafName = graphStep.GetScalar("leaf") ?? graphStep.GetScalar("workflow");
        if (!string.IsNullOrWhiteSpace(leafName))
            return RenderGraphLeafCallStep(graphStep, leafName, leafNames);

        var stepType = graphStep.GetScalar("type");
        if (string.IsNullOrWhiteSpace(stepType))
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Pipeline orchestration graph step must include either leaf or type.");
        if (string.Equals(stepType, "workflow.plan", StringComparison.Ordinal))
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, "Pipeline orchestration graph must not contain workflow.plan.");
        if (string.Equals(stepType, "workflow.call", StringComparison.Ordinal))
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, "Pipeline orchestration graph call nodes must use leaf and args, not raw workflow.call.");

        var rendered = CloneYamlMappingNode(graphStep);
        if (rendered.GetSequence("steps") is { } steps)
            ReplaceYaml(rendered, "steps", RenderGraphStepSequence(steps, leafNames));

        if (rendered.GetSequence("branches") is { } branches)
            ReplaceYaml(rendered, "branches", RenderGraphBranchSequence(branches, leafNames));

        if (rendered.GetSequence("cases") is { } cases)
            ReplaceYaml(rendered, "cases", RenderGraphCaseSequence(cases, leafNames));

        if (rendered.GetSequence("default") is { } defaultSteps)
            ReplaceYaml(rendered, "default", RenderGraphStepSequence(defaultSteps, leafNames));

        return rendered;
    }

    private static YamlMappingNode RenderGraphLeafCallStep(
        YamlMappingNode graphStep,
        string leafName,
        IReadOnlySet<string> leafNames)
    {
        if (!leafNames.Contains(leafName))
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Pipeline orchestration graph references unknown leaf workflow '{leafName}'.");

        var step = new YamlMappingNode();
        AddYaml(step, "id", Scalar(graphStep.GetScalar("id") ?? $"call_{leafName}"));
        AddYaml(step, "type", Scalar("workflow.call"));

        foreach (var commonField in new[] { "if", "retry", "on_error", "output" })
        {
            if (TryGetYaml(graphStep, commonField, out var commonValue))
                AddYaml(step, commonField, commonValue);
        }

        var input = new YamlMappingNode();
        var refNode = new YamlMappingNode();
        AddYaml(refNode, "kind", Scalar("local"));
        AddYaml(refNode, "name", Scalar(leafName));
        AddYaml(input, "ref", refNode);
        AddYaml(input, "args", graphStep.GetMapping("args") ?? new YamlMappingNode());
        AddYaml(step, "input", input);
        return step;
    }

    private static YamlSequenceNode RenderGraphBranchSequence(YamlSequenceNode sourceBranches, IReadOnlySet<string> leafNames)
    {
        var rendered = new YamlSequenceNode();
        foreach (var sourceBranch in sourceBranches.Children)
        {
            if (sourceBranch is not YamlMappingNode branch)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Pipeline orchestration graph branches must be mappings.");
            var renderedBranch = CloneYamlMappingNode(branch);
            if (renderedBranch.GetSequence("steps") is { } steps)
                ReplaceYaml(renderedBranch, "steps", RenderGraphStepSequence(steps, leafNames));
            rendered.Add(renderedBranch);
        }

        return rendered;
    }

    private static YamlSequenceNode RenderGraphCaseSequence(YamlSequenceNode sourceCases, IReadOnlySet<string> leafNames)
    {
        var rendered = new YamlSequenceNode();
        foreach (var sourceCase in sourceCases.Children)
        {
            if (sourceCase is not YamlMappingNode @case)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Pipeline orchestration graph cases must be mappings.");
            var renderedCase = CloneYamlMappingNode(@case);
            if (renderedCase.GetSequence("steps") is { } steps)
                ReplaceYaml(renderedCase, "steps", RenderGraphStepSequence(steps, leafNames));
            rendered.Add(renderedCase);
        }

        return rendered;
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

        AddYaml(mainWorkflowNode, "outputs", BuildDefaultMainOutputs(mainWorkflowNode, specs));
    }

    private static YamlMappingNode BuildDefaultMainOutputs(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var outputs = new YamlMappingNode();
        var topLevelSteps = mainWorkflowNode.GetSequence("steps");
        if (topLevelSteps != null)
        {
            foreach (var call in EnumerateTopLevelWorkflowCalls(topLevelSteps))
            {
                var key = BuildUniqueYamlKey(outputs, call.LeafName + "_outputs");
                AddYaml(outputs, key, Scalar($"${{data.steps.{call.StepId}.outputs}}"));
            }

            if (outputs.Children.Count > 0)
                return outputs;

            foreach (var step in topLevelSteps.Children.OfType<YamlMappingNode>())
            {
                var stepId = step.GetScalar("id");
                if (string.IsNullOrWhiteSpace(stepId))
                    continue;

                var key = BuildUniqueYamlKey(outputs, stepId + "_output");
                AddYaml(outputs, key, Scalar($"${{data.steps.{stepId}}}"));
            }

            if (outputs.Children.Count > 0)
                return outputs;
        }

        foreach (var spec in specs)
        {
            var fallbackStepId = "call_" + spec.Name;
            var key = BuildUniqueYamlKey(outputs, spec.Name + "_outputs");
            AddYaml(outputs, key, Scalar($"${{data.steps.{fallbackStepId}.outputs}}"));
        }

        return outputs;
    }

    private static IEnumerable<(string StepId, string LeafName)> EnumerateTopLevelWorkflowCalls(YamlSequenceNode steps)
    {
        foreach (var step in steps.Children.OfType<YamlMappingNode>())
        {
            if (!string.Equals(step.GetScalar("type"), "workflow.call", StringComparison.Ordinal))
                continue;

            var stepId = step.GetScalar("id");
            var leafName = step.GetMapping("input")?.GetMapping("ref")?.GetScalar("name");
            if (string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(leafName))
                continue;

            yield return (stepId, leafName);
        }
    }

    private static string BuildUniqueYamlKey(YamlMappingNode node, string requestedKey)
    {
        if (!ContainsYamlKey(node, requestedKey))
            return requestedKey;

        var index = 2;
        while (ContainsYamlKey(node, requestedKey + "_" + index))
            index++;

        return requestedKey + "_" + index;
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
            AddYaml(contract, "workflow", Scalar(leaf.Name));
            AddYaml(contract, "generated_workflow", Scalar(leaf.GeneratedWorkflowName));
            AddYaml(contract, "inputs", BuildYamlSchemaMap(BuildLeafInputSchemaMap(leaf)));
            AddYaml(contract, "outputs", BuildYamlSchemaMap(BuildLeafOutputSchemaMap(leaf)));
            AddYaml(root, leaf.Name, contract);
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static PipelineLeafResourceManifest BuildPipelineLeafResourceManifest(IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var producers = new List<PipelineLeafResourceProducer>();
        var consumers = new List<PipelineLeafResourceConsumer>();
        var nearMisses = new List<PipelineLeafResourceNearMiss>();

        foreach (var leaf in leaves)
        {
            var workflow = GetGeneratedLeafWorkflow(leaf);
            if (workflow?.Inputs != null)
            {
                foreach (var (inputName, inputDef) in workflow.Inputs)
                {
                    if (WorkflowResourceInference.InferRequiredResourceKind(inputName, inputDef) is not { } kind)
                        continue;

                    consumers.Add(new PipelineLeafResourceConsumer(
                        leaf.Name,
                        inputName,
                        kind,
                        inputDef.Description));
                }
            }

            if (workflow?.Outputs != null)
            {
                foreach (var (outputName, outputDef) in workflow.Outputs)
                {
                    var referencePattern = $"${{data.steps.<producer_call_id>.outputs.{outputName}}}";
                    if (WorkflowResourceInference.InferProducedResourceKind(outputName, outputDef) is { } kind)
                    {
                        producers.Add(new PipelineLeafResourceProducer(
                            leaf.Name,
                            outputName,
                            kind,
                            referencePattern,
                            outputDef.Description));
                        continue;
                    }

                    if (WorkflowResourceInference.LooksPathLikeButUnproven(outputName, outputDef.Description))
                    {
                        nearMisses.Add(new PipelineLeafResourceNearMiss(
                            leaf.Name,
                            outputName,
                            WorkflowResourceInference.ExistingWorkspaceRelativePathResource,
                            referencePattern,
                            outputDef.Description,
                            "path-like workspace-relative output, but the contract does not prove that the resource already exists or was produced by the leaf"));
                    }
                }
            }
        }

        var links = new List<PipelineLeafResourceLink>();
        foreach (var consumer in consumers)
        {
            foreach (var producer in producers.Where(producer => string.Equals(producer.Kind, consumer.Kind, StringComparison.Ordinal)))
            {
                links.Add(new PipelineLeafResourceLink(
                    consumer.Leaf,
                    consumer.Input,
                    consumer.Kind,
                    producer.Leaf,
                    producer.Output,
                    producer.ReferencePattern));
            }
        }

        var missingConsumers = consumers
            .Where(consumer => !producers.Any(producer => string.Equals(producer.Kind, consumer.Kind, StringComparison.Ordinal)))
            .ToArray();

        return new PipelineLeafResourceManifest(
            producers,
            consumers,
            links,
            missingConsumers,
            nearMisses);
    }

    private static void ValidatePipelineLeafResourceManifest(
        PipelineLeafResourceManifest manifest,
        IReadOnlyDictionary<string, JsonNode?> configuredMainInputs)
    {
        if (manifest.MissingConsumers.Count == 0)
            return;

        var configuredResourceKinds = InferConfiguredMainInputResourceKinds(configuredMainInputs);
        var nearMissesByKind = manifest.NearMisses
            .GroupBy(static nearMiss => nearMiss.Kind, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var actionableMissing = manifest.MissingConsumers
            .Where(consumer => !configuredResourceKinds.Contains(consumer.Kind))
            .Where(consumer => nearMissesByKind.ContainsKey(consumer.Kind))
            .ToArray();
        if (actionableMissing.Length == 0)
            return;

        var missingText = string.Join(
            "; ",
            actionableMissing.Select(static consumer => $"{consumer.Leaf}.{consumer.Input} requires {consumer.Kind}"));
        var details = new JsonObject
        {
            ["resource_links"] = BuildPipelineLeafResourceManifestJson(manifest),
            ["missing_consumers"] = new JsonArray(actionableMissing
                .Select(static consumer => (JsonNode)new JsonObject
                {
                    ["leaf"] = consumer.Leaf,
                    ["input"] = consumer.Input,
                    ["kind"] = consumer.Kind,
                    ["description"] = consumer.Description
                })
                .ToArray()),
            ["near_miss_producers"] = new JsonArray(manifest.NearMisses
                .Select(static nearMiss => (JsonNode)new JsonObject
                {
                    ["leaf"] = nearMiss.Leaf,
                    ["output"] = nearMiss.Output,
                    ["kind"] = nearMiss.Kind,
                    ["reference_pattern"] = nearMiss.ReferencePattern,
                    ["description"] = nearMiss.Description,
                    ["reason"] = nearMiss.Reason
                })
                .ToArray())
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline leaf resource contracts are inconsistent before main assembly: "
            + missingText
            + ". A path-like leaf output exists, but it is not proven as an existing resource producer. Regenerate or repair the producer leaf so its output is backed by a real producer contract.",
            details: details);
    }

    private static HashSet<string> InferConfiguredMainInputResourceKinds(IReadOnlyDictionary<string, JsonNode?> configuredMainInputs)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, schema) in configuredMainInputs)
        {
            if (InferConfiguredMainInputResourceKind(name, schema) is { } kind)
                kinds.Add(kind);
        }

        return kinds;
    }

    private static string? InferConfiguredMainInputResourceKind(string inputName, JsonNode? schema)
    {
        if (schema is JsonValue value
            && value.TryGetValue<string>(out var shortType)
            && string.Equals(shortType, "string", StringComparison.OrdinalIgnoreCase)
            && WorkflowResourceInference.LooksLikeRequiredExistingWorkspaceRelativePath(inputName, null))
        {
            return WorkflowResourceInference.ExistingWorkspaceRelativePathResource;
        }

        if (schema is not JsonObject obj)
            return null;

        var type = obj["type"]?.GetValue<string>();
        if (!string.Equals(type, "string", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "any", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var description = obj["description"]?.GetValue<string>();
        return WorkflowResourceInference.LooksLikeRequiredExistingWorkspaceRelativePath(inputName, description)
            ? WorkflowResourceInference.ExistingWorkspaceRelativePathResource
            : null;
    }

    private static string BuildPipelineLeafResourceManifestYaml(PipelineLeafResourceManifest manifest)
    {
        var root = new YamlMappingNode();
        AddYaml(root, "resource_producers", BuildPipelineResourceProducerSequence(manifest.Producers));
        AddYaml(root, "resource_consumers", BuildPipelineResourceConsumerSequence(manifest.Consumers));
        AddYaml(root, "resource_links", BuildPipelineResourceLinkSequence(manifest.Links));
        AddYaml(root, "missing_consumers", BuildPipelineResourceConsumerSequence(manifest.MissingConsumers));
        AddYaml(root, "near_miss_producers", BuildPipelineResourceNearMissSequence(manifest.NearMisses));

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static JsonObject BuildPipelineLeafResourceManifestJson(PipelineLeafResourceManifest manifest)
        => new()
        {
            ["resource_producers"] = new JsonArray(manifest.Producers
                .Select(static producer => (JsonNode)new JsonObject
                {
                    ["leaf"] = producer.Leaf,
                    ["output"] = producer.Output,
                    ["kind"] = producer.Kind,
                    ["reference_pattern"] = producer.ReferencePattern,
                    ["description"] = producer.Description
                })
                .ToArray()),
            ["resource_consumers"] = new JsonArray(manifest.Consumers
                .Select(static consumer => (JsonNode)new JsonObject
                {
                    ["leaf"] = consumer.Leaf,
                    ["input"] = consumer.Input,
                    ["kind"] = consumer.Kind,
                    ["description"] = consumer.Description
                })
                .ToArray()),
            ["resource_links"] = new JsonArray(manifest.Links
                .Select(static link => (JsonNode)new JsonObject
                {
                    ["consumer_leaf"] = link.ConsumerLeaf,
                    ["consumer_input"] = link.ConsumerInput,
                    ["kind"] = link.Kind,
                    ["producer_leaf"] = link.ProducerLeaf,
                    ["producer_output"] = link.ProducerOutput,
                    ["reference_pattern"] = link.ReferencePattern
                })
                .ToArray()),
            ["missing_consumers"] = new JsonArray(manifest.MissingConsumers
                .Select(static consumer => (JsonNode)new JsonObject
                {
                    ["leaf"] = consumer.Leaf,
                    ["input"] = consumer.Input,
                    ["kind"] = consumer.Kind,
                    ["description"] = consumer.Description
                })
                .ToArray()),
            ["near_miss_producers"] = new JsonArray(manifest.NearMisses
                .Select(static nearMiss => (JsonNode)new JsonObject
                {
                    ["leaf"] = nearMiss.Leaf,
                    ["output"] = nearMiss.Output,
                    ["kind"] = nearMiss.Kind,
                    ["reference_pattern"] = nearMiss.ReferencePattern,
                    ["description"] = nearMiss.Description,
                    ["reason"] = nearMiss.Reason
                })
                .ToArray())
        };

    private static YamlSequenceNode BuildPipelineResourceProducerSequence(IReadOnlyList<PipelineLeafResourceProducer> producers)
    {
        var seq = new YamlSequenceNode();
        foreach (var producer in producers)
        {
            var item = new YamlMappingNode();
            AddYaml(item, "leaf", Scalar(producer.Leaf));
            AddYaml(item, "output", Scalar(producer.Output));
            AddYaml(item, "kind", Scalar(producer.Kind));
            AddYaml(item, "reference_pattern", Scalar(producer.ReferencePattern));
            if (!string.IsNullOrWhiteSpace(producer.Description))
                AddYaml(item, "description", Scalar(producer.Description));
            seq.Add(item);
        }

        return seq;
    }

    private static YamlSequenceNode BuildPipelineResourceConsumerSequence(IReadOnlyList<PipelineLeafResourceConsumer> consumers)
    {
        var seq = new YamlSequenceNode();
        foreach (var consumer in consumers)
        {
            var item = new YamlMappingNode();
            AddYaml(item, "leaf", Scalar(consumer.Leaf));
            AddYaml(item, "input", Scalar(consumer.Input));
            AddYaml(item, "kind", Scalar(consumer.Kind));
            if (!string.IsNullOrWhiteSpace(consumer.Description))
                AddYaml(item, "description", Scalar(consumer.Description));
            seq.Add(item);
        }

        return seq;
    }

    private static YamlSequenceNode BuildPipelineResourceLinkSequence(IReadOnlyList<PipelineLeafResourceLink> links)
    {
        var seq = new YamlSequenceNode();
        foreach (var link in links)
        {
            var item = new YamlMappingNode();
            AddYaml(item, "consumer_leaf", Scalar(link.ConsumerLeaf));
            AddYaml(item, "consumer_input", Scalar(link.ConsumerInput));
            AddYaml(item, "kind", Scalar(link.Kind));
            AddYaml(item, "producer_leaf", Scalar(link.ProducerLeaf));
            AddYaml(item, "producer_output", Scalar(link.ProducerOutput));
            AddYaml(item, "reference_pattern", Scalar(link.ReferencePattern));
            seq.Add(item);
        }

        return seq;
    }

    private static YamlSequenceNode BuildPipelineResourceNearMissSequence(IReadOnlyList<PipelineLeafResourceNearMiss> nearMisses)
    {
        var seq = new YamlSequenceNode();
        foreach (var nearMiss in nearMisses)
        {
            var item = new YamlMappingNode();
            AddYaml(item, "leaf", Scalar(nearMiss.Leaf));
            AddYaml(item, "output", Scalar(nearMiss.Output));
            AddYaml(item, "kind", Scalar(nearMiss.Kind));
            AddYaml(item, "reference_pattern", Scalar(nearMiss.ReferencePattern));
            if (!string.IsNullOrWhiteSpace(nearMiss.Description))
                AddYaml(item, "description", Scalar(nearMiss.Description));
            AddYaml(item, "reason", Scalar(nearMiss.Reason));
            seq.Add(item);
        }

        return seq;
    }

    private static JsonObject BuildGeneratedLeafManifestJson(
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        WorkflowPipelineExtraction extraction)
    {
        var specsByName = extraction.Subworkflows.ToDictionary(static spec => spec.Name, StringComparer.Ordinal);
        var leafArray = new JsonArray();
        foreach (var leaf in leaves)
        {
            specsByName.TryGetValue(leaf.Name, out var spec);
            leafArray.Add((JsonNode)new JsonObject
            {
                ["name"] = leaf.Name,
                ["workflow"] = leaf.Name,
                ["goal"] = spec?.Goal ?? "",
                ["description"] = spec?.Description,
                ["extract_reason"] = spec?.ExtractReason ?? "",
                ["planned_tools"] = spec == null ? new JsonArray() : BuildPlannedToolsJson(spec.PlannedTools),
                ["inputs"] = BuildSchemaMapJson(BuildLeafInputSchemaMap(leaf)),
                ["outputs"] = BuildSchemaMapJson(BuildLeafOutputSchemaMap(leaf))
            });
        }

        return new JsonObject
        {
            ["leaves"] = leafArray,
            ["main_workflow_prompt"] = extraction.MainWorkflowPrompt
        };
    }

    private static JsonObject BuildSchemaMapJson(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in values)
            obj[name] = value?.DeepClone();
        return obj;
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

    private static Dictionary<string, JsonNode?> BuildLeafInputSchemaMap(GeneratedLeafWorkflow leaf)
    {
        var workflow = GetGeneratedLeafWorkflow(leaf);
        return workflow?.Inputs != null
            ? BuildInputSchemaMap(workflow.Inputs)
            : ReadYamlSchemaMap(ExtractSingleWorkflowNode(leaf.Yaml, leaf.GeneratedWorkflowName).GetMapping("inputs"));
    }

    private static Dictionary<string, JsonNode?> BuildLeafOutputSchemaMap(GeneratedLeafWorkflow leaf)
    {
        var workflow = GetGeneratedLeafWorkflow(leaf);
        return workflow?.Outputs != null
            ? BuildOutputSchemaMap(workflow.Outputs, leaf.Document.Skill?.Outputs)
            : ReadYamlSchemaMap(ExtractSingleWorkflowNode(leaf.Yaml, leaf.GeneratedWorkflowName).GetMapping("outputs"));
    }

    private static WorkflowDef? GetGeneratedLeafWorkflow(GeneratedLeafWorkflow leaf)
        => leaf.Document.Workflows.TryGetValue(leaf.GeneratedWorkflowName, out var workflow)
            ? workflow
            : null;

    private static Dictionary<string, JsonNode?> BuildInputSchemaMap(IReadOnlyDictionary<string, InputDef> inputs)
    {
        var schemas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, schema) in inputs)
            schemas[name] = InputDefToContractNode(schema);
        return schemas;
    }

    private static Dictionary<string, JsonNode?> BuildOutputSchemaMap(
        IReadOnlyDictionary<string, OutputDef> outputs,
        IReadOnlyDictionary<string, OutputDef>? skillOutputs)
    {
        var schemas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, schema) in outputs)
        {
            var contractSchema = schema;
            if (IsOpaqueOutputSchema(schema)
                && skillOutputs != null
                && skillOutputs.TryGetValue(name, out var skillSchema)
                && !IsOpaqueOutputSchema(skillSchema))
            {
                contractSchema = skillSchema;
            }

            schemas[name] = OutputDefToContractNode(contractSchema);
        }
        return schemas;
    }

    private static JsonNode InputDefToContractNode(InputDef schema)
    {
        var node = FlowTypeDescriptorConverter.ToWorkflowContractNode(
            FlowTypeDescriptorConverter.FromInputDef(schema),
            inputStyle: true,
            allowScalarShortForm: schema.Required);

        if (!schema.Required)
        {
            if (node is not JsonObject obj)
            {
                obj = new JsonObject { ["type"] = NormalizeWorkflowSchemaType(schema.Type) };
                node = obj;
            }

            obj["required"] = false;
        }

        return node;
    }

    private static JsonNode OutputDefToContractNode(OutputDef schema)
        => FlowTypeDescriptorConverter.ToWorkflowContractNode(
            FlowTypeDescriptorConverter.FromOutputDef(schema),
            inputStyle: false);

    private static bool IsOpaqueOutputSchema(OutputDef schema)
        => string.Equals(NormalizeWorkflowSchemaType(schema.Type), "any", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(schema.Description)
            && schema.Items == null
            && schema.Properties == null
            && schema.AdditionalProperties == null
            && schema.RequiredProperties is not { Count: > 0 };

    private static YamlMappingNode BuildYamlSchemaMap(IReadOnlyDictionary<string, JsonNode?> schemas)
    {
        var map = new YamlMappingNode();
        foreach (var (name, schema) in schemas)
            AddYaml(map, name, JsonToYaml(schema));
        return map;
    }

    private static void ValidateDeclaredMainInputReferences(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, JsonNode?> mainInputs)
    {
        var stream = new YamlStream(new YamlDocument(CloneYamlMappingNode(mainWorkflowNode)));
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
            var leafInputs = BuildLeafInputSchemaMap(leaf);
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

            foreach (var outputName in BuildLeafOutputSchemaMap(leaf).Keys)
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

    private static bool TryGetYaml(YamlMappingNode node, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YamlNode? value)
    {
        if (node.Children.TryGetValue(Scalar(key), out value))
            return true;

        value = null;
        return false;
    }

    private static void ReplaceYaml(YamlMappingNode node, string key, YamlNode value)
    {
        node.Children.Remove(Scalar(key));
        AddYaml(node, key, value);
    }

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

    private static void ValidatePipelineLeafCallArguments(WorkflowDocument doc, IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        if (!doc.Workflows.TryGetValue("main", out var main))
            return;

        var requiredInputsByLeaf = leaves.ToDictionary(
            static leaf => leaf.Name,
            static leaf => BuildLeafInputSchemaMap(leaf)
                .Where(static pair => IsRequiredLeafInput(pair.Value))
                .Select(static pair => pair.Key)
                .ToArray(),
            StringComparer.Ordinal);

        foreach (var step in EnumerateSteps(main.Steps))
        {
            if (step.Type != "workflow.call" || step.Input is not JsonObject input)
                continue;

            var refObj = input["ref"] as JsonObject;
            var targetName = refObj?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(targetName) || !requiredInputsByLeaf.TryGetValue(targetName, out var requiredInputs))
                continue;

            var args = input["args"] as JsonObject;
            var missing = requiredInputs
                .Where(inputName => args == null || !args.ContainsKey(inputName))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length == 0)
                continue;

            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Pipeline main workflow call '{step.Id}' to leaf '{targetName}' is missing required leaf argument(s): {string.Join(", ", missing)}");
        }
    }

    private static bool IsRequiredLeafInput(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return true;

        if (obj["required"] is JsonValue requiredValue
            && requiredValue.TryGetValue<bool>(out var required)
            && !required)
            return false;

        if (obj.ContainsKey("default"))
            return false;

        return true;
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

        var clonedWorkflow = CloneYamlMappingNode(workflowMapping);
        var documentFunctions = root.GetScalar("functions");
        if (!string.IsNullOrWhiteSpace(documentFunctions))
        {
            var workflowFunctions = clonedWorkflow.GetScalar("functions");
            var mergedFunctions = string.IsNullOrWhiteSpace(workflowFunctions)
                ? documentFunctions.TrimEnd()
                : documentFunctions.TrimEnd() + "\n\n" + workflowFunctions.TrimStart();
            ReplaceYaml(clonedWorkflow, "functions", LiteralScalar(mergedFunctions));
        }

        return clonedWorkflow;
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
            ["description"] = spec.Description,
            ["inputs"] = BuildStringMapJson(spec.Inputs),
            ["outputs"] = BuildStringMapJson(spec.Outputs),
            ["input_schemas"] = BuildSchemaMapJson(spec.InputSchemas),
            ["output_schemas"] = BuildSchemaMapJson(spec.OutputSchemas),
            ["planned_tools"] = BuildPlannedToolsJson(spec.PlannedTools),
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

    private static JsonArray BuildPlannedToolsJson(IReadOnlyList<PipelinePlannedTool> plannedTools)
    {
        var array = new JsonArray();
        foreach (var tool in plannedTools)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["server"] = tool.Server,
                ["kind"] = tool.Kind,
                ["method"] = tool.Method,
                ["required"] = tool.Required,
                ["purpose"] = tool.Purpose,
                ["consumes"] = BuildStringArrayJson(tool.Consumes),
                ["produces"] = BuildStringArrayJson(tool.Produces)
            });
        }

        return array;
    }

    private static JsonArray BuildStringArrayJson(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode)JsonValue.Create(value)!);
        return array;
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

    private static YamlMappingNode CloneYamlMappingNode(YamlMappingNode node)
        => (YamlMappingNode)CloneYamlNode(node);

    private static YamlMappingNode? CloneYamlMappingNodeOrNull(YamlMappingNode? node)
        => node == null ? null : CloneYamlMappingNode(node);

    private static YamlScalarNode Scalar(string value) => new(value);

    private static YamlScalarNode LiteralScalar(string value) => new(value)
    {
        Style = YamlDotNet.Core.ScalarStyle.Literal
    };

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
        string? Description,
        IReadOnlyDictionary<string, string> Inputs,
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyDictionary<string, JsonNode?> InputSchemas,
        IReadOnlyDictionary<string, JsonNode?> OutputSchemas,
        IReadOnlyList<PipelinePlannedTool> PlannedTools,
        string ExtractReason,
        string Content,
        string GenerationPrompt);

    private sealed record PipelinePlannedTool(
        string Server,
        string Kind,
        string Method,
        bool Required,
        string? Purpose,
        IReadOnlyList<string> Consumes,
        IReadOnlyList<string> Produces);

    private sealed record StructuredPipelineExtractionMetadata(
        IReadOnlyDictionary<string, StructuredPipelineSubworkflowMetadata> Subworkflows,
        string? MainOrchestration,
        bool IsStructuredResponse)
    {
        public static StructuredPipelineExtractionMetadata Empty { get; } = new(
            new Dictionary<string, StructuredPipelineSubworkflowMetadata>(StringComparer.Ordinal),
            null,
            IsStructuredResponse: false);
    }

    private sealed record StructuredPipelineSubworkflowMetadata(
        string Name,
        string? Description,
        IReadOnlyDictionary<string, JsonNode?> Inputs,
        IReadOnlyDictionary<string, JsonNode?> Outputs,
        IReadOnlyList<PipelinePlannedTool> PlannedTools);

    private sealed record GeneratedLeafWorkflow(
        string Name,
        string GeneratedWorkflowName,
        WorkflowDocument Document,
        string Yaml);

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
