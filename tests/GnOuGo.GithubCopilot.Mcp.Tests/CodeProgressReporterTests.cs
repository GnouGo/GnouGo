using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

[CollectionDefinition("Console progress", DisableParallelization = true)]
public sealed class ConsoleProgressCollection;

[Collection("Console progress")]
public sealed class CodeProgressReporterTests
{
    [Fact]
    public async Task BackgroundProgress_KeepsCapturedSendCorrelation()
    {
        var accessor = new CodeMcpTraceContextAccessor();
        var reporter = new CodeProgressReporter(accessor);
        CodeProgressReporter captured;
        using (accessor.Push(CodeMcpTraceContext.FromMcpMeta(JsonNode.Parse("""{"gnougo":{"correlationId":"send-a","stepId":"step-a","mcpMethod":"copilot_session_send"}}""")!.AsObject())))
            captured = reporter.Capture();
        using var different = accessor.Push(CodeMcpTraceContext.FromMcpMeta(JsonNode.Parse("""{"gnougo":{"correlationId":"send-b","stepId":"step-b"}}""")!.AsObject()));
        using var writer = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(writer);
            var ct = TestContext.Current.CancellationToken;
            Task pending;
            using (ExecutionContext.SuppressFlow())
                pending = Task.Run(() => captured.Report("tool.execution_complete", "info", "Tool completed."), ct);
            await pending;
        }
        finally { Console.SetError(previous); }
        var envelope = JsonSerializer.Deserialize(writer.ToString(), CodeMcpJsonContext.Default.CodeMcpProgressEnvelope)!;
        Assert.Equal("send-a", envelope.CorrelationId);
        Assert.Equal("step-a", envelope.StepId);
        Assert.Equal("copilot_session_send", envelope.Method);
    }
}
