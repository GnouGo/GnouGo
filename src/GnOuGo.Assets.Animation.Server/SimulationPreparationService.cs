using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation.Server;

public sealed class SimulationPreparationService
{
    public const int MaxWorkflowBytes = 1024 * 1024;
    public const int MaxInputBytes = 256 * 1024;

    public ValidationResponse Validate(SimulationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sizeDiagnostic = ValidatePayloadSizes(request);
        if (sizeDiagnostic is not null)
            return Invalid(sizeDiagnostic);

        WorkflowPreviewValidationResult validation;
        using (var activity = AnimationServerTelemetry.ActivitySource.StartActivity("animation.preview.parse", ActivityKind.Internal))
        {
            try
            {
                validation = new WorkflowPreviewValidationResult
                {
                    Document = WorkflowPreviewParser.Parse(request.Workflow),
                    Diagnostics = []
                };
                activity?.SetTag("animation.preview.parse_success", true);
            }
            catch (WorkflowPreviewParseException exception)
            {
                activity?.SetTag("animation.preview.parse_success", false);
                validation = new WorkflowPreviewValidationResult
                {
                    Document = new WorkflowPreviewDocument(),
                    Diagnostics =
                    [
                        new WorkflowPreviewDiagnostic(
                            "YAML_PARSE",
                            exception.Message,
                            WorkflowPreviewDiagnosticSeverity.Error)
                    ]
                };
            }
        }

        using var validationActivity = AnimationServerTelemetry.ActivitySource.StartActivity("animation.preview.validate", ActivityKind.Internal);
        if (validation.Diagnostics.Count == 0)
            validation = WorkflowPreviewValidator.Validate(validation.Document);
        var diagnostics = validation.Diagnostics.Select(ToDto).ToList();
        if (request.FailAt is not null && !validation.FailureTargets.Any(target =>
                string.Equals(target.WorkflowName, request.FailAt.WorkflowName, StringComparison.Ordinal)
                && string.Equals(target.StepId, request.FailAt.StepId, StringComparison.Ordinal)))
        {
            diagnostics.Add(new PreviewDiagnosticDto(
                "FAILURE_TARGET_NOT_FOUND",
                $"Failure target '{request.FailAt.WorkflowName}/{request.FailAt.StepId}' does not identify a previewable leaf step.",
                "Error",
                request.FailAt.WorkflowName,
                request.FailAt.StepId,
                "failAt"));
        }

        var response = new ValidationResponse(
            Valid: validation.IsValid && diagnostics.All(static diagnostic => !string.Equals(diagnostic.Severity, "Error", StringComparison.Ordinal)),
            Entrypoint: validation.Entrypoint,
            Diagnostics: diagnostics,
            FailureTargets: validation.FailureTargets.Select(static target =>
                new FailureTargetDto(target.WorkflowName, target.StepId, target.StepType, target.Label)).ToArray(),
            Workflows: validation.Document.Workflows.Select(pair =>
                new WorkflowSummaryDto(
                    pair.Key,
                    CountSteps(pair.Value.Steps),
                    string.Equals(pair.Key, validation.Entrypoint, StringComparison.Ordinal))).ToArray());

        validationActivity?.SetTag("animation.preview.valid", response.Valid);
        validationActivity?.SetTag("animation.preview.workflow_count", response.Workflows.Count);
        validationActivity?.SetTag("animation.preview.diagnostic_count", response.Diagnostics.Count);
        return response;
    }

