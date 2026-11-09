using GnOuGo.AI.Core;
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
    internal static async Task<CampaignInferenceProxy> StartAsync(KeyVaultBenchmarkModel model, string run, string? chatToolsReasoningEffort = null, string? wireApi = null)
    {
        KeyVaultBenchmarkModel.ValidateSdkChatToolsReasoningEffort(chatToolsReasoningEffort);
        ValidateWireApi(wireApi);
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
                var result = await model.ProxyInferenceAsync(run, context.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase), body, context.RequestAborted, chatToolsReasoningEffort, wireApi);
                context.Response.StatusCode = result.Status;
                context.Response.ContentType = result.ContentType;
                await context.Response.WriteAsync(result.Body, context.RequestAborted);
            }
            catch (Exception ex)
            {
                await model.RetainProxyFailureAsync(run, body, ex, context.Request.Headers["X-GnOuGo-Inference-Upstream"].ToString());
                context.Response.StatusCode = ex is InvalidOperationException ? 400 : 502;
                await context.Response.WriteAsync("Campaign inference admission or transport failed; encrypted evidence retained.", CancellationToken.None);
            }
        });
        await app.StartAsync(); return proxy;
    }
    internal static void ValidateWireApi(string? wireApi)
    {
        if (wireApi is not (null or "completions" or "responses")) throw new ArgumentException("Invalid explicit SDK wire API.");
    }
    internal static void ConfigureWireApi(McpServerOptions settings, string provider, string? wireApi)
    {
        ValidateWireApi(wireApi);
        if (wireApi is null) return;
        if (string.IsNullOrEmpty(provider) || !provider.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("An exact SDK provider configuration name is required.");
        settings.EnvironmentVariables ??= new();
        settings.EnvironmentVariables["Code__Copilot__Providers__" + provider + "__WireApi"] = wireApi;
        // Existing KeyVault precedence remains intact. The proxy checks the actual
        // selected endpoint before admission; a conflicting saved setting cannot fall back.
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
