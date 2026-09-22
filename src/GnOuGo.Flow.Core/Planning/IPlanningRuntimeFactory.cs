using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Core.Planning;
public interface IPlanningRuntimeFactory
{
    Task<IPlanningRuntimeSession> OpenAsync(StepExecutionContext context, PlanningSession initial, CancellationToken ct);
    Task<string> ReadApprovedYamlAsync(StepExecutionContext context, string sessionId, string artifactHash, CancellationToken ct);
}
public interface IPlanningRuntimeSession : IAsyncDisposable
{
    PlanningSession Session { get; }
    IPlanningRuntime Runtime { get; }
}
