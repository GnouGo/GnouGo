namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Cleanup is exclusive with live leaf effects. It never cancels an effect to obtain exclusivity.</summary>
internal sealed class WorkflowEffectGate
{
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly object _sync = new();
    private int _active;
    private TaskCompletionSource _drained = Completed();

    public async Task<IDisposable> EnterEffectAsync(CancellationToken ct)
    {
        await _admission.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                if (_active++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally { _admission.Release(); }
        return new Release(() => { lock (_sync) if (--_active == 0) _drained.TrySetResult(); });
    }

    public async Task<IDisposable> EnterFinalizationAsync(CancellationToken ct)
    {
        await _admission.WaitAsync(ct);
        try
        {
            Task drained;
            lock (_sync) drained = _drained.Task;
            await drained.WaitAsync(ct);
            return new Release(() => _admission.Release());
        }
        catch { _admission.Release(); throw; }
    }

    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(); value.SetResult(); return value; }
    private sealed class Release(Action action) : IDisposable
    {
        private Action? _release = action;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
