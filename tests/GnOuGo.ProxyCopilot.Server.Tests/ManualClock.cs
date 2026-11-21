namespace GnOuGo.ProxyCopilot.Server.Tests;

// Small virtual scheduler for exercising the real HTTP relay without minute-long
// tests. Callbacks run outside the scheduler lock, like System.Threading.Timer.
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private long _ticks;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(GetTimestamp());
    public bool HasTimer(TimeSpan remaining) { lock (_gate) return _timers.Any(t => t.Due == _ticks + remaining.Ticks); }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    public void Advance(TimeSpan time)
    {
        var target = GetTimestamp() + time.Ticks;
        while (true)
        {
            Timer[] due;
            lock (_gate)
            {
                var next = _timers.Select(t => t.Due).DefaultIfEmpty(long.MaxValue).Min();
                if (next > target) { _ticks = target; return; }
                _ticks = next;
                due = _timers.Where(t => t.Due == next).ToArray();
                foreach (var timer in due) timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
    }
    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; set; } = long.MaxValue;
        public long Period { get; private set; }
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (_disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                Period = period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (clock._gate) { _disposed = true; clock._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
