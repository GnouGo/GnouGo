using System.Text.Json;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotExecutionObservationsTests
{
    [Theory]
    [InlineData("completed", true)]
    [InlineData("missing", false)]
    [InlineData("conflicting", false)]
    [InlineData("reused", false)]
    [InlineData("denied", true)]
    [InlineData("execution_failed", false)]
    public void AsynchronousShellReceiptsRequireUnambiguousExitsOrNativePreExecutionRefusal(string scenario, bool expected)
    {
        var observations = new CopilotExecutionObservations("session");
        observations.Observe(StartShell("command", "shell"));
        observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "command", Success = scenario is not ("denied" or "execution_failed"),
            Error = scenario is "denied" or "execution_failed" ? new() { Code = scenario, Message = "Refused or failed" } : null } });
        if (scenario is "completed" or "conflicting" or "reused")
        {
            if (scenario == "reused")
            {
                observations.Observe(StartShell("other", "shell"));
                observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "other", Success = true } });
            }
            ObserveRead(observations, "read", 2);
            if (scenario == "conflicting") ObserveRead(observations, "read-again", 0);
        }
        observations.Observe(new SessionIdleEvent { Data = new() { Mode = SessionMode.Interactive } });
        Assert.Equal(expected, observations.VerifiedTerminalCompletion);
        var all = observations.Snapshot(); var command = all.Single(c => c.ToolCallId == "command");
        if (scenario == "completed")
        {
            Assert.Equal(2, Assert.Single(command.GetVerifiedTerminals(all)!).ExitCode); // failure is terminal, not success
            Assert.Null(command.GetVerifiedTerminals(all.Select(c => c.ToolCallId == "read" ? c with { CopilotSessionId = "other-session" } : c).ToArray()));
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(new CopilotSendResult("h", "session", "", null, []) { ToolExecutions = all }, CopilotCoreJsonContext.Default.CopilotSendResult), CopilotCoreJsonContext.Default.CopilotSendResult)!;
            Assert.Equal(2, Assert.Single(restored.ToolExecutions[0].GetVerifiedTerminals(restored.ToolExecutions)!).ExitCode);
        }
    }

    [Fact]
    public void HistoryRefreshRecoversMissingDeliveryWithoutImportingOldInvocations()
    {
        var observations = new CopilotExecutionObservations("session");
        var prior = StartShell("old", "old-shell");
        var start = StartShell("new", "shell");
        var complete = new ToolExecutionCompleteEvent { Id = Guid.NewGuid(), Data = new() { ToolCallId = "new", Success = true, ShellExecution = new() { ExitCode = 0 } } };
        var idle = new SessionIdleEvent { Id = Guid.NewGuid(), Data = new() { Mode = SessionMode.Interactive } };
        observations.Observe(start); observations.Observe(idle); Assert.False(observations.VerifiedTerminalCompletion);
        observations.MergeHistory([prior, start, complete, idle]);
        Assert.True(observations.VerifiedTerminalCompletion); Assert.Single(observations.Snapshot());
        observations.MergeHistory([prior, start, complete, idle]); Assert.Single(observations.Snapshot()); Assert.True(observations.VerifiedTerminalCompletion);
        observations.Observe(new ToolExecutionCompleteEvent { Id = complete.Id, Data = new() { ToolCallId = "new", Success = false } });
        Assert.False(observations.VerifiedTerminalCompletion);
    }

    private static ToolExecutionStartEvent StartShell(string id, string shell) => new() { Id = Guid.NewGuid(), Data = new()
    { ToolCallId = id, ToolName = "bash", Arguments = JsonSerializer.SerializeToElement(new { command = "fixture check", shellId = shell }),
      ShellToolInfo = new() { DisplayCommand = "fixture check", PossiblePaths = [], HasWriteFileRedirection = false } } };

    private static void ObserveRead(CopilotExecutionObservations observations, string id, long exit)
    {
        observations.Observe(new ToolExecutionStartEvent { Id = Guid.NewGuid(), Data = new() { ToolCallId = id, ToolName = "read_bash" } });
        observations.Observe(new ToolExecutionCompleteEvent { Id = Guid.NewGuid(), Data = new() { ToolCallId = id, Success = true,
            Result = new() { Content = "Observed exit", Contents = [new ToolExecutionCompleteContentShellExit { ShellId = "shell", ExitCode = exit, Cwd = "/fixture" }] } } });
    }

    [Fact]
    public void ShellExitObservations_PreserveFailureThenEditThenSuccessAndPreviewMetadata()
    {
        var observations = new CopilotExecutionObservations();
        foreach (var (id, code) in new[] { ("first", 1), ("rerun", 0) })
        {
            observations.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = id, ToolName = "command",
                Arguments = JsonSerializer.SerializeToElement(new { command = "python3 -m unittest -v" }) } });
            observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = id, Success = true,
                Result = new() { Content = "Assistant-facing summary", Contents = [new ToolExecutionCompleteContentShellExit
                { Cwd = "/fixture", ExitCode = code, OutputPreview = code == 0 ? "OK" : "FAIL", OutputTruncated = true, OutputFilePath = "/output", ShellId = id }] } } });
            if (code == 1)
            {
                observations.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "edit", ToolName = "project_write" } });
                observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "edit", Success = true } });
            }
        }
        var result = new CopilotSendResult("h", "s", "success", null, []) { ToolExecutions = observations.Snapshot() };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, CopilotCoreJsonContext.Default.CopilotSendResult), CopilotCoreJsonContext.Default.CopilotSendResult)!;
        Assert.Equal(["first", "edit", "rerun"], restored.ToolExecutions.Select(o => o.ToolCallId));
        Assert.Empty(restored.ToolExecutions[1].Terminals);
        var first = Assert.Single(restored.ToolExecutions[0].Terminals);
        Assert.Equal(1, first.ExitCode);
        Assert.Equal("/fixture", first.WorkingDirectory);
        Assert.Equal("FAIL", first.Text);
        Assert.True(first.OutputTruncated);
        Assert.Equal("/output", first.OutputFilePath);
        Assert.Equal("first", first.ShellId);
        Assert.Equal(0, Assert.Single(restored.ToolExecutions[2].Terminals).ExitCode);
    }

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

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task InterruptedSdkTurnsPreserveObservationsButTransportOrCancellationNeverProveStop(bool transport, bool cancelled, bool admission)
    {
        var observations = new CopilotExecutionObservations();
        observations.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "call", ToolName = "project_write" } });
        observations.Observe(Complete("call", 0));
        observations.Observe(new SessionErrorEvent { Data = new() { ErrorType = "fixture", Message = "secret raw provider content" } });
        var bounds = new CopilotExecutionBounds(0, 200000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(), (_, _, _) => Task.CompletedTask);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/responses");
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(request, TestContext.Current.CancellationToken));
        if (transport) bounds.RecordTransportFailure();
        using var cancel = new CancellationTokenSource(); if (cancelled) cancel.Cancel();
        var interrupted = observations.Interrupted("handle", "session", new InvalidOperationException("secret exception"), bounds, cancel.Token);
        Assert.Equal(admission, interrupted.BudgetAdmissionObserved);
        Assert.Single(interrupted.Snapshot.ToolExecutions); Assert.False(interrupted.Snapshot.Completed);
        Assert.DoesNotContain("secret", interrupted.Message); Assert.Empty(interrupted.Snapshot.Content);
        Assert.Null(interrupted.InnerException);
    }

    private static ToolExecutionCompleteEvent Complete(string id, long? exitCode) => new() { Data = new() { ToolCallId = id, Success = true,
        Result = new() { Content = "Misleading summary: success", Contents = [new ToolExecutionCompleteContentTerminal { Cwd = "work", ExitCode = exitCode, Text = "Actual terminal output" }] } } };
}
