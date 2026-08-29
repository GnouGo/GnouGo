using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveIntentBudgetLedgerTests
{
    [Fact]
    public async Task Ledger_PersistsOnlyRedactedBudgetAndPhaseState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path);
            Assert.False(ledger.Exists);

            var snapshot = new LLMUsageBudgetSnapshot
            {
                StartedAtUtc = DateTimeOffset.UtcNow,
                Calls = 7,
                InputTokens = 101,
                OutputTokens = 23,
                TotalTokens = 124,
                EstimatedCostUsd = 1.25m
            };
            await ledger.PersistAsync(snapshot, TestContext.Current.CancellationToken);
            ledger.MarkProbeCompleted(snapshot);
            ledger.MarkDiagnosticGenerationCompleted(snapshot);

            var reloaded = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path);
            Assert.True(reloaded.Exists);
            Assert.True(reloaded.ProbeCompleted);
            Assert.True(reloaded.DiagnosticGenerationCompleted);
            Assert.Equal(snapshot, reloaded.Snapshot);

            var raw = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("provider", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model", raw, StringComparison.OrdinalIgnoreCase);

            reloaded.Delete();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Ledger_MalformedContentFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{malformed", TestContext.Current.CancellationToken);

            var failure = Assert.Throws<InvalidOperationException>(() =>
                LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path));

            Assert.Contains("unreadable or malformed", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
