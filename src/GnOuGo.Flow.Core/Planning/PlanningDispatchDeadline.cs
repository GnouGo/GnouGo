namespace GnOuGo.Flow.Core.Planning;

internal static class PlanningDispatchDeadline
{
    internal static DateTimeOffset Resolve(PlanningRequest request, double activeMilliseconds = 0)
    {
        if (PlanningTokenThresholds.OptimizationAdvisory(request))
        {
            var transport = PlanningTransportPolicies.Read(request) ?? throw new PlanningConflictException("A finite captured transport policy is required.");
            return DateTimeOffset.UtcNow.AddMilliseconds((long)transport.MaxAttempts * transport.AttemptTimeoutMilliseconds + transport.MaxTotalDelayMilliseconds);
        }
        return DateTimeOffset.UtcNow + (PlanningBudgetOptions.Parse(request.Options)?.MaxElapsed ?? TimeSpan.FromHours(5)) - TimeSpan.FromMilliseconds(activeMilliseconds);
    }
}
