using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.AI.Core;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Hosting;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;

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
    public void Build_WhenDesktopHostedInDevelopment_LoadsDotnetRunStdIoMcpServersFromDevelopmentConfig()
    {
        var contentRoot = GetServerContentRoot();
        var tempContentRoot = Path.Combine(
            Path.GetTempPath(),
            "gnougo-agent-server-desktop-dev-config-tests",
            Guid.NewGuid().ToString("N"));
        var previousAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        Directory.CreateDirectory(tempContentRoot);
        File.Copy(
            Path.Combine(contentRoot, "appsettings.json"),
            Path.Combine(tempContentRoot, "appsettings.json"));
        File.Copy(
            Path.Combine(contentRoot, "appsettings.Development.json"),
            Path.Combine(tempContentRoot, "appsettings.Development.json"));
        File.Copy(
            Path.Combine(contentRoot, "appsettings.Desktop.json"),
            Path.Combine(tempContentRoot, "appsettings.Desktop.json"));

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", Environments.Development);

            var args = TelemetryTestHostArgs.Create();

            using var app = GnOuGoAgentWebHost.Build(
                args,
                urls: "http://127.0.0.1:0",
                contentRoot: tempContentRoot,
                enableHttpsRedirection: false);

            var llmOptions = app.Services.GetRequiredService<IOptions<LLMOptions>>().Value;
            AssertDevelopmentDotnetMcpServer(llmOptions, "GnOuGo.Browser.Mcp", Path.GetFullPath(Path.Combine(contentRoot, "..", "GnOuGo.Browser.Mcp", "GnOuGo.Browser.Mcp.csproj")));
            AssertDevelopmentDotnetMcpServer(llmOptions, "GnOuGo.Cmd.Mcp", Path.GetFullPath(Path.Combine(contentRoot, "..", "GnOuGo.Cmd.Mcp", "GnOuGo.Cmd.Mcp.csproj")));
            AssertDevelopmentDotnetMcpServer(llmOptions, "GnOuGo.Document.Mcp", Path.GetFullPath(Path.Combine(contentRoot, "..", "GnOuGo.Document.Mcp", "GnOuGo.Document.Mcp.csproj")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetCoreEnvironment);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);

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
    public async Task StartAsync_WhenDesktopHostedFromPublishLikeContentRoot_ServesBlazorFrameworkAndNegotiationEndpoint()
    {
        var sourceContentRoot = GetServerContentRoot();
        var tempContentRoot = Path.Combine(
            Path.GetTempPath(),
            "gnougo-agent-server-desktop-publishlike-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempContentRoot);
        CopyPublishLikeDesktopContent(sourceContentRoot, tempContentRoot);

        var args = TelemetryTestHostArgs.Create();

        await using var app = GnOuGoAgentWebHost.Build(
            args,
            urls: "http://127.0.0.1:0",
            contentRoot: tempContentRoot,
            enableHttpsRedirection: false);

        await app.StartAsync();

        try
        {
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);
            var baseAddress = TestServerAddressResolver.NormalizeBaseAddress(
                publishedEndpoints.AppBaseAddress ?? throw new InvalidOperationException("The main app address should be published."));

            using var http = new HttpClient { BaseAddress = new Uri(baseAddress + "/", UriKind.Absolute) };

            var scriptResponse = await http.GetAsync("_framework/blazor.web.js");
            Assert.True(scriptResponse.IsSuccessStatusCode, $"GET /_framework/blazor.web.js returned {(int)scriptResponse.StatusCode} {scriptResponse.StatusCode}.");

            var scriptBody = await scriptResponse.Content.ReadAsStringAsync();
            Assert.Contains("Blazor", scriptBody, StringComparison.OrdinalIgnoreCase);

            using var negotiateRequest = new HttpRequestMessage(HttpMethod.Post, "_blazor/negotiate?negotiateVersion=1")
            {
                Content = new StringContent("{}")
            };
            negotiateRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            negotiateRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var negotiateResponse = await http.SendAsync(negotiateRequest);
            Assert.True(negotiateResponse.IsSuccessStatusCode, $"POST /_blazor/negotiate returned {(int)negotiateResponse.StatusCode} {negotiateResponse.StatusCode}.");

            var negotiateBody = await negotiateResponse.Content.ReadAsStringAsync();
            Assert.Contains("connectionId", negotiateBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();

            if (Directory.Exists(tempContentRoot))
                Directory.Delete(tempContentRoot, recursive: true);
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
            }, timeout: TimeSpan.FromSeconds(20));

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

    [Fact]
    public async Task StartAsync_LoadsRuntimeProvidersAndMcpServersFromKeyVaultStore()
    {
        var contentRoot = GetServerContentRoot();
        var agentDbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-webhost-agent-{Guid.NewGuid():N}.db");
        var keyVaultDbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-webhost-keyvault-{Guid.NewGuid():N}.db");

        await using (var seedDb = new KeyVaultDbContext(new DbContextOptionsBuilder<KeyVaultDbContext>()
                         .UseSqlite($"Data Source={keyVaultDbPath}")
                         .Options))
        {
            await seedDb.Database.EnsureCreatedAsync();
        }

        var seedServices = new ServiceCollection();
        seedServices.AddLogging();
        seedServices.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite($"Data Source={keyVaultDbPath}"));
        seedServices.AddScoped<KeyVaultService>();

        await using (var seedProvider = seedServices.BuildServiceProvider())
        await using (var scope = seedProvider.CreateAsyncScope())
        {
            var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            await keyVault.EnsureDefaultKeyPairAsync();
            await keyVault.SetSecretAsync(
                "LLM--Models--ollama",
                "{\"provider\":\"ollama\",\"url\":\"http://127.0.0.1:11434\",\"model\":\"llama3.2\",\"authType\":\"none\"}",
                null,
                "test",
                CancellationToken.None);
            await keyVault.SetSecretAsync(
                "LLM--McpServers--Github",
                "{\"name\":\"Github\",\"transport\":\"http\",\"description\":\"GitHub automation\",\"url\":\"https://api.githubcopilot.com/mcp/\",\"authType\":\"api_key\",\"apiKey\":\"gh-secret\"}",
                null,
                "test",
                CancellationToken.None);
        }

        var args = TelemetryTestHostArgs.Create(
            $"--Agent:DatabasePath={agentDbPath}",
            $"--KeyVault:DatabasePath={keyVaultDbPath}");

        await using var app = GnOuGoAgentWebHost.Build(
            args,
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();
            var runtimeConfigStore = app.Services.GetRequiredService<IKeyVaultRuntimeConfigStore>();
            var effective = await runtimeConfigStore.BuildEffectiveOptionsAsync(new LLMOptions(), CancellationToken.None);

            Assert.True(effective.Models.TryGetValue("ollama", out var ollama));
            Assert.NotNull(ollama);
            Assert.Equal("http://127.0.0.1:11434", ollama.Url);
            Assert.True(effective.McpServers.TryGetValue("Github", out var github));
            Assert.NotNull(github);
            Assert.Equal("https://api.githubcopilot.com/mcp/", github.Url);
        }
        finally
        {
            await app.StopAsync();

            TryDeleteFile(agentDbPath);
            TryDeleteFile(keyVaultDbPath);
        }
    }

    [Fact]
    public void ResolveDatabasePath_WhenUsingAgentDesktopDefaultTelemetryPath_UsesDesktopGnOuGoDirectory()
    {
        var expected = Path.Combine(ResolveDesktopDirectory(), "GnOuGo", "data", "gnougo-telemetry.db");
        var actual = OtlpCollectorHostingExtensions.ResolveDatabasePath("data/gnougo-telemetry.db", AppContext.BaseDirectory);

        Assert.Equal(expected, actual);
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup for a temporary SQLite file.
        }
    }

    private static string ResolveDesktopDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
            return Path.GetFullPath(desktopPath);

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            return Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(Path.Combine(homePath, "Desktop"));

        throw new InvalidOperationException("Unable to resolve the current user's Desktop directory.");
    }

    private static void EnsureBundledToolExists(string bundledToolPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bundledToolPath)!);
        if (!File.Exists(bundledToolPath))
            File.WriteAllText(bundledToolPath, string.Empty);
    }

    private static void CopyPublishLikeDesktopContent(string sourceContentRoot, string destinationContentRoot)
    {
        File.Copy(
            Path.Combine(sourceContentRoot, "appsettings.json"),
            Path.Combine(destinationContentRoot, "appsettings.json"));
        File.Copy(
            Path.Combine(sourceContentRoot, "appsettings.Desktop.json"),
            Path.Combine(destinationContentRoot, "appsettings.Desktop.json"));

        var sourceWwwRoot = Path.Combine(sourceContentRoot, "wwwroot");
        var destinationWwwRoot = Path.Combine(destinationContentRoot, "wwwroot");
        CopyDirectory(sourceWwwRoot, destinationWwwRoot);

        var destinationFrameworkRoot = Path.Combine(destinationWwwRoot, "_framework");
        Directory.CreateDirectory(destinationFrameworkRoot);

        File.Copy(
            ResolveBlazorFrameworkAsset("blazor.web.js"),
            Path.Combine(destinationFrameworkRoot, "blazor.web.js"),
            overwrite: true);
        File.Copy(
            ResolveBlazorFrameworkAsset("blazor.server.js"),
            Path.Combine(destinationFrameworkRoot, "blazor.server.js"),
            overwrite: true);
    }

    private static string ResolveBlazorFrameworkAsset(string fileName)
    {
        var nugetPackageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(nugetPackageRoot))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            nugetPackageRoot = Path.Combine(userProfile, ".nuget", "packages");
        }

        var packageRoot = Path.Combine(nugetPackageRoot, "microsoft.aspnetcore.app.internal.assets");
        Assert.True(Directory.Exists(packageRoot), $"Blazor framework package root not found: {packageRoot}");

        var candidate = Directory.GetFiles(packageRoot, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}_framework{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(candidate), $"Unable to locate {fileName} under {packageRoot}.");
        return candidate!;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static void AssertBundledToolServer(LLMOptions llmOptions, string serverName, string expectedCommand)
    {
        Assert.True(llmOptions.McpServers.TryGetValue(serverName, out var server));
        Assert.NotNull(server);
        Assert.Equal("stdio", server.Type);
        Assert.Equal(expectedCommand, server.Command);
        Assert.True(server.Args is null or { Count: 0 });
    }

    private static void AssertDevelopmentDotnetMcpServer(LLMOptions llmOptions, string serverName, string expectedProject)
    {
        Assert.True(llmOptions.McpServers.TryGetValue(serverName, out var server));
        Assert.NotNull(server);
        Assert.Equal("stdio", server.Type);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(server.Command));
        Assert.NotNull(server.Args);
        Assert.Contains("run", server.Args);
        var projectArgumentIndex = server.Args.IndexOf("--project");
        Assert.True(projectArgumentIndex >= 0, $"{serverName} should pass --project to dotnet run.");
        Assert.True(projectArgumentIndex + 1 < server.Args.Count, $"{serverName} should pass a project path after --project.");
        Assert.Equal(expectedProject, server.Args[projectArgumentIndex + 1]);
        Assert.Contains("--no-launch-profile", server.Args);
        Assert.Contains("quiet", server.Args);
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

