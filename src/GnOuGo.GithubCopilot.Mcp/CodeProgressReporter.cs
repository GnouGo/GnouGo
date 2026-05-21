using System.Text.Json;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CodeProgressReporter
{
    private const string ProgressEnvelopeType = "gnougo.mcp.progress";
    private readonly CodeMcpTraceContextAccessor _traceContextAccessor;

    public CodeProgressReporter(CodeMcpTraceContextAccessor traceContextAccessor)
    {
        _traceContextAccessor = traceContextAccessor;
    }

    public CodeProgressEvent Report(
        string kind,
        string level,
        string message,
        string? file = null,
        string? fallbackServer = null,
        string? fallbackMethod = null,
        string? fallbackMcpKind = null)
    {
        var progressEvent = new CodeProgressEvent(
            Kind: kind,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            File: file);

        WriteProgress(progressEvent, fallbackServer, fallbackMethod, fallbackMcpKind);
        return progressEvent;
    }

    public CodeProgressEvent Report(
        CodeProgressEvent progressEvent,
        string? fallbackServer = null,
        string? fallbackMethod = null,
        string? fallbackMcpKind = null)
    {
        WriteProgress(progressEvent, fallbackServer, fallbackMethod, fallbackMcpKind);
        return progressEvent;
    }

    private void WriteProgress(CodeProgressEvent progressEvent, string? fallbackServer, string? fallbackMethod, string? fallbackMcpKind)
    {
        try
        {
            var context = CodeMcpTraceContext.Capture(_traceContextAccessor);
            var envelope = new CodeMcpProgressEnvelope(
                Type: ProgressEnvelopeType,
                CorrelationId: context?.CorrelationId,
                RunId: context?.RunId,
                StepId: context?.StepId,
                StepType: context?.StepType,
                Server: context?.McpServer ?? fallbackServer,
                Method: context?.McpMethod ?? fallbackMethod,
                Kind: context?.McpKind ?? fallbackMcpKind,
                Event: progressEvent);

            Console.Error.WriteLine(JsonSerializer.Serialize(envelope, CodeMcpJson.SerializerOptions));
        }
        catch
        {
            // Progress reporting is best-effort and must never fail a tool call.
        }
    }
}
