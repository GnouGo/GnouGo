using GnOuGo.Flow.Core.Models;
using System.Globalization;

namespace GnOuGo.Agent.Server.Configuration;

/// <summary>Host limits for new executions; recovery uses the journal's limits.</summary>
public sealed class WorkflowExecutionSettings
{
    public const string SectionName = "Flow:Execution";
    public int? MaxMappingInputTokens { get; init; }

    public int FinalizationTimeoutSeconds { get; init; } = ExecutionLimits.DefaultFinalizationTimeoutSeconds;

    internal static WorkflowExecutionSettings Read(IConfiguration configuration) => new()
    {
        MaxMappingInputTokens = ReadLimit(configuration, nameof(MaxMappingInputTokens)),
        // CancellationTokenSource accepts a timer interval of at most uint.MaxValue - 1 milliseconds.
        FinalizationTimeoutSeconds = ReadLimit(configuration, nameof(FinalizationTimeoutSeconds), (int)((uint.MaxValue - 1) / 1000))
            ?? ExecutionLimits.DefaultFinalizationTimeoutSeconds
    };

    private static int? ReadLimit(IConfiguration configuration, string name, int maximum = int.MaxValue)
    {
        var section = configuration.GetSection(SectionName + ":" + name);
        if (!section.Exists()) return null;
        if (section.GetChildren().Any() || !int.TryParse(section.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1 || value > maximum)
            throw new InvalidOperationException($"{section.Path} must be an integer between 1 and {maximum}.");
        return value;
    }
}
