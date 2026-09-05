using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GnOuGo.Agent.Server.SmartFlow;

internal static class ActivityAsyncEnumerableExtensions
{
    /// <summary>
    /// Restores the supplied activity for every iterator move. Async iterator callers invoke
    /// each move under their own execution context, so a single ambient assignment before the
    /// first yield is not sufficient to preserve telemetry parentage.
    /// </summary>
    public static async IAsyncEnumerable<T> WithActivity<T>(
        this IAsyncEnumerable<T> source,
        Activity activity,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(activity);

        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var previousActivity = Activity.Current;
                bool hasNext;
                try
                {
                    Activity.Current = activity;
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                finally
                {
                    Activity.Current = previousActivity;
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
        finally
        {
            var previousActivity = Activity.Current;
            try
            {
                Activity.Current = activity;
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Activity.Current = previousActivity;
            }
        }
    }
}
