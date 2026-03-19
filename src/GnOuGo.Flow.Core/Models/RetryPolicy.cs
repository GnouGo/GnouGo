namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Retry policy for a step.
/// </summary>
public sealed class RetryPolicy
{
    public int Max { get; set; } = 1;
    public int BackoffMs { get; set; } = 1000;
    public double BackoffMult { get; set; } = 2.0;
    public int JitterMs { get; set; } = 0;
}

