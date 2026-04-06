using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Endpoints;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Agent.Shared;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using OtlpTenantCollector.Hosting;

namespace GnOuGo.Agent.Server.Hosting;

public static class GnOuGoAgentWebHost
{
    public static WebApplication Build(
        string[] args,
        string? urls = null,
        string? contentRoot = null,
        bool enableHttpsRedirection = true)
    {
        WebApplicationBuilder builder;
        var isDesktopHosted = !string.IsNullOrWhiteSpace(contentRoot);
        var diagnosticsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GnOuGo.Agent",
            "Logs");
        var diagnosticsFile = Path.Combine(diagnosticsDir, "desktop.log");

        void Log(string message)
        {
            if (!isDesktopHosted)
                return;

            Directory.CreateDirectory(diagnosticsDir);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [SERVER] {message}{Environment.NewLine}";
            using var stream = new FileStream(diagnosticsFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
        }

        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            builder = WebApplication.CreateBuilder(args);
        }
        else
        {
            // When hosted from the Desktop (Photino) entry assembly, static web assets (including
            // /_framework/blazor.web.js) won't resolve unless we force the application name to
            // the Server assembly and load the static web assets manifest.
            var appName = typeof(GnOuGoAgentWebHost).Assembly.GetName().Name;

            var options = new WebApplicationOptions
            {
                Args = args,
                ApplicationName = appName,
                ContentRootPath = contentRoot,
                WebRootPath = Path.Combine(contentRoot, "wwwroot")
            };

            builder = WebApplication.CreateBuilder(options);

            // In Desktop/published mode the ASP.NET Core environment defaults to "Production",
            // so appsettings.Development.json is not loaded automatically.
            // Explicitly layer it here (optional – absent in a Release/AOT publish).
            builder.Configuration.AddJsonFile(
                Path.Combine(contentRoot, "appsettings.Development.json"),
                optional: true,
                reloadOnChange: false);

            // Ensure static web assets are available when running as a library host.
            // In published / NativeAOT builds the development manifest may be absent;
            // UseStaticFiles() + the copied wwwroot is sufficient in that case.
            try
            {
                builder.WebHost.UseStaticWebAssets();
            }
            catch (InvalidOperationException)
            {
                // Manifest not found – expected in published Desktop builds.
            }
        }

        if (!string.IsNullOrWhiteSpace(urls))
        {
            builder.WebHost.UseUrls(urls);
        }

