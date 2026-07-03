using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Main workflow execution engine.
/// </summary>
public sealed class WorkflowEngine : IWorkflowRuntime
{
    private readonly StepExecutorRegistry _registry;
    private ExpressionEvaluator _evaluator;
    private StringInterpolator _interpolator;
    private int _totalStepsExecuted;

    public ILLMClient? LLMClient { get; set; }
    public ILLMCapabilityResolver? LLMCapabilities { get; set; }
    public IWorkflowFetcher? WorkflowFetcher { get; set; }
    public ITemplateEngine? TemplateEngine { get; set; }
    public IMcpClientFactory? McpClientFactory { get; set; }
    public IHumanInputProvider? HumanInputProvider { get; set; }
    public IWorkflowCheckpointer? Checkpointer { get; set; }
    public IWorkflowCallResolver WorkflowCallResolver { get; set; } = new DefaultWorkflowCallResolver();
    public IWorkflowCandidateProvider? WorkflowCandidateProvider { get; set; }
    public IWorkflowTelemetry Telemetry { get; set; } = NullWorkflowTelemetry.Instance;
    public LlmRuntimeDefaults LlmDefaults { get; set; } = new();
    public FetchPolicy? FetchPolicy { get; set; }
    public ExecutionLimits Limits { get; set; } = new();
    public CompiledDocument? CompiledDocument { get; private set; }

    /// <summary>Optional structured logger for runtime diagnostics (MCP errors, etc.).</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Optional memory cache for MCP server capability listings.</summary>
    public IMemoryCache? McpCache { get; set; }

    /// <summary>Sliding expiration for cached MCP server capability listings.</summary>
    public TimeSpan McpCacheSlidingExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Additional functions registered from Jint scripts.</summary>
    public Dictionary<string, Func<JsonNode?[], JsonNode?>> ScriptFunctions { get; } = new();

    public WorkflowEngine(StepExecutorRegistry? registry = null)
    {
        _registry = registry ?? CreateDefaultRegistry();
        _evaluator = new ExpressionEvaluator();
        _interpolator = new StringInterpolator(_evaluator);
    }

