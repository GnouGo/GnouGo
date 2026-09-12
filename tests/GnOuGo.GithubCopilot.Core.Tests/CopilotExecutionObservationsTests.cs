using System.Text.Json;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotExecutionObservationsTests
{
    [Theory]
    [InlineData("renamed-tool", 0)]
    [InlineData("outil-renomme", 7)]
    public void CapturesCommandEvidenceSeparatelyFromInvocationAndAssistantClaims(string tool, long exitCode)
    {
        var observations = new CopilotExecutionObservations();
        observations.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "one", ToolName = tool, Arguments = JsonSerializer.SerializeToElement(new { command = "example check" }) } });
        observations.Observe(Complete("one", exitCode));
        var observed = Assert.Single(observations.Snapshot());
        Assert.Equal(tool, observed.ToolName); Assert.Contains("example check", observed.ArgumentsJson);
        Assert.True(observed.CompletionObserved); Assert.True(observed.ToolSucceeded); Assert.False(observed.ConflictingCompletion);
        Assert.Equal(exitCode, Assert.Single(observed.Terminals).ExitCode);
        var result = new CopilotSendResult("handle", "session", "Everything succeeded", null, []) { ToolExecutions = observations.Snapshot() };
        var json = JsonSerializer.Serialize(result, CopilotCoreJsonContext.Default.CopilotSendResult);
        var restored = JsonSerializer.Deserialize(json, CopilotCoreJsonContext.Default.CopilotSendResult)!;
        Assert.Equal(exitCode, Assert.Single(Assert.Single(restored.ToolExecutions).Terminals).ExitCode);
    }

    [Fact]
    public void MissingOrContradictoryEventsDoNotBecomeSuccessfulCompletion()
    {
        var observations = new CopilotExecutionObservations();
        observations.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "pending", ToolName = "any" } });
        var pending = Assert.Single(observations.Snapshot());
        Assert.False(pending.CompletionObserved); Assert.Null(pending.ToolSucceeded); Assert.Empty(pending.Terminals);
        observations.Observe(Complete("unpaired", null));
        var unpaired = observations.Snapshot().Single(o => o.ToolCallId == "unpaired");
        Assert.Null(unpaired.ToolName); Assert.Null(unpaired.ArgumentsJson); Assert.Null(Assert.Single(unpaired.Terminals).ExitCode);
        observations.Observe(Complete("pending", 1)); observations.Observe(Complete("pending", 1));
        Assert.False(observations.Snapshot().Single(o => o.ToolCallId == "pending").ConflictingCompletion);
        observations.Observe(Complete("pending", 0));
        var conflict = observations.Snapshot().Single(o => o.ToolCallId == "pending");
        Assert.True(conflict.ConflictingCompletion); Assert.Null(conflict.ToolSucceeded);
        Assert.Equal(1, Assert.Single(conflict.Terminals).ExitCode);
        Assert.False(pending.CompletionObserved); // Previously returned snapshots are immutable.
    }

    [Fact]
    public void OldResultsAndToolFailuresKeepAbsenceExplicit()
    {
        var old = JsonSerializer.Deserialize("""{"handle":"h","copilotSessionId":"s","content":"All checks passed","model":null,"progressEvents":[],"completed":true}""", CopilotCoreJsonContext.Default.CopilotSendResult)!;
        Assert.Empty(old.ToolExecutions);
        var observations = new CopilotExecutionObservations();
        observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "failed", Success = false, Error = new() { Code = "execution_failed", Message = "Failure" } } });
        var result = Assert.Single(observations.Snapshot());
        Assert.False(result.ToolSucceeded); Assert.Empty(result.Terminals); Assert.Equal("execution_failed", result.ErrorCode);
    }

    private static ToolExecutionCompleteEvent Complete(string id, long? exitCode) => new() { Data = new() { ToolCallId = id, Success = true,
        Result = new() { Content = "Misleading summary: success", Contents = [new ToolExecutionCompleteContentTerminal { Cwd = "work", ExitCode = exitCode, Text = "Actual terminal output" }] } } };
}