    public PreparedSimulation Prepare(SimulationRequest request)
    {
        var validationResponse = Validate(request);
        if (!validationResponse.Valid)
        {
            var hardLimit = validationResponse.Diagnostics.FirstOrDefault(static diagnostic =>
                diagnostic.Code is "WORKFLOW_TOO_LARGE" or "INPUTS_TOO_LARGE");
            throw new SimulationRequestException(
                hardLimit?.Code ?? "INVALID_PREVIEW",
                hardLimit?.Message ?? "The workflow preview is invalid.",
                validationResponse.Diagnostics);
        }

        var validation = WorkflowPreviewValidator.ParseAndValidate(request.Workflow);
        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);
        GnouGnouAnimationPlan plan;
        using (var activity = AnimationServerTelemetry.ActivitySource.StartActivity("animation.preview.plan", ActivityKind.Internal))
        {
            try
            {
                plan = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions
                {
                    Seed = seed,
                    Scene = request.Scene,
                    Inputs = request.Inputs?.DeepClone(),
                    FailAt = request.FailAt
                });
            }
            catch (GnouGnouAnimationPlanException exception)
            {
                throw new SimulationRequestException(exception.Code, exception.Message);
            }
            activity?.SetTag("animation.seed", seed);
            activity?.SetTag("animation.scene", plan.Scene.ToString());
            activity?.SetTag("animation.actor_count", plan.Actors.Count);
            activity?.SetTag("animation.event_count", plan.Events.Count);
        }

        GnouGnouAnimationSvgDocument svg;
        using (var activity = AnimationServerTelemetry.ActivitySource.StartActivity("animation.preview.render", ActivityKind.Internal))
        {
            svg = GnouGnouAnimationSvgRenderer.Render(plan);
            activity?.SetTag("animation.svg_bytes", Encoding.UTF8.GetByteCount(svg.Svg));
        }

        IReadOnlyList<SimulationEvent> events;
        try
        {
            events = WorkflowSimulationScheduler.Schedule(plan, request.Speed);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SimulationRequestException("INVALID_SPEED", exception.Message);
        }
        var duration = events.Count == 0 ? 0 : events.Max(static item => item.OffsetMs + item.DurationMs);
        var prepared = new SimulationPreparedData(
            SimulationId: Guid.NewGuid().ToString("N"),
            Seed: seed,
            Scene: plan.Scene,
            DurationMs: duration,
            Svg: svg.Svg,
            ActorCount: plan.Actors.Count(static actor => actor.Kind != AnimationActorKind.Clone),
            StepEventCount: events.Count(static item => item.Type == SimulationEventTypes.StepStarted),
            TaskObjectCount: plan.Tasks.Count(static item => item.Kind == "project-parcel"),
            CanvasWidth: svg.Width,
            CanvasHeight: svg.Height,
            LaneCount: plan.Lanes.Count,
            NodeCount: plan.Nodes.Count,
            Warnings: plan.Warnings.Select(ToDto).ToArray());
        return new PreparedSimulation(prepared, events);
    }

    private static PreviewDiagnosticDto? ValidatePayloadSizes(SimulationRequest request)
    {
        if (request.Workflow is null)
            return new PreviewDiagnosticDto("WORKFLOW_REQUIRED", "Workflow YAML is required.", "Error", null, null, "workflow");
        if (Encoding.UTF8.GetByteCount(request.Workflow) > MaxWorkflowBytes)
            return new PreviewDiagnosticDto("WORKFLOW_TOO_LARGE", $"Workflow YAML exceeds the {MaxWorkflowBytes} byte limit.", "Error", null, null, "workflow");
        if (request.Inputs is not null && Encoding.UTF8.GetByteCount(request.Inputs.ToJsonString()) > MaxInputBytes)
            return new PreviewDiagnosticDto("INPUTS_TOO_LARGE", $"Input JSON exceeds the {MaxInputBytes} byte limit.", "Error", null, null, "inputs");
        return null;
    }

    private static ValidationResponse Invalid(PreviewDiagnosticDto diagnostic) =>
        new(false, null, [diagnostic], [], []);

    private static PreviewDiagnosticDto ToDto(WorkflowPreviewDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity.ToString(),
        diagnostic.WorkflowName,
        diagnostic.StepId,
        diagnostic.Field);

    private static int CountSteps(IEnumerable<WorkflowPreviewStep> steps)
    {
        var count = 0;
        foreach (var step in steps)
        {
            count++;
            if (step.Steps is { } childSteps) count += CountSteps(childSteps);
            if (step.Branches is { } branches) count += branches.Sum(static branch => CountSteps(branch.Steps));
            if (step.Cases is { } cases) count += cases.Sum(static item => CountSteps(item.Steps));
            if (step.Default is { } defaultSteps) count += CountSteps(defaultSteps);
        }
        return count;
    }
}

public sealed record PreparedSimulation(SimulationPreparedData Metadata, IReadOnlyList<SimulationEvent> Events);

public sealed class SimulationRequestException(
    string code,
    string message,
    IReadOnlyList<PreviewDiagnosticDto>? diagnostics = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<PreviewDiagnosticDto>? Diagnostics { get; } = diagnostics;
}
