namespace GnOuGo.Agent.Server.Tests;

internal static class TestPolling
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = TimeProvider.System.GetUtcNow() + DefaultTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(PollInterval, cancellationToken);
        }

        return condition();
    }
}
