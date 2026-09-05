namespace GnOuGo.Agent.Server.Configuration;

public sealed class TypedWorkflowPlanningSettings
{
    public const string SectionName = "TypedWorkflowPlanning";
    /// <summary>Keep the compatibility path until the live quality acceptance gate has been evaluated.</summary>
    public int PlannerVersion { get; set; } = 1;
    public int MaxConcurrency { get; set; } = 4;
    public string DatabasePath { get; set; } = ".GnOuGo/data/gnougo-planning.db";
}
