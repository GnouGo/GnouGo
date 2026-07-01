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

    private const string PipelineWorkKindOrchestration = "orchestration";
    private const string PipelineWorkKindDeterministicShaping = "deterministic_shaping";
    private const string PipelineWorkKindExternalWork = "external_work";
    private const int PipelineExtractionScoreThreshold = 45;

    private static readonly HashSet<string> PipelineIntentStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "after",
        "before",
        "build",
        "called",
        "caller",
        "content",
        "data",
        "declare",
        "description",
        "detail",
        "details",
        "expose",
        "field",
        "fields",
        "from",
        "goal",
        "input",
        "inputs",
        "into",
        "later",
        "logic",
        "main",
        "must",
        "output",
        "outputs",
        "produce",
        "provided",
        "request",
        "result",
        "return",
        "should",
        "step",
        "subworkflow",
        "that",
        "this",
        "through",
        "using",
        "value",
        "values",
        "with",
        "workflow"
    };

    private sealed record PipelineMcpContext(
        IReadOnlyList<McpServerDiscovery> Servers,
        string? ServersDoc)
    {
        public static PipelineMcpContext Empty { get; } = new(Array.Empty<McpServerDiscovery>(), null);
    }

    private sealed record PipelineLeafContractDemand(
        string LeafName,
        string OutputName,
        string ConsumerStepId,
        string ConsumerField,
        string InvalidPath,
        string Reason,
        IReadOnlyList<string> RequiredOutputPaths,
        string? ExpectedType);

    private sealed record PipelineQualityEvent(
        string Kind,
        int Attempt,
        string? Phase,
        string? LeafName,
        string? OutputName,
        string? ConsumerStepId,
        string? ConsumerField,
        string? InvalidPath,
        string? Reason,
        IReadOnlyList<string>? RequiredOutputPaths,
        string? ExpectedType,
        string? ErrorType,
        string? Message);

    private sealed record PipelineMcpCapabilityMatch(string Server, string Kind, string Method)
    {
        public string DisplayName => $"{Server}/{Method} ({Kind})";
    }

    private sealed record PipelineExtractionScore(
        int Score,
        int Threshold,
        string Rating,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyList<string> Hints);

    private sealed record PipelineStepPath(
        StepDef Step,
        IReadOnlyList<StepDef> Ancestors);

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
        var qualityEvents = new List<PipelineQualityEvent>();
        using (var mainSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.assemble_main_workflow", "assemble_main_workflow", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.subworkflow_count", generatedLeaves.Length)
        }))
        {
            try
            {
                mainSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly.kind", "llm_orchestration_graph");
                var configuredMainInputs = BuildConfiguredMainInputContract(input, generator);
                var currentLeaves = generatedLeaves;
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
                var assemblySucceeded = false;

                for (var attempt = 1; attempt <= maxAssemblyAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var generatedLeafInputs = BuildGeneratedMainInputContract(currentLeaves);
                    var baseMainAssemblyPrompt = BuildMainAssemblyPrompt(
                        input, generator, normalizedMarkdown, extraction, currentLeaves, configuredMainInputs, generatedLeafInputs, ctx.Engine.Registry);
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
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_workflows", string.Join(",", currentLeaves.Select(static leaf => leaf.Name))),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.main_inputs", SerializeYamlMapping(configuredMainInputs)),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.assembly.note", "Configured metadata and inputs are authoritative. Otherwise the LLM infers the public contract while final YAML composition remains deterministic.")
                        });
                        mainSpan.AddEvent("gnougo-flow.plan.pipeline.assembly.input", new[]
                        {
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.main_workflow_prompt", extraction.MainWorkflowPrompt),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_workflows", string.Join(",", currentLeaves.Select(static leaf => leaf.Name))),
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

                        var assembly = ParseGeneratedMainAssembly(mainResponse.Text ?? string.Empty, currentLeaves);
                        var mainInputs = ResolveMainInputContract(configuredMainInputs, assembly, generatedLeafInputs);
                        ForceMainWorkflowInputs(assembly.MainWorkflowNode, mainInputs);
                        EnsureMainWorkflowOutputs(assembly.MainWorkflowNode, extraction.Subworkflows);
                        ValidateDeclaredMainInputReferences(assembly.MainWorkflowNode, mainInputs);

                        assembledYaml = ComposePipelineWorkflowYaml(input, generator, extraction, currentLeaves, assembly, mainInputs);
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
                                    currentLeaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal));
                                ValidatePipelineLeafCallArguments(assembledDocument, currentLeaves);
                                ValidatePipelineMainLeafOutputContracts(assembledDocument, currentLeaves);
                                ValidatePipelineMainDataflowQuality(assembledDocument, currentLeaves);
                                if (IsPipelineDryRunValidation(validate))
                                    ValidatePipelineMainDryRunOutputProjection(assembledDocument);
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
                        generatedLeaves = currentLeaves;
                        assemblySucceeded = true;
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
                        var contractDemand = TryAnalyzePipelineLeafContractDemand(ex, assembledDocument, currentLeaves);
                        attemptSpan.AddEvent(
                            "gnougo-flow.plan.pipeline.main_assembly.error",
                            BuildPlanErrorTelemetryAttributes(ex, attempt, "assemble_main_workflow"));
                        if (contractDemand != null)
                        {
                            try
                            {
                                currentLeaves = await RegenerateLeafForContractDemandAsync(
                                    ctx,
                                    input,
                                    generator,
                                    extraction,
                                    currentLeaves,
                                    contractDemand,
                                    ex,
                                    attempt,
                                    attemptSpan.Span,
                                    ct);
                                qualityEvents.Add(new PipelineQualityEvent(
                                    "leaf_contract_repair",
                                    attempt,
                                    "assemble_main_workflow",
                                    contractDemand.LeafName,
                                    contractDemand.OutputName,
                                    contractDemand.ConsumerStepId,
                                    contractDemand.ConsumerField,
                                    contractDemand.InvalidPath,
                                    contractDemand.Reason,
                                    contractDemand.RequiredOutputPaths,
                                    contractDemand.ExpectedType,
                                    null,
                                    "Regenerated producing leaf with a stronger output contract."));
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "leaf_contract_repaired");
                                ctx.Engine.Logger.LogWarning(
                                    ex,
                                    "workflow.plan pipeline main assembly attempt {Attempt}/{MaxAttempts} found weak leaf contract {Leaf}.{Output}, regenerated impacted leaf",
                                    attempt,
                                    maxAssemblyAttempts,
                                    contractDemand.LeafName,
                                    contractDemand.OutputName);
                                ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.leaf_contract_repair", new[]
                                {
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAssemblyAttempts),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", contractDemand.LeafName),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_output", contractDemand.OutputName),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.consumer_step", contractDemand.ConsumerStepId)
                                });
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception repairEx)
                            {
                                lastAssemblyException = repairEx;
                                previousAssemblyError = BuildStructuredPlanError(repairEx, attempt);
                                qualityEvents.Add(new PipelineQualityEvent(
                                    "leaf_contract_repair_failed",
                                    attempt,
                                    "repair_leaf_contract",
                                    contractDemand.LeafName,
                                    contractDemand.OutputName,
                                    contractDemand.ConsumerStepId,
                                    contractDemand.ConsumerField,
                                    contractDemand.InvalidPath,
                                    contractDemand.Reason,
                                    contractDemand.RequiredOutputPaths,
                                    contractDemand.ExpectedType,
                                    repairEx.GetType().Name,
                                    TruncatePipelineQualityMessage(repairEx.Message)));
                                attemptSpan.AddEvent(
                                    "gnougo-flow.plan.pipeline.leaf_contract_repair.error",
                                    BuildPlanErrorTelemetryAttributes(repairEx, attempt, "repair_leaf_contract", contractDemand.LeafName));
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "retrying");
                                attemptSpan.Fail(repairEx);
                                ctx.Engine.Logger.LogWarning(
                                    repairEx,
                                    "workflow.plan pipeline leaf contract repair for {Leaf}.{Output} failed during main assembly attempt {Attempt}/{MaxAttempts}, reprompting main",
                                    contractDemand.LeafName,
                                    contractDemand.OutputName,
                                    attempt,
                                    maxAssemblyAttempts);
                            }
                        }
                        else
                        {
                            attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "retrying");
                            attemptSpan.Fail(ex);
                            ctx.Engine.Logger.LogWarning(
                                ex,
                                "workflow.plan pipeline main assembly attempt {Attempt}/{MaxAttempts} failed, reprompting",
                                attempt,
                                maxAssemblyAttempts);
                        }

                        ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.main_assembly_retry", new[]
                        {
                            new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                            new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAssemblyAttempts),
                            new KeyValuePair<string, object?>("error.type", ex.GetType().Name),
                            new KeyValuePair<string, object?>("error.message", ex.Message)
                        });
                        qualityEvents.Add(new PipelineQualityEvent(
                            "main_assembly_retry",
                            attempt,
                            "assemble_main_workflow",
                            null,
                            null,
                            null,
                            null,
                            null,
                            contractDemand?.Reason,
                            contractDemand?.RequiredOutputPaths,
                            contractDemand?.ExpectedType,
                            ex.GetType().Name,
                            TruncatePipelineQualityMessage(ex.Message)));
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

                if (!assemblySucceeded || assembledYaml == null || assembledDocument == null)
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

        var qualityReport = BuildPipelineQualityReportJson(extraction, generatedLeaves, finalDoc, qualityEvents);
        var inspection = BuildPipelineInspectionJson(
            normalizedMarkdown,
            annotatedMarkdown,
            extraction,
            generatedLeaves,
            finalDoc,
            qualityEvents);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.status", qualityReport["status"]?.GetValue<string>() ?? "unknown");
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.repair_count", qualityEvents.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)));
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.retry_count", qualityEvents.Count(static item => item.Kind.EndsWith("_retry", StringComparison.Ordinal)));
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.warning_count", qualityReport["warnings"] is JsonArray warnings ? warnings.Count : 0);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.inspection.leaf_count", generatedLeaves.Length);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.inspection.repair_count", qualityEvents.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)));
        if (ctx.Limits.LogStepContent)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.inspection", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.inspection", inspection.ToJsonString(new JsonSerializerOptions { WriteIndented = false }))
            });
        }

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
                ["specs"] = BuildExtractionJson(extraction),
                ["quality_report"] = qualityReport,
                ["inspection"] = inspection
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
            - When a value is only needed as an implementation detail, such as an identifier, flag, or temporary value, list it as an internal implementation value to derive inside the responsible workflow step or leaf.
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

            Identify only the parts that contain significant algorithmic or external-work logic and wrap them in exactly this block syntax:

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

            A part deserves a leaf subworkflow only when it contains meaningful work such as:
            - a loop;
            - a conditional decision;
            - a multi-step sequence with state;
            - tool orchestration;
            - LLM or MCP work;
            - retry or error handling;
            - branching logic;
            - file or report generation;
            - cleanup logic;
            - a reusable technical operation.

            Do not extract:
            - simple one-line or few actions;
            - simple renames, constants, guards, field mapping, routing, aggregation, or loop orchestration that the main workflow can express with support nodes;
            - global style rules;
            - constants;
            - footer text;
            - wording rules;
            - tiny isolated actions that do not deserve a workflow.
            Leave simple deterministic orchestration in the main workflow. The main workflow can use `set`, `sequence`, `switch`, `parallel`, `loop.sequential`, and `loop.parallel` support nodes for shaping, guards, routing, aggregation, and loops.

            Keep extracted blocks focused:
            - Do not create one large block that mixes several responsibilities.
            - Avoid blocks with high cyclomatic complexity: too many branches, nested conditionals, nested loops, retry paths, cleanup paths, or state transitions.
            - When one algorithmic section has several independent decision paths or phases, split it into multiple self-contained leaf subworkflow blocks.
            - Prefer cohesive blocks that a workflow generator can implement without needing to reason about unrelated branches.
            - Do not over-split into trivial one-line operations or deterministic glue; split only when the reduced complexity improves workflow generation quality.

            Extraction scoring rubric:
            - Strong leaf candidates perform external side effects, MCP/LLM/file/report work, nontrivial parsing/normalization/analysis, reusable technical operations, retry/error-handling sequences, cleanup, or meaningful stateful sequences.
            - Weak leaf candidates are pure orchestration, one-line deterministic glue, simple renames, constants, guards, field mapping, routing, aggregation, filtering, sorting, or loop orchestration.
            - A weak candidate should stay in `## Main workflow orchestration` and be implemented by main support nodes.
            - A candidate that would score weak must not be wrapped as a subworkflow block.

            Rules for subworkflow blocks:
            - The name must use snake_case.
            - Each block must describe exactly one responsibility.
            - Each block must be self-contained.
            - Each block must be detailed enough to generate a workflow later.
            - Each block must be a leaf workflow.
            - A block must not exist only to rename fields, compute constants, filter/map already available values, route branches, or orchestrate loops; leave that work to the main workflow.
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
            - Structured subworkflow metadata must classify each leaf with `work_kind`: `orchestration`, `deterministic_shaping`, or `external_work`.
            - Structured output fields should declare concrete object properties and array item types when later workflow steps need field-level access.
            - Avoid `any`, bare `object`, and bare `array` outputs. If an output may be looped over or inspected by the main workflow, declare concrete `items` and object `properties`.
            - Structured `planned_tools` must list every MCP server tool or prompt this leaf is expected to call directly.
            - Mark planned tools as required when omitting that MCP call would violate the leaf goal.
            - For each relevant MCP tool or prompt, add a structured planned_tools entry with the exact server name, kind, method name, purpose, consumed fields, and produced fields.
            - External-work leaves that clone, read/fetch/query/list external data, write, delete, cleanup, report, post, push, or call outside systems must declare concrete planned_tools when matching MCP tools/prompts are documented above.
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
            sb.AppendLine("- Structured work_kind must match the leaf role: orchestration, deterministic_shaping, or external_work.");
            sb.AppendLine("- External-work leaves with matching MCP capabilities must include concrete planned_tools entries.");
            sb.AppendLine("- Structured planned_tools must use exact MCP server/tool/prompt names from the global MCP context.");
            sb.AppendLine("- Fix low extraction scores by either making the leaf a meaningful external/algorithmic unit with concrete planned_tools/contracts, or moving trivial shaping/orchestration back to the main workflow.");
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
                  "work_kind": { "type": "string", "enum": ["orchestration", "deterministic_shaping", "external_work"] },
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
            NormalizePipelineWorkKind(GetStringProperty(subworkflow, "work_kind")),
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
            var workKind = NormalizePipelineWorkKind(structured?.WorkKind) ?? InferPipelineWorkKind(spec);
            ValidatePlannedToolsAgainstMcpContext(spec.Name, plannedTools, pipelineMcpContext, validationErrors);

            var enrichedSpec = spec with
            {
                WorkKind = workKind,
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
                    workKind,
                    spec.Content)
            };
            if (metadata.IsStructuredResponse)
            {
                var score = ScorePipelineExtractionSpec(enrichedSpec, pipelineMcpContext);
                enrichedSpec = enrichedSpec with { ExtractionScore = score };
                ValidatePipelineExtractionQuality(enrichedSpec, pipelineMcpContext, validationErrors);
            }
            enriched.Add(enrichedSpec);
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

    private static void ValidatePipelineExtractionQuality(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext,
        List<string> validationErrors)
    {
        var score = spec.ExtractionScore ?? ScorePipelineExtractionSpec(spec, pipelineMcpContext);
        foreach (var diagnostic in score.Diagnostics)
            validationErrors.Add(diagnostic);

        if (!IsExternalWorkSpec(spec) || spec.PlannedTools.Count > 0)
            return;

        var matches = FindLikelyMcpCapabilityMatches(spec, pipelineMcpContext)
            .Take(8)
            .ToArray();
        if (matches.Length == 0)
            return;

        validationErrors.Add(
            $"Subworkflow '{spec.Name}' is external work but declares no planned_tools. "
            + $"Matching discovered MCP capabilities: {string.Join(", ", matches.Select(static match => match.DisplayName))}. "
            + "Add concrete planned_tools entries for the required MCP calls, or leave trivial deterministic shaping/orchestration in the main workflow.");
    }

    private static PipelineExtractionScore ScorePipelineExtractionSpec(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
    {
        var score = 50;
        var reasons = new List<string>();
        var diagnostics = new List<string>();
        var hints = new List<string>();
        var intentText = BuildPipelineSpecIntentText(spec);
        var hasExternalIntent = ContainsExternalWorkIntent(intentText);
        var hasDeterministicIntent = ContainsDeterministicShapingIntent(intentText);
        var hasAlgorithmicIntent = ContainsAlgorithmicExtractionIntent(intentText);
        var hasRequiredPlannedTool = spec.PlannedTools.Any(static tool => tool.Required);
        var hasAnyPlannedTool = spec.PlannedTools.Count > 0;
        var contentTokenCount = ExtractIntentTokens(spec.Content).Count;
        var boundaryFieldCount = spec.Inputs.Count + spec.Outputs.Count;

        if (string.Equals(spec.WorkKind, PipelineWorkKindExternalWork, StringComparison.Ordinal))
        {
            score += 10;
            reasons.Add("classified as external work");
        }

        if (hasExternalIntent)
        {
            score += 10;
            reasons.Add("describes external or side-effecting work");
        }

        if (hasRequiredPlannedTool)
        {
            score += 25;
            reasons.Add("declares required planned MCP tool/prompt calls");
        }
        else if (hasAnyPlannedTool)
        {
            score += 15;
            reasons.Add("declares planned MCP tool/prompt calls");
        }

        if (hasAlgorithmicIntent)
        {
            score += 15;
            reasons.Add("contains parsing, analysis, normalization, or algorithmic work");
        }

        if (boundaryFieldCount >= 3)
        {
            score += 5;
            reasons.Add("has a meaningful input/output boundary");
        }

        if (contentTokenCount >= 18)
        {
            score += 10;
            reasons.Add("content contains enough detail to generate a focused leaf");
        }
        else
        {
            score -= 10;
            hints.Add("Add enough leaf-specific detail, or keep the work in the main workflow.");
        }

        if (string.Equals(spec.WorkKind, PipelineWorkKindOrchestration, StringComparison.Ordinal)
            && !hasExternalIntent
            && !hasAnyPlannedTool
            && !hasAlgorithmicIntent)
        {
            score -= 30;
            diagnostics.Add(
                $"PIPELINE_EXTRACTION_ORCHESTRATION_LEAF: Subworkflow '{spec.Name}' is classified as orchestration without external, tool, or algorithmic work. "
                + "Main workflow support nodes should handle simple orchestration.");
            hints.Add("Move routing, sequencing, loops, fan-out/fan-in, and leaf calls back to the main workflow.");
        }

        if (IsTrivialDeterministicExtractionCandidate(spec, hasAlgorithmicIntent, hasExternalIntent))
        {
            score -= 35;
            diagnostics.Add(
                $"PIPELINE_EXTRACTION_TRIVIAL_LEAF: Subworkflow '{spec.Name}' appears to be simple deterministic shaping/glue. "
                + "Leave simple renames, constants, guards, field mapping, routing, aggregation, filtering, sorting, and loop orchestration in the main workflow.");
            hints.Add("Extract only nontrivial parsing, analysis, external work, reusable operations, retries, cleanup, or meaningful stateful sequences.");
        }

        if (IsExternalWorkSpec(spec) && !hasAnyPlannedTool)
        {
            var matches = FindLikelyMcpCapabilityMatches(spec, pipelineMcpContext);
            if (matches.Count > 0)
            {
                score -= 25;
                reasons.Add("external work has matching MCP capabilities but no planned_tools");
            }
        }

        score = Math.Clamp(score, 0, 100);
        var rating = score >= 75 ? "strong" : score >= PipelineExtractionScoreThreshold ? "acceptable" : "weak";
        if (score < PipelineExtractionScoreThreshold)
        {
            diagnostics.Add(
                $"PIPELINE_EXTRACTION_LOW_SCORE: Subworkflow '{spec.Name}' extraction score {score}/100 is below threshold {PipelineExtractionScoreThreshold}. "
                + $"Reasons: {FormatPipelineExtractionScoreList(reasons)}. "
                + $"Hints: {FormatPipelineExtractionScoreList(hints)}");
        }

        if (reasons.Count == 0)
            reasons.Add("no strong extraction signal found");

        return new PipelineExtractionScore(score, PipelineExtractionScoreThreshold, rating, reasons, diagnostics, hints);
    }

    private static bool IsTrivialDeterministicExtractionCandidate(
        WorkflowPipelineSubworkflowSpec spec,
        bool hasAlgorithmicIntent,
        bool hasExternalIntent)
    {
        if (spec.PlannedTools.Count > 0 || hasExternalIntent)
            return false;

        if (hasAlgorithmicIntent)
            return false;

        var isDeterministicOrchestration = string.Equals(spec.WorkKind, PipelineWorkKindDeterministicShaping, StringComparison.Ordinal)
                                           || string.Equals(spec.WorkKind, PipelineWorkKindOrchestration, StringComparison.Ordinal)
                                           || ContainsDeterministicShapingIntent(BuildPipelineSpecIntentText(spec));
        if (!isDeterministicOrchestration)
            return false;

        var intentText = BuildPipelineSpecIntentText(spec);
        return TrivialExtractionIntentRegex().IsMatch(intentText)
               || (ExtractIntentTokens(spec.Content).Count <= 10 && spec.Outputs.Count <= 1);
    }

    private static string FormatPipelineExtractionScoreList(IReadOnlyList<string> values)
        => values.Count == 0 ? "none" : string.Join("; ", values.Distinct(StringComparer.OrdinalIgnoreCase).Take(5));

    private static string? NormalizePipelineWorkKind(string? workKind)
    {
        if (string.IsNullOrWhiteSpace(workKind))
            return null;

        var normalized = workKind.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            PipelineWorkKindOrchestration => PipelineWorkKindOrchestration,
            PipelineWorkKindDeterministicShaping => PipelineWorkKindDeterministicShaping,
            PipelineWorkKindExternalWork => PipelineWorkKindExternalWork,
            _ => null
        };
    }

    private static string InferPipelineWorkKind(WorkflowPipelineSubworkflowSpec spec)
    {
        if (ContainsExternalWorkIntent(BuildPipelineSpecIntentText(spec)))
            return PipelineWorkKindExternalWork;

        if (ContainsDeterministicShapingIntent(BuildPipelineSpecIntentText(spec)))
            return PipelineWorkKindDeterministicShaping;

        return PipelineWorkKindOrchestration;
    }

    private static bool IsExternalWorkSpec(WorkflowPipelineSubworkflowSpec spec)
        => string.Equals(spec.WorkKind, PipelineWorkKindExternalWork, StringComparison.Ordinal)
           || ContainsExternalWorkIntent(BuildPipelineSpecIntentText(spec));

    private static string BuildPipelineSpecIntentText(WorkflowPipelineSubworkflowSpec spec)
        => string.Join('\n', new[]
            {
                spec.Name,
                spec.Goal,
                spec.Description,
                spec.ExtractReason,
                spec.Content
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value)))!;

    private static bool ContainsExternalWorkIntent(string text)
        => ExternalWorkIntentRegex().IsMatch(text);

    private static bool ContainsDeterministicShapingIntent(string text)
        => DeterministicShapingIntentRegex().IsMatch(text);

    private static bool ContainsAlgorithmicExtractionIntent(string text)
        => AlgorithmicExtractionIntentRegex().IsMatch(text);

    private static IReadOnlyList<PipelineMcpCapabilityMatch> FindLikelyMcpCapabilityMatches(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
    {
        if (pipelineMcpContext.Servers.Count == 0)
            return Array.Empty<PipelineMcpCapabilityMatch>();

        var specTokens = ExtractIntentTokens(BuildPipelineSpecIntentText(spec));
        if (specTokens.Count == 0)
            return Array.Empty<PipelineMcpCapabilityMatch>();

        var matches = new List<PipelineMcpCapabilityMatch>();
        foreach (var server in pipelineMcpContext.Servers)
        {
            foreach (var tool in server.Tools)
            {
                if (CapabilityTextMatchesIntent(specTokens, tool.Name, tool.Description, server.Name, server.Description))
                    matches.Add(new PipelineMcpCapabilityMatch(server.Name, "tool", tool.Name));
            }

            foreach (var prompt in server.Prompts)
            {
                if (CapabilityTextMatchesIntent(specTokens, prompt.Name, prompt.Description, server.Name, server.Description))
                    matches.Add(new PipelineMcpCapabilityMatch(server.Name, "prompt", prompt.Name));
            }
        }

        return matches
            .DistinctBy(static match => match.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CapabilityTextMatchesIntent(
        IReadOnlySet<string> specTokens,
        string name,
        string? description,
        string serverName,
        string? serverDescription)
    {
        var capabilityTokens = ExtractIntentTokens(string.Join(' ', new[] { name, description, serverName, serverDescription }
            .Where(static value => !string.IsNullOrWhiteSpace(value)))!);
        if (capabilityTokens.Count == 0)
            return false;

        return capabilityTokens.Any(specTokens.Contains);
    }

    private static IReadOnlySet<string> ExtractIntentTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in IntentTokenRegex().Matches(text))
        {
            var token = match.Value.Trim().ToLowerInvariant();
            if (token.Length < 4 || PipelineIntentStopWords.Contains(token))
                continue;

            tokens.Add(token);
        }

        return tokens;
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
            WorkKind: null,
            inputs,
            outputs,
            inputSchemas,
            outputSchemas,
            Array.Empty<PipelinePlannedTool>(),
            ExtractionScore: null,
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
                workKind: null,
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
        string? workKind,
        string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate exactly one leaf GnOuGo workflow named `{name}`.");
        sb.AppendLine($"Goal: {goal}");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"Description: {description}");
        if (!string.IsNullOrWhiteSpace(workKind))
            sb.AppendLine($"Work kind: {workKind}.");
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
        if (string.Equals(workKind, PipelineWorkKindExternalWork, StringComparison.Ordinal))
        {
            sb.AppendLine("- This leaf is external work: it must execute the external/LLM/rendered action with a real step such as mcp.call, llm.call, template.render, or human.input.");
            sb.AppendLine("- Do not replace external work with emit-only instructions, static success flags, or a string telling someone else to run a command.");
        }
        sb.AppendLine("- If a step has an `if`, later unconditional steps must not reference that step directly. Either give the later step the same guard or create guaranteed branch outputs/default values first.");
        sb.AppendLine("- Function arguments are evaluated before the function runs. Do not hide unavailable step references inside `coalesce`, ternaries, or helper calls.");
        sb.AppendLine("- For MCP schemas, required numeric/integer/boolean request fields must be literal YAML scalars when the schema or validator requires explicit values; do not use expressions, casts, empty strings, or `data.env.*` fallbacks.");
        sb.AppendLine("- Follow discovered MCP schemas and tool descriptions exactly; do not add Flow-specific conventions for request fields.");
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

    private async Task<GeneratedLeafWorkflow[]> RegenerateLeafForContractDemandAsync(
        StepExecutionContext parentCtx,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineExtraction extraction,
        GeneratedLeafWorkflow[] leaves,
        PipelineLeafContractDemand demand,
        Exception mainValidationException,
        int attempt,
        ITelemetrySpan parentSpan,
        CancellationToken ct)
    {
        var leafIndex = Array.FindIndex(leaves, leaf => string.Equals(leaf.Name, demand.LeafName, StringComparison.Ordinal));
        if (leafIndex < 0)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Cannot repair leaf contract: generated leaf '{demand.LeafName}' was not found.");

        var spec = extraction.Subworkflows.FirstOrDefault(subworkflow => string.Equals(subworkflow.Name, demand.LeafName, StringComparison.Ordinal))
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Cannot repair leaf contract: extracted leaf spec '{demand.LeafName}' was not found.");

        using var leafRepairSpan = parentCtx.BeginTelemetrySpan(
            parentSpan,
            "workflow.plan.pipeline.repair_leaf_contract",
            "repair_leaf_contract",
            new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", demand.LeafName),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_output", demand.OutputName),
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
            });

        var currentLeaf = leaves[leafIndex];
        var previousError = BuildLeafContractDemandError(demand, mainValidationException, attempt);
        var repairContext = BuildLeafContractDemandRepairContext(demand, currentLeaf);
        var leafInput = BuildLeafPlanInput(
            pipelineInput,
            generator,
            spec,
            previousError,
            currentLeaf.Yaml,
            spec.GenerationPrompt,
            repairContext);
        ForceSinglePlanAttempt(leafInput);

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

        var result = await ExecuteSinglePlanAsync(leafCtx, leafInput, ct, leafRepairSpan.Span) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' contract repair did not return an object.");
        var yaml = result["yaml"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' contract repair did not return YAML.");
        var repairedLeaf = PrepareGeneratedLeaf(spec, yaml);
        EnsureLeafSatisfiesContractDemand(repairedLeaf, demand);

        var repairedLeaves = leaves.ToArray();
        repairedLeaves[leafIndex] = repairedLeaf;
        leafRepairSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_contract_repair_status", "succeeded");
        return repairedLeaves;
    }

    private static void ForceSinglePlanAttempt(JsonObject planInput)
    {
        if (planInput["validate"] is JsonObject validate)
            validate["max_repair_attempts"] = 1;
        planInput["on_invalid"] = new JsonObject { ["action"] = "fail", ["max_attempts"] = 1 };
    }

    private static string BuildLeafContractDemandError(
        PipelineLeafContractDemand demand,
        Exception mainValidationException,
        int attempt)
    {
        var details = BuildLeafContractDemandJson(demand);
        details["main_validation_error"] = JsonNode.Parse(BuildStructuredPlanError(mainValidationException, attempt));
        return new JsonObject
        {
            ["attempt"] = attempt,
            ["code"] = "PIPELINE_LEAF_CONTRACT_DEMAND",
            ["message"] = $"Final main validation requires a stronger public contract for leaf output {demand.LeafName}.{demand.OutputName}.",
            ["pipeline_leaf_contract_demand"] = details
        }.ToJsonString(PromptJsonOptions);
    }

    private static string BuildLeafContractDemandRepairContext(
        PipelineLeafContractDemand demand,
        GeneratedLeafWorkflow currentLeaf)
    {
        var currentOutputs = BuildLeafOutputSchemaMap(currentLeaf);
        currentOutputs.TryGetValue(demand.OutputName, out var currentOutputSchema);

        var sb = new StringBuilder();
        sb.AppendLine("Pipeline leaf contract demand:");
        sb.AppendLine("- Preserve the original leaf goal, public inputs, and implementation intent.");
        sb.AppendLine("- Regenerate only this leaf workflow.");
        sb.AppendLine("- Strengthen the public output contract named below so downstream orchestration can be validated statically.");
        sb.AppendLine("- Do not weaken semantic validation and do not declare deep fields under `type: any`.");
        sb.AppendLine("- Do not introduce tool-specific rules; use the leaf's existing task context only.");
        sb.AppendLine("- If the demanded output is an array, declare `type: array` with concrete `items.properties` for every required item field.");
        sb.AppendLine("- If the demanded output is an object, declare concrete `properties` for every required field.");
        sb.AppendLine();
        AppendPromptSection(sb, "pipeline_leaf_contract_demand", BuildLeafContractDemandJson(demand).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "current_leaf_output_schema_yaml", SerializeYamlMapping(new Dictionary<string, JsonNode?>
        {
            [demand.OutputName] = currentOutputSchema?.DeepClone()
        }));
        AppendPromptSection(sb, "required_output_schema_guidance_yaml", BuildLeafContractDemandSchemaGuidanceYaml(demand));
        return sb.ToString().TrimEnd();
    }

    private static string BuildLeafContractDemandSchemaGuidanceYaml(PipelineLeafContractDemand demand)
    {
        var paths = demand.RequiredOutputPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var sb = new StringBuilder();
        sb.AppendLine("# Minimum public output shape needed by the main workflow.");
        sb.AppendLine("# Replace <concrete type> with string, number, boolean, object, array, or dictionary from the leaf semantics; never use any.");
        sb.AppendLine($"{demand.OutputName}:");

        if (paths.Any(static path => string.Equals(path, "items", StringComparison.Ordinal)
                                    || path.StartsWith("items.", StringComparison.Ordinal))
            || string.Equals(demand.ExpectedType, "array", StringComparison.OrdinalIgnoreCase)
            || demand.ExpectedType?.Contains("array", StringComparison.OrdinalIgnoreCase) == true)
        {
            sb.AppendLine("  type: array");
            sb.AppendLine("  items:");
            var itemPaths = paths
                .Where(static path => path.StartsWith("items.", StringComparison.Ordinal))
                .Select(static path => path["items.".Length..])
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (itemPaths.Length == 0)
            {
                sb.AppendLine("    type: <concrete type>");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine("    type: object");
            AppendSchemaGuidanceProperties(sb, "    ", itemPaths);
            return sb.ToString().TrimEnd();
        }

        if (paths.Length == 0)
        {
            sb.AppendLine("  type: <concrete type>");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("  type: object");
        AppendSchemaGuidanceProperties(sb, "  ", paths);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSchemaGuidanceProperties(StringBuilder sb, string indent, IReadOnlyList<string> paths)
    {
        var firstSegments = paths
            .Select(static path => path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static segments => segments.Length > 0)
            .GroupBy(static segments => segments[0], StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal);

        sb.AppendLine($"{indent}properties:");
        foreach (var group in firstSegments)
        {
            var childPaths = group
                .Where(static segments => segments.Length > 1)
                .Select(static segments => string.Join('.', segments.Skip(1)))
                .ToArray();
            sb.AppendLine($"{indent}  {group.Key}:");
            if (childPaths.Length == 0)
            {
                sb.AppendLine($"{indent}    type: <concrete type>");
                continue;
            }

            sb.AppendLine($"{indent}    type: object");
            AppendSchemaGuidanceProperties(sb, indent + "    ", childPaths);
        }
    }

    private static JsonObject BuildLeafContractDemandJson(PipelineLeafContractDemand demand)
        => new()
        {
            ["leaf"] = demand.LeafName,
            ["output"] = demand.OutputName,
            ["consumer_step"] = demand.ConsumerStepId,
            ["consumer_field"] = demand.ConsumerField,
            ["invalid_path"] = demand.InvalidPath,
            ["reason"] = demand.Reason,
            ["expected_type"] = demand.ExpectedType,
            ["required_output_paths"] = new JsonArray(demand.RequiredOutputPaths
                .Select(static path => (JsonNode?)JsonValue.Create(path))
                .ToArray())
        };

    private static void EnsureLeafSatisfiesContractDemand(
        GeneratedLeafWorkflow leaf,
        PipelineLeafContractDemand demand)
    {
        var outputs = BuildLeafOutputSchemaMap(leaf);
        if (!outputs.TryGetValue(demand.OutputName, out var schema))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Leaf workflow '{demand.LeafName}' contract repair did not expose required output '{demand.OutputName}'.",
                details: new JsonObject { ["pipeline_leaf_contract_demand"] = BuildLeafContractDemandJson(demand) });
        }

        var descriptor = FlowTypeDescriptorConverter.FromJsonSchema(schema);
        var errors = new List<string>();
        if (string.Equals(demand.Reason, "WEAK_OUTPUT_SCHEMA", StringComparison.Ordinal)
            && IsWeakPipelineOutputDescriptor(descriptor))
        {
            errors.Add($"output '{demand.OutputName}' must declare a strong concrete schema");
        }

        if (!string.IsNullOrWhiteSpace(demand.ExpectedType)
            && !PipelineOutputDescriptorSatisfiesExpectedType(descriptor, demand.ExpectedType))
        {
            errors.Add($"output '{demand.OutputName}' must be compatible with expected type '{demand.ExpectedType}'");
        }

        foreach (var requiredPath in demand.RequiredOutputPaths)
        {
            if (!PipelineOutputDescriptorHasRequiredPath(descriptor, requiredPath))
                errors.Add($"output '{demand.OutputName}' does not declare required path '{requiredPath}'");
        }

        if (errors.Count == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{demand.LeafName}' contract repair did not satisfy downstream contract demand: {string.Join("; ", errors)}.",
            details: new JsonObject { ["pipeline_leaf_contract_demand"] = BuildLeafContractDemandJson(demand) });
    }

    private static bool PipelineOutputDescriptorSatisfiesExpectedType(FlowTypeDescriptor descriptor, string expectedType)
    {
        var normalized = expectedType.Trim().ToLowerInvariant();
        if (normalized.Contains("array", StringComparison.Ordinal))
            return DescriptorContainsKind(descriptor, FlowTypeKind.Array);
        if (normalized.Contains("object", StringComparison.Ordinal))
            return DescriptorContainsKind(descriptor, FlowTypeKind.Object) || DescriptorContainsKind(descriptor, FlowTypeKind.Dictionary);
        if (normalized.Contains("string", StringComparison.Ordinal))
            return DescriptorContainsKind(descriptor, FlowTypeKind.String);
        if (normalized.Contains("number", StringComparison.Ordinal))
            return DescriptorContainsKind(descriptor, FlowTypeKind.Number) || DescriptorContainsKind(descriptor, FlowTypeKind.Integer);
        if (normalized.Contains("integer", StringComparison.Ordinal))
            return DescriptorContainsKind(descriptor, FlowTypeKind.Integer);
        if (normalized.Contains("boolean", StringComparison.Ordinal) || normalized.Contains("bool", StringComparison.Ordinal))
            return DescriptorContainsKind(descriptor, FlowTypeKind.Boolean);

        return true;
    }

    private static bool DescriptorContainsKind(FlowTypeDescriptor descriptor, FlowTypeKind kind)
    {
        if (descriptor.Kind == kind)
            return true;
        return descriptor.Kind == FlowTypeKind.Union && descriptor.Variants.Any(variant => DescriptorContainsKind(variant, kind));
    }

    private static bool PipelineOutputDescriptorHasRequiredPath(FlowTypeDescriptor descriptor, string requiredPath)
    {
        if (string.IsNullOrWhiteSpace(requiredPath))
            return !descriptor.IsOpaque;

        var segments = SplitContractPath(requiredPath);
        if (segments.Length == 0)
            return !descriptor.IsOpaque;

        if (string.Equals(segments[0], "items", StringComparison.Ordinal))
        {
            var itemType = ExtractPipelineArrayItemType(descriptor);
            if (itemType == null || itemType.IsOpaque)
                return false;

            return segments.Length == 1
                || itemType.ResolvePath(segments.Skip(1).ToArray()) is { IsOpaque: false };
        }

        return descriptor.ResolvePath(segments) is { IsOpaque: false };
    }

    private static FlowTypeDescriptor? ExtractPipelineArrayItemType(FlowTypeDescriptor descriptor)
    {
        if (descriptor.Kind == FlowTypeKind.Array)
            return descriptor.Items;

        if (descriptor.Kind != FlowTypeKind.Union)
            return null;

        var items = descriptor.Variants
            .Select(ExtractPipelineArrayItemType)
            .Where(static item => item != null)
            .Cast<FlowTypeDescriptor>()
            .ToArray();

        return items.Length == 0 ? null : FlowTypeDescriptor.Union(items);
    }

    private static string[] SplitContractPath(string path)
        => path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static PipelineLeafContractDemand? TryAnalyzePipelineLeafContractDemand(
        Exception exception,
        WorkflowDocument? assembledDocument,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        if (assembledDocument == null || !assembledDocument.Workflows.TryGetValue("main", out var main))
            return null;

        var leafNames = leaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal);
        var demands = new List<PipelineLeafContractDemand>();
        foreach (var diagnostic in EnumeratePipelineDiagnostics(exception))
        {
            var code = GetStringProperty(diagnostic, "code");
            if (string.Equals(code, "WEAK_OUTPUT_SCHEMA", StringComparison.Ordinal))
            {
                var location = GetStringProperty(diagnostic, "location") ?? "";
                if (TryBuildWeakLeafOutputSchemaDemand(
                        main,
                        leafNames,
                        location,
                        GetStringProperty(diagnostic, "message"),
                        out var weakOutputDemand))
                {
                    demands.Add(weakOutputDemand);
                    continue;
                }
            }

            if (!IsLeafContractDemandDiagnosticCode(code))
                continue;

            var workflow = GetStringProperty(diagnostic, "workflow");
            if (!string.IsNullOrWhiteSpace(workflow) && !string.Equals(workflow, "main", StringComparison.Ordinal))
                continue;

            var invalidPath = GetStringProperty(diagnostic, "invalid_path") ?? "";
            var consumerStepId = GetStringProperty(diagnostic, "step") ?? "";
            var consumerField = GetStringProperty(diagnostic, "field") ?? "";
            var expected = GetStringProperty(diagnostic, "expected");
            var diagnosticRequiredPaths = GetStringArray(diagnostic["required_output_paths"] as JsonArray);
            var reason = string.IsNullOrWhiteSpace(code) ? "VALIDATION_ERROR" : code;

            if (TryBuildDirectLeafOutputDemand(
                    main,
                    leafNames,
                    invalidPath,
                    consumerStepId,
                    consumerField,
                    reason,
                    expected,
                    diagnosticRequiredPaths,
                    out var directDemand))
            {
                demands.Add(directDemand);
                continue;
            }

            if (TryBuildLoopLeafOutputDemand(
                    main,
                    leafNames,
                    invalidPath,
                    consumerStepId,
                    consumerField,
                    reason,
                    expected,
                    out var loopDemand))
            {
                demands.Add(loopDemand);
            }
        }

        return MergePipelineLeafContractDemands(demands);
    }

    private static bool IsLeafContractDemandDiagnosticCode(string? code)
        => code is "OPAQUE_DATA_VARIABLE_DEEP_ACCESS"
            or "DATA_VARIABLE_PROPERTY_UNKNOWN"
            or "OPAQUE_RESPONSE_DEEP_ACCESS"
            or "STEP_OUTPUT_PROPERTY_UNKNOWN"
            or "OPAQUE_ARRAY_LOOP_ITEMS"
            or "WEAK_ARRAY_LOOP_ITEMS"
            or "LEAF_OUTPUT_LOOP_ITEMS_NOT_ARRAY"
            or ErrorCodes.ExprTypeMismatch;

    private static bool TryBuildWeakLeafOutputSchemaDemand(
        WorkflowDef main,
        IReadOnlySet<string> leafNames,
        string location,
        string? message,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineLeafContractDemand? demand)
    {
        demand = null;
        var segments = location.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 4
            || !string.Equals(segments[0], "workflows", StringComparison.Ordinal)
            || !leafNames.Contains(segments[1])
            || !string.Equals(segments[2], "outputs", StringComparison.Ordinal))
        {
            return false;
        }

        var leafName = segments[1];
        var outputName = segments[3];
        var outputPath = segments.Skip(4).ToArray();
        var requiredPaths = outputPath.Length == 0
            ? Array.Empty<string>()
            : new[] { string.Join('.', outputPath) };
        var expectedType = InferWeakOutputExpectedType(message, outputPath);
        if (requiredPaths.Length == 0 && string.Equals(expectedType, "array", StringComparison.Ordinal))
            requiredPaths = new[] { "items" };

        demand = new PipelineLeafContractDemand(
            leafName,
            outputName,
            TryFindMainLeafCallStepId(main, leafName) ?? "",
            "",
            location,
            "WEAK_OUTPUT_SCHEMA",
            requiredPaths,
            expectedType);
        return true;
    }

    private static string? InferWeakOutputExpectedType(string? message, IReadOnlyList<string> outputPath)
    {
        if (outputPath.Count > 0 && string.Equals(outputPath[0], "items", StringComparison.Ordinal))
            return "array";

        if (string.IsNullOrWhiteSpace(message))
            return null;

        if (message.Contains("Array output schema", StringComparison.OrdinalIgnoreCase))
            return "array";
        if (message.Contains("Object output schema", StringComparison.OrdinalIgnoreCase))
            return "object";
        if (message.Contains("Dictionary output schema", StringComparison.OrdinalIgnoreCase))
            return "dictionary";

        return null;
    }

    private static string? TryFindMainLeafCallStepId(WorkflowDef main, string leafName)
    {
        foreach (var step in EnumerateSteps(main.Steps))
        {
            if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
                && string.Equals(ReadWorkflowCallRefNameFromInput(step), leafName, StringComparison.Ordinal))
            {
                return step.Id;
            }
        }

        return null;
    }

    private static string? ReadWorkflowCallRefNameFromInput(StepDef step)
        => step.Input?["ref"] is JsonObject refObj
            ? GetStringProperty(refObj, "name")
            : null;

    private static IEnumerable<JsonObject> EnumeratePipelineDiagnostics(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not WorkflowRuntimeException { Details: JsonObject details })
                continue;

            foreach (var diagnostic in EnumerateDiagnosticsFromDetails(details))
                yield return diagnostic;
        }
    }

    private static IEnumerable<JsonObject> EnumerateDiagnosticsFromDetails(JsonObject details)
    {
        if (details["diagnostics"] is JsonArray diagnostics)
        {
            foreach (var diagnostic in diagnostics.OfType<JsonObject>())
                yield return diagnostic;
        }

        if (details["details"] is JsonObject nestedDetails)
        {
            foreach (var diagnostic in EnumerateDiagnosticsFromDetails(nestedDetails))
                yield return diagnostic;
        }
    }

    private static bool TryBuildDirectLeafOutputDemand(
        WorkflowDef main,
        IReadOnlySet<string> leafNames,
        string invalidPath,
        string consumerStepId,
        string consumerField,
        string reason,
        string? expected,
        IReadOnlyList<string> diagnosticRequiredPaths,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineLeafContractDemand? demand)
    {
        demand = null;
        if (!TryParseStepOutputReference(invalidPath, out var callStepId, out var outputName, out var remainingPath))
            return false;

        if (!TryGetMainLeafCall(main, leafNames, callStepId, out var leafName))
            return false;

        var requiredPaths = diagnosticRequiredPaths.Count > 0
            ? diagnosticRequiredPaths
            : remainingPath.Count == 0
                ? Array.Empty<string>()
                : new[] { string.Join('.', remainingPath) };
        if (requiredPaths.Count == 0 && IsLoopItemsContractDiagnosticCode(reason))
            requiredPaths = new[] { "items" };
        if (requiredPaths.Count == 0
            && string.Equals(reason, ErrorCodes.ExprTypeMismatch, StringComparison.Ordinal)
            && !HasConcreteExpectedTypeSignal(expected))
        {
            return false;
        }

        demand = new PipelineLeafContractDemand(
            leafName,
            outputName,
            consumerStepId,
            consumerField,
            invalidPath,
            reason,
            requiredPaths,
            IsLoopItemsContractDiagnosticCode(reason) ? expected ?? "array" : expected);
        return true;
    }

    private static bool IsLoopItemsContractDiagnosticCode(string reason)
        => reason is "OPAQUE_ARRAY_LOOP_ITEMS" or "WEAK_ARRAY_LOOP_ITEMS" or "LEAF_OUTPUT_LOOP_ITEMS_NOT_ARRAY";

    private static bool HasConcreteExpectedTypeSignal(string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        var normalized = expected.ToLowerInvariant();
        return normalized.Contains("array", StringComparison.Ordinal)
               || normalized.Contains("object", StringComparison.Ordinal)
               || normalized.Contains("string", StringComparison.Ordinal)
               || normalized.Contains("number", StringComparison.Ordinal)
               || normalized.Contains("integer", StringComparison.Ordinal)
               || normalized.Contains("boolean", StringComparison.Ordinal)
               || normalized.Contains("bool", StringComparison.Ordinal);
    }

    private static bool TryBuildLoopLeafOutputDemand(
        WorkflowDef main,
        IReadOnlySet<string> leafNames,
        string invalidPath,
        string consumerStepId,
        string consumerField,
        string reason,
        string? expected,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineLeafContractDemand? demand)
    {
        demand = null;
        if (string.IsNullOrWhiteSpace(consumerStepId)
            || !TryParseDataVariablePath(invalidPath, out var variableName, out var variablePath)
            || !TryFindStepPath(main.Steps, consumerStepId, Array.Empty<StepDef>(), out var stepPath))
        {
            return false;
        }

        foreach (var loopStep in stepPath.Ancestors.Reverse())
        {
            if (!IsPipelineLoopStep(loopStep))
                continue;

            var requiredItemPath = TryBuildRequiredLoopItemPath(loopStep, variableName, variablePath);
            if (requiredItemPath == null)
                continue;

            if (!TryGetLoopItemsExpression(loopStep, out var itemsExpression)
                || !TryParseStepOutputReference(itemsExpression, out var callStepId, out var outputName, out var outputPath)
                || outputPath.Count > 0
                || !TryGetMainLeafCall(main, leafNames, callStepId, out var leafName))
            {
                continue;
            }

            demand = new PipelineLeafContractDemand(
                leafName,
                outputName,
                consumerStepId,
                consumerField,
                invalidPath,
                reason,
                new[] { requiredItemPath },
                expected ?? "array");
            return true;
        }

        return false;
    }

    private static PipelineLeafContractDemand? MergePipelineLeafContractDemands(IReadOnlyList<PipelineLeafContractDemand> demands)
    {
        if (demands.Count == 0)
            return null;

        return demands
            .GroupBy(demand => (demand.LeafName, demand.OutputName), demand => demand)
            .Select(static group =>
            {
                var first = group.First();
                var requiredPaths = group
                    .SelectMany(static demand => demand.RequiredOutputPaths)
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var reasons = group
                    .Select(static demand => demand.Reason)
                    .Where(static reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return first with
                {
                    Reason = string.Join(", ", reasons),
                    RequiredOutputPaths = requiredPaths,
                    ExpectedType = group.Select(static demand => demand.ExpectedType).FirstOrDefault(static expected => !string.IsNullOrWhiteSpace(expected))
                };
            })
            .OrderByDescending(static demand => demand.RequiredOutputPaths.Count)
            .ThenBy(static demand => demand.LeafName, StringComparer.Ordinal)
            .ThenBy(static demand => demand.OutputName, StringComparer.Ordinal)
            .First();
    }

    private static bool TryGetMainLeafCall(
        WorkflowDef main,
        IReadOnlySet<string> leafNames,
        string stepId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? leafName)
    {
        leafName = null;
        var step = EnumerateSteps(main.Steps).FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.Ordinal));
        if (step?.Input is not JsonObject input
            || !string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            || input["ref"] is not JsonObject refObject)
        {
            return false;
        }

        var kind = refObject["kind"]?.GetValue<string>() ?? "local";
        var targetName = refObject["name"]?.GetValue<string>();
        if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(targetName)
            || !leafNames.Contains(targetName))
        {
            return false;
        }

        leafName = targetName;
        return true;
    }

    private static bool TryFindStepPath(
        IReadOnlyList<StepDef> steps,
        string stepId,
        IReadOnlyList<StepDef> ancestors,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineStepPath? path)
    {
        foreach (var step in steps)
        {
            if (string.Equals(step.Id, stepId, StringComparison.Ordinal))
            {
                path = new PipelineStepPath(step, ancestors.ToArray());
                return true;
            }

            var nestedAncestors = ancestors.Concat(new[] { step }).ToArray();
            if (step.Steps != null && TryFindStepPath(step.Steps, stepId, nestedAncestors, out path))
                return true;

            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                {
                    if (TryFindStepPath(branch.Steps, stepId, nestedAncestors, out path))
                        return true;
                }
            }

            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                {
                    if (TryFindStepPath(@case.Steps, stepId, nestedAncestors, out path))
                        return true;
                }
            }

            if (step.Default != null && TryFindStepPath(step.Default, stepId, nestedAncestors, out path))
                return true;
        }

        path = null;
        return false;
    }

    private static bool IsPipelineLoopStep(StepDef step)
        => string.Equals(step.Type, "loop.sequential", StringComparison.Ordinal)
            || string.Equals(step.Type, "loop.parallel", StringComparison.Ordinal);

    private static string? TryBuildRequiredLoopItemPath(
        StepDef loopStep,
        string variableName,
        IReadOnlyList<string> variablePath)
    {
        var itemVar = loopStep.ItemVar ?? "item";
        if (string.Equals(variableName, itemVar, StringComparison.Ordinal))
        {
            return variablePath.Count == 0
                ? "items"
                : "items." + string.Join('.', variablePath);
        }

        if ((string.Equals(variableName, "_loop", StringComparison.Ordinal) || string.Equals(variableName, "loop", StringComparison.Ordinal))
            && variablePath.Count > 0
            && string.Equals(variablePath[0], "item", StringComparison.Ordinal))
        {
            return variablePath.Count == 1
                ? "items"
                : "items." + string.Join('.', variablePath.Skip(1));
        }

        return null;
    }

    private static bool TryGetLoopItemsExpression(
        StepDef loopStep,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? expression)
    {
        expression = null;
        if (loopStep.Input is not JsonObject input)
            return false;

        JsonNode? itemsNode = null;
        if (input.TryGetPropertyValue("items", out var items) && items != null)
            itemsNode = items;
        else if (string.Equals(loopStep.Type, "loop.sequential", StringComparison.Ordinal)
                 && input.TryGetPropertyValue("over", out var over)
                 && over != null)
            itemsNode = over;

        if (itemsNode is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            return false;

        expression = text;
        return true;
    }

    private static bool TryParseDataVariablePath(
        string invalidPath,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? variableName,
        out IReadOnlyList<string> variablePath)
    {
        variableName = null;
        variablePath = Array.Empty<string>();
        var path = TrimWorkflowExpression(invalidPath);
        const string prefix = "data.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = path[prefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments[0] is "inputs" or "steps")
            return false;

        variableName = segments[0];
        variablePath = segments.Skip(1).ToArray();
        return true;
    }

    private static bool TryParseStepOutputReference(
        string reference,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? stepId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? outputName,
        out IReadOnlyList<string> remainingPath)
    {
        stepId = null;
        outputName = null;
        remainingPath = Array.Empty<string>();
        var path = TrimWorkflowExpression(reference);
        const string prefix = "data.steps.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = path[prefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3 || !string.Equals(segments[1], "outputs", StringComparison.Ordinal))
            return false;

        stepId = segments[0];
        outputName = segments[2];
        remainingPath = segments.Skip(3).ToArray();
        return true;
    }

    private static string TrimWorkflowExpression(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'))
            return text[2..^1].Trim();
        return text;
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
        (doc, yaml) = PromoteLeafOutputSchemasFromDirectSources(spec.Name, workflowName, doc, yaml);
        var workflow = doc.Workflows[workflowName];
        EnforceStrongObjectSchemas(spec.Name, doc);
        EnforceStrongArrayOutputSchemas(spec.Name, spec, workflowName, doc);
        EnforcePlannedMcpToolsUsed(spec, workflow);
        EnforcePipelineLeafIntent(spec, workflow);
        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            if (step.Type is "workflow.call" or "workflow.plan")
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, $"Leaf workflow '{spec.Name}' must not contain step type '{step.Type}'.");
        }

        return new GeneratedLeafWorkflow(spec.Name, workflowName, doc, yaml);
    }

    private static (WorkflowDocument Document, string Yaml) PromoteLeafOutputSchemasFromDirectSources(
        string leafName,
        string workflowName,
        WorkflowDocument doc,
        string yaml)
    {
        if (!doc.Workflows.TryGetValue(workflowName, out var workflow) || workflow.Outputs == null || workflow.Outputs.Count == 0)
            return (doc, yaml);

        var stepOutputTypes = BuildLeafStepOutputTypeMap(workflowName, doc, workflow);
        var workflowOutputTypes = workflow.Outputs
            .ToDictionary(
                static pair => pair.Key,
                static pair => FlowTypeDescriptorConverter.FromOutputDef(pair.Value),
                StringComparer.Ordinal);
        var promotions = new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);
        foreach (var (outputName, output) in workflow.Outputs)
        {
            if (!ShouldPromoteLeafOutputSchema(output))
                continue;

            if (!TryResolveDirectLeafOutputSource(output.Expr, stepOutputTypes, out var sourceType) || sourceType.IsOpaque)
                continue;

            promotions[outputName] = string.IsNullOrWhiteSpace(output.Description)
                ? sourceType
                : sourceType with { Description = output.Description };
        }

        var root = LoadYamlRoot(yaml);
        var workflowsNode = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{leafName}' YAML is missing workflows.");
        if (!workflowsNode.Children.TryGetValue(Scalar(workflowName), out var workflowNode) || workflowNode is not YamlMappingNode workflowMap)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{leafName}' YAML does not contain workflow '{workflowName}'.");

        var workflowOutputs = workflowMap.GetMapping("outputs") ?? new YamlMappingNode();
        foreach (var (outputName, descriptor) in promotions)
        {
            var output = workflow.Outputs[outputName];
            ReplaceYaml(workflowOutputs, outputName, BuildPromotedLeafOutputYaml(output, descriptor));
        }
        if (!ContainsYamlKey(workflowMap, "outputs"))
            AddYaml(workflowMap, "outputs", workflowOutputs);

        if (root.GetMapping("skill")?.GetMapping("outputs") is { } skillOutputs)
        {
            foreach (var (outputName, workflowOutput) in workflow.Outputs)
            {
                if (!ShouldPromoteLeafOutputSchema(workflowOutput))
                    continue;

                if (!skillOutputs.Children.TryGetValue(Scalar(outputName), out var skillOutputSchema))
                    continue;

                var strengthenedWorkflowOutput = BuildWorkflowOutputFromSkillSchema(skillOutputSchema, workflowOutput.Expr);
                if (strengthenedWorkflowOutput == null || IsWeakYamlOutputSchema(strengthenedWorkflowOutput))
                    continue;

                ReplaceYaml(workflowOutputs, outputName, strengthenedWorkflowOutput);
            }

            foreach (var (outputName, currentOutput) in skillOutputs.Children.ToArray())
            {
                if (outputName is not YamlScalarNode outputKey || string.IsNullOrWhiteSpace(outputKey.Value))
                    continue;

                var currentSkillOutput = currentOutput;
                if (currentSkillOutput is not YamlScalarNode && !IsWeakYamlOutputSchema(currentSkillOutput))
                    continue;

                if (workflowOutputs.Children.TryGetValue(Scalar(outputKey.Value), out var workflowOutputYaml)
                    && TryBuildSkillOutputFromWorkflowOutputYaml(workflowOutputYaml, out var skillOutputFromWorkflow)
                    && !IsWeakYamlOutputSchema(skillOutputFromWorkflow))
                {
                    ReplaceYaml(skillOutputs, outputKey.Value, skillOutputFromWorkflow);
                    continue;
                }

                if (!promotions.TryGetValue(outputKey.Value, out var descriptor)
                    && (!workflowOutputTypes.TryGetValue(outputKey.Value, out descriptor) || IsWeakPipelineOutputDescriptor(descriptor)))
                {
                    continue;
                }

                if (!workflow.Outputs.TryGetValue(outputKey.Value, out var output))
                    continue;

                ReplaceYaml(skillOutputs, outputKey.Value, BuildPromotedLeafOutputYaml(output, descriptor, includeExpr: false));
            }
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var promotedYaml = writer.ToString();
        return (ParseAndValidateGeneratedWorkflow(promotedYaml), promotedYaml);
    }

    private static bool TryBuildSkillOutputFromWorkflowOutputYaml(
        YamlNode workflowOutputYaml,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YamlMappingNode? skillOutput)
    {
        skillOutput = WorkflowPlanContractNormalizer.BuildSkillOutputFromWorkflowOutputYaml(workflowOutputYaml);
        return skillOutput != null;
    }

    private static Dictionary<string, FlowTypeDescriptor> BuildLeafStepOutputTypeMap(
        string workflowName,
        WorkflowDocument doc,
        WorkflowDef workflow)
    {
        var allStepIds = EnumerateSteps(workflow.Steps)
            .Select(static step => step.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var symbols = WorkflowSymbolTable.Create(workflowName, workflow.Inputs, allStepIds);
        var result = new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);
        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                continue;

            var outputType = StepOutputTypeResolver.Resolve(
                step,
                doc.Workflows,
                symbols,
                new Dictionary<(string ServerName, string ToolName), McpToolOutputContract>(),
                BuiltInStepContracts.All);
            symbols.SetStepOutput(step.Id, outputType);
            result[step.Id] = outputType;
        }

        return result;
    }

    private static bool ShouldPromoteLeafOutputSchema(OutputDef output)
    {
        var descriptor = FlowTypeDescriptorConverter.FromOutputDef(output);
        return IsWeakPipelineOutputDescriptor(descriptor);
    }

    private static bool IsWeakPipelineOutputDescriptor(FlowTypeDescriptor descriptor)
        => WorkflowPlanContractNormalizer.IsWeakDescriptor(descriptor);

    private static bool TryResolveDirectLeafOutputSource(
        string expression,
        IReadOnlyDictionary<string, FlowTypeDescriptor> stepOutputTypes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FlowTypeDescriptor? sourceType)
    {
        sourceType = null;
        var path = TrimWorkflowExpression(expression);
        const string prefix = "data.steps.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = path[prefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !stepOutputTypes.TryGetValue(segments[0], out var stepType))
            return false;

        sourceType = segments.Length == 1
            ? stepType
            : stepType.ResolvePath(segments.Skip(1).ToArray());
        return sourceType != null;
    }

    private static YamlNode BuildPromotedLeafOutputYaml(
        OutputDef output,
        FlowTypeDescriptor descriptor,
        bool includeExpr = true)
    {
        var described = string.IsNullOrWhiteSpace(output.Description)
            ? descriptor
            : descriptor with { Description = output.Description };
        if (includeExpr)
            return WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(described, output.Expr)
                   ?? WorkflowPlanContractNormalizer.BuildCanonicalSchemaYaml(described);

        return WorkflowPlanContractNormalizer.BuildCanonicalSchemaYaml(described);
    }

    private static bool IsWeakYamlOutputSchema(YamlNode node)
        => WorkflowPlanContractNormalizer.IsWeakYamlOutputSchema(node);

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

    private static void EnforcePipelineLeafIntent(WorkflowPipelineSubworkflowSpec spec, WorkflowDef workflow)
    {
        var isExternalWork = IsExternalWorkSpec(spec);
        var hasActionStep = WorkflowContainsExecutableActionStep(workflow);
        var diagnostics = new JsonArray();

        foreach (var fakeAction in EnumerateFakeActionEmitDiagnostics(spec, workflow))
            diagnostics.Add((JsonNode)fakeAction);

        if (isExternalWork && !hasActionStep)
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "PIPELINE_LEAF_EXTERNAL_WORK_WITHOUT_ACTION",
                ["phase"] = "pipeline_leaf_intent_validation",
                ["leaf"] = spec.Name,
                ["work_kind"] = spec.WorkKind ?? PipelineWorkKindExternalWork,
                ["message"] = "The leaf is external work but contains no executable action step.",
                ["expected"] = "Use a real mcp.call, llm.call, template.render, human.input, or another executable external/action step required by the leaf goal."
            });
        }

        if (LeafClaimsSideEffectSuccess(spec, workflow) && !hasActionStep)
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "PIPELINE_LEAF_SUCCESS_OUTPUT_WITHOUT_ACTION",
                ["phase"] = "pipeline_leaf_intent_validation",
                ["leaf"] = spec.Name,
                ["message"] = "The leaf claims side-effect success but has no step that can perform the side effect.",
                ["expected"] = "Base success outputs on a real action step response, or remove the side-effect success claim."
            });
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_leaf_intent_validation",
            ["leaf"] = spec.Name,
            ["summary"] = $"{diagnostics.Count} pipeline leaf intent diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Regenerate only this leaf. Preserve its public contract, and replace fake/claimed side effects with real executable workflow steps.")!)
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' failed intent validation. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static bool WorkflowContainsExecutableActionStep(WorkflowDef workflow)
        => EnumerateSteps(workflow.Steps).Any(static step => IsExecutableActionStepType(step.Type));

    private static bool IsExecutableActionStepType(string? stepType)
        => stepType is "mcp.call" or "llm.call" or "template.render" or "human.input" or "mcp.list";

    private static IEnumerable<JsonObject> EnumerateFakeActionEmitDiagnostics(
        WorkflowPipelineSubworkflowSpec spec,
        WorkflowDef workflow)
    {
        if (!IsExternalWorkSpec(spec) || WorkflowContainsExecutableActionStep(workflow))
            yield break;

        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            if (!string.Equals(step.Type, "emit", StringComparison.Ordinal))
                continue;

            foreach (var text in EnumerateJsonScalarStrings(step.Input))
            {
                if (!FakeActionTextRegex().IsMatch(text))
                    continue;

                yield return new JsonObject
                {
                    ["code"] = "PIPELINE_LEAF_FAKE_ACTION_EMIT",
                    ["phase"] = "pipeline_leaf_intent_validation",
                    ["leaf"] = spec.Name,
                    ["step"] = step.Id,
                    ["message"] = "The leaf emits an instruction that describes external work instead of performing it.",
                    ["invalid_text"] = text.Length > 300 ? text[..300] : text,
                    ["expected"] = "Replace the emit-only instruction with a real executable step for the action."
                };
            }
        }
    }

    private static bool LeafClaimsSideEffectSuccess(WorkflowPipelineSubworkflowSpec spec, WorkflowDef workflow)
    {
        if (!IsExternalWorkSpec(spec))
            return false;

        if (workflow.Outputs == null)
            return false;

        foreach (var (name, output) in workflow.Outputs)
        {
            var text = string.Join(' ', new[]
            {
                name,
                output.Description,
                output.Expr
            }.Where(static value => !string.IsNullOrWhiteSpace(value)))!;

            if (SideEffectSuccessOutputRegex().IsMatch(text))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateJsonScalarStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                break;

            case JsonObject obj:
                foreach (var (_, child) in obj)
                foreach (var item in EnumerateJsonScalarStrings(child))
                    yield return item;
                break;

            case JsonArray array:
                foreach (var child in array)
                foreach (var item in EnumerateJsonScalarStrings(child))
                    yield return item;
                break;
        }
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
        var mainStepOutputTypes = AnalyzePipelineMainStepOutputs(mainWorkflowNode, leaves);
        var skillNode = BuildPipelineSkillNode(documentName, pipelineInput, generator, extraction, leaves, mainWorkflowNode, mainStepOutputTypes, mainInputs, assembly?.SkillNode);
        StrengthenPipelineSkillOutputsFromMainWorkflow(skillNode, mainWorkflowNode, mainStepOutputTypes);
        StrengthenMainWorkflowOutputsFromSkill(mainWorkflowNode, skillNode.GetMapping("outputs"));
        StrengthenMainWorkflowOutputsFromAnalyzedSteps(mainWorkflowNode, mainStepOutputTypes);

        var workflowsNode = new YamlMappingNode();
        AddYaml(workflowsNode, "main", mainWorkflowNode);
        foreach (var leaf in leaves)
            AddYaml(workflowsNode, leaf.Name, ExtractSingleWorkflowNode(leaf.Yaml, leaf.GeneratedWorkflowName));

        var root = new YamlMappingNode();
        AddYaml(root, "version", Scalar("1"));
        AddYaml(root, "name", Scalar(documentName));
        AddYaml(root, "skill", skillNode);
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
        sb.AppendLine("- Keep simple deterministic work in the main graph: renames, constants, guards, field mapping, routing, aggregation, and loop orchestration.");
        sb.AppendLine("- The main graph may only use compact leaf calls plus support nodes: `set`, `sequence`, `switch`, `parallel`, `loop.sequential`, and `loop.parallel`.");
        sb.AppendLine("- The main graph must not emit `mcp.call`, `llm.call`, `template.render`, `human.input`, `workflow.plan`, or inline leaf implementation logic.");
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
        sb.AppendLine("- Do not expose loop variables, intermediate values, identifiers, flags, or leaf-only implementation details as public inputs unless the user explicitly requested them.");
        sb.AppendLine("- Use `set` support nodes for data shaping in the main graph: renaming fields, building objects/arrays, constants, and safe type conversions.");
        sb.AppendLine("- Keep exact JSON values intact when passing arrays, objects, numbers, or booleans. Do not stringify a structured leaf output unless a downstream leaf explicitly wants a string.");
        sb.AppendLine("- Every generated `document.skill.outputs` and `graph.outputs` entry must be strongly typed: no `any`, no bare `object`, and no bare `array` without `items`.");
        sb.AppendLine("- Array outputs must declare concrete `items`; object outputs and object array items must declare non-empty `properties`.");
        sb.AppendLine("- If a leaf call is inside a switch, loop, parallel branch, or conditional path, do not reference that leaf call step from outside that container/path. Put dependent work in the same path, or expose the container step itself as the output.");
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
          functions: |         # optional workflow-local JavaScript helpers for deterministic projection only; every function needs JSDoc
            /**
             * Projects loop iteration snapshots into public result objects.
             * @param {Array<object>} iterations - Per-iteration loop result snapshots.
             * @returns {Array<object>} Clean public result objects.
             */
            function projectResults(iterations) { return []; }
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
        - Never expose raw `${data.steps.<loop_id>.results}` as a public business output. It contains full per-iteration step snapshots and will not match a clean public array contract.
        - To flatten loop results, add a post-loop `set` support node with an `output_schema`, project exact declared fields into a clean array, and point graph.outputs at that set field.
        - If flattening needs array map/filter logic, define a deterministic helper in `graph.functions` and call it from the post-loop `set` input. The renderer copies `graph.functions` to the generated main workflow.
        - Every helper in `graph.functions` must have a JSDoc block immediately before the `function` declaration, including `@param` and `@returns`.
        - Projection helpers must read child step outputs through the iteration snapshot, for example `iteration.build_issue_result.status` or `iteration.route_by_classification.summarize_issue_result_bug.status`.
        - switch output is path-dependent. Do not reference case/default child step ids after the switch unless the reference remains inside that same case/default path.
        - Do not flatten switch child fields onto the switch step. Invalid: `${data.steps.route.pr_url}` when `pr_url` is produced by a child `set` inside a case/default branch.
        - For final graph.outputs after containers, return only projected/typed outputs that match the public contract. Do not return raw loop snapshots or raw branch snapshots as business outputs.

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

        loop projection shape:
        functions: |
          /**
           * Projects loop iteration snapshots into processed item results.
           * @param {Array<object>} iterations - Per-iteration loop result snapshots.
           * @returns {Array<object>} Clean public processed item results.
           */
          function projectProcessedItems(iterations) {
            if (!Array.isArray(iterations)) return [];
            return iterations.map(function (iteration) {
              var shaped = iteration && iteration.shape_item ? iteration.shape_item : {};
              return {
                id: shaped.id || "",
                status: shaped.status || "unknown"
              };
            });
          }
        steps:
          - id: process_items
            type: loop.sequential
            input:
              items: ${data.steps.call_list_items.outputs.items}
            item_var: item
            steps:
              - id: shape_item
                type: set
                output_schema:
                  type: object
                  properties:
                    id: { type: string }
                    status: { type: string }
                  required_properties: [id, status]
                input:
                  id: ${data.item.id}
                  status: done
          - id: project_processed_items
            type: set
            output_schema:
              type: object
              properties:
                result:
                  type: array
                  items:
                    type: object
                    properties:
                      id: { type: string }
                      status: { type: string }
                    required_properties: [id, status]
              required_properties: [result]
            input:
              result: ${functions.projectProcessedItems(data.steps.process_items.results)}
        outputs:
          result: ${data.steps.project_processed_items.result}

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

        var functions = graph.GetScalar("functions");
        if (!string.IsNullOrWhiteSpace(functions))
            AddYaml(main, "functions", LiteralScalar(functions));

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
                ["generated_workflow"] = leaf.GeneratedWorkflowName,
                ["goal"] = spec?.Goal ?? "",
                ["description"] = spec?.Description,
                ["work_kind"] = spec?.WorkKind,
                ["extraction_score"] = spec?.ExtractionScore == null ? null : BuildPipelineExtractionScoreJson(spec.ExtractionScore),
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

    private static JsonObject BuildPipelineInspectionJson(
        string normalizedMarkdown,
        string annotatedMarkdown,
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        WorkflowDocument finalDoc,
        IReadOnlyList<PipelineQualityEvent> events)
    {
        finalDoc.Workflows.TryGetValue("main", out var mainWorkflow);
        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["leaf_count"] = leaves.Count,
                ["repair_count"] = events.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)),
                ["main_step_count"] = mainWorkflow?.Steps.Count ?? 0,
                ["workflow_count"] = finalDoc.Workflows.Count
            },
            ["normalized_prompt"] = normalizedMarkdown,
            ["annotated_markdown"] = annotatedMarkdown,
            ["leaf_manifest"] = BuildGeneratedLeafManifestJson(leaves, extraction),
            ["generated_leaf_contracts"] = BuildGeneratedLeafContractsJson(leaves),
            ["final_main_graph"] = mainWorkflow == null
                ? new JsonObject { ["missing"] = true }
                : BuildWorkflowGraphInspectionJson("main", mainWorkflow, finalDoc.Skill?.Outputs),
            ["repair_history"] = BuildPipelineQualityEventsJson(events.Where(static item => item.Kind.Contains("repair", StringComparison.Ordinal)))
        };
    }

    private static JsonObject BuildGeneratedLeafContractsJson(IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var obj = new JsonObject();
        foreach (var leaf in leaves)
        {
            obj[leaf.Name] = new JsonObject
            {
                ["workflow"] = leaf.Name,
                ["generated_workflow"] = leaf.GeneratedWorkflowName,
                ["inputs"] = BuildSchemaMapJson(BuildLeafInputSchemaMap(leaf)),
                ["outputs"] = BuildSchemaMapJson(BuildLeafOutputSchemaMap(leaf))
            };
        }

        return obj;
    }

    private static JsonObject BuildWorkflowGraphInspectionJson(
        string workflowName,
        WorkflowDef workflow,
        IReadOnlyDictionary<string, OutputDef>? skillOutputs)
    {
        return new JsonObject
        {
            ["workflow"] = workflowName,
            ["has_functions"] = !string.IsNullOrWhiteSpace(workflow.Functions),
            ["inputs"] = workflow.Inputs == null
                ? new JsonObject()
                : BuildSchemaMapJson(BuildInputSchemaMap(workflow.Inputs)),
            ["steps"] = BuildStepInspectionArray(workflow.Steps),
            ["outputs"] = workflow.Outputs == null
                ? new JsonObject()
                : BuildWorkflowOutputInspectionJson(workflow.Outputs, skillOutputs)
        };
    }

    private static JsonArray BuildStepInspectionArray(IReadOnlyList<StepDef> steps)
    {
        var array = new JsonArray();
        foreach (var step in steps)
            array.Add((JsonNode)BuildStepInspectionJson(step));
        return array;
    }

    private static JsonObject BuildStepInspectionJson(StepDef step)
    {
        var obj = new JsonObject
        {
            ["id"] = step.Id,
            ["type"] = step.Type
        };

        if (!string.IsNullOrWhiteSpace(step.If))
            obj["if"] = step.If;
        if (!string.IsNullOrWhiteSpace(step.Output))
            obj["output"] = step.Output;
        if (step.OutputSchema != null)
            obj["output_schema"] = step.OutputSchema.DeepClone();

        if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            && step.Input is JsonObject callInput)
        {
            if (callInput["ref"] is JsonObject refObj)
                obj["leaf"] = GetStringProperty(refObj, "name");
            if (callInput["args"] is JsonObject args)
                obj["args"] = args.DeepClone();
        }
        else if (step.Input != null)
        {
            obj["input"] = step.Input.DeepClone();
        }

        if (step.Steps is { Count: > 0 })
            obj["steps"] = BuildStepInspectionArray(step.Steps);
        if (step.Default is { Count: > 0 })
            obj["default"] = BuildStepInspectionArray(step.Default);
        if (step.Branches is { Count: > 0 })
        {
            var branches = new JsonArray();
            for (var i = 0; i < step.Branches.Count; i++)
            {
                branches.Add((JsonNode)new JsonObject
                {
                    ["index"] = i,
                    ["steps"] = BuildStepInspectionArray(step.Branches[i].Steps)
                });
            }
            obj["branches"] = branches;
        }
        if (step.Cases is { Count: > 0 })
        {
            var cases = new JsonArray();
            foreach (var @case in step.Cases)
            {
                var caseObj = new JsonObject
                {
                    ["steps"] = BuildStepInspectionArray(@case.Steps)
                };
                if (!string.IsNullOrWhiteSpace(@case.Value))
                    caseObj["value"] = @case.Value;
                if (!string.IsNullOrWhiteSpace(@case.When))
                    caseObj["when"] = @case.When;
                cases.Add((JsonNode)caseObj);
            }
            obj["cases"] = cases;
        }

        return obj;
    }

    private static JsonObject BuildWorkflowOutputInspectionJson(
        IReadOnlyDictionary<string, OutputDef> outputs,
        IReadOnlyDictionary<string, OutputDef>? skillOutputs)
    {
        var schemas = BuildOutputSchemaMap(outputs, skillOutputs);
        var obj = new JsonObject();
        foreach (var (name, output) in outputs)
        {
            obj[name] = new JsonObject
            {
                ["expr"] = output.Expr,
                ["schema"] = schemas.TryGetValue(name, out var schema)
                    ? schema?.DeepClone()
                    : OutputDefToContractNode(output)
            };
        }

        return obj;
    }

    private static JsonObject BuildPipelineQualityReportJson(
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        WorkflowDocument finalDoc,
        IReadOnlyList<PipelineQualityEvent> events)
    {
        finalDoc.Workflows.TryGetValue("main", out var main);
        var mainSteps = main == null
            ? Array.Empty<StepDef>()
            : EnumerateSteps(main.Steps).ToArray();
        var totalStepCount = finalDoc.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps))
            .Count();
        var warnings = BuildPipelineQualityWarningsJson(extraction);
        var skillOutputSchemas = finalDoc.Skill?.Outputs == null
            ? new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            : BuildOutputSchemaMap(finalDoc.Skill.Outputs, null);
        var mainOutputSchemas = main?.Outputs == null
            ? new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            : BuildOutputSchemaMap(main.Outputs, finalDoc.Skill?.Outputs);
        var extractionScores = extraction.Subworkflows
            .Select(static spec => spec.ExtractionScore)
            .Where(static score => score != null)
            .Select(static score => score!)
            .ToArray();

        var summary = new JsonObject
        {
            ["workflow_count"] = finalDoc.Workflows.Count,
            ["leaf_count"] = leaves.Count,
            ["main_step_count"] = mainSteps.Length,
            ["total_step_count"] = totalStepCount,
            ["external_work_leaf_count"] = extraction.Subworkflows.Count(static spec => string.Equals(spec.WorkKind, PipelineWorkKindExternalWork, StringComparison.Ordinal)),
            ["deterministic_shaping_leaf_count"] = extraction.Subworkflows.Count(static spec => string.Equals(spec.WorkKind, PipelineWorkKindDeterministicShaping, StringComparison.Ordinal)),
            ["orchestration_leaf_count"] = extraction.Subworkflows.Count(static spec => string.Equals(spec.WorkKind, PipelineWorkKindOrchestration, StringComparison.Ordinal)),
            ["unknown_work_kind_leaf_count"] = extraction.Subworkflows.Count(static spec => string.IsNullOrWhiteSpace(spec.WorkKind)),
            ["planned_tool_count"] = extraction.Subworkflows.Sum(static spec => spec.PlannedTools.Count),
            ["required_planned_tool_count"] = extraction.Subworkflows.Sum(static spec => spec.PlannedTools.Count(static tool => tool.Required)),
            ["skill_output_count"] = skillOutputSchemas.Count,
            ["main_output_count"] = mainOutputSchemas.Count,
            ["repair_count"] = events.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)),
            ["main_retry_count"] = events.Count(static item => string.Equals(item.Kind, "main_assembly_retry", StringComparison.Ordinal)),
            ["leaf_contract_repair_count"] = events.Count(static item => string.Equals(item.Kind, "leaf_contract_repair", StringComparison.Ordinal)),
            ["warning_count"] = warnings.Count
        };
        if (extractionScores.Length > 0)
        {
            summary["extraction_scored_leaf_count"] = extractionScores.Length;
            summary["min_extraction_score"] = extractionScores.Min(static score => score.Score);
            summary["average_extraction_score"] = Math.Round(extractionScores.Average(static score => score.Score), 2);
        }

        return new JsonObject
        {
            ["status"] = "passed",
            ["summary"] = summary,
            ["checks"] = new JsonObject
            {
                ["extraction_validated"] = true,
                ["leaf_intent_validated"] = true,
                ["leaf_contracts_validated"] = true,
                ["main_dataflow_validated"] = true,
                ["strong_output_schemas_validated"] = true,
                ["workflow_hierarchy_validated"] = true
            },
            ["extraction"] = new JsonObject
            {
                ["main_workflow_prompt"] = extraction.MainWorkflowPrompt,
                ["validation"] = BuildValidationJson(extraction.ValidationErrors)
            },
            ["leaves"] = BuildPipelineQualityLeavesJson(extraction, leaves),
            ["contracts"] = new JsonObject
            {
                ["skill_outputs"] = BuildSchemaMapJson(skillOutputSchemas),
                ["main_outputs"] = BuildSchemaMapJson(mainOutputSchemas),
                ["leaf_outputs"] = BuildPipelineQualityLeafOutputsJson(leaves)
            },
            ["repairs"] = BuildPipelineQualityEventsJson(events.Where(static item => item.Kind.Contains("repair", StringComparison.Ordinal))),
            ["events"] = BuildPipelineQualityEventsJson(events),
            ["warnings"] = warnings
        };
    }

    private static JsonArray BuildPipelineQualityLeavesJson(
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var leavesByName = leaves.ToDictionary(static leaf => leaf.Name, StringComparer.Ordinal);
        var array = new JsonArray();
        foreach (var spec in extraction.Subworkflows)
        {
            leavesByName.TryGetValue(spec.Name, out var leaf);
            var workflow = leaf == null ? null : GetGeneratedLeafWorkflow(leaf);
            var steps = workflow == null
                ? Array.Empty<StepDef>()
                : EnumerateSteps(workflow.Steps).ToArray();
            var item = new JsonObject
            {
                ["name"] = spec.Name,
                ["goal"] = spec.Goal,
                ["description"] = spec.Description,
                ["work_kind"] = spec.WorkKind,
                ["extract_reason"] = spec.ExtractReason,
                ["extraction_score"] = spec.ExtractionScore == null ? null : BuildPipelineExtractionScoreJson(spec.ExtractionScore),
                ["planned_tools"] = BuildPlannedToolsJson(spec.PlannedTools),
                ["required_planned_tool_count"] = spec.PlannedTools.Count(static tool => tool.Required),
                ["declared_input_schemas"] = BuildSchemaMapJson(spec.InputSchemas),
                ["declared_output_schemas"] = BuildSchemaMapJson(spec.OutputSchemas),
                ["generated"] = leaf != null
            };

            if (leaf != null)
            {
                item["generated_workflow_name"] = leaf.GeneratedWorkflowName;
                item["step_count"] = steps.Length;
                item["action_step_count"] = steps.Count(static step => IsExecutableActionStepType(step.Type));
                item["input_contracts"] = BuildSchemaMapJson(BuildLeafInputSchemaMap(leaf));
                item["output_contracts"] = BuildSchemaMapJson(BuildLeafOutputSchemaMap(leaf));
            }

            array.Add((JsonNode)item);
        }

        return array;
    }

    private static JsonObject BuildPipelineQualityLeafOutputsJson(IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var obj = new JsonObject();
        foreach (var leaf in leaves)
        {
            obj[leaf.Name] = new JsonObject
            {
                ["generated_workflow_name"] = leaf.GeneratedWorkflowName,
                ["outputs"] = BuildSchemaMapJson(BuildLeafOutputSchemaMap(leaf))
            };
        }

        return obj;
    }

    private static JsonObject BuildPipelineExtractionScoreJson(PipelineExtractionScore score)
        => new()
        {
            ["score"] = score.Score,
            ["threshold"] = score.Threshold,
            ["rating"] = score.Rating,
            ["reasons"] = BuildStringArrayJson(score.Reasons),
            ["diagnostics"] = BuildStringArrayJson(score.Diagnostics),
            ["hints"] = BuildStringArrayJson(score.Hints)
        };

    private static JsonArray BuildPipelineQualityEventsJson(IEnumerable<PipelineQualityEvent> events)
    {
        var array = new JsonArray();
        foreach (var item in events)
        {
            var obj = new JsonObject
            {
                ["kind"] = item.Kind,
                ["attempt"] = item.Attempt
            };
            if (!string.IsNullOrWhiteSpace(item.Phase))
                obj["phase"] = item.Phase;
            if (!string.IsNullOrWhiteSpace(item.LeafName))
                obj["leaf"] = item.LeafName;
            if (!string.IsNullOrWhiteSpace(item.OutputName))
                obj["output"] = item.OutputName;
            if (!string.IsNullOrWhiteSpace(item.ConsumerStepId))
                obj["consumer_step"] = item.ConsumerStepId;
            if (!string.IsNullOrWhiteSpace(item.ConsumerField))
                obj["consumer_field"] = item.ConsumerField;
            if (!string.IsNullOrWhiteSpace(item.InvalidPath))
                obj["invalid_path"] = item.InvalidPath;
            if (!string.IsNullOrWhiteSpace(item.Reason))
                obj["reason"] = item.Reason;
            if (item.RequiredOutputPaths is { Count: > 0 })
                obj["required_output_paths"] = BuildStringArrayJson(item.RequiredOutputPaths);
            if (!string.IsNullOrWhiteSpace(item.ExpectedType))
                obj["expected_type"] = item.ExpectedType;
            if (!string.IsNullOrWhiteSpace(item.ErrorType))
                obj["error_type"] = item.ErrorType;
            if (!string.IsNullOrWhiteSpace(item.Message))
                obj["message"] = item.Message;

            array.Add((JsonNode)obj);
        }

        return array;
    }

    private static JsonArray BuildPipelineQualityWarningsJson(WorkflowPipelineExtraction extraction)
    {
        var warnings = new JsonArray();
        foreach (var error in extraction.ValidationErrors)
        {
            warnings.Add((JsonNode)new JsonObject
            {
                ["code"] = "PIPELINE_EXTRACTION_VALIDATION_ERROR",
                ["message"] = error
            });
        }

        return warnings;
    }

    private static string TruncatePipelineQualityMessage(string? message, int maxLength = 700)
    {
        if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
            return message ?? "";

        return message[..maxLength] + "...";
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
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, FlowTypeDescriptor> mainStepOutputTypes,
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
            foreach (var output in BuildPipelineSkillOutputsFromMainWorkflow(mainWorkflowNode, mainStepOutputTypes).Children)
                outputs.Add(CloneYamlNode(output.Key), CloneYamlNode(output.Value));
        }

        if (outputs.Children.Count == 0)
        {
            foreach (var spec in extraction.Subworkflows)
            {
                var output = BuildPipelineSkillOutputEnvelope(spec, leaves);
                if (output != null)
                    AddYaml(outputs, $"{spec.Name}_outputs", output);
            }
        }
        AddYaml(skill, "outputs", outputs);

        return skill;
    }

    private static void StrengthenPipelineSkillOutputsFromMainWorkflow(
        YamlMappingNode skillNode,
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, FlowTypeDescriptor> mainStepOutputTypes)
    {
        if (skillNode.GetMapping("outputs") is not { } outputs)
            return;

        var derivedOutputs = BuildPipelineSkillOutputsFromMainWorkflow(mainWorkflowNode, mainStepOutputTypes);
        if (derivedOutputs.Children.Count == 0)
            return;

        foreach (var (keyNode, currentOutput) in outputs.Children.ToArray())
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                continue;

            if (!IsWeakYamlOutputSchema(currentOutput))
                continue;

            if (!derivedOutputs.Children.TryGetValue(Scalar(key.Value), out var derivedOutput)
                || IsWeakYamlOutputSchema(derivedOutput))
            {
                continue;
            }

            ReplaceYaml(outputs, key.Value, derivedOutput);
        }
    }

    private static YamlMappingNode BuildPipelineSkillOutputsFromMainWorkflow(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, FlowTypeDescriptor> mainStepOutputTypes)
    {
        var outputs = new YamlMappingNode();
        if (mainWorkflowNode.GetMapping("outputs") is not { } mainOutputs)
            return outputs;

        foreach (var (keyNode, outputNode) in mainOutputs.Children)
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                continue;

            var skillOutput = WorkflowPlanContractNormalizer.BuildSkillOutputFromWorkflowOutputYaml(outputNode);
            if (skillOutput == null
                && TryGetYamlOutputExpression(outputNode, out var expr)
                && TryBuildWorkflowOutputFromAnalyzedStepExpression(expr, mainStepOutputTypes, out var workflowOutput))
            {
                skillOutput = WorkflowPlanContractNormalizer.BuildSkillOutputFromWorkflowOutputYaml(workflowOutput);
            }

            if (skillOutput != null && !IsWeakYamlOutputSchema(skillOutput))
            {
                AddYaml(outputs, key.Value, skillOutput);
            }
        }

        return outputs;
    }

    private static YamlMappingNode? BuildPipelineSkillOutputEnvelope(
        WorkflowPipelineSubworkflowSpec spec,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var schemas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var leaf = leaves.FirstOrDefault(leaf => string.Equals(leaf.Name, spec.Name, StringComparison.Ordinal));
        if (leaf != null)
        {
            foreach (var (name, schema) in BuildLeafOutputSchemaMap(leaf))
                schemas[name] = schema?.DeepClone();
        }

        if (schemas.Count == 0)
        {
            foreach (var (name, schema) in spec.OutputSchemas)
                schemas[name] = schema?.DeepClone();
        }

        if (schemas.Count == 0)
        {
            foreach (var (name, type) in spec.Outputs)
                schemas[name] = JsonValue.Create(type);
        }

        var properties = new YamlMappingNode();
        var requiredProperties = new YamlSequenceNode();
        foreach (var (name, schema) in schemas)
        {
            var property = JsonSchemaToWorkflowOutputSchemaYaml(schema);
            if (IsWeakYamlOutputSchema(property))
                continue;

            AddYaml(properties, name, property);
            requiredProperties.Add(Scalar(name));
        }

        if (properties.Children.Count == 0)
            return null;

        var output = new YamlMappingNode();
        AddYaml(output, "type", Scalar("object"));
        AddYaml(output, "description", Scalar($"Outputs from the {spec.Name} leaf workflow."));
        AddYaml(output, "properties", properties);
        AddYaml(output, "required_properties", requiredProperties);
        return output;
    }

    private static IReadOnlyDictionary<string, FlowTypeDescriptor> AnalyzePipelineMainStepOutputs(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        try
        {
            var workflowsNode = new YamlMappingNode();
            AddYaml(workflowsNode, "main", mainWorkflowNode);
            foreach (var leaf in leaves)
                AddYaml(workflowsNode, leaf.Name, ExtractSingleWorkflowNode(leaf.Yaml, leaf.GeneratedWorkflowName));

            var root = new YamlMappingNode();
            AddYaml(root, "version", Scalar("1"));
            AddYaml(root, "name", Scalar("pipeline-analysis"));
            AddYaml(root, "workflows", workflowsNode);

            var stream = new YamlStream(new YamlDocument(root));
            using var writer = new StringWriter();
            stream.Save(writer, assignAnchors: false);
            var doc = WorkflowParser.Parse(writer.ToString());
            if (!doc.Workflows.TryGetValue("main", out var main))
                return new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);

            return WorkflowStepOutputAnalyzer
                .AnalyzeWorkflow("main", main, doc.Workflows)
                .StepOutputs;
        }
        catch
        {
            return new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);
        }
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

    private static void StrengthenMainWorkflowOutputsFromAnalyzedSteps(
        YamlMappingNode mainWorkflowNode,
        IReadOnlyDictionary<string, FlowTypeDescriptor> mainStepOutputTypes)
    {
        if (mainWorkflowNode.GetMapping("outputs") is not { } outputs || mainStepOutputTypes.Count == 0)
            return;

        foreach (var (keyNode, valueNode) in outputs.Children.ToArray())
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                continue;

            if (valueNode is not YamlScalarNode && !IsWeakYamlOutputSchema(valueNode)
                || !TryGetYamlOutputExpression(valueNode, out var expr)
                || !TryBuildWorkflowOutputFromAnalyzedStepExpression(expr, mainStepOutputTypes, out var strengthened)
                || IsWeakYamlOutputSchema(strengthened))
            {
                continue;
            }

            ReplaceYaml(outputs, key.Value, strengthened);
        }
    }

    private static bool TryBuildWorkflowOutputFromAnalyzedStepExpression(
        string expr,
        IReadOnlyDictionary<string, FlowTypeDescriptor> mainStepOutputTypes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YamlMappingNode? output)
    {
        output = null;
        if (!TryParseExactStepPathExpression(expr, out var stepId, out var path)
            || !mainStepOutputTypes.TryGetValue(stepId, out var stepType))
        {
            return false;
        }

        var selected = stepType.ResolvePath(path);
        if (selected == null || selected.IsOpaque)
            return false;

        output = WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(selected, expr);
        return output != null;
    }

    private static void StrengthenMainWorkflowOutputsFromSkill(
        YamlMappingNode mainWorkflowNode,
        YamlMappingNode? skillOutputs)
    {
        if (skillOutputs == null || mainWorkflowNode.GetMapping("outputs") is not { } outputs)
            return;

        foreach (var (keyNode, valueNode) in outputs.Children.ToArray())
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                continue;

            if (!TryGetYamlOutputExpression(valueNode, out var expr))
                continue;

            if (!skillOutputs.Children.TryGetValue(Scalar(key.Value), out var skillOutputSchema))
                continue;

            var strengthened = BuildWorkflowOutputFromSkillSchema(skillOutputSchema, expr);
            if (strengthened == null || IsWeakYamlOutputSchema(strengthened))
                continue;

            ReplaceYaml(outputs, key.Value, strengthened);
        }
    }

    private static bool TryParseExactStepPathExpression(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? stepId,
        out IReadOnlyList<string> path)
    {
        stepId = null;
        path = Array.Empty<string>();
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("${", StringComparison.Ordinal) || !trimmed.EndsWith('}'))
            return false;

        var inner = trimmed[2..^1].Trim();
        var match = ExactStepPathExpressionRegex().Match(inner);
        if (!match.Success)
            return false;

        stepId = match.Groups["step"].Value;
        path = match.Groups["path"].Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
    }

    private static YamlNode JsonSchemaToWorkflowOutputSchemaYaml(JsonNode? schema)
        => WorkflowPlanContractNormalizer.BuildCanonicalSchemaYaml(schema);

    private static bool TryGetYamlOutputExpression(
        YamlNode outputNode,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? expr)
    {
        expr = null;
        if (outputNode is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            expr = scalar.Value;
            return true;
        }

        if (outputNode is YamlMappingNode mapping)
        {
            expr = mapping.GetScalar("expr");
            return !string.IsNullOrWhiteSpace(expr);
        }

        return false;
    }

    private static YamlMappingNode? BuildWorkflowOutputFromSkillSchema(YamlNode skillOutputSchema, string expr)
        => WorkflowPlanContractNormalizer.BuildWorkflowOutputFromSchema(WorkflowParser.YamlToJson(skillOutputSchema), expr);

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

    private static void ValidatePipelineMainLeafOutputContracts(
        WorkflowDocument doc,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        if (!doc.Workflows.TryGetValue("main", out var main))
            return;

        var leafNames = leaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal);
        var leafOutputTypes = leaves.ToDictionary(
            static leaf => leaf.Name,
            static leaf => BuildLeafOutputSchemaMap(leaf)
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => FlowTypeDescriptorConverter.FromJsonSchema(pair.Value),
                    StringComparer.Ordinal),
            StringComparer.Ordinal);
        var diagnostics = new JsonArray();

        foreach (var step in EnumerateSteps(main.Steps))
        {
            foreach (var expression in EnumerateStepExpressionTexts(step))
            {
                foreach (Match match in PipelineStepOutputReferenceRegex().Matches(expression.Text))
                {
                    var callStepId = match.Groups["step"].Value;
                    var outputName = match.Groups["output"].Value;
                    var remainingPath = SplitContractPath(match.Groups["path"].Value.TrimStart('.'));
                    if (remainingPath.Length == 0
                        || !TryGetMainLeafCall(main, leafNames, callStepId, out var leafName)
                        || !TryGetLeafOutputDescriptor(leafOutputTypes, leafName, outputName, out var descriptor))
                    {
                        continue;
                    }

                    if (PipelineOutputDescriptorHasRequiredPath(descriptor, string.Join('.', remainingPath)))
                        continue;

                    diagnostics.Add((JsonNode)BuildPipelineLeafContractDiagnostic(
                        descriptor.IsOpaque ? "OPAQUE_RESPONSE_DEEP_ACCESS" : "STEP_OUTPUT_PROPERTY_UNKNOWN",
                        step.Id,
                        expression.Field,
                        $"data.steps.{callStepId}.outputs.{outputName}.{string.Join('.', remainingPath)}",
                        FlowTypeDescriptorConverter.EnumerateAllowedPaths($"data.steps.{callStepId}.outputs.{outputName}", descriptor).Take(64).ToArray(),
                        descriptor.IsOpaque
                            ? "Leaf output is opaque, so the main workflow cannot validate deep output access."
                            : "Leaf output schema does not declare the deep path used by the main workflow."));
                }
            }
        }

        foreach (var path in EnumeratePipelineStepPaths(main.Steps, Array.Empty<StepDef>()))
        {
            var loopStep = path.Step;
            if (!IsPipelineLoopStep(loopStep)
                || loopStep.Steps == null
                || !TryGetLoopItemsExpression(loopStep, out var itemsExpression)
                || !TryParseStepOutputReference(itemsExpression, out var callStepId, out var outputName, out var outputPath)
                || outputPath.Count > 0
                || !TryGetMainLeafCall(main, leafNames, callStepId, out var leafName)
                || !TryGetLeafOutputDescriptor(leafOutputTypes, leafName, outputName, out var descriptor))
            {
                continue;
            }

            if (TryGetPipelineLoopItemsContractIssue(descriptor, out var issueCode, out var issueMessage))
            {
                var invalidPath = $"data.steps.{callStepId}.outputs.{outputName}";
                diagnostics.Add((JsonNode)BuildPipelineLeafContractDiagnostic(
                    issueCode,
                    loopStep.Id,
                    "input.items",
                    invalidPath,
                    FlowTypeDescriptorConverter.EnumerateAllowedPaths(invalidPath, descriptor).Take(64).ToArray(),
                    issueMessage,
                    expected: "array with concrete items",
                    requiredOutputPaths: new[] { "items" }));
            }

            var itemDescriptor = ExtractPipelineArrayItemType(descriptor);
            var itemVar = loopStep.ItemVar ?? "item";
            foreach (var access in EnumerateLoopItemAccesses(loopStep.Steps, itemVar))
            {
                var requiredOutputPath = "items." + string.Join('.', access.Path);
                if (PipelineOutputDescriptorHasRequiredPath(descriptor, requiredOutputPath))
                    continue;

                diagnostics.Add((JsonNode)BuildPipelineLeafContractDiagnostic(
                    itemDescriptor == null || itemDescriptor.IsOpaque ? "OPAQUE_DATA_VARIABLE_DEEP_ACCESS" : "DATA_VARIABLE_PROPERTY_UNKNOWN",
                    access.StepId,
                    access.Field,
                    access.InvalidPath,
                    itemDescriptor == null
                        ? Array.Empty<string>()
                        : FlowTypeDescriptorConverter.EnumerateAllowedPaths("data." + itemVar, itemDescriptor).Take(64).ToArray(),
                    itemDescriptor == null || itemDescriptor.IsOpaque
                        ? "Leaf array output item schema is opaque, so the main workflow cannot validate item field access."
                        : "Leaf array output item schema does not declare the field used by the main workflow."));
            }
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_leaf_contract_validation",
            ["summary"] = $"{diagnostics.Count} pipeline leaf contract diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Regenerate the producing leaf with a stronger public output schema, or change main to use only declared leaf output paths.")!)
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow requires stronger leaf output contract(s). | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static void ValidatePipelineMainDataflowQuality(
        WorkflowDocument doc,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        if (!doc.Workflows.TryGetValue("main", out var main))
            return;

        var diagnostics = new JsonArray();
        foreach (var assignment in EnumerateSuspiciousUrlToIdentifierAssignments(main))
        {
            diagnostics.Add((JsonNode)BuildPipelineMainDataflowDiagnostic(
                assignment.StepId,
                assignment.Field,
                assignment.TargetName,
                assignment.SourceInputName,
                assignment.Expression,
                leaves));
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_main_dataflow_validation",
            ["summary"] = $"{diagnostics.Count} pipeline main dataflow diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Reprompt only main assembly. Do not assign a raw URL/link input directly to narrower identifier fields such as owner, repo, id, number, name, or slug.")!,
                (JsonNode)JsonValue.Create("Use an existing typed leaf output for canonical parsed values, or add/route through a deterministic support step that truly parses the identifier.")!)
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow dataflow quality validation failed. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static IEnumerable<(string StepId, string Field, string TargetName, string SourceInputName, string Expression)>
        EnumerateSuspiciousUrlToIdentifierAssignments(WorkflowDef main)
    {
        foreach (var step in EnumerateSteps(main.Steps))
        {
            foreach (var expression in EnumerateJsonExpressionTexts(step.Input, "input"))
            {
                if (!TryParseExactDataInputExpression(expression.Text, out var sourceInputName))
                    continue;

                var targetName = GetAssignmentTargetName(expression.Field);
                if (IsSuspiciousUrlToIdentifierAssignment(sourceInputName, targetName))
                    yield return (step.Id, expression.Field, targetName, sourceInputName, expression.Text);
            }
        }

        if (main.Outputs == null)
            yield break;

        foreach (var (outputName, output) in main.Outputs)
        {
            if (!TryParseExactDataInputExpression(output.Expr, out var sourceInputName))
                continue;

            if (IsSuspiciousUrlToIdentifierAssignment(sourceInputName, outputName))
                yield return ("outputs", "outputs." + outputName, outputName, sourceInputName, output.Expr);
        }
    }

    private static JsonObject BuildPipelineMainDataflowDiagnostic(
        string? stepId,
        string field,
        string targetName,
        string sourceInputName,
        string expression,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var candidateSources = FindLeafOutputsNamed(leaves, targetName)
            .Select(outputName => "leaf output `" + outputName + "`")
            .ToArray();

        var diagnostic = new JsonObject
        {
            ["code"] = "PIPELINE_MAIN_SUSPICIOUS_NARROWING",
            ["phase"] = "pipeline_main_dataflow_validation",
            ["workflow"] = "main",
            ["step"] = stepId,
            ["field"] = field,
            ["invalid_assignment"] = expression,
            ["source_input"] = sourceInputName,
            ["target_name"] = targetName,
            ["message"] = $"Raw URL/link input '{sourceInputName}' is assigned directly to narrower identifier field '{targetName}'.",
            ["expected"] = "Pass a typed parsed identifier produced by a leaf/support parser, or keep the value in a URL/link-shaped target field.",
            ["hint"] = "A URL string is not a parsed owner/repo/id/name/slug contract."
        };

        if (candidateSources.Length > 0)
        {
            diagnostic["candidate_sources"] = new JsonArray(candidateSources
                .Select(static source => (JsonNode?)JsonValue.Create(source))
                .ToArray());
        }

        return diagnostic;
    }

    private static IEnumerable<string> FindLeafOutputsNamed(IReadOnlyList<GeneratedLeafWorkflow> leaves, string targetName)
    {
        foreach (var leaf in leaves)
        {
            var outputs = BuildLeafOutputSchemaMap(leaf);
            foreach (var outputName in outputs.Keys)
            {
                if (string.Equals(outputName, targetName, StringComparison.OrdinalIgnoreCase))
                    yield return $"{leaf.Name}.{outputName}";
            }
        }
    }

    private static bool IsPipelineDryRunValidation(JsonObject? validate)
        => validate?["dry_run"]?.GetValue<bool>() ?? false;

    private static void ValidatePipelineMainDryRunOutputProjection(WorkflowDocument doc)
    {
        if (!doc.Workflows.TryGetValue("main", out var main) || main.Outputs == null || main.Outputs.Count == 0)
            return;

        var stepsById = EnumerateSteps(main.Steps)
            .Where(static step => !string.IsNullOrWhiteSpace(step.Id))
            .ToDictionary(static step => step.Id, StringComparer.Ordinal);
        var diagnostics = new JsonArray();

        foreach (var (outputName, output) in main.Outputs)
        {
            if (!TryParseExactStepPathExpression(output.Expr, out var stepId, out var path)
                || path.Count != 1
                || !string.Equals(path[0], "results", StringComparison.Ordinal)
                || !stepsById.TryGetValue(stepId, out var step)
                || step.Type is not ("loop.sequential" or "loop.parallel"))
            {
                continue;
            }

            diagnostics.Add((JsonNode)BuildPipelineRawLoopResultsOutputDiagnostic(outputName, step, output.Expr));
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_main_output_projection_validation",
            ["summary"] = $"{diagnostics.Count} pipeline main output projection diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Reprompt only main assembly. Do not expose raw loop `results` as a public business output.")!,
                (JsonNode)JsonValue.Create("Add a post-loop projection step with a closed output_schema and set graph.outputs to that projected array.")!,
                (JsonNode)JsonValue.Create("If projection logic needs array mapping, put helper JavaScript in graph.functions; the renderer will copy it to the main workflow.")!)
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow exposes raw loop result snapshots as public output. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static JsonObject BuildPipelineRawLoopResultsOutputDiagnostic(
        string outputName,
        StepDef loopStep,
        string expression)
    {
        IEnumerable<StepDef> childSteps = loopStep.Steps ?? Enumerable.Empty<StepDef>();
        var childStepIds = childSteps
            .Select(static step => step.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();

        var diagnostic = new JsonObject
        {
            ["code"] = "PIPELINE_MAIN_RAW_LOOP_RESULTS_OUTPUT",
            ["phase"] = "pipeline_main_output_projection_validation",
            ["workflow"] = "main",
            ["step"] = loopStep.Id,
            ["field"] = "outputs." + outputName,
            ["invalid_assignment"] = expression,
            ["message"] = $"Public output '{outputName}' is assigned directly from loop step '{loopStep.Id}.results'.",
            ["expected"] = "Project loop snapshots into a clean typed business array before assigning the public output.",
            ["hint"] = "Loop `results[]` items are full per-iteration step snapshots, not the direct output of the last loop child step.",
            ["llm_guidance"] = "Create a post-loop `set` step with `output_schema` and assign graph.outputs to `${data.steps.<projection_step>.<field>}`."
        };

        if (childStepIds.Length > 0)
        {
            diagnostic["loop_child_steps"] = new JsonArray(childStepIds
                .Select(static id => (JsonNode?)JsonValue.Create(id))
                .ToArray());
            diagnostic["projection_source_examples"] = new JsonArray(childStepIds
                .Select(static id => (JsonNode?)JsonValue.Create($"iteration.{id}"))
                .ToArray());
        }

        return diagnostic;
    }

    private static bool TryParseExactDataInputExpression(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? inputName)
    {
        inputName = null;
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("${", StringComparison.Ordinal) || !trimmed.EndsWith('}'))
            return false;

        var inner = trimmed[2..^1].Trim();
        var match = ExactDataInputPathRegex().Match(inner);
        if (!match.Success)
            return false;

        inputName = match.Groups["name"].Value;
        return true;
    }

    private static string GetAssignmentTargetName(string field)
    {
        var trimmed = field.Trim();
        var bracketIndex = trimmed.LastIndexOf('[');
        if (bracketIndex >= 0)
            trimmed = trimmed[..bracketIndex];

        var dotIndex = trimmed.LastIndexOf('.');
        return dotIndex >= 0 ? trimmed[(dotIndex + 1)..] : trimmed;
    }

    private static bool IsSuspiciousUrlToIdentifierAssignment(string sourceInputName, string targetName)
    {
        if (!IsUrlLikeName(sourceInputName) || IsUrlLikeName(targetName))
            return false;

        return IsNarrowIdentifierName(targetName);
    }

    private static bool IsUrlLikeName(string name)
        => NameTokenRegex().Matches(name)
            .Select(static match => match.Value.ToLowerInvariant())
            .Any(static token => token is "url" or "uri" or "link" or "href");

    private static bool IsNarrowIdentifierName(string name)
    {
        var tokens = NameTokenRegex().Matches(name)
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0)
            return false;

        return tokens.Any(static token => token is
            "owner" or
            "org" or
            "organization" or
            "repo" or
            "id" or
            "identifier" or
            "number" or
            "name" or
            "slug" or
            "branch");
    }

    private static bool TryGetLeafOutputDescriptor(
        IReadOnlyDictionary<string, Dictionary<string, FlowTypeDescriptor>> leafOutputTypes,
        string leafName,
        string outputName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FlowTypeDescriptor? descriptor)
    {
        descriptor = null;
        if (!leafOutputTypes.TryGetValue(leafName, out var outputs)
            || !outputs.TryGetValue(outputName, out descriptor))
        {
            return false;
        }

        return true;
    }

    private static JsonObject BuildPipelineLeafContractDiagnostic(
        string code,
        string? stepId,
        string field,
        string invalidPath,
        IReadOnlyList<string> allowedPaths,
        string message,
        string? expected = null,
        IReadOnlyList<string>? requiredOutputPaths = null)
    {
        var diagnostic = new JsonObject
        {
            ["code"] = code,
            ["phase"] = "pipeline_leaf_contract_validation",
            ["workflow"] = "main",
            ["step"] = stepId,
            ["field"] = field,
            ["invalid_path"] = invalidPath,
            ["allowed_paths"] = new JsonArray(allowedPaths
                .Select(static path => (JsonNode?)JsonValue.Create(path))
                .ToArray()),
            ["message"] = message,
            ["hint"] = "Strengthen the producing leaf output contract or avoid undeclared deep access in main.",
            ["llm_guidance"] = "Leaf output contracts are authoritative for main orchestration deep access."
        };

        if (!string.IsNullOrWhiteSpace(expected))
            diagnostic["expected"] = expected;

        if (requiredOutputPaths is { Count: > 0 })
        {
            diagnostic["required_output_paths"] = new JsonArray(requiredOutputPaths
                .Select(static path => (JsonNode?)JsonValue.Create(path))
                .ToArray());
        }

        return diagnostic;
    }

    private static bool TryGetPipelineLoopItemsContractIssue(
        FlowTypeDescriptor descriptor,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? code,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? message)
    {
        descriptor = descriptor.RemoveNull();
        if (descriptor.IsOpaque)
        {
            code = "OPAQUE_ARRAY_LOOP_ITEMS";
            message = "Leaf output is opaque, so the main workflow cannot validate loop item values.";
            return true;
        }

        if (descriptor.Kind == FlowTypeKind.Union)
        {
            var variants = descriptor.Variants
                .Select(static variant => variant.RemoveNull())
                .Where(static variant => variant.Kind != FlowTypeKind.Null)
                .ToArray();
            if (variants.Length == 0)
            {
                code = "OPAQUE_ARRAY_LOOP_ITEMS";
                message = "Leaf output union does not expose a concrete array item contract for loop iteration.";
                return true;
            }

            var variantIssues = variants
                .Select(static variant =>
                {
                    var hasIssue = TryGetPipelineLoopItemsContractIssue(variant, out var variantCode, out var variantMessage);
                    return (hasIssue, variantCode, variantMessage);
                })
                .Where(static issue => issue.hasIssue)
                .ToArray();

            if (variantIssues.Length == 0)
            {
                code = null;
                message = null;
                return false;
            }

            code = variantIssues.Any(static issue => issue.variantCode == "OPAQUE_ARRAY_LOOP_ITEMS")
                ? "OPAQUE_ARRAY_LOOP_ITEMS"
                : "WEAK_ARRAY_LOOP_ITEMS";
            message = "Leaf output union must guarantee an array with concrete item schema before it can feed a main workflow loop.";
            return true;
        }

        if (descriptor.Kind != FlowTypeKind.Array)
        {
            code = "LEAF_OUTPUT_LOOP_ITEMS_NOT_ARRAY";
            message = $"Leaf output is typed as {descriptor.Kind.ToString().ToLowerInvariant()}, but main workflow loop items require an array.";
            return true;
        }

        if (!IsConcretePipelineLoopItemType(descriptor.Items))
        {
            code = descriptor.Items == null || descriptor.Items.IsOpaque
                ? "OPAQUE_ARRAY_LOOP_ITEMS"
                : "WEAK_ARRAY_LOOP_ITEMS";
            message = "Leaf array output must declare concrete item schema before it can feed a main workflow loop.";
            return true;
        }

        code = null;
        message = null;
        return false;
    }

    private static bool IsConcretePipelineLoopItemType(FlowTypeDescriptor? descriptor)
    {
        descriptor = descriptor?.RemoveNull();
        if (descriptor == null || descriptor.IsOpaque)
            return false;

        return descriptor.Kind switch
        {
            FlowTypeKind.Union => descriptor.Variants
                .Select(static variant => variant.RemoveNull())
                .Where(static variant => variant.Kind != FlowTypeKind.Null)
                .All(IsConcretePipelineLoopItemType),
            FlowTypeKind.Array => IsConcretePipelineLoopItemType(descriptor.Items),
            FlowTypeKind.Object => descriptor.Properties.Count > 0
                                   || descriptor.AdditionalProperties != null
                                   && IsConcretePipelineLoopItemType(descriptor.AdditionalProperties),
            FlowTypeKind.Dictionary => descriptor.AdditionalProperties != null
                                       && IsConcretePipelineLoopItemType(descriptor.AdditionalProperties),
            _ => true
        };
    }

    private static IEnumerable<(string Field, string Text)> EnumerateStepExpressionTexts(StepDef step)
    {
        if (!string.IsNullOrWhiteSpace(step.If))
            yield return ("if", step.If);
        if (!string.IsNullOrWhiteSpace(step.Expr))
            yield return ("expr", step.Expr);

        foreach (var item in EnumerateJsonExpressionTexts(step.Input, "input"))
            yield return item;

        if (step.OnError != null)
        {
            for (var i = 0; i < step.OnError.Cases.Count; i++)
            {
                var @case = step.OnError.Cases[i];
                if (!string.IsNullOrWhiteSpace(@case.If))
                    yield return ($"on_error.cases[{i}].if", @case.If);
                foreach (var item in EnumerateJsonExpressionTexts(@case.SetOutput, $"on_error.cases[{i}].set_output"))
                    yield return item;
            }
        }
    }

    private static IEnumerable<(string Field, string Text)> EnumerateJsonExpressionTexts(JsonNode? node, string field)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Contains("${", StringComparison.Ordinal):
                yield return (field, text);
                break;

            case JsonObject obj:
                foreach (var (name, child) in obj)
                foreach (var item in EnumerateJsonExpressionTexts(child, field + "." + name))
                    yield return item;
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                foreach (var item in EnumerateJsonExpressionTexts(array[i], $"{field}[{i}]"))
                    yield return item;
                break;
        }
    }

    private static IEnumerable<PipelineStepPath> EnumeratePipelineStepPaths(
        IReadOnlyList<StepDef> steps,
        IReadOnlyList<StepDef> ancestors)
    {
        foreach (var step in steps)
        {
            yield return new PipelineStepPath(step, ancestors.ToArray());

            var nestedAncestors = ancestors.Concat(new[] { step }).ToArray();
            if (step.Steps != null)
            {
                foreach (var child in EnumeratePipelineStepPaths(step.Steps, nestedAncestors))
                    yield return child;
            }

            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                foreach (var child in EnumeratePipelineStepPaths(branch.Steps, nestedAncestors))
                    yield return child;
            }

            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                foreach (var child in EnumeratePipelineStepPaths(@case.Steps, nestedAncestors))
                    yield return child;
            }

            if (step.Default != null)
            {
                foreach (var child in EnumeratePipelineStepPaths(step.Default, nestedAncestors))
                    yield return child;
            }
        }
    }

    private static IEnumerable<(string StepId, string Field, string InvalidPath, IReadOnlyList<string> Path)> EnumerateLoopItemAccesses(
        IReadOnlyList<StepDef> steps,
        string itemVar)
    {
        foreach (var step in EnumerateSteps(steps))
        {
            foreach (var expression in EnumerateStepExpressionTexts(step))
            {
                foreach (Match match in PipelineDataVariableReferenceRegex().Matches(expression.Text))
                {
                    var variableName = match.Groups["name"].Value;
                    var path = SplitContractPath(match.Groups["path"].Value.TrimStart('.'));
                    if (path.Length == 0)
                        continue;

                    if (string.Equals(variableName, itemVar, StringComparison.Ordinal))
                    {
                        yield return (step.Id, expression.Field, "data." + itemVar + "." + string.Join('.', path), path);
                        continue;
                    }

                    if ((string.Equals(variableName, "_loop", StringComparison.Ordinal) || string.Equals(variableName, "loop", StringComparison.Ordinal))
                        && path.Length > 1
                        && string.Equals(path[0], "item", StringComparison.Ordinal))
                    {
                        var itemPath = path.Skip(1).ToArray();
                        yield return (step.Id, expression.Field, "data." + variableName + "." + string.Join('.', path), itemPath);
                    }
                }
            }
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

                if (workflowName == "main" && !PipelineMainSupportStepTypes.Contains(step.Type, StringComparer.Ordinal))
                    throw new WorkflowRuntimeException(
                        ErrorCodes.TemplatePolicy,
                        $"Pipeline main workflow may only use leaf workflow.call plus support step types: {string.Join(", ", PipelineMainSupportStepTypes)}. Found '{step.Type}' in step '{step.Id}'.");

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
            ["work_kind"] = spec.WorkKind,
            ["inputs"] = BuildStringMapJson(spec.Inputs),
            ["outputs"] = BuildStringMapJson(spec.Outputs),
            ["input_schemas"] = BuildSchemaMapJson(spec.InputSchemas),
            ["output_schemas"] = BuildSchemaMapJson(spec.OutputSchemas),
            ["planned_tools"] = BuildPlannedToolsJson(spec.PlannedTools),
            ["extraction_score"] = spec.ExtractionScore == null ? null : BuildPipelineExtractionScoreJson(spec.ExtractionScore),
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

    [GeneratedRegex(@"\bdata\.steps\.(?<step>[A-Za-z_][A-Za-z0-9_-]*)\.outputs\.(?<output>[A-Za-z_][A-Za-z0-9_-]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex PipelineStepOutputReferenceRegex();

    [GeneratedRegex(@"\bdata\.(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex PipelineDataVariableReferenceRegex();

    private sealed record WorkflowPipelineSubworkflowSpec(
        string Name,
        string Goal,
        string? Description,
        string? WorkKind,
        IReadOnlyDictionary<string, string> Inputs,
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyDictionary<string, JsonNode?> InputSchemas,
        IReadOnlyDictionary<string, JsonNode?> OutputSchemas,
        IReadOnlyList<PipelinePlannedTool> PlannedTools,
        PipelineExtractionScore? ExtractionScore,
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
        string? WorkKind,
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

    [GeneratedRegex(@"\b(clone|cleanup|clean\s+up|delete|remove|write|save|create|update|post|publish|push|commit|send|download|upload|fetch|retrieve|read|list|call|report|file|external|mcp|llm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalWorkIntentRegex();

    [GeneratedRegex(@"\b(rename|constant|guard|map|mapping|field\s+mapping|aggregate|aggregation|route|routing|shape|shaping|filter|sort|select)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeterministicShapingIntentRegex();

    [GeneratedRegex(@"\b(parse|parsing|normalize|normalise|classify|classification|summari[sz]e|summary|synthesi[sz]e|analy[sz]e|analysis|validate|deduplicate|rank|score|calculate|compute|resolve|derive|merge|group|correlate|extract|transform|reconcile|compare|evaluate)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlgorithmicExtractionIntentRegex();

    [GeneratedRegex(@"\b(rename|copy|constant|guard|field\s+mapping|map\s+fields?|route|routing|aggregate|aggregation|filter|sort|select|loop\s+orchestration|fan-?out|fan-?in)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrivialExtractionIntentRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.CultureInvariant)]
    private static partial Regex IntentTokenRegex();

    [GeneratedRegex(@"\b(execute|run|invoke|call|clone|delete|remove|cleanup|clean\s+up|write|save|create|update|post|publish|push|commit|download|upload)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FakeActionTextRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(success|succeeded|completed|done|created|updated|deleted|removed|cleaned|cleanup|pushed|posted|cloned|written|saved|sent|published)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SideEffectSuccessOutputRegex();

    [GeneratedRegex(@"^data\.inputs\.(?<name>[A-Za-z_][A-Za-z0-9_-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactDataInputPathRegex();

    [GeneratedRegex(@"^data\.steps\.(?<step>[A-Za-z_][A-Za-z0-9_-]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactStepPathExpressionRegex();

    [GeneratedRegex(@"[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameTokenRegex();

    [GeneratedRegex(@"(?i)\bworkflow\.call\b|\bcall(?:s|ing)?\s+(?:a|an|the|another\s+)?(?:leaf\s+)?sub-?workflow\b|\bsub-?workflow\s+call\b")]
    private static partial Regex SubworkflowCallMentionRegex();

    [GeneratedRegex(@"(?im)^##\s+Main workflow orchestration\s*$")]
    private static partial Regex MainWorkflowOrchestrationRegex();
}
