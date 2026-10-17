namespace GnOuGo.Agent.Server.Configuration;

/// <summary>Host limits for new executions; recovery uses the journal's limits.</summary>
public sealed class WorkflowExecutionSettings
{
    public const string SectionName = "Flow:Execution";
    public int? MaxMappingInputTokens { get; init; }

    internal static WorkflowExecutionSettings Read(IConfiguration configuration)
    {
        const string key = SectionName + ":MaxMappingInputTokens";
        var section = configuration.GetSection(key);
        if (!section.Exists()) return new();
        try
        {
            if (section.GetChildren().Any()) throw new ArgumentException("The mapping allowance must be a scalar.");
            var value = configuration.GetValue<int?>(key);
            if (value is not > 0) throw new ArgumentException("The mapping allowance must be positive.");
            return new() { MaxMappingInputTokens = value };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{key} must be a positive integer.", error);
        }
    }
}
