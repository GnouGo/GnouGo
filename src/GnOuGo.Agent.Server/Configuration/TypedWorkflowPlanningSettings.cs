namespace GnOuGo.Agent.Server.Configuration;

public sealed class TypedWorkflowPlanningSettings
{
    public const string SectionName = "TypedWorkflowPlanning";
    /// <summary>Disable only automatic advancement for an explicitly driven recovery host.</summary>
    public bool BackgroundProcessingEnabled { get; set; } = true;
    public int MaxConcurrency { get; set; } = 4;
    public int MaxRepairs { get; set; } = 3;
    public long MaxTotalTokens { get; set; } = 15_000_000;
    public long MaxActiveMilliseconds { get; set; } = 18_000_000;
    public int MaxModelCalls { get; set; } = 100;
    public string Reasoning { get; set; } = "low";
    public int MaxInputTokensPerRequest { get; set; } = 12_000;
    public int MaxOutputTokens { get; set; } = 8_192;
    public string DatabasePath { get; set; } = ".GnOuGo/data/gnougo-planning-v3.db";
}
