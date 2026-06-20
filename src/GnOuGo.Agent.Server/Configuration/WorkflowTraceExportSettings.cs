namespace GnOuGo.Agent.Server.Configuration;

/// <summary>
/// Controls the per-workflow OpenTelemetry trace file export.
/// </summary>
public sealed class WorkflowTraceExportSettings
{
    public const string SectionName = "WorkflowTraceExport";

    /// <summary>
    /// When enabled, each SmartFlow execution is written to a separate JSON trace file.
    /// </summary>
    public bool Enabled { get; set; }
}
