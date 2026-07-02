namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Exports the completed OpenTelemetry trace associated with a workflow execution.
/// </summary>
public interface IWorkflowTraceFileExporter
{
    /// <summary>
    /// Registers a SmartFlow trace before its child activities are created.
    /// </summary>
    void BeginCapture(string traceId);

    Task ExportAsync(string traceId, string correlationId, CancellationToken cancellationToken);
}
