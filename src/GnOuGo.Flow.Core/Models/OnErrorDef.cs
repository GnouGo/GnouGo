namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Conditional error handler for a step.
/// </summary>
public sealed class OnErrorDef
{
    public List<OnErrorCase> Cases { get; set; } = new();
}

public sealed class OnErrorCase
{
    /// <summary>Optional guard expression (defaults to true).</summary>
    public string? If { get; set; }

    /// <summary>Action: stop or continue in the current runtime.</summary>
    public string Action { get; set; } = "stop";

    /// <summary>Expression for output when action=continue.</summary>
    public string? SetOutput { get; set; }

    /// <summary>Reserved retry override metadata parsed from YAML for future use.</summary>
    public RetryPolicy? Retry { get; set; }
}