    public async Task<RunResult> ExecuteAsync(CompiledWorkflow workflow, JsonNode? inputs, CancellationToken ct)
    {
        _totalStepsExecuted = 0;
        CompiledDocument = workflow.Document;

        var executionScope = PrepareEvaluator(workflow);

        var data = new JsonObject
        {
            ["inputs"] = inputs?.DeepClone() ?? new JsonObject(),
            ["steps"] = new JsonObject(),
            ["env"] = new JsonObject()
        };

        // Validate inputs against rich type schema
        if (workflow.Source?.Inputs != null && data["inputs"] is JsonObject inputsObj)
        {
            var typeErrors = InputTypeValidator.Validate(workflow.Source, inputsObj);
            if (typeErrors.Count > 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"Input validation failed: {string.Join("; ", typeErrors)}");
        }

        var result = new RunResult { Success = true };

        var workflowSpan = Telemetry.WorkflowStart(new WorkflowTelemetryInfo
        {
            WorkflowName = workflow.Name,
            DocumentName = workflow.Document?.Source?.Name,
            Inputs = inputs?.DeepClone(),
            SourceText = workflow.Document?.Source?.RawYaml,
            SourceFormat = "yaml"
        });
        WorkflowTelemetryInputAttributes.Apply(workflowSpan, data["inputs"]);

        var workflowSw = Stopwatch.StartNew();

        Logger.LogInformation("Workflow '{WorkflowName}' starting (document: {DocumentName})",
            workflow.Name, workflow.Document?.Source?.Name ?? "(inline)");

        try
        {
            await ExecuteStepsAsync(workflow.Steps, data, result, Limits, 0, new HashSet<string>(), executionScope, ct, workflowSpan);

            // Evaluate outputs
            if (workflow.Outputs != null)
            {
                result.Outputs = EvaluateWorkflowOutputs(workflow.Outputs, data, executionScope, workflow.Name);
            }
            else
            {
                result.Outputs = data["steps"]?.DeepClone();
            }

            Logger.LogInformation("Workflow '{WorkflowName}' completed successfully in {DurationMs:F1}ms ({StepsExecuted} steps)",
                workflow.Name, workflowSw.Elapsed.TotalMilliseconds, _totalStepsExecuted);
        }
        catch (WorkflowRuntimeException ex)
        {
            result.Success = false;
            result.Error = ex.ToWorkflowError();
            Logger.LogError(ex, "Workflow '{WorkflowName}' failed: [{ErrorCode}] {ErrorMessage}",
                workflow.Name, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = new WorkflowError
            {
                Code = "CANCELLED",
                Message = "Workflow execution cancelled",
                Retryable = true
            };
            Logger.LogWarning("Workflow '{WorkflowName}' was cancelled after {DurationMs:F1}ms",
                workflow.Name, workflowSw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = new WorkflowError
            {
                Code = "INTERNAL_ERROR",
                Message = ex.Message,
                Retryable = false
            };
            Logger.LogError(ex, "Workflow '{WorkflowName}' internal error: {ErrorMessage}",
                workflow.Name, ex.Message);
        }
        finally
        {
            workflowSw.Stop();
            Telemetry.WorkflowEnd(workflowSpan, new WorkflowResultInfo
            {
                Success = result.Success,
                StepsExecuted = _totalStepsExecuted,
                Duration = workflowSw.Elapsed,
                ErrorCode = result.Error?.Code,
                ErrorMessage = result.Error?.Message
            });
            workflowSpan.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Resume a workflow from a previously saved checkpoint.
    /// Requires <see cref="Checkpointer"/> to be configured.
    /// </summary>
    public async Task<RunResult> ResumeAsync(string runId, CompiledWorkflow workflow, CancellationToken ct)
    {
        if (Checkpointer == null)
            throw new InvalidOperationException("ResumeAsync requires a configured IWorkflowCheckpointer.");

        var checkpoint = await Checkpointer.LoadAsync(runId, ct)
            ?? throw new WorkflowRuntimeException("CHECKPOINT_NOT_FOUND",
                $"No checkpoint found for run '{runId}'.");

        _totalStepsExecuted = 0;
        CompiledDocument = workflow.Document;
        Limits.RunId = runId;

        var executionScope = PrepareEvaluator(workflow);

        // Restore data from checkpoint
        var data = new JsonObject
        {
            ["inputs"] = checkpoint.Inputs?.DeepClone() ?? new JsonObject(),
            ["steps"] = checkpoint.StepOutputs.DeepClone(),
            ["env"] = new JsonObject()
        };

        var result = new RunResult { Success = true };

        var workflowSpan = Telemetry.WorkflowStart(new WorkflowTelemetryInfo
        {
            WorkflowName = workflow.Name,
            DocumentName = workflow.Document?.Source?.Name,
            Inputs = checkpoint.Inputs?.DeepClone(),
            SourceText = workflow.Document?.Source?.RawYaml,
            SourceFormat = "yaml"
        });
        WorkflowTelemetryInputAttributes.Apply(workflowSpan, data["inputs"]);

        var workflowSw = Stopwatch.StartNew();
        Logger.LogInformation("Workflow '{WorkflowName}' resuming from step index {StartIndex} (runId: {RunId})",
            workflow.Name, checkpoint.NextStepIndex, runId);

        try
        {
            await ExecuteStepsAsync(workflow.Steps, data, result, Limits, 0, new HashSet<string>(), executionScope, ct, workflowSpan, checkpoint.NextStepIndex);

            // Evaluate outputs
            if (workflow.Outputs != null)
            {
                result.Outputs = EvaluateWorkflowOutputs(workflow.Outputs, data, executionScope, workflow.Name);
            }
            else
            {
                result.Outputs = data["steps"]?.DeepClone();
            }

            // Mark checkpoint as completed
            checkpoint.Status = "completed";
            await Checkpointer.SaveAsync(checkpoint, ct);

            Logger.LogInformation("Workflow '{WorkflowName}' resumed and completed in {DurationMs:F1}ms",
                workflow.Name, workflowSw.Elapsed.TotalMilliseconds);
        }
        catch (WorkflowRuntimeException ex)
        {
            result.Success = false;
            result.Error = ex.ToWorkflowError();
            checkpoint.Status = "failed";
            await Checkpointer.SaveAsync(checkpoint, ct);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = new WorkflowError { Code = "CANCELLED", Message = "Workflow execution cancelled", Retryable = true };
            checkpoint.Status = "paused";
            await Checkpointer.SaveAsync(checkpoint, ct);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = new WorkflowError { Code = "INTERNAL_ERROR", Message = ex.Message, Retryable = false };
            checkpoint.Status = "failed";
            await Checkpointer.SaveAsync(checkpoint, ct);
        }
        finally
        {
            workflowSw.Stop();
            Telemetry.WorkflowEnd(workflowSpan, new WorkflowResultInfo
            {
                Success = result.Success,
                StepsExecuted = _totalStepsExecuted,
                Duration = workflowSw.Elapsed,
                ErrorCode = result.Error?.Code,
                ErrorMessage = result.Error?.Message
            });
            workflowSpan.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Executes a sub-workflow under an existing telemetry span without creating a detached trace.
    /// Intended for executors that need isolated engine state, such as parallel workflow routing.
    /// </summary>
    public async Task<RunResult> ExecuteChildWorkflowAsync(
        CompiledWorkflow workflow,
        JsonNode? inputs,
        ExecutionLimits limits,
        int callDepth,
        HashSet<string> callStack,
        ITelemetrySpan? parentSpan,
        CancellationToken ct)
    {
        _totalStepsExecuted = 0;
        CompiledDocument = workflow.Document;
        var executionScope = PrepareEvaluator(workflow);

        var data = new JsonObject
        {
            ["inputs"] = inputs?.DeepClone() ?? new JsonObject(),
            ["steps"] = new JsonObject(),
            ["env"] = new JsonObject()
        };

        if (workflow.Source?.Inputs != null && data["inputs"] is JsonObject inputsObj)
        {
            var typeErrors = InputTypeValidator.Validate(workflow.Source, inputsObj);
            if (typeErrors.Count > 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"Input validation failed: {string.Join("; ", typeErrors)}");
        }

        var result = new RunResult { Success = true };
        var workflowInfo = new WorkflowTelemetryInfo
        {
            WorkflowName = workflow.Name,
            DocumentName = workflow.Document?.Source?.Name,
            Inputs = inputs?.DeepClone(),
            SourceText = workflow.Document?.Source?.RawYaml,
            SourceFormat = "yaml"
        };
        var workflowSpan = parentSpan is null
            ? Telemetry.WorkflowStart(workflowInfo)
            : Telemetry.WorkflowStart(parentSpan, workflowInfo);
        WorkflowTelemetryInputAttributes.Apply(workflowSpan, data["inputs"]);

        var workflowSw = Stopwatch.StartNew();
        Logger.LogInformation("Child workflow '{WorkflowName}' starting (document: {DocumentName})",
            workflow.Name, workflow.Document?.Source?.Name ?? "(inline)");

        try
        {
            await ExecuteStepsAsync(workflow.Steps, data, result, limits, callDepth, callStack, executionScope, ct, workflowSpan);

            if (workflow.Outputs != null)
            {
                result.Outputs = EvaluateWorkflowOutputs(workflow.Outputs, data, executionScope, workflow.Name);
            }
            else
            {
                result.Outputs = data["steps"]?.DeepClone();
            }

            Logger.LogInformation("Child workflow '{WorkflowName}' completed successfully in {DurationMs:F1}ms ({StepsExecuted} steps)",
                workflow.Name, workflowSw.Elapsed.TotalMilliseconds, _totalStepsExecuted);
        }
        catch (WorkflowRuntimeException ex)
        {
            result.Success = false;
            result.Error = ex.ToWorkflowError();
            Logger.LogError(ex, "Child workflow '{WorkflowName}' failed: [{ErrorCode}] {ErrorMessage}",
                workflow.Name, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = new WorkflowError
            {
                Code = "CANCELLED",
                Message = "Workflow execution cancelled",
                Retryable = true
            };
            Logger.LogWarning("Child workflow '{WorkflowName}' was cancelled after {DurationMs:F1}ms",
                workflow.Name, workflowSw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = new WorkflowError
            {
                Code = "INTERNAL_ERROR",
                Message = ex.Message,
                Retryable = false
            };
            Logger.LogError(ex, "Child workflow '{WorkflowName}' internal error: {ErrorMessage}",
                workflow.Name, ex.Message);
        }
        finally
        {
            workflowSw.Stop();
            Telemetry.WorkflowEnd(workflowSpan, new WorkflowResultInfo
            {
                Success = result.Success,
                StepsExecuted = _totalStepsExecuted,
                Duration = workflowSw.Elapsed,
                ErrorCode = result.Error?.Code,
                ErrorMessage = result.Error?.Message
            });
            workflowSpan.Dispose();
        }

        return result;
    }

    public async Task ExecuteStepsAsync(
        List<CompiledStep> steps,
        JsonObject data,
        RunResult result,
        ExecutionLimits limits,
        int callDepth,
        HashSet<string> callStack,
        CancellationToken ct,
        ITelemetrySpan? parentSpan = null,
        int startFromIndex = 0)
    {
        var executionScope = new WorkflowExecutionScope(null, _evaluator, _interpolator);
        await ExecuteStepsAsync(steps, data, result, limits, callDepth, callStack, executionScope, ct, parentSpan, startFromIndex);
    }

    internal async Task ExecuteStepsAsync(
        List<CompiledStep> steps,
        JsonObject data,
        RunResult result,
        ExecutionLimits limits,
        int callDepth,
        HashSet<string> callStack,
        WorkflowExecutionScope executionScope,
        CancellationToken ct,
        ITelemetrySpan? parentSpan = null,
        int startFromIndex = 0)
    {
        parentSpan ??= NullWorkflowTelemetry.Instance.WorkflowStart(new WorkflowTelemetryInfo());

        for (var stepIndex = startFromIndex; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            ct.ThrowIfCancellationRequested();

            var stepCount = Interlocked.Increment(ref _totalStepsExecuted);
            if (stepCount > limits.MaxTotalStepsExecuted)
                throw new WorkflowRuntimeException(ErrorCodes.LoopLimit,
                    $"Total steps executed ({stepCount}) exceeds limit ({limits.MaxTotalStepsExecuted})");

            var stepResult = new StepResult
            {
                StepId = step.Id,
                StepType = step.Type,
                Status = StepStatus.Running
            };
            result.StepResults.Add(stepResult);

            var sw = Stopwatch.StartNew();
            IStepSpan? stepSpan = null;
            JsonNode? resolvedInput = null;

            try
            {
                // 1. Evaluate if guard
                if (step.Source.If != null)
                {
                    var guardResult = executionScope.Interpolator.Interpolate(step.Source.If, data);
                    if (!ExpressionEvaluator.GetBool(guardResult))
                    {
                        stepSpan ??= Telemetry.StepStart(parentSpan, new StepTelemetryInfo
                        {
                            StepId = step.Id,
                            StepType = step.Type,
                            CallDepth = callDepth
                        });

                        stepResult.Status = StepStatus.Skipped;
                        stepResult.Duration = sw.Elapsed;
                        Telemetry.StepEnd(stepSpan, new StepResultInfo
                        {
                            Status = StepStatus.Skipped,
                            Duration = sw.Elapsed
                        });
                        continue;
                    }
                }

                // 2. Resolve input expressions
                if (step.Source.Input != null)
                {
                    if (step.Type == "loop.sequential" && step.Source.Input is JsonObject loopInput && loopInput.ContainsKey("while"))
                    {
                        var inputClone = loopInput.DeepClone() as JsonObject ?? new JsonObject();
                        var whileExpr = inputClone["while"]?.DeepClone();
                        inputClone.Remove("while");
                        var resolvedLoopInput = executionScope.Interpolator.ResolveDeep(inputClone, data) as JsonObject ?? inputClone;
                        if (whileExpr != null)
                            resolvedLoopInput["while"] = whileExpr;
                        resolvedInput = resolvedLoopInput;
                    }
                    else
                    {
                        resolvedInput = executionScope.Interpolator.ResolveDeep(step.Source.Input.DeepClone(), data);
                    }
                }

                // Open telemetry span for this step after input resolution so the live stream can expose it.
                var stepTelemetryInfo = new StepTelemetryInfo
                {
                    StepId = step.Id,
                    StepType = step.Type,
                    Input = resolvedInput?.DeepClone(),
                    CallDepth = callDepth
                };
                stepSpan = Telemetry.StepStart(parentSpan, stepTelemetryInfo);

                Logger.LogDebug("Step '{StepId}' ({StepType}) starting at depth {CallDepth}",
                    step.Id, step.Type, callDepth);

                // 2b. Log resolved input as span event (GenAI content convention)
                if (limits.LogStepContent && resolvedInput != null)
                {
                    stepSpan.AddEvent("gnougo-flow.step.input", new[]
                    {
                        new KeyValuePair<string, object?>("gnougo-flow.step.id", step.Id),
                        new KeyValuePair<string, object?>("gnougo-flow.step.type", step.Type),
                        new KeyValuePair<string, object?>("gnougo-flow.step.call_depth", callDepth),
                        new KeyValuePair<string, object?>("gnougo-flow.content.input", resolvedInput.ToJsonString())
                    });
                }

                // 3. Execute with retry — the executor writes telemetry attributes on stepSpan
                var output = await ExecuteWithRetryAsync(step, data, resolvedInput, limits, callDepth, callStack, executionScope, ct, stepSpan);

                // 4. Write output to data.steps.<id>
                var stepsObj = data["steps"] as JsonObject ?? new JsonObject();
                stepsObj[step.Id] = output?.DeepClone();
                data["steps"] = stepsObj;

                // Write to output alias
                if (step.Source.Output != null)
                    data[step.Source.Output] = output?.DeepClone();

                // 4b. Log output as span event (GenAI content convention)
                if (limits.LogStepContent && output != null)
                {
                    stepSpan.AddEvent("gnougo-flow.step.output", new[]
                    {
                        new KeyValuePair<string, object?>("gnougo-flow.step.id", step.Id),
                        new KeyValuePair<string, object?>("gnougo-flow.step.type", step.Type),
                        new KeyValuePair<string, object?>("gnougo-flow.step.call_depth", callDepth),
                        new KeyValuePair<string, object?>("gnougo-flow.content.output", output.ToJsonString())
                    });
                }

                stepResult.Output = output;
                stepResult.Status = StepStatus.Succeeded;

                Logger.LogInformation("Step '{StepId}' ({StepType}) succeeded in {DurationMs:F1}ms",
                    step.Id, step.Type, sw.Elapsed.TotalMilliseconds);

                Telemetry.StepEnd(stepSpan, new StepResultInfo
                {
                    Status = StepStatus.Succeeded,
                    Duration = sw.Elapsed,
                    Output = output
                });

                // ── Checkpoint: save progress after each successful top-level step ──
                if (callDepth == 0 && Checkpointer != null && limits.RunId != null)
                {
                    var checkpoint = new WorkflowCheckpoint
                    {
                        RunId = limits.RunId,
                        WorkflowName = CompiledDocument?.Source?.Name ?? "",
                        WorkflowYaml = CompiledDocument?.Source?.RawYaml ?? "",
                        NextStepIndex = stepIndex + 1,
                        StepOutputs = (data["steps"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject(),
                        Inputs = data["inputs"]?.DeepClone(),
                        Status = "running",
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    await Checkpointer.SaveAsync(checkpoint, ct);
                }
            }
            catch (WorkflowRuntimeException ex)
            {
                stepSpan ??= Telemetry.StepStart(parentSpan, new StepTelemetryInfo
                {
                    StepId = step.Id,
                    StepType = step.Type,
                    Input = resolvedInput?.DeepClone(),
                    CallDepth = callDepth
                });

                stepResult.Error = ex.ToWorkflowError();

                Logger.LogError(ex, "Step '{StepId}' ({StepType}) failed: [{ErrorCode}] {ErrorMessage}",
                    step.Id, step.Type, ex.Code, ex.Message);

                // Apply on_error handler
                if (step.Source.OnError != null)
                {
                    var handled = HandleOnError(step.Source.OnError, ex, step, data, executionScope);
                    if (handled.action == "continue")
                    {
                        stepResult.Status = StepStatus.Succeeded;
                        if (handled.output != null)
                        {
                            var stepsObj2 = data["steps"] as JsonObject ?? new JsonObject();
                            stepsObj2[step.Id] = handled.output.DeepClone();
                            data["steps"] = stepsObj2;

                            if (step.Source.Output != null)
                                data[step.Source.Output] = handled.output.DeepClone();

                            stepResult.Output = handled.output;
                        }
                        stepResult.Duration = sw.Elapsed;
                        Telemetry.StepEnd(stepSpan, new StepResultInfo
                        {
                            Status = StepStatus.Succeeded,
                            Duration = sw.Elapsed,
                            ErrorCode = ex.Code,
                            ErrorMessage = ex.Message,
                            GenAiFinishReason = "error_handled"
                        });
                        continue;
                    }
                }

                stepResult.Status = StepStatus.Failed;
                stepResult.Duration = sw.Elapsed;
                Telemetry.StepEnd(stepSpan, new StepResultInfo
                {
                    Status = StepStatus.Failed,
                    Duration = sw.Elapsed,
                    ErrorCode = ex.Code,
                    ErrorMessage = ex.Message,
                    GenAiFinishReason = "error"
                });
                throw;
            }
            finally
            {
                stepResult.Duration = sw.Elapsed;
                stepSpan?.Dispose();
            }
        }
    }

    private async Task<JsonNode?> ExecuteWithRetryAsync(
        CompiledStep step,
        JsonObject data,
        JsonNode? resolvedInput,
        ExecutionLimits limits,
        int callDepth,
        HashSet<string> callStack,
        WorkflowExecutionScope executionScope,
        CancellationToken ct,
        IStepSpan? stepSpan = null)
    {
        var retry = step.Source.Retry;
        var maxAttempts = (retry?.Max ?? 1);
        var backoffMs = retry?.BackoffMs ?? 1000;
        var backoffMult = retry?.BackoffMult ?? 2.0;
        var jitterMs = retry?.JitterMs ?? 0;

        WorkflowRuntimeException? lastEx = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var executor = _registry.Get(step.Type)
                    ?? throw new WorkflowRuntimeException(ErrorCodes.StepTypeUnknown, $"Unknown step type: {step.Type}");

                // Inject resolved input into step source for executor use
                var ctx = new StepExecutionContext
                {
                    Step = step,
                    Data = data,
                    Engine = this,
                    Limits = limits,
                    CallDepth = callDepth,
                    CallStack = callStack,
                    ExecutionScope = executionScope,
                    TelemetrySpan = stepSpan
                };

                // Store resolved input in data temporarily
                if (resolvedInput != null)
                {
                    var stepsObj = data["steps"] as JsonObject ?? new JsonObject();
                    stepsObj[$"__{step.Id}_input__"] = resolvedInput.DeepClone();
                    data["steps"] = stepsObj;
                }

                var output = await executor.ExecuteAsync(ctx, ct);

                // Clean up temp input
                if (data["steps"] is JsonObject so)
                    so.Remove($"__{step.Id}_input__");

                return output;
            }
            catch (WorkflowRuntimeException ex) when (attempt < maxAttempts - 1 && ex.Retryable)
            {
                lastEx = ex;
                var delay = backoffMs + (jitterMs > 0 ? Random.Shared.Next(0, jitterMs) : 0);
                await Task.Delay(delay, ct);
                backoffMs = (int)(backoffMs * backoffMult);
            }
        }

        throw lastEx ?? new WorkflowRuntimeException(ErrorCodes.EvalError, "Execution failed after retries");
    }

    private (string action, JsonNode? output) HandleOnError(
        OnErrorDef onError,
        WorkflowRuntimeException ex,
        CompiledStep step,
        JsonObject data,
        WorkflowExecutionScope executionScope)
    {
        // Build error context
        var errorContext = data.DeepClone() as JsonObject ?? new JsonObject();
        var errorObject = new JsonObject
        {
            ["code"] = ex.Code,
            ["type"] = ex.Code,
            ["message"] = ex.Message,
            ["retryable"] = ex.Retryable
        };
        if (ex.Details != null)
            errorObject["details"] = ex.Details.DeepClone();
        errorContext["error"] = errorObject;
        errorContext["step"] = new JsonObject
        {
            ["id"] = step.Id,
            ["type"] = step.Type
        };

        foreach (var c in onError.Cases)
        {
            // Evaluate case condition
            if (c.If != null)
            {
                var guardResult = executionScope.Interpolator.Interpolate(c.If, errorContext);
                if (!ExpressionEvaluator.GetBool(guardResult))
                    continue;
            }

            // Match found
            JsonNode? output = null;
            if (c.SetOutput != null)
                output = executionScope.Interpolator.ResolveDeep(c.SetOutput.DeepClone(), errorContext);

            return (c.Action, output);
        }

        return ("stop", null);
    }

    /// <summary>
    /// Get the resolved input for a step.
    /// </summary>
    public JsonNode? GetResolvedInput(StepExecutionContext ctx)
    {
        if (ctx.Data["steps"] is JsonObject so && so.ContainsKey($"__{ctx.Step.Id}_input__"))
            return so[$"__{ctx.Step.Id}_input__"];
        return ctx.Step.Source.Input;
    }

    /// <summary>
    /// Resolves the effective LLM provider/model for a step by applying runtime defaults
    /// when the workflow input omits one or both values.
    /// </summary>
    public (string? Provider, string? Model) ResolveLlmTarget(string? provider, string? model)
    {
        var resolvedProvider = string.IsNullOrWhiteSpace(provider) ? LlmDefaults.Provider : provider;
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? LlmDefaults.Model : model;

        return (
            string.IsNullOrWhiteSpace(resolvedProvider) ? null : resolvedProvider,
            string.IsNullOrWhiteSpace(resolvedModel) ? null : resolvedModel);
    }

    public ExpressionEvaluator Evaluator => _evaluator;
    public StringInterpolator Interpolator => _interpolator;
    public StepExecutorRegistry Registry => _registry;

    internal WorkflowExecutionScope CreateExecutionScopeForWorkflow(CompiledWorkflow workflow)
    {
        var scriptFunctions = new Dictionary<string, Func<JsonNode?[], JsonNode?>>();
        var jint = new JintSandbox();

        if (workflow.Document?.Source?.Functions != null)
        {
            var globalFns = jint.LoadFunctions(workflow.Document.Source.Functions);
            foreach (var kv in globalFns)
                scriptFunctions[kv.Key] = kv.Value;
        }

        if (workflow.Source?.Functions != null)
        {
            var localFns = jint.LoadFunctions(workflow.Source.Functions);
            foreach (var kv in localFns)
                scriptFunctions[kv.Key] = kv.Value;
        }

        foreach (var kv in ScriptFunctions)
            scriptFunctions[kv.Key] = kv.Value;

        var evaluator = CreateExpressionEvaluator(scriptFunctions, Limits);
        return new WorkflowExecutionScope(workflow, evaluator, new StringInterpolator(evaluator));
    }

    private WorkflowExecutionScope PrepareEvaluator(CompiledWorkflow workflow)
    {
        var executionScope = CreateExecutionScopeForWorkflow(workflow);
        _evaluator = executionScope.Evaluator;
        _interpolator = executionScope.Interpolator;
        return executionScope;
    }

    private static ExpressionEvaluator CreateExpressionEvaluator(
        Dictionary<string, Func<JsonNode?[], JsonNode?>> scriptFunctions,
        ExecutionLimits limits)
    {
        return new ExpressionEvaluator(
            scriptFunctions,
            limits.MaxExpressionStatements,
            TimeSpan.FromSeconds(limits.ExpressionTimeoutSeconds),
            limits.ExpressionMemoryLimitBytes);
    }

    /// <summary>
    /// Evaluates an <see cref="OutputDef"/> against the data context.
    /// Handles both simple expression outputs and nested object outputs (backward-compat).
    /// </summary>
    internal JsonNode? EvaluateOutputDef(Models.OutputDef def, JsonObject data)
        => EvaluateOutputDef(def, data, new WorkflowExecutionScope(null, _evaluator, _interpolator));

    internal JsonNode? EvaluateOutputDef(Models.OutputDef def, JsonObject data, WorkflowExecutionScope executionScope)
    {
        // Simple expression output
        if (!string.IsNullOrEmpty(def.Expr))
            return executionScope.Interpolator.Interpolate(def.Expr, data)?.DeepClone();

        // Nested object output (backward-compat: outputs without expr, with properties containing expressions)
        if (def.Properties != null)
        {
            var obj = new JsonObject();
            foreach (var (key, propDef) in def.Properties)
            {
                obj[key] = EvaluateOutputDef(propDef, data, executionScope)?.DeepClone();
            }
            return obj;
        }

        return null;
    }

    internal JsonObject EvaluateWorkflowOutputs(
        IReadOnlyDictionary<string, Models.OutputDef> outputs,
        JsonObject data,
        WorkflowExecutionScope executionScope,
        string workflowName)
    {
        var outputObj = new JsonObject();
        foreach (var (name, definition) in outputs)
        {
            var value = EvaluateOutputDef(definition, data, executionScope);
            ValidateWorkflowOutputValue(workflowName, name, definition, value);
            outputObj[name] = value?.DeepClone();
        }

        return outputObj;
    }

    private static void ValidateWorkflowOutputValue(
        string workflowName,
        string outputName,
        Models.OutputDef definition,
        JsonNode? value)
    {
        if (string.Equals(definition.Type, "any", StringComparison.OrdinalIgnoreCase))
            return;

        if (value == null && WorkflowOutputAllowsNull(definition))
            return;

        var schema = FlowTypeDescriptorConverter.ToRuntimeJsonSchema(
            FlowTypeDescriptorConverter.FromOutputDef(definition));
        var errors = JsonSchemaContractValidator.ValidateInstance(value, schema);
        if (errors.Count == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.InputValidation,
            $"Workflow '{workflowName}' output '{outputName}' does not satisfy declared output type '{definition.Type}': {string.Join("; ", errors)}");
    }

    private static bool WorkflowOutputAllowsNull(Models.OutputDef definition)
    {
        if (string.Equals(definition.Type, "any", StringComparison.OrdinalIgnoreCase))
            return true;

        return definition.Expr.Contains("?.", StringComparison.Ordinal)
               || definition.Expr.Contains("?? null", StringComparison.Ordinal)
               || definition.Expr.Contains(": null", StringComparison.Ordinal);
    }

    private static StepExecutorRegistry CreateDefaultRegistry()
    {
        var registry = new StepExecutorRegistry();
        registry.Register(new Executors.SequenceExecutor());
        registry.Register(new Executors.ParallelExecutor());
        registry.Register(new Executors.LoopSequentialExecutor());
        registry.Register(new Executors.LoopParallelExecutor());
        registry.Register(new Executors.SwitchExecutor());
        registry.Register(new Executors.SetExecutor());
        registry.Register(new Executors.AssertNonNullExecutor());
        registry.Register(new Executors.TemplateRenderExecutor());
        registry.Register(new Executors.LlmCallExecutor());
        registry.Register(new Executors.WorkflowCallExecutor());
        registry.Register(new Executors.WorkflowRouteExecutor());
        registry.Register(new Executors.WorkflowPlanExecutor());
        registry.Register(new Executors.WorkflowExecuteExecutor());
        registry.Register(new Executors.McpCallExecutor());
        registry.Register(new Executors.McpListExecutor());
        registry.Register(new Executors.EmitExecutor());
        registry.Register(new Executors.HumanInputExecutor());
        return registry;
    }
}
