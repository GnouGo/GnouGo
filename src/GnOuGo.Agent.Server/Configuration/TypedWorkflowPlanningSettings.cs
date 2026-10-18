namespace GnOuGo.Agent.Server.Configuration;
public sealed class TypedWorkflowPlanningSettings
{
    public const string SectionName = "TypedWorkflowPlanning";
    public bool BackgroundProcessingEnabled { get; set; } = true;
    public bool AutomaticCorrectionEnabled { get; set; } = true;
    public int MaxReplanAttempts { get; set; } = 4;
    public int MaxModelCalls { get; set; } = 12;
    public long MaxTotalTokens { get; set; } = 15_000_000;
    public long MaxActiveMilliseconds { get; set; } = 18_000_000;
    public string Reasoning { get; set; } = "medium";
    public int MaxInputTokensPerRequest { get; set; } = 32_000;
    public int MaxOutputTokens { get; set; } = 32_768;
    public string DatabasePath { get; set; } = ".GnOuGo/data/gnougo-planning-v9.db";
}
