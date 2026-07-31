using System.Text.Json.Serialization;
using GnOuGo.Assets.Animation;
using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Agent.Server.SmartFlow;

public sealed record AnimationPreparedPayload(
    string Svg,
    int Width,
    int Height,
    int Seed,
    AnimationSceneKind Scene,
    string Entrypoint,
    int LaneCount,
    int NodeCount);

public sealed record AnimationStreamPayload(
    AnimationPreparedPayload? Prepared = null,
    AnimationScenePatch? ScenePatch = null,
    SimulationEvent? Event = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AnimationStreamPayload))]
[JsonSerializable(typeof(AnimationPreparedPayload))]
[JsonSerializable(typeof(AnimationScenePatch))]
[JsonSerializable(typeof(SimulationEvent))]
internal partial class AgentAnimationJsonContext : JsonSerializerContext;

/// <summary>
/// Adapts neutral execution signals to the autonomous animation library.
/// Flow-specific span interpretation remains in <see cref="AgentStreamingTelemetry"/>.
/// </summary>
public sealed class AgentWorkflowAnimationBridge
{
    private readonly WorkflowLiveAnimationSession _session;
    private readonly Action<SmartFlowEvent> _emit;
    private readonly string _entrypoint;
    private readonly object _sync = new();
    private bool _hasWorkflowStarted;

    private AgentWorkflowAnimationBridge(
        WorkflowLiveAnimationSession session,
        string entrypoint,
        Action<SmartFlowEvent> emit)
    {
        _session = session;
        _entrypoint = entrypoint;
        _emit = emit;
    }

    public static AgentWorkflowAnimationBridge Create(
        string? sourceText,
        string workflowName,
        string correlationId,
        Action<SmartFlowEvent> emit,
        out SmartFlowEvent preparedEvent)
    {
        ArgumentNullException.ThrowIfNull(emit);
        var seed = StableSeed(correlationId);
        var options = new GnouGnouAnimationOptions
        {
            Seed = seed,
            Scene = AnimationSceneKind.Random
        };

        var validation = TryValidate(sourceText, workflowName);
        var plan = GnouGnouAnimationPlanner.BuildLive(validation, options);
        var rendered = GnouGnouAnimationSvgRenderer.Render(plan, new GnouGnouAnimationRenderOptions
        {
            Title = $"Live GnOuGo execution · {workflowName}",
            Description = "A live, telemetry-driven view of the current GnOuGo workflow execution."
        });
        var prepared = new AnimationPreparedPayload(
            rendered.Svg,
            rendered.Width,
            rendered.Height,
            plan.Seed,
            plan.Scene,
            plan.Entrypoint,
            plan.Lanes.Count,
            plan.Nodes.Count);
        preparedEvent = new SmartFlowEvent(
            "animation.prepared",
            null,
            Animation: new AnimationStreamPayload(Prepared: prepared));
        return new AgentWorkflowAnimationBridge(
            new WorkflowLiveAnimationSession(plan, options),
            plan.Entrypoint,
            emit);
    }

    public void Apply(AnimationExecutionSignal signal)
    {
        lock (_sync)
        {
            if (signal.Kind == AnimationExecutionSignalKind.WorkflowStarted)
                _hasWorkflowStarted = true;
            Emit(_session.Apply(signal));
        }
    }

    /// <summary>
    /// Gives pre-execution validation failures a visible terminal path. Flow can
    /// reject inputs before opening its first telemetry span; without this
    /// fallback the prepared scene would otherwise remain permanently static.
    /// </summary>
    public void FailBeforeWorkflowStart(string? message)
    {
        lock (_sync)
        {
            if (_hasWorkflowStarted)
                return;

            const string instanceId = "workflow-preflight";
            _hasWorkflowStarted = true;
            Emit(_session.Apply(new AnimationExecutionSignal
            {
                Kind = AnimationExecutionSignalKind.WorkflowStarted,
                WorkflowInstanceId = instanceId,
                WorkflowName = _entrypoint,
                Status = SimulationStatus.Running,
                Message = $"Workflow '{_entrypoint}' is starting."
            }));
            Emit(_session.Apply(new AnimationExecutionSignal
            {
                Kind = AnimationExecutionSignalKind.WorkflowCompleted,
                WorkflowInstanceId = instanceId,
                WorkflowName = _entrypoint,
                Status = SimulationStatus.Failed,
                Message = string.IsNullOrWhiteSpace(message)
                    ? "The workflow failed before its first step could start."
                    : message
            }));
        }
    }

    private void Emit(IReadOnlyList<AnimationLiveUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (update.ScenePatch is not null)
            {
                _emit(new SmartFlowEvent(
                    "animation.scene.patch",
                    null,
                    Animation: new AnimationStreamPayload(ScenePatch: update.ScenePatch)));
            }

            if (update.Event is not null)
            {
                _emit(new SmartFlowEvent(
                    "animation.event",
                    null,
                    Animation: new AnimationStreamPayload(Event: update.Event)));
            }
        }
    }

    private static WorkflowPreviewValidationResult TryValidate(string? sourceText, string workflowName)
    {
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            var validation = WorkflowPreviewValidator.ParseAndValidate(sourceText);
            if (validation.IsValid)
                return validation;
        }

        var safeWorkflowName = string.IsNullOrWhiteSpace(workflowName) ? "main" : workflowName;
        var fallback = new WorkflowPreviewDocument
        {
            Version = 1,
            Name = "Live workflow",
            Entrypoint = safeWorkflowName
        };
        fallback.Workflows[safeWorkflowName] = new WorkflowPreviewDefinition
        {
            Steps =
            [
                new WorkflowPreviewStep
                {
                    Id = "runtime-work",
                    Type = "llm.call"
                }
            ]
        };
        return WorkflowPreviewValidator.Validate(fallback);
    }

    private static int StableSeed(string correlationId)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in correlationId)
                hash = hash * 31 + character;
            return hash == int.MinValue ? int.MaxValue : Math.Abs(hash);
        }
    }
}
