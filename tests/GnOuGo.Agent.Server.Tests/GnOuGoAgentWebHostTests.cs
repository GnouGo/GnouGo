using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using GnOuGo.Agent.Server.Hosting;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GnOuGoAgentWebHostTests
{
    private static string GetServerContentRoot()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src",
            "GnOuGo.Agent.Server"));

        Assert.True(Directory.Exists(contentRoot), $"Content root not found: {contentRoot}");
        return contentRoot;
    }

    [Fact]
    public void Build_WhenDesktopHosted_RegistersEmbeddedCollectorServices()
    {
        var contentRoot = GetServerContentRoot();

        using var app = GnOuGoAgentWebHost.Build(
            Array.Empty<string>(),
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        var queue = app.Services.GetRequiredService<TelemetryIngestQueue>();
        Assert.NotNull(queue);

        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("GnOuGo.Agent.Server.Tests.Boot");
        logger.LogInformation("Collector-backed logging should resolve successfully during desktop host boot.");
    }

    [Fact]
    public async Task StartAsync_WhenDesktopHosted_ReturnsHomePageSuccessfully()
    {
        var contentRoot = GetServerContentRoot();

        await using var app = GnOuGoAgentWebHost.Build(
            Array.Empty<string>(),
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        await app.StartAsync();

        try
        {
            var addressesFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();

            var address = Assert.Single(addressesFeature?.Addresses ?? []);

            using var http = new HttpClient();
            var response = await http.GetAsync(address);

            Assert.True(response.IsSuccessStatusCode, $"GET / returned {(int)response.StatusCode} {response.StatusCode}.");

            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("GnOuGo.Agent", html, StringComparison.Ordinal);
            Assert.Contains("Start a new chat", html, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}

