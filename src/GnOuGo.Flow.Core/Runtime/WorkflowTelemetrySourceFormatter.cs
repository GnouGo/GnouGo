namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Prepares workflow source text for telemetry attributes while limiting payload size
/// and avoiding obvious secret values at rest in trace stores.
/// </summary>
public static class WorkflowTelemetrySourceFormatter
{
    public const int DefaultSourceAttributeLimit = 64 * 1024;

    private static readonly string[] SensitiveKeyFragments =
    [
        "api_key",
        "apikey",
        "authorization",
        "bearer",
        "credential",
        "password",
        "secret",
        "token"
    ];

    public static WorkflowTelemetrySourceSnapshot Format(string source, int limit = DefaultSourceAttributeLimit)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The workflow telemetry source limit must be positive.");

        var redactedSource = RedactSensitiveValues(source);
        var truncated = redactedSource.Length > limit;
        return new WorkflowTelemetrySourceSnapshot(
            truncated ? redactedSource[..limit] : redactedSource,
            source.Length,
            redactedSource.Length,
            truncated,
            !string.Equals(source, redactedSource, StringComparison.Ordinal));
    }

    private static string RedactSensitiveValues(string source)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = RedactSensitiveLine(lines[i]);

        return string.Join("\n", lines);
    }

    private static string RedactSensitiveLine(string line)
    {
        var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex < 0)
            separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
            return line;

        var key = line[..separatorIndex]
            .Trim()
            .Trim('{', ',', ' ', '\t', '"', '\'');

        if (!IsSensitiveKey(key))
            return line;

        return line[..(separatorIndex + 1)] + " <redacted>";
    }

    private static bool IsSensitiveKey(string key)
        => SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

public sealed record WorkflowTelemetrySourceSnapshot(
    string Text,
    int OriginalLength,
    int RedactedLength,
    bool Truncated,
    bool Redacted);

