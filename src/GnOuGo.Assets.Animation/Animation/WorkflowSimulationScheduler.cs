namespace GnOuGo.Assets.Animation;

public static class WorkflowSimulationScheduler
{
    private static readonly double[] SupportedSpeeds = [0.5d, 1d, 2d, 4d];

    public static IReadOnlyList<SimulationEvent> Schedule(GnouGnouAnimationPlan plan, double speed = 1d)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!SupportedSpeeds.Contains(speed))
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed must be one of 0.5, 1, 2, or 4.");

        return plan.Events.Select(item => item with
        {
            OffsetMs = (long)Math.Round(item.OffsetMs / speed, MidpointRounding.AwayFromZero),
            DurationMs = (long)Math.Round(item.DurationMs / speed, MidpointRounding.AwayFromZero)
        }).ToArray();
    }
}
