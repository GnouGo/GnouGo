using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Hosts open an owned, durable session for workflow.plan.</summary>
public interface IPlanningRuntimeFactory
{
    Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSnapshot initial, CancellationToken ct);
}

public interface IPlanningRuntimeSession : IAsyncDisposable
{
    PlanningSnapshot Snapshot { get; }
    IPlanningRuntime Runtime { get; }
}
