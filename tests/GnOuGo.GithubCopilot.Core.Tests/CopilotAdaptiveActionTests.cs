using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotAdaptiveActionTests
{
    [Fact]
    public async Task ReuseStableReadsRefreshAfterMutationAndRequireRealCommandCompletionBeforeAnotherEffect()
    {
        var stored = new List<JsonObject>();
        var receipts = new List<CopilotToolExecutionObservation>();
        var actions = new CopilotAdaptiveActions((_, row) => { stored.Add(row); return Task.CompletedTask; },
            new HashSet<string> { "test" }) { ExecutionObservations = () => receipts };
        var bounds = new CopilotExecutionBounds(8, 2000, DateTimeOffset.UtcNow.AddMinutes(1),
            new HashSet<string> { "project_read", "bash", "project_write" }, (_, _, _) => Task.CompletedTask) { MaxActions = 9 };
        var file = JsonDocument.Parse("""{"path":"input"}""").RootElement.Clone();
        var command = JsonDocument.Parse("""{"command":"test"}""").RootElement.Clone();
        Task<string?> Admit(string id, string tool, object args) => actions.AdmitAsync(bounds, "session", id, tool, args);
        async Task Read(string id, string value)
        {
            Assert.Null(await Admit(id, "project_read", file));
            Assert.Equal("admitted", stored[^1]["state"]!.ToString());
            await actions.CompleteAsync(bounds, "session", id, "project_read", value, true);
        }
        await Read("1", "first"); await Read("2", "second"); await Read("3", "second");
        Assert.Contains("identical", await Admit("4", "project_read", file));
        Assert.Null(await Admit("5", "bash", command));
        await actions.CompleteAsync(bounds, "session", "5", "bash", "started", true);
        Assert.Contains("terminal receipt", await Admit("6", "project_write", file));
        var test = new CopilotToolExecutionObservation("5", null, "bash", command.GetRawText(), true, true, false, [new("/workspace", 1, "failed test")], null)
            { CopilotSessionId = "session", IsShellCommand = true, StartedSequence = 1, CompletedSequence = 2 };
        receipts.Add(test with { CopilotSessionId = "other" });
        Assert.Contains("terminal receipt", await Admit("7", "project_write", file));
        receipts.Add(test);
        Assert.Contains("workspace change", await Admit("8", "bash", command));
        Assert.Null(await Admit("9", "project_write", file));
        await actions.CompleteAsync(bounds, "session", "9", "project_write", "written", true);
        receipts.Add(new("9", null, "project_write", file.GetRawText(), true, true, false, [], null) { CopilotSessionId = "session", StartedSequence = 3, CompletedSequence = 4 });
        await Read("10", "updated");
        Assert.Null(await Admit("11", "bash", command));
        Assert.Equal(7, bounds.Actions);
        Assert.Equal(7, stored.Count(row => row["state"]!.ToString() == "admitted"));
    }

    [Fact]
    public async Task FailedIntentWriteDeniesAllLaterActions()
    {
        var actions = new CopilotAdaptiveActions((_, _) => throw new IOException("store unavailable"), new HashSet<string>());
        var bounds = new CopilotExecutionBounds(1, 2000, DateTimeOffset.UtcNow.AddMinutes(1),
            new HashSet<string> { "project_read" }, (_, _, _) => Task.CompletedTask);
        Assert.NotNull(await actions.AdmitAsync(bounds, "session", "one", "project_read", "file"));
        Assert.True(actions.Unresolved);
        Assert.Contains("receipt", await actions.AdmitAsync(bounds, "session", "two", "project_read", "file"));
    }
}
