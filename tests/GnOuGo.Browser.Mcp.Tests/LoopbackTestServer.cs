using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Browser.Mcp.Tests;

/// <summary>Kestrel retains ownership of its ephemeral port from bind until disposal.</summary>
internal sealed class LoopbackTestServer(WebApplication app) : IAsyncDisposable
{
    internal string Origin => app.Urls.Single().TrimEnd('/');
    internal static async Task<LoopbackTestServer> StartAsync(RequestDelegate handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var app = builder.Build(); app.Run(handler);
        try { await app.StartAsync(ct); return new(app); }
        catch { await app.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => app.DisposeAsync();
}
