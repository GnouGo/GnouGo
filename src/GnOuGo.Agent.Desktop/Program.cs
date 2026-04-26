using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Photino.NET;
using GnOuGo.Agent.Server.Hosting;

internal static class Program
{
    private const string RequestedServerUrl = "http://127.0.0.1:58443";
    private static readonly string DiagnosticsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GnOuGo.Agent",
        "Logs");

    private static readonly string DiagnosticsFile = Path.Combine(DiagnosticsDir, "desktop.log");
    private static readonly object DiagnosticsSync = new();

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DiagnosticsDir);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"UnhandledException: {e.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        Log("Desktop startup begin");
        Log($"BaseDirectory={AppContext.BaseDirectory}");
        Log($"Args={string.Join(' ', args)}");

#if DEBUG
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", Environments.Development);
            Log("Debug mode: defaulting ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT to Development");
        }
#endif

        var desktopToken = Guid.NewGuid().ToString("N");
        var desktopWwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var useExternalBrowserShell = string.Equals(
            Environment.GetEnvironmentVariable("GNOUGO_FORCE_EXTERNAL_BROWSER"),
            "1",
            StringComparison.Ordinal);
        var webViewDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GnOuGo.Agent",
            "Photino");

        Directory.CreateDirectory(webViewDataPath);

        Log($"RequestedServerUrl={RequestedServerUrl}");
        Log($"DesktopToken={desktopToken}");
        Log($"UseExternalBrowserShell={useExternalBrowserShell}");
        Log($"DesktopWwwRoot={desktopWwwRoot} Exists={Directory.Exists(desktopWwwRoot)}");
        Log($"WebViewDataPath={webViewDataPath}");

        var url = RequestedServerUrl.TrimEnd('/');
        var appUrl = $"{url}/?desktopToken={desktopToken}";
        var healthUrl = $"{url}/health";
        var bootLogUrl = $"{url}/desktop/boot-log/{desktopToken}";
        Log($"RequestedServerBaseUrl={url}");
        Log($"AppUrl={appUrl}");
        Log($"HealthUrl={healthUrl}");
        Log($"BootLogUrl={bootLogUrl}");

        var window = new PhotinoWindow();
        Log("PhotinoWindow created");

        var iconFile = ResolveIconFile();
        if (!string.IsNullOrWhiteSpace(iconFile) && File.Exists(iconFile))
        {
            try { window.SetIconFile(iconFile); } catch { /* ignore */ }
        }

        window
            .SetTitle("GnOuGo.Agent")
            .SetWidth(1280)
            .SetHeight(800)
            .Center()
            .SetTemporaryFilesPath(webViewDataPath);

        if (OperatingSystem.IsWindows())
        {
#if DEBUG
            window.SetBrowserControlInitParameters("--disable-gpu --auto-open-devtools-for-tabs");
            Log("Windows WebView2 args=--disable-gpu --auto-open-devtools-for-tabs");
#else
            window.SetBrowserControlInitParameters("--disable-gpu");
            Log("Windows WebView2 args=--disable-gpu");
#endif
        }

#if DEBUG
        window
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(true);
        Log("Debug mode: DevTools enabled");
