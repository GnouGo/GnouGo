using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using GnOuGo.Agent.Server.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Only host code can mint this grant, after local operator authentication.</summary>
public sealed class PlanningDiagnosticGrant
{
    internal PlanningDiagnosticGrant(string subject) => Subject = subject;
    internal string Subject { get; }
}

internal sealed class LocalOperatorRequired { }

public sealed class LocalOperatorAccess(IHttpContextAccessor http, IServiceProvider services)
{
    internal const string Claim = "gnougo.local_operator";
    public async Task<bool> CanInspectAsync()
    {
        var user = http.HttpContext?.User;
        if (http.HttpContext is { } context && !IsLocal(context)) return false;
        // HTTP endpoints already have an authenticated principal. The circuit
        // provider is only available after Blazor has established its scope.
        if (user?.Identity?.IsAuthenticated != true && services.GetService<AuthenticationStateProvider>() is { } authentication)
        {
            try { user = (await authentication.GetAuthenticationStateAsync()).User; }
            catch (InvalidOperationException) { return false; }
        }
        return user?.Identity?.IsAuthenticated == true && user.HasClaim(Claim, "true");
    }
    public async Task<PlanningDiagnosticGrant> GrantAsync()
    {
        if (!await CanInspectAsync()) throw new UnauthorizedAccessException("Authenticate as the local operator before enabling diagnostic planning.");
        return new("local-operator");
    }
    internal async Task<(string Mode, PlanningDiagnosticGrant? Grant)> ResolveAsync(string? requested)
    {
        var settings = services.GetRequiredService<IOptions<TypedWorkflowPlanningSettings>>().Value;
        var mode = settings.ResolveThresholdMode(requested, await CanInspectAsync());
        if (mode == "diagnostic" && !settings.DiagnosticEnabled)
            throw new UnauthorizedAccessException("Diagnostic planning is disabled on this host.");
        return (mode, mode == "diagnostic" ? await GrantAsync() : null);
    }
    internal static bool IsLocal(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            !Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var host) || !host.IsLoopback) return false;
        if (context.Request.Headers.Origin is { Count: > 0 } origin &&
            (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var source) || source.Authority != host.Authority || source.Scheme != host.Scheme)) return false;
        return true;
    }
}

public interface ILocalOperatorBrowserLauncher
{
    void Open(Uri address);
}

internal sealed class LocalOperatorBrowserLauncher(ILogger<LocalOperatorBrowserLauncher> logger) : ILocalOperatorBrowserLauncher
{
    public void Open(Uri address)
    {
        if (OperatingSystem.IsWindows())
        {
            using var shell = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        // Shell launchers can echo a failed URL. Drain their output without logging
        // it, so an unavailable headless browser cannot disclose a bootstrap token.
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(address.AbsoluteUri);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The browser launcher did not start.");
        _ = ObserveAsync(process);
    }
    private async Task ObserveAsync(Process process)
    {
        using (process)
        {
            try
            {
                await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), process.StandardError.BaseStream.CopyToAsync(Stream.Null));
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) logger.LogWarning("The local operator browser launcher exited with code {ExitCode}. Ordinary strict planning remains available.", process.ExitCode);
            }
            catch (Exception ex) { logger.LogWarning("The local operator browser launcher could not be observed ({ErrorType}).", ex.GetType().Name); }
        }
    }
}

/// <summary>One-use bootstrap credentials exist only in memory; the fragment never reaches HTTP logs.</summary>
public sealed class LocalOperatorBootstrap
{
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _expires;
    internal string Issue()
    {
        lock (_gate) { _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); _expires = DateTimeOffset.UtcNow.AddMinutes(5); return _token; }
    }
    internal bool Consume(string token)
    {
        lock (_gate)
        {
            if (_token is null || _expires < DateTimeOffset.UtcNow || token.Length != _token.Length ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(_token))) return false;
            _token = null; return true;
        }
    }
    internal static void Map(WebApplication app)
    {
        app.MapGet("/local-operator", () => Results.Content("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Local operator</title>
            <body><h1>Local operator access</h1><p id="status">Opening the protected local planning interface…</p>
            <script>(async()=>{const t=location.hash.slice(1);history.replaceState(null,'','/local-operator');
            if(!t){document.getElementById('status').textContent='Restart the local server to open an authenticated operator window.';return;}
            const r=await fetch('/local-operator/session',{method:'POST',headers:{'Content-Type':'text/plain'},body:t});
            if(r.ok)location.replace('/planning');else document.getElementById('status').textContent='This operator link expired or was already used. Restart the local profile.';})();</script></body></html>
            """, "text/html"));
        app.MapPost("/local-operator/session", async (HttpContext context, LocalOperatorBootstrap bootstrap) =>
        {
            if (!LocalOperatorAccess.IsLocal(context) || context.Request.ContentLength is not (> 0 and <= 128)) return Results.Forbid();
            using var reader = new StreamReader(context.Request.Body);
            if (!bootstrap.Consume(await reader.ReadToEndAsync(context.RequestAborted))) return Results.Unauthorized();
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "local-operator"), new Claim(LocalOperatorAccess.Claim, "true")], CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.NoContent();
        });
        if (app.Services.GetRequiredService<IOptions<TypedWorkflowPlanningSettings>>().Value.OpenLocalOperatorBrowser)
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var url = Hosting.GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || !address.IsLoopback) return;
                var token = app.Services.GetRequiredService<LocalOperatorBootstrap>().Issue();
                try { app.Services.GetRequiredService<ILocalOperatorBrowserLauncher>().Open(new Uri(address.AbsoluteUri.TrimEnd('/') + "/local-operator#" + token)); }
                catch (Exception ex)
                {
                    app.Logger.LogWarning("The local operator browser could not be opened ({ErrorType}). Ordinary strict planning remains available.", ex.GetType().Name);
                }
            });
    }
}
