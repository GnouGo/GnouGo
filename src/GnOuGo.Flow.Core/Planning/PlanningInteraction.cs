namespace GnOuGo.Flow.Core.Planning;

public static class PlanningMode
{
    public const string Auto = "auto", Interactive = "interactive";
    public static void Validate(string mode)
    {
        if (mode is not (Auto or Interactive)) throw new ArgumentException("Unknown planning mode.");
    }
}

/// <summary>The workflow owner collects planner commands separately from runtime human.input.</summary>
public interface IPlanningInteraction
{
    Task<PlanningCommand> RequestAsync(PlanningSession session, CancellationToken ct);
    Task CheckpointedAsync(PlanningSession session, CancellationToken ct);
}
