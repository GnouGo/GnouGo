using System.Diagnostics;

namespace GnOuGo.Agent.Server.Tests;

public sealed class TelemetryOwnershipTests
{
    [Fact]
    public void DisposedHarnessUnsubscribesBeforeLaterPlanningFillsItsQueue()
    {
        var retired = SmartFlowTestFactory.CreateTelemetryHarness();
        using var source = new ActivitySource("GnOuGo.Agent.Planning");
        using (source.StartActivity("original")) { }
        Assert.True(retired.Queue.Channel.Reader.TryRead(out _));
        retired.Dispose();
        using var current = SmartFlowTestFactory.CreateTelemetryHarness();
        // More than the original queue capacity. Drain the live owner normally;
        // an abandoned listener would fill its private queue and block Activity.Stop.
        for (var i = 0; i < 1100; i++)
        {
            using (source.StartActivity("later")) { }
            Assert.True(current.Queue.Channel.Reader.TryRead(out _));
            Assert.False(current.Queue.Channel.Reader.TryRead(out _));
            Assert.False(retired.Queue.Channel.Reader.TryRead(out _));
        }
        Assert.True(retired.Queue.Channel.Reader.Completion.IsCompletedSuccessfully);
    }
}
