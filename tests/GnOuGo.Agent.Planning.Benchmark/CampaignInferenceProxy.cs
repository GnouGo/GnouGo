using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// The existing SDK interception hook attests at /ready before its MCP starts.
// This test host admits every inference through the same encrypted EUR ledger.
internal sealed class CampaignInferenceProxy(WebApplication app) : IAsyncDisposable
{
    internal string Endpoint => app.Urls.Single() + "/inference";
    internal bool Ready { get; private set; }
    internal bool ExecutionEnabled { get; set; }
    internal static async Task<CampaignInferenceProxy> StartAsync(KeyVaultBenchmarkModel model, string run)
    {
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build(); var proxy = new CampaignInferenceProxy(app);
        app.MapPost("/inference/ready", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            if (await reader.ReadToEndAsync(context.RequestAborted) != "sdk-http-interception-v1") { context.Response.StatusCode = 400; return; }
            proxy.Ready = true; context.Response.StatusCode = 204;
        });
        app.MapPost("/inference", async (HttpContext context) =>
        {
            if (!proxy.Ready || !proxy.ExecutionEnabled) { context.Response.StatusCode = 403; return; }
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            try
            {
                var result = await model.ProxyInferenceAsync(run, context.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToString()), body, context.RequestAborted);
                context.Response.StatusCode = result.Status;
                context.Response.ContentType = result.ContentType;
                await context.Response.WriteAsync(result.Body, context.RequestAborted);
            }
            catch (Exception) { context.Response.StatusCode = 502; await context.Response.WriteAsync("Campaign inference admission or transport failed.", CancellationToken.None); }
        });
        await app.StartAsync(); return proxy;
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
