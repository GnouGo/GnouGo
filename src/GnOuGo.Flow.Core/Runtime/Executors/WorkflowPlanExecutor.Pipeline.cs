using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private static readonly string[] PipelineMainSupportStepTypes =
    [
        "workflow.call",
        "set",
        "assert.non_null",
        "sequence",
        "switch",
        "parallel",
        "loop.sequential",
        "loop.parallel",
        "human.input",
        "emit"
    ];

    private const string PipelineWorkKindOrchestration = "orchestration";
    private const string PipelineWorkKindDeterministicShaping = "deterministic_shaping";
    private const string PipelineWorkKindExternalWork = "external_work";
    private const string PipelineContractRoleExternalAction = "external_action";
    private const string PipelineContractRoleTypedDataProducer = "typed_data_producer";
    private const string PipelineContractRoleAlgorithmicTransform = "algorithmic_transform";
    private const string PipelineContractRoleDeterministicGlue = "deterministic_glue";
    private const string PipelineContractRoleOrchestration = "orchestration";
    private const string PipelineContractRoleAbstractPolicy = "abstract_policy";
    private const int PipelineExtractionScoreThreshold = 45;
    private const int PipelineExtractionQualityReviewThreshold = 75;

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

    private sealed record PipelineLeafInputContractDemand(
        string LeafName,
        string ConsumerStepId,
        string Reason,
        IReadOnlyDictionary<string, JsonNode?> RequiredInputSchemas,
        IReadOnlyDictionary<string, string> SourceExpressions);

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

    private sealed record PipelineRootCause(
        string Category,
        string Phase,
        string? LeafName,
        string? OutputName,
        string? InvalidPath,
        string? Code,
        string Message,
        bool Primary);

    private sealed record PipelineExtractionQualityReview(
        int Score,
        string Verdict,
        IReadOnlyList<PipelineExtractionQualityDiagnostic> Diagnostics,
        string? RetryGuidance);

    private sealed record PipelineExtractionQualityDiagnostic(
        string Code,
        string Kind,
        string Severity,
        string? LeafName,
        string Message,
        string? Recommendation,
        IReadOnlyList<PipelineExtractionQualityEvidence>? Evidence = null,
        bool EvidenceQualified = false,
        string RemediationSurface = "extraction_contract");

    private sealed record PipelineExtractionQualityEvidence(
        string Source,
        string Reference,
        string? Excerpt = null);

    private sealed record PipelineLeafBlueprint(
        string LeafName,
        string WorkflowName,
        string Summary,
        IReadOnlyList<PipelineLeafBlueprintStep> Steps,
        IReadOnlyList<PipelineLeafBlueprintOutput> Outputs);

    private sealed record PipelineLeafBlueprintStep(
        string Id,
        string Type,
        string Purpose,
        PipelinePlannedTool? PlannedTool,
        PipelinePlannedNativeStep? PlannedNativeStep,
        JsonNode? OutputSchema);

    private sealed record PipelineLeafBlueprintOutput(
        string Name,
        string Expr,
        string SourceStepId,
        JsonNode? Schema);

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

    private async Task<JsonNode?> ExecutePipelineAsync(
        StepExecutionContext ctx,
        JsonObject input,
        CapabilityPreflightResult capabilityPreflight,
        CancellationToken ct)
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
        var capabilityPreflightProvesStructuredExtraction =
            CapabilityPreflightProvesStructuredPipelineExtraction(capabilityPreflight.Mode);
        var useStructuredExtraction = capabilityPreflightProvesStructuredExtraction
            || await ShouldUseStructuredPipelineExtractionAsync(ctx, provider, model, ct);
        var structuredExtractionSource = capabilityPreflightProvesStructuredExtraction
            ? "capability_preflight_proven"
            : useStructuredExtraction
                ? "provider_capability"
                : "annotated_markdown_fallback";

        ctx.SetTelemetryAttribute("gnougo-flow.plan.mode", "pipeline");
        ctx.SetTelemetryAttribute("gen_ai.operation.name", "chat");
        ctx.SetTelemetryAttribute("gen_ai.system", provider ?? "unspecified");
        ctx.SetTelemetryAttribute("gen_ai.request.model", model);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.structured_extraction", useStructuredExtraction);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.structured_extraction_source", structuredExtractionSource);

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Preparing workflow generation prompt through pipeline mode."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var normalizedMarkdown = await NormalizeUserPromptAsync(
            llmClient, rawPrompt, provider, model, reasoning, ctx, ct);

        var globalMcpContext = await BuildPipelineGlobalMcpContextAsync(
            llmClient, generator, normalizedMarkdown, rawPrompt, model, provider, reasoning, capabilityPreflight, ctx, ct);

        var (annotatedMarkdown, extraction) = await MarkAndExtractSubworkflowSpecsAsync(
            llmClient, normalizedMarkdown, globalMcpContext, input, provider, model, reasoning, useStructuredExtraction, capabilityPreflight, ctx, ct);

        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count);

        GeneratedLeafWorkflow[] generatedLeaves;
        var leafContractRepairAttempt = 0;
        while (true)
        {
            using var generationSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.generate_subworkflows", "generate_subworkflows", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.contract_repair_attempt", leafContractRepairAttempt)
            });
            try
            {
                var tasks = extraction.Subworkflows
                    .Select(spec => GenerateLeafWorkflowAsync(
                        ctx,
                        input,
                        generator,
                        spec,
                        globalMcpContext,
                        capabilityPreflight,
                        ct))
                    .ToArray();
                generatedLeaves = await Task.WhenAll(tasks);
                break;
            }
            catch (Exception ex) when (useStructuredExtraction
                                       && leafContractRepairAttempt == 0
                                       && TryGetLeafRepairCycle(ex, out var failedLeaf))
            {
                generationSpan.SetAttribute("gnougo-flow.plan.pipeline.contract_repair_status", "returning_to_extraction");
                generationSpan.AddEvent(
                    "gnougo-flow.plan.pipeline.leaf_contract_repair_requested",
                    BuildPlanErrorTelemetryAttributes(ex, leafContractRepairAttempt + 1, "repair_extraction_after_leaf_cycle", failedLeaf));
                var baseline = AddLeafRepairCycleDiagnostic(extraction, failedLeaf, ex);
                var repair = await RequestPipelineExtractionPatchAsync(
                    llmClient,
                    normalizedMarkdown,
                    globalMcpContext,
                    baseline,
                    BuildExtractionQualityReviewRetryFeedback(baseline.QualityReview),
                    provider,
                    model,
                    reasoning,
                    ctx,
                    ct,
                    attempt: 1,
                    maxAttempts: 1);
                var repairedExtraction = ComposeLockedCapabilitiesIntoPipelineExtraction(
                    repair.Extraction,
                    capabilityPreflight,
                    globalMcpContext);
                AddConditionalCapabilityOwnershipTelemetry(ctx, repairedExtraction);
                repairedExtraction = ReconcileRequiredLeafToolValidationAfterCapabilityComposition(repairedExtraction);
                repairedExtraction = RevalidatePatchedPipelineExtraction(repairedExtraction, globalMcpContext);
                repairedExtraction = PreserveSharedPipelineBoundaryContracts(repairedExtraction);
                repairedExtraction = ValidateLockedCapabilitiesInExtraction(repairedExtraction, capabilityPreflight);
                repairedExtraction = ValidatePlannedToolArtifactPrerequisites(
                    repairedExtraction,
                    globalMcpContext,
                    normalizedMarkdown);
                if (repairedExtraction.ValidationErrors.Count > 0)
                {
                    var validationException = BuildPipelineExtractionException(
                        repairedExtraction,
                        repair.AnnotatedMarkdown,
                        globalMcpContext);
                    generationSpan.Fail(validationException);
                    throw validationException;
                }

                repairedExtraction = await ReviewPipelineExtractionQualityAsync(
                    llmClient,
                    normalizedMarkdown,
                    globalMcpContext,
                    repair.AnnotatedMarkdown,
                    repairedExtraction,
                    baseline,
                    repair.AddressedDiagnosticCodes,
                    provider,
                    model,
                    reasoning,
                    useStructuredOutput: true,
                    ctx,
                    ct,
                    attempt: 1,
                    maxAttempts: 1);
                if (ShouldRetryPipelineExtractionReview(repairedExtraction.QualityReview))
                {
                    var reviewException = BuildPipelineExtractionQualityReviewException(
                        repairedExtraction,
                        repair.AnnotatedMarkdown,
                        globalMcpContext);
                    generationSpan.Fail(reviewException);
                    throw reviewException;
                }

                annotatedMarkdown = repair.AnnotatedMarkdown;
                extraction = repairedExtraction;
                leafContractRepairAttempt++;
                ctx.SetTelemetryAttribute(
                    "gnougo-flow.plan.pipeline.leaf_contract_repair_count",
                    leafContractRepairAttempt);
            }
            catch (Exception ex)
            {
                generationSpan.Fail(ex);
                throw;
            }
        }

        string finalYaml;
        WorkflowDocument finalDoc;
        var qualityEvents = generatedLeaves
            .SelectMany(static leaf => leaf.QualityEvents)
            .ToList();
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
                string? previousAssemblyDiagnosticFingerprint = null;
                var unchangedAssemblyRepairAttempts = 0;
                string? bestAssemblyResponse = null;
                string? bestAssemblyError = null;
                string? bestAssemblyCandidateFingerprint = null;
                string? bestAssemblyCandidateDiagnosticFingerprint = null;
                var bestAssemblyValidationProgress = -1;
                var regressedAssemblyRepairAttempts = 0;
                Exception? lastAssemblyException = null;
                string? assembledYaml = null;
                WorkflowDocument? assembledDocument = null;
                var assemblySucceeded = false;

                for (var attempt = 1; attempt <= maxAssemblyAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidateAssemblyValidationProgress = 0;
                    string? candidateAssemblyFingerprint = null;
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
                        var mainResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
                        {
                            Provider = provider,
                            Model = model,
                            Prompt = mainAssemblyPrompt,
                            Reasoning = reasoning,
                            UseBackgroundMode = true
                        }, "workflow.plan.pipeline.main_assembly", ct);
                        previousAssemblyResponse = mainResponse.Text;
                        candidateAssemblyFingerprint = BuildPipelineMainAssemblyFingerprint(mainResponse.Text);
                        attemptSpan.SetAttribute(
                            "gnougo-flow.plan.pipeline.candidate.fingerprint",
                            candidateAssemblyFingerprint);
                        attemptSpan.SetAttribute("gen_ai.operation.name", "chat");
                        attemptSpan.SetAttribute("gen_ai.system", provider ?? "unspecified");
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
                        candidateAssemblyValidationProgress = 10;
                        var mainInputs = ResolveMainInputContract(configuredMainInputs, assembly, generatedLeafInputs);
                        ValidateInferredMainArtifactInputs(mainInputs, configuredMainInputs, rawPrompt);
                        candidateAssemblyValidationProgress = 20;
                        ForceMainWorkflowInputs(assembly.MainWorkflowNode, mainInputs);
                        EnsureMainWorkflowOutputs(assembly.MainWorkflowNode, extraction.Subworkflows);
                        ValidateDeclaredMainInputReferences(assembly.MainWorkflowNode, mainInputs);
                        candidateAssemblyValidationProgress = 30;

                        assembledYaml = ComposePipelineWorkflowYaml(input, generator, extraction, currentLeaves, assembly, mainInputs);
                        candidateAssemblyValidationProgress = 40;
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
                                candidateAssemblyValidationProgress = 50;
                                (assembledDocument, assembledYaml) = PromoteGeneratedDirectMcpScalarInputSchemas(
                                    assembledDocument,
                                    assembledYaml,
                                    validationDiscovered);
                                (assembledDocument, assembledYaml) = PromoteGeneratedDirectWorkflowCallObjectInputSchemas(
                                    assembledDocument,
                                    assembledYaml,
                                    validationDiscovered,
                                    ctx.Engine.Registry);
                                (assembledDocument, assembledYaml) = PromoteGeneratedDirectSetOutputSchemas(
                                    assembledDocument,
                                    assembledYaml,
                                    validationDiscovered,
                                    ctx.Engine.Registry);
                                (assembledDocument, assembledYaml) = PromoteGeneratedDirectOutputSchemas(
                                    assembledDocument,
                                    assembledYaml,
                                    validationDiscovered,
                                    ctx.Engine.Registry);
                                var artifactRoutingNormalization = NormalizeGeneratedMcpArtifactCallerBindings(
                                    assembledDocument,
                                    assembledYaml,
                                    validationDiscovered);
                                assembledDocument = artifactRoutingNormalization.Document;
                                assembledYaml = artifactRoutingNormalization.Yaml;
                                if (artifactRoutingNormalization.ReplacementCount > 0)
                                {
                                    validationSpan.AddEvent(
                                        "gnougo-flow.plan.pipeline.artifact_routing.normalized",
                                        [new KeyValuePair<string, object?>(
                                            "gnougo-flow.plan.pipeline.artifact_routing.replacement_count",
                                            artifactRoutingNormalization.ReplacementCount)]);
                                }
                                candidateAssemblyValidationProgress = 60;
                                validationSpan.SetAttribute("gnougo-flow.plan.workflow_count", assembledDocument.Workflows.Count);
                                EnforcePipelineWorkflowHierarchy(
                                    assembledDocument,
                                    currentLeaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal));
                                candidateAssemblyValidationProgress = 70;
                                ValidatePipelineLeafCallArguments(assembledDocument, currentLeaves);
                                candidateAssemblyValidationProgress = 80;
                                ValidatePipelineMainLeafOutputContracts(assembledDocument, currentLeaves);
                                candidateAssemblyValidationProgress = 90;
                                ValidatePipelineMainDataflowQuality(assembledDocument, currentLeaves);
                                candidateAssemblyValidationProgress = 100;
                                if (IsPipelineDryRunValidation(validate))
                                    ValidatePipelineMainDryRunOutputProjection(assembledDocument);
                                candidateAssemblyValidationProgress = 110;
                                await RunStandardPlanValidationSequenceAsync(
                                    assembledDocument,
                                    input["policy"] as JsonObject,
                                    input["limits"] as JsonObject,
                                    validate,
                                    validationDiscovered,
                                    ctx,
                                    validationSpan.Span,
                                    ct);
                                candidateAssemblyValidationProgress = 120;
                                ValidateLockedCapabilitiesInDocument(assembledDocument, capabilityPreflight);
                                candidateAssemblyValidationProgress = 130;
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
                    catch (Exception ex)
                    {
                        if (WorkflowPlanDiagnostics.IsNonRepairableLlmFailure(ex))
                        {
                            attemptSpan.Fail(ex);
                            throw;
                        }

                        var inputContractDemand = TryAnalyzePipelineLeafInputContractDemand(ex, assembledDocument, currentLeaves);
                        var contractDemand = inputContractDemand == null
                            ? TryAnalyzePipelineLeafContractDemand(ex, assembledDocument, currentLeaves)
                            : null;
                        var runtimeValidationLeaf = inputContractDemand == null && contractDemand == null
                            ? TryGetPipelineLeafRuntimeValidationFailure(
                                ex,
                                currentLeaves.Select(static leaf => leaf.Name).ToArray())
                              ?? TryGetConditionalActivationLeafTopologyFailure(ex, currentLeaves)
                            : null;
                        if (inputContractDemand == null && contractDemand == null && runtimeValidationLeaf == null)
                        {
                            var stalled = DetectRepairStall(
                                ex,
                                attempt,
                                isRepairAttempt: attempt > 1,
                                ref previousAssemblyDiagnosticFingerprint,
                                ref unchangedAssemblyRepairAttempts);
                            if (stalled != null)
                            {
                                attemptSpan.Fail(stalled);
                                throw stalled;
                            }

                            var diagnosticFingerprint = WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(ex);
                            var candidateImproved = !string.IsNullOrWhiteSpace(previousAssemblyResponse)
                                                    && (bestAssemblyResponse == null
                                                        || candidateAssemblyValidationProgress > bestAssemblyValidationProgress
                                                        || candidateAssemblyValidationProgress == bestAssemblyValidationProgress
                                                        && !string.Equals(
                                                            diagnosticFingerprint,
                                                            bestAssemblyCandidateDiagnosticFingerprint,
                                                            StringComparison.Ordinal));
                            if (candidateImproved)
                            {
                                var priorProgress = bestAssemblyValidationProgress;
                                bestAssemblyResponse = previousAssemblyResponse;
                                bestAssemblyError = BuildStructuredPlanError(ex, attempt);
                                bestAssemblyCandidateFingerprint = candidateAssemblyFingerprint;
                                bestAssemblyCandidateDiagnosticFingerprint = diagnosticFingerprint;
                                bestAssemblyValidationProgress = candidateAssemblyValidationProgress;
                                regressedAssemblyRepairAttempts = 0;
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.accepted", true);
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.validation_progress", candidateAssemblyValidationProgress);
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.previous_validation_progress", priorProgress);
                            }
                            else if (bestAssemblyResponse != null)
                            {
                                regressedAssemblyRepairAttempts++;
                                previousAssemblyResponse = bestAssemblyResponse;
                                previousAssemblyError = bestAssemblyError;
                                assembledYaml = null;
                                assembledDocument = null;
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.accepted", false);
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.validation_progress", candidateAssemblyValidationProgress);
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.best_validation_progress", bestAssemblyValidationProgress);
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.stall_reason", "validation_progress_regression");
                                ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.main_assembly_candidate_rejected", new[]
                                {
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.candidate.fingerprint", candidateAssemblyFingerprint),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.candidate.best_fingerprint", bestAssemblyCandidateFingerprint),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.candidate.validation_progress", candidateAssemblyValidationProgress),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.candidate.best_validation_progress", bestAssemblyValidationProgress)
                                });
                                if (regressedAssemblyRepairAttempts >= 2)
                                {
                                    var repairStalled = BuildPipelineMainAssemblyRepairStalledException(
                                        attempt,
                                        bestAssemblyCandidateFingerprint,
                                        diagnosticFingerprint,
                                        "Two repair responses failed to improve the best validated main assembly candidate.",
                                        ex);
                                    attemptSpan.Fail(repairStalled);
                                    throw repairStalled;
                                }
                            }

                            previousAssemblyResponse = bestAssemblyResponse ?? previousAssemblyResponse;
                            previousAssemblyError = bestAssemblyError ?? BuildStructuredPlanError(ex, attempt);
                        }
                        if (attempt >= maxAssemblyAttempts)
                        {
                            lastAssemblyException = ex;
                            attemptSpan.AddEvent(
                                "gnougo-flow.plan.pipeline.main_assembly.error",
                                BuildPlanErrorTelemetryAttributes(ex, attempt, "assemble_main_workflow"));
                            attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "failed");
                            attemptSpan.Fail(ex);
                            break;
                        }

                        lastAssemblyException = ex;
                        previousAssemblyError ??= BuildStructuredPlanError(ex, attempt);
                        attemptSpan.AddEvent(
                            "gnougo-flow.plan.pipeline.main_assembly.error",
                            BuildPlanErrorTelemetryAttributes(ex, attempt, "assemble_main_workflow"));
                        if (inputContractDemand != null)
                        {
                            try
                            {
                                currentLeaves = await RegenerateLeafForInputContractDemandAsync(
                                    ctx,
                                    input,
                                    generator,
                                    extraction,
                                    currentLeaves,
                                    globalMcpContext,
                                    inputContractDemand,
                                    ex,
                                    attempt,
                                    attemptSpan.Span,
                                    ct);
                                qualityEvents.Add(new PipelineQualityEvent(
                                    "leaf_input_contract_repair",
                                    attempt,
                                    "assemble_main_workflow",
                                    inputContractDemand.LeafName,
                                    null,
                                    inputContractDemand.ConsumerStepId,
                                    string.Join(",", inputContractDemand.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal)),
                                    null,
                                    inputContractDemand.Reason,
                                    inputContractDemand.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal).ToArray(),
                                    "compatible input contract",
                                    null,
                                    "Regenerated consuming leaf against authoritative parent source contracts."));
                                // The previous parent assembly was produced against the old leaf input
                                // contract. Reusing it as repair context after the contract changes can
                                // preserve an obsolete adapter and make parent/leaf types oscillate.
                                // Reassemble from the locked blueprint and the refreshed leaf contracts.
                                previousAssemblyResponse = null;
                                previousAssemblyError = null;
                                assembledYaml = null;
                                assembledDocument = null;
                                previousAssemblyDiagnosticFingerprint = null;
                                unchangedAssemblyRepairAttempts = 0;
                                bestAssemblyResponse = null;
                                bestAssemblyError = null;
                                bestAssemblyCandidateFingerprint = null;
                                bestAssemblyCandidateDiagnosticFingerprint = null;
                                bestAssemblyValidationProgress = -1;
                                regressedAssemblyRepairAttempts = 0;
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "leaf_input_contract_repaired");
                                ctx.Engine.Logger.LogWarning(
                                    ex,
                                    "workflow.plan pipeline main assembly attempt {Attempt}/{MaxAttempts} found incompatible input contracts for leaf {Leaf}, regenerated impacted leaf",
                                    attempt,
                                    maxAssemblyAttempts,
                                    inputContractDemand.LeafName);
                                ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.leaf_input_contract_repair", new[]
                                {
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAssemblyAttempts),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", inputContractDemand.LeafName),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.consumer_step", inputContractDemand.ConsumerStepId),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.input_names", string.Join(",", inputContractDemand.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal)))
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
                                    "leaf_input_contract_repair_failed",
                                    attempt,
                                    "repair_leaf_input_contract",
                                    inputContractDemand.LeafName,
                                    null,
                                    inputContractDemand.ConsumerStepId,
                                    string.Join(",", inputContractDemand.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal)),
                                    null,
                                    inputContractDemand.Reason,
                                    inputContractDemand.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal).ToArray(),
                                    "compatible input contract",
                                    repairEx.GetType().Name,
                                    TruncatePipelineQualityMessage(repairEx.Message)));
                                attemptSpan.AddEvent(
                                    "gnougo-flow.plan.pipeline.leaf_input_contract_repair.error",
                                    BuildPlanErrorTelemetryAttributes(repairEx, attempt, "repair_leaf_input_contract", inputContractDemand.LeafName));
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "retrying");
                                attemptSpan.Fail(repairEx);
                                ctx.Engine.Logger.LogWarning(
                                    repairEx,
                                    "workflow.plan pipeline input contract repair for {Leaf} failed during main assembly attempt {Attempt}/{MaxAttempts}, reprompting main",
                                    inputContractDemand.LeafName,
                                    attempt,
                                    maxAssemblyAttempts);
                            }
                        }
                        else if (contractDemand != null)
                        {
                            try
                            {
                                currentLeaves = await RegenerateLeafForContractDemandAsync(
                                    ctx,
                                    input,
                                    generator,
                                    extraction,
                                    currentLeaves,
                                    globalMcpContext,
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
                                // The old parent assembly was based on the weaker producer contract.
                                // It is no longer a sound repair baseline once the public leaf contract
                                // has changed, so compose a fresh parent from the authoritative inputs.
                                previousAssemblyResponse = null;
                                previousAssemblyError = null;
                                assembledYaml = null;
                                assembledDocument = null;
                                previousAssemblyDiagnosticFingerprint = null;
                                unchangedAssemblyRepairAttempts = 0;
                                bestAssemblyResponse = null;
                                bestAssemblyError = null;
                                bestAssemblyCandidateFingerprint = null;
                                bestAssemblyCandidateDiagnosticFingerprint = null;
                                bestAssemblyValidationProgress = -1;
                                regressedAssemblyRepairAttempts = 0;
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
                        else if (runtimeValidationLeaf != null)
                        {
                            try
                            {
                                currentLeaves = await RegenerateLeafForRuntimeValidationFailureAsync(
                                    ctx,
                                    input,
                                    generator,
                                    extraction,
                                    currentLeaves,
                                    globalMcpContext,
                                    runtimeValidationLeaf,
                                    ex,
                                    attempt,
                                    attemptSpan.Span,
                                    ct);
                                qualityEvents.Add(new PipelineQualityEvent(
                                    "leaf_runtime_validation_repair",
                                    attempt,
                                    "assemble_main_workflow",
                                    runtimeValidationLeaf,
                                    null,
                                    null,
                                    null,
                                    null,
                                    "Final composed dry-run failed inside a generated leaf.",
                                    Array.Empty<string>(),
                                    "runtime-valid leaf implementation",
                                    null,
                                    "Regenerated only the leaf identified by structured dry-run runtime details."));
                                previousAssemblyResponse = null;
                                previousAssemblyError = null;
                                assembledYaml = null;
                                assembledDocument = null;
                                previousAssemblyDiagnosticFingerprint = null;
                                unchangedAssemblyRepairAttempts = 0;
                                bestAssemblyResponse = null;
                                bestAssemblyError = null;
                                bestAssemblyCandidateFingerprint = null;
                                bestAssemblyCandidateDiagnosticFingerprint = null;
                                bestAssemblyValidationProgress = -1;
                                regressedAssemblyRepairAttempts = 0;
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "leaf_runtime_validation_repaired");
                                ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.leaf_runtime_validation_repair", new[]
                                {
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAssemblyAttempts),
                                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", runtimeValidationLeaf)
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
                                    "leaf_runtime_validation_repair_failed",
                                    attempt,
                                    "repair_leaf_runtime_validation",
                                    runtimeValidationLeaf,
                                    null,
                                    null,
                                    null,
                                    null,
                                    "Final composed dry-run failed inside a generated leaf.",
                                    Array.Empty<string>(),
                                    "runtime-valid leaf implementation",
                                    repairEx.GetType().Name,
                                    TruncatePipelineQualityMessage(repairEx.Message)));
                                attemptSpan.AddEvent(
                                    "gnougo-flow.plan.pipeline.leaf_runtime_validation_repair.error",
                                    BuildPlanErrorTelemetryAttributes(repairEx, attempt, "repair_leaf_runtime_validation", runtimeValidationLeaf));
                                attemptSpan.SetAttribute("gnougo-flow.plan.pipeline.assembly_status", "retrying");
                                attemptSpan.Fail(repairEx);
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
                            inputContractDemand?.Reason ?? contractDemand?.Reason,
                            inputContractDemand?.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal).ToArray() ?? contractDemand?.RequiredOutputPaths,
                            inputContractDemand == null ? contractDemand?.ExpectedType : "compatible input contract",
                            ex.GetType().Name,
                            TruncatePipelineQualityMessage(ex.Message)));
                    }
                }

                if (!assemblySucceeded || assembledYaml == null || assembledDocument == null)
                {
                    var failureRootCauses = BuildPipelineRootCauses(extraction, qualityEvents, lastAssemblyException);
                    throw new WorkflowRuntimeException(
                        ErrorCodes.TemplatePlan,
                        $"Pipeline main workflow assembly failed after {maxAssemblyAttempts} attempt(s): {lastAssemblyException?.Message ?? "unknown error"}",
                        inner: lastAssemblyException,
                        details: BuildPipelineFailureDetails(
                            normalizedMarkdown,
                            annotatedMarkdown,
                            extraction,
                            currentLeaves,
                            globalMcpContext,
                            qualityEvents,
                            failureRootCauses,
                            previousAssemblyResponse,
                            assembledYaml,
                            lastAssemblyException));
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

        var qualityReport = BuildPipelineQualityReportJson(extraction, generatedLeaves, finalDoc, globalMcpContext, qualityEvents);
        var inspection = BuildPipelineInspectionJson(
            normalizedMarkdown,
            annotatedMarkdown,
            extraction,
            generatedLeaves,
            finalDoc,
            globalMcpContext,
            qualityEvents);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.status", qualityReport["status"]?.GetValue<string>() ?? "unknown");
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.repair_count", qualityEvents.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)));
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.retry_count", qualityEvents.Count(static item => item.Kind.EndsWith("_retry", StringComparison.Ordinal)));
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.warning_count", qualityReport["warnings"] is JsonArray warnings ? warnings.Count : 0);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.quality.root_cause_count", qualityReport["root_causes"] is JsonArray rootCauses ? rootCauses.Count : 0);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.inspection.leaf_count", generatedLeaves.Length);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.inspection.leaf_blueprint_count", generatedLeaves.Count(static leaf => leaf.Blueprint != null));
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
                ["capability_preflight"] = BuildCapabilityPreflightJson(capabilityPreflight),
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

    private static bool CapabilityPreflightProvesStructuredPipelineExtraction(string mode)
        => string.Equals(mode, "infer", StringComparison.Ordinal);

    private static async Task<PipelineMcpContext> BuildPipelineGlobalMcpContextAsync(
        ILLMClient llmClient,
        JsonObject generator,
        string normalizedMarkdown,
        string rawPrompt,
        string model,
        string? provider,
        string? reasoning,
        CapabilityPreflightResult capabilityPreflight,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var mcpFactory = ctx.Engine.McpClientFactory;
        var prefilterNode = generator["prefilter"];
        var shouldPrefilter = prefilterNode == null
            || prefilterNode is JsonObject
            || (prefilterNode is JsonValue jv && (!jv.TryGetValue<bool>(out var bv) || bv));
        if (!shouldPrefilter && !capabilityPreflight.Enabled)
            return PipelineMcpContext.Empty;

        if (!capabilityPreflight.Enabled
            && (mcpFactory?.ServerMetadata == null || mcpFactory.ServerMetadata.Count == 0))
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
            if (capabilityPreflight.Enabled)
            {
                var discoveredFromPreflight = capabilityPreflight.DiscoveredServers.Select(CloneDiscovery).ToList();
                if (shouldPrefilter && discoveredFromPreflight.Count > 0)
                {
                    var complete = discoveredFromPreflight.Select(CloneDiscovery).ToList();
                    discoveredFromPreflight = await PrefilterMcpServersAsync(
                        llmClient,
                        discoveredFromPreflight,
                        normalizedMarkdown,
                        rawPrompt,
                        prefilterModel,
                        prefilterProvider,
                        prefilterTemperature,
                        reasoning,
                        ctx,
                        mcpContextSpan.Span,
                        ct);
                    discoveredFromPreflight = MergeLockedCapabilitiesIntoDiscovery(
                        discoveredFromPreflight,
                        complete,
                        capabilityPreflight);
                    discoveredFromPreflight = ExpandSelectedOperationalArtifactPrerequisites(
                        discoveredFromPreflight,
                        complete,
                        rawPrompt);
                }

                var preflightDoc = discoveredFromPreflight.Count == 0
                    ? FormatLockedCapabilities(capabilityPreflight)
                    : FormatMcpServersDoc(discoveredFromPreflight) + "\n" + FormatLockedCapabilities(capabilityPreflight);
                mcpContextSpan.SetAttribute("mcp.servers_selected", discoveredFromPreflight.Count);
                mcpContextSpan.SetAttribute("mcp.tools_selected", discoveredFromPreflight.Sum(static server => server.Tools.Count));
                return new PipelineMcpContext(discoveredFromPreflight, preflightDoc);
            }

            var requiredMcpServerNames = ExtractRequiredMcpServerNames(
                normalizedMarkdown,
                rawPrompt,
                mcpFactory!.ServerMetadata);

            var candidateMcpServers = await PrefilterMcpServerMetadataAsync(
                llmClient,
                mcpFactory,
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
                mcpFactory.ServerMetadata,
                requiredMcpServerNames,
                ctx);

            var discovered = await DiscoverMcpServersAsync(
                mcpFactory,
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
                discovered = ExpandSelectedOperationalArtifactPrerequisites(
                    discovered,
                    prefilterSource,
                    rawPrompt);
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
        CapabilityPreflightResult capabilityPreflight,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var maxAttempts = GetPipelineGenerationMaxAttempts(pipelineInput);
        string? previousAnnotatedMarkdown = null;
        string? previousExtractionJson = null;
        IReadOnlyList<string>? previousValidationErrors = null;
        StructuredPipelineExtractionMetadata? previousValidatedStructuredMetadata = null;
        WorkflowPipelineExtraction? bestValidatedCandidate = null;
        string? bestAnnotatedMarkdown = null;
        int bestPatchOperationCount = 0;
        WorkflowPipelineExtraction? bestRepairableCandidate = null;
        string? bestRepairableAnnotatedMarkdown = null;
        int bestRepairablePatchOperationCount = 0;
        string? previousQualityDiagnosticFingerprint = null;
        var unchangedQualityRepairAttempts = 0;
        var rejectedPatchAttempts = 0;
        var qualityNonImprovingPatchAttempts = 0;
        var deterministicRegressionPatchAttempts = 0;
        var validationNonImprovingPatchAttempts = 0;
        string? previousPatchFailureFingerprint = null;
        var repeatedPatchFailureAttempts = 0;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var targetedPatchBase = bestValidatedCandidate ?? bestRepairableCandidate;
            var targetedPatchBaseMarkdown = bestValidatedCandidate != null
                ? bestAnnotatedMarkdown
                : bestRepairableAnnotatedMarkdown;
            var useTargetedPatch = useStructuredExtraction
                                   && targetedPatchBase != null
                                   && previousValidationErrors != null;
            var prompt = useTargetedPatch
                ? null
                : previousValidationErrors == null
                    ? BuildMarkExtractableBlocksPrompt(
                        normalizedMarkdown,
                        pipelineMcpContext.ServersDoc,
                        useStructuredExtraction,
                        capabilityPreflight)
                    : BuildMarkExtractableBlocksRepairPrompt(
                        normalizedMarkdown,
                        pipelineMcpContext.ServersDoc,
                        previousAnnotatedMarkdown,
                        previousExtractionJson,
                        previousValidationErrors,
                        useStructuredExtraction,
                        capabilityPreflight);

            string annotatedMarkdown;
            string? currentExtractionJson = null;
            StructuredPipelineExtractionMetadata structuredMetadata;
            IReadOnlyList<string> responseValidationErrors;
            PipelineRepairApplication? repairApplication = null;
            try
            {
                if (useTargetedPatch)
                {
                    repairApplication = await RequestPipelineExtractionPatchAsync(
                        llmClient,
                        normalizedMarkdown,
                        pipelineMcpContext,
                        targetedPatchBase!,
                        previousValidationErrors!,
                        provider,
                        model,
                        reasoning,
                        ctx,
                        ct,
                        attempt,
                        maxAttempts);
                    annotatedMarkdown = repairApplication.AnnotatedMarkdown;
                    structuredMetadata = StructuredPipelineExtractionMetadata.Empty;
                    responseValidationErrors = Array.Empty<string>();
                }
                else if (useStructuredExtraction)
                {
                    var response = await ExecutePipelineLlmStructuredPhaseAsync(
                        llmClient,
                        "mark_extractable_blocks",
                        prompt!,
                        provider,
                        model,
                        reasoning,
                        ctx,
                        ct,
                        attempt,
                        maxAttempts,
                        BuildMarkExtractableBlocksStructuredOutputSchemaForCapabilities(capabilityPreflight));

                    (annotatedMarkdown, structuredMetadata, responseValidationErrors) =
                        ParseMarkExtractableBlocksResponse(response, allowAnnotatedMarkdownFallback: false);
                }
                else
                {
                    annotatedMarkdown = await ExecutePipelineLlmTextPhaseAsync(
                        llmClient,
                        "mark_extractable_blocks",
                        prompt!,
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
                if (WorkflowPlanDiagnostics.IsNonRepairableLlmFailure(ex))
                    throw;
                if (IsPipelineExtractionRepairStalled(ex))
                    throw;
                if (IsPipelineQualityReviewContractFailure(ex))
                    throw;
                if (useTargetedPatch)
                {
                    var patchFailureFingerprint = WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(ex);
                    repeatedPatchFailureAttempts = string.Equals(
                        previousPatchFailureFingerprint,
                        patchFailureFingerprint,
                        StringComparison.Ordinal)
                        ? repeatedPatchFailureAttempts + 1
                        : 1;
                    previousPatchFailureFingerprint = patchFailureFingerprint;
                    if (repeatedPatchFailureAttempts >= 2)
                    {
                        var stalled = BuildPipelineExtractionRepairStalledException(
                            attempt,
                            "Two targeted patch responses repeated the same contract diagnostic.",
                            targetedPatchBase!);
                        ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.patch.stall_reason", "repeated_patch_contract_diagnostic");
                        throw stalled;
                    }
                }
                lastException = ex;
                previousAnnotatedMarkdown = targetedPatchBaseMarkdown;
                previousExtractionJson = targetedPatchBase == null
                    ? previousExtractionJson
                    : BuildExtractionJson(targetedPatchBase).ToJsonString(PromptJsonOptions);
                previousValidationErrors = new[]
                {
                    $"{(useTargetedPatch ? "patch_pipeline_extraction" : "mark_extractable_blocks")} failed before extraction validation: {ex.Message}"
                };
                AddPipelineExtractionRetryTelemetry(ctx, attempt, maxAttempts, ex);
                continue;
            }

            previousPatchFailureFingerprint = null;
            repeatedPatchFailureAttempts = 0;

            using var extractionSpan = ctx.BeginTelemetrySpan("workflow.plan.pipeline.extract_subworkflow_specs", "extract_subworkflow_specs", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.max_attempts", maxAttempts)
            });

            try
            {
                if (repairApplication == null && structuredMetadata.IsStructuredResponse)
                {
                    if (previousValidatedStructuredMetadata != null)
                    {
                        structuredMetadata = PreservePreviouslyValidatedStructuredContracts(
                            structuredMetadata,
                            previousValidatedStructuredMetadata);
                    }

                    annotatedMarkdown = NormalizeAnnotatedMarkdownContractSections(
                        annotatedMarkdown,
                        structuredMetadata);
                }
                var extraction = repairApplication?.Extraction ?? ExtractSubworkflowSpecs(annotatedMarkdown);
                if (repairApplication == null)
                {
                    extraction = EnrichSubworkflowSpecsWithStructuredMetadata(
                        extraction,
                        structuredMetadata,
                        pipelineMcpContext,
                        responseValidationErrors);
                }
                extraction = ComposeLockedCapabilitiesIntoPipelineExtraction(
                    extraction,
                    capabilityPreflight,
                    pipelineMcpContext);
                AddConditionalCapabilityOwnershipTelemetry(ctx, extraction);
                extraction = ReconcileRequiredLeafToolValidationAfterCapabilityComposition(extraction);
                if (repairApplication != null)
                    extraction = RevalidatePatchedPipelineExtraction(extraction, pipelineMcpContext);
                extraction = PreserveSharedPipelineBoundaryContracts(extraction);
                extraction = ValidateLockedCapabilitiesInExtraction(extraction, capabilityPreflight);
                extraction = ValidatePlannedToolArtifactPrerequisites(
                    extraction,
                    pipelineMcpContext,
                    normalizedMarkdown);
                currentExtractionJson = BuildExtractionJson(extraction).ToJsonString(PromptJsonOptions);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.subworkflow_count", extraction.Subworkflows.Count);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.validation_error_count", extraction.ValidationErrors.Count);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.planned_tool_count", extraction.Subworkflows.Sum(static spec => spec.PlannedTools.Count));
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.operation_count", repairApplication?.OperationCount ?? 0);
                extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.base_fingerprint", repairApplication?.BaseFingerprint);

                if (extraction.ValidationErrors.Count == 0)
                {
                    if (structuredMetadata.IsStructuredResponse)
                        previousValidatedStructuredMetadata = structuredMetadata;

                    extraction = await ReviewPipelineExtractionQualityAsync(
                        llmClient,
                        normalizedMarkdown,
                        pipelineMcpContext,
                        annotatedMarkdown,
                        extraction,
                        repairApplication == null ? null : targetedPatchBase,
                        repairApplication?.AddressedDiagnosticCodes,
                        provider,
                        model,
                        reasoning,
                        useStructuredExtraction,
                        ctx,
                        ct,
                        attempt,
                        maxAttempts);

                    if (extraction.QualityReview != null)
                    {
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_quality.score", extraction.QualityReview.Score);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_quality.verdict", extraction.QualityReview.Verdict);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_quality.diagnostic_count", extraction.QualityReview.Diagnostics.Count);
                        extractionSpan.SetAttribute(
                            "gnougo-flow.plan.pipeline.extraction_quality.qualified_critical_count",
                            extraction.QualityReview.Diagnostics.Count(static diagnostic => diagnostic.EvidenceQualified
                                && string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal)));
                        extractionSpan.SetAttribute(
                            "gnougo-flow.plan.pipeline.extraction_quality.warning_count",
                            extraction.QualityReview.Diagnostics.Count(static diagnostic =>
                                string.Equals(diagnostic.Severity, "warning", StringComparison.Ordinal)));
                    }

                    var patchOperationCount = repairApplication?.OperationCount ?? 0;
                    var candidateFingerprint = BuildPipelineExtractionFingerprint(extraction);
                    var previousBestScore = bestValidatedCandidate?.QualityReview?.Score;
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.fingerprint", candidateFingerprint);
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.candidate.score", extraction.QualityReview?.Score);
                    extractionSpan.SetAttribute(
                        "gnougo-flow.plan.pipeline.candidate.score_change",
                        previousBestScore == null || extraction.QualityReview == null
                            ? null
                            : extraction.QualityReview.Score - previousBestScore.Value);
                    var improved = bestValidatedCandidate == null
                                   || IsPipelineExtractionCandidateStrictlyBetter(
                                       extraction,
                                       patchOperationCount,
                                       bestValidatedCandidate,
                                       bestPatchOperationCount);
                    if (improved)
                    {
                        bestValidatedCandidate = extraction;
                        bestAnnotatedMarkdown = annotatedMarkdown;
                        bestPatchOperationCount = patchOperationCount;
                        bestRepairableCandidate = null;
                        bestRepairableAnnotatedMarkdown = null;
                        bestRepairablePatchOperationCount = 0;
                        rejectedPatchAttempts = 0;
                        qualityNonImprovingPatchAttempts = 0;
                        deterministicRegressionPatchAttempts = 0;
                        validationNonImprovingPatchAttempts = 0;
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.accepted", repairApplication != null);
                    }
                    else if (repairApplication != null)
                    {
                        rejectedPatchAttempts++;
                        qualityNonImprovingPatchAttempts++;
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.accepted", false);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.rejected_attempts", rejectedPatchAttempts);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.quality_non_improving_attempts", qualityNonImprovingPatchAttempts);
                        if (rejectedPatchAttempts >= 2)
                        {
                            var stalled = BuildPipelineExtractionRepairStalledException(
                                attempt,
                                "Targeted patches did not improve the best deterministically valid extraction.",
                                bestValidatedCandidate!,
                                qualityNonImprovingPatchAttempts,
                                deterministicRegressionPatchAttempts,
                                validationNonImprovingPatchAttempts);
                            extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.stall_reason", "repeated_non_improvement");
                            extractionSpan.Fail(stalled);
                            throw stalled;
                        }

                        previousAnnotatedMarkdown = bestAnnotatedMarkdown;
                        previousExtractionJson = BuildExtractionJson(bestValidatedCandidate!).ToJsonString(PromptJsonOptions);
                        previousValidationErrors = new[]
                        {
                            "PIPELINE_EXTRACTION_PATCH_REGRESSION: The proposed patch was rejected because it did not strictly improve the best validated candidate. Repair the unchanged base candidate with a smaller evidence-directed patch."
                        }.Concat(BuildExtractionQualityReviewRetryFeedback(bestValidatedCandidate!.QualityReview)).ToArray();
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "retrying");
                        continue;
                    }

                    if (ShouldRetryPipelineExtractionReview(extraction.QualityReview))
                    {
                        var reviewException = BuildPipelineExtractionQualityReviewException(extraction, annotatedMarkdown, pipelineMcpContext);
                        var stalled = DetectRepairStall(
                            reviewException,
                            attempt,
                            isRepairAttempt: repairApplication != null,
                            ref previousQualityDiagnosticFingerprint,
                            ref unchangedQualityRepairAttempts);
                        if (stalled != null)
                        {
                            extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.stall_reason", "repeated_diagnostic_fingerprint");
                            extractionSpan.Fail(stalled);
                            throw stalled;
                        }
                        extractionSpan.AddEvent(
                            "gnougo-flow.plan.pipeline.extraction_quality.validation_error",
                            BuildPlanErrorTelemetryAttributes(reviewException, attempt, "review_extraction_quality"));

                        if (attempt >= maxAttempts)
                        {
                            extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "failed");
                            extractionSpan.Fail(reviewException);
                            throw reviewException;
                        }

                        lastException = reviewException;
                        previousAnnotatedMarkdown = bestAnnotatedMarkdown;
                        previousExtractionJson = BuildExtractionJson(bestValidatedCandidate!).ToJsonString(PromptJsonOptions);
                        previousValidationErrors = BuildExtractionQualityReviewRetryFeedback(bestValidatedCandidate!.QualityReview);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "retrying");
                        extractionSpan.Fail(reviewException);
                        AddPipelineExtractionRetryTelemetry(ctx, attempt, maxAttempts, reviewException);
                        continue;
                    }

                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "succeeded");
                    return (annotatedMarkdown, extraction);
                }

                if (repairApplication != null && bestValidatedCandidate != null)
                {
                    rejectedPatchAttempts++;
                    deterministicRegressionPatchAttempts++;
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.accepted", false);
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.rejected_attempts", rejectedPatchAttempts);
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.deterministic_regression_attempts", deterministicRegressionPatchAttempts);
                    var regressionException = BuildPipelineExtractionException(
                        extraction,
                        annotatedMarkdown,
                        pipelineMcpContext);
                    extractionSpan.AddEvent(
                        "gnougo-flow.plan.pipeline.extraction.validation_error",
                        BuildPlanErrorTelemetryAttributes(regressionException, attempt, "extract_subworkflow_specs"));
                    if (rejectedPatchAttempts >= 2)
                    {
                        var stalled = BuildPipelineExtractionRepairStalledException(
                            attempt,
                            "Targeted patches were rejected before a better candidate could be emitted.",
                            bestValidatedCandidate,
                            qualityNonImprovingPatchAttempts,
                            deterministicRegressionPatchAttempts,
                            validationNonImprovingPatchAttempts,
                            extraction.ValidationErrors);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.stall_reason", "deterministic_validation_regression");
                        extractionSpan.Fail(stalled);
                        throw stalled;
                    }

                    previousAnnotatedMarkdown = bestAnnotatedMarkdown;
                    previousExtractionJson = BuildExtractionJson(bestValidatedCandidate).ToJsonString(PromptJsonOptions);
                    previousValidationErrors = new[]
                    {
                        "PIPELINE_EXTRACTION_PATCH_REGRESSION: The proposed patch was rejected because it introduced deterministic validation defects. Repair the unchanged best candidate without weakening any validated schema or ownership contract."
                    }.Concat(BuildExtractionQualityReviewRetryFeedback(bestValidatedCandidate.QualityReview)).ToArray();
                    extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "retrying");
                    extractionSpan.Fail(regressionException);
                    continue;
                }

                if (useStructuredExtraction
                    && IsPipelineExtractionPatchableInvalidCandidate(extraction))
                {
                    var improvedInvalidCandidate = bestRepairableCandidate == null
                                                   || IsPipelineExtractionValidationStrictlyBetter(
                                                       extraction,
                                                       repairApplication?.OperationCount ?? 0,
                                                       bestRepairableCandidate,
                                                       bestRepairablePatchOperationCount);
                    if (improvedInvalidCandidate)
                    {
                        bestRepairableCandidate = extraction;
                        bestRepairableAnnotatedMarkdown = annotatedMarkdown;
                        bestRepairablePatchOperationCount = repairApplication?.OperationCount ?? 0;
                        rejectedPatchAttempts = 0;
                        qualityNonImprovingPatchAttempts = 0;
                        deterministicRegressionPatchAttempts = 0;
                        validationNonImprovingPatchAttempts = 0;
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.accepted", repairApplication != null);
                    }
                    else if (repairApplication != null)
                    {
                        rejectedPatchAttempts++;
                        validationNonImprovingPatchAttempts++;
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.accepted", false);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.rejected_attempts", rejectedPatchAttempts);
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.validation_non_improving_attempts", validationNonImprovingPatchAttempts);
                        if (rejectedPatchAttempts >= 2)
                        {
                            var stalled = BuildPipelineExtractionRepairStalledException(
                                attempt,
                                "Targeted patches did not reduce deterministic extraction validation errors.",
                                bestRepairableCandidate!,
                                qualityNonImprovingPatchAttempts,
                                deterministicRegressionPatchAttempts,
                                validationNonImprovingPatchAttempts,
                                extraction.ValidationErrors);
                            extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.patch.stall_reason", "repeated_validation_non_improvement");
                            extractionSpan.Fail(stalled);
                            throw stalled;
                        }

                        previousAnnotatedMarkdown = bestRepairableAnnotatedMarkdown;
                        previousExtractionJson = BuildExtractionJson(bestRepairableCandidate!).ToJsonString(PromptJsonOptions);
                        previousValidationErrors = bestRepairableCandidate!.ValidationErrors;
                        extractionSpan.SetAttribute("gnougo-flow.plan.pipeline.extraction_status", "retrying");
                        continue;
                    }
                }

                var validationException = BuildPipelineExtractionException(
                    extraction,
                    annotatedMarkdown,
                    pipelineMcpContext);
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
                previousAnnotatedMarkdown = bestAnnotatedMarkdown ?? bestRepairableAnnotatedMarkdown ?? annotatedMarkdown;
                var bestCandidate = bestValidatedCandidate ?? bestRepairableCandidate;
                previousExtractionJson = bestCandidate == null
                    ? currentExtractionJson
                    : BuildExtractionJson(bestCandidate).ToJsonString(PromptJsonOptions);
                previousValidationErrors = bestCandidate?.ValidationErrors ?? extraction.ValidationErrors;
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
                if (WorkflowPlanDiagnostics.IsNonRepairableLlmFailure(ex))
                    throw;
                if (IsPipelineExtractionRepairStalled(ex))
                    throw;
                if (IsPipelineQualityReviewContractFailure(ex))
                    throw;
                lastException = ex;
                previousAnnotatedMarkdown = bestAnnotatedMarkdown ?? bestRepairableAnnotatedMarkdown ?? annotatedMarkdown;
                var bestCandidate = bestValidatedCandidate ?? bestRepairableCandidate;
                previousExtractionJson = bestCandidate == null
                    ? currentExtractionJson ?? previousExtractionJson
                    : BuildExtractionJson(bestCandidate).ToJsonString(PromptJsonOptions);
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

    private static WorkflowPipelineExtraction ReconcileRequiredLeafToolValidationAfterCapabilityComposition(
        WorkflowPipelineExtraction extraction)
    {
        var resolvedLeaves = extraction.Subworkflows
            .Where(static spec => spec.PlannedTools.Any(static tool => tool.Required)
                                  || (spec.PlannedNativeSteps?.Any(static step => step.Required) ?? false))
            .Select(static spec => spec.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (resolvedLeaves.Count == 0)
            return extraction;

        var errors = extraction.ValidationErrors
            .Where(error => !error.StartsWith(
                                "PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL:",
                                StringComparison.Ordinal)
                            || !resolvedLeaves.Any(leaf => error.Contains(
                                $"Subworkflow '{leaf}'",
                                StringComparison.Ordinal)))
            .ToArray();
        var rootCauses = extraction.RootCauses
            .Where(rootCause => !string.Equals(
                                    rootCause.Code,
                                    "PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL",
                                    StringComparison.Ordinal)
                                || string.IsNullOrWhiteSpace(rootCause.LeafName)
                                || !resolvedLeaves.Contains(rootCause.LeafName))
            .ToArray();
        return errors.Length == extraction.ValidationErrors.Count
               && rootCauses.Length == extraction.RootCauses.Count
            ? extraction
            : extraction with { ValidationErrors = errors, RootCauses = rootCauses };
    }

    private static string BuildMarkExtractableBlocksPrompt(
        string normalizedMarkdown,
        string? pipelineMcpServersDoc,
        bool useStructuredExtraction,
        CapabilityPreflightResult capabilityPreflight)
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
            Main cannot call MCP, LLM, file, rendering, command, or other external capabilities directly. Every required external invocation must belong to a cohesive leaf even when it is a single call; only its ordering, guard, loop, native human confirmation, and data routing stay in main.

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
            - When a typed output is passed into a later tool request, preserve every field that the downstream request contract or block prose actually uses. Do not replace a structured record with a lossy projection that omits required locations, evidence, ranges, discriminators, or explanations.
            - Build an explicit typed dataflow across every leaf boundary. If main passes a producer result to a consumer, declare a named output on the producer and a compatible named input on the consumer; an instruction in prose is not a substitute for the contract.
            - A consumer that validates, filters, classifies, explains, or branches on an upstream result must receive the complete typed evidence it reads. Do not expose only one sibling field such as changed items when the consumer also needs status, coverage, failures, rejected items, diagnostics, locations, or explanations.
            - Every outcome promised by a leaf's goal, content, or concrete_outcome and consumed downstream must be a typed output. Include negative and unresolved states needed for fail-closed behavior, not only the successful subset.
            - A leaf that owns conditionally activated calls must receive or derive the activation decision source as typed runtime data. Never ask the user for a future runtime outcome and never add a mutating default branch.
            - For a field that must always be present but may legitimately contain no value, set `required: true` and `nullable: true`. This is especially important for locations, ranges, evidence anchors, optional external metadata, and runtime diagnostics that distinguish null from omission.
            - When a planned tool requires an existing operational artifact that the user did not explicitly supply, plan a documented producer tool and expose its compatible response field through an upstream leaf. Never invent an artifact path, handle, directory, or workspace input.
            - MCP artifact_contract metadata is authoritative. Materialize one artifact per locked producer occurrence, expose the exact declared response pointer, and route that value unchanged through main to every compatible consumer leaf.
            - Do not repeat a materializer merely because preparation, validation, analysis, or publication are separate leaves.
            - When cleanup is required and a materializer accepts a caller-chosen creation target, derive that exact target before the materializer call and register it for cleanup before execution. Reconcile the returned artifact after success without duplicating the registry entry. Never invent a creation target when the documented tool contract does not accept one.
            - Required workflow inputs are validated by the runtime before execution. Do not add missing/empty required-input decision trees or user-facing fallback branches unless the user explicitly requested that behavior.
            - Do not copy every MCP field into every block; include only the variables needed for that block boundary.
            - Do not use placeholders for missing required variables. If they are not derivable, make them explicit inputs.
            """);

        if (useStructuredExtraction)
        {
            sb.Append("""
            - Structured subworkflow metadata must repeat inputs and outputs as strongly typed entries with descriptions.
            - When a string field is a closed discriminator, declare every allowed value in `enum_values`; never describe a closed set only in prose.
            - Structured subworkflow metadata must classify each leaf with `work_kind`: `orchestration`, `deterministic_shaping`, or `external_work`.
            - Structured subworkflow metadata must also declare `contract_role`: `external_action`, `typed_data_producer`, `algorithmic_transform`, `deterministic_glue`, `orchestration`, or `abstract_policy`.
            - Only `external_action`, `typed_data_producer`, and `algorithmic_transform` are valid leaf roles. `deterministic_glue`, `orchestration`, and `abstract_policy` must stay in `## Main workflow orchestration`.
            - Structured subworkflow metadata must include `concrete_outcome`: the exact concrete value, side effect, or typed data product this leaf owns.
            - Structured subworkflow metadata must include `owned_operation_ids`. Copy each supplied locked operation ID to exactly one cohesive leaf that owns its implementation. This ownership declaration is independent from `planned_tools`: it remains required even if a planned-tool entry is accidentally omitted, and it must never be inferred from leaf wording, provider/tool names, catalog numbering, or document order.
            - Structured output fields should declare concrete object properties and array item types when later workflow steps need field-level access.
            - Avoid `any`, bare `object`, and bare `array` outputs. If an output may be looped over or inspected by the main workflow, declare concrete `items` and object `properties`.
            - Structured `planned_tools` must list every MCP server tool or prompt this leaf is expected to call directly.
            - Native Flow steps are supplied separately by the locked extraction contract. Never encode a native step as an MCP `planned_tools` entry; `planned_tools` may contain only an exact capability documented in the global MCP context.
            - A capability name that appears only inside a prohibition such as "do not call ..." is not a planned tool. Never add negated capabilities to `planned_tools`.
            - Mark planned tools as required when omitting that MCP call would violate the leaf goal.
            - For each relevant MCP tool or prompt, add a structured planned_tools entry with the exact server name, kind, method name, purpose, consumed fields, produced fields, and any locked request_bindings.
            - Treat each locked capability as a separate invocation obligation, even when multiple operations use the same physical tool. Copy supplied operation_id and catalog_id values into operation_ids and catalog_ids for traceability.
            - Every planned tool carrying a locked operation_id must belong to the leaf whose `owned_operation_ids` contains that exact ID. A composed or conditional operation keeps one leaf owner while listing each distinct locked catalog occurrence as its own planned tool.
            - request_bindings are only immutable selector literals explicitly documented by a selected capability contract. They are not ordinary tool arguments. Use an empty array unless the MCP context explicitly supplies a locked JSON Pointer/scalar pair; never put runtime commands, paths, URLs, identifiers, or other dynamic inputs in request_bindings.
            - Never return an `external_work` or `external_action` leaf with both an empty planned_tools array and an empty planned_native_steps array. Select the exact documented MCP or native capability, or keep the work in main when it is orchestration/glue, or classify it as an algorithmic transform only when it operates exclusively on declared inputs without external calls or state inspection.
            - Do not invent a separate external inspection, preparation, or analysis leaf that has no corresponding requested/locked capability occurrence. Fold that obligation into the cohesive leaf that owns the compatible locked external operation, or rewrite it as deterministic processing of an already materialized typed input with no external session or state inspection.
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
            - Main contains only workflow.call, native human confirmation, and deterministic support nodes. It must never own or inline an MCP, LLM, command, file, rendering, or other external step.
            - Main may order independent leaves and pass a typed output from an earlier producer leaf into one or more later consumer leaves.
            - The final YAML will contain the main workflow and all leaf subworkflows in the same local YAML file.
            - The main workflow calls leaf workflows with local workflow.call.

            <normalized_markdown>
            {{normalizedMarkdown}}
            </normalized_markdown>
            """);

        AppendLockedMainNativeStepGuidance(sb, capabilityPreflight);

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
        string? previousExtractionJson,
        IReadOnlyList<string> validationErrors,
        bool useStructuredExtraction,
        CapabilityPreflightResult capabilityPreflight)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildMarkExtractableBlocksPrompt(
            normalizedMarkdown,
            pipelineMcpServersDoc,
            useStructuredExtraction,
            capabilityPreflight).TrimEnd());
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
            sb.AppendLine("- Structured contract_role must be one of external_action, typed_data_producer, algorithmic_transform, deterministic_glue, orchestration, or abstract_policy.");
            sb.AppendLine("- Preserve `owned_operation_ids` as exact locked ownership claims. Assign each supplied non-main operation ID to exactly one cohesive leaf, independently of planned_tools; never choose ownership from wording, names, catalog numbering, or leaf order.");
            sb.AppendLine("- Only external_action, typed_data_producer, and algorithmic_transform can remain as leaf blocks; move deterministic_glue, orchestration, and abstract_policy back to the main workflow.");
            sb.AppendLine("- Every remaining leaf must have a concrete_outcome and strongly typed input and output schemas.");
            sb.AppendLine("- Public leaf boundaries must never use `type: any`, bare objects, or arrays without concrete items. Model the fields consumed from another leaf explicitly.");
            sb.AppendLine("- Treat the prior extraction JSON below as the contract snapshot being repaired. Apply every diagnostic to its named leaf and preserve unrelated valid contracts and capability ownership.");
            sb.AppendLine("- Every value that main passes from one leaf to another must be a named producer output and a named consumer input with compatible complete schemas; prose in main is not a data contract.");
            sb.AppendLine("- If a consumer's goal, content, or decision reads fields from an upstream result, accept the complete typed result or explicitly model every consumed field. Never pass only one convenient sibling field.");
            sb.AppendLine("- Every result promised by a leaf's goal, content, or concrete_outcome and used by main or another leaf must be a typed output, including status, coverage/completeness, unresolved/failure state, rejected items, diagnostics, explanations, and branch decisions when applicable.");
            sb.AppendLine("- A leaf that owns conditionally activated tools must accept or derive the declared runtime decision source through its typed contract. Future runtime outcomes stay runtime data and must never become generation-time clarification or a mutating default.");
            sb.AppendLine("- Represent a field whose presence is required but value may be absent with `required: true` and `nullable: true`; do not make location, range, discriminator, or evidence fields optional when downstream logic distinguishes null from omission.");
            sb.AppendLine("- Contract-field `properties` are recursive. For every object property, dictionary object value, or object array item named by a validation error, add its own non-empty nested `properties` entries; never stop at `type: object`.");
            sb.AppendLine("- External-work leaves with matching MCP capabilities must include concrete planned_tools entries.");
            sb.AppendLine("- Structured planned_tools must use exact MCP server/tool/prompt names from the global MCP context.");
            sb.AppendLine("- Never encode a native Flow step as planned_tools. Native requirements are supplied separately by the locked extraction contract; planned_tools contains only exact discovered MCP capabilities.");
            sb.AppendLine("- request_bindings must be empty unless the MCP context explicitly documents the exact selector pointer and literal; normal runtime tool inputs never belong in request_bindings.");
            sb.AppendLine("- Never leave an external_work/external_action leaf without a required planned MCP tool or native step: select an exact documented capability, move orchestration/glue back to main, or make it an input-only algorithmic transform with no external inspection.");
            sb.AppendLine("- Remove or merge every invented external inspection/preparation/analysis leaf that has no requested or locked capability occurrence. A deterministic_shaping leaf may process declared typed inputs but must not require an external session, filesystem inspection, or external state in prose.");
            sb.AppendLine("- For every unlocked selector-based planned tool, preserve an exact literal request binding whenever its purpose or prose names the selector. If that selector is not actually required, remove the invented call instead of leaving a selector-incomplete whole-tool entry.");
            sb.AppendLine("- Fix low extraction scores by either making the leaf a meaningful external/algorithmic unit with concrete planned_tools/contracts, or moving trivial shaping/orchestration back to the main workflow.");
        }
        sb.AppendLine("- The document must include `## Main workflow orchestration` after the leaf blocks.");
        AppendPromptSectionEnd(sb, "correction_checklist");

        if (!string.IsNullOrWhiteSpace(previousAnnotatedMarkdown))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "invalid_annotated_markdown", previousAnnotatedMarkdown);
        }

        if (!string.IsNullOrWhiteSpace(previousExtractionJson))
        {
            sb.AppendLine();
            AppendPromptSection(sb, "invalid_extraction_json", previousExtractionJson);
        }

        sb.AppendLine();
        sb.AppendLine(useStructuredExtraction
            ? "Fix the validation errors above and return ONLY the corrected JSON response."
            : "Fix the validation errors above and return ONLY the corrected annotated Markdown.");
        return sb.ToString();
    }

    private static WorkflowRuntimeException BuildPipelineExtractionException(
        WorkflowPipelineExtraction extraction,
        string? annotatedMarkdown,
        PipelineMcpContext pipelineMcpContext)
    {
        var validationErrors = extraction.ValidationErrors;
        var rootCauses = extraction.RootCauses;
        var ownership = BuildPipelinePatchOwnershipSummary(extraction);
        var details = new JsonObject
        {
            ["planning_outcome"] = "cannot_plan_safely",
            ["recommended_action"] = "clarify_or_abandon",
            ["validation"] = BuildValidationJson(validationErrors),
            ["root_causes"] = BuildPipelineRootCausesJson(rootCauses),
            ["leaf_ownership"] = ownership.DeepClone(),
            ["pipeline_inspection"] = new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["root_cause_count"] = rootCauses.Count,
                    ["validation_error_count"] = validationErrors.Count
                },
                ["mcp_context"] = BuildPipelineMcpContextJson(pipelineMcpContext),
                ["annotated_markdown"] = annotatedMarkdown ?? "",
                ["root_causes"] = BuildPipelineRootCausesJson(rootCauses)
            }
        };

        if (!string.IsNullOrWhiteSpace(annotatedMarkdown))
            details["invalid_annotated_markdown"] = annotatedMarkdown;

        return new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "workflow.plan pipeline extraction failed: " + string.Join("; ", validationErrors)
            + " | redacted leaf ownership: " + ownership.ToJsonString(PromptJsonOptions),
            details: details);
    }

    private static async Task<WorkflowPipelineExtraction> ReviewPipelineExtractionQualityAsync(
        ILLMClient llmClient,
        string normalizedMarkdown,
        PipelineMcpContext pipelineMcpContext,
        string annotatedMarkdown,
        WorkflowPipelineExtraction extraction,
        WorkflowPipelineExtraction? reviewBaseline,
        IReadOnlySet<string>? addressedDiagnosticCodes,
        string? provider,
        string model,
        string? reasoning,
        bool useStructuredOutput,
        StepExecutionContext ctx,
        CancellationToken ct,
        int attempt,
        int maxAttempts)
    {
        var basePrompt = BuildExtractionQualityReviewPrompt(
            normalizedMarkdown,
            pipelineMcpContext,
            annotatedMarkdown,
            extraction,
            reviewBaseline,
            addressedDiagnosticCodes);
        Exception? firstContractFailure = null;
        for (var reviewAttempt = 1; reviewAttempt <= 2; reviewAttempt++)
        {
            var prompt = reviewAttempt == 1
                ? basePrompt
                : basePrompt + "\n\nThe previous quality-review response violated its structured contract. Return one corrected response only. Contract error: "
                  + firstContractFailure!.Message;
            try
            {
                var response = useStructuredOutput
                    ? await ExecutePipelineLlmStructuredPhaseAsync(
                        llmClient,
                        "review_extraction_quality",
                        prompt,
                        provider,
                        model,
                        reasoning,
                        ctx,
                        ct,
                        attempt,
                        maxAttempts,
                        BuildExtractionQualityReviewStructuredOutputSchema())
                    : new LLMResponse
                    {
                        Text = await ExecutePipelineLlmTextPhaseAsync(
                            llmClient,
                            "review_extraction_quality",
                            prompt,
                            provider,
                            model,
                            reasoning,
                            ctx,
                            ct,
                            attempt,
                            maxAttempts)
                    };

                var review = NormalizeExtractionQualityReviewAgainstLockedContracts(
                    normalizedMarkdown,
                    extraction,
                    pipelineMcpContext,
                    ParseExtractionQualityReviewResponse(response));
                if (reviewBaseline?.QualityReview != null)
                {
                    review = StabilizePipelineExtractionQualityReviewAgainstBaseline(
                        reviewBaseline,
                        extraction,
                        review,
                        addressedDiagnosticCodes ?? new HashSet<string>(StringComparer.Ordinal));
                }
                ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.extraction_quality.score", review.Score);
                ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.extraction_quality.verdict", review.Verdict);
                ctx.SetTelemetryAttribute("gnougo-flow.plan.pipeline.extraction_quality.diagnostic_count", review.Diagnostics.Count);
                return extraction with { QualityReview = review };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (WorkflowPlanDiagnostics.IsNonRepairableLlmFailure(ex))
                    throw;
                if (!useStructuredOutput)
                {
                    var warning = "PIPELINE_EXTRACTION_QUALITY_REVIEW_WARNING: legacy text review_extraction_quality failed or returned invalid JSON output; continuing with deterministic validation only. "
                                  + ex.Message;
                    ctx.Engine.Logger.LogWarning(ex, "workflow.plan pipeline: legacy text extraction quality review failed; continuing with deterministic validation");
                    return extraction with { QualityWarnings = AppendPipelineExtractionQualityWarning(extraction, warning) };
                }
                if (reviewAttempt == 1)
                {
                    firstContractFailure = ex;
                    ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.extraction_quality.contract_retry", new[]
                    {
                        new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "review_extraction_quality"),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.review_attempt", reviewAttempt),
                        new KeyValuePair<string, object?>("error.type", ex.GetType().Name)
                    });
                    continue;
                }

                throw BuildPipelineQualityReviewContractFailure(ex);
            }
        }

        throw BuildPipelineQualityReviewContractFailure(firstContractFailure
            ?? new InvalidOperationException("Quality review did not return a result."));
    }

    private static string BuildExtractionQualityReviewPrompt(
        string normalizedMarkdown,
        PipelineMcpContext pipelineMcpContext,
        string annotatedMarkdown,
        WorkflowPipelineExtraction extraction,
        WorkflowPipelineExtraction? reviewBaseline,
        IReadOnlySet<string>? addressedDiagnosticCodes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are reviewing the quality of a `workflow.plan` pipeline `mark_extractable_blocks` result.");
        sb.AppendLine("Judge whether the extracted leaf workflow contracts and main orchestration faithfully cover the original normalized request.");
        sb.AppendLine();
        sb.AppendLine("You are NOT generating YAML. You are only judging extraction quality.");
        sb.AppendLine("Return ONLY a strict JSON object. Do not wrap it in Markdown fences and do not include commentary.");
        sb.AppendLine("The JSON object must have exactly these top-level fields:");
        sb.AppendLine("- `score`: integer from 0 to 100.");
        sb.AppendLine("- `verdict`: `pass` or `retry`.");
        sb.AppendLine("- `diagnostics`: array of objects with `code`, `kind`, `severity`, `leaf_name`, `message`, `recommendation`, and `evidence`.");
        sb.AppendLine("- `retry_guidance`: concise summary of evidence-qualified critical diagnostics only, or an empty string. It is advisory metadata and is never an independent repair instruction.");
        sb.AppendLine("Each diagnostic also requires `remediation_surface`: `extraction_contract`, `locked_ownership`, or `generated_workflow_topology`. Use `locked_ownership` when questioning an operation/capability owner already declared by the locked extraction contract. Use `generated_workflow_topology` for exact selector expressions, switch cases, or intra-leaf ordering that do not exist until YAML generation. Only `extraction_contract` can block this review; the other surfaces are validated authoritatively in their owning phase.");
        sb.AppendLine("Every critical diagnostic must cite machine-verifiable evidence. Every evidence item has `source`, `reference`, and `excerpt`. For source `request`, both reference and excerpt must be the same exact non-empty substring from normalized_prompt. For sources `extraction` and `capability_contract`, reference is an RFC 6901 JSON Pointer into the supplied JSON section and excerpt is an exact non-empty substring of the resolved canonical value. A pointer that merely exists does not prove a claim when its excerpt is absent from the resolved value. Unsupported critical claims are advisory and cannot force repair.");
        sb.AppendLine("Diagnostic `severity` must be `info`, `warning`, or `critical`.");
        sb.AppendLine();
        sb.AppendLine("Score rubric:");
        sb.AppendLine("- 90-100: excellent coverage, cohesive leaf boundaries, concrete contracts, no important missing work.");
        sb.AppendLine("- 75-89: acceptable, minor improvements only.");
        sb.AppendLine("- 50-74: risky; retry unless the issues are clearly cosmetic.");
        sb.AppendLine("- 0-49: unusable extraction; retry.");
        sb.AppendLine();
        sb.AppendLine("Critical issues include:");
        sb.AppendLine("- a major obligation from the original prompt is missing from leaves or main orchestration;");
        sb.AppendLine("- external work is described in prose but not represented by a concrete external-action leaf/tool contract;");
        sb.AppendLine("- main orchestration fabricates an operational artifact that should come from an external producer leaf;");
        sb.AppendLine("- leaves are abstract, cross-cutting, or too broad to generate reliably;");
        sb.AppendLine("- trivial deterministic glue is extracted instead of staying in main.");
        sb.AppendLine();
        sb.AppendLine("Authoritative platform rules:");
        sb.AppendLine("- locked capability operations, selector bindings, local-processing obligations, and constraints in extraction_json are authoritative;");
        sb.AppendLine("- main can call leaves, perform native human confirmation, and use deterministic support nodes only; it cannot own a direct MCP, LLM, command, file, rendering, or other external step. If an external capability lacks a compatible owner, require a cohesive external-action leaf rather than moving the call into main;");
        sb.AppendLine("- required=true on a locked planned tool means the generated workflow must contain that call; it does not mean the call is unconditional, so preserve any documented guard or branch;");
        sb.AppendLine("- never recommend removing or downgrading a planned tool carrying locked operation_ids or catalog_ids;");
        sb.AppendLine("- locked capabilities are an exact multiset: deterministic validation has already assigned each required occurrence once; an additional technical call may intentionally have empty operation_ids/catalog_ids, and you must not ask to duplicate an identity already assigned to another call;");
        sb.AppendLine("- occurrence identity is the `(operation_id, catalog_id)` pair. A composed operation intentionally repeats one operation_id across multiple distinct catalog_ids, and those complementary calls may belong to different cohesive leaves; do not report that as duplication;");
        sb.AppendLine("- a documented unconditional draft/preparation write followed by exactly one conditional terminal finalization is not a duplicate final decision. Count terminal effect selectors, not every prerequisite write; still reject multiple unconditional or independently activated terminal effects.");
        sb.AppendLine("- work_kind is intentionally limited to orchestration, deterministic_shaping, or external_work; contract_role is intentionally limited to external_action, typed_data_producer, algorithmic_transform, deterministic_glue, orchestration, or abstract_policy. Do not request unsupported semantic subtypes such as external_read, llm_work, or analysis;");
        sb.AppendLine("- native main-orchestration steps are an exact locked set: human confirmation is permitted only when extraction_json `/main_native_steps` contains a required `human.input`; otherwise any confirmation in main is an invented behavioral overconstraint. Never infer an unlisted confirmation merely from the presence of an external write;");
        sb.AppendLine("- confirmation may guard the mutating workflow.call in main. When that call is reachable only from the submitted/confirmed branch, do not require a redundant confirmation boolean in the leaf contract; require one only when the leaf itself evaluates that value.");
        sb.AppendLine("- do not require an optional external action mentioned only in extractor prose unless it is required by the normalized request or a locked capability operation;");
        sb.AppendLine("- deterministic shaping may group, classify, select, or derive typed subsets from already materialized producer outputs such as records, paths, and manifests. Do not demand a new external inspection action merely to process those supplied values; require an external read only when information absent from every input must be fetched from external state.");
        sb.AppendLine("- distinguish a deterministic fallback policy from a separately requested runtime fallback action: the policy may reuse its governed operation's capability, while an AI, agent, service, or tool that must inspect, choose, analyze, or generate a new runtime value requires concrete external ownership. If compatible ownership already exists on another leaf, recommend moving the action and its typed boundary to that owner instead of inventing a capability or leaving external work as prose.");
        sb.AppendLine("- a cohesive locked external action may perform non-observable prerequisite inspection, selection, or preparation needed to execute that action when the declared schema and provider-neutral metadata support the context. Do not require a second external capability solely for such an internal prerequisite. Require a separate owner only when the request makes the prerequisite an independently observable effect or another workflow boundary consumes its typed result.");
        sb.AppendLine("- conditional activation decision_operation_id values refer to immutable local-operation ownership. The owning leaf must derive and expose that runtime decision unless the operation is explicitly owned by main; main may route a typed decision but must not claim to recompute an operation owned by a leaf.");
        sb.AppendLine("- review extraction contracts, not not-yet-generated step topology. A critical extraction diagnostic may require the declared decision owner, typed boundary, upstream operation dependencies, or parent routing to be represented, but must not require an exact switch expression, selector implementation, or intra-leaf step order that deterministic validation proves only after YAML generation.");
        sb.AppendLine("- cleanup obligations follow authoritative artifact_contract materializers. Do not assume every external leaf creates a directory, and do not demand created-directory outputs from capabilities whose artifact contracts materialize none.");
        sb.AppendLine("- when cleanup is required and a materializer has a documented caller-chosen creation target, main must register that exact target before invoking the materializer so partial failures remain cleanable, then reconcile the returned artifact after success without duplication. Do not demand pre-registration when no such documented input exists.");
        sb.AppendLine("- when extractor prose names a concrete discovered MCP method and selector, planned_tools must contain that literal discovered call or the prose must be removed.");
        sb.AppendLine("- capability names mentioned only inside a prohibition are not calls and must not appear in planned_tools.");
        sb.AppendLine("- only normalized_prompt defines generated-workflow behavior. Agent names, descriptions, persistence identifiers, and configuration supplied by the surrounding host are host metadata, not workflow inputs or obligations, unless normalized_prompt explicitly asks the generated workflow itself to use them.");
        sb.AppendLine("- classify every diagnostic kind as intent_ambiguity, plan_defect, capability_unavailable, or contract_violation. Use intent_ambiguity only when a user preference or design-time requirement is genuinely missing; never use it for malformed output, invalid identifiers, unavailable tools, schema violations, or decisions driven by future runtime results.");
        if (reviewBaseline?.QualityReview != null)
        {
            sb.AppendLine("- this is a delta review of a targeted patch. Apply the same rubric as the baseline review. Preserve diagnostics on unchanged extraction surfaces at their prior severity unless patch_addressed_diagnostic_codes names the exact baseline code and the patch changed structure. A named code is only a claim of resolution: report the diagnostic again when the defect remains. A new critical diagnostic must include extraction evidence pointing into a structurally changed leaf or the changed main orchestration; request-only or capability-only evidence cannot establish that a targeted patch regressed an unchanged surface.");
        }
        sb.AppendLine();
        AppendPromptSection(sb, "normalized_prompt", normalizedMarkdown);
        AppendPromptSection(sb, "mcp_context_json", BuildPipelineMcpContextJson(pipelineMcpContext).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "annotated_markdown", annotatedMarkdown);
        AppendPromptSection(sb, "extraction_json", BuildExtractionJson(extraction).ToJsonString(PromptJsonOptions));
        if (reviewBaseline is { QualityReview: { } baselineQualityReview })
        {
            AppendPromptSection(
                sb,
                "baseline_quality_review_json",
                BuildExtractionQualityReviewJson(baselineQualityReview)!.ToJsonString(PromptJsonOptions));
            AppendPromptSection(
                sb,
                "changed_extraction_surfaces_json",
                BuildPipelineExtractionChangedSurfacesJson(reviewBaseline, extraction).ToJsonString(PromptJsonOptions));
            AppendPromptSection(
                sb,
                "patch_addressed_diagnostic_codes",
                BuildStringArrayJson(addressedDiagnosticCodes?.ToArray() ?? Array.Empty<string>()).ToJsonString(PromptJsonOptions));
        }
        return sb.ToString();
    }

    private static JsonNode BuildExtractionQualityReviewStructuredOutputSchema() => JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["score", "verdict", "diagnostics", "retry_guidance"],
          "properties": {
            "score": { "type": "number", "minimum": 0, "maximum": 100 },
            "verdict": { "type": "string", "enum": ["pass", "retry"] },
            "retry_guidance": { "type": "string" },
            "diagnostics": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["code", "kind", "severity", "remediation_surface", "leaf_name", "message", "recommendation", "evidence"],
                "properties": {
                  "code": { "type": "string" },
                  "kind": { "type": "string", "enum": ["intent_ambiguity", "plan_defect", "capability_unavailable", "contract_violation"] },
                  "severity": { "type": "string", "enum": ["info", "warning", "critical"] },
                  "remediation_surface": { "type": "string", "enum": ["extraction_contract", "locked_ownership", "generated_workflow_topology"] },
                  "leaf_name": { "type": "string" },
                  "message": { "type": "string" },
                  "recommendation": { "type": "string" },
                  "evidence": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["source", "reference", "excerpt"],
                      "properties": {
                        "source": { "type": "string", "enum": ["request", "extraction", "capability_contract"] },
                        "reference": { "type": "string" },
                        "excerpt": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """)!;

    private static PipelineExtractionQualityReview NormalizeExtractionQualityReviewAgainstLockedContracts(
        string normalizedMarkdown,
        WorkflowPipelineExtraction extraction,
        PipelineMcpContext pipelineMcpContext,
        PipelineExtractionQualityReview review)
    {
        var specs = extraction.Subworkflows.ToDictionary(static spec => spec.Name, StringComparer.Ordinal);
        var lockedOccurrencePairs = extraction.Subworkflows
            .SelectMany(static spec => spec.PlannedTools)
            .Where(static tool => tool.Required && tool.OperationIds.Count == 1 && tool.CatalogIds.Count == 1)
            .Select(static tool => (OperationId: tool.OperationIds[0], CatalogId: tool.CatalogIds[0]))
            .ToArray();
        var lockedOccurrencePairsAreUnique = lockedOccurrencePairs
            .GroupBy(static pair => pair)
            .All(static group => group.Count() == 1);
        var correctedCritical = false;
        var diagnostics = review.Diagnostics.Select(diagnostic =>
        {
            if (string.Equals(
                    diagnostic.RemediationSurface,
                    "generated_workflow_topology",
                    StringComparison.Ordinal)
                || string.Equals(
                    diagnostic.RemediationSurface,
                    "locked_ownership",
                    StringComparison.Ordinal)
                && ExtractionHasAuthoritativeLockedOwnership(extraction))
            {
                correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                return diagnostic with
                {
                    Severity = "info",
                    Message = diagnostic.Message
                              + (string.Equals(
                                      diagnostic.RemediationSurface,
                                      "locked_ownership",
                                      StringComparison.Ordinal)
                                  ? " Deterministic capability composition and extraction validation own this immutable assignment."
                                  : " Generated-workflow topology is validated after leaf and main YAML generation."),
                    Recommendation = "Preserve the extraction contract and defer this check to its authoritative deterministic validation phase."
                };
            }

            var diagnosticText = string.Join(' ', new[]
            {
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Recommendation
            }.Where(static value => !string.IsNullOrWhiteSpace(value))!).ToLowerInvariant();
            var mentionsOccurrenceIdentity = diagnosticText.Contains("operation_id", StringComparison.Ordinal)
                                             || diagnosticText.Contains("operation id", StringComparison.Ordinal)
                                             || diagnosticText.Contains("catalog_id", StringComparison.Ordinal)
                                             || diagnosticText.Contains("catalog id", StringComparison.Ordinal)
                                             || diagnosticText.Contains("locked occurrence", StringComparison.Ordinal)
                                             || Regex.IsMatch(
                                                 diagnosticText,
                                                 @"\bop[_a-z0-9-]*::cap_[a-z0-9_-]+\b",
                                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var asksToAddOccurrenceIdentity = diagnosticText.Contains("omit", StringComparison.Ordinal)
                                              || diagnosticText.Contains("missing", StringComparison.Ordinal)
                                              || diagnosticText.Contains("add", StringComparison.Ordinal)
                                              || diagnosticText.Contains("include", StringComparison.Ordinal)
                                              || diagnosticText.Contains("copy", StringComparison.Ordinal)
                                              || diagnosticText.Contains("reflect", StringComparison.Ordinal);
            var challengesPlacement = diagnosticText.Contains("misassign", StringComparison.Ordinal)
                                      || diagnosticText.Contains("wrong leaf", StringComparison.Ordinal)
                                      || diagnosticText.Contains("wrong owner", StringComparison.Ordinal)
                                      || diagnosticText.Contains("incorrectly includes", StringComparison.Ordinal)
                                      || diagnosticText.Contains("move ", StringComparison.Ordinal)
                                      || diagnosticText.Contains("assign", StringComparison.Ordinal)
                                         && diagnosticText.Contains(" leaf", StringComparison.Ordinal)
                                      || Regex.IsMatch(
                                          diagnosticText,
                                          @"\b(?:remove|keep|reassign|relocate)\b[^.;\n]{0,120}\b(?:capabilit(?:y|ies)|calls?|operations?|writes?|reads?)\b[^.;\n]{0,100}\b(?:leaf|main|owner)\b|\b(?:belong|belongs|owned|ownership)\b[^.;\n]{0,120}\b(?:leaf|main|owner)\b|\b(?:before|after)\b[^.;\n]{0,80}\bconfirmation\b",
                                          RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var challengesValidatedOccurrenceIdentity = mentionsOccurrenceIdentity
                                                         && asksToAddOccurrenceIdentity
                                                         && !challengesPlacement
                                                         && !diagnosticText.Contains("duplicate", StringComparison.Ordinal);
            var misclassifiesComposedOperationAsDuplicate = lockedOccurrencePairsAreUnique
                                                            && mentionsOccurrenceIdentity
                                                            && diagnosticText.Contains("duplicate", StringComparison.Ordinal);
            if (challengesValidatedOccurrenceIdentity || misclassifiesComposedOperationAsDuplicate)
            {
                correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                return diagnostic with
                {
                    Severity = "info",
                    Message = diagnostic.Message + " Deterministic multiset validation already assigned every locked capability occurrence; additional technical calls intentionally remain unlocked.",
                    Recommendation = "Preserve the deterministic locked occurrence identities and do not duplicate them onto additional calls."
                };
            }

            if (ClaimsWorkspaceCleanupWithoutMaterializingCapability(
                    diagnostic,
                    diagnosticText,
                    extraction,
                    pipelineMcpContext))
            {
                correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                return diagnostic with
                {
                    Severity = "info",
                    Message = diagnostic.Message + " Authoritative artifact contracts show that the cited leaf does not materialize a workspace directory.",
                    Recommendation = "Accumulate and clean only directories returned by documented materializer capabilities."
                };
            }

            if (string.IsNullOrWhiteSpace(diagnostic.LeafName)
                || !specs.TryGetValue(diagnostic.LeafName, out var spec))
            {
                return diagnostic;
            }

            var demandsInventedExternalInspection = Regex.IsMatch(
                diagnosticText,
                @"\bmissing\b[^.;\n]{0,100}\bexternal\b[^.;\n]{0,100}\b(?:inspection|action|tool|read)\b|\b(?:add|require)\b[^.;\n]{0,120}\b(?:external|inspection)\b[^.;\n]{0,80}\b(?:action|tool|call|read)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var isLockedLocalShaping = (spec.LocalOperationIds?.Count ?? 0) > 0
                                       && spec.PlannedTools.Count == 0
                                       && (spec.PlannedNativeSteps?.Count ?? 0) == 0
                                       && string.Equals(spec.WorkKind, PipelineWorkKindDeterministicShaping, StringComparison.Ordinal)
                                       && spec.InputSchemas.Count > 0
                                       && !SpecMentionsExactDiscoveredCapability(spec, pipelineMcpContext)
                                       && !ContainsPositiveExternalInvocationIntent(BuildPipelineSpecIntentText(spec));
            if (demandsInventedExternalInspection && isLockedLocalShaping)
            {
                correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                return diagnostic with
                {
                    Severity = "info",
                    Message = diagnostic.Message + " Capability preflight locked this leaf as local processing over already materialized typed inputs; no concrete external invocation is present.",
                    Recommendation = "Preserve the locked local-processing operation and derive its typed output from the supplied producer values."
                };
            }

            if (diagnostic.Code.Contains("MISSING_PLANNED_TOOL", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Code.Contains("MISSING_NAMED_MCP", StringComparison.OrdinalIgnoreCase))
            {
                var claimedMethods = pipelineMcpContext.Servers
                    .Where(static server => server.Discovered)
                    .SelectMany(static server => server.Tools.Select(static tool => tool.Name)
                        .Concat(server.Prompts.Select(static prompt => prompt.Name)))
                    .Distinct(StringComparer.Ordinal)
                    .Where(method => ContainsIntentToken(diagnosticText, method))
                    .ToArray();
                var specClauses = SplitCapabilityMentionClauses(BuildPipelineSpecIntentText(spec));
                var hasClaimedInvocation = claimedMethods.Any(method => specClauses.Any(clause =>
                    ContainsIntentToken(clause, method)
                    && IsPositiveCapabilityInvocationClause(clause, method)));
                if (claimedMethods.Length > 0 && !hasClaimedInvocation)
                {
                    correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                    return diagnostic with
                    {
                        Severity = "info",
                        Message = diagnostic.Message + " Deterministic evidence validation found no invocation of the claimed method in the cited leaf.",
                        Recommendation = "Do not add a capability solely from an unsupported quality-review claim."
                    };
                }
            }

            var claimedContradictoryMethods = extraction.Subworkflows
                .SelectMany(static candidate => candidate.PlannedTools)
                .Where(static tool => tool.Required && (tool.OperationIds.Count > 0 || tool.CatalogIds.Count > 0))
                .Select(static tool => tool.Method)
                .Distinct(StringComparer.Ordinal)
                .Where(method => ContainsIntentToken(diagnostic.Message, method))
                .ToArray();
            var reportsContradictoryPlannedTool = diagnostic.Code.Contains(
                "CONTRADICTORY_PLANNED_TOOL",
                StringComparison.OrdinalIgnoreCase);
            if (reportsContradictoryPlannedTool
                && claimedContradictoryMethods.Length > 0
                && claimedContradictoryMethods.All(method => spec.PlannedTools.All(tool =>
                    !string.Equals(tool.Method, method, StringComparison.Ordinal))))
            {
                correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                return diagnostic with
                {
                    Severity = "info",
                    Message = diagnostic.Message + " Deterministic ownership validation shows that the claimed locked tool is not assigned to this leaf.",
                    Recommendation = "Preserve the deterministically assigned `(operation_id, catalog_id)` owner."
                };
            }
            if (challengesPlacement
                && HasCompleteConditionalCapabilityGroup(spec)
                && DiagnosticChallengesConditionalCapability(diagnosticText, spec))
            {
                correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
                return diagnostic with
                {
                    Severity = "info",
                    Message = diagnostic.Message + " The cited leaf owns one complete exactly-one conditional capability group; the main workflow supplies its decision value but cannot inline MCP calls.",
                    Recommendation = "Keep every conditional variant in this cohesive leaf and implement them as cases of one switch with a non-mutating default."
                };
            }
            var challengesRequiredLock = diagnosticText.Contains("optional", StringComparison.Ordinal)
                                         || diagnosticText.Contains("not required", StringComparison.Ordinal)
                                         || diagnosticText.Contains("remove", StringComparison.Ordinal)
                                         || diagnosticText.Contains("downgrade", StringComparison.Ordinal);
            if (!challengesRequiredLock || challengesPlacement)
                return diagnostic;

            var challengedLockedTool = spec.PlannedTools.Any(tool => tool.Required
                                                                     && (tool.OperationIds.Count > 0 || tool.CatalogIds.Count > 0)
                                                                     && ContainsIntentToken(diagnosticText, tool.Method));
            if (!challengedLockedTool)
                return diagnostic;

            correctedCritical |= string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal);
            return diagnostic with
            {
                Severity = "info",
                Message = diagnostic.Message + " The proposed downgrade is ignored because capability preflight locked this runtime operation as required.",
                Recommendation = "Preserve the locked call. Its control flow may remain conditional when the leaf contract requires that."
            };
        }).ToArray();

        var hasCritical = diagnostics.Any(static diagnostic => string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal));
        var score = correctedCritical && !hasCritical
            ? Math.Max(review.Score, PipelineExtractionQualityReviewThreshold)
            : review.Score;
        var verdict = correctedCritical && !hasCritical && score >= PipelineExtractionQualityReviewThreshold
            ? "pass"
            : review.Verdict;
        return QualifyPipelineExtractionQualityEvidence(
            normalizedMarkdown,
            extraction,
            pipelineMcpContext,
            review with { Score = score, Verdict = verdict, Diagnostics = diagnostics });
    }

    private static bool ExtractionHasAuthoritativeLockedOwnership(WorkflowPipelineExtraction extraction)
        => extraction.Subworkflows.Any(static spec =>
               spec.PlannedTools.Any(IsImmutablePipelinePlannedTool)
               || (spec.PlannedNativeSteps?.Count ?? 0) > 0
               || (spec.LocalOperationIds?.Count ?? 0) > 0)
           || (extraction.MainNativeSteps?.Count ?? 0) > 0
           || (extraction.MainLocalOperationIds?.Count ?? 0) > 0;

    private static bool HasCompleteConditionalCapabilityGroup(WorkflowPipelineSubworkflowSpec spec)
        => spec.PlannedTools
            .Where(static tool => tool.Required && tool.Activation != null)
            .GroupBy(static tool => tool.Activation!.Group, StringComparer.Ordinal)
            .Any(static group => group.Count() >= 2
                                 && group.All(static tool => string.Equals(
                                     tool.Activation!.Mode,
                                     "exactly_one",
                                     StringComparison.Ordinal))
                                 && group.Select(static tool => tool.Activation!.BranchValue)
                                     .Distinct(StringComparer.Ordinal)
                                     .Count() == group.Count());

    private static bool DiagnosticChallengesConditionalCapability(
        string diagnosticText,
        WorkflowPipelineSubworkflowSpec spec)
        => spec.PlannedTools
            .Where(static tool => tool.Required && tool.Activation != null)
            .Any(tool => ContainsIntentToken(diagnosticText, tool.Method)
                         || ContainsIntentToken(diagnosticText, tool.Activation!.Group)
                         || ContainsIntentToken(diagnosticText, tool.Activation.BranchValue));

    private static bool ClaimsWorkspaceCleanupWithoutMaterializingCapability(
        PipelineExtractionQualityDiagnostic diagnostic,
        string diagnosticText,
        WorkflowPipelineExtraction extraction,
        PipelineMcpContext pipelineMcpContext)
    {
        if (!Regex.IsMatch(
                diagnosticText,
                @"\b(?:cleanup|clean\s+up|director(?:y|ies))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(diagnostic.LeafName))
            return false;

        var citedSpec = extraction.Subworkflows.FirstOrDefault(spec => string.Equals(
            spec.Name,
            diagnostic.LeafName,
            StringComparison.Ordinal));
        return citedSpec != null
               && SpecHasAuthoritativeArtifactContract(citedSpec, pipelineMcpContext)
               && !SpecMaterializesWorkspaceDirectory(citedSpec, pipelineMcpContext);
    }

    private static bool SpecHasAuthoritativeArtifactContract(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
        => spec.PlannedTools
            .Where(static tool => string.Equals(tool.Kind, "tool", StringComparison.Ordinal))
            .Any(planned =>
            {
                var server = pipelineMcpContext.Servers.FirstOrDefault(candidate => string.Equals(
                    candidate.Name,
                    planned.Server,
                    StringComparison.Ordinal));
                var tool = server?.Tools.FirstOrDefault(candidate => string.Equals(
                    candidate.Name,
                    planned.Method,
                    StringComparison.Ordinal));
                return tool != null && GetValidatedMcpArtifactContract(tool, server!.Name) != null;
            });

    private static bool SpecMaterializesWorkspaceDirectory(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
    {
        foreach (var planned in spec.PlannedTools.Where(static tool => string.Equals(tool.Kind, "tool", StringComparison.Ordinal)))
        {
            var server = pipelineMcpContext.Servers.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, planned.Server, StringComparison.Ordinal));
            var tool = server?.Tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, planned.Method, StringComparison.Ordinal));
            var contract = tool == null ? null : GetValidatedMcpArtifactContract(tool, server!.Name);
            if (contract?.Produces.Any(static artifact =>
                    string.Equals(artifact.Kind, McpArtifactContractConventions.WorkspaceDirectoryKind, StringComparison.Ordinal)
                    && string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal)) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static PipelineExtractionQualityReview ParseExtractionQualityReviewResponse(LLMResponse response)
    {
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
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"review_extraction_quality returned invalid JSON: {ex.Message}", inner: ex);
            }
        }

        if (root is not JsonObject obj)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "review_extraction_quality response must be structured JSON.");

        var score = Math.Clamp(GetRequiredIntegerProperty(obj, "score", "review_extraction_quality"), 0, 100);
        var verdict = NormalizeExtractionQualityVerdict(GetStringProperty(obj, "verdict"));
        var retryGuidance = GetStringProperty(obj, "retry_guidance") ?? "";
        var diagnostics = ParseExtractionQualityDiagnostics(obj["diagnostics"] as JsonArray);

        return new PipelineExtractionQualityReview(score, verdict, diagnostics, retryGuidance);
    }

    private static IReadOnlyList<PipelineExtractionQualityDiagnostic> ParseExtractionQualityDiagnostics(JsonArray? diagnostics)
    {
        if (diagnostics == null)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "review_extraction_quality response must include diagnostics array.");

        var parsed = new List<PipelineExtractionQualityDiagnostic>();
        for (var i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i] is not JsonObject diagnostic)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"review_extraction_quality diagnostic at index {i} must be an object.");

            var code = GetStringProperty(diagnostic, "code");
            if (string.IsNullOrWhiteSpace(code))
                code = "PIPELINE_EXTRACTION_QUALITY_DIAGNOSTIC";

            var severity = NormalizeExtractionQualitySeverity(GetStringProperty(diagnostic, "severity"));
            var kind = NormalizeExtractionQualityDiagnosticKind(GetStringProperty(diagnostic, "kind"));
            var remediationSurface = NormalizeExtractionQualityRemediationSurface(
                GetStringProperty(diagnostic, "remediation_surface"));
            var leafName = GetStringProperty(diagnostic, "leaf_name");
            if (string.IsNullOrWhiteSpace(leafName))
                leafName = null;
            var message = GetStringProperty(diagnostic, "message");
            if (string.IsNullOrWhiteSpace(message))
                message = "Extraction quality review diagnostic.";
            var recommendation = GetStringProperty(diagnostic, "recommendation");
            var evidence = ParseExtractionQualityEvidence(diagnostic["evidence"] as JsonArray);

            parsed.Add(new PipelineExtractionQualityDiagnostic(
                code,
                kind,
                severity,
                leafName,
                message,
                string.IsNullOrWhiteSpace(recommendation) ? null : recommendation,
                evidence,
                RemediationSurface: remediationSurface));
        }

        return parsed;
    }

    private static string NormalizeExtractionQualityRemediationSurface(string? surface)
    {
        var normalized = surface?.Trim().ToLowerInvariant();
        return normalized switch
        {
            null or "" or "extraction_contract" => "extraction_contract",
            "locked_ownership" => "locked_ownership",
            "generated_workflow_topology" => "generated_workflow_topology",
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"review_extraction_quality remediation_surface '{surface}' is invalid.")
        };
    }

    private static int GetRequiredIntegerProperty(JsonObject obj, string propertyName, string phase)
    {
        if (obj[propertyName] is not JsonValue value)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"{phase} response must include numeric '{propertyName}'.");

        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        if (value.TryGetValue<double>(out var doubleValue))
            return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
        if (value.TryGetValue<string>(out var stringValue) && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"{phase} response property '{propertyName}' must be numeric.");
    }

    private static string NormalizeExtractionQualityVerdict(string? verdict)
    {
        var normalized = verdict?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pass" => "pass",
            "retry" => "retry",
            _ => throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"review_extraction_quality verdict '{verdict}' is invalid.")
        };
    }

    private static string NormalizeExtractionQualitySeverity(string? severity)
    {
        var normalized = severity?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "critical" or "error" or "fail" or "failure" => "critical",
            "warning" or "warn" => "warning",
            "info" or "information" or "" or null => "info",
            _ => "warning"
        };
    }

    private static bool ShouldRetryPipelineExtractionReview(PipelineExtractionQualityReview? review)
        => review != null
           && review.Diagnostics.Any(static diagnostic => diagnostic.EvidenceQualified
                                                               && string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal));

    private static IReadOnlyList<string> BuildExtractionQualityReviewRetryFeedback(PipelineExtractionQualityReview? review)
    {
        if (review == null)
            return Array.Empty<string>();

        var feedback = new List<string>();

        foreach (var diagnostic in review.Diagnostics.Where(static diagnostic =>
                     diagnostic.EvidenceQualified
                     && string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal)))
        {
            var leafText = string.IsNullOrWhiteSpace(diagnostic.LeafName) ? "" : $" leaf '{diagnostic.LeafName}':";
            var recommendation = string.IsNullOrWhiteSpace(diagnostic.Recommendation)
                ? ""
                : $" Recommendation: {diagnostic.Recommendation}";
            feedback.Add($"{diagnostic.Code} ({diagnostic.Severity}){leafText} {diagnostic.Message}{recommendation}");
        }

        return feedback;
    }

    private static WorkflowRuntimeException BuildPipelineExtractionQualityReviewException(
        WorkflowPipelineExtraction extraction,
        string annotatedMarkdown,
        PipelineMcpContext pipelineMcpContext)
    {
        var feedback = BuildExtractionQualityReviewRetryFeedback(extraction.QualityReview);
        var rootCauses = BuildExtractionQualityReviewRootCauses(extraction.QualityReview);
        var lockedCapabilityOwnership = BuildLockedCapabilityOwnershipSummary(extraction);
        var details = new JsonObject
        {
            ["planning_outcome"] = "cannot_plan_safely",
            ["recommended_action"] = "clarify_or_abandon",
            ["quality_review"] = BuildExtractionQualityReviewJson(extraction.QualityReview),
            ["validation"] = BuildValidationJson(feedback),
            ["root_causes"] = BuildPipelineRootCausesJson(rootCauses),
            ["locked_capability_ownership"] = lockedCapabilityOwnership,
            ["pipeline_inspection"] = new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["root_cause_count"] = rootCauses.Count,
                    ["validation_error_count"] = feedback.Count,
                    ["extraction_quality_score"] = extraction.QualityReview?.Score
                },
                ["mcp_context"] = BuildPipelineMcpContextJson(pipelineMcpContext),
                ["annotated_markdown"] = annotatedMarkdown,
                ["quality_review"] = BuildExtractionQualityReviewJson(extraction.QualityReview),
                ["root_causes"] = BuildPipelineRootCausesJson(rootCauses)
            },
            ["invalid_annotated_markdown"] = annotatedMarkdown
        };

        return new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "workflow.plan pipeline extraction quality review failed: " + string.Join("; ", feedback)
            + (lockedCapabilityOwnership.Count == 0
                ? string.Empty
                : "; Locked capability ownership: " + string.Join(", ", lockedCapabilityOwnership
                    .OfType<JsonObject>()
                    .Select(static item => $"{item["leaf"]?.GetValue<string>()}={item["server"]?.GetValue<string>()}/{item["method"]?.GetValue<string>()} [{item["operation_id"]?.GetValue<string>()}::{item["catalog_id"]?.GetValue<string>()}]"))),
            details: details);
    }

    private static JsonArray BuildLockedCapabilityOwnershipSummary(WorkflowPipelineExtraction extraction)
        => new(extraction.Subworkflows
            .SelectMany(spec => spec.PlannedTools
                .Where(static tool => tool.Required && tool.OperationIds.Count > 0)
                .SelectMany(tool => tool.OperationIds.SelectMany(operationId =>
                    (tool.CatalogIds.Count == 0 ? new string?[] { null } : tool.CatalogIds.Cast<string?>())
                    .Select(catalogId => (JsonNode)new JsonObject
                    {
                        ["leaf"] = spec.Name,
                        ["server"] = tool.Server,
                        ["method"] = tool.Method,
                        ["operation_id"] = operationId,
                        ["catalog_id"] = catalogId
                    }))))
            .Take(64)
            .ToArray());

    private static IReadOnlyList<PipelineRootCause> BuildExtractionQualityReviewRootCauses(PipelineExtractionQualityReview? review)
    {
        var rootCauses = new List<PipelineRootCause>();
        if (review == null)
            return rootCauses;

        foreach (var diagnostic in review.Diagnostics)
        {
            if (!diagnostic.EvidenceQualified
                || !string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal))
                continue;

            var message = string.IsNullOrWhiteSpace(diagnostic.Recommendation)
                ? diagnostic.Message
                : diagnostic.Message + " Recommendation: " + diagnostic.Recommendation;
            AddPipelineRootCause(
                rootCauses,
                "extraction_quality_judge",
                "review_extraction_quality",
                diagnostic.LeafName,
                outputName: null,
                invalidPath: string.IsNullOrWhiteSpace(diagnostic.LeafName) ? null : $"subworkflows.{diagnostic.LeafName}",
                code: diagnostic.Code,
                message,
                primary: string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal));
        }

        if (rootCauses.Count == 0 && ShouldRetryPipelineExtractionReview(review))
        {
            AddPipelineRootCause(
                rootCauses,
                "extraction_quality_judge",
                "review_extraction_quality",
                leafName: null,
                outputName: null,
                invalidPath: null,
                code: "PIPELINE_EXTRACTION_QUALITY_REVIEW_RETRY",
                $"Extraction quality review scored {review.Score}/100 with verdict '{review.Verdict}'.",
                primary: true);
        }

        return rootCauses;
    }

    private static IReadOnlyList<string> AppendPipelineExtractionQualityWarning(
        WorkflowPipelineExtraction extraction,
        string warning)
    {
        var warnings = extraction.QualityWarnings == null
            ? new List<string>()
            : extraction.QualityWarnings.ToList();
        warnings.Add(warning);
        return warnings;
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
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unspecified"),
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
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                UseBackgroundMode = true
            }, "workflow.plan.pipeline.extraction", ct);

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
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unspecified"),
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
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = structuredOutputSchema,
                StructuredOutputStrict = true
            }, $"workflow.plan.pipeline.{phase}", ct);

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

    private static JsonNode BuildMarkExtractableBlocksStructuredOutputSchema()
        => BuildMarkExtractableBlocksStructuredOutputSchemaForCapabilities(CapabilityPreflightResult.Off);

    private static JsonNode BuildMarkExtractableBlocksStructuredOutputSchemaForCapabilities(
        CapabilityPreflightResult capabilityPreflight)
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["annotated_markdown", "subworkflows", "main_orchestration"],
          "$defs": {
            "contract_field": {
              "type": "object",
              "additionalProperties": false,
              "required": ["name", "type", "description", "required", "nullable", "item_type", "properties", "enum_values"],
              "properties": {
                "name": { "type": "string" },
                "type": { "type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"] },
                "description": { "type": "string" },
                "required": { "type": "boolean" },
                "nullable": { "type": "boolean" },
                "item_type": { "type": "string" },
                "enum_values": { "type": "array", "items": { "type": "string" } },
                "properties": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/contract_field" }
                }
              }
            }
          },
          "properties": {
            "annotated_markdown": { "type": "string" },
            "main_orchestration": { "type": "string" },
            "subworkflows": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "goal", "description", "work_kind", "contract_role", "concrete_outcome", "owned_operation_ids", "inputs", "outputs", "extract_reason", "content", "planned_tools"],
                "properties": {
                  "name": { "type": "string" },
                  "goal": { "type": "string" },
                  "description": { "type": "string" },
                  "work_kind": { "type": "string", "enum": ["orchestration", "deterministic_shaping", "external_work"] },
                  "contract_role": { "type": "string", "enum": ["external_action", "typed_data_producer", "algorithmic_transform", "deterministic_glue", "orchestration", "abstract_policy"] },
                  "concrete_outcome": { "type": "string" },
                  "owned_operation_ids": { "type": "array", "items": { "type": "string" } },
                  "inputs": {
                    "type": "array",
                    "items": { "$ref": "#/$defs/contract_field" }
                  },
                  "outputs": {
                    "type": "array",
                    "items": { "$ref": "#/$defs/contract_field" }
                  },
                  "extract_reason": { "type": "string" },
                  "content": { "type": "string" },
                  "planned_tools": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["server", "kind", "method", "operation_ids", "catalog_ids", "request_bindings", "required", "purpose", "consumes", "produces"],
                      "properties": {
                        "server": { "type": "string" },
                        "kind": { "type": "string", "enum": ["tool", "prompt"] },
                        "method": { "type": "string" },
                        "operation_ids": { "type": "array", "items": { "type": "string" } },
                        "catalog_ids": { "type": "array", "items": { "type": "string" } },
                        "request_bindings": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["path", "value"],
                            "properties": {
                              "path": { "type": "string" },
                              "value": { "type": ["string", "number", "boolean", "null"] }
                            }
                          }
                        },
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
        var operationIds = capabilityPreflight.Capabilities
            .Where(static capability => capability.Required && !IsMainOrchestrationNativeCapability(capability))
            .SelectMany(GetResolvedCapabilityOperationIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(static operationId => (JsonNode?)JsonValue.Create(operationId))
            .ToArray();
        if (operationIds.Length > 0)
        {
            schema["properties"]!["subworkflows"]!["items"]!["properties"]!
                ["owned_operation_ids"]!["items"]!["enum"] = new JsonArray(operationIds);
        }
        return schema;
    }

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

    private static string NormalizeExtractionQualityDiagnosticKind(string? kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "intent_ambiguity" => "intent_ambiguity",
            "plan_defect" => "plan_defect",
            "capability_unavailable" => "capability_unavailable",
            "contract_violation" => "contract_violation",
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"review_extraction_quality diagnostic kind '{kind}' is invalid.")
        };
    }

    private static string NormalizeAnnotatedMarkdownContractSections(
        string annotatedMarkdown,
        StructuredPipelineExtractionMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(annotatedMarkdown) || metadata.Subworkflows.Count == 0)
            return annotatedMarkdown;

        var normalized = annotatedMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return SubworkflowBlockRegex().Replace(normalized, match =>
        {
            var name = match.Groups["name"].Value;
            if (!metadata.Subworkflows.TryGetValue(name, out var contract))
                return match.Value;

            var lines = match.Groups["body"].Value.Split('\n').ToList();
            var inputsIndex = lines.FindIndex(static line => string.Equals(line.Trim(), "inputs:", StringComparison.Ordinal));
            var outputsIndex = lines.FindIndex(static line => string.Equals(line.Trim(), "outputs:", StringComparison.Ordinal));
            var extractReasonIndex = lines.FindIndex(static line => line.TrimStart().StartsWith("extract_reason:", StringComparison.Ordinal));
            if (inputsIndex < 0 || outputsIndex <= inputsIndex || extractReasonIndex <= outputsIndex)
                return match.Value;

            var rebuilt = new List<string>(lines.Count);
            rebuilt.AddRange(lines.Take(inputsIndex + 1));
            AddAnnotatedContractProjection(rebuilt, contract.Inputs);
            rebuilt.Add(lines[outputsIndex]);
            AddAnnotatedContractProjection(rebuilt, contract.Outputs);
            rebuilt.AddRange(lines.Skip(extractReasonIndex));
            return $":::subworkflow name=\"{name}\"\n{string.Join('\n', rebuilt).TrimEnd()}\n:::";
        });
    }

    private static void AddAnnotatedContractProjection(
        List<string> lines,
        IReadOnlyDictionary<string, JsonNode?> fields)
    {
        foreach (var (name, schema) in fields)
        {
            var type = schema is JsonObject schemaObject
                ? NormalizeWorkflowSchemaType(GetStringProperty(schemaObject, "type") ?? "any")
                : "any";
            lines.Add($"  {name}: {type}");
        }
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
            NormalizePipelineContractRole(GetStringProperty(subworkflow, "contract_role")),
            GetStringProperty(subworkflow, "concrete_outcome"),
            GetStringArray(subworkflow["owned_operation_ids"] as JsonArray),
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

        if (field["nullable"] is JsonValue nullableValue
            && nullableValue.TryGetValue<bool>(out var nullable)
            && nullable)
        {
            schema["nullable"] = true;
        }

        if (field["enum_values"] is JsonArray enumValues && enumValues.Count > 0)
        {
            var values = enumValues
                .OfType<JsonValue>()
                .Select(static value => value.TryGetValue<string>(out var text)
                    ? text
                    : null)
                .Where(static value => value != null)
                .Cast<string>()
                .ToArray();
            if (!string.Equals(type, "string", StringComparison.Ordinal)
                || values.Length != enumValues.Count
                || values.Any(string.IsNullOrWhiteSpace)
                || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            {
                throw new InvalidOperationException(
                    "Structured contract enum_values must contain unique non-empty strings and may be used only with type string.");
            }

            schema["enum"] = new JsonArray(values
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
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
            else if (string.Equals(type, "dictionary", StringComparison.Ordinal))
            {
                var itemType = NormalizeWorkflowSchemaType(GetStringProperty(field, "item_type") ?? "object");
                var additionalProperties = new JsonObject
                {
                    ["type"] = string.Equals(itemType, "any", StringComparison.Ordinal) ? "object" : itemType
                };
                if (string.Equals(additionalProperties["type"]?.GetValue<string>(), "object", StringComparison.Ordinal))
                    AddStructuredObjectProperties(additionalProperties, properties);
                schema["additional_properties"] = additionalProperties;
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
        else if (string.Equals(type, "dictionary", StringComparison.Ordinal))
        {
            var itemType = NormalizeWorkflowSchemaType(GetStringProperty(field, "item_type") ?? "any");
            if (!string.Equals(itemType, "any", StringComparison.Ordinal))
            {
                schema["additional_properties"] = new JsonObject
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

            var schema = BuildStructuredNestedPropertySchema(property);

            var required = property["required"] is JsonValue requiredValue
                           && requiredValue.TryGetValue<bool>(out var requiredBool)
                           && requiredBool;
            result.Add((name, schema, required));
        }

        return result;
    }

    private static StructuredPipelineExtractionMetadata PreservePreviouslyValidatedStructuredContracts(
        StructuredPipelineExtractionMetadata current,
        StructuredPipelineExtractionMetadata previous)
    {
        if (!current.IsStructuredResponse || !previous.IsStructuredResponse)
            return current;

        var subworkflows = current.Subworkflows.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, currentSpec) in current.Subworkflows)
        {
            if (!previous.Subworkflows.TryGetValue(name, out var previousSpec))
                continue;

            subworkflows[name] = currentSpec with
            {
                Inputs = PreservePreviouslyValidatedContractSchemas(currentSpec.Inputs, previousSpec.Inputs),
                Outputs = PreservePreviouslyValidatedContractSchemas(currentSpec.Outputs, previousSpec.Outputs)
            };
        }

        return current with { Subworkflows = subworkflows };
    }

    private static IReadOnlyDictionary<string, JsonNode?> PreservePreviouslyValidatedContractSchemas(
        IReadOnlyDictionary<string, JsonNode?> current,
        IReadOnlyDictionary<string, JsonNode?> previous)
    {
        var preserved = current.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value?.DeepClone(),
            StringComparer.Ordinal);

        foreach (var (name, currentSchema) in current)
        {
            if (!previous.TryGetValue(name, out var previousSchema)
                || IsWeakExtractionContractSchema(previousSchema)
                || previousSchema is not JsonObject previousObject)
            {
                continue;
            }

            JsonObject replacement;
            if (IsWeakExtractionContractSchema(currentSchema))
            {
                replacement = (JsonObject)previousObject.DeepClone();
                if (currentSchema is JsonObject currentObject)
                {
                    PreserveCurrentContractAnnotation(currentObject, replacement, "description");
                    PreserveCurrentContractAnnotation(currentObject, replacement, "required");
                    PreserveCurrentContractAnnotation(currentObject, replacement, "nullable");
                }
            }
            else if (currentSchema is JsonObject currentObject)
            {
                replacement = (JsonObject)currentObject.DeepClone();
                PreservePreviouslyValidatedSchemaMembers(replacement, previousObject);
            }
            else
            {
                continue;
            }

            preserved[name] = replacement;
        }

        return preserved;
    }

    private static void PreservePreviouslyValidatedSchemaMembers(JsonObject current, JsonObject previous)
    {
        var currentType = GetStringProperty(current, "type");
        var previousType = GetStringProperty(previous, "type");
        if (!string.Equals(currentType, previousType, StringComparison.Ordinal))
            return;

        if (previous["enum"] is JsonArray previousEnum
            && current["enum"] is not JsonArray)
        {
            current["enum"] = previousEnum.DeepClone();
        }

        if (previous["properties"] is JsonObject previousProperties
            && current["properties"] is JsonObject currentProperties)
        {
            foreach (var (propertyName, previousProperty) in previousProperties)
            {
                if (!currentProperties.TryGetPropertyValue(propertyName, out var currentProperty))
                {
                    currentProperties[propertyName] = previousProperty?.DeepClone();
                    continue;
                }

                if (currentProperty is JsonObject currentPropertyObject
                    && previousProperty is JsonObject previousPropertyObject)
                {
                    PreservePreviouslyValidatedSchemaMembers(currentPropertyObject, previousPropertyObject);
                }
            }

            PreservePreviouslyValidatedRequiredProperties(current, previous, currentProperties);
        }

        PreservePreviouslyValidatedNestedSchema(current, previous, "items");
        PreservePreviouslyValidatedNestedSchema(current, previous, "additional_properties");
    }

    private static void PreservePreviouslyValidatedNestedSchema(
        JsonObject current,
        JsonObject previous,
        string propertyName)
    {
        if (current[propertyName] is JsonObject currentNested
            && previous[propertyName] is JsonObject previousNested)
        {
            PreservePreviouslyValidatedSchemaMembers(currentNested, previousNested);
        }
    }

    private static void PreservePreviouslyValidatedRequiredProperties(
        JsonObject current,
        JsonObject previous,
        JsonObject currentProperties)
    {
        if (previous["required_properties"] is not JsonArray previousRequired)
            return;

        var currentRequired = current["required_properties"] as JsonArray ?? new JsonArray();
        var requiredNames = currentRequired
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var name) ? name : null)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var node in previousRequired.OfType<JsonValue>())
        {
            if (!node.TryGetValue<string>(out var name)
                || string.IsNullOrWhiteSpace(name)
                || !currentProperties.ContainsKey(name)
                || !requiredNames.Add(name))
            {
                continue;
            }

            currentRequired.Add((JsonNode)JsonValue.Create(name)!);
        }

        if (currentRequired.Count > 0)
            current["required_properties"] = currentRequired;
    }

    private static bool IsWeakExtractionContractSchema(JsonNode? schema)
        => EnumerateWeakExtractionSchemaDiagnostics(schema, "contract", isInput: false).Any();

    private static void PreserveCurrentContractAnnotation(JsonObject current, JsonObject replacement, string propertyName)
    {
        if (current.TryGetPropertyValue(propertyName, out var value))
            replacement[propertyName] = value?.DeepClone();
    }

    private static JsonObject BuildStructuredNestedPropertySchema(JsonObject property)
    {
        var type = NormalizeWorkflowSchemaType(GetStringProperty(property, "type") ?? "any");
        var schema = new JsonObject
        {
            ["type"] = type
        };
        if (GetStringProperty(property, "description") is { } description)
            schema["description"] = description;
        if (property["nullable"] is JsonValue nullableValue
            && nullableValue.TryGetValue<bool>(out var nullable)
            && nullable)
        {
            schema["nullable"] = true;
        }

        var nestedProperties = BuildStructuredFieldProperties(property["properties"] as JsonArray);
        if (nestedProperties.Count > 0)
        {
            if (string.Equals(type, "array", StringComparison.Ordinal))
            {
                var itemType = NormalizeWorkflowSchemaType(GetStringProperty(property, "item_type") ?? "object");
                var items = new JsonObject
                {
                    ["type"] = string.Equals(itemType, "any", StringComparison.Ordinal) ? "object" : itemType
                };
                if (string.Equals(items["type"]?.GetValue<string>(), "object", StringComparison.Ordinal))
                    AddStructuredObjectProperties(items, nestedProperties);
                schema["items"] = items;
            }
            else if (string.Equals(type, "dictionary", StringComparison.Ordinal))
            {
                var itemType = NormalizeWorkflowSchemaType(GetStringProperty(property, "item_type") ?? "object");
                var additionalProperties = new JsonObject
                {
                    ["type"] = string.Equals(itemType, "any", StringComparison.Ordinal) ? "object" : itemType
                };
                if (string.Equals(additionalProperties["type"]?.GetValue<string>(), "object", StringComparison.Ordinal))
                    AddStructuredObjectProperties(additionalProperties, nestedProperties);
                schema["additional_properties"] = additionalProperties;
            }
            else
            {
                AddStructuredObjectProperties(schema, nestedProperties);
            }
        }
        else if (string.Equals(type, "array", StringComparison.Ordinal))
        {
            var itemType = NormalizeWorkflowSchemaType(GetStringProperty(property, "item_type") ?? "any");
            if (!string.Equals(itemType, "any", StringComparison.Ordinal))
                schema["items"] = new JsonObject { ["type"] = itemType };
        }
        else if (string.Equals(type, "dictionary", StringComparison.Ordinal))
        {
            var itemType = NormalizeWorkflowSchemaType(GetStringProperty(property, "item_type") ?? "any");
            if (!string.Equals(itemType, "any", StringComparison.Ordinal))
                schema["additional_properties"] = new JsonObject { ["type"] = itemType };
        }

        return schema;
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
            IReadOnlyList<IReadOnlyList<CapabilityRequestBinding>> requestBindingVariants;
            try
            {
                requestBindingVariants = ParseStructuredRequestBindingVariants(
                    tool["request_bindings"] as JsonArray,
                    $"Structured subworkflow '{subworkflowName}' planned tool '{server}/{method}'");
            }
            catch (WorkflowRuntimeException ex)
            {
                validationErrors.Add(ex.Message);
                requestBindingVariants = [Array.Empty<CapabilityRequestBinding>()];
            }
            var required = tool["required"] is JsonValue requiredValue
                           && requiredValue.TryGetValue<bool>(out var requiredBool)
                           && requiredBool;
            var operationIds = GetStringArray(tool["operation_ids"] as JsonArray);
            var catalogIds = GetStringArray(tool["catalog_ids"] as JsonArray);
            if (!required
                && string.IsNullOrWhiteSpace(server)
                && string.IsNullOrWhiteSpace(method)
                && operationIds.Count == 0
                && catalogIds.Count == 0
                && requestBindingVariants.All(static variant => variant.Count == 0))
            {
                continue;
            }

            foreach (var requestBindings in requestBindingVariants)
            {
                result.Add(new PipelinePlannedTool(
                    server,
                    kind,
                    method,
                    required,
                    GetStringProperty(tool, "purpose"),
                    GetStringArray(tool["consumes"] as JsonArray),
                    GetStringArray(tool["produces"] as JsonArray),
                    requestBindings,
                    requestBindingVariants.Count == 1 ? operationIds : Array.Empty<string>(),
                    requestBindingVariants.Count == 1 ? catalogIds : Array.Empty<string>()));
            }
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<CapabilityRequestBinding>> ParseStructuredRequestBindingVariants(
        JsonArray? nodes,
        string owner)
    {
        if (nodes == null || nodes.Count == 0)
            return [Array.Empty<CapabilityRequestBinding>()];

        var parsed = new List<CapabilityRequestBinding>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not JsonObject binding)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} contains an invalid request binding.");
            var path = binding["path"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !IsValidJsonPointer(path))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} request binding paths must be RFC 6901 JSON Pointers.");
            if (!binding.ContainsKey("value") || !IsJsonScalar(binding["value"]))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} request binding '{path}' must contain a JSON scalar value.");
            parsed.Add(new CapabilityRequestBinding(path, binding["value"]?.DeepClone()));
        }

        var groups = parsed
            .GroupBy(static binding => binding.Path, StringComparer.Ordinal)
            .Select(group => new
            {
                Path = group.Key,
                Values = group
                    .DistinctBy(static binding => binding.Value?.ToJsonString() ?? "null", StringComparer.Ordinal)
                    .ToArray()
            })
            .ToArray();
        var conflicting = groups.Where(static group => group.Values.Length > 1).ToArray();
        if (conflicting.Length > 1)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"{owner} contains conflicting values on multiple request binding paths; separate the exact selector combinations into individual planned_tools entries.");
        }

        if (conflicting.Length == 0)
            return [groups.Select(static group => group.Values[0]).ToArray()];

        var varying = conflicting[0];
        return varying.Values.Select(selected => (IReadOnlyList<CapabilityRequestBinding>)groups
            .Select(group => string.Equals(group.Path, varying.Path, StringComparison.Ordinal)
                ? selected
                : group.Values[0])
            .ToArray()).ToArray();
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
        var rootCauses = extraction.RootCauses.ToList();
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

            var inputSchemas = PruneOptionalWeakContractMembers(
                MergeStructuredSchemas(spec.InputSchemas, structured?.Inputs));
            var outputSchemas = PruneOptionalWeakContractMembers(
                MergeStructuredSchemas(spec.OutputSchemas, structured?.Outputs));
            var plannedTools = NormalizeAdvisoryPlannedToolRequestBindings(
                structured?.PlannedTools ?? Array.Empty<PipelinePlannedTool>(),
                pipelineMcpContext);
            var workKind = NormalizePipelineWorkKind(structured?.WorkKind) ?? InferPipelineWorkKind(spec);
            var contractRole = NormalizePipelineContractRole(structured?.ContractRole);
            var structuredIntent = string.Join(' ', new[]
                {
                    spec.Goal,
                    spec.ExtractReason,
                    spec.Content,
                    structured?.Description,
                    structured?.ConcreteOutcome
                }.Where(static value => !string.IsNullOrWhiteSpace(value))!);
            if (DeclaresNoExternalCalls(structuredIntent))
            {
                // A leaf that explicitly declares local-only processing cannot also own an MCP
                // call. Locked external capabilities are composed into another compatible leaf.
                plannedTools = Array.Empty<PipelinePlannedTool>();
                workKind = PipelineWorkKindDeterministicShaping;
                contractRole = PipelineContractRoleAlgorithmicTransform;
            }
            else
            {
                plannedTools = RemoveToolsMentionedOnlyAsProhibitions(spec, structured, plannedTools);
                if (ContainsExternalExecutionOrStateInspectionIntent(structuredIntent))
                {
                    // Executing tests, commands, dependency restoration, or repository
                    // inspection remains external work even when the extractor labels
                    // the result-shaping portion as deterministic.
                    workKind = PipelineWorkKindExternalWork;
                    contractRole = PipelineContractRoleExternalAction;
                }
            }
            ValidatePlannedToolsAgainstMcpContext(spec.Name, plannedTools, pipelineMcpContext, validationErrors);

            var enrichedSpec = spec with
            {
                WorkKind = workKind,
                Description = structured?.Description,
                ContractRole = contractRole,
                ConcreteOutcome = structured?.ConcreteOutcome,
                OwnedOperationIds = structured?.OwnedOperationIds ?? Array.Empty<string>(),
                InputSchemas = inputSchemas,
                OutputSchemas = outputSchemas,
                PlannedTools = plannedTools,
                GenerationPrompt = spec.GenerationPrompt
            };

            if (string.IsNullOrWhiteSpace(enrichedSpec.ContractRole))
            {
                var inferredContractRole = InferPipelineContractRole(enrichedSpec);
                enrichedSpec = enrichedSpec with
                {
                    ContractRole = inferredContractRole
                };
            }

            if (metadata.IsStructuredResponse)
                enrichedSpec = ApplyRequiredLeafToolContracts(enrichedSpec, pipelineMcpContext, validationErrors, rootCauses);
            enrichedSpec = enrichedSpec with { GenerationPrompt = BuildSubworkflowGenerationPrompt(enrichedSpec) };

            if (metadata.IsStructuredResponse)
            {
                var score = ScorePipelineExtractionSpec(enrichedSpec, pipelineMcpContext);
                enrichedSpec = enrichedSpec with { ExtractionScore = score };
                ValidatePipelineExtractionQuality(enrichedSpec, pipelineMcpContext, validationErrors, rootCauses);
            }
            enriched.Add(enrichedSpec);
        }

        return extraction with
        {
            Subworkflows = enriched,
            ValidationErrors = validationErrors,
            RootCauses = rootCauses
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

    private static IReadOnlyDictionary<string, JsonNode?> PruneOptionalWeakContractMembers(
        IReadOnlyDictionary<string, JsonNode?> schemas)
        => schemas.ToDictionary(
            static pair => pair.Key,
            static pair => PruneOptionalWeakContractMembers(pair.Value),
            StringComparer.Ordinal);

    private static JsonNode? PruneOptionalWeakContractMembers(JsonNode? schema)
    {
        if (schema is not JsonObject source)
            return schema?.DeepClone();

        var normalized = (JsonObject)source.DeepClone();
        foreach (var unionName in new[] { "anyOf", "oneOf" })
        {
            if (normalized[unionName] is not JsonArray variants)
                continue;
            for (var index = 0; index < variants.Count; index++)
                variants[index] = PruneOptionalWeakContractMembers(variants[index]);
        }

        if (normalized["items"] is { } items)
            normalized["items"] = PruneOptionalWeakContractMembers(items);
        foreach (var additionalPropertiesName in new[] { "additional_properties", "additionalProperties" })
        {
            if (normalized[additionalPropertiesName] is { } additionalProperties)
                normalized[additionalPropertiesName] = PruneOptionalWeakContractMembers(additionalProperties);
        }

        if (normalized["properties"] is not JsonObject properties)
            return normalized;

        var required = normalized["required"] is JsonArray requiredNames
            ? requiredNames
                .OfType<JsonValue>()
                .Select(static value => value.TryGetValue<string>(out var name) ? name : string.Empty)
                .Where(static name => name.Length > 0)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in properties.Select(static pair => pair.Key).ToArray())
        {
            var refined = PruneOptionalWeakContractMembers(properties[propertyName]);
            if (!required.Contains(propertyName)
                && WorkflowPlanContractNormalizer.IsWeakDescriptor(
                    FlowTypeDescriptorConverter.FromJsonSchema(refined)))
            {
                properties.Remove(propertyName);
                continue;
            }

            properties[propertyName] = refined;
        }

        return normalized;
    }

    private static WorkflowPipelineExtraction PreserveSharedPipelineBoundaryContracts(
        WorkflowPipelineExtraction extraction)
    {
        var outputsByName = extraction.Subworkflows
            .SelectMany(spec => spec.OutputSchemas.Select(output => (
                Spec: spec,
                Name: output.Key,
                Schema: output.Value)))
            .Where(static output => output.Schema is JsonObject)
            .GroupBy(static output => output.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        var updated = extraction.Subworkflows.Select(spec =>
        {
            var inputs = spec.InputSchemas.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value?.DeepClone(),
                StringComparer.Ordinal);
            var changed = false;
            foreach (var (inputName, targetSchema) in spec.InputSchemas)
            {
                if (targetSchema is not JsonObject target
                    || !outputsByName.TryGetValue(inputName, out var candidates))
                {
                    continue;
                }

                var sources = candidates
                    .Where(candidate => !string.Equals(candidate.Spec.Name, spec.Name, StringComparison.Ordinal))
                    .Select(static candidate => candidate.Schema)
                    .OfType<JsonObject>()
                    .Where(schema => !WorkflowPlanContractNormalizer.IsWeakDescriptor(
                        FlowTypeDescriptorConverter.FromJsonSchema(schema)))
                    .ToArray();
                if (sources.Length == 0)
                    continue;

                var source = sources[0];
                if (sources.Skip(1).Any(candidate => !JsonNode.DeepEquals(source, candidate)))
                    continue;

                var sourceType = FlowTypeDescriptorConverter.FromJsonSchema(source);
                var targetType = FlowTypeDescriptorConverter.FromJsonSchema(target);
                if (sourceType.FindAssignmentIssue(targetType) == null)
                    continue;

                // A same-named leaf boundary passes one typed value through main. A
                // consumer cannot silently narrow that producer contract because doing
                // so would discard nullable or location fields required downstream.
                inputs[inputName] = source.DeepClone();
                changed = true;
            }

            if (!changed)
                return spec;
            var normalized = spec with { InputSchemas = inputs };
            return normalized with { GenerationPrompt = BuildSubworkflowGenerationPrompt(normalized) };
        }).ToArray();

        return extraction with { Subworkflows = updated };
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
            else if (plannedTool.RequestBindings.Count > 0 && !AlternativeBindingsMatchSchema(
                         new CapabilityAlternative(plannedTool.Server, plannedTool.Kind, plannedTool.Method, plannedTool.RequestBindings),
                         server))
                validationErrors.Add($"Subworkflow '{subworkflowName}' planned tool '{plannedTool.Server}/{plannedTool.Method}' contains request_bindings that are not documented scalar selectors in the discovered input schema.");
        }
    }

    private static WorkflowPipelineSubworkflowSpec ApplyRequiredLeafToolContracts(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext,
        List<string> validationErrors,
        List<PipelineRootCause> rootCauses)
    {
        var plannedTools = PromoteRequiredPlannedTools(spec, pipelineMcpContext);
        var normalizedSpec = ReferenceEquals(plannedTools, spec.PlannedTools)
            ? spec
            : spec with { PlannedTools = plannedTools };

        ValidateRequiredLeafToolContracts(normalizedSpec, pipelineMcpContext, validationErrors, rootCauses);
        return normalizedSpec;
    }

    private static IReadOnlyList<PipelinePlannedTool> PromoteRequiredPlannedTools(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
    {
        if (!RequiresRequiredLeafToolContract(spec)
            || spec.PlannedTools.Count == 0
            || spec.PlannedTools.Any(static tool => tool.Required))
        {
            return spec.PlannedTools;
        }

        var matches = FindLikelyMcpCapabilityMatches(spec, pipelineMcpContext);
        return spec.PlannedTools
            .Select(tool => ShouldPromotePlannedToolToRequired(tool, matches, pipelineMcpContext)
                ? tool with
                {
                    Required = true,
                    Purpose = string.IsNullOrWhiteSpace(tool.Purpose)
                        ? "Required by the leaf's external-action contract."
                        : tool.Purpose
                }
                : tool)
            .ToArray();
    }

    private static bool ShouldPromotePlannedToolToRequired(
        PipelinePlannedTool plannedTool,
        IReadOnlyList<PipelineMcpCapabilityMatch> matches,
        PipelineMcpContext pipelineMcpContext)
        => matches.Any(match =>
            string.Equals(match.Server, plannedTool.Server, StringComparison.Ordinal)
            && string.Equals(match.Kind, plannedTool.Kind, StringComparison.Ordinal)
            && string.Equals(match.Method, plannedTool.Method, StringComparison.Ordinal))
           || PlannedToolExistsInMcpContext(plannedTool, pipelineMcpContext);

    private static bool PlannedToolExistsInMcpContext(PipelinePlannedTool plannedTool, PipelineMcpContext pipelineMcpContext)
    {
        var server = pipelineMcpContext.Servers.FirstOrDefault(server =>
            string.Equals(server.Name, plannedTool.Server, StringComparison.Ordinal));
        if (server == null)
            return false;

        return string.Equals(plannedTool.Kind, "prompt", StringComparison.Ordinal)
            ? server.Prompts.Any(prompt => string.Equals(prompt.Name, plannedTool.Method, StringComparison.Ordinal))
            : server.Tools.Any(tool => string.Equals(tool.Name, plannedTool.Method, StringComparison.Ordinal));
    }

    private static void ValidateRequiredLeafToolContracts(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext,
        List<string> validationErrors,
        List<PipelineRootCause> rootCauses)
    {
        if (!RequiresRequiredLeafToolContract(spec)
            || HasRequiredPlannedExternalCapability(spec))
        {
            return;
        }

        var matches = FindLikelyMcpCapabilityMatches(spec, pipelineMcpContext)
            .Take(8)
            .ToArray();

        var matchGuidance = matches.Length > 0
            ? $"Matching discovered MCP capabilities: {string.Join(", ", matches.Select(static match => match.DisplayName))}."
            : pipelineMcpContext.Servers.Count > 0
                ? "No exact MCP capability match was inferred; use the global MCP context to choose the concrete required tool, or move the work back to main if it is not external."
                : "MCP context is empty; external-action leaves still need explicit required planned_tools or must be reclassified as non-external algorithmic work.";

        var hasAnyPlannedCapability = spec.PlannedTools.Count > 0
                                      || (spec.PlannedNativeSteps?.Count ?? 0) > 0;
        var message = !hasAnyPlannedCapability
            ? $"PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL: Subworkflow '{spec.Name}' is external work but declares no planned MCP tool or native step. "
              + matchGuidance
              + " "
              + "Add the required planned capability for the external action, or move non-external shaping/orchestration back to the main workflow."
            : $"PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL: Subworkflow '{spec.Name}' declares planned capabilities but none are marked required. "
              + matchGuidance
              + " "
              + "Mark the mandatory MCP or native calls as required, or split optional enrichment away from the external-action leaf.";
        validationErrors.Add(message);
        AddPipelineRootCause(
            rootCauses,
            "missing_required_leaf_tool",
            "pipeline_extraction",
            spec.Name,
            outputName: null,
            invalidPath: $"subworkflows.{spec.Name}.planned_tools",
            code: "PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL",
            message,
            primary: true);
    }

    private static bool RequiresRequiredLeafToolContract(WorkflowPipelineSubworkflowSpec spec)
    {
        if (string.Equals(spec.ContractRole, PipelineContractRoleExternalAction, StringComparison.Ordinal))
            return true;

        if (string.Equals(spec.ContractRole, PipelineContractRoleAlgorithmicTransform, StringComparison.Ordinal))
            return false;

        return string.Equals(spec.WorkKind, PipelineWorkKindExternalWork, StringComparison.Ordinal)
               || IsExternalWorkSpec(spec);
    }

    private static bool HasRequiredPlannedExternalCapability(WorkflowPipelineSubworkflowSpec spec)
        => spec.PlannedTools.Any(static tool => tool.Required)
           || (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
               .Any(static step => step.Required);

    private static void ValidatePipelineExtractionQuality(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext,
        List<string> validationErrors,
        List<PipelineRootCause> rootCauses)
    {
        var score = spec.ExtractionScore ?? ScorePipelineExtractionSpec(spec, pipelineMcpContext);
        foreach (var diagnostic in score.Diagnostics)
        {
            validationErrors.Add(diagnostic);
            AddExtractionRootCauseForScoreDiagnostic(spec, diagnostic, rootCauses);
        }

        ValidatePipelineContractRole(spec, validationErrors, rootCauses);
        ValidatePipelineExtractionContracts(spec, validationErrors, rootCauses);

    }

    private static void ValidatePipelineContractRole(
        WorkflowPipelineSubworkflowSpec spec,
        List<string> validationErrors,
        List<PipelineRootCause> rootCauses)
    {
        if (string.IsNullOrWhiteSpace(spec.ContractRole))
        {
            var missingRoleMessage = $"PIPELINE_EXTRACTION_MISSING_CONTRACT_ROLE: Subworkflow '{spec.Name}' must declare or infer a concrete contract_role.";
            validationErrors.Add(missingRoleMessage);
            AddPipelineRootCause(
                rootCauses,
                "weak_extraction_contract",
                "pipeline_extraction",
                spec.Name,
                outputName: null,
                invalidPath: $"subworkflows.{spec.Name}.contract_role",
                code: "PIPELINE_EXTRACTION_MISSING_CONTRACT_ROLE",
                missingRoleMessage,
                primary: true);
            return;
        }

        var invalidRoleCategory = spec.ContractRole switch
        {
            PipelineContractRoleDeterministicGlue => "extraction_over_split",
            PipelineContractRoleOrchestration => "extraction_over_split",
            PipelineContractRoleAbstractPolicy => "abstract_leaf",
            _ => null
        };
        if (invalidRoleCategory == null)
            return;

        var nonLeafRoleMessage = $"PIPELINE_EXTRACTION_NON_LEAF_ROLE: Subworkflow '{spec.Name}' has contract_role '{spec.ContractRole}', which is not valid for a leaf. "
                                 + "Move deterministic glue, orchestration, and abstract policy back to the main workflow.";
        validationErrors.Add(nonLeafRoleMessage);
        AddPipelineRootCause(
            rootCauses,
            invalidRoleCategory,
            "pipeline_extraction",
            spec.Name,
            outputName: null,
            invalidPath: $"subworkflows.{spec.Name}.contract_role",
            code: "PIPELINE_EXTRACTION_NON_LEAF_ROLE",
            nonLeafRoleMessage,
            primary: true);
    }

    private static void ValidatePipelineExtractionContracts(
        WorkflowPipelineSubworkflowSpec spec,
        List<string> validationErrors,
        List<PipelineRootCause> rootCauses)
    {
        if (spec.OutputSchemas.Count == 0 && spec.PlannedTools.Count == 0)
        {
            var message = $"PIPELINE_EXTRACTION_NO_CONCRETE_OUTCOME: Subworkflow '{spec.Name}' has neither planned_tools nor typed outputs. "
                          + "Every leaf must own a concrete external action or a concrete typed data product.";
            validationErrors.Add(message);
            AddPipelineRootCause(
                rootCauses,
                "weak_extraction_contract",
                "pipeline_extraction",
                spec.Name,
                outputName: null,
                invalidPath: $"subworkflows.{spec.Name}.outputs",
                code: "PIPELINE_EXTRACTION_NO_CONCRETE_OUTCOME",
                message,
                primary: true);
        }

        if (string.IsNullOrWhiteSpace(spec.ConcreteOutcome)
            && spec.PlannedTools.Count == 0
            && !HasConcreteTypedOutputContract(spec))
        {
            var message = $"PIPELINE_EXTRACTION_MISSING_CONCRETE_OUTCOME: Subworkflow '{spec.Name}' must describe its concrete_outcome or expose a strong typed output contract.";
            validationErrors.Add(message);
            AddPipelineRootCause(
                rootCauses,
                "weak_extraction_contract",
                "pipeline_extraction",
                spec.Name,
                outputName: null,
                invalidPath: $"subworkflows.{spec.Name}.concrete_outcome",
                code: "PIPELINE_EXTRACTION_MISSING_CONCRETE_OUTCOME",
                message,
                primary: true);
        }

        foreach (var (outputName, schema) in spec.OutputSchemas)
        {
            CollectWeakExtractionSchemaDiagnostics(
                schema,
                $"subworkflows.{spec.Name}.outputs.{outputName}",
                spec.Name,
                outputName,
                validationErrors,
                rootCauses);
        }

        foreach (var (inputName, schema) in spec.InputSchemas)
        {
            CollectWeakExtractionSchemaDiagnostics(
                schema,
                $"subworkflows.{spec.Name}.inputs.{inputName}",
                spec.Name,
                inputName,
                validationErrors,
                rootCauses,
                isInput: true);
        }
    }

    private static bool HasConcreteTypedOutputContract(WorkflowPipelineSubworkflowSpec spec)
        => spec.OutputSchemas.Count > 0
           && spec.OutputSchemas.Values.All(static schema =>
               !WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptorConverter.FromJsonSchema(schema)));

    private static void CollectWeakExtractionSchemaDiagnostics(
        JsonNode? schema,
        string path,
        string leafName,
        string outputName,
        List<string> validationErrors,
        List<PipelineRootCause> rootCauses,
        bool isInput = false)
    {
        foreach (var diagnostic in EnumerateWeakExtractionSchemaDiagnostics(schema, path, isInput))
        {
            validationErrors.Add(diagnostic.Message);
            AddPipelineRootCause(
                rootCauses,
                "weak_extraction_contract",
                "pipeline_extraction",
                leafName,
                outputName,
                diagnostic.Path,
                diagnostic.Code,
                diagnostic.Message,
                primary: true);
        }
    }

    private static IEnumerable<(string Code, string Path, string Message)> EnumerateWeakExtractionSchemaDiagnostics(
        JsonNode? schema,
        string path,
        bool isInput = false)
    {
        if (schema is not JsonObject obj)
        {
            yield return WeakExtractionSchemaDiagnostic(path, "schema is missing or not an object", isInput);
            yield break;
        }

        if (obj.TryGetPropertyValue("anyOf", out var anyOfNode) && anyOfNode is JsonArray anyOf)
        {
            for (var i = 0; i < anyOf.Count; i++)
            {
                foreach (var diagnostic in EnumerateWeakExtractionSchemaDiagnostics(anyOf[i], $"{path}.anyOf[{i}]", isInput))
                    yield return diagnostic;
            }
            yield break;
        }

        if (obj.TryGetPropertyValue("oneOf", out var oneOfNode) && oneOfNode is JsonArray oneOf)
        {
            for (var i = 0; i < oneOf.Count; i++)
            {
                foreach (var diagnostic in EnumerateWeakExtractionSchemaDiagnostics(oneOf[i], $"{path}.oneOf[{i}]", isInput))
                    yield return diagnostic;
            }
            yield break;
        }

        var type = NormalizeWorkflowSchemaType(GetStringProperty(obj, "type") ?? "any");
        switch (type)
        {
            case "any":
                yield return WeakExtractionSchemaDiagnostic(path, "type `any` is not a concrete public leaf contract", isInput);
                yield break;

            case "array":
                {
                    if (obj["items"] is not JsonObject items)
                    {
                        yield return WeakExtractionSchemaDiagnostic($"{path}.items", "array contract must declare concrete items", isInput);
                        yield break;
                    }

                    foreach (var diagnostic in EnumerateWeakExtractionSchemaDiagnostics(items, $"{path}.items", isInput))
                        yield return diagnostic;
                    yield break;
                }

            case "object":
                {
                    if (obj["properties"] is not JsonObject properties || properties.Count == 0)
                    {
                        yield return WeakExtractionSchemaDiagnostic($"{path}.properties", "object contract must declare non-empty properties", isInput);
                        yield break;
                    }

                    foreach (var (propertyName, propertySchema) in properties)
                    {
                        foreach (var diagnostic in EnumerateWeakExtractionSchemaDiagnostics(propertySchema, $"{path}.properties.{propertyName}", isInput))
                            yield return diagnostic;
                    }
                    yield break;
                }

            case "dictionary":
                {
                    var additionalProperties = obj["additional_properties"] ?? obj["additionalProperties"];
                    if (additionalProperties is not JsonObject additionalPropertiesObject)
                    {
                        yield return WeakExtractionSchemaDiagnostic($"{path}.additional_properties", "dictionary contract must declare concrete additional_properties", isInput);
                        yield break;
                    }

                    foreach (var diagnostic in EnumerateWeakExtractionSchemaDiagnostics(additionalPropertiesObject, $"{path}.additional_properties", isInput))
                        yield return diagnostic;
                    yield break;
                }
        }
    }

    private static (string Code, string Path, string Message) WeakExtractionSchemaDiagnostic(
        string path,
        string reason,
        bool isInput = false)
    {
        var code = isInput ? "WEAK_EXTRACTION_INPUT_SCHEMA" : "WEAK_EXTRACTION_OUTPUT_SCHEMA";
        var contract = isInput ? "input" : "output";
        var message = $"{code}: {path}: {reason}. "
                      + $"Strengthen the extracted leaf {contract} contract before leaf generation, or move the candidate back to main.";
        return (code, path, message);
    }

    private static void AddExtractionRootCauseForScoreDiagnostic(
        WorkflowPipelineSubworkflowSpec spec,
        string diagnostic,
        List<PipelineRootCause> rootCauses)
    {
        string? category = null;
        var code = "PIPELINE_EXTRACTION_SCORE_DIAGNOSTIC";
        if (diagnostic.Contains("PIPELINE_EXTRACTION_TRIVIAL_LEAF", StringComparison.Ordinal))
        {
            category = "extraction_over_split";
            code = "PIPELINE_EXTRACTION_TRIVIAL_LEAF";
        }
        else if (diagnostic.Contains("PIPELINE_EXTRACTION_ORCHESTRATION_LEAF", StringComparison.Ordinal))
        {
            category = "extraction_over_split";
            code = "PIPELINE_EXTRACTION_ORCHESTRATION_LEAF";
        }
        else if (diagnostic.Contains("PIPELINE_EXTRACTION_LOW_SCORE", StringComparison.Ordinal))
        {
            category = "extraction_over_split";
            code = "PIPELINE_EXTRACTION_LOW_SCORE";
        }

        if (category == null)
            return;

        AddPipelineRootCause(
            rootCauses,
            category,
            "pipeline_extraction",
            spec.Name,
            outputName: null,
            invalidPath: $"subworkflows.{spec.Name}",
            code,
            diagnostic,
            primary: true);
    }

    private static void AddPipelineRootCause(
        List<PipelineRootCause> rootCauses,
        string category,
        string phase,
        string? leafName,
        string? outputName,
        string? invalidPath,
        string? code,
        string message,
        bool primary)
    {
        if (rootCauses.Any(existing =>
                string.Equals(existing.Category, category, StringComparison.Ordinal)
                && string.Equals(existing.Phase, phase, StringComparison.Ordinal)
                && string.Equals(existing.LeafName, leafName, StringComparison.Ordinal)
                && string.Equals(existing.OutputName, outputName, StringComparison.Ordinal)
                && string.Equals(existing.InvalidPath, invalidPath, StringComparison.Ordinal)
                && string.Equals(existing.Code, code, StringComparison.Ordinal)))
        {
            return;
        }

        rootCauses.Add(new PipelineRootCause(category, phase, leafName, outputName, invalidPath, code, message, primary));
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
        var hasRequiredPlannedNativeStep = (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
            .Any(static step => step.Required);
        var hasAnyPlannedTool = spec.PlannedTools.Count > 0;
        var hasAnyPlannedNativeStep = (spec.PlannedNativeSteps?.Count ?? 0) > 0;
        var hasAnyPlannedCapability = hasAnyPlannedTool || hasAnyPlannedNativeStep;
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

        if (hasRequiredPlannedNativeStep)
        {
            score += 25;
            reasons.Add("declares required native Flow step calls");
        }
        else if (hasAnyPlannedNativeStep)
        {
            score += 15;
            reasons.Add("declares planned native Flow step calls");
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
            && !hasAnyPlannedCapability
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

        if (IsExternalWorkSpec(spec) && !hasAnyPlannedCapability)
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
        return new WorkflowPipelineExtraction(specs, mainWorkflowPrompt, errors, Array.Empty<PipelineRootCause>());
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
            ContractRole: null,
            ConcreteOutcome: null,
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
                contractRole: null,
                concreteOutcome: null,
                inputs,
                outputs,
                inputSchemas,
                outputSchemas,
                Array.Empty<PipelinePlannedTool>(),
                Array.Empty<PipelinePlannedNativeStep>(),
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
        WorkflowPipelineSubworkflowSpec spec)
        => BuildSubworkflowGenerationPrompt(
            spec.Name,
            spec.Goal,
            spec.Description,
            spec.ContractRole,
            spec.ConcreteOutcome,
            spec.Inputs,
            spec.Outputs,
            spec.InputSchemas,
            spec.OutputSchemas,
            spec.PlannedTools,
            spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>(),
            spec.WorkKind,
            spec.Content);

    private static string BuildSubworkflowGenerationPrompt(
        string name,
        string goal,
        string? description,
        string? contractRole,
        string? concreteOutcome,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyDictionary<string, JsonNode?> inputSchemas,
        IReadOnlyDictionary<string, JsonNode?> outputSchemas,
        IReadOnlyList<PipelinePlannedTool> plannedTools,
        IReadOnlyList<PipelinePlannedNativeStep> plannedNativeSteps,
        string? workKind,
        string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate exactly one leaf GnOuGo workflow named `{name}`.");
        sb.AppendLine($"Goal: {goal}");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"Description: {description}");
        if (!string.IsNullOrWhiteSpace(contractRole))
            sb.AppendLine($"Contract role: {contractRole}.");
        if (!string.IsNullOrWhiteSpace(concreteOutcome))
            sb.AppendLine($"Concrete outcome: {concreteOutcome}");
        if (!string.IsNullOrWhiteSpace(workKind))
            sb.AppendLine($"Work kind: {workKind}.");
        sb.AppendLine();
        sb.AppendLine("Leaf workflow constraints:");
        sb.AppendLine("- Generate a complete YAML document with version, name, skill, and workflows.");
        sb.AppendLine($"- The document must contain exactly one workflow, preferably named `{name}`.");
        sb.AppendLine("- The workflow must be a leaf workflow.");
        sb.AppendLine("- Do not use workflow.call.");
        sb.AppendLine("- Do not use workflow.plan.");
        sb.AppendLine("- Do not call or inspect another subworkflow directly. This leaf may consume typed values that main passes from an earlier producer leaf.");
        sb.AppendLine("- Treat the declared input/output contract as a draft when MCP tools require additional arguments.");
        sb.AppendLine("- An MCP request field name does not automatically become a public leaf input. Map it from an existing compatible declared input whenever possible.");
        sb.AppendLine("- Never add a second public input that differs from a declared input only by case or separators; reuse the declared input and map it to the exact request field name.");
        AppendMcpInputContractChecklist(sb);
        AppendExpressionFunctionRules(sb);
        sb.AppendLine("- Workflow outputs must match their declared contract type exactly. A string output must resolve to a string; a boolean output must resolve to a boolean.");
        sb.AppendLine("- Comparison/predicate expressions such as `${a == b}`, `${a != b}`, `${contains(...)}`, and `${exists(...)}` return boolean. Use them only for boolean outputs or `if`/`switch.when` conditions.");
        sb.AppendLine("- For string outputs such as classification/status/level/severity, return a string-valued field or quoted string literal. Invalid for a string output: `${data.steps.classify.json.classification == 'bug'}`. Valid: `${data.steps.classify.json.classification}`.");
        sb.AppendLine("- Quote a complete `${...}` expression used as a YAML scalar, especially when it contains a ternary colon or another mapping-significant character.");
        sb.AppendLine("- If a string output must be derived from an MCP/LLM response, first normalize it with `llm.call` or `mcp.call` `structured_output`, then map `data.steps.<normalizer>.json.<field>` to the workflow output.");
        sb.AppendLine("- The `.json` step output exists only on an `llm.call` or on a call that explicitly declares `structured_output`; an ordinary `mcp.call` exposes only its documented `response` fields. Never reference `data.steps.<ordinary_mcp_call>.json`.");
        sb.AppendLine("- When an MCP tool exposes text such as `response.stdout` but this leaf needs typed domain fields, feed that documented text into a separate `llm.call` with strict `structured_output`, then read the normalizer's `.json` fields. Do not invent domain fields on the MCP result, and use `fromJson(response.stdout)` only when the tool contract explicitly guarantees JSON text.");
        AppendStructuredOutputStrictSchemaRules(sb);
        if (plannedTools.Count > 0)
        {
            sb.AppendLine("- Required planned MCP tools must appear as explicit direct `mcp.call` steps in this leaf.");
            sb.AppendLine("- For planned MCP tools, use exact `input.server`, `input.kind`, and literal `input.method`/`input.methods`; do not satisfy required planned tools through LLM-assisted MCP selection.");
            sb.AppendLine("- Planned tools with activation metadata in one group are mutually exclusive runtime branches: place every branch in a distinct case of exactly one expression-based switch driven by `decision_operation_id`, execute exactly one `branch_value`, and keep the default non-mutating.");
            sb.AppendLine("- Implement an exactly-one capability group with `switch.expr` plus literal `cases[].value`; do not use `cases[].when`. Put exactly one matching variant call in each case, keep unconditional prerequisites outside the switch exactly once, and emit `default: []` unless non-mutating failure handling is required.");
            sb.AppendLine("- A switch `default` is a YAML step sequence. Never emit `default: |` or `default: >`; emit `default:` followed directly by `- id:` step items, or `default: []` when no default action is needed.");
        }
        if (plannedNativeSteps.Count > 0)
        {
            sb.AppendLine("- Required planned native Flow steps must appear as explicit direct steps in this leaf with the exact documented step type.");
            sb.AppendLine("- Do not replace a required native interaction or action with prose, an emit step, or an MCP call.");
        }
        if (string.Equals(workKind, PipelineWorkKindExternalWork, StringComparison.Ordinal))
        {
            sb.AppendLine("- This leaf is external work: it must execute the external/LLM/rendered action with a real step such as mcp.call, llm.call, template.render, or human.input.");
            sb.AppendLine("- Do not replace external work with emit-only instructions, static success flags, or a string telling someone else to run a command.");
        }
        sb.AppendLine("- If a step has an `if`, later unconditional steps must not reference that step directly. Either give the later step the same guard or create guaranteed branch outputs/default values first.");
        sb.AppendLine("- Function arguments are evaluated before the function runs. Do not hide unavailable step references inside `coalesce`, ternaries, or helper calls.");
        sb.AppendLine("- A loop's `results` array contains per-iteration child-step snapshots, never the original input items and never a flattened last-step result.");
        sb.AppendLine("- If a loop body ends with child step `record_result`, a projection function over `data.steps.<loop>.results` must read `iteration.record_result.<field>`; `iteration.<field>` is invalid.");
        sb.AppendLine("- When an MCP call is repeated for array inputs, put the direct required `mcp.call` inside the loop, add a typed `set` child that records the original item plus the call outcome, then project loop snapshots through that typed child step.");
        sb.AppendLine("- Never create an additional empty workflow while repairing a loop. This leaf document must retain exactly one workflow with at least one executable step.");
        sb.AppendLine("- For MCP schemas, required numeric/integer/boolean request fields must be literal YAML scalars when the schema or validator requires explicit values; do not use expressions, casts, empty strings, or `data.env.*` fallbacks.");
        sb.AppendLine("- Follow discovered MCP schemas and tool descriptions exactly; do not add Flow-specific conventions for request fields.");
        sb.AppendLine("- Any schema with `type: object` MUST be strongly typed with a non-empty `properties` mapping. Never generate a bare `type: object` input, output, item, or nested property.");
        sb.AppendLine("- For closed set output_schema objects or arrays, project exact declared fields before assigning custom-function results; do not pass opaque source objects through.");
        sb.AppendLine("- An opaque MCP response may be plain text even after JSON envelope decoding. Preserve textual results verbatim, retain the raw text when parsing it, and never assume a decoded string exposes object fields or an items array.");
        sb.AppendLine("- If this leaf contributes evidence for a later external write, expose enough source/coverage information for main to distinguish a genuinely empty result from a failed or lossy parse.");
        sb.AppendLine("- A public output that is an operational artifact locator (for example a workspace, project root, directory, or file that must already exist) must remain statically traceable to the exact external/action response field that created or proved it, or to an exact caller input. Bind it directly or through transparent one-field `set` aliases; never return an invented literal, string template, cast, or custom-function result as that artifact locator.");
        sb.AppendLine("- If this leaf owns an MCP materializer, expose the exact field declared by artifact_contract. If it consumes an artifact, accept the compatible typed value as an input; never add a second materializer to make the leaf self-contained.");
        sb.AppendLine("- Any workflow output with `type: array` MUST be strongly typed with an `items` schema. Never generate an array output as a bare expression or bare `type: array` without `items`.");
        sb.AppendLine("- Array output `items` must use a concrete type. If items are objects, include every property the parent may need under `items.properties`.");
        sb.AppendLine("- Never duplicate the YAML key `required` in an object schema. Use `required: true|false` only for input-level requiredness, and use `required_properties: [field_name]` for required object property names.");
        sb.AppendLine();
        AppendContractSection(sb, "Inputs", inputs);
        AppendContractSection(sb, "Outputs", outputs);
        AppendStructuredContractSection(sb, "Structured input schemas", inputSchemas);
        AppendStructuredContractSection(sb, "Structured output schemas", outputSchemas);
        AppendPlannedToolsSection(sb, plannedTools);
        AppendPlannedNativeStepsSection(sb, plannedNativeSteps);
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
            if (plannedTool.RequestBindings.Count > 0)
                sb.AppendLine($"  request_bindings (mandatory literal request values): {FormatBindingsCompact(plannedTool.RequestBindings)}");
        }
    }

    private static void AppendPlannedNativeStepsSection(
        StringBuilder sb,
        IReadOnlyList<PipelinePlannedNativeStep> plannedNativeSteps)
    {
        sb.AppendLine();
        sb.AppendLine("Planned native Flow steps:");
        if (plannedNativeSteps.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var plannedStep in plannedNativeSteps)
        {
            var required = plannedStep.Required ? "required" : "optional";
            sb.AppendLine($"- {plannedStep.Method} ({required})");
            if (!string.IsNullOrWhiteSpace(plannedStep.Purpose))
                sb.AppendLine($"  purpose: {plannedStep.Purpose}");
            if (plannedStep.OperationIds.Count > 0)
                sb.AppendLine($"  operation_ids: {string.Join(", ", plannedStep.OperationIds)}");
            if (plannedStep.CatalogIds.Count > 0)
                sb.AppendLine($"  catalog_ids: {string.Join(", ", plannedStep.CatalogIds)}");
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

    private PipelineLeafBlueprint PlanLeafBlueprint(
        StepExecutionContext parentCtx,
        WorkflowPipelineSubworkflowSpec spec)
    {
        using var blueprintSpan = parentCtx.BeginTelemetrySpan("workflow.plan.pipeline.plan_leaf", "plan_leaf", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", spec.Name)
        });

        try
        {
            var blueprint = BuildLeafBlueprint(spec);
            ValidateLeafBlueprint(spec, blueprint);
            blueprintSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_blueprint.step_count", blueprint.Steps.Count);
            blueprintSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_blueprint.output_count", blueprint.Outputs.Count);
            blueprintSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_blueprint.status", "succeeded");
            if (parentCtx.Limits.LogStepContent)
            {
                blueprintSpan.AddEvent("gnougo-flow.plan.pipeline.leaf_blueprint", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", spec.Name),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_blueprint", BuildPipelineLeafBlueprintJson(blueprint).ToJsonString(PromptJsonOptions))
                });
            }

            return blueprint;
        }
        catch (Exception ex)
        {
            blueprintSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_blueprint.status", "failed");
            blueprintSpan.Fail(ex);
            throw;
        }
    }

    private static PipelineLeafBlueprint BuildLeafBlueprint(WorkflowPipelineSubworkflowSpec spec)
    {
        var steps = new List<PipelineLeafBlueprintStep>();
        var usedStepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plannedNativeStep in spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
        {
            var stepId = MakeUniqueBlueprintStepId(
                "perform_" + SanitizeBlueprintIdentifier(plannedNativeStep.Method),
                usedStepIds);
            steps.Add(new PipelineLeafBlueprintStep(
                stepId,
                plannedNativeStep.Method,
                string.IsNullOrWhiteSpace(plannedNativeStep.Purpose)
                    ? $"Execute required native Flow step {plannedNativeStep.Method}."
                    : plannedNativeStep.Purpose!,
                PlannedTool: null,
                plannedNativeStep,
                OutputSchema: null));
        }

        foreach (var plannedTool in spec.PlannedTools)
        {
            var stepId = MakeUniqueBlueprintStepId(
                "call_" + SanitizeBlueprintIdentifier(plannedTool.Method),
                usedStepIds);
            steps.Add(new PipelineLeafBlueprintStep(
                stepId,
                "mcp.call",
                string.IsNullOrWhiteSpace(plannedTool.Purpose)
                    ? $"Call planned MCP {plannedTool.Kind} {plannedTool.Server}/{plannedTool.Method}."
                    : plannedTool.Purpose!,
                plannedTool,
                PlannedNativeStep: null,
                OutputSchema: null));
        }

        if (steps.Count == 0)
        {
            var stepType = IsExternalWorkSpec(spec) ? "llm.call" : "set";
            var stepId = MakeUniqueBlueprintStepId(
                IsExternalWorkSpec(spec) ? "perform_leaf_work" : "build_outputs",
                usedStepIds);
            steps.Add(new PipelineLeafBlueprintStep(
                stepId,
                stepType,
                string.IsNullOrWhiteSpace(spec.ConcreteOutcome)
                    ? $"Produce the public outputs for leaf '{spec.Name}'."
                    : spec.ConcreteOutcome!,
                PlannedTool: null,
                PlannedNativeStep: null,
                BuildBlueprintStepOutputSchema(spec.OutputSchemas)));
        }

        var requiresPathTotalProjection = steps.Count > 1
                                          || spec.PlannedTools.Any(static tool => tool.Activation != null);
        if (requiresPathTotalProjection)
        {
            var conditionalGroups = spec.PlannedTools
                .Where(static tool => tool.Activation != null)
                .Select(static tool => tool.Activation!)
                .GroupBy(static activation => activation.Group, StringComparer.Ordinal)
                .Select(group => $"conditional group '{group.Key}' ({string.Join(", ", group.Select(static activation => activation.BranchValue).Distinct(StringComparer.Ordinal))})")
                .ToArray();
            var purpose = conditionalGroups.Length == 0
                ? "Project the complete public output contract from all preceding action results."
                : $"Project the complete public output contract from values guaranteed on every accepted path after {string.Join(" and ", conditionalGroups)}; never reference a branch-local call directly.";
            steps.Add(new PipelineLeafBlueprintStep(
                MakeUniqueBlueprintStepId("project_outputs", usedStepIds),
                "set",
                purpose,
                PlannedTool: null,
                PlannedNativeStep: null,
                BuildBlueprintStepOutputSchema(spec.OutputSchemas)));
        }
        else if (steps.Count == 1 && steps[0].OutputSchema == null)
        {
            steps[0] = steps[0] with { OutputSchema = BuildBlueprintStepOutputSchema(spec.OutputSchemas) };
        }

        var sourceStepId = steps[^1].Id;
        var outputs = spec.OutputSchemas
            .Select(pair => new PipelineLeafBlueprintOutput(
                pair.Key,
                $"${{data.steps.{sourceStepId}.{pair.Key}}}",
                sourceStepId,
                pair.Value?.DeepClone()))
            .ToArray();

        return new PipelineLeafBlueprint(
            spec.Name,
            spec.Name,
            string.IsNullOrWhiteSpace(spec.ConcreteOutcome)
                ? spec.Goal
                : spec.ConcreteOutcome!,
            steps,
            outputs);
    }

    private static JsonObject BuildBlueprintStepOutputSchema(IReadOnlyDictionary<string, JsonNode?> outputSchemas)
    {
        var properties = new JsonObject();
        var requiredProperties = new JsonArray();
        foreach (var (name, schema) in outputSchemas)
        {
            properties[name] = schema?.DeepClone();
            requiredProperties.Add((JsonNode)JsonValue.Create(name)!);
        }

        var outputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (requiredProperties.Count > 0)
            outputSchema["required_properties"] = requiredProperties;
        return outputSchema;
    }

    private static string MakeUniqueBlueprintStepId(string baseId, HashSet<string> usedStepIds)
    {
        var id = string.IsNullOrWhiteSpace(baseId) ? "step" : baseId;
        if (!IdentifierRegex().IsMatch(id))
            id = "step";

        var candidate = id;
        var suffix = 2;
        while (!usedStepIds.Add(candidate))
        {
            candidate = $"{id}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeBlueprintIdentifier(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (ch is '_' or '-' or '.')
                sb.Append('_');
        }

        var id = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(id))
            return "step";
        if (!char.IsLetter(id[0]) && id[0] != '_')
            id = "_" + id;
        return id;
    }

    private static void ValidateLeafBlueprint(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineLeafBlueprint blueprint)
    {
        var diagnostics = new JsonArray();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(blueprint.WorkflowName))
            AddLeafBlueprintDiagnostic(diagnostics, spec.Name, "workflow_name", "PIPELINE_LEAF_BLUEPRINT_MISSING_WORKFLOW", "Leaf blueprint must declare a workflow_name.");

        if (blueprint.Steps.Count == 0)
        {
            AddLeafBlueprintDiagnostic(diagnostics, spec.Name, "steps", "PIPELINE_LEAF_BLUEPRINT_MISSING_STEPS", "Leaf blueprint must declare at least one implementation step.");
        }
        else
        {
            foreach (var step in blueprint.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Id) || !IdentifierRegex().IsMatch(step.Id))
                {
                    AddLeafBlueprintDiagnostic(diagnostics, spec.Name, $"steps.{step.Id}", "PIPELINE_LEAF_BLUEPRINT_INVALID_STEP_ID", $"Blueprint step id '{step.Id}' must be a valid identifier.");
                    continue;
                }

                if (!stepIds.Add(step.Id))
                    AddLeafBlueprintDiagnostic(diagnostics, spec.Name, $"steps.{step.Id}", "PIPELINE_LEAF_BLUEPRINT_DUPLICATE_STEP_ID", $"Blueprint step id '{step.Id}' is duplicated.");

                if (step.Type is "workflow.call" or "workflow.plan")
                    AddLeafBlueprintDiagnostic(diagnostics, spec.Name, $"steps.{step.Id}.type", "PIPELINE_LEAF_BLUEPRINT_FORBIDDEN_STEP", $"Leaf blueprint step '{step.Id}' must not use step type '{step.Type}'.");

                if (string.Equals(step.Type, "emit", StringComparison.Ordinal)
                    && FakeActionTextRegex().IsMatch(step.Purpose ?? ""))
                {
                    AddLeafBlueprintDiagnostic(diagnostics, spec.Name, $"steps.{step.Id}.purpose", "PIPELINE_LEAF_BLUEPRINT_FAKE_ACTION", $"Leaf blueprint step '{step.Id}' describes an external action as text instead of using an executable action step.");
                }
            }
        }

        foreach (var requiredTool in spec.PlannedTools.Where(static tool => tool.Required))
        {
            if (!blueprint.Steps.Any(step => step.PlannedTool != null && PlannedToolMatches(step.PlannedTool, requiredTool)))
            {
                AddLeafBlueprintDiagnostic(
                    diagnostics,
                    spec.Name,
                    "steps",
                    "PIPELINE_LEAF_BLUEPRINT_MISSING_PLANNED_TOOL",
                    $"Leaf blueprint must include required planned MCP {requiredTool.Kind} {requiredTool.Server}/{requiredTool.Method}.");
            }
        }

        foreach (var activationGroup in spec.PlannedTools
                     .Where(static tool => tool.Activation != null)
                     .Select(static tool => tool.Activation!)
                     .GroupBy(static activation => activation.Group, StringComparer.Ordinal))
        {
            var activation = activationGroup.First();
            var branches = activationGroup.Select(static item => item.BranchValue)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (string.IsNullOrWhiteSpace(activation.DecisionOutputPath)
                || !ConditionalActivationValuesAreValid(activation, branches))
            {
                AddLeafBlueprintDiagnostic(
                    diagnostics,
                    spec.Name,
                    $"activation_groups.{activation.Group}",
                    "PIPELINE_LEAF_BLUEPRINT_PATH_DEPENDENT_OUTPUT",
                    $"Conditional group '{activation.Group}' has no grounded enum decision path with exact branch coverage, so no public output can be proven on every path.");
            }
        }

        foreach (var requiredNativeStep in (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
                     .Where(static step => step.Required))
        {
            if (!blueprint.Steps.Any(step => step.PlannedNativeStep != null
                                             && PlannedNativeStepMatches(step.PlannedNativeStep, requiredNativeStep)))
            {
                AddLeafBlueprintDiagnostic(
                    diagnostics,
                    spec.Name,
                    "steps",
                    "PIPELINE_LEAF_BLUEPRINT_MISSING_PLANNED_NATIVE_STEP",
                    $"Leaf blueprint must include required native Flow step {requiredNativeStep.Method}.");
            }
        }

        if (IsExternalWorkSpec(spec) && !blueprint.Steps.Any(static step => IsExecutableActionStepType(step.Type)))
        {
            AddLeafBlueprintDiagnostic(
                diagnostics,
                spec.Name,
                "steps",
                "PIPELINE_LEAF_BLUEPRINT_EXTERNAL_WITHOUT_ACTION",
                "External-work leaf blueprint must include a real executable action step.");
        }

        var outputsByName = blueprint.Outputs.ToDictionary(static output => output.Name, StringComparer.Ordinal);
        foreach (var (outputName, expectedSchema) in spec.OutputSchemas)
        {
            if (!outputsByName.TryGetValue(outputName, out var output))
            {
                AddLeafBlueprintDiagnostic(
                    diagnostics,
                    spec.Name,
                    $"outputs.{outputName}",
                    "PIPELINE_LEAF_BLUEPRINT_MISSING_OUTPUT",
                    $"Leaf blueprint must bind public output '{outputName}'.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(output.Expr))
                AddLeafBlueprintDiagnostic(diagnostics, spec.Name, $"outputs.{outputName}.expr", "PIPELINE_LEAF_BLUEPRINT_MISSING_OUTPUT_EXPR", $"Leaf blueprint output '{outputName}' must declare an expression binding.");
            if (!string.IsNullOrWhiteSpace(output.SourceStepId) && !stepIds.Contains(output.SourceStepId))
                AddLeafBlueprintDiagnostic(diagnostics, spec.Name, $"outputs.{outputName}.source_step", "PIPELINE_LEAF_BLUEPRINT_UNKNOWN_OUTPUT_SOURCE", $"Leaf blueprint output '{outputName}' references unknown source step '{output.SourceStepId}'.");

            if (blueprint.Steps.FirstOrDefault(step => string.Equals(step.Id, output.SourceStepId, StringComparison.Ordinal))
                is { PlannedTool.Activation: not null })
            {
                AddLeafBlueprintDiagnostic(
                    diagnostics,
                    spec.Name,
                    $"outputs.{outputName}.source_step",
                    "PIPELINE_LEAF_BLUEPRINT_PATH_DEPENDENT_OUTPUT",
                    $"Leaf blueprint output '{outputName}' binds directly to a conditional branch call. Bind it to a typed projection guaranteed after the branch group.");
            }

            ValidateLeafBlueprintOutputSchema(spec.Name, outputName, output.Schema, expectedSchema, diagnostics);
        }

        if (diagnostics.Count > 0)
            throw BuildLeafBlueprintValidationException(spec.Name, BuildLeafBlueprintJsonSafe(blueprint), diagnostics);
    }

    private static void ValidateLeafBlueprintOutputSchema(
        string leafName,
        string outputName,
        JsonNode? actualSchema,
        JsonNode? expectedSchema,
        JsonArray diagnostics)
    {
        var actualDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(actualSchema);
        var expectedDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(expectedSchema);
        if (WorkflowPlanContractNormalizer.IsWeakDescriptor(expectedDescriptor))
            return;

        if (WorkflowPlanContractNormalizer.IsWeakDescriptor(actualDescriptor))
        {
            AddLeafBlueprintDiagnostic(
                diagnostics,
                leafName,
                $"outputs.{outputName}.schema",
                "PIPELINE_LEAF_BLUEPRINT_WEAK_OUTPUT_SCHEMA",
                $"Leaf blueprint output '{outputName}' must use a concrete schema.");
            return;
        }

        if (!actualDescriptor.IsCompatibleWith(expectedDescriptor))
        {
            AddLeafBlueprintDiagnostic(
                diagnostics,
                leafName,
                $"outputs.{outputName}.schema",
                "PIPELINE_LEAF_BLUEPRINT_OUTPUT_SCHEMA_MISMATCH",
                $"Leaf blueprint output '{outputName}' schema '{actualDescriptor.Describe()}' is not compatible with extracted contract '{expectedDescriptor.Describe()}'.");
        }
    }

    private static void AddLeafBlueprintDiagnostic(
        JsonArray diagnostics,
        string leafName,
        string path,
        string code,
        string message)
    {
        diagnostics.Add((JsonNode)new JsonObject
        {
            ["code"] = code,
            ["phase"] = "pipeline_leaf_blueprint_validation",
            ["leaf"] = leafName,
            ["invalid_path"] = $"blueprints.{leafName}.{path}",
            ["message"] = message
        });
    }

    private static WorkflowRuntimeException BuildLeafBlueprintValidationException(
        string leafName,
        JsonObject blueprint,
        JsonArray diagnostics)
    {
        var rootCauses = BuildLeafBlueprintRootCausesJson(leafName, diagnostics, "leaf_blueprint_invalid");
        var details = new JsonObject
        {
            ["phase"] = "pipeline_leaf_blueprint_validation",
            ["leaf"] = leafName,
            ["blueprint"] = blueprint,
            ["diagnostics"] = diagnostics.DeepClone(),
            ["root_causes"] = rootCauses
        };
        var message = diagnostics
            .Select(static node => node is JsonObject obj ? GetStringProperty(obj, "message") : null)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .DefaultIfEmpty("Leaf blueprint validation failed.")
            .Aggregate((left, right) => left + "; " + right);

        return new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf blueprint '{leafName}' failed validation: {message}",
            details: details);
    }

    private static JsonObject BuildLeafBlueprintJsonSafe(PipelineLeafBlueprint blueprint)
        => BuildPipelineLeafBlueprintJson(blueprint);

    private static JsonArray BuildLeafBlueprintRootCausesJson(
        string leafName,
        JsonArray diagnostics,
        string category)
    {
        var rootCauses = new List<PipelineRootCause>();
        foreach (var node in diagnostics)
        {
            if (node is not JsonObject diagnostic)
                continue;

            AddPipelineRootCause(
                rootCauses,
                category,
                GetStringProperty(diagnostic, "phase") ?? "pipeline_leaf_blueprint_validation",
                leafName,
                outputName: null,
                invalidPath: GetStringProperty(diagnostic, "invalid_path"),
                code: GetStringProperty(diagnostic, "code"),
                GetStringProperty(diagnostic, "message") ?? "Leaf blueprint validation failed.",
                primary: true);
        }

        return BuildPipelineRootCausesJson(rootCauses);
    }

    private static bool PlannedToolMatches(PipelinePlannedTool candidate, PipelinePlannedTool expected)
        => string.Equals(candidate.Server, expected.Server, StringComparison.Ordinal)
           && string.Equals(candidate.Kind, expected.Kind, StringComparison.Ordinal)
           && string.Equals(candidate.Method, expected.Method, StringComparison.Ordinal)
           && RequestBindingsEqual(candidate.RequestBindings, expected.RequestBindings);

    private static bool PlannedNativeStepMatches(
        PipelinePlannedNativeStep candidate,
        PipelinePlannedNativeStep expected)
        => string.Equals(candidate.Method, expected.Method, StringComparison.Ordinal);

    private static JsonObject BuildLeafPlanInput(
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineSubworkflowSpec spec,
        PipelineLeafBlueprint blueprint,
        IReadOnlyList<PipelineStructuredDecisionRequirement> structuredDecisionRequirements,
        string? previousError,
        string? previousYaml,
        string? previousRepairContext,
        int outerAttempt)
    {
        var leafGenerator = generator.DeepClone() as JsonObject ?? new JsonObject();
        leafGenerator.Remove("mode");
        leafGenerator.Remove("raw_prompt");
        var generationPrompt = BuildLeafYamlGenerationPrompt(
            spec.GenerationPrompt,
            blueprint,
            structuredDecisionRequirements);
        leafGenerator["instruction"] = string.IsNullOrWhiteSpace(previousError)
            ? generationPrompt
            : BuildLeafRepairPrompt(generationPrompt, previousYaml, previousError, previousRepairContext);
        leafGenerator["context"] = "";
        leafGenerator["pipeline_leaf_name"] = spec.Name;
        leafGenerator["pipeline_leaf_attempt"] = outerAttempt;

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

    private static string BuildLeafYamlGenerationPrompt(
        string baseGenerationPrompt,
        PipelineLeafBlueprint blueprint,
        IReadOnlyList<PipelineStructuredDecisionRequirement> structuredDecisionRequirements)
    {
        var sb = new StringBuilder();
        sb.AppendLine(baseGenerationPrompt.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Locked leaf blueprint:");
        sb.AppendLine("- The blueprint below was validated before YAML generation and is authoritative.");
        sb.AppendLine("- Implement every required planned tool call and public output binding from the blueprint.");
        sb.AppendLine("- Do not weaken output schemas from the blueprint.");
        sb.AppendLine("- When the blueprint contains `project_outputs`, implement one typed projection after every action. Every public output must come from that projection, and conditional branches must first publish the same typed intermediate fields on every declared enum case.");
        sb.AppendLine("- If the blueprint names a planned MCP tool, the YAML must contain a matching explicit direct mcp.call.");
        sb.AppendLine("- Every planned tool request_binding must appear as the exact literal scalar at its JSON Pointer path under mcp.call.input.request. Expressions, opaque request objects, and a different selector value are invalid.");
        sb.AppendLine("- If implementation uses a loop, every `results[]` item is a child-step snapshot. Projection code must read `iteration.<child_step_id>.<field>`, never `iteration.<field>` or the original item shape directly.");
        sb.AppendLine("- In every JavaScript `functions` return object, use actual expressions as property values; never quote source-code expressions. Put property-separator commas outside closing string quotes and syntax-check the complete functions block.");
        sb.AppendLine("- Preserve exactly one non-empty workflow throughout repair; never append an empty workflow copy.");
        foreach (var requirement in structuredDecisionRequirements)
        {
            var fieldName = GetDecisionBoundaryFieldName(requirement.Activation.DecisionOutputPath);
            sb.AppendLine($"- This leaf owns decision operation '{requirement.Activation.DecisionOperationId}'. Implement producer capability '{requirement.Producer.CatalogId}' as exactly one direct `{requirement.Producer.Method}` step. Its `input.structured_output.strict` must be true and `schema_inline` must declare required string field '{fieldName}' with exactly enum [{string.Join(", ", requirement.Activation.AllowedValues)}]. Expose workflow output '{fieldName}' unchanged from exact producer path '{requirement.Activation.DecisionOutputPath}'. A direct expression or pure `set` projection is allowed; custom functions, coercion, fallback, aliasing, and recomputation are invalid.");
        }
        AppendPromptSection(sb, "locked_leaf_blueprint_json", BuildPipelineLeafBlueprintJson(blueprint).ToJsonString(PromptJsonOptions));
        return sb.ToString().TrimEnd();
    }

    private async Task<GeneratedLeafWorkflow> GenerateLeafWorkflowAsync(
        StepExecutionContext parentCtx,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext,
        CapabilityPreflightResult capabilityPreflight,
        CancellationToken ct)
    {
        var maxAttempts = GetPipelineGenerationMaxAttempts(pipelineInput);
        var blueprint = PlanLeafBlueprint(parentCtx, spec);
        var structuredDecisionRequirements = BuildPipelineStructuredDecisionRequirements(
            spec,
            capabilityPreflight);
        Exception? lastException = null;
        string? previousError = null;
        string? previousYaml = null;
        string? previousRepairContext = null;
        string? previousDiagnosticFingerprint = null;
        var unchangedRepairAttempts = 0;
        var previousErrors = new List<(string Fingerprint, string Error)>();
        var repairDiagnosticHistory = new List<string>();
        var repairYamlStructureHistory = new List<string>();
        var leafQualityEvents = new List<PipelineQualityEvent>();

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
                    blueprint,
                    structuredDecisionRequirements,
                    previousError,
                    previousYaml,
                    previousRepairContext,
                    attempt);
                ApplyPipelineLeafRepairReasoning(
                    leafInput,
                    GetStringProperty(generator, "reasoning"),
                    attempt);
                var leafCtx = new StepExecutionContext
                {
                    Step = parentCtx.Step,
                    Data = parentCtx.Data,
                    Engine = parentCtx.Engine,
                    Limits = parentCtx.Limits,
                    CallDepth = parentCtx.CallDepth,
                    CallStack = new HashSet<string>(parentCtx.CallStack),
                    LLMUsageBudget = parentCtx.LLMUsageBudget,
                    ExecutionScope = parentCtx.ExecutionScope,
                    TelemetrySpan = parentCtx.TelemetrySpan
                };

                var result = await ExecuteSinglePlanAsync(
                    leafCtx,
                    leafInput,
                    ct,
                    leafAttemptSpan.Span,
                    preselectedMcpServers: pipelineMcpContext.Servers.Count > 0
                        ? pipelineMcpContext.Servers
                        : null) as JsonObject
                    ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' generation did not return an object.");
                var yaml = result["yaml"]?.GetValue<string>()
                    ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' generation did not return YAML.");

                leafAttemptSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_status", "succeeded");
                try
                {
                    return PrepareGeneratedLeaf(
                        spec,
                        yaml,
                        blueprint,
                        leafQualityEvents,
                        pipelineMcpContext,
                        parentCtx.Engine.Registry,
                        structuredDecisionRequirements);
                }
                catch
                {
                    previousYaml = yaml;
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                if (WorkflowPlanDiagnostics.IsNonRepairableLlmFailure(ex))
                    throw;

                var failedYaml = previousYaml ?? TryExtractGeneratedYamlFromException(ex);

                var stalled = DetectRepairStall(
                    ex,
                    attempt,
                    isRepairAttempt: attempt > 1,
                    ref previousDiagnosticFingerprint,
                    ref unchangedRepairAttempts);
                if (stalled != null)
                    throw stalled;

                var cycled = DetectLeafRepairCycle(
                    ex,
                    spec.Name,
                    attempt,
                    isRepairAttempt: attempt > 1,
                    failedYaml,
                    repairDiagnosticHistory,
                    repairYamlStructureHistory);
                if (cycled != null)
                    throw cycled;

                lastException = ex;
                previousYaml = failedYaml;
                previousError = TruncatePipelineQualityMessage(
                    FormatLeafGenerationError(spec.Name, attempt, ex),
                    maxLength: 6_000);
                var diagnosticFingerprint = WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(ex);
                previousErrors.RemoveAll(item => string.Equals(item.Fingerprint, diagnosticFingerprint, StringComparison.Ordinal));
                previousErrors.Add((diagnosticFingerprint, previousError));
                if (previousErrors.Count > 4)
                    previousErrors.RemoveAt(0);
                if (TryCreateLeafBlueprintQualityEvent(spec.Name, attempt, ex, out var qualityEvent))
                    leafQualityEvents.Add(qualityEvent);
                previousRepairContext = await BuildPipelineLeafRepairContextAsync(
                    parentCtx,
                    pipelineInput,
                    previousYaml,
                    ex,
                    leafAttemptSpan.Span,
                    ct);
                previousRepairContext = MergeLeafCumulativeRepairContext(
                    previousErrors.Select(static item => item.Error).ToArray(),
                    previousRepairContext);
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

    private static bool TryCreateLeafBlueprintQualityEvent(
        string leafName,
        int attempt,
        Exception ex,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineQualityEvent? qualityEvent)
    {
        if (!TryFindPipelineRootCause(ex, "leaf_blueprint_yaml_mismatch", out var rootCause))
        {
            qualityEvent = null;
            return false;
        }

        qualityEvent = new PipelineQualityEvent(
            "leaf_blueprint_yaml_mismatch",
            attempt,
            "generate_leaf",
            leafName,
            OutputName: null,
            ConsumerStepId: null,
            ConsumerField: null,
            InvalidPath: GetStringProperty(rootCause, "invalid_path"),
            Reason: GetStringProperty(rootCause, "code"),
            RequiredOutputPaths: null,
            ExpectedType: null,
            ErrorType: ex.GetType().Name,
            Message: TruncatePipelineQualityMessage(GetStringProperty(rootCause, "message") ?? ex.Message));
        return true;
    }

    private static bool TryFindPipelineRootCause(
        Exception ex,
        string category,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonObject? rootCause)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is not WorkflowRuntimeException { Details: JsonObject details })
                continue;

            if (details["root_causes"] is not JsonArray rootCauses)
                continue;

            foreach (var node in rootCauses)
            {
                if (node is JsonObject obj
                    && string.Equals(GetStringProperty(obj, "category"), category, StringComparison.Ordinal))
                {
                    rootCause = obj;
                    return true;
                }
            }
        }

        rootCause = null;
        return false;
    }

    private static string MergeLeafCumulativeRepairContext(
        IReadOnlyList<string> previousErrors,
        string? latestRepairContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cumulative leaf retry requirements:");
        sb.AppendLine("- Preserve all fixes made for earlier validation failures; do not regress one MCP request or output while fixing another.");
        sb.AppendLine("- Re-check every mcp.call in the leaf against its discovered input_schema, not only the step named in the latest error.");
        sb.AppendLine("- Re-check every ordinary mcp.call output reference too: it has no `.json` domain object. Consume only documented `response` fields, or pass the documented response into one separate llm.call with strict structured_output and read that normalizer's `.json` fields.");
        sb.AppendLine("- If a required MCP request field is numeric/integer/boolean, emit an explicit YAML scalar of that type when the validator requires it.");
        sb.AppendLine("- If a required MCP request field is string/number/boolean, do not pass a nullable structured_output field into it; make the source non-null, refine it with assert.non_null, add an exact non-null step guard, or skip the mcp.call.");
        sb.AppendLine("- Never assign a `null|T` producer directly to a non-null set/output schema. Preserve nullable at the exact property when the public contract allows it, or project a documented neutral absence representation for an intentionally non-null contract; for object arrays, repair every nullable nested property in fresh projected items.");
        sb.AppendLine("- Never satisfy missing MCP arguments with `data.env.*`, empty strings, fake values, casts, or string-to-number conversions.");
        sb.AppendLine("- Do not reference an `if`-guarded step from an unconditional later step unless a guaranteed value has first been produced on every path.");
        sb.AppendLine("- Workflow outputs must resolve to their declared type on every path.");
        sb.AppendLine("- An output schema is a contract, not a cast. When an expression resolves to object, it cannot satisfy array, string, number, or boolean merely by declaring that target type.");
        sb.AppendLine("- When one opaque or object response feeds several typed outputs, add one evidence-backed typed normalization/projection step and bind each output to its compatible projected field, or expose only a compatible documented value. Never bind every typed output to the same whole response.");
        sb.AppendLine("- Preserve operational artifact provenance: an artifact-locator output must bind directly to the external/action response field that created or proved it, or to an exact caller input. Do not route that locator through a custom function, string template, cast, or invented literal.");
        sb.AppendLine("- Preserve artifact reuse: repair consumers to accept the existing producer output; never repair a missing artifact argument by adding another materializer.");
        sb.AppendLine("- For JavaScript `functions` syntax failures, preserve the locked output/property names and repair only the malformed statement. Use actual expressions as object values, never quoted source code, and keep property-separator commas outside strings.");

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
            sb.AppendLine(TruncatePipelineQualityMessage(latestRepairContext.Trim(), maxLength: 12_000));
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
        string? previousYaml,
        string previousError,
        string? additionalRepairContext)
    {
        var repairContext = new StringBuilder();
        repairContext.AppendLine("Previous generated YAML for this leaf workflow failed validation.");
        repairContext.AppendLine("Regenerate only this leaf workflow and fix the YAML below.");

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
        PipelineMcpContext pipelineMcpContext,
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
            currentLeaf.Blueprint,
            currentLeaf.StructuredDecisionRequirements ?? Array.Empty<PipelineStructuredDecisionRequirement>(),
            previousError,
            currentLeaf.Yaml,
            repairContext,
            attempt);
        ApplyPipelineLeafRepairReasoning(
            leafInput,
            GetStringProperty(generator, "reasoning"),
            attempt + 2);
        ForceSinglePlanAttempt(leafInput);

        var leafCtx = new StepExecutionContext
        {
            Step = parentCtx.Step,
            Data = parentCtx.Data,
            Engine = parentCtx.Engine,
            Limits = parentCtx.Limits,
            CallDepth = parentCtx.CallDepth,
            CallStack = new HashSet<string>(parentCtx.CallStack),
            LLMUsageBudget = parentCtx.LLMUsageBudget,
            ExecutionScope = parentCtx.ExecutionScope,
            TelemetrySpan = parentCtx.TelemetrySpan
        };

        var result = await ExecuteSinglePlanAsync(
            leafCtx,
            leafInput,
            ct,
            leafRepairSpan.Span,
            preselectedMcpServers: pipelineMcpContext.Servers.Count > 0
                ? pipelineMcpContext.Servers
                : null) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' contract repair did not return an object.");
        var yaml = result["yaml"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' contract repair did not return YAML.");
        var repairedLeaf = PrepareGeneratedLeaf(
            spec,
            yaml,
            currentLeaf.Blueprint,
            currentLeaf.QualityEvents,
            pipelineMcpContext,
            parentCtx.Engine.Registry,
            currentLeaf.StructuredDecisionRequirements);
        EnsureLeafSatisfiesContractDemand(repairedLeaf, demand);

        var repairedLeaves = leaves.ToArray();
        repairedLeaves[leafIndex] = repairedLeaf;
        leafRepairSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_contract_repair_status", "succeeded");
        return repairedLeaves;
    }

    private async Task<GeneratedLeafWorkflow[]> RegenerateLeafForInputContractDemandAsync(
        StepExecutionContext parentCtx,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineExtraction extraction,
        GeneratedLeafWorkflow[] leaves,
        PipelineMcpContext pipelineMcpContext,
        PipelineLeafInputContractDemand demand,
        Exception mainValidationException,
        int attempt,
        ITelemetrySpan parentSpan,
        CancellationToken ct)
    {
        var leafIndex = Array.FindIndex(leaves, leaf => string.Equals(leaf.Name, demand.LeafName, StringComparison.Ordinal));
        if (leafIndex < 0)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Cannot repair leaf input contract: generated leaf '{demand.LeafName}' was not found.");

        var spec = extraction.Subworkflows.FirstOrDefault(subworkflow => string.Equals(subworkflow.Name, demand.LeafName, StringComparison.Ordinal))
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Cannot repair leaf input contract: extracted leaf spec '{demand.LeafName}' was not found.");

        using var leafRepairSpan = parentCtx.BeginTelemetrySpan(
            parentSpan,
            "workflow.plan.pipeline.repair_leaf_input_contract",
            "repair_leaf_input_contract",
            new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", demand.LeafName),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.consumer_step", demand.ConsumerStepId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.input_names", string.Join(",", demand.RequiredInputSchemas.Keys.Order(StringComparer.Ordinal))),
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
            });

        var currentLeaf = leaves[leafIndex];
        var previousError = BuildLeafInputContractDemandError(demand, mainValidationException, attempt);
        var repairContext = BuildLeafInputContractDemandRepairContext(demand, currentLeaf);
        var leafInput = BuildLeafPlanInput(
            pipelineInput,
            generator,
            spec,
            currentLeaf.Blueprint,
            currentLeaf.StructuredDecisionRequirements ?? Array.Empty<PipelineStructuredDecisionRequirement>(),
            previousError,
            currentLeaf.Yaml,
            repairContext,
            attempt);
        ApplyPipelineLeafRepairReasoning(
            leafInput,
            GetStringProperty(generator, "reasoning"),
            attempt + 2);
        ForceSinglePlanAttempt(leafInput);

        var leafCtx = new StepExecutionContext
        {
            Step = parentCtx.Step,
            Data = parentCtx.Data,
            Engine = parentCtx.Engine,
            Limits = parentCtx.Limits,
            CallDepth = parentCtx.CallDepth,
            CallStack = new HashSet<string>(parentCtx.CallStack),
            LLMUsageBudget = parentCtx.LLMUsageBudget,
            ExecutionScope = parentCtx.ExecutionScope,
            TelemetrySpan = parentCtx.TelemetrySpan
        };

        var result = await ExecuteSinglePlanAsync(
            leafCtx,
            leafInput,
            ct,
            leafRepairSpan.Span,
            preselectedMcpServers: pipelineMcpContext.Servers.Count > 0
                ? pipelineMcpContext.Servers
                : null) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' input contract repair did not return an object.");
        var yaml = result["yaml"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' input contract repair did not return YAML.");
        var repairedLeaf = PrepareGeneratedLeaf(
            spec,
            yaml,
            currentLeaf.Blueprint,
            currentLeaf.QualityEvents,
            pipelineMcpContext,
            parentCtx.Engine.Registry,
            currentLeaf.StructuredDecisionRequirements);
        EnsureLeafSatisfiesInputContractDemand(repairedLeaf, demand);

        var repairedLeaves = leaves.ToArray();
        repairedLeaves[leafIndex] = repairedLeaf;
        leafRepairSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_input_contract_repair_status", "succeeded");
        return repairedLeaves;
    }

    private async Task<GeneratedLeafWorkflow[]> RegenerateLeafForRuntimeValidationFailureAsync(
        StepExecutionContext parentCtx,
        JsonObject pipelineInput,
        JsonObject generator,
        WorkflowPipelineExtraction extraction,
        GeneratedLeafWorkflow[] leaves,
        PipelineMcpContext pipelineMcpContext,
        string leafName,
        Exception mainValidationException,
        int attempt,
        ITelemetrySpan parentSpan,
        CancellationToken ct)
    {
        var leafIndex = Array.FindIndex(leaves, leaf => string.Equals(leaf.Name, leafName, StringComparison.Ordinal));
        if (leafIndex < 0)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Cannot repair leaf runtime validation: generated leaf '{leafName}' was not found.");

        var spec = extraction.Subworkflows.FirstOrDefault(subworkflow => string.Equals(subworkflow.Name, leafName, StringComparison.Ordinal))
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Cannot repair leaf runtime validation: extracted leaf spec '{leafName}' was not found.");

        using var leafRepairSpan = parentCtx.BeginTelemetrySpan(
            parentSpan,
            "workflow.plan.pipeline.repair_leaf_runtime_validation",
            "repair_leaf_runtime_validation",
            new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.leaf_name", leafName),
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", attempt)
            });

        var currentLeaf = leaves[leafIndex];
        var previousError = BuildStructuredPlanError(mainValidationException, attempt);
        var repairContext = BuildLeafRuntimeValidationRepairContext(leafName);
        var conditionalRepairContext = BuildConditionalActivationRepairContext(mainValidationException);
        if (!string.IsNullOrWhiteSpace(conditionalRepairContext))
            repairContext += Environment.NewLine + Environment.NewLine + conditionalRepairContext;
        var leafInput = BuildLeafPlanInput(
            pipelineInput,
            generator,
            spec,
            currentLeaf.Blueprint,
            currentLeaf.StructuredDecisionRequirements ?? Array.Empty<PipelineStructuredDecisionRequirement>(),
            previousError,
            currentLeaf.Yaml,
            repairContext,
            attempt);
        ApplyPipelineLeafRepairReasoning(
            leafInput,
            GetStringProperty(generator, "reasoning"),
            attempt + 2);
        ForceSinglePlanAttempt(leafInput);

        var leafCtx = new StepExecutionContext
        {
            Step = parentCtx.Step,
            Data = parentCtx.Data,
            Engine = parentCtx.Engine,
            Limits = parentCtx.Limits,
            CallDepth = parentCtx.CallDepth,
            CallStack = new HashSet<string>(parentCtx.CallStack),
            LLMUsageBudget = parentCtx.LLMUsageBudget,
            ExecutionScope = parentCtx.ExecutionScope,
            TelemetrySpan = parentCtx.TelemetrySpan
        };

        var result = await ExecuteSinglePlanAsync(
            leafCtx,
            leafInput,
            ct,
            leafRepairSpan.Span,
            preselectedMcpServers: pipelineMcpContext.Servers.Count > 0
                ? pipelineMcpContext.Servers
                : null) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{leafName}' runtime validation repair did not return an object.");
        var yaml = result["yaml"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{leafName}' runtime validation repair did not return YAML.");
        var repairedLeaf = PrepareGeneratedLeaf(
            spec,
            yaml,
            currentLeaf.Blueprint,
            currentLeaf.QualityEvents,
            pipelineMcpContext,
            parentCtx.Engine.Registry,
            currentLeaf.StructuredDecisionRequirements);

        var repairedLeaves = leaves.ToArray();
        repairedLeaves[leafIndex] = repairedLeaf;
        leafRepairSpan.SetAttribute("gnougo-flow.plan.pipeline.leaf_runtime_validation_repair_status", "succeeded");
        return repairedLeaves;
    }

    private static string BuildLeafRuntimeValidationRepairContext(string leafName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Pipeline leaf runtime-validation repair:");
        sb.AppendLine("- Regenerate only the leaf identified by the structured dry-run failure.");
        sb.AppendLine("- Preserve its public input/output contracts, locked planned tools, selector bindings, conditional activation, and native requirements.");
        sb.AppendLine("- Repair the exact failed step and field from the structured diagnostic; do not change the parent orchestration or unrelated leaves.");
        sb.AppendLine("- An output_schema is a contract, not a cast. Every assigned value must have the declared runtime type on every reachable path.");
        sb.AppendLine("- Use only documented source fields or values proven by control flow. If a success marker is reached only after its required action succeeds, emit a boolean success value instead of assigning an opaque response object or status string to a boolean field.");
        sb.AppendLine("- Do not weaken schemas, invent response fields, add external capabilities, or replace missing values with strings, nulls, environment variables, or fake production data.");
        AppendPromptSection(sb, "failed_leaf", leafName);
        return sb.ToString().TrimEnd();
    }

    private static bool TryGetLeafRepairCycle(Exception exception, out string leafName)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is WorkflowRuntimeException
                {
                    Code: ErrorCodes.WorkflowPlanRepairStalled,
                    Details: JsonObject details
                }
                && string.Equals(GetStringProperty(details, "phase"), "pipeline_leaf_generation", StringComparison.Ordinal)
                && string.Equals(GetStringProperty(details, "repair_scope"), "extraction_or_blueprint", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(GetStringProperty(details, "leaf")))
            {
                leafName = GetStringProperty(details, "leaf")!;
                return true;
            }
        }

        leafName = string.Empty;
        return false;
    }

    private static WorkflowPipelineExtraction AddLeafRepairCycleDiagnostic(
        WorkflowPipelineExtraction extraction,
        string leafName,
        Exception exception)
    {
        var leafIndex = -1;
        for (var index = 0; index < extraction.Subworkflows.Count; index++)
        {
            if (!string.Equals(extraction.Subworkflows[index].Name, leafName, StringComparison.Ordinal))
                continue;
            leafIndex = index;
            break;
        }
        if (leafIndex < 0)
            throw new InvalidOperationException($"Leaf repair cycle referenced unknown extraction leaf '{leafName}'.");
        var diagnostic = new PipelineExtractionQualityDiagnostic(
            "PIPELINE_LEAF_REPAIR_CYCLE",
            "contract_violation",
            "critical",
            leafName,
            $"Leaf generation entered a repeated validation/YAML cycle: {TruncatePipelineQualityMessage(exception.Message, 2_000)}",
            "Repair the affected extraction contract or leaf blueprint instead of retrying the same YAML shape.",
            [new PipelineExtractionQualityEvidence(
                "extraction",
                $"/subworkflows/{leafIndex}/name",
                leafName)],
            EvidenceQualified: true);
        return extraction with
        {
            QualityReview = new PipelineExtractionQualityReview(
                0,
                "retry",
                [diagnostic],
                "Repair the affected extraction contract or leaf blueprint.")
        };
    }

    private static WorkflowRuntimeException? DetectLeafRepairCycle(
        Exception exception,
        string leafName,
        int attempt,
        bool isRepairAttempt,
        string? yaml,
        List<string> diagnosticHistory,
        List<string> yamlStructureHistory)
    {
        if (WorkflowPlanDiagnostics.IsTransientProviderFailure(exception))
        {
            diagnosticHistory.Clear();
            yamlStructureHistory.Clear();
            return null;
        }

        var diagnostic = WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(exception);
        var yamlStructure = BuildLeafYamlStructureFingerprint(yaml);
        var repeatedDiagnosticCycle = isRepairAttempt
                                      && diagnosticHistory.Count >= 2
                                      && !string.Equals(diagnosticHistory[^1], diagnostic, StringComparison.Ordinal)
                                      && diagnosticHistory.Contains(diagnostic, StringComparer.Ordinal);
        var repeatedShapeCycle = repeatedDiagnosticCycle
                                 && yamlStructure.Length > 0
                                 && Enumerable.Range(0, Math.Min(diagnosticHistory.Count, yamlStructureHistory.Count))
                                     .Any(index => string.Equals(diagnosticHistory[index], diagnostic, StringComparison.Ordinal)
                                                   && string.Equals(yamlStructureHistory[index], yamlStructure, StringComparison.Ordinal));

        diagnosticHistory.Add(diagnostic);
        yamlStructureHistory.Add(yamlStructure);
        if (diagnosticHistory.Count > 6)
            diagnosticHistory.RemoveAt(0);
        if (yamlStructureHistory.Count > 6)
            yamlStructureHistory.RemoveAt(0);

        if (!repeatedDiagnosticCycle && !repeatedShapeCycle)
            return null;

        return new WorkflowRuntimeException(
            ErrorCodes.WorkflowPlanRepairStalled,
            "Workflow leaf repair stopped because validation diagnostics or YAML structure entered a repeated cycle. The locked extraction or blueprint must be repaired before another leaf generation attempt. Last validation error: " + exception.Message,
            details: new JsonObject
            {
                ["phase"] = "pipeline_leaf_generation",
                ["repair_scope"] = "extraction_or_blueprint",
                ["leaf"] = leafName,
                ["attempt"] = attempt,
                ["repair_attempts"] = Math.Max(0, attempt - 1),
                ["cycle_kind"] = repeatedShapeCycle ? "diagnostic_and_yaml_structure" : "diagnostic",
                ["diagnostic_fingerprint"] = diagnostic,
                ["yaml_structure_fingerprint"] = yamlStructure,
                ["diagnostic_history"] = new JsonArray(diagnosticHistory
                    .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["yaml_structure_history"] = new JsonArray(yamlStructureHistory
                    .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["last_error"] = JsonNode.Parse(BuildStructuredPlanError(exception, attempt))
            });
    }

    private static string BuildLeafYamlStructureFingerprint(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return string.Empty;

        try
        {
            var signature = new StringBuilder();
            AppendJsonStructureSignature(WorkflowParser.YamlToJson(yaml), signature);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
        }
        catch
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
        }
    }

    private static void AppendJsonStructureSignature(JsonNode? node, StringBuilder signature)
    {
        switch (node)
        {
            case JsonObject obj:
                signature.Append('{');
                foreach (var (name, value) in obj.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    signature.Append(name).Append(':');
                    AppendJsonStructureSignature(value, signature);
                }
                signature.Append('}');
                break;
            case JsonArray array:
                signature.Append('[');
                foreach (var value in array)
                    AppendJsonStructureSignature(value, signature);
                signature.Append(']');
                break;
            case JsonValue value when value.TryGetValue<bool>(out _):
                signature.Append("bool;");
                break;
            case JsonValue value when value.TryGetValue<decimal>(out _):
                signature.Append("number;");
                break;
            case JsonValue:
                signature.Append("string;");
                break;
            default:
                signature.Append("null;");
                break;
        }
    }

    private static string? TryGetPipelineLeafRuntimeValidationFailure(
        Exception exception,
        IReadOnlyCollection<string> leafNames)
    {
        if (leafNames.Count == 0)
            return null;

        var names = leafNames.ToHashSet(StringComparer.Ordinal);
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not WorkflowRuntimeException { Details: JsonObject details }
                || !string.Equals(GetStringProperty(details, "phase"), "dry_run", StringComparison.Ordinal)
                || details["diagnostics"] is not JsonArray diagnostics)
            {
                continue;
            }

            foreach (var diagnostic in diagnostics.OfType<JsonObject>())
            {
                if (!string.Equals(GetStringProperty(diagnostic, "phase"), "dry_run", StringComparison.Ordinal)
                    || diagnostic["runtime_details"] is not JsonObject runtimeDetails)
                {
                    continue;
                }

                var failedWorkflow = GetStringProperty(runtimeDetails, "failed_workflow")
                                     ?? GetStringProperty(runtimeDetails, "workflow");
                if (!string.IsNullOrWhiteSpace(failedWorkflow) && names.Contains(failedWorkflow))
                    return failedWorkflow;
            }
        }

        return null;
    }

    private static string? TryGetConditionalActivationLeafTopologyFailure(
        Exception exception,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not WorkflowRuntimeException { Details: JsonObject details }
                || !string.Equals(
                    GetStringProperty(details, "reason"),
                    "conditional_activation_invalid",
                    StringComparison.Ordinal)
                || !string.Equals(
                    GetStringProperty(details, "repair_scope"),
                    "leaf_topology",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var workflow = GetStringProperty(details, "workflow");
            if (string.IsNullOrWhiteSpace(workflow))
                return null;
            var leaf = leaves.FirstOrDefault(candidate =>
                string.Equals(candidate.GeneratedWorkflowName, workflow, StringComparison.Ordinal)
                || string.Equals(candidate.Name, workflow, StringComparison.Ordinal));
            return leaf?.Name;
        }

        return null;
    }

    private static void ForceSinglePlanAttempt(JsonObject planInput)
    {
        if (planInput["validate"] is JsonObject validate)
            validate["max_repair_attempts"] = 1;
        planInput["on_invalid"] = new JsonObject { ["action"] = "fail", ["max_attempts"] = 1 };
    }

    private static void ApplyPipelineLeafRepairReasoning(
        JsonObject planInput,
        string? configuredReasoning,
        int attempt)
    {
        if (planInput["generator"] is not JsonObject generator)
            return;

        var resolved = ResolvePipelinePatchReasoning(configuredReasoning, attempt);
        if (!string.IsNullOrWhiteSpace(resolved))
            generator["reasoning"] = resolved;
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

    private static string BuildLeafInputContractDemandError(
        PipelineLeafInputContractDemand demand,
        Exception mainValidationException,
        int attempt)
    {
        var details = BuildLeafInputContractDemandJson(demand);
        details["main_validation_error"] = JsonNode.Parse(BuildStructuredPlanError(mainValidationException, attempt));
        return new JsonObject
        {
            ["attempt"] = attempt,
            ["code"] = "PIPELINE_LEAF_INPUT_CONTRACT_DEMAND",
            ["message"] = $"Final main validation requires leaf '{demand.LeafName}' to accept authoritative parent-produced input contracts.",
            ["pipeline_leaf_input_contract_demand"] = details
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
        sb.AppendLine("- An output declaration does not cast its expression. Repair the value-producing expression or add one typed projection so the value is assignable to the complete declared output contract.");
        sb.AppendLine("- For object outputs, map every required target property from an exact documented source property, preserving the source property's declared spelling and type. Do not bind a closed target object to a whole response that lacks one of its required fields.");
        sb.AppendLine("- If the discovered source contract cannot prove a required field, do not invent a value or claim the field exists; retain fail-closed validation.");
        sb.AppendLine("- If the demanded output is an array, declare `type: array` with concrete `items.properties` for every required item field.");
        sb.AppendLine("- If the demanded output is an object, declare concrete `properties` for every required field.");
        if (IsExternalArtifactProvenanceDemand(demand))
        {
            sb.AppendLine("- This demand is about artifact provenance, not only schema shape.");
            sb.AppendLine("- Bind the demanded output directly to the exact external/action response field that created or proved the artifact, or to an exact caller input. A transparent one-field `set` alias is allowed.");
            sb.AppendLine("- Do not obtain the demanded artifact locator from a custom function, aggregate helper, cast, string template, or invented literal, even when such a helper receives the real locator as an argument.");
        }
        sb.AppendLine();
        AppendPromptSection(sb, "pipeline_leaf_contract_demand", BuildLeafContractDemandJson(demand).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "current_leaf_output_schema_yaml", SerializeYamlMapping(new Dictionary<string, JsonNode?>
        {
            [demand.OutputName] = currentOutputSchema?.DeepClone()
        }));
        AppendPromptSection(sb, "required_output_schema_guidance_yaml", BuildLeafContractDemandSchemaGuidanceYaml(demand));
        return sb.ToString().TrimEnd();
    }

    private static string BuildLeafInputContractDemandRepairContext(
        PipelineLeafInputContractDemand demand,
        GeneratedLeafWorkflow currentLeaf)
    {
        var currentInputs = BuildLeafInputSchemaMap(currentLeaf);
        var demandedSchemas = demand.RequiredInputSchemas.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value?.DeepClone(),
            StringComparer.Ordinal);
        var currentDemandedInputs = demand.RequiredInputSchemas.Keys
            .ToDictionary(
                static name => name,
                name => currentInputs.TryGetValue(name, out var schema) ? schema?.DeepClone() : null,
                StringComparer.Ordinal);

        var sources = new JsonObject();
        foreach (var (inputName, sourceExpression) in demand.SourceExpressions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            sources[inputName] = sourceExpression;

        var sb = new StringBuilder();
        sb.AppendLine("Pipeline leaf input contract demand:");
        sb.AppendLine("- Preserve the original leaf goal, public outputs, planned tool calls, and implementation intent.");
        sb.AppendLine("- Regenerate only this consuming leaf workflow.");
        sb.AppendLine("- For every named input below, the authoritative parent source contract must be assignable to the regenerated leaf input contract.");
        sb.AppendLine("- Copy the authoritative schema when possible. A broader concrete contract is allowed only when it does not require properties or item fields that the source does not guarantee.");
        sb.AppendLine("- Do not use `any`, omit array item schemas, or add required nested fields absent from the authoritative source contract.");
        sb.AppendLine("- Adapt the leaf's internal deterministic shaping or parsing to the accepted input shape; do not move the leaf's work into the parent workflow.");
        sb.AppendLine("- Do not introduce new public inputs, tools, external work, or use-case-specific assumptions.");
        sb.AppendLine();
        AppendPromptSection(sb, "pipeline_leaf_input_contract_demand", BuildLeafInputContractDemandJson(demand).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "authoritative_parent_input_contracts_yaml", SerializeYamlMapping(demandedSchemas));
        AppendPromptSection(sb, "current_leaf_input_contracts_yaml", SerializeYamlMapping(currentDemandedInputs));
        AppendPromptSection(sb, "authoritative_parent_source_expressions_json", sources.ToJsonString(PromptJsonOptions));
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

    private static JsonObject BuildLeafInputContractDemandJson(PipelineLeafInputContractDemand demand)
    {
        var schemas = new JsonObject();
        foreach (var (inputName, schema) in demand.RequiredInputSchemas.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            schemas[inputName] = schema?.DeepClone();

        var sources = new JsonObject();
        foreach (var (inputName, sourceExpression) in demand.SourceExpressions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            sources[inputName] = sourceExpression;

        return new JsonObject
        {
            ["leaf"] = demand.LeafName,
            ["consumer_step"] = demand.ConsumerStepId,
            ["reason"] = demand.Reason,
            ["required_input_schemas"] = schemas,
            ["source_expressions"] = sources
        };
    }

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
        if (IsExternalArtifactProvenanceDemand(demand)
            && !WorkflowPlanPipelineQualityAnalyzer.IsLeafArtifactOutputProven(
                leaf.Document,
                leaf.GeneratedWorkflowName,
                demand.OutputName,
                out var provenanceFailure))
        {
            errors.Add(provenanceFailure ?? $"output '{demand.OutputName}' must preserve external artifact provenance");
        }
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

    private static void EnsureLeafSatisfiesInputContractDemand(
        GeneratedLeafWorkflow leaf,
        PipelineLeafInputContractDemand demand)
    {
        var inputs = BuildLeafInputSchemaMap(leaf);
        var errors = new List<string>();
        foreach (var (inputName, sourceSchema) in demand.RequiredInputSchemas.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!inputs.TryGetValue(inputName, out var targetSchema))
            {
                errors.Add($"input '{inputName}' is missing");
                continue;
            }

            var sourceDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(sourceSchema);
            var targetDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(targetSchema);
            if (WorkflowPlanContractNormalizer.IsWeakDescriptor(targetDescriptor))
            {
                errors.Add($"input '{inputName}' must declare a strong concrete schema");
                continue;
            }

            var issue = sourceDescriptor.FindAssignmentIssue(targetDescriptor);
            if (issue != null)
                errors.Add($"input '{inputName}' is not compatible with its parent source contract: {issue.Message}");
        }

        if (errors.Count == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{demand.LeafName}' input contract repair did not satisfy parent contract demand: {string.Join("; ", errors)}.",
            details: new JsonObject { ["pipeline_leaf_input_contract_demand"] = BuildLeafInputContractDemandJson(demand) });
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

    private static PipelineLeafInputContractDemand? TryAnalyzePipelineLeafInputContractDemand(
        Exception exception,
        WorkflowDocument? assembledDocument,
        IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        if (assembledDocument == null || !assembledDocument.Workflows.TryGetValue("main", out var main))
            return null;

        var leafNames = leaves.Select(static leaf => leaf.Name).ToHashSet(StringComparer.Ordinal);
        string? demandedLeaf = null;
        string? consumerStepId = null;
        var schemas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var diagnostic in EnumeratePipelineDiagnostics(exception))
        {
            if (!string.Equals(GetStringProperty(diagnostic, "code"), ErrorCodes.ExprTypeMismatch, StringComparison.Ordinal))
                continue;

            var workflow = GetStringProperty(diagnostic, "workflow");
            var stepId = GetStringProperty(diagnostic, "step");
            var field = GetStringProperty(diagnostic, "field") ?? "";
            var invalidPath = GetStringProperty(diagnostic, "invalid_path") ?? "";
            const string argumentPrefix = "input.args.";
            if (!string.Equals(workflow, "main", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(stepId)
                || !field.StartsWith(argumentPrefix, StringComparison.Ordinal)
                || field.Length == argumentPrefix.Length)
            {
                continue;
            }

            var inputName = field[argumentPrefix.Length..];
            if (inputName.Contains('.', StringComparison.Ordinal)
                || !IdentifierRegex().IsMatch(inputName)
                || !TryGetMainLeafCall(main, leafNames, stepId, out var consumerLeaf)
                || !TryResolveMainSourceContract(main, leaves, invalidPath, out var sourceSchema))
            {
                continue;
            }

            var consumer = leaves.FirstOrDefault(leaf => string.Equals(leaf.Name, consumerLeaf, StringComparison.Ordinal));
            if (consumer == null
                || !BuildLeafInputSchemaMap(consumer).TryGetValue(inputName, out var targetSchema))
            {
                continue;
            }

            var sourceDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(sourceSchema);
            var targetDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(targetSchema);
            if (sourceDescriptor.IsOpaque
                || sourceDescriptor.FindAssignmentIssue(targetDescriptor) == null)
            {
                continue;
            }

            if (demandedLeaf != null
                && (!string.Equals(demandedLeaf, consumerLeaf, StringComparison.Ordinal)
                    || !string.Equals(consumerStepId, stepId, StringComparison.Ordinal)))
            {
                continue;
            }

            demandedLeaf = consumerLeaf;
            consumerStepId = stepId;
            schemas[inputName] = sourceSchema.DeepClone();
            sources[inputName] = invalidPath;
        }

        return demandedLeaf == null || consumerStepId == null || schemas.Count == 0
            ? null
            : new PipelineLeafInputContractDemand(
                demandedLeaf,
                consumerStepId,
                ErrorCodes.ExprTypeMismatch,
                schemas,
                sources);
    }

    private static bool TryResolveMainSourceContract(
        WorkflowDef main,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonNode? schema)
    {
        schema = null;
        var path = TrimWorkflowExpression(expression);
        const string inputPrefix = "data.inputs.";
        if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
        {
            var segments = path[inputPrefix.Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0 || main.Inputs == null || !main.Inputs.TryGetValue(segments[0], out var input))
                return false;

            var descriptor = FlowTypeDescriptorConverter.FromInputDef(input);
            var resolved = segments.Length == 1 ? descriptor : descriptor.ResolvePath(segments.Skip(1).ToArray());
            return TryBuildStrongInputContract(resolved, out schema);
        }

        const string stepPrefix = "data.steps.";
        if (!path.StartsWith(stepPrefix, StringComparison.Ordinal))
            return false;

        var stepSegments = path[stepPrefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (stepSegments.Length < 2)
            return false;

        var step = EnumerateSteps(main.Steps)
            .Concat(EnumerateSteps(main.Finally))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, stepSegments[0], StringComparison.Ordinal));
        if (step == null)
            return false;

        FlowTypeDescriptor? sourceDescriptor = null;
        if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            && stepSegments.Length >= 3
            && string.Equals(stepSegments[1], "outputs", StringComparison.Ordinal)
            && ReadWorkflowCallRefNameFromInput(step) is { } leafName)
        {
            var producer = leaves.FirstOrDefault(leaf => string.Equals(leaf.Name, leafName, StringComparison.Ordinal));
            if (producer == null
                || !BuildLeafOutputSchemaMap(producer).TryGetValue(stepSegments[2], out var outputSchema))
            {
                return false;
            }

            sourceDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(outputSchema);
            if (stepSegments.Length > 3)
                sourceDescriptor = sourceDescriptor.ResolvePath(stepSegments.Skip(3).ToArray());
        }
        else if (step.OutputSchema != null)
        {
            sourceDescriptor = FlowTypeDescriptorConverter.FromJsonSchema(step.OutputSchema)
                .ResolvePath(stepSegments.Skip(1).ToArray());
        }

        return TryBuildStrongInputContract(sourceDescriptor, out schema);
    }

    private static bool TryBuildStrongInputContract(
        FlowTypeDescriptor? descriptor,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonNode? schema)
    {
        schema = null;
        if (descriptor == null || descriptor.IsOpaque || WorkflowPlanContractNormalizer.IsWeakDescriptor(descriptor))
            return false;

        schema = FlowTypeDescriptorConverter.ToWorkflowContractNode(
            descriptor,
            inputStyle: true,
            allowScalarShortForm: false);
        return true;
    }

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
            if (string.Equals(
                    code,
                    WorkflowPlanPipelineQualityAnalyzer.UnprovenExternalArtifactCode,
                    StringComparison.Ordinal)
                && TryBuildExternalArtifactProducerDemand(
                    main,
                    leafNames,
                    diagnostic,
                    out var artifactProducerDemand))
            {
                demands.Add(artifactProducerDemand);
                continue;
            }

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
            {
                if (TryBuildLeafInternalOutputDemand(
                        assembledDocument,
                        leaves,
                        workflow,
                        diagnostic,
                        out var leafInternalDemand))
                {
                    demands.Add(leafInternalDemand);
                }
                continue;
            }

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

    private static bool TryBuildLeafInternalOutputDemand(
        WorkflowDocument assembledDocument,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        string workflowName,
        JsonObject diagnostic,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineLeafContractDemand? demand)
    {
        demand = null;
        var classification = ClassifyLeafInternalOutputMismatch(
            assembledDocument,
            workflowName,
            diagnostic);
        if (!classification.HasValue)
            return false;

        var leaf = leaves.FirstOrDefault(candidate =>
            MatchesGeneratedLeafWorkflow(candidate.Name, candidate.GeneratedWorkflowName, workflowName));
        if (leaf == null)
            return false;

        var field = GetStringProperty(diagnostic, "field") ?? "";
        demand = new PipelineLeafContractDemand(
            leaf.Name,
            classification.Value.OutputName,
            ConsumerStepId: "",
            ConsumerField: field,
            InvalidPath: GetStringProperty(diagnostic, "invalid_path") ?? "",
            Reason: ErrorCodes.ExprTypeMismatch,
            RequiredOutputPaths: Array.Empty<string>(),
            ExpectedType: classification.Value.ExpectedType);
        return true;
    }

    internal static bool MatchesGeneratedLeafWorkflow(
        string leafName,
        string generatedWorkflowName,
        string composedWorkflowName)
        => string.Equals(leafName, composedWorkflowName, StringComparison.Ordinal)
           || string.Equals(generatedWorkflowName, composedWorkflowName, StringComparison.Ordinal);

    internal static (string OutputName, string ExpectedType)? ClassifyLeafInternalOutputMismatch(
        WorkflowDocument assembledDocument,
        string workflowName,
        JsonObject diagnostic)
    {
        if (!string.Equals(GetStringProperty(diagnostic, "code"), ErrorCodes.ExprTypeMismatch, StringComparison.Ordinal)
            || !assembledDocument.Workflows.TryGetValue(workflowName, out var workflow)
            || workflow.Outputs == null)
        {
            return null;
        }

        var field = GetStringProperty(diagnostic, "field") ?? "";
        const string outputPrefix = "outputs.";
        if (!field.StartsWith(outputPrefix, StringComparison.Ordinal))
            return null;

        var outputName = field[outputPrefix.Length..].Split('.', 2)[0];
        if (string.IsNullOrWhiteSpace(outputName)
            || !workflow.Outputs.TryGetValue(outputName, out var output))
        {
            return null;
        }

        return (outputName, FlowTypeDescriptorConverter.FromOutputDef(output).Describe());
    }

    private static bool TryBuildExternalArtifactProducerDemand(
        WorkflowDef main,
        IReadOnlySet<string> leafNames,
        JsonObject diagnostic,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineLeafContractDemand? demand)
    {
        demand = null;
        var consumerLeaf = GetStringProperty(diagnostic, "workflow");
        var invalidAssignment = GetStringProperty(diagnostic, "invalid_assignment") ?? "";
        if (string.IsNullOrWhiteSpace(consumerLeaf)
            || string.Equals(consumerLeaf, "main", StringComparison.Ordinal)
            || !leafNames.Contains(consumerLeaf)
            || !TryParseExactPipelineInputReference(invalidAssignment, out var consumerInputName))
        {
            return false;
        }

        PipelineLeafContractDemand? resolved = null;
        foreach (var consumerCall in EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally)))
        {
            if (!string.Equals(consumerCall.Type, "workflow.call", StringComparison.Ordinal)
                || !string.Equals(ReadWorkflowCallRefNameFromInput(consumerCall), consumerLeaf, StringComparison.Ordinal))
            {
                continue;
            }

            if (consumerCall.Input?["args"] is not JsonObject args
                || !args.TryGetPropertyValue(consumerInputName, out var argumentNode)
                || argumentNode is not JsonValue argumentValue
                || !argumentValue.TryGetValue<string>(out var argument)
                || !TryParseStepOutputReference(argument, out var producerCallId, out var outputName, out var remainingPath)
                || remainingPath.Count != 0
                || !TryGetMainLeafCall(main, leafNames, producerCallId, out var producerLeaf))
            {
                return false;
            }

            var candidate = new PipelineLeafContractDemand(
                producerLeaf,
                outputName,
                consumerCall.Id,
                GetStringProperty(diagnostic, "field") ?? consumerInputName,
                argument,
                WorkflowPlanPipelineQualityAnalyzer.UnprovenExternalArtifactCode,
                Array.Empty<string>(),
                "string");
            if (resolved != null
                && (!string.Equals(resolved.LeafName, candidate.LeafName, StringComparison.Ordinal)
                    || !string.Equals(resolved.OutputName, candidate.OutputName, StringComparison.Ordinal)))
            {
                return false;
            }

            resolved = candidate;
        }

        demand = resolved;
        return demand != null;
    }

    private static bool TryParseExactPipelineInputReference(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? inputName)
    {
        inputName = null;
        var path = TrimWorkflowExpression(expression);
        const string prefix = "data.inputs.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var suffix = path[prefix.Length..];
        if (string.IsNullOrWhiteSpace(suffix)
            || suffix.Contains('.', StringComparison.Ordinal)
            || !IdentifierRegex().IsMatch(suffix))
        {
            return false;
        }

        inputName = suffix;
        return true;
    }

    private static bool IsExternalArtifactProvenanceDemand(PipelineLeafContractDemand demand)
        => demand.Reason.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(reason => string.Equals(
                reason,
                WorkflowPlanPipelineQualityAnalyzer.UnprovenExternalArtifactCode,
                StringComparison.Ordinal));

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
        foreach (var step in EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally)))
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
        var step = EnumerateSteps(main.Steps)
            .Concat(EnumerateSteps(main.Finally))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.Ordinal));
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

    private static GeneratedLeafWorkflow PrepareGeneratedLeaf(
        WorkflowPipelineSubworkflowSpec spec,
        string yaml,
        PipelineLeafBlueprint blueprint,
        IReadOnlyList<PipelineQualityEvent> qualityEvents,
        PipelineMcpContext pipelineMcpContext,
        StepExecutorRegistry registry,
        IReadOnlyList<PipelineStructuredDecisionRequirement>? structuredDecisionRequirements = null)
    {
        var doc = ParseAndValidateGeneratedWorkflow(yaml);
        if (doc.Workflows.Count != 1)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Leaf workflow '{spec.Name}' must generate exactly one workflow.");

        var workflowName = doc.Workflows.Keys.Single();
        (doc, yaml) = NormalizeGeneratedLeafInputAliases(spec, workflowName, doc, yaml);
        (doc, yaml) = NormalizeGeneratedLeafConditionalDecisionInputs(spec, workflowName, doc, yaml);
        (doc, yaml) = MaterializeMissingPlannedMcpCallVariants(spec, workflowName, doc, yaml);
        (doc, yaml, _) = NormalizeGeneratedEvidenceBackedLeafOutputProjections(
            doc,
            yaml,
            pipelineMcpContext,
            registry);
        (doc, yaml) = PromoteLeafOutputSchemasFromDirectSources(spec, workflowName, doc, yaml, pipelineMcpContext);
        ValidateKnownLeafOutputAssignments(
            doc,
            BuildMcpToolOutputContracts(pipelineMcpContext.Servers),
            registry.GetContracts());
        var workflow = doc.Workflows[workflowName];
        EnforceStrongObjectSchemas(spec.Name, doc);
        EnforceStrongArrayOutputSchemas(spec.Name, spec, workflowName, doc);
        EnforcePlannedMcpToolsUsed(spec, workflow);
        ValidateConditionalCapabilityTopologyInLeaf(spec, workflowName, doc);
        EnforcePlannedNativeStepsUsed(spec, workflow);
        EnforceStructuredDecisionProducerContracts(
            spec,
            workflowName,
            doc,
            structuredDecisionRequirements ?? Array.Empty<PipelineStructuredDecisionRequirement>());
        EnforcePipelineLeafIntent(spec, workflow);
        EnforceLeafBlueprintImplemented(spec, blueprint, workflow);
        foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
        {
            if (step.Type is "workflow.call" or "workflow.plan")
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy, $"Leaf workflow '{spec.Name}' must not contain step type '{step.Type}'.");
        }

        return new GeneratedLeafWorkflow(
            spec.Name,
            workflowName,
            doc,
            yaml,
            blueprint,
            qualityEvents.ToArray(),
            structuredDecisionRequirements);
    }

    private sealed record EvidenceBackedLeafOutputProjection(
        string WorkflowName,
        string OutputName,
        string? DirectExpression,
        string? ProjectionStepId,
        string SourceExpression,
        IReadOnlyDictionary<string, string>? PropertyExpressions);

    private static (WorkflowDocument Document, string Yaml, int ReplacementCount)
        NormalizeGeneratedEvidenceBackedLeafOutputProjections(
            WorkflowDocument document,
            string yaml,
            PipelineMcpContext pipelineMcpContext,
            StepExecutorRegistry registry)
        => NormalizeGeneratedEvidenceBackedLeafOutputProjections(
            document,
            yaml,
            BuildMcpToolOutputContracts(pipelineMcpContext.Servers),
            registry);

    internal static (WorkflowDocument Document, string Yaml, int ReplacementCount)
        NormalizeGeneratedEvidenceBackedLeafOutputProjections(
            WorkflowDocument document,
            string yaml,
            IReadOnlyList<McpToolOutputContract> discoveredContracts,
            StepExecutorRegistry registry)
    {
        var mcpContracts = discoveredContracts
            .ToDictionary(
                static contract => (contract.ServerName, contract.ToolName),
                static contract => contract,
                EqualityComparer<(string ServerName, string ToolName)>.Default);
        var stepContracts = registry.GetContracts();
        var projections = new List<EvidenceBackedLeafOutputProjection>();

        foreach (var (workflowName, workflow) in document.Workflows)
        {
            if (workflow.Outputs == null || workflow.Outputs.Count == 0)
                continue;

            var stepOutputTypes = BuildLeafStepOutputTypeMap(
                workflowName,
                document,
                workflow,
                mcpContracts,
                stepContracts);
            var allStepIds = EnumerateSteps(workflow.Steps)
                .Concat(EnumerateSteps(workflow.Finally))
                .Select(static step => step.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (outputName, output) in workflow.Outputs)
            {
                if (!TryResolveDirectLeafOutputSource(output.Expr, stepOutputTypes, out var sourceType)
                    || !TryParseDirectStepExpression(output.Expr, out var sourceStepId, out var sourceExpression)
                    || sourceType.Kind != FlowTypeKind.Object
                    || sourceType.Properties.Count == 0
                    || workflow.Steps.FirstOrDefault(step => string.Equals(step.Id, sourceStepId, StringComparison.Ordinal)) is not { If: null })
                {
                    continue;
                }

                var targetType = FlowTypeDescriptorConverter.FromOutputDef(output);
                if (sourceType.FindAssignmentIssue(targetType) == null)
                    continue;

                if (targetType.Kind != FlowTypeKind.Object)
                {
                    var sourceProperty = FindEvidenceBackedProjectionProperty(
                        outputName,
                        targetType,
                        sourceType.Properties,
                        allowTargetSuffix: true);
                    if (sourceProperty == null)
                        continue;

                    projections.Add(new EvidenceBackedLeafOutputProjection(
                        workflowName,
                        outputName,
                        DirectExpression: $"${{{sourceExpression}.{sourceProperty}}}",
                        ProjectionStepId: null,
                        sourceExpression,
                        PropertyExpressions: null));
                    continue;
                }

                if (targetType.Properties.Count == 0)
                    continue;

                var propertyExpressions = new Dictionary<string, string>(StringComparer.Ordinal);
                var complete = true;
                foreach (var (targetPropertyName, targetProperty) in targetType.Properties)
                {
                    var sourceProperty = FindEvidenceBackedProjectionProperty(
                        targetPropertyName,
                        targetProperty.Type,
                        sourceType.Properties,
                        allowTargetSuffix: false,
                        requireSourceProperty: targetProperty.Required);
                    if (sourceProperty == null)
                    {
                        if (targetProperty.Required)
                        {
                            complete = false;
                            break;
                        }
                        continue;
                    }

                    propertyExpressions[targetPropertyName] = $"${{{sourceExpression}.{sourceProperty}}}";
                }

                if (!complete || propertyExpressions.Count == 0)
                    continue;

                var projectionStepId = MakeUniqueBlueprintStepId(
                    "project_output_" + SanitizeBlueprintIdentifier(outputName),
                    allStepIds);
                projections.Add(new EvidenceBackedLeafOutputProjection(
                    workflowName,
                    outputName,
                    DirectExpression: null,
                    projectionStepId,
                    sourceExpression,
                    propertyExpressions));
            }
        }

        if (projections.Count == 0)
            return (document, yaml, 0);

        var root = LoadYamlRoot(yaml);
        var workflows = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated YAML is missing workflows.");
        var replacementCount = 0;
        foreach (var projection in projections)
        {
            if (!document.Workflows.TryGetValue(projection.WorkflowName, out var workflow)
                || workflow.Outputs == null
                || !workflow.Outputs.TryGetValue(projection.OutputName, out var output)
                || !workflows.Children.TryGetValue(Scalar(projection.WorkflowName), out var workflowNode)
                || workflowNode is not YamlMappingNode workflowMap
                || workflowMap.GetMapping("outputs") is not { } outputs
                || !outputs.Children.TryGetValue(Scalar(projection.OutputName), out var outputNode)
                || outputNode is not YamlMappingNode outputMap)
            {
                continue;
            }

            if (projection.DirectExpression != null)
            {
                ReplaceYaml(outputMap, "expr", Scalar(projection.DirectExpression));
                replacementCount++;
                continue;
            }

            if (projection.ProjectionStepId == null || projection.PropertyExpressions == null)
                continue;

            var targetSchema = BuildPromotedLeafOutputYaml(
                output,
                FlowTypeDescriptorConverter.FromOutputDef(output),
                includeExpr: false);
            var projectionProperties = new YamlMappingNode();
            foreach (var (propertyName, expression) in projection.PropertyExpressions)
                AddYaml(projectionProperties, propertyName, Scalar(expression));

            var wrappedProperties = new YamlMappingNode();
            AddYaml(wrappedProperties, "value", targetSchema);
            var outputSchema = new YamlMappingNode();
            AddYaml(outputSchema, "type", Scalar("object"));
            AddYaml(outputSchema, "properties", wrappedProperties);
            AddYaml(outputSchema, "required_properties", new YamlSequenceNode(Scalar("value")));

            var input = new YamlMappingNode();
            AddYaml(input, "value", projectionProperties);
            var projectionStep = new YamlMappingNode();
            AddYaml(projectionStep, "id", Scalar(projection.ProjectionStepId));
            AddYaml(projectionStep, "type", Scalar("set"));
            AddYaml(projectionStep, "output_schema", outputSchema);
            AddYaml(projectionStep, "input", input);

            var steps = workflowMap.GetSequence("steps") ?? new YamlSequenceNode();
            steps.Add(projectionStep);
            if (!ContainsYamlKey(workflowMap, "steps"))
                AddYaml(workflowMap, "steps", steps);
            ReplaceYaml(
                outputMap,
                "expr",
                Scalar($"${{data.steps.{projection.ProjectionStepId}.value}}"));
            replacementCount++;
        }

        if (replacementCount == 0)
            return (document, yaml, 0);

        var normalizedYaml = SerializeYamlNode(root);
        return (ParseAndValidateGeneratedWorkflow(normalizedYaml), normalizedYaml, replacementCount);
    }

    private static string? FindEvidenceBackedProjectionProperty(
        string targetName,
        FlowTypeDescriptor targetType,
        IReadOnlyDictionary<string, FlowPropertyDescriptor> sourceProperties,
        bool allowTargetSuffix,
        bool requireSourceProperty = true)
    {
        var targetTokens = TokenizeProjectionContractName(targetName);
        var candidates = sourceProperties
            .Where(pair => (!requireSourceProperty || pair.Value.Required)
                           && pair.Value.Type.FindAssignmentIssue(targetType) == null)
            .Select(pair => new
            {
                Name = pair.Key,
                Score = ScoreProjectionPropertyName(
                    targetTokens,
                    TokenizeProjectionContractName(pair.Key),
                    allowTargetSuffix)
            })
            .Where(static candidate => candidate.Score.HasValue)
            .OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var bestScore = candidates[0].Score;
        return candidates.Count(candidate => candidate.Score == bestScore) == 1
            ? candidates[0].Name
            : null;
    }

    private static int? ScoreProjectionPropertyName(
        IReadOnlyList<string> targetTokens,
        IReadOnlyList<string> sourceTokens,
        bool allowTargetSuffix)
    {
        if (targetTokens.Count == 0 || sourceTokens.Count == 0)
            return null;
        if (targetTokens.SequenceEqual(sourceTokens, StringComparer.Ordinal))
            return 0;
        if (allowTargetSuffix
            && targetTokens.Count > sourceTokens.Count
            && targetTokens.Skip(targetTokens.Count - sourceTokens.Count).SequenceEqual(sourceTokens, StringComparer.Ordinal))
        {
            return 1;
        }
        return null;
    }

    private static string[] TokenizeProjectionContractName(string name)
        => Regex.Matches(
                name,
                @"[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|[0-9]+",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();

    private static bool TryParseDirectStepExpression(
        string expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? stepId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sourceExpression)
    {
        stepId = null;
        sourceExpression = null;
        var path = TrimWorkflowExpression(expression);
        const string prefix = "data.steps.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var suffix = path[prefix.Length..];
        var separator = suffix.IndexOf('.');
        if (separator <= 0 || separator >= suffix.Length - 1)
            return false;

        stepId = suffix[..separator];
        sourceExpression = path;
        return IdentifierRegex().IsMatch(stepId);
    }

    internal static void ValidateKnownLeafOutputAssignments(
        WorkflowDocument document,
        IReadOnlyList<McpToolOutputContract> mcpToolContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        try
        {
            WorkflowPlanSemanticValidator.ValidateWithStepContracts(
                document,
                mcpToolContracts,
                stepContracts);
        }
        catch (WorkflowSemanticValidationException ex)
        {
            var evidenceBackedOutputMismatches = ex.Errors
                .Where(static error => string.Equals(error.Code, ErrorCodes.ExprTypeMismatch, StringComparison.Ordinal)
                                       && error.StepId == null
                                       && error.Field.StartsWith("outputs.", StringComparison.Ordinal))
                .ToArray();
            if (evidenceBackedOutputMismatches.Length > 0)
                throw new WorkflowSemanticValidationException(evidenceBackedOutputMismatches);
        }
    }

    private static (WorkflowDocument Document, string Yaml) NormalizeGeneratedLeafInputAliases(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument document,
        string yaml)
    {
        if (!document.Workflows.TryGetValue(workflowName, out var workflow)
            || workflow.Inputs == null
            || workflow.Inputs.Count == 0
            || spec.InputSchemas.Count == 0)
        {
            return (document, yaml);
        }

        var declaredByCanonicalName = spec.InputSchemas.Keys
            .GroupBy(CanonicalizeContractAliasName, StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.Ordinal);
        var generatedSchemas = BuildInputSchemaMap(workflow.Inputs);
        var aliases = new List<(string Alias, string Declared)>();
        foreach (var (inputName, aliasSchema) in generatedSchemas)
        {
            if (spec.InputSchemas.ContainsKey(inputName)
                || !declaredByCanonicalName.TryGetValue(CanonicalizeContractAliasName(inputName), out var declaredName)
                || string.Equals(inputName, declaredName, StringComparison.Ordinal))
            {
                continue;
            }

            var declaredSchema = generatedSchemas.TryGetValue(declaredName, out var generatedDeclaredSchema)
                ? generatedDeclaredSchema
                : spec.InputSchemas[declaredName];
            if (!AreEquivalentLeafInputSchemas(aliasSchema, declaredSchema))
                continue;

            aliases.Add((inputName, declaredName));
        }

        if (aliases.Count == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        if (root.GetMapping("workflows") is not { } workflows
            || !workflows.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
            || workflowNode is not YamlMappingNode workflowMap
            || workflowMap.GetMapping("inputs") is not { } workflowInputs)
        {
            return (document, yaml);
        }

        var skillInputs = root.GetMapping("skill")?.GetMapping("inputs");
        foreach (var (alias, declaredName) in aliases)
        {
            RewriteGeneratedLeafInputReferences(root, alias, declaredName);
            RenameOrRemoveGeneratedLeafInput(workflowInputs, alias, declaredName);
            if (skillInputs != null)
                RenameOrRemoveGeneratedLeafInput(skillInputs, alias, declaredName);
        }

        var normalizedYaml = SerializeYamlNode(root);
        return (ParseAndValidateGeneratedWorkflow(normalizedYaml), normalizedYaml);
    }

    private static (WorkflowDocument Document, string Yaml) NormalizeGeneratedLeafConditionalDecisionInputs(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument document,
        string yaml)
    {
        (document, yaml) = NormalizeGeneratedLeafConditionalDecisionInputAliases(
            spec,
            workflowName,
            document,
            yaml);
        if (!document.Workflows.TryGetValue(workflowName, out var workflow)
            || workflow.Inputs == null
            || workflow.Inputs.Count == 0)
        {
            return (document, yaml);
        }

        var allSteps = EnumerateSteps(workflow.Steps)
            .Concat(EnumerateSteps(workflow.Finally))
            .ToArray();
        var switches = allSteps
            .Where(static step => string.Equals(step.Type, "switch", StringComparison.Ordinal)
                                  && !string.IsNullOrWhiteSpace(step.Id))
            .ToArray();
        var rewrites = new List<(string SwitchId, string Expression)>();

        foreach (var group in spec.PlannedTools
                     .Where(static tool => tool.Activation != null)
                     .GroupBy(static tool => tool.Activation!.Group, StringComparer.Ordinal))
        {
            var activation = group.First().Activation!;
            if (PipelineSpecOwnsOperation(spec, activation.DecisionOperationId))
                continue;

            var fieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
            if (!GeneratedLeafDeclaresExactConditionalDecisionInput(
                    spec,
                    workflow.Inputs,
                    fieldName,
                    activation.AllowedValues))
            {
                continue;
            }

            var groupTools = group.ToArray();
            var groupCalls = allSteps
                .Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal))
                .Where(step => groupTools.Any(tool => WorkflowStepMatchesPlannedMcpToolCall(step, tool)))
                .ToArray();
            if (groupCalls.Length != groupTools.Length
                || groupCalls.Any(call => spec.PlannedTools
                    .Where(tool => WorkflowStepMatchesPlannedMcpToolCall(call, tool))
                    .Any(tool => !string.Equals(
                        tool.Activation?.Group,
                        group.Key,
                        StringComparison.Ordinal))))
            {
                continue;
            }

            var owners = switches.Where(candidate =>
            {
                var nested = (candidate.Cases ?? [])
                    .SelectMany(static @case => EnumerateSteps(@case.Steps))
                    .Concat(EnumerateSteps(candidate.Default ?? []))
                    .ToHashSet(ReferenceEqualityComparer.Instance);
                return groupCalls.All(nested.Contains);
            }).ToArray();
            if (owners.Length != 1
                || ConditionalDecisionExpressionMatchesDeclaredPath(
                    owners[0].Expr ?? string.Empty,
                    activation.DecisionOutputPath))
            {
                continue;
            }

            rewrites.Add((owners[0].Id, $"${{data.inputs.{fieldName}}}"));
        }

        var unambiguousRewrites = rewrites
            .GroupBy(static rewrite => rewrite.SwitchId, StringComparer.Ordinal)
            .Where(static group => group.Select(static rewrite => rewrite.Expression)
                .Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(static group => group.First())
            .ToArray();
        if (unambiguousRewrites.Length == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        if (root.GetMapping("workflows") is not { } workflows
            || !workflows.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
            || workflowNode is not YamlMappingNode workflowMap
            || workflowMap.GetSequence("steps") is not { } rootSteps)
        {
            return (document, yaml);
        }

        var rewritten = 0;
        foreach (var (switchId, expression) in unambiguousRewrites)
            rewritten += RewriteGeneratedLeafSwitchExpression(rootSteps, switchId, expression);
        if (rewritten != unambiguousRewrites.Length)
            return (document, yaml);

        var normalizedYaml = SerializeYamlNode(root);
        return (ParseAndValidateGeneratedWorkflow(normalizedYaml), normalizedYaml);
    }

    private static (WorkflowDocument Document, string Yaml) NormalizeGeneratedLeafConditionalDecisionInputAliases(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument document,
        string yaml)
    {
        if (!document.Workflows.TryGetValue(workflowName, out var workflow)
            || workflow.Inputs == null
            || workflow.Inputs.Count == 0)
        {
            return (document, yaml);
        }

        var aliases = new List<(string Alias, string Declared)>();
        foreach (var activation in spec.PlannedTools
                     .Where(static tool => tool.Activation != null)
                     .Select(static tool => tool.Activation!)
                     .GroupBy(static activation => activation.Group, StringComparer.Ordinal)
                     .Select(static group => group.First()))
        {
            if (PipelineSpecOwnsOperation(spec, activation.DecisionOperationId))
                continue;

            var fieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
            if (fieldName.Length == 0
                || workflow.Inputs.ContainsKey(fieldName)
                || !SpecDeclaresExactConditionalDecisionInput(spec, fieldName, activation.AllowedValues))
            {
                continue;
            }

            var candidates = workflow.Inputs
                .Where(pair => !spec.InputSchemas.ContainsKey(pair.Key)
                               && GeneratedLeafInputMatchesConditionalDecision(
                                   pair.Value,
                                   activation.AllowedValues))
                .Select(static pair => pair.Key)
                .ToArray();
            if (candidates.Length == 1)
                aliases.Add((candidates[0], fieldName));
        }

        var unambiguousAliases = aliases
            .GroupBy(static alias => alias.Alias, StringComparer.Ordinal)
            .Where(static group => group.Select(static alias => alias.Declared)
                .Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(static group => group.First())
            .ToArray();
        if (unambiguousAliases.Length == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        if (root.GetMapping("workflows") is not { } workflows
            || !workflows.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
            || workflowNode is not YamlMappingNode workflowMap
            || workflowMap.GetMapping("inputs") is not { } workflowInputs)
        {
            return (document, yaml);
        }

        var skillInputs = root.GetMapping("skill")?.GetMapping("inputs");
        foreach (var (alias, declared) in unambiguousAliases)
        {
            RewriteGeneratedLeafInputReferences(root, alias, declared);
            RenameOrRemoveGeneratedLeafInput(workflowInputs, alias, declared);
            if (skillInputs != null)
                RenameOrRemoveGeneratedLeafInput(skillInputs, alias, declared);
        }

        var normalizedYaml = SerializeYamlNode(root);
        return (ParseAndValidateGeneratedWorkflow(normalizedYaml), normalizedYaml);
    }

    private static bool GeneratedLeafDeclaresExactConditionalDecisionInput(
        WorkflowPipelineSubworkflowSpec spec,
        IReadOnlyDictionary<string, InputDef> workflowInputs,
        string fieldName,
        IReadOnlyList<string> allowedValues)
    {
        if (fieldName.Length == 0
            || !SpecDeclaresExactConditionalDecisionInput(spec, fieldName, allowedValues)
            || !workflowInputs.TryGetValue(fieldName, out var workflowInput))
        {
            return false;
        }

        return GeneratedLeafInputMatchesConditionalDecision(workflowInput, allowedValues);
    }

    private static bool SpecDeclaresExactConditionalDecisionInput(
        WorkflowPipelineSubworkflowSpec spec,
        string fieldName,
        IReadOnlyList<string> allowedValues)
    {
        if (!spec.InputSchemas.TryGetValue(fieldName, out var specSchema))
            return false;
        var expected = FlowTypeDescriptorConverter.FromJsonSchema(specSchema);
        return expected.Kind == FlowTypeKind.String
               && expected.EnumValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                   .SequenceEqual(allowedValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool GeneratedLeafInputMatchesConditionalDecision(
        InputDef workflowInput,
        IReadOnlyList<string> allowedValues)
    {
        var actual = FlowTypeDescriptorConverter.FromInputDef(workflowInput);
        return actual.Kind == FlowTypeKind.String
               && workflowInput.Required
               && !workflowInput.Nullable
               && actual.EnumValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                   .SequenceEqual(allowedValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)
               && actual.EnumValues.Count > 0;
    }

    private static int RewriteGeneratedLeafSwitchExpression(
        YamlSequenceNode steps,
        string switchId,
        string expression)
    {
        var rewritten = 0;
        foreach (var step in steps.Children.OfType<YamlMappingNode>())
        {
            if (string.Equals(step.GetScalar("type"), "switch", StringComparison.Ordinal)
                && string.Equals(step.GetScalar("id"), switchId, StringComparison.Ordinal))
            {
                ReplaceYaml(step, "expr", Scalar(expression));
                rewritten++;
            }

            foreach (var nested in EnumerateNestedYamlStepSequences(step))
                rewritten += RewriteGeneratedLeafSwitchExpression(nested, switchId, expression);
        }

        return rewritten;
    }

    private static bool AreEquivalentLeafInputSchemas(JsonNode? first, JsonNode? second)
    {
        var firstType = FlowTypeDescriptorConverter.FromJsonSchema(first);
        var secondType = FlowTypeDescriptorConverter.FromJsonSchema(second);
        return firstType.FindAssignmentIssue(secondType) == null
               && secondType.FindAssignmentIssue(firstType) == null;
    }

    private static string CanonicalizeContractAliasName(string name)
        => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static void RenameOrRemoveGeneratedLeafInput(
        YamlMappingNode inputs,
        string alias,
        string declaredName)
    {
        var aliasKey = Scalar(alias);
        if (!inputs.Children.TryGetValue(aliasKey, out var aliasSchema))
            return;

        if (!inputs.Children.ContainsKey(Scalar(declaredName)))
            inputs.Add(Scalar(declaredName), CloneYamlNode(aliasSchema));
        inputs.Children.Remove(aliasKey);
    }

    private static void RewriteGeneratedLeafInputReferences(YamlNode node, string alias, string declaredName)
    {
        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value != null:
                scalar.Value = Regex.Replace(
                    scalar.Value,
                    $@"(?<![A-Za-z0-9_-])data\.inputs\.{Regex.Escape(alias)}(?![A-Za-z0-9_-])",
                    $"data.inputs.{declaredName}",
                    RegexOptions.CultureInvariant);
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                    RewriteGeneratedLeafInputReferences(child, alias, declaredName);
                break;

            case YamlMappingNode mapping:
                foreach (var (_, child) in mapping.Children)
                    RewriteGeneratedLeafInputReferences(child, alias, declaredName);
                break;
        }
    }

    private static void EnforceLeafBlueprintImplemented(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineLeafBlueprint blueprint,
        WorkflowDef workflow)
    {
        var diagnostics = new JsonArray();
        var requiredTools = blueprint.Steps
            .Where(static step => step.PlannedTool is { Required: true })
            .Select(static step => step.PlannedTool!)
            .ToArray();
        foreach (var requiredTool in FindMissingPlannedMcpToolOccurrences(workflow, requiredTools))
        {
            AddLeafBlueprintYamlMismatchDiagnostic(
                diagnostics,
                spec.Name,
                "steps",
                "PIPELINE_LEAF_BLUEPRINT_YAML_MISSING_PLANNED_TOOL",
                $"Generated YAML did not use a required occurrence of planned MCP tool {requiredTool.Server}/{requiredTool.Method} ({requiredTool.Kind}) from the locked blueprint with literal request bindings [{FormatBindingsCompact(requiredTool.RequestBindings)}].");
        }

        var requiredNativeSteps = blueprint.Steps
            .Where(static step => step.PlannedNativeStep is { Required: true })
            .Select(static step => step.PlannedNativeStep!)
            .ToArray();
        foreach (var requiredNativeStep in FindMissingPlannedNativeStepOccurrences(workflow, requiredNativeSteps))
        {
            AddLeafBlueprintYamlMismatchDiagnostic(
                diagnostics,
                spec.Name,
                "steps",
                "PIPELINE_LEAF_BLUEPRINT_YAML_MISSING_PLANNED_NATIVE_STEP",
                $"Generated YAML did not use a required occurrence of native Flow step {requiredNativeStep.Method} from the locked blueprint.");
        }

        foreach (var output in blueprint.Outputs)
        {
            if (workflow.Outputs == null || !workflow.Outputs.ContainsKey(output.Name))
            {
                AddLeafBlueprintYamlMismatchDiagnostic(
                    diagnostics,
                    spec.Name,
                    $"outputs.{output.Name}",
                    "PIPELINE_LEAF_BLUEPRINT_YAML_MISSING_OUTPUT",
                    $"Generated YAML must expose blueprint output '{output.Name}'.");
                continue;
            }
        }

        if (diagnostics.Count == 0)
            return;

        var rootCauses = BuildLeafBlueprintRootCausesJson(spec.Name, diagnostics, "leaf_blueprint_yaml_mismatch");
        var details = new JsonObject
        {
            ["phase"] = "pipeline_leaf_blueprint_yaml_validation",
            ["leaf"] = spec.Name,
            ["blueprint"] = BuildPipelineLeafBlueprintJson(blueprint),
            ["diagnostics"] = diagnostics.DeepClone(),
            ["root_causes"] = rootCauses
        };
        var message = diagnostics
            .Select(static node => node is JsonObject obj ? GetStringProperty(obj, "message") : null)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .DefaultIfEmpty("Generated YAML does not implement the locked leaf blueprint.")
            .Aggregate((left, right) => left + "; " + right);

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' did not implement its locked blueprint: {message}",
            details: details);
    }

    private static void AddLeafBlueprintYamlMismatchDiagnostic(
        JsonArray diagnostics,
        string leafName,
        string path,
        string code,
        string message)
    {
        diagnostics.Add((JsonNode)new JsonObject
        {
            ["code"] = code,
            ["phase"] = "pipeline_leaf_blueprint_yaml_validation",
            ["leaf"] = leafName,
            ["invalid_path"] = $"blueprints.{leafName}.{path}",
            ["message"] = message
        });
    }

    private static (WorkflowDocument Document, string Yaml) PromoteLeafOutputSchemasFromDirectSources(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument doc,
        string yaml,
        PipelineMcpContext pipelineMcpContext)
    {
        var leafName = spec.Name;
        if (!doc.Workflows.TryGetValue(workflowName, out var workflow) || workflow.Outputs == null || workflow.Outputs.Count == 0)
            return (doc, yaml);

        var stepOutputTypes = BuildLeafStepOutputTypeMap(workflowName, doc, workflow, pipelineMcpContext);
        var workflowOutputTypes = workflow.Outputs
            .ToDictionary(
                static pair => pair.Key,
                static pair => FlowTypeDescriptorConverter.FromOutputDef(pair.Value),
                StringComparer.Ordinal);
        var promotions = new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);
        foreach (var (outputName, output) in workflow.Outputs)
        {
            var generatedType = FlowTypeDescriptorConverter.FromOutputDef(output);
            var generatedTypeIsWeak = IsWeakPipelineOutputDescriptor(generatedType);
            FlowTypeDescriptor? lockedType = null;

            if (spec.OutputSchemas.TryGetValue(outputName, out var lockedSchema))
            {
                lockedType = FlowTypeDescriptorConverter.FromJsonSchema(lockedSchema);
                if (!lockedType.IsOpaque
                    && !IsWeakPipelineOutputDescriptor(lockedType)
                    && generatedTypeIsWeak)
                {
                    promotions[outputName] = string.IsNullOrWhiteSpace(output.Description)
                        ? lockedType
                        : lockedType with { Description = output.Description };
                }
            }

            if (!TryResolveDirectLeafOutputSource(output.Expr, stepOutputTypes, out var sourceType)
                || sourceType.IsOpaque
                || IsWeakPipelineOutputDescriptor(sourceType))
                continue;

            // A direct expression cannot transform its nested contract. Prefer the
            // authoritative registered step/MCP contract whenever a generated or
            // extracted schema is incompatible with that source. This also covers
            // extra documented optional fields: claiming a narrower closed object
            // would be false because the value is passed through without projection.
            if (!generatedTypeIsWeak && sourceType.FindAssignmentIssue(generatedType) == null)
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

                if (promotions.TryGetValue(outputKey.Value, out var promotedDescriptor)
                    && workflow.Outputs.TryGetValue(outputKey.Value, out var promotedOutput))
                {
                    ReplaceYaml(
                        skillOutputs,
                        outputKey.Value,
                        BuildPromotedLeafOutputYaml(promotedOutput, promotedDescriptor, includeExpr: false));
                    continue;
                }

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

    private static (WorkflowDocument Document, string Yaml) PromoteGeneratedDirectOutputSchemas(
        WorkflowDocument document,
        string yaml,
        IReadOnlyList<McpServerDiscovery>? discovered,
        StepExecutorRegistry registry)
    {
        var mcpContracts = BuildMcpToolOutputContracts(discovered)
            .ToDictionary(
                static contract => (contract.ServerName, contract.ToolName),
                static contract => contract,
                EqualityComparer<(string ServerName, string ToolName)>.Default);
        var stepContracts = registry.GetContracts();
        var promotionsByWorkflow = new Dictionary<string, Dictionary<string, FlowTypeDescriptor>>(StringComparer.Ordinal);

        foreach (var (workflowName, workflow) in document.Workflows)
        {
            if (workflow.Outputs == null || workflow.Outputs.Count == 0)
                continue;

            var stepOutputTypes = BuildLeafStepOutputTypeMap(
                workflowName,
                document,
                workflow,
                mcpContracts,
                stepContracts);
            var promotions = new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);
            foreach (var (outputName, output) in workflow.Outputs)
            {
                if (!TryResolveDirectLeafOutputSource(output.Expr, stepOutputTypes, out var sourceType)
                    || sourceType.IsOpaque
                    || IsWeakPipelineOutputDescriptor(sourceType))
                {
                    continue;
                }

                var declaredType = FlowTypeDescriptorConverter.FromOutputDef(output);
                if (!IsWeakPipelineOutputDescriptor(declaredType)
                    && sourceType.FindAssignmentIssue(declaredType) == null)
                {
                    continue;
                }

                promotions[outputName] = string.IsNullOrWhiteSpace(output.Description)
                    ? sourceType
                    : sourceType with { Description = output.Description };
            }

            if (promotions.Count > 0)
                promotionsByWorkflow[workflowName] = promotions;
        }

        if (promotionsByWorkflow.Count == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        var workflowsNode = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated YAML is missing workflows.");
        foreach (var (workflowName, promotions) in promotionsByWorkflow)
        {
            if (!workflowsNode.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
                || workflowNode is not YamlMappingNode workflowMap)
            {
                continue;
            }

            var workflowOutputs = workflowMap.GetMapping("outputs") ?? new YamlMappingNode();
            foreach (var (outputName, descriptor) in promotions)
            {
                var output = document.Workflows[workflowName].Outputs![outputName];
                ReplaceYaml(workflowOutputs, outputName, BuildPromotedLeafOutputYaml(output, descriptor));
            }
            if (!ContainsYamlKey(workflowMap, "outputs"))
                AddYaml(workflowMap, "outputs", workflowOutputs);
        }

        // A generated leaf exposes its single workflow through the document skill.
        // Keep that public schema aligned with the exact value returned by the leaf.
        if (document.Workflows.Count == 1
            && root.GetMapping("skill")?.GetMapping("outputs") is { } skillOutputs)
        {
            var workflowName = document.Workflows.Keys.Single();
            if (promotionsByWorkflow.TryGetValue(workflowName, out var promotions))
            {
                foreach (var (outputName, descriptor) in promotions)
                {
                    if (!skillOutputs.Children.ContainsKey(Scalar(outputName)))
                        continue;

                    var output = document.Workflows[workflowName].Outputs![outputName];
                    ReplaceYaml(skillOutputs, outputName, BuildPromotedLeafOutputYaml(output, descriptor, includeExpr: false));
                }
            }
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var promotedYaml = writer.ToString();
        return (ParseAndValidateGeneratedWorkflow(promotedYaml), promotedYaml);
    }

    private static IReadOnlyList<PipelinePlannedTool> NormalizeAdvisoryPlannedToolRequestBindings(
        IReadOnlyList<PipelinePlannedTool> plannedTools,
        PipelineMcpContext pipelineMcpContext)
    {
        List<PipelinePlannedTool>? normalized = null;
        for (var index = 0; index < plannedTools.Count; index++)
        {
            var plannedTool = plannedTools[index];
            if (plannedTool.RequestBindings.Count == 0)
                continue;

            var server = pipelineMcpContext.Servers.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                plannedTool.Server,
                StringComparison.Ordinal));
            if (server == null
                || !TryClassifyPlannedToolBindings(
                    plannedTool,
                    server,
                    out var bindingsAreExplicitSelectors)
                || AlternativeBindingsMatchSchema(
                        new CapabilityAlternative(
                            plannedTool.Server,
                            plannedTool.Kind,
                            plannedTool.Method,
                            plannedTool.RequestBindings),
                        server)
                || bindingsAreExplicitSelectors)
            {
                continue;
            }

            normalized ??= plannedTools.ToList();
            normalized[index] = plannedTool with
            {
                RequestBindings = Array.Empty<CapabilityRequestBinding>()
            };
        }

        return normalized ?? plannedTools;
    }

    private static bool TryClassifyPlannedToolBindings(
        PipelinePlannedTool plannedTool,
        McpServerDiscovery server,
        out bool bindingsAreExplicitSelectors)
    {
        bindingsAreExplicitSelectors = false;
        if (!string.Equals(plannedTool.Kind, "tool", StringComparison.Ordinal)
            || server.Tools.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                plannedTool.Method,
                StringComparison.Ordinal))?.InputSchema is not JsonObject inputSchema)
        {
            return false;
        }

        foreach (var binding in plannedTool.RequestBindings)
        {
            JsonObject? schema = inputSchema;
            foreach (var token in binding.Path.Split('/').Skip(1).Select(DecodeJsonPointerToken))
            {
                schema = schema?["properties"]?[token] as JsonObject;
                if (schema == null)
                    return false;
            }

            var documentedValues = ReadDocumentedScalarValues(schema, binding.Path);
            if (!documentedValues.Any(value => string.Equals(
                    CanonicalScalar(value),
                    CanonicalScalar(binding.Value),
                    StringComparison.Ordinal)))
            {
                return true;
            }
        }

        bindingsAreExplicitSelectors = true;
        return true;
    }

    private static (WorkflowDocument Document, string Yaml) PromoteGeneratedDirectSetOutputSchemas(
        WorkflowDocument document,
        string yaml,
        IReadOnlyList<McpServerDiscovery>? discovered,
        StepExecutorRegistry registry)
    {
        var mcpContracts = BuildMcpToolOutputContracts(discovered)
            .ToDictionary(
                static contract => (contract.ServerName, contract.ToolName),
                static contract => contract,
                EqualityComparer<(string ServerName, string ToolName)>.Default);
        var stepContracts = registry.GetContracts();
        var promotions = new Dictionary<(string Workflow, string Step, string Property), FlowTypeDescriptor>();

        foreach (var (workflowName, workflow) in document.Workflows)
        {
            var stepOutputTypes = BuildLeafStepOutputTypeMap(
                workflowName,
                document,
                workflow,
                mcpContracts,
                stepContracts);
            foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
            {
                if (!string.Equals(step.Type, "set", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(step.Id)
                    || step.Input is not JsonObject input
                    || step.OutputSchema is not JsonObject outputSchema
                    || outputSchema["properties"] is not JsonObject properties)
                {
                    continue;
                }

                foreach (var (propertyName, value) in input)
                {
                    if (value is not JsonValue scalar
                        || !scalar.TryGetValue<string>(out var expression)
                        || string.IsNullOrWhiteSpace(expression)
                        || properties[propertyName] is not JsonObject declaredSchema
                        || !TryResolveDirectLeafOutputSource(expression, stepOutputTypes, out var sourceType)
                        || sourceType.IsOpaque
                        || IsWeakPipelineOutputDescriptor(sourceType))
                    {
                        continue;
                    }

                    var declaredType = FlowTypeDescriptorConverter.FromJsonSchema(declaredSchema);
                    if (!IsWeakPipelineOutputDescriptor(declaredType)
                        && sourceType.FindAssignmentIssue(declaredType) == null)
                    {
                        continue;
                    }

                    // A set assignment containing one exact expression is a pass-through,
                    // not a projection. Its registered source contract is authoritative;
                    // retaining a narrower generated schema would make a valid nullable or
                    // richer source fail both semantic and runtime validation.
                    promotions[(workflowName, step.Id, propertyName)] = sourceType;
                }
            }
        }

        if (promotions.Count == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        var workflowsNode = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated YAML is missing workflows.");
        foreach (var ((workflowName, stepId, propertyName), descriptor) in promotions)
        {
            if (!workflowsNode.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
                || workflowNode is not YamlMappingNode workflowMap
                || FindGeneratedYamlStepById(workflowMap, stepId) is not { } stepMap
                || stepMap.GetMapping("output_schema")?.GetMapping("properties") is not { } properties)
            {
                continue;
            }

            var promoted = WorkflowPlanContractNormalizer.JsonToYaml(
                FlowTypeDescriptorConverter.ToRuntimeJsonSchema(descriptor));
            if (properties.Children.TryGetValue(Scalar(propertyName), out var current)
                && current is YamlMappingNode currentMap
                && promoted is YamlMappingNode promotedMap
                && currentMap.Children.TryGetValue(Scalar("description"), out var description))
            {
                ReplaceYaml(promotedMap, "description", CloneYamlNode(description));
            }
            ReplaceYaml(properties, propertyName, promoted);
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var promotedYaml = writer.ToString();
        return (ParseAndValidateGeneratedWorkflow(promotedYaml), promotedYaml);
    }

    private static YamlMappingNode? FindGeneratedYamlStepById(YamlMappingNode workflow, string stepId)
    {
        foreach (var key in new[] { "steps", "finally" })
        {
            if (workflow.GetSequence(key) is { } steps
                && FindGeneratedYamlStepById(steps, stepId) is { } match)
            {
                return match;
            }
        }
        return null;
    }

    private static YamlMappingNode? FindGeneratedYamlStepById(YamlSequenceNode steps, string stepId)
    {
        foreach (var step in steps.Children.OfType<YamlMappingNode>())
        {
            if (string.Equals(step.GetScalar("id"), stepId, StringComparison.Ordinal))
                return step;
            foreach (var nested in EnumerateNestedYamlStepSequences(step))
            {
                if (FindGeneratedYamlStepById(nested, stepId) is { } match)
                    return match;
            }
        }
        return null;
    }

    private static (WorkflowDocument Document, string Yaml) PromoteGeneratedDirectMcpScalarInputSchemas(
        WorkflowDocument document,
        string yaml,
        IReadOnlyList<McpServerDiscovery>? discovered)
    {
        var contracts = BuildMcpToolOutputContracts(discovered)
            .ToDictionary(
                static contract => (contract.ServerName, contract.ToolName),
                static contract => contract,
                EqualityComparer<(string ServerName, string ToolName)>.Default);
        if (contracts.Count == 0)
            return (document, yaml);

        var promotionsByWorkflow = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (workflowName, workflow) in document.Workflows)
        {
            if (workflow.Inputs == null || workflow.Inputs.Count == 0)
                continue;

            var demands = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
            {
                if (!string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)
                    || step.Input is not JsonObject input
                    || input["server"] is not JsonValue serverNode
                    || !serverNode.TryGetValue<string>(out var server)
                    || input["method"] is not JsonValue methodNode
                    || !methodNode.TryGetValue<string>(out var method)
                    || input["request"] is not JsonObject request
                    || !contracts.TryGetValue((server, method), out var contract)
                    || contract.InputSchema == null)
                {
                    continue;
                }

                var requestType = FlowTypeDescriptorConverter.FromJsonSchema(contract.InputSchema);
                CollectDirectMcpScalarInputDemands(request, requestType, Array.Empty<string>(), workflow.Inputs, demands);
            }

            var promotions = demands
                .Where(static pair => pair.Value.Count == 1)
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.Single(),
                    StringComparer.Ordinal);
            if (promotions.Count > 0)
                promotionsByWorkflow[workflowName] = promotions;
        }

        if (promotionsByWorkflow.Count == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        var workflowsNode = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated YAML is missing workflows.");
        foreach (var (workflowName, promotions) in promotionsByWorkflow)
        {
            if (!workflowsNode.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
                || workflowNode is not YamlMappingNode workflowMap
                || workflowMap.GetMapping("inputs") is not { } workflowInputs)
            {
                continue;
            }

            foreach (var (inputName, scalarType) in promotions)
                PromoteYamlInputScalarType(workflowInputs, inputName, scalarType);
        }

        if (document.Workflows.Count == 1
            && root.GetMapping("skill")?.GetMapping("inputs") is { } skillInputs)
        {
            var workflowName = document.Workflows.Keys.Single();
            if (promotionsByWorkflow.TryGetValue(workflowName, out var promotions))
            {
                foreach (var (inputName, scalarType) in promotions)
                    PromoteYamlInputScalarType(skillInputs, inputName, scalarType);
            }
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var promotedYaml = writer.ToString();
        return (ParseAndValidateGeneratedWorkflow(promotedYaml), promotedYaml);
    }

    private static (WorkflowDocument Document, string Yaml) PromoteGeneratedDirectWorkflowCallObjectInputSchemas(
        WorkflowDocument document,
        string yaml,
        IReadOnlyList<McpServerDiscovery>? discovered,
        StepExecutorRegistry registry)
    {
        var mcpContracts = BuildMcpToolOutputContracts(discovered)
            .ToDictionary(
                static contract => (contract.ServerName, contract.ToolName),
                static contract => contract,
                EqualityComparer<(string ServerName, string ToolName)>.Default);
        var stepContracts = registry.GetContracts();
        var promotions = new Dictionary<(string Workflow, string Input), FlowTypeDescriptor>();
        var conflicts = new HashSet<(string Workflow, string Input)>();

        foreach (var (callerName, caller) in document.Workflows)
        {
            var sourceTypes = BuildLeafStepOutputTypeMap(
                callerName,
                document,
                caller,
                mcpContracts,
                stepContracts);
            foreach (var step in EnumerateSteps(caller.Steps).Concat(EnumerateSteps(caller.Finally)))
            {
                if (!string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
                    || ReadWorkflowCallRefNameFromInput(step) is not { } targetName
                    || !document.Workflows.TryGetValue(targetName, out var target)
                    || target.Inputs == null
                    || step.Input is not JsonObject callInput
                    || callInput["args"] is not JsonObject args)
                {
                    continue;
                }

                foreach (var (inputName, argument) in args)
                {
                    if (argument is not JsonValue value
                        || !value.TryGetValue<string>(out var expression)
                        || string.IsNullOrWhiteSpace(expression)
                        || !target.Inputs.TryGetValue(inputName, out var destinationInput)
                        || !TryResolveDirectLeafOutputSource(expression, sourceTypes, out var sourceType)
                        || sourceType.IsOpaque
                        || IsWeakPipelineOutputDescriptor(sourceType))
                    {
                        continue;
                    }

                    var destinationType = FlowTypeDescriptorConverter.FromInputDef(destinationInput);
                    var issue = sourceType.FindAssignmentIssue(destinationType);
                    var widensClosedObject = issue != null
                                              && string.Equals(issue.ActualType, "declared property", StringComparison.Ordinal)
                                              && string.Equals(issue.ExpectedType, "closed object without additional properties", StringComparison.Ordinal);
                    var widensIntegerToAuthoritativeNumber = issue != null
                                                             && sourceType.Kind == FlowTypeKind.Number
                                                             && destinationType.Kind == FlowTypeKind.Integer;
                    if (!widensClosedObject && !widensIntegerToAuthoritativeNumber)
                    {
                        continue;
                    }

                    var key = (targetName, inputName);
                    if (conflicts.Contains(key))
                        continue;
                    if (promotions.TryGetValue(key, out var existing)
                        && !JsonNode.DeepEquals(
                            FlowTypeDescriptorConverter.ToRuntimeJsonSchema(existing),
                            FlowTypeDescriptorConverter.ToRuntimeJsonSchema(sourceType)))
                    {
                        promotions.Remove(key);
                        conflicts.Add(key);
                        continue;
                    }

                    promotions[key] = sourceType;
                }
            }
        }

        if (promotions.Count == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        var workflowsNode = root.GetMapping("workflows")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Generated YAML is missing workflows.");
        foreach (var ((workflowName, inputName), descriptor) in promotions)
        {
            if (!workflowsNode.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
                || workflowNode is not YamlMappingNode workflowMap
                || workflowMap.GetMapping("inputs") is not { } workflowInputs
                || !workflowInputs.Children.TryGetValue(Scalar(inputName), out var currentInput))
            {
                continue;
            }

            ReplaceYaml(
                workflowInputs,
                inputName,
                BuildPromotedWorkflowInputYaml(currentInput, descriptor));
        }

        // Generated single-leaf documents mirror workflow inputs in skill.inputs.
        // Final pipeline documents contain several leaves, so update skill.inputs only
        // when the promoted workflow is the sole exposed workflow.
        if (document.Workflows.Count == 1
            && root.GetMapping("skill")?.GetMapping("inputs") is { } skillInputs)
        {
            var workflowName = document.Workflows.Keys.Single();
            foreach (var ((targetName, inputName), descriptor) in promotions)
            {
                if (!string.Equals(targetName, workflowName, StringComparison.Ordinal)
                    || !skillInputs.Children.TryGetValue(Scalar(inputName), out var currentInput))
                {
                    continue;
                }

                ReplaceYaml(
                    skillInputs,
                    inputName,
                    BuildPromotedWorkflowInputYaml(currentInput, descriptor));
            }
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var promotedYaml = writer.ToString();
        return (ParseAndValidateGeneratedWorkflow(promotedYaml), promotedYaml);
    }

    private static YamlNode BuildPromotedWorkflowInputYaml(
        YamlNode currentInput,
        FlowTypeDescriptor descriptor)
    {
        var promoted = JsonToYaml(FlowTypeDescriptorConverter.ToWorkflowContractNode(
            descriptor,
            inputStyle: true,
            allowScalarShortForm: false));
        if (promoted is not YamlMappingNode promotedMap || currentInput is not YamlMappingNode currentMap)
            return promoted;

        foreach (var metadataKey in new[] { "description", "required", "default" })
        {
            if (currentMap.Children.TryGetValue(Scalar(metadataKey), out var metadata))
                ReplaceYaml(promotedMap, metadataKey, CloneYamlNode(metadata));
        }
        return promotedMap;
    }

    private static void CollectDirectMcpScalarInputDemands(
        JsonNode node,
        FlowTypeDescriptor requestType,
        IReadOnlyList<string> path,
        IReadOnlyDictionary<string, InputDef> workflowInputs,
        Dictionary<string, HashSet<string>> demands)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj)
            {
                if (child != null)
                    CollectDirectMcpScalarInputDemands(child, requestType, path.Append(name).ToArray(), workflowInputs, demands);
            }
            return;
        }

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var expression)
            || string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        var match = Regex.Match(
            expression,
            @"^\s*\$\{\s*data\.inputs\.(?<name>[A-Za-z_][A-Za-z0-9_-]*)\s*\}\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return;

        var inputName = match.Groups["name"].Value;
        if (!workflowInputs.TryGetValue(inputName, out var inputDef))
            return;

        var expected = requestType.ResolvePath(path.ToArray());
        if (!TryGetSingleNonNullScalarType(expected, out var scalarType))
            return;

        var actual = FlowTypeDescriptorConverter.FromInputDef(inputDef);
        if (actual.FindAssignmentIssue(expected!) == null)
            return;

        if (!demands.TryGetValue(inputName, out var inputDemands))
        {
            inputDemands = new HashSet<string>(StringComparer.Ordinal);
            demands[inputName] = inputDemands;
        }
        inputDemands.Add(scalarType);
    }

    private static bool TryGetSingleNonNullScalarType(
        FlowTypeDescriptor? descriptor,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? scalarType)
    {
        scalarType = null;
        if (descriptor == null || descriptor.IsOpaque)
            return false;

        var candidates = descriptor.Kind == FlowTypeKind.Union
            ? descriptor.Variants.Where(static variant => variant.Kind != FlowTypeKind.Null).ToArray()
            : [descriptor];
        if (candidates.Length != 1)
            return false;

        scalarType = candidates[0].Kind switch
        {
            FlowTypeKind.String => "string",
            FlowTypeKind.Number => "number",
            FlowTypeKind.Integer => "integer",
            FlowTypeKind.Boolean => "boolean",
            _ => null
        };
        return scalarType != null;
    }

    private static void PromoteYamlInputScalarType(
        YamlMappingNode inputs,
        string inputName,
        string scalarType)
    {
        if (!inputs.Children.TryGetValue(Scalar(inputName), out var inputNode))
            return;

        if (inputNode is YamlMappingNode inputMap)
            ReplaceYaml(inputMap, "type", Scalar(scalarType));
        else
            ReplaceYaml(inputs, inputName, Scalar(scalarType));
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
        WorkflowDef workflow,
        PipelineMcpContext pipelineMcpContext)
    {
        var mcpContracts = BuildMcpToolOutputContracts(pipelineMcpContext.Servers)
            .ToDictionary(
                static contract => (contract.ServerName, contract.ToolName),
                static contract => contract,
                EqualityComparer<(string ServerName, string ToolName)>.Default);
        return BuildLeafStepOutputTypeMap(
            workflowName,
            doc,
            workflow,
            mcpContracts,
            BuiltInStepContracts.All);
    }

    private static Dictionary<string, FlowTypeDescriptor> BuildLeafStepOutputTypeMap(
        string workflowName,
        WorkflowDocument doc,
        WorkflowDef workflow,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        var allStepIds = EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally))
            .Select(static step => step.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var symbols = WorkflowSymbolTable.Create(workflowName, workflow.Inputs, allStepIds);
        var result = new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);
        foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                continue;

            var outputType = StepOutputTypeResolver.Resolve(
                step,
                doc.Workflows,
                symbols,
                mcpContracts,
                stepContracts);
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

    private static (WorkflowDocument Document, string Yaml) MaterializeMissingPlannedMcpCallVariants(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument document,
        string yaml)
    {
        if (!document.Workflows.TryGetValue(workflowName, out var workflow))
            return (document, yaml);

        var requiredTools = spec.PlannedTools.Where(static tool => tool.Required).ToArray();
        var missing = FindMissingPlannedMcpToolOccurrences(workflow, requiredTools);
        if (missing.Count == 0)
            return (document, yaml);

        var root = LoadYamlRoot(yaml);
        var workflows = root.GetMapping("workflows");
        if (workflows == null
            || !workflows.Children.TryGetValue(Scalar(workflowName), out var workflowNode)
            || workflowNode is not YamlMappingNode workflowMap
            || workflowMap.GetSequence("steps") is not { } rootSteps)
        {
            return (document, yaml);
        }

        var usedStepIds = new HashSet<string>(StringComparer.Ordinal);
        CollectYamlStepIds(rootSteps, usedStepIds);
        var added = false;
        foreach (var plannedTool in missing)
        {
            // Never synthesize an unclassified or write-like variant. Cloning is limited to
            // selector values whose documented literal is conservatively read-only; all other
            // effects must be generated with their original control-flow and confirmation gate.
            if (!IsConservativelyReadOnlySelectorVariant(plannedTool))
                continue;
            if (!TryFindYamlMcpCallTemplate(rootSteps, plannedTool, out var parentSteps, out var template))
                continue;

            var clone = CloneYamlMappingNode(template);
            var input = clone.GetMapping("input");
            var request = input?.GetMapping("request");
            if (input == null || request == null
                || !TryApplyYamlRequestBindings(request, plannedTool.RequestBindings))
            {
                continue;
            }

            var selectorSuffix = plannedTool.RequestBindings.Count == 0
                ? "variant"
                : string.Join('_', plannedTool.RequestBindings.Select(binding =>
                    SanitizeBlueprintIdentifier(binding.Value is JsonValue scalar
                                                 && scalar.TryGetValue<string>(out var text)
                        ? text
                        : binding.Value?.ToJsonString() ?? "value")));
            ReplaceYaml(
                clone,
                "id",
                Scalar(MakeUniqueBlueprintStepId(
                    $"call_{SanitizeBlueprintIdentifier(plannedTool.Method)}_{selectorSuffix}",
                    usedStepIds)));
            var templateIndex = parentSteps.Children.IndexOf(template);
            parentSteps.Children.Insert(templateIndex < 0 ? parentSteps.Children.Count : templateIndex + 1, clone);
            added = true;
        }

        if (!added)
            return (document, yaml);

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var normalizedYaml = writer.ToString().Trim();
        return (ParseAndValidateGeneratedWorkflow(normalizedYaml), normalizedYaml);
    }

    private static bool IsConservativelyReadOnlySelectorVariant(PipelinePlannedTool plannedTool)
    {
        if (plannedTool.RequestBindings.Count == 0)
            return false;

        foreach (var binding in plannedTool.RequestBindings)
        {
            if (binding.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out var value))
                return false;
            var normalized = value.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(
                    normalized,
                    @"^(?:get|list|read|fetch|search|find|query|compare|diff|status|check|inspect|describe|show|resolve)(?:_|$)",
                    RegexOptions.CultureInvariant))
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<PipelinePlannedTool> FindMissingPlannedMcpToolOccurrences(
        WorkflowDef workflow,
        IReadOnlyList<PipelinePlannedTool> requiredTools)
    {
        var steps = EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)).ToArray();
        var consumed = new HashSet<int>();
        var missing = new List<PipelinePlannedTool>();
        foreach (var plannedTool in requiredTools)
        {
            var matchedIndex = Array.FindIndex(
                steps,
                0,
                steps.Length,
                step => !consumed.Contains(Array.IndexOf(steps, step))
                        && WorkflowStepMatchesPlannedMcpToolCall(step, plannedTool));
            if (matchedIndex < 0)
                missing.Add(plannedTool);
            else
                consumed.Add(matchedIndex);
        }
        return missing;
    }

    private static void CollectYamlStepIds(YamlSequenceNode steps, HashSet<string> ids)
    {
        foreach (var child in steps.Children.OfType<YamlMappingNode>())
        {
            if (child.GetScalar("id") is { Length: > 0 } id)
                ids.Add(id);
            foreach (var nested in EnumerateNestedYamlStepSequences(child))
                CollectYamlStepIds(nested, ids);
        }
    }

    private static bool TryFindYamlMcpCallTemplate(
        YamlSequenceNode steps,
        PipelinePlannedTool plannedTool,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YamlSequenceNode? parentSteps,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YamlMappingNode? template)
    {
        foreach (var child in steps.Children.OfType<YamlMappingNode>())
        {
            var input = child.GetMapping("input");
            var methodMatches = string.Equals(input?.GetScalar("method"), plannedTool.Method, StringComparison.Ordinal)
                                || input?.GetSequence("methods")?.Children
                                    .OfType<YamlScalarNode>()
                                    .Any(item => string.Equals(item.Value, plannedTool.Method, StringComparison.Ordinal)) == true;
            if (string.Equals(child.GetScalar("type"), "mcp.call", StringComparison.Ordinal)
                && string.Equals(input?.GetScalar("server"), plannedTool.Server, StringComparison.Ordinal)
                && string.Equals(input?.GetScalar("kind") ?? "tool", plannedTool.Kind, StringComparison.Ordinal)
                && methodMatches
                && input?.GetMapping("request") != null)
            {
                parentSteps = steps;
                template = child;
                return true;
            }

            foreach (var nested in EnumerateNestedYamlStepSequences(child))
            {
                if (TryFindYamlMcpCallTemplate(nested, plannedTool, out parentSteps, out template))
                    return true;
            }
        }

        parentSteps = null;
        template = null;
        return false;
    }

    private static IEnumerable<YamlSequenceNode> EnumerateNestedYamlStepSequences(YamlMappingNode step)
    {
        if (step.GetSequence("steps") is { } nestedSteps)
            yield return nestedSteps;
        if (step.GetSequence("default") is { } defaultSteps)
            yield return defaultSteps;
        foreach (var collectionName in new[] { "branches", "cases" })
        {
            if (step.GetSequence(collectionName) is not { } collection)
                continue;
            foreach (var item in collection.Children.OfType<YamlMappingNode>())
            {
                if (item.GetSequence("steps") is { } itemSteps)
                    yield return itemSteps;
            }
        }
    }

    private static bool TryApplyYamlRequestBindings(
        YamlMappingNode request,
        IReadOnlyList<CapabilityRequestBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            var segments = binding.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal))
                .ToArray();
            if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
                return false;

            var current = request;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (current.Children.TryGetValue(Scalar(segments[index]), out var child))
                {
                    if (child is not YamlMappingNode childMap)
                        return false;
                    current = childMap;
                    continue;
                }

                var created = new YamlMappingNode();
                AddYaml(current, segments[index], created);
                current = created;
            }

            ReplaceYaml(current, segments[^1], JsonToYaml(binding.Value));
        }
        return true;
    }

    private static void EnforcePlannedMcpToolsUsed(WorkflowPipelineSubworkflowSpec spec, WorkflowDef workflow)
    {
        var requiredTools = spec.PlannedTools
            .Where(static tool => tool.Required)
            .ToArray();
        if (requiredTools.Length == 0)
            return;

        var missing = FindMissingPlannedMcpToolOccurrences(workflow, requiredTools)
            .Select(static plannedTool => $"{plannedTool.Server}/{plannedTool.Method} ({plannedTool.Kind}) bindings=[{FormatBindingsCompact(plannedTool.RequestBindings)}]")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' did not use required planned MCP tool(s): {string.Join(", ", missing)}. Add explicit direct mcp.call step(s) with matching input.server, input.kind, literal input.method, and every locked request binding as a literal input.request value. Dynamic or opaque selector construction cannot satisfy a locked capability.");
    }

    private static void EnforcePlannedNativeStepsUsed(
        WorkflowPipelineSubworkflowSpec spec,
        WorkflowDef workflow)
    {
        var requiredSteps = (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
            .Where(static step => step.Required)
            .ToArray();
        if (requiredSteps.Length == 0)
            return;

        var missing = FindMissingPlannedNativeStepOccurrences(workflow, requiredSteps)
            .Select(static step => step.Method)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' did not use required planned native Flow step occurrence(s): {string.Join(", ", missing)}. Add explicit direct steps with the exact locked step type.");
    }

    private static IReadOnlyList<PipelinePlannedNativeStep> FindMissingPlannedNativeStepOccurrences(
        WorkflowDef workflow,
        IReadOnlyList<PipelinePlannedNativeStep> requiredSteps)
    {
        var available = EnumerateSteps(workflow.Steps)
            .Concat(EnumerateSteps(workflow.Finally))
            .ToList();
        var missing = new List<PipelinePlannedNativeStep>();
        foreach (var requiredStep in requiredSteps)
        {
            var index = available.FindIndex(step => string.Equals(step.Type, requiredStep.Method, StringComparison.Ordinal));
            if (index < 0)
                missing.Add(requiredStep);
            else
                available.RemoveAt(index);
        }
        return missing;
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
            ["root_causes"] = BuildPipelineLeafIntentRootCausesJson(spec, diagnostics),
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Regenerate only this leaf. Preserve its public contract, and replace fake/claimed side effects with real executable workflow steps.")!)
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Leaf workflow '{spec.Name}' failed intent validation. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static JsonArray BuildPipelineLeafIntentRootCausesJson(
        WorkflowPipelineSubworkflowSpec spec,
        JsonArray diagnostics)
    {
        var rootCauses = new List<PipelineRootCause>();
        foreach (var node in diagnostics)
        {
            if (node is not JsonObject diagnostic)
                continue;

            var code = GetStringProperty(diagnostic, "code");
            var category = code switch
            {
                "PIPELINE_LEAF_FAKE_ACTION_EMIT" => "fake_external_leaf",
                "PIPELINE_LEAF_EXTERNAL_WORK_WITHOUT_ACTION" => "fake_external_leaf",
                "PIPELINE_LEAF_SUCCESS_OUTPUT_WITHOUT_ACTION" => "fake_external_leaf",
                _ => "weak_leaf_contract"
            };
            AddPipelineRootCause(
                rootCauses,
                category,
                "pipeline_leaf_intent_validation",
                spec.Name,
                outputName: null,
                invalidPath: GetStringProperty(diagnostic, "step"),
                code,
                GetStringProperty(diagnostic, "message") ?? "Leaf intent validation failed.",
                primary: true);
        }

        return BuildPipelineRootCausesJson(rootCauses);
    }

    private static bool WorkflowContainsExecutableActionStep(WorkflowDef workflow)
        => EnumerateSteps(workflow.Steps)
            .Concat(EnumerateSteps(workflow.Finally))
            .Any(static step => IsExecutableActionStepType(step.Type));

    private static bool IsExecutableActionStepType(string? stepType)
        => stepType is "mcp.call" or "llm.call" or "template.render" or "human.input" or "mcp.list";

    private static IEnumerable<JsonObject> EnumerateFakeActionEmitDiagnostics(
        WorkflowPipelineSubworkflowSpec spec,
        WorkflowDef workflow)
    {
        if (!IsExternalWorkSpec(spec) || WorkflowContainsExecutableActionStep(workflow))
            yield break;

        foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
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
        foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
        {
            if (WorkflowStepMatchesPlannedMcpToolCall(step, plannedTool))
                return true;
        }

        return false;
    }

    private static bool WorkflowStepMatchesPlannedMcpToolCall(StepDef step, PipelinePlannedTool plannedTool)
    {
        if (step.Type != "mcp.call" || step.Input is not JsonObject input)
            return false;

        var server = GetStringProperty(input, "server");
        if (!string.Equals(server, plannedTool.Server, StringComparison.Ordinal))
            return false;

        var kind = GetStringProperty(input, "kind") ?? "tool";
        if (!string.Equals(kind, plannedTool.Kind, StringComparison.Ordinal))
            return false;

        var methodMatches = StringNodeEquals(input["method"], plannedTool.Method)
                            || input["methods"] is JsonArray methods
                            && methods.Any(method => StringNodeEquals(method, plannedTool.Method));
        return methodMatches && RequestContainsLiteralBindings(input["request"], plannedTool.RequestBindings);
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
        var type = NormalizeWorkflowSchemaType(schema.Type);
        if (string.Equals(type, "any", StringComparison.Ordinal))
        {
            errors.Add($"{path} has type any; declare the concrete public leaf input contract");
        }
        else if (string.Equals(type, "array", StringComparison.Ordinal) && schema.Items == null)
        {
            errors.Add($"{path} has type array without items");
        }
        else if (string.Equals(type, "object", StringComparison.Ordinal)
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
        MaterializeCompositeMainWorkflowOutputs(mainWorkflowNode, skillNode.GetMapping("outputs"));
        mainStepOutputTypes = AnalyzePipelineMainStepOutputs(mainWorkflowNode, leaves);
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
        NormalizeGeneratedStepIds(root);
        PruneWeakNestedOutputProperties(root);

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static void PruneWeakNestedOutputProperties(YamlMappingNode root)
    {
        NormalizeGeneratedFunctionParameterDocs(root);
        if (root.GetMapping("skill")?.GetMapping("outputs") is { } skillOutputs)
        {
            foreach (var output in skillOutputs.Children.Values)
                WorkflowPlanContractNormalizer.PruneWeakNestedOutputProperties(output);
        }

        if (root.GetMapping("workflows") is not { } workflows)
            return;
        foreach (var workflowNode in workflows.Children.Values.OfType<YamlMappingNode>())
        {
            if (workflowNode.GetMapping("outputs") is { } outputs)
            {
                foreach (var output in outputs.Children.Values)
                    WorkflowPlanContractNormalizer.PruneWeakNestedOutputProperties(output);
            }

            if (workflowNode.GetSequence("steps") is { } steps)
                NormalizeSetOutputSchemas(steps);
            if (workflowNode.GetSequence("finally") is { } finalizers)
                NormalizeSetOutputSchemas(finalizers);
        }
    }

    private static void NormalizeGeneratedStepIds(YamlMappingNode root)
    {
        if (root.GetMapping("workflows") is not { } workflows)
            return;

        foreach (var workflow in workflows.Children.Values.OfType<YamlMappingNode>())
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var suffixes = new Dictionary<string, int>(StringComparer.Ordinal);
            if (workflow.GetSequence("steps") is { } steps)
                NormalizeGeneratedStepIds(steps, used, suffixes);
            if (workflow.GetSequence("finally") is { } finalizers)
                NormalizeGeneratedStepIds(finalizers, used, suffixes);
        }
    }

    private static void NormalizeGeneratedStepIds(
        YamlSequenceNode steps,
        HashSet<string> used,
        Dictionary<string, int> suffixes,
        IReadOnlySet<YamlMappingNode>? allowedDuplicateSteps = null)
    {
        for (var index = 0; index < steps.Children.Count; index++)
        {
            if (steps.Children[index] is not YamlMappingNode step)
                continue;

            if (step.GetScalar("id") is { Length: > 0 } originalId
                && !used.Add(originalId)
                && allowedDuplicateSteps?.Contains(step) != true)
            {
                var suffix = suffixes.TryGetValue(originalId, out var previousSuffix)
                    ? previousSuffix + 1
                    : 2;
                string replacementId;
                do
                {
                    replacementId = $"{originalId}_{suffix++}";
                } while (!used.Add(replacementId));
                suffixes[originalId] = suffix - 1;
                step.Children[Scalar("id")] = Scalar(replacementId);
                ReplaceGeneratedStepReferences(step, originalId, replacementId);
                for (var following = index + 1; following < steps.Children.Count; following++)
                    ReplaceGeneratedStepReferences(steps.Children[following], originalId, replacementId);
            }

            var nestedAllowedDuplicateSteps = allowedDuplicateSteps;
            if (TryGetSharedSwitchProjectionNodes(step, used, out var sharedProjectionSteps))
            {
                var combined = allowedDuplicateSteps == null
                    ? new HashSet<YamlMappingNode>(ReferenceEqualityComparer.Instance)
                    : new HashSet<YamlMappingNode>(allowedDuplicateSteps, ReferenceEqualityComparer.Instance);
                combined.UnionWith(sharedProjectionSteps);
                nestedAllowedDuplicateSteps = combined;
            }

            foreach (var nested in EnumerateNestedYamlStepSequences(step))
                NormalizeGeneratedStepIds(nested, used, suffixes, nestedAllowedDuplicateSteps);
        }
    }

    private static bool TryGetSharedSwitchProjectionNodes(
        YamlMappingNode step,
        IReadOnlySet<string> used,
        out IReadOnlyList<YamlMappingNode> projections)
    {
        projections = Array.Empty<YamlMappingNode>();
        if (!string.Equals(step.GetScalar("type"), "switch", StringComparison.Ordinal)
            || step.GetSequence("cases") is not { } cases
            || cases.Children.Count == 0
            || step.GetSequence("default") is not { } defaultSteps
            || defaultSteps.Children.Count == 0)
        {
            return false;
        }

        var branches = cases.Children.OfType<YamlMappingNode>()
            .Select(static @case => @case.GetSequence("steps"))
            .Append(defaultSteps)
            .ToArray();
        if (branches.Length != cases.Children.Count + 1
            || branches.Any(static branch => branch == null || branch.Children.Count == 0))
        {
            return false;
        }

        var candidates = branches
            .Select(static branch => branch!.Children[^1] as YamlMappingNode)
            .ToArray();
        if (candidates.Any(static candidate => candidate == null))
            return false;

        var first = candidates[0]!;
        var firstId = first.GetScalar("id");
        var firstSchema = first.GetMapping("output_schema");
        if (string.IsNullOrWhiteSpace(firstId)
            || used.Contains(firstId)
            || !string.Equals(first.GetScalar("type"), "set", StringComparison.Ordinal)
            || firstSchema?.GetMapping("properties") is not { } firstProperties
            || firstProperties.Children.Count == 0)
        {
            return false;
        }

        var serializedFirstSchema = SerializeYamlNode(firstSchema);
        for (var index = 0; index < branches.Length; index++)
        {
            var branch = branches[index]!;
            var candidate = candidates[index]!;
            if (!string.Equals(candidate.GetScalar("id"), firstId, StringComparison.Ordinal)
                || !string.Equals(candidate.GetScalar("type"), "set", StringComparison.Ordinal)
                || candidate.GetMapping("output_schema") is not { } candidateSchema
                || !string.Equals(serializedFirstSchema, SerializeYamlNode(candidateSchema), StringComparison.Ordinal)
                || branch.Children.Take(branch.Children.Count - 1)
                    .OfType<YamlMappingNode>()
                    .Any(item => string.Equals(item.GetScalar("id"), firstId, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        projections = candidates.Select(static candidate => candidate!).ToArray();
        return true;
    }

    private static void ReplaceGeneratedStepReferences(YamlNode node, string originalId, string replacementId)
    {
        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value != null:
                scalar.Value = Regex.Replace(
                    scalar.Value,
                    $@"(?<![A-Za-z0-9_-])data\.steps\.{Regex.Escape(originalId)}(?![A-Za-z0-9_-])",
                    $"data.steps.{replacementId}",
                    RegexOptions.CultureInvariant);
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                    ReplaceGeneratedStepReferences(child, originalId, replacementId);
                break;
            case YamlMappingNode mapping:
                foreach (var child in mapping.Children)
                {
                    if (child.Key is YamlScalarNode { Value: "id" })
                        continue;
                    ReplaceGeneratedStepReferences(child.Value, originalId, replacementId);
                }
                break;
        }
    }

    private static string NormalizeGeneratedStepIds(string yaml)
    {
        var root = LoadYamlRoot(yaml);
        NormalizeGeneratedStepIds(root);
        return SerializeYamlNode(root);
    }

    private static void NormalizeSetOutputSchemas(YamlSequenceNode steps)
    {
        foreach (var step in steps.Children.OfType<YamlMappingNode>())
        {
            if (string.Equals(step.GetScalar("type"), "set", StringComparison.Ordinal)
                && step.GetMapping("output_schema") is { } outputSchema)
            {
                WorkflowPlanContractNormalizer.NormalizeSetOutputSchema(outputSchema);
            }

            foreach (var nested in EnumerateNestedYamlStepSequences(step))
                NormalizeSetOutputSchemas(nested);
        }
    }

    private static string PruneWeakNestedOutputProperties(string yaml)
    {
        YamlMappingNode root;
        try
        {
            root = LoadYamlRoot(yaml);
        }
        catch
        {
            // Preserve the original parser diagnostic for malformed YAML.
            return yaml;
        }

        var before = SerializeYamlNode(root);
        PruneWeakNestedOutputProperties(root);
        var after = SerializeYamlNode(root);

        // Validation and normalization should not become a formatting rewrite.
        // In particular, repair mode consumers persist the returned YAML and
        // expect a minimal patch. Preserve the generated text exactly when the
        // normalized representation did not change.
        return string.Equals(before, after, StringComparison.Ordinal)
            ? yaml
            : after;
    }

    private static string NormalizeGeneratedFunctionParameterDocs(string yaml)
    {
        YamlMappingNode root;
        try
        {
            root = LoadYamlRoot(yaml);
        }
        catch
        {
            return yaml;
        }

        var before = SerializeYamlNode(root);
        NormalizeGeneratedFunctionParameterDocs(root);
        var after = SerializeYamlNode(root);
        return string.Equals(before, after, StringComparison.Ordinal)
            ? yaml
            : after;
    }

    private static string NormalizeGeneratedSetOutputSchemas(string yaml)
    {
        YamlMappingNode root;
        try
        {
            root = LoadYamlRoot(yaml);
        }
        catch
        {
            return yaml;
        }

        var before = SerializeYamlNode(root);
        if (root.GetMapping("workflows") is { } workflows)
        {
            foreach (var workflow in workflows.Children.Values.OfType<YamlMappingNode>())
            {
                if (workflow.GetSequence("steps") is { } steps)
                    NormalizeSetOutputSchemas(steps);
                if (workflow.GetSequence("finally") is { } finalizers)
                    NormalizeSetOutputSchemas(finalizers);
            }
        }

        var after = SerializeYamlNode(root);
        return string.Equals(before, after, StringComparison.Ordinal)
            ? yaml
            : after;
    }

    private static string NormalizeGeneratedFlowNullableSchemas(string yaml)
    {
        YamlMappingNode root;
        try
        {
            root = LoadYamlRoot(yaml);
        }
        catch
        {
            return yaml;
        }

        var before = SerializeYamlNode(root);
        if (root.GetMapping("skill") is { } skill)
        {
            NormalizeFlowContractSchemaMap(skill.GetMapping("inputs"));
            NormalizeFlowContractSchemaMap(skill.GetMapping("outputs"));
        }

        if (root.GetMapping("workflows") is { } workflows)
        {
            foreach (var workflow in workflows.Children.Values.OfType<YamlMappingNode>())
            {
                NormalizeFlowContractSchemaMap(workflow.GetMapping("inputs"));
                NormalizeFlowContractSchemaMap(workflow.GetMapping("outputs"));
            }
        }

        var after = SerializeYamlNode(root);
        return string.Equals(before, after, StringComparison.Ordinal)
            ? yaml
            : after;
    }

    private static void NormalizeFlowContractSchemaMap(YamlMappingNode? contracts)
    {
        if (contracts == null)
            return;

        foreach (var schema in contracts.Children.Values.OfType<YamlMappingNode>())
            NormalizeFlowNullableSchema(schema);
    }

    private static void NormalizeFlowNullableSchema(YamlMappingNode schema)
    {
        var typeKey = Scalar("type");
        var anyOfKey = Scalar("anyOf");
        string? nonNullType = null;

        if (schema.Children.TryGetValue(anyOfKey, out var anyOfNode)
            && anyOfNode is YamlSequenceNode { Children.Count: 2 } variants
            && variants.Children.All(static variant => variant is YamlMappingNode { Children.Count: 1 }))
        {
            var types = variants.Children
                .Cast<YamlMappingNode>()
                .Select(static variant => variant.GetScalar("type"))
                .ToArray();
            var nonNullTypes = types.Where(static type => !string.IsNullOrWhiteSpace(type)
                                                           && !string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            nonNullType = nonNullTypes.Length == 1 ? nonNullTypes[0] : null;
            if (nonNullType != null
                && types.Count(static type => string.Equals(type, "null", StringComparison.OrdinalIgnoreCase)) == 1)
            {
                schema.Children.Remove(anyOfKey);
                schema.Children[typeKey] = Scalar(nonNullType);
                schema.Children[Scalar("nullable")] = Scalar("true");
            }
        }
        else if (schema.Children.TryGetValue(typeKey, out var typeNode)
                 && typeNode is YamlSequenceNode { Children.Count: 2 } typeVariants)
        {
            var types = typeVariants.Children.OfType<YamlScalarNode>()
                .Select(static type => type.Value)
                .ToArray();
            var nonNullTypes = types.Where(static type => !string.IsNullOrWhiteSpace(type)
                                                           && !string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            nonNullType = nonNullTypes.Length == 1 ? nonNullTypes[0] : null;
            if (types.Length == 2
                && nonNullType != null
                && types.Count(static type => string.Equals(type, "null", StringComparison.OrdinalIgnoreCase)) == 1)
            {
                schema.Children[typeKey] = Scalar(nonNullType);
                schema.Children[Scalar("nullable")] = Scalar("true");
            }
        }

        if (schema.GetMapping("properties") is { } properties)
            NormalizeFlowContractSchemaMap(properties);
        if (schema.Children.TryGetValue(Scalar("items"), out var items)
            && items is YamlMappingNode itemSchema)
        {
            NormalizeFlowNullableSchema(itemSchema);
        }
    }

    private static string SerializeYamlNode(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static void NormalizeGeneratedFunctionParameterDocs(YamlMappingNode root)
    {
        NormalizeFunctionsScalar(root);
        if (root.GetMapping("workflows") is not { } workflows)
            return;
        foreach (var workflow in workflows.Children.Values.OfType<YamlMappingNode>())
            NormalizeFunctionsScalar(workflow);
    }

    private static void NormalizeFunctionsScalar(YamlMappingNode owner)
    {
        var key = Scalar("functions");
        if (!owner.Children.TryGetValue(key, out var functionsNode)
            || functionsNode is not YamlScalarNode functions
            || string.IsNullOrWhiteSpace(functions.Value))
        {
            return;
        }

        var normalized = WorkflowPlanSemanticValidator.CompleteInferableFunctionParameterJsDoc(functions.Value);
        if (!string.Equals(normalized, functions.Value, StringComparison.Ordinal))
            owner.Children[key] = Scalar(normalized);
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
        sb.AppendLine("- The main graph may only use compact leaf calls plus support nodes: `set`, `sequence`, `switch`, `parallel`, `loop.sequential`, `loop.parallel`, and exact native orchestration steps locked in `main_required_native_steps_json`.");
        sb.AppendLine("- Emit every required occurrence from `main_required_native_steps_json` as a direct graph step with the exact `type`; preserve its operation/catalog identity semantically and place it at the documented orchestration boundary.");
        sb.AppendLine("- The main graph must not emit `mcp.call`, `llm.call`, `template.render`, `workflow.plan`, an unlisted `human.input`/`emit`, or inline leaf implementation logic.");
        sb.AppendLine("- The main workflow must never use `workflow.plan`, and graph nodes must not inline leaf logic.");
        sb.AppendLine("- Leaf workflows must never call other workflows.");
        sb.AppendLine("- Call a leaf that owns an exactly-one conditional capability group exactly once. Pass its typed decision input unchanged and let that leaf perform the complete switch; do not duplicate the leaf call across parent cases.");
        sb.AppendLine("- Preserve the orchestration algorithm from the normalized prompt and the Main workflow orchestration section.");
        sb.AppendLine("- Use conditionals, switches, loops, or parallel branches when the orchestration requires them.");
        sb.AppendLine("- For container support nodes (`sequence`, `switch`, `parallel`, loops), nested graph nodes are allowed in `steps`, `branches[].steps`, `cases[].steps`, and `default`.");
        sb.AppendLine("- Pass leaf arguments from declared `data.inputs.<name>`, earlier step outputs, loop variables, derived values, or constants.");
        sb.AppendLine("- Route MCP-declared artifact values directly from the producer leaf output to every compatible consumer leaf argument. Reuse one value for fan-out; never derive or fabricate another locator in main.");
        sb.AppendLine("- Every `data.inputs.<name>` reference MUST have an identically named declaration in `graph.inputs` or `document.skill.inputs`.");
        sb.AppendLine("- Keep `graph.inputs` and `document.skill.inputs` synchronized as one public contract. Before returning, scan every `data.inputs.<name>` reference and use only exact declared names; map a public input to a differently named leaf argument instead of inventing an alias input.");
        sb.AppendLine("- Leaf input names are call arguments, not automatically public main inputs.");
        sb.AppendLine("- `generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.");
        sb.AppendLine("- If `leaf_input_candidates_yaml` or `leaf_manifest_json` disagree with `generated_leaf_contracts_yaml`, follow `generated_leaf_contracts_yaml`.");
        sb.AppendLine("- Map public user input names to differently named leaf arguments when their meanings match.");
        sb.AppendLine("- Do not expose loop variables, intermediate values, identifiers, flags, or leaf-only implementation details as public inputs unless the user explicitly requested them.");
        sb.AppendLine("- Use `set` support nodes for data shaping in the main graph: renaming fields, building objects/arrays, constants, and safe type conversions.");
        sb.AppendLine("- Step IDs must be globally unique across the complete main graph, including nested containers, except that every mutually exclusive case and default of one switch may end with the same path-total `set` projection id when all copies declare the same non-empty `output_schema`.");
        sb.AppendLine("- `output_schema` is supported only on `set` steps. Never attach it to switch, sequence, parallel, loop, workflow.call, or another step type.");
        sb.AppendLine("- Required public inputs are validated before main starts. Do not generate missing/empty required-input switches or fallback responses unless the normalized user request explicitly requires them.");
        sb.AppendLine("- A switch case supports only `when` (or `value`) and `steps`; `default` is a step list. Never emit `output` on a case/default item or pretend child fields are flattened onto the switch step.");
        sb.AppendLine("- A `set` support node whose field is produced by a custom function and later passed to a typed leaf input must declare an `output_schema` for that field matching the destination leaf contract. Custom-function results are otherwise opaque to static type validation.");
        sb.AppendLine("- Preserve object and array values as expressions under typed `set.output_schema` fields; do not serialize them to JSON strings merely to satisfy a contract.");
        sb.AppendLine("- Keep exact JSON values intact when passing arrays, objects, numbers, or booleans. Do not stringify a structured leaf output unless a downstream leaf explicitly wants a string.");
        sb.AppendLine("- Keep opaque textual leaf outputs as strings and preserve their raw content when main explicitly parses them; a JSON-decoded string must not be treated as an object or silently normalized to an empty collection.");
        sb.AppendLine("- Before a Human Input confirmation or external write based on analyzed items, add a fail-closed coverage guard. If upstream metadata reports non-empty records/files but the normalized collection is empty, stop that path instead of using the no-findings/no-comments write branch.");
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
        AppendPromptSection(sb, "main_required_native_steps_json", BuildPlannedNativeStepsJson(
            extraction.MainNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
        sb.AppendLine("  finally: []");
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
          finally: []          # optional idempotent cleanup nodes; same shape as steps
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

        Support graph nodes may use only these DSL step types in the main graph (human.input and emit only when listed in main_required_native_steps_json):
        - set: derive constants or mappings.
        - assert.non_null: refine a nullable producer field before passing it to a required leaf input; route the exact refined output and never replace an external artifact with a synthesized fallback.
        - sequence: run nested steps sequentially.
        - switch: choose one branch with cases[].steps and optional default.
        - parallel: run branches[].steps concurrently.
        - loop.sequential or loop.parallel: iterate with input.items or input.over and nested steps.
        - human.input: pause for a required orchestration confirmation or additional input.
        - emit: report a required orchestration message/progress event.

        Support node outputs and safe references:
        - set output: `${data.steps.<set_id>.<field>}`.
        - workflow.call output: `${data.steps.<call_id>.outputs.<leaf_output>}`.
        - sequence output: object keyed by nested step id; nested steps also execute in order on the same path.
        - parallel output: `${data.steps.<parallel_id>.branches}` is an array of branch step-output objects. Do not reference branch child step ids outside the branch.
        - loop output: `${data.steps.<loop_id>.results}` is an array of per-iteration step-output objects and `${data.steps.<loop_id>.count}` is the number of iterations. Do not reference loop child step ids after the loop.
        - loop result item shape: each element of `${data.steps.<loop_id>.results}` is a per-iteration step-output object. If a loop child step `build_item_result` produced fields, read them as `iteration.build_item_result.<field>` when flattening/filtering, not `iteration.<field>`.
        - Never expose raw `${data.steps.<loop_id>.results}` as a public business output. It contains full per-iteration step snapshots and will not match a clean public array contract.
        - To flatten loop results, add a post-loop `set` support node with an `output_schema`, project exact declared fields into a clean array, and point graph.outputs at that set field.
        - If flattening needs array map/filter logic, define a deterministic helper in `graph.functions` and call it from the post-loop `set` input. The renderer copies `graph.functions` to the generated main workflow.
        - Every helper in `graph.functions` must have a JSDoc block immediately before the `function` declaration, including `@param` and `@returns`.
        - Projection helpers must read child step outputs through the iteration snapshot, for example `iteration.build_item_result.status` or `iteration.route_by_classification.summarize_item_result_warning.status`.
        - switch output is path-dependent. To expose one guaranteed result, include a default branch and end every case/default with the same nested set step id and the same closed output_schema; then read `${data.steps.<switch_id>.<shared_result_step_id>.<field>}`. Otherwise do not reference case/default child step ids after the switch unless the reference remains inside that same case/default path.
        - Do not flatten switch child fields onto the switch step. Invalid: `${data.steps.route.result_url}` when `result_url` is produced by a child `set` inside a case/default branch.
        - A switch case has only `when` (or `value`) plus `steps`; it never has an `output` field. The `default` value is only a step list.
        - Never emit `default: |` or `default: >`; those create a string. Emit `default:` followed directly by YAML step-list items.
        - Required graph inputs are already validated by Flow. Do not build switches for missing or empty required inputs unless the user explicitly requested custom fallback behavior.
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
        - Keep business/tool/LLM work inside leaf workflows. The main graph should only orchestrate, derive values, branch, loop, call leaves, and execute exact native orchestration steps listed in main_required_native_steps_json.
        - Put required resource cleanup in graph.finally. Finalizers may reference inputs, previous step outputs, and data.workflow_error.
        - Every finalizer expression must be total on success, failure, and partial execution. Never dereference a switch child, failed/skipped call, or optional result directly; first create a guaranteed top-level support value with an empty cleanup collection default.
        - Cleanup may consume exact artifact/materializer outputs declared in generated_leaf_contracts_yaml and exact caller-chosen creation targets documented by a selected materializer input contract. Never invent generic fields such as `created_directories`, `paths`, or `resources` on a leaf that does not declare them.
        - When cleanup is required for a caller-chosen creation target, derive and register that exact target before invoking the materializer, then reconcile its returned materialized artifact after success without duplicate registry entries. This keeps partial materialization failures cleanable.
        - If a value is required by a generated leaf input contract, pass it in the leaf args or derive it in an earlier support step.
        - Preserve operational artifact provenance across leaf calls. When a consuming leaf uses one of its inputs as a required artifact locator, bind that exact leaf argument directly from a compatible materializer leaf output. Reuse the same unchanged producer expression for every compatible consumer; do not substitute a caller input, literal, template, cast, function, concatenation, or reconstructed path.
        - Do not add MCP, LLM, template, workflow.plan, raw workflow.call, or native orchestration steps not listed in main_required_native_steps_json to the main graph.
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
        sb.AppendLine("Treat `invalid_main_assembly_yaml` as the exact repair base. Preserve every unaffected step id, branch, expression, schema, and nesting relationship; make only the smallest changes directly justified by `main_assembly_validation_error`.");
        sb.AppendLine("Do not re-plan the workflow, rename existing steps, reorder unrelated work, or introduce replacement capabilities while repairing the graph.");
        sb.AppendLine("For nested containers, every reference made after an outer container must resolve through a result path guaranteed by every branch of that outer container. If a branch contains another container, project its shared result into a direct child result step of the outer branch before exposing it outside.");
        if (!string.IsNullOrWhiteSpace(previousResponse))
            AppendPromptSection(sb, "base_candidate_fingerprint", BuildPipelineMainAssemblyFingerprint(previousResponse));
        if (structuredError.Contains("Pipeline main workflow references undeclared inputs", StringComparison.Ordinal))
        {
            sb.AppendLine("Undeclared input reference repair:");
            sb.AppendLine("- Scan the complete corrected graph for every `data.inputs.<name>` reference, including nested branches, loops, and finalizers.");
            sb.AppendLine("- Each referenced name must exactly match one name in both `graph.inputs` and `document.skill.inputs`; use the declared names and allowed paths from the structured diagnostic below.");
            sb.AppendLine("- When an undeclared name is merely an alias, abbreviation, spelling variant, or renamed form of an existing public input, replace every reference with the existing declared name. Do not add a duplicate public input alias.");
            sb.AppendLine("- Add a new public input declaration only when the normalized user request requires a genuinely distinct caller-supplied value. Never add an input merely because a leaf argument uses a different name; map that leaf argument from the semantically matching public input.");
            sb.AppendLine("- After repair, re-check document.skill.inputs, graph.inputs, every leaf args mapping, and every finalizer as one consistent contract.");
        }
        if (structuredError.Contains("conditional_decision_lineage_unproven", StringComparison.Ordinal))
        {
            sb.AppendLine("Conditional decision routing repair:");
            sb.AppendLine("- Preserve the already valid conditional switch and its owning leaf. Repair only the parent workflow.call args and output routing that carry the declared decision field from its producer leaf to its consumer leaf.");
            sb.AppendLine("- Keep the declared decision boundary field name unchanged at every workflow boundary. Use only a direct expression, a same-named `set` projection, or a same-named `assert.non_null` refinement.");
            sb.AppendLine("- Do not alias, recompute, coerce, concatenate, default, or source the decision from an unproven caller input.");
        }
        if (structuredError.Contains("unproven_artifact_provenance", StringComparison.Ordinal))
        {
            sb.AppendLine("Artifact provenance routing repair:");
            sb.AppendLine("- Each diagnostic `caller_bindings` entry identifies the parent leaf call and exact argument path whose current value is unproven. Repair those parent args; do not alter the consuming leaf merely because it reads `data.inputs.<name>`.");
            sb.AppendLine("- Find the compatible materializer leaf output in `generated_leaf_contracts_yaml`, then bind each listed argument directly to `${data.steps.<materializer-call-id>.outputs.<declared-output-name>}` (or the exact equivalent path through a containing branch or loop result). Preserve the producer value unchanged.");
            sb.AppendLine("- Reuse one compatible materializer result for all consumers of the same artifact. Keep every consumer leaf's declared argument name even when producer and consumer field names differ.");
            sb.AppendLine("- A raw public input, literal, set-to-literal, function, template, cast, concatenation, normalized path, or reconstructed value does not prove a materialized artifact. Do not add a public input to bypass the producer.");
        }
        if (!string.IsNullOrWhiteSpace(previousResponse))
            AppendPromptSection(sb, "invalid_main_assembly_yaml", StripMarkdownFences(previousResponse));
        AppendPromptSection(sb, "main_assembly_validation_error", structuredError);
        return sb.ToString();
    }

    private static string BuildPipelineMainAssemblyFingerprint(string? yaml)
    {
        var normalized = NormalizeGeneratedYamlUnsafeExactExpressionScalars(StripMarkdownFences(yaml ?? string.Empty));
        normalized = NormalizeGeneratedUnsafePlainMappingScalars(normalized);
        normalized = NormalizeGeneratedSwitchDefaultStepLists(normalized).Trim();
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static WorkflowRuntimeException BuildPipelineMainAssemblyRepairStalledException(
        int attempt,
        string? bestCandidateFingerprint,
        string diagnosticFingerprint,
        string reason,
        Exception inner)
        => new(
            ErrorCodes.WorkflowPlanRepairStalled,
            reason,
            inner: inner,
            details: new JsonObject
            {
                ["stage"] = "assemble_main_workflow",
                ["classification"] = "plan_defect",
                ["attempt_count"] = attempt,
                ["best_candidate_fingerprint"] = bestCandidateFingerprint,
                ["diagnostic_fingerprint"] = diagnosticFingerprint,
                ["stall_reason"] = "non_improving_main_assembly_repair",
                ["recommended_action"] = "Clarify the requested orchestration or revise the declared contracts; the rejected candidate was not emitted."
            });

    private static GeneratedMainAssembly ParseGeneratedMainAssembly(string yaml, IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var normalizedYaml = NormalizeGeneratedYamlUnsafeExactExpressionScalars(StripMarkdownFences(yaml));
        normalizedYaml = NormalizeGeneratedUnsafePlainMappingScalars(normalizedYaml);
        normalizedYaml = NormalizeGeneratedSwitchDefaultStepLists(normalizedYaml);
        var root = LoadYamlRoot(normalizedYaml);
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

        if (graph.GetSequence("finally") is { } sourceFinalizers)
            AddYaml(main, "finally", RenderGraphStepSequence(sourceFinalizers, leafNames));

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
        else if (TryGetYaml(rendered, "default", out var defaultNode)
                 && defaultNode is YamlScalarNode defaultScalar
                 && TryParseGraphStepSequenceScalar(defaultScalar.Value) is { } parsedDefaultSteps)
        {
            ReplaceYaml(rendered, "default", RenderGraphStepSequence(parsedDefaultSteps, leafNames));
        }

        return rendered;
    }

    private static YamlSequenceNode? TryParseGraphStepSequenceScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var indented = string.Join(
                Environment.NewLine,
                value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(static line => "  " + line));
            var sequence = LoadYamlRoot("items:" + Environment.NewLine + indented).GetSequence("items");
            return sequence == null ? null : (YamlSequenceNode)CloneYamlNode(sequence);
        }
        catch
        {
            return null;
        }
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
                ["contract_role"] = spec?.ContractRole,
                ["concrete_outcome"] = spec?.ConcreteOutcome,
                ["extraction_score"] = spec?.ExtractionScore == null ? null : BuildPipelineExtractionScoreJson(spec.ExtractionScore),
                ["extract_reason"] = spec?.ExtractReason ?? "",
                ["planned_tools"] = spec == null ? new JsonArray() : BuildPlannedToolsJson(spec.PlannedTools),
                ["planned_native_steps"] = spec == null
                    ? new JsonArray()
                    : BuildPlannedNativeStepsJson(spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()),
                ["required_capabilities"] = spec == null ? new JsonArray() : BuildRequiredCapabilitiesJson(spec),
                ["local_operation_ids"] = spec == null
                    ? new JsonArray()
                    : BuildStringArrayJson(spec.LocalOperationIds ?? Array.Empty<string>()),
                ["blueprint"] = BuildPipelineLeafBlueprintJson(leaf.Blueprint),
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

    private static JsonObject BuildGeneratedLeafBlueprintsJson(IReadOnlyList<GeneratedLeafWorkflow> leaves)
    {
        var obj = new JsonObject();
        foreach (var leaf in leaves)
            obj[leaf.Name] = BuildPipelineLeafBlueprintJson(leaf.Blueprint);
        return obj;
    }

    private static JsonObject BuildPipelineLeafBlueprintJson(PipelineLeafBlueprint blueprint)
    {
        return new JsonObject
        {
            ["leaf"] = blueprint.LeafName,
            ["workflow_name"] = blueprint.WorkflowName,
            ["summary"] = blueprint.Summary,
            ["steps"] = BuildPipelineLeafBlueprintStepsJson(blueprint.Steps),
            ["outputs"] = BuildPipelineLeafBlueprintOutputsJson(blueprint.Outputs)
        };
    }

    private static JsonArray BuildPipelineLeafBlueprintStepsJson(IReadOnlyList<PipelineLeafBlueprintStep> steps)
    {
        var array = new JsonArray();
        foreach (var step in steps)
        {
            var obj = new JsonObject
            {
                ["id"] = step.Id,
                ["type"] = step.Type,
                ["purpose"] = step.Purpose
            };
            if (step.PlannedTool != null)
                obj["planned_tool"] = BuildPlannedToolJson(step.PlannedTool);
            if (step.PlannedNativeStep != null)
                obj["planned_native_step"] = BuildPlannedNativeStepJson(step.PlannedNativeStep);
            if (step.OutputSchema != null)
                obj["output_schema"] = step.OutputSchema.DeepClone();
            array.Add((JsonNode)obj);
        }

        return array;
    }

    private static JsonArray BuildPipelineLeafBlueprintOutputsJson(IReadOnlyList<PipelineLeafBlueprintOutput> outputs)
    {
        var array = new JsonArray();
        foreach (var output in outputs)
        {
            var obj = new JsonObject
            {
                ["name"] = output.Name,
                ["expr"] = output.Expr,
                ["source_step"] = output.SourceStepId
            };
            if (output.Schema != null)
                obj["schema"] = output.Schema.DeepClone();
            array.Add((JsonNode)obj);
        }

        return array;
    }

    private static JsonObject BuildPipelineInspectionJson(
        string normalizedMarkdown,
        string annotatedMarkdown,
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        WorkflowDocument finalDoc,
        PipelineMcpContext pipelineMcpContext,
        IReadOnlyList<PipelineQualityEvent> events)
    {
        finalDoc.Workflows.TryGetValue("main", out var mainWorkflow);
        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["leaf_count"] = leaves.Count,
                ["leaf_blueprint_count"] = leaves.Count(static leaf => leaf.Blueprint != null),
                ["repair_count"] = events.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)),
                ["root_cause_count"] = BuildPipelineRootCauses(extraction, events, terminalException: null).Count,
                ["main_step_count"] = mainWorkflow?.Steps.Count ?? 0,
                ["workflow_count"] = finalDoc.Workflows.Count
            },
            ["mcp_context"] = BuildPipelineMcpContextJson(pipelineMcpContext),
            ["normalized_prompt"] = normalizedMarkdown,
            ["annotated_markdown"] = annotatedMarkdown,
            ["extraction_quality_review"] = BuildExtractionQualityReviewJson(extraction.QualityReview),
            ["leaf_manifest"] = BuildGeneratedLeafManifestJson(leaves, extraction),
            ["generated_leaf_blueprints"] = BuildGeneratedLeafBlueprintsJson(leaves),
            ["generated_leaf_contracts"] = BuildGeneratedLeafContractsJson(leaves),
            ["final_main_graph"] = mainWorkflow == null
                ? new JsonObject { ["missing"] = true }
                : BuildWorkflowGraphInspectionJson("main", mainWorkflow, finalDoc.Skill?.Outputs),
            ["repair_history"] = BuildPipelineQualityEventsJson(events.Where(static item => item.Kind.Contains("repair", StringComparison.Ordinal))),
            ["root_causes"] = BuildPipelineRootCausesJson(BuildPipelineRootCauses(extraction, events, terminalException: null))
        };
    }

    private static JsonObject BuildPipelineFailureDetails(
        string normalizedMarkdown,
        string annotatedMarkdown,
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<GeneratedLeafWorkflow> leaves,
        PipelineMcpContext pipelineMcpContext,
        IReadOnlyList<PipelineQualityEvent> events,
        IReadOnlyList<PipelineRootCause> rootCauses,
        string? previousAssemblyResponse,
        string? assembledYaml,
        Exception? terminalException)
    {
        var inspection = new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["leaf_count"] = leaves.Count,
                ["leaf_blueprint_count"] = leaves.Count(static leaf => leaf.Blueprint != null),
                ["repair_count"] = events.Count(static item => item.Kind.Contains("repair", StringComparison.Ordinal)),
                ["root_cause_count"] = rootCauses.Count,
                ["workflow_count"] = 0,
                ["main_step_count"] = 0
            },
            ["mcp_context"] = BuildPipelineMcpContextJson(pipelineMcpContext),
            ["normalized_prompt"] = normalizedMarkdown,
            ["annotated_markdown"] = annotatedMarkdown,
            ["extraction_quality_review"] = BuildExtractionQualityReviewJson(extraction.QualityReview),
            ["leaf_manifest"] = BuildGeneratedLeafManifestJson(leaves, extraction),
            ["generated_leaf_blueprints"] = BuildGeneratedLeafBlueprintsJson(leaves),
            ["generated_leaf_contracts"] = BuildGeneratedLeafContractsJson(leaves),
            ["final_main_graph"] = new JsonObject
            {
                ["missing"] = true,
                ["last_error"] = terminalException?.Message ?? ""
            },
            ["repair_history"] = BuildPipelineQualityEventsJson(events.Where(static item => item.Kind.Contains("repair", StringComparison.Ordinal))),
            ["root_causes"] = BuildPipelineRootCausesJson(rootCauses)
        };

        var details = new JsonObject
        {
            ["root_causes"] = BuildPipelineRootCausesJson(rootCauses),
            ["pipeline_inspection"] = inspection,
            ["events"] = BuildPipelineQualityEventsJson(events)
        };
        if (!string.IsNullOrWhiteSpace(previousAssemblyResponse))
            details["last_main_assembly_response"] = previousAssemblyResponse;
        if (!string.IsNullOrWhiteSpace(assembledYaml))
            details["generated_yaml"] = assembledYaml;
        if (terminalException != null)
            details["terminal_error"] = JsonNode.Parse(BuildStructuredPlanError(terminalException, 0));

        return details;
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
            ["finally"] = BuildStepInspectionArray(workflow.Finally),
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
        PipelineMcpContext pipelineMcpContext,
        IReadOnlyList<PipelineQualityEvent> events)
    {
        finalDoc.Workflows.TryGetValue("main", out var main);
        var mainSteps = main == null
            ? Array.Empty<StepDef>()
            : EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally)).ToArray();
        var totalStepCount = finalDoc.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
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
            ["leaf_blueprint_count"] = leaves.Count(static leaf => leaf.Blueprint != null),
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
            ["warning_count"] = warnings.Count,
            ["root_cause_count"] = BuildPipelineRootCauses(extraction, events, terminalException: null).Count
        };
        if (extraction.QualityReview != null)
        {
            summary["extraction_quality_score"] = extraction.QualityReview.Score;
            summary["extraction_quality_verdict"] = extraction.QualityReview.Verdict;
            summary["extraction_quality_diagnostic_count"] = extraction.QualityReview.Diagnostics.Count;
        }
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
                ["workflow_hierarchy_validated"] = true,
                ["extraction_quality_reviewed"] = extraction.QualityReview != null
            },
            ["extraction"] = new JsonObject
            {
                ["main_workflow_prompt"] = extraction.MainWorkflowPrompt,
                ["validation"] = BuildValidationJson(extraction.ValidationErrors),
                ["root_causes"] = BuildPipelineRootCausesJson(extraction.RootCauses),
                ["quality_review"] = BuildExtractionQualityReviewJson(extraction.QualityReview)
            },
            ["mcp_context"] = BuildPipelineMcpContextJson(pipelineMcpContext),
            ["leaves"] = BuildPipelineQualityLeavesJson(extraction, leaves),
            ["contracts"] = new JsonObject
            {
                ["skill_outputs"] = BuildSchemaMapJson(skillOutputSchemas),
                ["main_outputs"] = BuildSchemaMapJson(mainOutputSchemas),
                ["leaf_outputs"] = BuildPipelineQualityLeafOutputsJson(leaves)
            },
            ["repairs"] = BuildPipelineQualityEventsJson(events.Where(static item => item.Kind.Contains("repair", StringComparison.Ordinal))),
            ["events"] = BuildPipelineQualityEventsJson(events),
            ["root_causes"] = BuildPipelineRootCausesJson(BuildPipelineRootCauses(extraction, events, terminalException: null)),
            ["warnings"] = warnings
        };
    }

    private static JsonObject BuildPipelineMcpContextJson(PipelineMcpContext pipelineMcpContext)
    {
        var serverNames = new JsonArray();
        var toolNames = new JsonArray();
        var promptNames = new JsonArray();
        var servers = new JsonArray();

        foreach (var server in pipelineMcpContext.Servers)
        {
            serverNames.Add((JsonNode)JsonValue.Create(server.Name)!);

            var serverTools = new JsonArray();
            foreach (var tool in server.Tools)
            {
                var displayName = $"{server.Name}/{tool.Name}";
                toolNames.Add((JsonNode)JsonValue.Create(displayName)!);
                serverTools.Add((JsonNode)JsonValue.Create(tool.Name)!);
            }

            var serverPrompts = new JsonArray();
            foreach (var prompt in server.Prompts)
            {
                var displayName = $"{server.Name}/{prompt.Name}";
                promptNames.Add((JsonNode)JsonValue.Create(displayName)!);
                serverPrompts.Add((JsonNode)JsonValue.Create(prompt.Name)!);
            }

            servers.Add((JsonNode)new JsonObject
            {
                ["name"] = server.Name,
                ["discovered"] = server.Discovered,
                ["tool_count"] = server.Tools.Count,
                ["prompt_count"] = server.Prompts.Count,
                ["tools"] = serverTools,
                ["prompts"] = serverPrompts
            });
        }

        return new JsonObject
        {
            ["available"] = pipelineMcpContext.Servers.Count > 0,
            ["selected_server_count"] = pipelineMcpContext.Servers.Count,
            ["selected_tool_count"] = pipelineMcpContext.Servers.Sum(static server => server.Tools.Count),
            ["selected_prompt_count"] = pipelineMcpContext.Servers.Sum(static server => server.Prompts.Count),
            ["server_names"] = serverNames,
            ["tool_names"] = toolNames,
            ["prompt_names"] = promptNames,
            ["servers"] = servers
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
                : EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)).ToArray();
            var item = new JsonObject
            {
                ["name"] = spec.Name,
                ["goal"] = spec.Goal,
                ["description"] = spec.Description,
                ["work_kind"] = spec.WorkKind,
                ["contract_role"] = spec.ContractRole,
                ["concrete_outcome"] = spec.ConcreteOutcome,
                ["extract_reason"] = spec.ExtractReason,
                ["extraction_score"] = spec.ExtractionScore == null ? null : BuildPipelineExtractionScoreJson(spec.ExtractionScore),
                ["planned_tools"] = BuildPlannedToolsJson(spec.PlannedTools),
                ["planned_native_steps"] = BuildPlannedNativeStepsJson(spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()),
                ["required_capabilities"] = BuildRequiredCapabilitiesJson(spec),
                ["local_operation_ids"] = BuildStringArrayJson(spec.LocalOperationIds ?? Array.Empty<string>()),
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
                item["blueprint"] = BuildPipelineLeafBlueprintJson(leaf.Blueprint);
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

    private static JsonNode? BuildExtractionQualityReviewJson(PipelineExtractionQualityReview? review)
    {
        if (review == null)
            return null;

        var diagnostics = new JsonArray();
        foreach (var diagnostic in review.Diagnostics)
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = diagnostic.Code,
                ["kind"] = diagnostic.Kind,
                ["severity"] = diagnostic.Severity,
                ["remediation_surface"] = diagnostic.RemediationSurface,
                ["leaf_name"] = diagnostic.LeafName ?? "",
                ["message"] = diagnostic.Message,
                ["recommendation"] = diagnostic.Recommendation ?? "",
                ["evidence_qualified"] = diagnostic.EvidenceQualified,
                ["evidence"] = BuildPipelineExtractionQualityEvidenceJson(diagnostic.Evidence)
            });
        }

        return new JsonObject
        {
            ["score"] = review.Score,
            ["threshold"] = PipelineExtractionQualityReviewThreshold,
            ["verdict"] = review.Verdict,
            ["retry"] = ShouldRetryPipelineExtractionReview(review),
            ["retry_guidance"] = review.RetryGuidance ?? "",
            ["diagnostics"] = diagnostics
        };
    }

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

    private static IReadOnlyList<PipelineRootCause> BuildPipelineRootCauses(
        WorkflowPipelineExtraction extraction,
        IEnumerable<PipelineQualityEvent> events,
        Exception? terminalException)
    {
        var rootCauses = extraction.RootCauses.ToList();
        foreach (var cause in BuildExtractionQualityReviewRootCauses(extraction.QualityReview))
        {
            AddPipelineRootCause(
                rootCauses,
                cause.Category,
                cause.Phase,
                cause.LeafName,
                cause.OutputName,
                cause.InvalidPath,
                cause.Code,
                cause.Message,
                cause.Primary);
        }

        foreach (var item in events)
        {
            var category = item.Kind switch
            {
                "leaf_contract_repair" or "leaf_contract_repair_failed" => "weak_leaf_contract",
                "leaf_blueprint_yaml_mismatch" => "leaf_blueprint_yaml_mismatch",
                "leaf_blueprint_invalid" => "leaf_blueprint_invalid",
                "main_assembly_retry" when !string.IsNullOrWhiteSpace(item.Reason) => "downstream_symptom",
                "main_assembly_retry" => "main_contract_mismatch",
                _ => null
            };
            if (category == null)
                continue;

            var code = item.Kind switch
            {
                "leaf_contract_repair" => "PIPELINE_LEAF_CONTRACT_REPAIR",
                "leaf_contract_repair_failed" => "PIPELINE_LEAF_CONTRACT_REPAIR_FAILED",
                "leaf_blueprint_yaml_mismatch" => "PIPELINE_LEAF_BLUEPRINT_YAML_MISMATCH",
                "leaf_blueprint_invalid" => "PIPELINE_LEAF_BLUEPRINT_INVALID",
                "main_assembly_retry" when string.Equals(category, "downstream_symptom", StringComparison.Ordinal) => "PIPELINE_MAIN_DOWNSTREAM_SYMPTOM",
                "main_assembly_retry" => "PIPELINE_MAIN_CONTRACT_MISMATCH",
                _ => item.Kind.ToUpperInvariant()
            };
            var message = item.Message
                          ?? item.Reason
                          ?? $"Pipeline quality event '{item.Kind}' in phase '{item.Phase ?? "unknown"}'.";
            AddPipelineRootCause(
                rootCauses,
                category,
                item.Phase ?? "pipeline_generation",
                item.LeafName,
                item.OutputName,
                item.InvalidPath,
                code,
                message,
                primary: !string.Equals(category, "downstream_symptom", StringComparison.Ordinal));
        }

        AddPipelineRootCausesFromTerminalDetails(rootCauses, terminalException);

        if (terminalException != null && rootCauses.Count == 0)
        {
            AddPipelineRootCause(
                rootCauses,
                "main_contract_mismatch",
                "pipeline_generation",
                leafName: null,
                outputName: null,
                invalidPath: null,
                code: terminalException is WorkflowRuntimeException workflowEx ? workflowEx.Code : terminalException.GetType().Name,
                terminalException.Message,
                primary: true);
        }

        return rootCauses;
    }

    private static void AddPipelineRootCausesFromTerminalDetails(
        List<PipelineRootCause> rootCauses,
        Exception? terminalException)
    {
        if (terminalException is not WorkflowRuntimeException { Details: JsonObject details }
            || details["root_causes"] is not JsonArray terminalRootCauses)
        {
            return;
        }

        foreach (var item in terminalRootCauses.OfType<JsonObject>())
        {
            var category = GetStringProperty(item, "category");
            var phase = GetStringProperty(item, "phase") ?? "pipeline_generation";
            var message = GetStringProperty(item, "message");
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(message))
                continue;

            AddPipelineRootCause(
                rootCauses,
                category,
                phase,
                leafName: GetStringProperty(item, "leaf") ?? GetStringProperty(item, "leaf_name"),
                outputName: GetStringProperty(item, "output") ?? GetStringProperty(item, "output_name"),
                invalidPath: GetStringProperty(item, "invalid_path") ?? GetStringProperty(item, "consumer_field"),
                code: GetStringProperty(item, "code"),
                message,
                primary: item["primary"] is not JsonValue primaryValue
                         || !primaryValue.TryGetValue<bool>(out var primary)
                         || primary);
        }
    }

    private static JsonArray BuildPipelineRootCausesJson(IEnumerable<PipelineRootCause> rootCauses)
    {
        var array = new JsonArray();
        foreach (var cause in rootCauses)
        {
            var obj = new JsonObject
            {
                ["category"] = cause.Category,
                ["phase"] = cause.Phase,
                ["message"] = cause.Message,
                ["primary"] = cause.Primary
            };
            if (!string.IsNullOrWhiteSpace(cause.LeafName))
                obj["leaf"] = cause.LeafName;
            if (!string.IsNullOrWhiteSpace(cause.OutputName))
                obj["output"] = cause.OutputName;
            if (!string.IsNullOrWhiteSpace(cause.InvalidPath))
                obj["invalid_path"] = cause.InvalidPath;
            if (!string.IsNullOrWhiteSpace(cause.Code))
                obj["code"] = cause.Code;

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

        foreach (var warning in extraction.QualityWarnings ?? Array.Empty<string>())
        {
            warnings.Add((JsonNode)new JsonObject
            {
                ["code"] = "PIPELINE_EXTRACTION_QUALITY_REVIEW_WARNING",
                ["message"] = warning
            });
        }

        if (extraction.QualityReview != null)
        {
            foreach (var diagnostic in extraction.QualityReview.Diagnostics.Where(static diagnostic =>
                         string.Equals(diagnostic.Severity, "warning", StringComparison.Ordinal)))
            {
                warnings.Add((JsonNode)new JsonObject
                {
                    ["code"] = diagnostic.Code,
                    ["leaf"] = diagnostic.LeafName ?? "",
                    ["message"] = diagnostic.Message,
                    ["recommendation"] = diagnostic.Recommendation ?? ""
                });
            }
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

    private static void MaterializeCompositeMainWorkflowOutputs(
        YamlMappingNode mainWorkflowNode,
        YamlMappingNode? skillOutputs)
    {
        if (skillOutputs == null
            || mainWorkflowNode.GetSequence("steps") is not { } steps
            || mainWorkflowNode.GetMapping("outputs") is not { } outputs)
        {
            return;
        }

        var usedStepIds = new HashSet<string>(StringComparer.Ordinal);
        CollectYamlStepIds(steps, usedStepIds);
        if (mainWorkflowNode.GetSequence("finally") is { } finalizers)
            CollectYamlStepIds(finalizers, usedStepIds);

        foreach (var (keyNode, outputValue) in outputs.Children.ToArray())
        {
            if (keyNode is not YamlScalarNode { Value.Length: > 0 } key
                || outputValue is not YamlMappingNode composite
                || composite.GetScalar("expr") != null
                || !skillOutputs.Children.TryGetValue(Scalar(key.Value), out var skillOutputSchema))
            {
                continue;
            }

            var outputType = FlowTypeDescriptorConverter.FromJsonSchema(WorkflowParser.YamlToJson(skillOutputSchema));
            if (outputType.Kind != FlowTypeKind.Object
                || outputType.Properties.Count == 0
                || composite.Children.Keys.OfType<YamlScalarNode>().Any(property =>
                    string.IsNullOrWhiteSpace(property.Value)
                    || !outputType.Properties.ContainsKey(property.Value)))
            {
                continue;
            }

            var propertySchema = CloneYamlNode(skillOutputSchema);
            WorkflowPlanContractNormalizer.NormalizeSetOutputSchema(propertySchema);
            if (WorkflowPlanContractNormalizer.IsWeakYamlOutputSchema(propertySchema))
                continue;

            var normalizedName = Regex.Replace(
                key.Value,
                "[^A-Za-z0-9_]+",
                "_",
                RegexOptions.CultureInvariant).Trim('_');
            if (normalizedName.Length == 0)
                normalizedName = "value";
            var baseStepId = $"project_main_output_{normalizedName}";
            var stepId = baseStepId;
            for (var suffix = 2; !usedStepIds.Add(stepId); suffix++)
                stepId = $"{baseStepId}_{suffix}";

            var outputProperties = new YamlMappingNode { { Scalar(key.Value), propertySchema } };
            var outputSchema = new YamlMappingNode
            {
                { Scalar("type"), Scalar("object") },
                { Scalar("properties"), outputProperties },
                { Scalar("required"), new YamlSequenceNode(Scalar(key.Value)) },
                { Scalar("additionalProperties"), Scalar("false") }
            };
            var input = new YamlMappingNode { { Scalar(key.Value), CloneYamlNode(composite) } };
            var projection = new YamlMappingNode
            {
                { Scalar("id"), Scalar(stepId) },
                { Scalar("type"), Scalar("set") },
                { Scalar("output_schema"), outputSchema },
                { Scalar("input"), input }
            };
            steps.Add(projection);

            var expression = $"${{data.steps.{stepId}.{key.Value}}}";
            if (BuildWorkflowOutputFromSkillSchema(skillOutputSchema, expression) is { } strengthened)
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

    internal static void ValidateInferredMainArtifactInputs(
        IReadOnlyDictionary<string, JsonNode?> mainInputs,
        IReadOnlyDictionary<string, JsonNode?> configuredInputs,
        string rawPrompt)
    {
        if (configuredInputs.Count > 0)
            return;

        var diagnostics = new JsonArray();
        foreach (var (name, schema) in mainInputs)
        {
            var description = schema is JsonObject schemaObject
                ? GetStringProperty(schemaObject, "description") ?? string.Empty
                : string.Empty;
            var field = new CapabilitySchemaField("/" + name, "string", description, Array.Empty<string>());
            var artifactKind = GetOperationalArtifactKind(field);
            if (artifactKind == null
                || IsExplicitCallerArtifactInput(rawPrompt, field, artifactKind))
            {
                continue;
            }

            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "PIPELINE_MAIN_UNREQUESTED_ARTIFACT_INPUT",
                ["phase"] = "pipeline_main_input_validation",
                ["workflow"] = "main",
                ["field"] = $"inputs.{name}",
                ["invalid_path"] = $"data.inputs.{name}",
                ["message"] = $"The inferred parent contract exposes operational artifact input '{name}', but the user did not request that artifact as a runtime input.",
                ["expected"] = "Route a documented output from an upstream artifact-producing capability, or keep this input only when the user explicitly requests it.",
                ["hint"] = "Do not expose leaf-only implementation prerequisites as new public inputs."
            });
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_main_input_validation",
            ["summary"] = $"{diagnostics.Count} unrequested operational artifact input diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Remove implementation-only artifact inputs from document.skill.inputs and graph.inputs.")!,
                (JsonNode)JsonValue.Create("Route the artifact from an earlier leaf whose documented external/action output creates or proves it.")!,
                (JsonNode)JsonValue.Create("Preserve only public runtime inputs explicitly requested by the user.")!)
        };
        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow exposed an unrequested operational artifact as a public input. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
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

        var allowedPaths = new JsonArray(mainInputs.Keys
            .Order(StringComparer.Ordinal)
            .Select(static name => (JsonNode)JsonValue.Create("data.inputs." + name)!)
            .ToArray());
        var diagnostics = new JsonArray(undeclared.Select(name => (JsonNode)new JsonObject
        {
            ["code"] = "PIPELINE_MAIN_UNDECLARED_INPUT_REFERENCE",
            ["phase"] = "pipeline_main_input_validation",
            ["workflow"] = "main",
            ["field"] = "graph",
            ["invalid_path"] = "data.inputs." + name,
            ["allowed_paths"] = allowedPaths.DeepClone(),
            ["message"] = $"Main references undeclared public input '{name}'.",
            ["expected"] = "Reference an exactly declared public input name, or declare a genuinely distinct caller input required by the normalized request.",
            ["hint"] = "Prefer mapping an existing public input to differently named leaf arguments; do not create duplicate aliases."
        }).ToArray());
        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_main_input_validation",
            ["summary"] = $"{undeclared.Length} undeclared main input reference(s)",
            ["declared_inputs"] = new JsonArray(mainInputs.Keys
                .Order(StringComparer.Ordinal)
                .Select(static name => (JsonNode)JsonValue.Create(name)!)
                .ToArray()),
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Rename alias/spelling-variant references to an existing declared public input and map that value into differently named leaf arguments.")!,
                (JsonNode)JsonValue.Create("Add a new declaration only for a genuinely distinct caller value required by the normalized request.")!,
                (JsonNode)JsonValue.Create("Keep document.skill.inputs and graph.inputs identical after repair.")!)
        };
        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Pipeline main workflow references undeclared inputs: " + string.Join(", ", undeclared)
            + " | repair diagnostics: " + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
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

        foreach (var step in EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally)))
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

        foreach (var step in EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally)))
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

        foreach (var diagnostic in WorkflowPlanPipelineQualityAnalyzer.AnalyzeExternalArtifactReadiness(doc))
            diagnostics.Add(diagnostic?.DeepClone());

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "pipeline_main_dataflow_validation",
            ["summary"] = $"{diagnostics.Count} pipeline main dataflow diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["root_causes"] = WorkflowPlanPipelineQualityAnalyzer.BuildRootCauses(diagnostics),
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Reprompt only main assembly. Do not assign a raw URL/link input directly to narrower identifier fields such as owner, repo, id, number, name, or slug.")!,
                (JsonNode)JsonValue.Create("Use an existing typed leaf output for canonical parsed values, or add/route through a deterministic support step that truly parses the identifier.")!,
                (JsonNode)JsonValue.Create("Do not synthesize operational artifact locators such as project/workspace/root/path/directory/file values in main before external work uses them.")!,
                (JsonNode)JsonValue.Create("Use caller-provided workflow inputs for pre-existing artifacts, or pass a typed output from an upstream external-producing leaf/action that proves the artifact exists.")!)
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
        foreach (var step in EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally)))
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

        var stepsById = EnumerateSteps(main.Steps).Concat(EnumerateSteps(main.Finally))
            .Where(static step => !string.IsNullOrWhiteSpace(step.Id))
            .GroupBy(static step => step.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
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
            foreach (var step in EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
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
        try
        {
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw WorkflowParser.CreateYamlSyntaxException(yaml, ex);
        }
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
            ["main_local_operation_ids"] = BuildStringArrayJson(extraction.MainLocalOperationIds ?? Array.Empty<string>()),
            ["main_native_steps"] = BuildPlannedNativeStepsJson(extraction.MainNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()),
            ["validation"] = BuildValidationJson(extraction.ValidationErrors),
            ["root_causes"] = BuildPipelineRootCausesJson(extraction.RootCauses),
            ["quality_review"] = BuildExtractionQualityReviewJson(extraction.QualityReview),
            ["quality_warnings"] = BuildStringArrayJson(extraction.QualityWarnings ?? Array.Empty<string>())
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
            ["contract_role"] = spec.ContractRole,
            ["concrete_outcome"] = spec.ConcreteOutcome,
            ["owned_operation_ids"] = BuildStringArrayJson(spec.OwnedOperationIds ?? Array.Empty<string>()),
            ["inputs"] = BuildStringMapJson(spec.Inputs),
            ["outputs"] = BuildStringMapJson(spec.Outputs),
            ["input_schemas"] = BuildSchemaMapJson(spec.InputSchemas),
            ["output_schemas"] = BuildSchemaMapJson(spec.OutputSchemas),
            ["planned_tools"] = BuildPlannedToolsJson(spec.PlannedTools),
            ["planned_native_steps"] = BuildPlannedNativeStepsJson(spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()),
            ["required_capabilities"] = BuildRequiredCapabilitiesJson(spec),
            ["local_operation_ids"] = BuildStringArrayJson(spec.LocalOperationIds ?? Array.Empty<string>()),
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
            array.Add((JsonNode)BuildPlannedToolJson(tool));

        return array;
    }

    private static JsonArray BuildRequiredCapabilitiesJson(WorkflowPipelineSubworkflowSpec spec)
    {
        var array = new JsonArray();
        foreach (var tool in spec.PlannedTools.Where(static tool => tool.Required))
            array.Add((JsonNode)BuildPlannedToolJson(tool));
        foreach (var step in (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
                     .Where(static step => step.Required))
            array.Add((JsonNode)BuildPlannedNativeStepJson(step));

        return array;
    }

    private static JsonArray BuildPlannedNativeStepsJson(
        IReadOnlyList<PipelinePlannedNativeStep> plannedNativeSteps)
    {
        var array = new JsonArray();
        foreach (var step in plannedNativeSteps)
            array.Add((JsonNode)BuildPlannedNativeStepJson(step));
        return array;
    }

    private static JsonObject BuildPlannedNativeStepJson(PipelinePlannedNativeStep step)
        => new()
        {
            ["resolution"] = "native",
            ["method"] = step.Method,
            ["operation_ids"] = BuildStringArrayJson(step.OperationIds),
            ["catalog_ids"] = BuildStringArrayJson(step.CatalogIds),
            ["required"] = step.Required,
            ["purpose"] = step.Purpose
        };

    private static JsonObject BuildPlannedToolJson(PipelinePlannedTool tool)
    {
        var result = new JsonObject
        {
            ["server"] = tool.Server,
            ["kind"] = tool.Kind,
            ["method"] = tool.Method,
            ["operation_ids"] = BuildStringArrayJson(tool.OperationIds),
            ["catalog_ids"] = BuildStringArrayJson(tool.CatalogIds),
            ["request_bindings"] = BuildRequestBindingsJson(tool.RequestBindings),
            ["required"] = tool.Required,
            ["purpose"] = tool.Purpose,
            ["consumes"] = BuildStringArrayJson(tool.Consumes),
            ["produces"] = BuildStringArrayJson(tool.Produces),
            ["activation"] = tool.Activation == null
                ? null
                : new JsonObject
                {
                    ["mode"] = tool.Activation.Mode,
                    ["group"] = tool.Activation.Group,
                    ["decision_operation_id"] = tool.Activation.DecisionOperationId,
                    ["decision_output_path"] = tool.Activation.DecisionOutputPath,
                    ["allowed_values"] = new JsonArray(tool.Activation.AllowedValues
                        .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["no_effect_values"] = new JsonArray(tool.Activation.NoEffectValues
                        .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["decision_contract_source"] = tool.Activation.DecisionContractSource,
                    ["decision_producer_catalog_id"] = tool.Activation.DecisionProducerCatalogId,
                    ["decision_input_operation_ids"] = BuildStringArrayJson(
                        tool.Activation.DecisionInputOperationIds),
                    ["branch_value"] = tool.Activation.BranchValue
                }
        };
        return result;
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
        IReadOnlyList<string> ValidationErrors,
        IReadOnlyList<PipelineRootCause> RootCauses,
        PipelineExtractionQualityReview? QualityReview = null,
        IReadOnlyList<string>? QualityWarnings = null,
        IReadOnlyList<string>? MainLocalOperationIds = null,
        IReadOnlyList<PipelinePlannedNativeStep>? MainNativeSteps = null);

    private sealed record GeneratedMainAssembly(
        YamlMappingNode MainWorkflowNode,
        string? DocumentName,
        YamlMappingNode? SkillNode);

    private sealed record PipelineStructuredDecisionRequirement(
        ResolvedCapability Producer,
        McpCapabilityActivation Activation);

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
        string? ContractRole,
        string? ConcreteOutcome,
        IReadOnlyDictionary<string, string> Inputs,
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyDictionary<string, JsonNode?> InputSchemas,
        IReadOnlyDictionary<string, JsonNode?> OutputSchemas,
        IReadOnlyList<PipelinePlannedTool> PlannedTools,
        PipelineExtractionScore? ExtractionScore,
        string ExtractReason,
        string Content,
        string GenerationPrompt,
        IReadOnlyList<string>? LocalOperationIds = null,
        IReadOnlyList<PipelinePlannedNativeStep>? PlannedNativeSteps = null)
    {
        public IReadOnlyList<string> OwnedOperationIds { get; init; } = Array.Empty<string>();
    }

    private sealed record PipelinePlannedTool(
        string Server,
        string Kind,
        string Method,
        bool Required,
        string? Purpose,
        IReadOnlyList<string> Consumes,
        IReadOnlyList<string> Produces,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings,
        IReadOnlyList<string> OperationIds,
        IReadOnlyList<string> CatalogIds,
        McpCapabilityActivation? Activation = null)
    {
        public string? ExternalEffectKind { get; init; }
    }

    private sealed record PipelinePlannedNativeStep(
        string Method,
        bool Required,
        string? Purpose,
        IReadOnlyList<string> OperationIds,
        IReadOnlyList<string> CatalogIds);

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
        string? ContractRole,
        string? ConcreteOutcome,
        IReadOnlyList<string> OwnedOperationIds,
        IReadOnlyDictionary<string, JsonNode?> Inputs,
        IReadOnlyDictionary<string, JsonNode?> Outputs,
        IReadOnlyList<PipelinePlannedTool> PlannedTools);

    private sealed record GeneratedLeafWorkflow(
        string Name,
        string GeneratedWorkflowName,
        WorkflowDocument Document,
        string Yaml,
        PipelineLeafBlueprint Blueprint,
        IReadOnlyList<PipelineQualityEvent> QualityEvents,
        IReadOnlyList<PipelineStructuredDecisionRequirement>? StructuredDecisionRequirements = null);

    [GeneratedRegex(@"(?ms)^:::subworkflow\s+name=""(?<name>[a-z0-9_]+)""\s*\n(?<body>.*?)^:::\s*$")]
    private static partial Regex SubworkflowBlockRegex();

    [GeneratedRegex(@"(?m)^:::subworkflow\b")]
    private static partial Regex SubworkflowMarkerRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCaseNameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\b(clone|cleanup|clean\s+up|delete|remove|write|save|create|update|post|publish|push|commit|send|download|upload|fetch|retrieve|read|list|call|install|restore)\b|\b(?:run|execute)\b.{0,60}\b(?:command|script|process|test|lint|build)\w*\b|\b(?:inspect|scan|discover)\b.{0,60}\b(?:repository|workspace|filesystem|files?|projects?|configuration)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalWorkIntentRegex();

    [GeneratedRegex(@"\b(?:ask|instruct)\s+(?:the\s+)?[A-Za-z][A-Za-z0-9_.-]*(?:\s+(?:agent|assistant|model|tool|capability))?\s+to\b|\bdelegate\b.{0,40}\bto\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelegatedExternalWorkIntentRegex();

    [GeneratedRegex(@"\b(?:call|invoke|execute|run|use)\s+(?:the\s+|an?\s+)?(?:configured\s+)?(?:external|mcp|llm|ai|agent|assistant|model|tool|capability)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitExternalCapabilityInvocationRegex();

    [GeneratedRegex(@"\b(?:install|restore)\b|\b(?:run|execute)\b.{0,60}\b(?:command|script|process|test|lint|build)\w*\b|\b(?:inspect|scan|discover)\b.{0,60}\b(?:repository|workspace|filesystem|files?|projects?|configuration)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalExecutionOrStateInspectionRegex();

    [GeneratedRegex(@"\b(rename|constant|guard|map|mapping|field\s+mapping|aggregate|aggregation|route|routing|shape|shaping|filter|sort|select)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeterministicShapingIntentRegex();

    [GeneratedRegex(@"\b(parse|parsing|normalize|normalise|classify|classification|summari[sz]e|summary|synthesi[sz]e|analy[sz]e|analysis|validate|deduplicate|rank|score|calculate|compute|resolve|derive|merge|group|correlate|extract|transform|reconcile|compare|evaluate)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlgorithmicExtractionIntentRegex();

    [GeneratedRegex(@"\b(parse|parsing|validate|validation|normalize|normalise|deduplicate|filter|select|rank|score|sort|classify|classification|map|mapping|shape|shaping|aggregate|aggregation|transform|project|projection)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongLocalProcessingLeafRegex();

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
