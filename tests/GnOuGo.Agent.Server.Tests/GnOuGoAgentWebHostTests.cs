using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.AI.Core;
using OtlpTenantCollector.Services;
using System.Net;

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
        var args = TelemetryTestHostArgs.Create();

        using var app = GnOuGoAgentWebHost.Build(
            args,
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
    public void Build_WhenServerRunsOutsideDevelopment_LoadsBundledStdIoMcpServersFromBaseConfig()
    {
        var contentRoot = GetServerContentRoot();
        var tempContentRoot = Path.Combine(
            Path.GetTempPath(),
            "gnougo-agent-server-config-tests",
            Guid.NewGuid().ToString("N"));
        var bundledCmdToolPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "GnOuGo.Cmd.Mcp",
            "GnOuGo.Cmd.Mcp"));
        var bundledDocumentToolPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "GnOuGo.Document.Mcp",
            "GnOuGo.Document.Mcp"));

        Directory.CreateDirectory(tempContentRoot);
        File.Copy(
            Path.Combine(contentRoot, "appsettings.json"),
            Path.Combine(tempContentRoot, "appsettings.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(bundledCmdToolPath)!);
        if (!File.Exists(bundledCmdToolPath))
            File.WriteAllText(bundledCmdToolPath, string.Empty);

        Directory.CreateDirectory(Path.GetDirectoryName(bundledDocumentToolPath)!);
        if (!File.Exists(bundledDocumentToolPath))
            File.WriteAllText(bundledDocumentToolPath, string.Empty);

        try
        {
            var args = TelemetryTestHostArgs.Create();

            using var app = GnOuGoAgentWebHost.Build(
                args,
                urls: "http://127.0.0.1:0",
                contentRoot: tempContentRoot,
                enableHttpsRedirection: false);

            var llmOptions = app.Services.GetRequiredService<IOptions<LLMOptions>>().Value;
            Assert.True(llmOptions.McpServers.TryGetValue("GnOuGo.Cmd.Mcp", out var cmdServer));
            Assert.NotNull(cmdServer);
            Assert.Equal("stdio", cmdServer.Type);
            Assert.Equal(bundledCmdToolPath, cmdServer.Command);
            Assert.True(cmdServer.Args is null or { Count: 0 });

            Assert.True(llmOptions.McpServers.TryGetValue("GnOuGo.Document.Mcp", out var documentServer));
            Assert.NotNull(documentServer);
            Assert.Equal("stdio", documentServer.Type);
            Assert.Equal(bundledDocumentToolPath, documentServer.Command);
            Assert.True(documentServer.Args is null or { Count: 0 });
        }
        finally
        {
            if (Directory.Exists(tempContentRoot))
                Directory.Delete(tempContentRoot, recursive: true);
        }
    }

    [Fact]
    public void Build_WhenDesktopHosted_LoadsBundledDesktopStdIoMcpServersFromDesktopConfig()
    {
        var contentRoot = GetServerContentRoot();
        var tempContentRoot = Path.Combine(
            Path.GetTempPath(),
            "gnougo-agent-server-desktop-config-tests",
            Guid.NewGuid().ToString("N"));
        var bundledBrowserToolPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "GnOuGo.Browser.Mcp",
            "GnOuGo.Browser.Mcp"));
        var bundledCmdToolPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "GnOuGo.Cmd.Mcp",
            "GnOuGo.Cmd.Mcp"));
        var bundledDocumentToolPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "GnOuGo.Document.Mcp",
            "GnOuGo.Document.Mcp"));

        Directory.CreateDirectory(tempContentRoot);
        File.Copy(
            Path.Combine(contentRoot, "appsettings.json"),
            Path.Combine(tempContentRoot, "appsettings.json"));
        File.Copy(
            Path.Combine(contentRoot, "appsettings.Desktop.json"),
            Path.Combine(tempContentRoot, "appsettings.Desktop.json"));

        EnsureBundledToolExists(bundledBrowserToolPath);
        EnsureBundledToolExists(bundledCmdToolPath);
        EnsureBundledToolExists(bundledDocumentToolPath);

        try
        {
            var args = TelemetryTestHostArgs.Create();

            using var app = GnOuGoAgentWebHost.Build(
                args,
                urls: "http://127.0.0.1:0",
                contentRoot: tempContentRoot,
                enableHttpsRedirection: false);

            var llmOptions = app.Services.GetRequiredService<IOptions<LLMOptions>>().Value;
            AssertBundledToolServer(llmOptions, "GnOuGo.Browser.Mcp", bundledBrowserToolPath);
            AssertBundledToolServer(llmOptions, "GnOuGo.Cmd.Mcp", bundledCmdToolPath);
            AssertBundledToolServer(llmOptions, "GnOuGo.Document.Mcp", bundledDocumentToolPath);
        }
        finally
        {
            if (Directory.Exists(tempContentRoot))
                Directory.Delete(tempContentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_WhenDesktopHosted_ReturnsHomePageSuccessfully()
    {
        var contentRoot = GetServerContentRoot();
        var args = TelemetryTestHostArgs.Create();

        await using var app = GnOuGoAgentWebHost.Build(
            args,
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        await app.StartAsync();

        try
        {
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);
            Assert.NotNull(publishedEndpoints.AppBaseAddress);
            Assert.NotNull(publishedEndpoints.TelemetryGrpcBaseAddress);
            Assert.NotNull(publishedEndpoints.TelemetryHttpBaseAddress);
            var address = new Uri(publishedEndpoints.AppBaseAddress ?? throw new InvalidOperationException("The main app address should be published."));

            using var http = new HttpClient();
            var response = await http.GetAsync(address);

            Assert.True(response.IsSuccessStatusCode, $"GET / returned {(int)response.StatusCode} {response.StatusCode}.");

            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("GnOuGo.Agent", html, StringComparison.Ordinal);
            Assert.Contains("Start a new chat", html, StringComparison.Ordinal);

            var telemetryHealth = await http.GetAsync($"{publishedEndpoints.TelemetryHttpBaseAddress}/health");
            Assert.True(telemetryHealth.IsSuccessStatusCode, $"Telemetry health returned {(int)telemetryHealth.StatusCode} {telemetryHealth.StatusCode}.");

            var collectorApiOnMainPort = await http.GetAsync($"{publishedEndpoints.AppBaseAddress}/api/tenants/traces/recent");
            Assert.Equal(HttpStatusCode.NotFound, collectorApiOnMainPort.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_WhenOpenTelemetryUsesEmbeddedCollector_ExportsPlainLogsToCollectorStorage()
    {
        var contentRoot = GetServerContentRoot();
        var args = TelemetryTestHostArgs.Create(
            "--OpenTelemetry:Protocol=HttpProtobuf",
            $"--OpenTelemetry:ServiceName=GnOuGo.Agent.Server.Tests.{Guid.NewGuid():N}");

        await using var app = GnOuGoAgentWebHost.Build(
            args,
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        await app.StartAsync();

        try
        {
            var logger = app.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("GnOuGo.Agent.Server.Tests.EmbeddedCollector");

            var marker = $"embedded-collector-log-{Guid.NewGuid():N}";
            logger.LogInformation(marker);

            var exported = await WaitForAsync(async () =>
            {
                using var scope = app.Services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                var logs = await store.GetRecentLogsAsync(
                    tenantId: null,
                    limit: 200,
                    serviceName: null,
                    startUtc: null,
                    endUtc: null,
                    severityLevels: null,
                    traceIdFilter: null,
                    attributeContains: marker);

                return logs.Any(log => log.Body?.Contains(marker, StringComparison.Ordinal) == true);
            }, timeout: TimeSpan.FromSeconds(10));

            Assert.True(exported, "The embedded OTLP collector did not persist the exported OpenTelemetry log entry.");

            await WaitForAsync(async () =>
            {
                using var scope = app.Services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                var logs = await store.GetRecentLogsAsync(
                    tenantId: null,
                    limit: 200,
                    serviceName: null,
                    startUtc: null,
                    endUtc: null,
                    severityLevels: null,
                    traceIdFilter: null,
                    attributeContains: null);

                return !logs.Any(log => IsRecursiveInfrastructureLog(log));
            }, timeout: TimeSpan.FromSeconds(5));

            using (var scope = app.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                var logs = await store.GetRecentLogsAsync(
                    tenantId: null,
                    limit: 200,
                    serviceName: null,
                    startUtc: null,
                    endUtc: null,
                    severityLevels: null,
                    traceIdFilter: null,
                    attributeContains: null);

                var blockedLogs = logs
                    .Where(IsRecursiveInfrastructureLog)
                    .Take(10)
                    .Select(log => $"scope={ExtractScopeName(log.ScopeJson)} | body={Truncate(log.Body, 180)}")
                    .ToList();

                Assert.True(
                    blockedLogs.Count == 0,
                    $"Recursive infrastructure logs were still persisted:{Environment.NewLine}{string.Join(Environment.NewLine, blockedLogs)}");
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static bool IsRecursiveInfrastructureLog(OtlpTenantCollector.Models.LogRecordEntity log)
    {
        var scopeJson = log.ScopeJson ?? string.Empty;
        var body = log.Body ?? string.Empty;

        return scopeJson.Contains("OtlpTenantCollector", StringComparison.Ordinal)
            || scopeJson.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || scopeJson.Contains("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)
            || scopeJson.Contains("Microsoft.AspNetCore.Routing.EndpointMiddleware", StringComparison.Ordinal)
            || scopeJson.Contains("Grpc.AspNetCore.Server", StringComparison.Ordinal)
            || body.Contains("INSERT INTO \"log_records\"", StringComparison.Ordinal)
            || body.Contains("/opentelemetry.proto.collector.logs.v1.LogsService/Export", StringComparison.Ordinal);
    }

    private static string ExtractScopeName(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
            return "<none>";

        var marker = "\"name\":\"";
        var start = scopeJson.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return scopeJson;

        start += marker.Length;
        var end = scopeJson.IndexOf('"', start);
        return end < 0 ? scopeJson[start..] : scopeJson[start..end];
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private static void EnsureBundledToolExists(string bundledToolPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bundledToolPath)!);
        if (!File.Exists(bundledToolPath))
            File.WriteAllText(bundledToolPath, string.Empty);
    }

    private static void AssertBundledToolServer(LLMOptions llmOptions, string serverName, string expectedCommand)
    {
        Assert.True(llmOptions.McpServers.TryGetValue(serverName, out var server));
        Assert.NotNull(server);
        Assert.Equal("stdio", server.Type);
        Assert.Equal(expectedCommand, server.Command);
        Assert.True(server.Args is null or { Count: 0 });
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < timeout)
        {
            if (await predicate())
                return true;

            await Task.Delay(200);
        }

        return false;
    }
}

