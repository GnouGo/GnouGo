namespace GnOuGo.Agent.Server.Configuration;

public sealed class TypedWorkflowPlanningSettings
{
    public const string SectionName = "TypedWorkflowPlanning";
    /// <summary>Use the typed workflow designer by default; set to 1 for explicit compatibility rollback.</summary>
    public int PlannerVersion { get; set; } = 2;
    public int MaxConcurrency { get; set; } = 4;
    public string Reasoning { get; set; } = "low";
    public int MaxNodesPerUnit { get; set; } = 4;
    public int MaxInputTokensPerUnit { get; set; } = 12_000;
    public int MaxOutputTokens { get; set; } = 8_192;
    public string DatabasePath { get; set; } = ".GnOuGo/data/gnougo-planning.db";
}
