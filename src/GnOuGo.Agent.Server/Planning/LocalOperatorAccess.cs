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
    internal static bool IsLocal(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            !Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var host) || !host.IsLoopback) return false;
        if (context.Request.Headers.Origin is { Count: > 0 } origin &&
            (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var source) || source.Authority != host.Authority || source.Scheme != host.Scheme)) return false;
        return true;
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
            if(!t){document.getElementById('status').textContent='Start the Diagnostic launch profile to open an authenticated operator window.';return;}
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
                var url = app.Urls.FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback);
                if (url is null) return;
                var token = app.Services.GetRequiredService<LocalOperatorBootstrap>().Issue();
                Process.Start(new ProcessStartInfo(url.TrimEnd('/') + "/local-operator#" + token) { UseShellExecute = true });
            });
    }
}
