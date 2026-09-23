namespace GnOuGo.GithubCopilot.Core;

/// <summary>SDK callbacks run on background dispatchers, outside the caller's execution context.</summary>
internal sealed class CopilotHumanInputRouter(ICopilotHumanInputProvider provider) : ICopilotHumanInputProvider
{
    private ActiveCall? _active;
    internal IDisposable Bind(CopilotRequestContext context)
    {
        var call = new ActiveCall(provider.Capture(), context);
        if (Interlocked.CompareExchange(ref _active, call, null) is not null)
            throw new InvalidOperationException("A Copilot human-input call is already active for this session.");
        return new Binding(this, call);
    }
    public Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken)
    {
        var call = Volatile.Read(ref _active)
            ?? throw new InvalidOperationException("Copilot human input requires an active session operation.");
        return call.Provider.RequestAsync(request with { Context = call.Context }, cancellationToken);
    }
    private sealed record ActiveCall(ICopilotHumanInputProvider Provider, CopilotRequestContext Context);
    private sealed class Binding(CopilotHumanInputRouter router, ActiveCall call) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref router._active, null, call);
    }
}
