using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GnOuGo.ProxyCopilot.Server.Tests;

internal sealed class TestHost(WebApplication app) : IAsyncDisposable
{
    public WebApplication App { get; } = app;
    public string Url => App.Urls.Single();
    public HttpClient Client { get; private set; } = null!;
    public static async Task<TestHost> Upstream(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        var app = builder.Build(); app.Run(context => handler(context));
        return await Start(app);
    }
    public static async Task<TestHost> Proxy(string upstream, string type = "openai", Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:0",
            ["ProxyCopilot:Providers:test:Connection:Type"] = type,
            ["ProxyCopilot:Providers:test:Connection:Url"] = upstream,
            ["ProxyCopilot:Providers:test:Models:model:UpstreamId"] = "vendor/upstream-model",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:MaxInputTokens"] = "10000",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:MaxOutputTokens"] = "1000",
            ["ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:SupportsTools"] = "true",
            ["ProxyCopilot:Providers:test:Connection:RequestPolicy:UnspecifiedOutputTokens"] = "Configured",
            ["ProxyCopilot:Providers:test:Connection:RequestPolicy:DefaultMaxOutputTokens"] = "1000"
        };
        foreach (var value in extra ?? []) values[value.Key] = value.Value;
        var app = ProxyApplication.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(values);
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        });
        return await Start(app);
    }
    private static async Task<TestHost> Start(WebApplication app)
    {
        await app.StartAsync();
        return new(app) { Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) } };
    }
    public static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
    public static async Task<JsonObject> Read(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
    public async ValueTask DisposeAsync() { Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); }
}