        // --- config ---
        // Layer in the persisted user-settings.json (written by LLMRuntimeOptionsStore after /llm wizard).
        // This ensures wizard-saved API keys survive restarts and are visible in IOptions<LLMOptions>.
        var userSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GnOuGo.Agent",
            "user-settings.json");
        builder.Configuration.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: false);

        // LLM + MCP configuration (same structure as GnOuGo.Flow.Server)
        var llmOptions = builder.Configuration.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();

        // Resolve the dotnet executable used by this process so stdio MCP servers are spawned
        // with the SAME dotnet installation that's running the agent server.
        // Without this, child processes may find a system dotnet that has no SDKs installed.
        var dotnetExe = ResolveDotnetExecutable();
        EnsureDotnetRootEnv(dotnetExe);
        SubstituteDotnetCommand(llmOptions.McpServers, dotnetExe);
        ResolveRelativeMcpProjectPaths(llmOptions.McpServers);

        // Bind LLMOptions for IOptions<LLMOptions> (used by LLMRuntimeOptionsStore)
        builder.Services.Configure<LLMOptions>(
            builder.Configuration.GetSection(LLMOptions.SectionName));

        // OpenTelemetry configuration
        builder.Services.Configure<OpenTelemetrySettings>(
            builder.Configuration.GetSection(OpenTelemetrySettings.SectionName));
        builder.Services.Configure<TraceDebugSettings>(
            builder.Configuration.GetSection(TraceDebugSettings.SectionName));

        var otelSettings = builder.Configuration
            .GetSection(OpenTelemetrySettings.SectionName)
            .Get<OpenTelemetrySettings>() ?? new OpenTelemetrySettings();

        if (otelSettings.Enabled)
        {
            var protocol = otelSettings.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;

            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(otelSettings.ServiceName, serviceVersion: "1.0.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = builder.Environment.EnvironmentName,
                    ["host.name"] = Environment.MachineName
                });

            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing =>
                {
                    tracing
                        .SetResourceBuilder(resourceBuilder)
                        .AddSource(AgentOTelTelemetry.ActivitySourceName);

                    if (otelSettings.IncludeAspNetCoreTraces)
                    {
                        tracing.AddAspNetCoreInstrumentation();
                    }

                    tracing
                        .AddHttpClientInstrumentation()
                        .AddOtlpExporter(o =>
                        {
                            o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                            o.Protocol = protocol;
                            if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                                o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                        });
                })
                .WithMetrics(metrics =>
                {
                    metrics
                        .SetResourceBuilder(resourceBuilder)
                        .AddMeter(AgentOTelTelemetry.MeterName)
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddOtlpExporter(o =>
                        {
                            o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                            o.Protocol = protocol;
                            if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                                o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                        });
                });

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.SetResourceBuilder(resourceBuilder);
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.ParseStateValues = true;
                logging.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                    o.Protocol = protocol;
                    if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                        o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                });
            });
        }

        // AOT-friendly JSON for Minimal APIs
        builder.Services.ConfigureHttpJsonOptions(static o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ChatJsonContext.Default);
        });
        builder.Services.Configure<ModelCatalogCacheSettings>(
            builder.Configuration.GetSection(ModelCatalogCacheSettings.SectionName));
        builder.Services.Configure<KeyVaultSettings>(
            builder.Configuration.GetSection(KeyVaultSettings.SectionName));
        builder.Services.AddOtlpCollectorCore(builder.Configuration);

        var keyVaultDbRelativePath = builder.Configuration.GetValue<string>("KeyVault:DatabasePath")
            ?? "data/gnougo-keyvault.db";
        var keyVaultDbPath = KeyVaultDatabasePathResolver.Resolve(keyVaultDbRelativePath, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(keyVaultDbPath)!);

        // --- services ---
        // LLMRuntimeOptionsStore: holds the live LLMOptions (updated by /llm wizard, persisted to user-settings.json)
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(TraceDebugService.HttpClientName);
        builder.Services.AddSingleton<LocalTraceDebugStore>();
        builder.Services.AddSingleton<LLMRuntimeOptionsStore>();
        builder.Services.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite($"Data Source={keyVaultDbPath}"));
        builder.Services.AddScoped<KeyVaultService>();
        builder.Services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();
        builder.Services.AddSingleton<SecureWorkflowRuntimeFactory>();
        builder.Services.AddSingleton<CollectorTracePersistence>();
        builder.Services.AddSingleton<ILoggerProvider, CollectorLoggerProvider>();


        builder.Services.AddSingleton<ILLMClient>(sp =>
        {
            var store = sp.GetRequiredService<LLMRuntimeOptionsStore>();
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            // DynamicRoutingLLMClientAdapter reads the LATEST options from the store on every call,
            // so a /llm wizard update takes effect for the very next message.
            return new DynamicRoutingLLMClientAdapter(http, store);
        });
        builder.Services.AddSingleton<ILLMModelCatalog>(sp =>
        {
            var store = sp.GetRequiredService<LLMRuntimeOptionsStore>();
            var cache = sp.GetRequiredService<IMemoryCache>();
            var settings = sp.GetRequiredService<IOptions<ModelCatalogCacheSettings>>().Value;
            var logger = sp.GetRequiredService<ILogger<CachedLlmModelCatalog>>();
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var innerCatalog = new DynamicRoutingLLMModelCatalogAdapter(http, store);
            return new CachedLlmModelCatalog(innerCatalog, store, cache, settings, logger);
        });
        builder.Services.AddSingleton<IMcpClientFactory>(_ =>
        {
            if (llmOptions.McpServers.Count > 0)
                return new ConfiguredMcpClientFactory(llmOptions.McpServers);
            return new InMemoryMcpClientFactory();
        });
        builder.Services.AddSingleton<AgentHumanInputProvider>();
        builder.Services.AddSingleton<AgentOTelTelemetry>();
        builder.Services.AddSingleton<ConfigureProvidersService>();
        builder.Services.AddSingleton<ConfigureAgentsService>();
        builder.Services.AddSingleton<SmartFlowService>();
        builder.Services.AddSingleton<TraceDebugService>();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddSingleton<WordChunker>();

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var keyVaultDb = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
            keyVaultDb.Database.EnsureCreated();

            var keyVaultService = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
            keyVaultService.EnsureDefaultKeyPairAsync().GetAwaiter().GetResult();
        }

        app.Services.InitializeOtlpCollectorAsync().GetAwaiter().GetResult();

        if (isDesktopHosted)
        {
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? "<null>";
                var shouldTrace =
                    path == "/" ||
                    path == "/health" ||
                    path.StartsWith("/desktop/page-loaded", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/desktop/client-ready", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/ui", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase);

                if (shouldTrace)
                {
                    var ua = context.Request.Headers.UserAgent.ToString();
                    Log($"REQ {context.Request.Method} {path} ua={ua}");
                }

                await next();

                if (shouldTrace)
                {
                    Log($"RES {context.Request.Method} {path} status={context.Response.StatusCode}");
                }
            });
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        if (enableHttpsRedirection)
        {
            app.UseHttpsRedirection();
        }

        app.UseStaticFiles();

        // Also map static assets via endpoints (Static Web Assets), so /_framework/*
        // and other package-provided assets resolve correctly even when hosted from
        // the Desktop entry assembly.
        // The manifest file may be absent in NativeAOT / Desktop published builds;
        // in that case UseStaticFiles() above is sufficient.
        var staticAssetsManifest = Path.Combine(
            app.Environment.ContentRootPath,
            $"{typeof(GnOuGoAgentWebHost).Assembly.GetName().Name}.staticwebassets.endpoints.json");

        if (File.Exists(staticAssetsManifest))
        {
            app.MapStaticAssets();
        }
        else
        {
            Log($"Static web assets manifest not found at '{staticAssetsManifest}', skipping MapStaticAssets().");
        }

        app.UseAntiforgery();

        // --- API ---
        app.MapPost("/api/chat", ChatEndpoints.CompleteAsync);
        app.MapPost("/api/chat/stream", ChatEndpoints.StreamAsync);
        app.MapGet("/api/llm/providers", LlmProviderEndpoints.ListProviders);
        app.MapGet("/api/llm/providers/{provider}/models", LlmProviderEndpoints.ListModelsAsync);
        app.MapOtlpCollectorApi(includeReceivers: true, includeHealthEndpoint: false);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/desktop/boot-log/{token}", (string token, string? step, string? detail) =>
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                var safeStep = string.IsNullOrWhiteSpace(step) ? "<unknown>" : step.Trim();
                var safeDetail = string.IsNullOrWhiteSpace(detail) ? "<none>" : detail.Trim();
                Log($"BOOT token={token} step={safeStep} detail={safeDetail}");
            }

            return Results.NoContent();
        });
        app.MapPost("/desktop/page-loaded/{token}", (string token) =>
        {
            DesktopWebViewTracker.MarkPageLoaded(token);
            return Results.NoContent();
        });
        app.MapGet("/desktop/client-ready/{token}", (string token) =>
        {
            DesktopWebViewTracker.MarkClientReady(token);
            return Results.NoContent();
        });

        // --- UI ---
        // Always register interactive server render mode.
        // In published Desktop/NativeAOT builds the static web assets manifest
        // may be absent; MapStaticAssets() is only called when the manifest exists,
        // but interactive SSR is always available.
        app.MapRazorComponents<GnOuGo.Agent.Server.Components.App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    // ── Dotnet resolution helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the full path to the dotnet executable that is running this process.
    /// Falls back to "dotnet" (resolved via PATH) if it cannot be determined.
    /// </summary>
    private static string ResolveDotnetExecutable()
    {
        // 1. DOTNET_ROOT env var — set by dotnet installers
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                      ?? Environment.GetEnvironmentVariable("DOTNET_ROOT_X64")
                      ?? Environment.GetEnvironmentVariable("DOTNET_ROOT_X86");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        // 2. Current process path — for framework-dependent apps the host IS dotnet.exe
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        // 3. Fallback — relies on PATH being correct
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    /// <summary>
    /// Sets DOTNET_ROOT in the current process environment (inherited by all child processes)
    /// so stdio MCP subprocesses find the same SDK that the agent server is using.
    /// </summary>
    private static void EnsureDotnetRootEnv(string dotnetExe)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            return; // already set — respect it

        var dir = Path.GetDirectoryName(dotnetExe);
        if (!string.IsNullOrWhiteSpace(dir) && File.Exists(dotnetExe))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", dir);
    }

    /// <summary>
    /// Replaces the literal "dotnet" / "dotnet.exe" command in stdio MCP server configs
    /// with the resolved full path so subprocesses use the correct dotnet installation.
    /// </summary>
    private static void SubstituteDotnetCommand(
        Dictionary<string, GnOuGo.AI.Core.McpServerOptions> servers,
        string dotnetExe)
    {
        if (dotnetExe is "dotnet" or "dotnet.exe") return; // nothing to substitute

        foreach (var cfg in servers.Values)
        {
            if (!string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(cfg.Command)) continue;
            var cmdName = Path.GetFileNameWithoutExtension(cfg.Command);
            if (cmdName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                cfg.Command = dotnetExe;
        }
    }

    /// <summary>
    /// Converts relative <c>--project</c> paths in stdio MCP server configs to absolute paths
    /// by locating the solution root (directory containing <c>global.json</c> or a <c>.sln</c> file)
    /// and resolving the project name under <c>src/</c>.
    /// This is necessary because child processes inherit the parent's working directory,
    /// which may not match the relative path written in appsettings.json.
    /// </summary>
    private static void ResolveRelativeMcpProjectPaths(
        Dictionary<string, GnOuGo.AI.Core.McpServerOptions> servers)
    {
        var solutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
        if (solutionRoot is null) return;

        var srcDir = Path.Combine(solutionRoot, "src");

        foreach (var cfg in servers.Values)
        {
            if (!string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase)) continue;
            if (cfg.Args is not { Count: > 1 }) continue;

            for (var i = 0; i < cfg.Args.Count - 1; i++)
            {
                if (cfg.Args[i] != "--project") continue;

                var projectArg = cfg.Args[i + 1];
                if (Path.IsPathRooted(projectArg)) continue; // already absolute

                // Extract the project name from any relative path like ../../GnOuGo.Foo/GnOuGo.Foo.csproj
                var normalised = projectArg.Replace('/', Path.DirectorySeparatorChar)
                                           .Replace('\\', Path.DirectorySeparatorChar);
                var projectDir  = Path.GetDirectoryName(normalised) ?? "";
                var projectName = Path.GetFileName(projectDir);
                var csprojFile  = Path.GetFileName(normalised);

                if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(csprojFile)) continue;

                var absPath = Path.Combine(srcDir, projectName, csprojFile);
                if (File.Exists(absPath))
                    cfg.Args[i + 1] = absPath;
            }
        }
    }

    /// <summary>
    /// Walks up the directory tree from <paramref name="start"/> looking for the
    /// solution root — identified by the presence of <c>global.json</c> or any <c>.sln</c> file.
    /// </summary>
    private static string? FindSolutionRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) ||
                dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;

            dir = dir.Parent;
        }
        return null;
    }
}