#endif

        if (useExternalBrowserShell)
        {
            window.StartString = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>GnOuGo.Agent</title>
  <style>
    html, body { height: 100%; margin: 0; background: #ffffff; color: #24292f; font-family: Segoe UI, system-ui, sans-serif; }
    body { display: grid; place-items: center; }
    .box { max-width: 640px; padding: 24px; text-align: center; }
    h1 { font-size: 22px; margin: 0 0 12px; }
    p { margin: 8px 0; color: #57606a; }
    code { background: #f6f8fa; padding: 2px 6px; border-radius: 6px; }
    a { color: #0969da; text-decoration: none; }
  </style>
</head>
<body>
  <div class="box">
    <h1>GnOuGo.Agent is running in your browser</h1>
    <p>The embedded Windows web view is unstable on this machine, so the app opens in your default browser instead.</p>
    <p>Keep this window open while using the app.</p>
    <p><a href="__APP_URL__">Open the app again</a></p>
    <p><code>__APP_URL__</code></p>
  </div>
</body>
</html>
""".Replace("__APP_URL__", appUrl);
            Log("Window.StartString assigned (browser shell mode)");
        }
        else
        {
            window.StartString = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>GnOuGo.Agent</title>
  <style>
    html, body { height: 100%; margin: 0; background: #ffffff; color: #24292f; font-family: Segoe UI, system-ui, sans-serif; }
    body { display: grid; place-items: center; padding: 24px; box-sizing: border-box; }
    .boot { position: fixed; inset: 0; display: grid; place-items: center; padding: 24px; background: #ffffff; z-index: 1; }
    .boot.hidden { display: none; }
    .boot-card { max-width: 560px; text-align: center; }
    .boot-title { font-size: 24px; margin: 0 0 12px; }
    .boot-status { margin: 0; color: #57606a; }
    .fallback { position: fixed; inset: auto 16px 16px 16px; font-size: 12px; color: #57606a; background: rgba(255,255,255,.92); padding: 8px 10px; border-radius: 8px; box-shadow: 0 1px 3px rgba(0,0,0,.12); display: none; z-index: 2; }
    .fallback.visible { display: block; }
    a { color: #0969da; text-decoration: none; }
  </style>
</head>
<body>
  <div id="boot" class="boot">
    <div class="boot-card">
      <h1 class="boot-title">Starting GnOuGo.Agent…</h1>
      <p id="bootStatus" class="boot-status">Waiting for the local server to become ready.</p>
    </div>
  </div>

  <div id="fallback" class="fallback">If startup takes too long, <a href="__APP_URL__">open the app in your browser</a>.</div>

  <script>
    (function () {
      const healthUrl = __HEALTH_URL__;
      const appUrl = __APP_URL_JS__;
      const bootLogUrl = __BOOT_LOG_URL__;
      const pollDelayMs = 250;
      const maxAttempts = 80;
      const requiredConsecutiveHealthChecks = 2;
      const boot = document.getElementById('boot');
      const bootStatus = document.getElementById('bootStatus');
      const fallback = document.getElementById('fallback');
      let navigating = false;

      const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

      const logBoot = (step, detail) => {
        try {
          const img = new Image();
          const query = `?step=${encodeURIComponent(step)}&detail=${encodeURIComponent(detail || '')}&_=${Date.now()}`;
          img.src = bootLogUrl + query;
        } catch {
          // ignore
        }
      };

      const setStatus = (message) => {
        if (bootStatus) {
          bootStatus.textContent = message;
        }
      };

      async function probeHealth() {
        const url = `${healthUrl}${healthUrl.indexOf('?') >= 0 ? '&' : '?'}_=${Date.now()}`;

        try {
          const response = await fetch(url, {
            cache: 'no-store',
            mode: 'no-cors',
          });

          return response.type === 'opaque' || response.ok;
        } catch {
          return false;
        }
      }

      window.addEventListener('beforeunload', () => {
        if (navigating) {
          logBoot('top-level-beforeunload', appUrl);
        }
      });

      async function start() {
        let consecutiveOk = 0;

        logBoot('boot-script-start', navigator.userAgent || 'unknown-user-agent');
        setStatus('Checking the local server…');

        for (let attempt = 1; attempt <= maxAttempts; attempt++) {
          const healthOk = await probeHealth();
          if (healthOk) {
            consecutiveOk++;

            if (consecutiveOk === 1) {
              logBoot('health-ok', `attempt=${attempt}`);
            }

            setStatus('Local server is ready. Loading the embedded app…');
            if (consecutiveOk >= requiredConsecutiveHealthChecks) {
              navigating = true;
              logBoot('top-level-navigate', appUrl);
              window.location.replace(appUrl);
              return;
            }
          } else {
            consecutiveOk = 0;
            if (attempt === 1 || attempt % 10 === 0) {
              logBoot('health-wait', `attempt=${attempt}`);
            }
            setStatus(`Waiting for the local server… (${attempt}/${maxAttempts})`);
          }

          await sleep(pollDelayMs);
        }

        setStatus('The local server is taking longer than expected.');
        logBoot('health-timeout', `attempts=${maxAttempts}`);
        if (fallback) {
          fallback.classList.add('visible');
        }
      }

      start();
    })();
  </script>
</body>
</html>
"""
                .Replace("__APP_URL__", appUrl)
                .Replace("__APP_URL_JS__", ToJavaScriptStringLiteral(appUrl))
                .Replace("__HEALTH_URL__", ToJavaScriptStringLiteral(healthUrl))
                .Replace("__BOOT_LOG_URL__", ToJavaScriptStringLiteral(bootLogUrl));
            Log("Window.StartString assigned (embedded mode)");
        }

        window.RegisterWindowCreatedHandler((_, _) => Log("WindowCreated"));
        window.RegisterFocusInHandler((_, _) => Log("WindowFocusIn"));
        window.RegisterFocusOutHandler((_, _) => Log("WindowFocusOut"));
        window.RegisterSizeChangedHandler((_, size) => Log($"WindowSizeChanged={size.Width}x{size.Height}"));
        window.RegisterLocationChangedHandler((_, pos) => Log($"WindowLocationChanged={pos.X},{pos.Y}"));

        using var cts = new CancellationTokenSource();
        var app = GnOuGoAgentWebHost.Build(args, urls: RequestedServerUrl, contentRoot: AppContext.BaseDirectory, enableHttpsRedirection: false);
        Log("Web host built");

        Task? serverTask = null;
        var startupThread = new Thread(() =>
        {
            try
            {
                app.StartAsync(cts.Token).GetAwaiter().GetResult();
                Log("Web host StartAsync completed");

                var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);
                Log($"PublishedServerUrl={publishedEndpoints.AppBaseAddress ?? "<unresolved>"}");
                Log($"TelemetryGrpcUrl={publishedEndpoints.TelemetryGrpcBaseAddress ?? "<disabled>"}");
                Log($"TelemetryHttpUrl={publishedEndpoints.TelemetryHttpBaseAddress ?? "<disabled>"}");

                if (!ProbeServer(url))
                {
                    throw new InvalidOperationException($"The local server did not become ready at {url}.");
                }

                serverTask = app.WaitForShutdownAsync();
                Log("Web host WaitForShutdownAsync registered");
            }
            catch (Exception ex)
            {
                Log($"Desktop server startup failed: {ex}");
            }
        })
        {
            IsBackground = true,
            Name = "GnOuGo.Agent.DesktopServerStartup"
        };

        var fallbackThread = new Thread(() =>
        {
            try
            {
                Log("Fallback watcher started");

                if (useExternalBrowserShell)
                {
                    Thread.Sleep(250);
                    Log("Opening browser shell target");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = appUrl,
                        UseShellExecute = true
                    });
                    return;
                }

                var pageLoadedLogged = false;
                var pageLoadedObserved = false;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    Thread.Sleep(250);
                    if (!pageLoadedLogged && DesktopWebViewTracker.IsPageLoaded(desktopToken))
                    {
                        pageLoadedLogged = true;
                        pageLoadedObserved = true;
                        Log($"Embedded webview reported page-loaded after {attempt + 1} checks");
                    }

                    if (DesktopWebViewTracker.IsClientReady(desktopToken))
                    {
                        Log($"Embedded webview reported client-ready after {attempt + 1} checks");
                        return;
                    }

                    if (attempt == 59 && !pageLoadedObserved)
                    {
                        Log("Embedded webview still has not reported page-loaded after 15 seconds; opening external browser fallback");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = appUrl,
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                Log("Embedded webview did not report client-ready after extended wait; opening external browser fallback");
                Process.Start(new ProcessStartInfo
                {
                    FileName = appUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log($"External browser fallback failed: {ex}");
            }
        })
        {
            IsBackground = true,
            Name = "GnOuGo.Agent.DesktopFallback"
        };
        startupThread.Start();
        fallbackThread.Start();

        window.RegisterWindowClosingHandler((_, _) =>
        {
            Log("WindowClosing");
            try { cts.Cancel(); } catch { /* ignore */ }
            return false;
        });

        Log("Entering WaitForClose");
        window.WaitForClose();
        Log("WaitForClose returned");

        try
        {
            app.StopAsync().GetAwaiter().GetResult();
            Log("Web host stopped");
        }
        catch
        {
            // ignore
        }

        try
        {
            if (serverTask is not null)
            {
                serverTask.GetAwaiter().GetResult();
                Log("Server task completed cleanly");
            }
        }
        catch
        {
            // ignore
        }

        Log("Desktop shutdown end");
    }

    private static bool ProbeServer(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var healthOk = false;

        Log($"Waiting for server readiness at {url}");

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var resp = http.GetAsync($"{url}/health").GetAwaiter().GetResult();
                Log($"Health probe attempt={attempt + 1} status={(int)resp.StatusCode}");
                if (resp.IsSuccessStatusCode)
                {
                    healthOk = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                Log($"Health probe attempt={attempt + 1} failed: {ex.Message}");
            }

            Thread.Sleep(500);
        }

        string Probe(string path)
        {
            try
            {
                var resp = http.GetAsync($"{url}{path}").GetAwaiter().GetResult();
                return $"{path} => {(int)resp.StatusCode} {resp.StatusCode}";
            }
            catch (Exception ex)
            {
                return $"{path} => ERROR {ex.Message}";
            }
        }

        string ProbeBlazorNegotiate()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/_blazor/negotiate?negotiateVersion=1")
                {
                    Content = new StringContent("{}")
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var resp = http.SendAsync(request).GetAwaiter().GetResult();
                return $"/_blazor/negotiate => {(int)resp.StatusCode} {resp.StatusCode}";
            }
            catch (Exception ex)
            {
                return $"/_blazor/negotiate => ERROR {ex.Message}";
            }
        }

        Log($"HealthReady={healthOk}");
        Log(Probe("/"));
        Log(Probe("/health"));
        Log(Probe("/_framework/blazor.web.js"));
        Log(ProbeBlazorNegotiate());
        Log(Probe("/ui/app.js"));
        return healthOk;
    }

    private static string ResolveServerUrl(GnOuGoAgentWebHost.PublishedAgentEndpoints publishedEndpoints)
    {
        if (!string.IsNullOrWhiteSpace(publishedEndpoints.AppBaseAddress))
            return publishedEndpoints.AppBaseAddress;

        throw new InvalidOperationException("The local server did not publish a usable main application address.");
    }

    private static void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (DiagnosticsSync)
        {
            using var stream = new FileStream(DiagnosticsFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
        }
    }

    private static string ToJavaScriptStringLiteral(string value)
    {
        if (value is null)
        {
            return "null";
        }

        return "\""
            + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
                .Replace("\u2029", "\\u2029", StringComparison.Ordinal)
            + "\"";
    }

    private static string? ResolveIconFile()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (OperatingSystem.IsWindows())
            return Path.Combine(assets, "icon.ico");
        if (OperatingSystem.IsLinux())
            return Path.Combine(assets, "icon.png");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(assets, "icon.icns");
        return null;
    }
}
