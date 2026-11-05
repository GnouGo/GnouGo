using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GnOuGo.Agent.Server.Planning;

/// <summary>Hands a one-use operator link to the embedded window without logging it.</summary>
internal sealed class DesktopOperatorLauncher(Action<Action> invoke, Action<string> load, string desktopToken, bool external) : ILocalOperatorBrowserLauncher
{
    private readonly object _gate = new();
    private bool _ready;
    private Uri? _pending;
    public void Open(Uri address)
    {
        var target = new UriBuilder(address) { Query = "desktopToken=" + desktopToken }.Uri;
        if (external) { OpenExternal(target); return; }
        lock (_gate)
        {
            _pending = target;
            if (_ready) Navigate();
        }
    }
    public void WindowCreated()
    {
        lock (_gate) { _ready = true; if (_pending is not null) Navigate(); }
    }
    private void Navigate()
    {
        var address = _pending!;
        _pending = null;
        invoke(() => load(address.AbsoluteUri));
    }
    internal static void OpenExternal(Uri address)
    {
        if (OperatingSystem.IsWindows()) { using var shell = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); return; }
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(address.AbsoluteUri);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The operator browser did not start.");
        _ = DrainAsync(process);
    }
    private static async Task DrainAsync(Process process)
    {
        using (process) { await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), process.StandardError.BaseStream.CopyToAsync(Stream.Null)); await process.WaitForExitAsync(); }
    }
}
