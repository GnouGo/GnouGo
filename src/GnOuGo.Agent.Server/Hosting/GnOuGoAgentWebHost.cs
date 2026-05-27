using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
#if !GNOU_GO_AGENT_SERVER_NO_RAZOR_COMPONENTS
using GnOuGo.Agent.Server.Components;
#endif
using OpenTelemetry;
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
using GnOuGo.DocIngestor.Mcp;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Files.Server;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Mcp;
using GnOuGo.KeyVault.Core.Services;
using OtlpTenantCollector.Hosting;
using OtlpTenantCollector.Web;
using System.Net.Http.Headers;

namespace GnOuGo.Agent.Server.Hosting;

public static class GnOuGoAgentWebHost
{
    private static readonly HttpClient MountedMcpProxyHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
    };

    private static readonly MountedMcpRegistration AgentMcpRegistration = new(
        AgentMcpHostingExtensions.ServerName,
        "/mcp/agent",
        "Agent management and chat history via locally mounted MCP HTTP endpoint",
        "GnOuGo.Agent.Server.AgentMcpMount");

    private static readonly MountedMcpRegistration KeyVaultMcpRegistration = new(
        KeyVaultMcpHostingExtensions.ServerName,
        "/mcp/keyvault",
        "Encrypted secret manager via locally mounted MCP HTTP endpoint",
        "GnOuGo.Agent.Server.KeyVaultMcpMount");

    private static readonly MountedMcpRegistration DocsIngestorMcpRegistration = new(
        DocsIngestorMcpHostingExtensions.ServerName,
        "/mcp/docs-ingestor",
        "Document ingestion and vector search via locally mounted MCP HTTP endpoint",
        "GnOuGo.Agent.Server.DocsIngestorMcpMount");

    private static readonly IReadOnlyList<MountedMcpRegistration> MountedMcpRegistrations =
    [
        AgentMcpRegistration,
        KeyVaultMcpRegistration,
        DocsIngestorMcpRegistration
    ];

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

        var buildLogger = new DelegateLogger("GnOuGo.Agent.Server.Hosting", Log);

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

            if (!builder.Environment.IsDevelopment())
            {
                // Only apply desktop publish overrides when bundled tools are actually present.
                // In local source runs (no ./tools folder), keep Development MCP commands.
                var bundledToolsDirectory = Path.Combine(contentRoot, "tools");
                if (Directory.Exists(bundledToolsDirectory))
                {
                    builder.Configuration.AddJsonFile(
                        Path.Combine(contentRoot, "appsettings.Desktop.json"),
                        optional: true,
                        reloadOnChange: false);
                }
                else
                {
                    Log($"Skipping appsettings.Desktop.json because bundled tools directory '{bundledToolsDirectory}' was not found.");
                }
            }

            // Re-apply command-line arguments after the extra desktop JSON layers so
            // ad-hoc/test overrides (ports, paths, feature flags) still take precedence.
            if (args.Length > 0)
            {
                builder.Configuration.AddCommandLine(args);
            }

            // Ensure static web assets are available when running as a library host.
            // In published / NativeAOT builds the development manifest may be absent;
            // UseStaticFiles() + the copied wwwroot is sufficient in that case.
            try
            {
                builder.WebHost.UseStaticWebAssets();
            }
            catch (Exception ex)
            {
                buildLogger.LogDebug(ex, "Static web assets could not be enabled; falling back to copied wwwroot assets.");
                // Manifest not found or references non-existent paths — expected in published Desktop builds.
                // UseStaticFiles() + the copied wwwroot/ folder is sufficient.
            }
        }

        var primaryUrls = string.IsNullOrWhiteSpace(urls)
            ? builder.Configuration[WebHostDefaults.ServerUrlsKey] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            : urls;
        var applicationBasePath = isDesktopHosted
            ? builder.Environment.ContentRootPath
            : AppContext.BaseDirectory;
        Log($"ApplicationBasePath={applicationBasePath}");
        var collectorEndpointSettings = builder.WebHost.ConfigureEmbeddedCollectorEndpoints(builder.Configuration);

        if (collectorEndpointSettings.Enabled)
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                ConfigurePrimaryAndCollectorListeners(options, primaryUrls, collectorEndpointSettings);
            });
        }
        else if (!string.IsNullOrWhiteSpace(primaryUrls))
        {
            builder.WebHost.UseUrls(primaryUrls);
        }

        // LLM + MCP configuration (same structure as GnOuGo.Flow.Server)
        var llmOptions = builder.Configuration.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();

        // Resolve the dotnet executable used by this process so stdio MCP servers are spawned
        // with the SAME dotnet installation that's running the agent server.
        // Without this, child processes may find a system dotnet that has no SDKs installed.
        var dotnetExe = ResolveDotnetExecutable();
        EnsureDotnetRootEnv(dotnetExe);
        SubstituteDotnetCommand(llmOptions.McpServers, dotnetExe);
        ResolveRelativeMcpProjectPaths(llmOptions.McpServers, applicationBasePath);
        ResolveRelativeMcpCommandPaths(llmOptions.McpServers, applicationBasePath);

        // Register the normalized LLM options so runtime services receive the same MCP
        // configuration after command/path resolution.
        builder.Services.AddSingleton<IOptions<LLMOptions>>(_ => Options.Create(llmOptions));

        // OpenTelemetry configuration
        builder.Services.Configure<OpenTelemetrySettings>(
            builder.Configuration.GetSection(OpenTelemetrySettings.SectionName));
        builder.Services.Configure<TraceDebugSettings>(
            builder.Configuration.GetSection(TraceDebugSettings.SectionName));
        builder.Services.Configure<OtlpCollectorEndpointSettings>(
            builder.Configuration.GetSection(OtlpCollectorEndpointSettings.SectionName));

        var otelSettings = builder.Configuration
            .GetSection(OpenTelemetrySettings.SectionName)
            .Get<OpenTelemetrySettings>() ?? new OpenTelemetrySettings();
        var devModeEnabled = builder.Configuration.GetValue<bool>("DevMode:Enabled");

        if (otelSettings.Enabled)
        {
            builder.Logging.AddFilter<OpenTelemetryLoggerProvider>((category, _) =>
                EmbeddedCollectorLogCategoryFilter.ShouldCapture(category));

            var protocol = otelSettings.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
            var exporterEndpoint = ResolveOtlpExporterEndpoint(otelSettings, collectorEndpointSettings, protocol);

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
                        tracing.AddAspNetCoreInstrumentation(options =>
                        {
                            options.Filter = httpContext => !IsTelemetryRequest(httpContext.Request, collectorEndpointSettings);
                        });
                    }

                    tracing
                        .AddHttpClientInstrumentation(options =>
                        {
                            options.FilterHttpRequestMessage = request => !IsCollectorRequestUri(request.RequestUri, collectorEndpointSettings);
                        })
                        .AddOtlpExporter(o =>
                        {
                            o.Endpoint = exporterEndpoint;
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
                            o.Endpoint = exporterEndpoint;
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
                logging.AddOtlpExporter((o, processor) =>
                {
                    o.Endpoint = exporterEndpoint;
                    o.Protocol = protocol;
                    if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                        o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";

                    if (collectorEndpointSettings.Enabled)
                    {
                        processor.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 200;
                        processor.BatchExportProcessorOptions.MaxQueueSize = 256;
                        processor.BatchExportProcessorOptions.MaxExportBatchSize = 32;
                    }
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
        builder.Services.AddGnOuGoFilesServer(builder.Configuration);

        var agentDbRelativePath = builder.Configuration.GetValue<string>("Agent:DatabasePath")
            ?? AgentMcpHostingExtensions.DefaultDatabasePath;
        var agentDbPath = AgentMcpHostingExtensions.ResolveDatabasePath(agentDbRelativePath, applicationBasePath);
        var keyVaultDbRelativePath = builder.Configuration.GetValue<string>("KeyVault:DatabasePath")
            ?? "data/gnougo-keyvault.db";
        var keyVaultDbPath = KeyVaultDatabasePathResolver.Resolve(keyVaultDbRelativePath, applicationBasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(agentDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(keyVaultDbPath)!);

        // --- services ---
        // LLMRuntimeOptionsStore: holds the live LLMOptions hydrated from appsettings + KeyVault.
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(TraceDebugService.HttpClientName);
        builder.Services.AddSingleton<LocalTraceDebugStore>();
        builder.Services.AddSingleton<LLMRuntimeOptionsStore>(sp =>
        {
            var initialOptions = sp.GetRequiredService<IOptions<LLMOptions>>();
            var logger = sp.GetRequiredService<ILogger<LLMRuntimeOptionsStore>>();
            return new LLMRuntimeOptionsStore(initialOptions, logger);
        });
        builder.Services.AddSingleton<AgentUserConfigMcpClient>();
        builder.Services.AddAgentMcpPersistence(agentDbPath);
        builder.Services.AddKeyVaultMcpPersistence(keyVaultDbPath);
        builder.Services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();
        builder.Services.AddSingleton<SecureWorkflowRuntimeFactory>();
        builder.Services.AddSingleton<CollectorTracePersistence>();
        builder.Services.AddSingleton<ILoggerProvider, CollectorLoggerProvider>();


        builder.Services.AddSingleton<ILLMClient>(sp =>
        {
            var store = sp.GetRequiredService<LLMRuntimeOptionsStore>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sslLogger = loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
            var dangerousCert = store.Current.DangerousAcceptAnyServerCertificate;
            var http = LLMHttpClientFactory.Create(dangerousCert, LLMHttpClientDefaults.MinimumTimeout, sslLogger);
            // DynamicRoutingLLMClientAdapter reads the LATEST options from the store on every call,
            // so a /llm wizard update takes effect for the very next message.
            return new DynamicRoutingLLMClientAdapter(http, store, loggerFactory);
        });
        builder.Services.AddSingleton<ILLMModelCatalog>(sp =>
        {
            var store = sp.GetRequiredService<LLMRuntimeOptionsStore>();
            var cache = sp.GetRequiredService<IMemoryCache>();
            var settings = sp.GetRequiredService<IOptions<ModelCatalogCacheSettings>>().Value;
            var logger = sp.GetRequiredService<ILogger<CachedLlmModelCatalog>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sslLogger = loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
            var dangerousCert = store.Current.DangerousAcceptAnyServerCertificate;
            var http = LLMHttpClientFactory.Create(dangerousCert, TimeSpan.FromMinutes(2), sslLogger);
            var innerCatalog = new DynamicRoutingLLMModelCatalogAdapter(http, store, loggerFactory);
            return new CachedLlmModelCatalog(innerCatalog, store, cache, settings, logger);
        });
        builder.Services.AddSingleton<IMcpClientFactory>(sp =>
        {
            var runtimeOptions = sp.GetRequiredService<LLMRuntimeOptionsStore>().Current;
            if (runtimeOptions.McpServers.Count > 0)
                return new ConfiguredMcpClientFactory(runtimeOptions.McpServers);
            return new InMemoryMcpClientFactory();
        });
        builder.Services.AddSingleton<AgentHumanInputProvider>();
        builder.Services.AddSingleton<AgentOTelTelemetry>();
        builder.Services.AddSingleton<ConfigureProvidersService>();
        builder.Services.AddSingleton<ConfigureAgentsService>();
        builder.Services.AddSingleton<SmartFlowService>();
        builder.Services.AddSingleton<TraceDebugService>();

#if !GNOU_GO_AGENT_SERVER_NO_RAZOR_COMPONENTS
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
#endif

        builder.Services.AddSingleton<WordChunker>();
        var capturedArgs = args;
        builder.Services.AddSingleton<MountedMcpHostsHolder>(sp => new MountedMcpHostsHolder(capturedArgs, sp, startInBackground: isDesktopHosted));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MountedMcpHostsHolder>());

        var app = builder.Build();

        app.Services.InitializeAgentMcpAsync().GetAwaiter().GetResult();
        app.Services.InitializeGnOuGoFilesServerAsync().GetAwaiter().GetResult();

        app.Services.InitializeKeyVaultMcpAsync().GetAwaiter().GetResult();

        HydrateRuntimeOptionsFromKeyVaultAsync(app.Services).GetAwaiter().GetResult();

        app.Services.InitializeOtlpCollectorAsync().GetAwaiter().GetResult();

        // Mounted MCP sub-hosts are started/stopped by the MountedMcpHostsHolder
        // IHostedService — no direct startup here.
        var mountedMcpHostsHolder = app.Services.GetRequiredService<MountedMcpHostsHolder>();
        app.Lifetime.ApplicationStarted.Register(() => _ = InitializeMountedAgentServicesAsync(app));

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

#if !GNOU_GO_AGENT_SERVER_NO_RAZOR_COMPONENTS
        app.UseAntiforgery();
#endif

        // --- API ---
        app.MapPost("/api/chat", ChatEndpoints.CompleteAsync);
        app.MapPost("/api/chat/stream", ChatEndpoints.StreamAsync);
        app.MapGnOuGoFilesServer(includeHealthEndpoint: false);
        app.MapGet("/api/llm/providers", LlmProviderEndpoints.ListProviders);
        app.MapGet("/api/llm/providers/{provider}/models", LlmProviderEndpoints.ListModelsAsync);

        if (collectorEndpointSettings.Enabled)
        {
            var telemetryGrpc = app.MapGroup(string.Empty);
            telemetryGrpc.RequireHost(collectorEndpointSettings.BuildRequireHostPattern(collectorEndpointSettings.GrpcPort));
            telemetryGrpc.MapOtlpGrpcReceivers();

            var telemetryHttp = app.MapGroup(string.Empty);
            telemetryHttp.RequireHost(collectorEndpointSettings.BuildRequireHostPattern(collectorEndpointSettings.HttpPort));
            telemetryHttp.MapOtlpHttpReceiver(includeHealthEndpoint: collectorEndpointSettings.ExposeHealthEndpoint);
            telemetryHttp.MapTenantApi();
        }

        MapMountedMcpEndpoints(app, mountedMcpHostsHolder);
        app.MapGet("/health", () => Results.Text("{\"status\":\"ok\"}", "application/json"));
#if GNOU_GO_AGENT_SERVER_NO_RAZOR_COMPONENTS
        app.MapGet("/", () => Results.Redirect("/ui/"));
        app.MapGet("/Error", () => Results.Text("An unhandled server error occurred.", "text/plain", statusCode: StatusCodes.Status500InternalServerError));
#endif
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

#if !GNOU_GO_AGENT_SERVER_NO_RAZOR_COMPONENTS
        // --- UI ---
        // Always register interactive server render mode.
        // In published Desktop/NativeAOT builds the static web assets manifest
        // may be absent; MapStaticAssets() is only called when the manifest exists,
        // but interactive SSR is always available.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
#endif

        return app;
    }

    public static PublishedAgentEndpoints ResolvePublishedEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;

        if (addresses is null || addresses.Count == 0)
            return new PublishedAgentEndpoints(null, null, null);

        var collectorSettings = app.Services
            .GetRequiredService<IOptions<OtlpCollectorEndpointSettings>>()
            .Value;

        var parsedAddresses = addresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
            .Where(static uri => uri is not null)
            .Cast<Uri>()
            .Select(NormalizePublishedAddress)
            .ToList();

        var appBaseAddress = SelectPreferredHttpAddress(parsedAddresses, uri => !IsCollectorPort(uri.Port, collectorSettings));
        var telemetryGrpcBaseAddress = collectorSettings.Enabled
            ? SelectPreferredHttpAddress(parsedAddresses, uri => uri.Port == collectorSettings.GrpcPort)
            : null;
        var telemetryHttpBaseAddress = collectorSettings.Enabled
            ? SelectPreferredHttpAddress(parsedAddresses, uri => uri.Port == collectorSettings.HttpPort)
            : null;

        return new PublishedAgentEndpoints(appBaseAddress, telemetryGrpcBaseAddress, telemetryHttpBaseAddress);
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
        Dictionary<string, McpServerOptions> servers,
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
        Dictionary<string, McpServerOptions> servers,
        string applicationBasePath)
    {
        var solutionRoot = FindSolutionRoot(applicationBasePath)
            ?? FindSolutionRoot(AppContext.BaseDirectory);
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
    /// Resolves relative stdio MCP command paths from the current application base directory.
    /// This allows published desktop builds to ship bundled tools under ./tools and launch
    /// them correctly regardless of the process working directory.
    /// </summary>
    private static void ResolveRelativeMcpCommandPaths(
        Dictionary<string, McpServerOptions> servers,
        string applicationBasePath)
    {
        foreach (var cfg in servers.Values)
        {
            if (!string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(cfg.Command)) continue;
            if (Path.IsPathRooted(cfg.Command)) continue;

            var normalizedCommand = cfg.Command.Replace('/', Path.DirectorySeparatorChar)
                                               .Replace('\\', Path.DirectorySeparatorChar);
            var resolvedCommand = Path.GetFullPath(Path.Combine(applicationBasePath, normalizedCommand));

            if (File.Exists(resolvedCommand))
            {
                cfg.Command = resolvedCommand;
                continue;
            }

            var appContextCommand = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalizedCommand));
            if (File.Exists(appContextCommand))
            {
                cfg.Command = appContextCommand;
                continue;
            }

            if (!OperatingSystem.IsWindows() || !string.IsNullOrWhiteSpace(Path.GetExtension(resolvedCommand)))
                continue;

            var windowsExecutable = resolvedCommand + ".exe";
            if (File.Exists(windowsExecutable))
            {
                cfg.Command = windowsExecutable;
                continue;
            }

            var appContextWindowsExecutable = appContextCommand + ".exe";
            if (File.Exists(appContextWindowsExecutable))
                cfg.Command = appContextWindowsExecutable;
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

    private static void ConfigureMountedMcpServers(WebApplication app)
    {
        var publishedEndpoints = ResolvePublishedEndpoints(app);
        if (string.IsNullOrWhiteSpace(publishedEndpoints.AppBaseAddress))
            return;

        foreach (var registration in MountedMcpRegistrations)
            TryConfigureMountedMcpServer(app, registration, publishedEndpoints.AppBaseAddress);
    }

    private static async Task InitializeMountedAgentServicesAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ConfigureMountedMcpServers(app);
        await InitializeMountedAgentServicesFromServicesAsync(app.Services);
    }

    private static async Task InitializeMountedAgentServicesFromServicesAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            await HydrateRuntimeOptionsFromKeyVaultAsync(services);

            using var scope = services.CreateScope();
            var userConfigs = scope.ServiceProvider.GetRequiredService<IUserConfigRepository>();
            var runtimeOptions = scope.ServiceProvider.GetRequiredService<LLMRuntimeOptionsStore>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GnOuGo.Agent.Server.UserConfigBootstrap");

            var snapshot = await userConfigs.GetAsync(ct: CancellationToken.None);
            foreach (var kv in NormalizeModelOverrides(snapshot.ModelOverrides))
                runtimeOptions.UpsertModelOverride(kv.Key, kv.Value);

            if (!string.IsNullOrWhiteSpace(snapshot.DefaultLlmProvider))
            {
                if (!runtimeOptions.SetDefaultProvider(snapshot.DefaultLlmProvider, snapshot.DefaultLlmModel))
                {
                    logger.LogWarning(
                        "Persisted default LLM provider '{Provider}' could not be applied because it is not configured in runtime options.",
                        snapshot.DefaultLlmProvider);
                }
            }
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GnOuGo.Agent.Server.UserConfigBootstrap");
            logger.LogWarning(ex, "Could not hydrate persisted user defaults from Agent MCP.");
        }
    }

    private static async Task HydrateRuntimeOptionsFromKeyVaultAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            using var scope = services.CreateScope();
            var keyVaultStore = scope.ServiceProvider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
            var runtimeOptions = scope.ServiceProvider.GetRequiredService<LLMRuntimeOptionsStore>();

            var effectiveOptions = await keyVaultStore.BuildEffectiveOptionsAsync(runtimeOptions.Current, ct);
            runtimeOptions.ReplaceRuntimeOptions(effectiveOptions);
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GnOuGo.Agent.Server.KeyVaultConfigBootstrap");
            logger.LogWarning(ex, "Could not hydrate runtime LLM and MCP settings from KeyVault.");
        }
    }

    private static IReadOnlyDictionary<string, LLMModelMetadata> NormalizeModelOverrides(
        IReadOnlyDictionary<string, LLMModelMetadata>? modelOverrides)
        => modelOverrides is null
            ? new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LLMModelMetadata>(modelOverrides, StringComparer.OrdinalIgnoreCase);

    private static void MapMountedMcpEndpoints(WebApplication app, MountedMcpHostsHolder holder)
    {
        MapMountedMcpProxy(app, AgentMcpRegistration.RoutePrefix, () => holder.AgentBaseAddress);
        MapMountedMcpProxy(app, KeyVaultMcpRegistration.RoutePrefix, () => holder.KeyVaultBaseAddress);
        MapMountedMcpProxy(app, DocsIngestorMcpRegistration.RoutePrefix, () => holder.DocsIngestorBaseAddress);
    }

    private static void MapMountedMcpProxy(WebApplication app, string routePrefix, Func<Uri?> resolveTarget)
    {
        app.Map(routePrefix, context =>
            {
                var target = resolveTarget();
                return target is null
                    ? Task.FromResult(Results.StatusCode(503))
                    : ProxyMountedMcpRequestAsync(context, target, path: null);
            })
            .DisableAntiforgery();

        app.Map($"{routePrefix}/{{**path}}", (HttpContext context, string path) =>
            {
                var target = resolveTarget();
                return target is null
                    ? Task.FromResult(Results.StatusCode(503))
                    : ProxyMountedMcpRequestAsync(context, target, path);
            })
            .DisableAntiforgery();
    }

    private static async Task ProxyMountedMcpRequestAsync(HttpContext context, Uri targetBaseAddress, string? path)
    {
        var relativePath = string.IsNullOrWhiteSpace(path) ? string.Empty : "/" + path.TrimStart('/');
        var targetUri = new Uri($"{targetBaseAddress.ToString().TrimEnd('/')}{relativePath}{context.Request.QueryString}");

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        var hasBody = context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (hasBody)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
        }

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipProxyRequestHeader(header.Key, hasBody))
            {
                continue;
            }

            if (!(request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) ?? false))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        using var response = await MountedMcpProxyHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = RewriteProxyResponseHeaderValues(
                context,
                targetBaseAddress,
                header.Key,
                header.Value).ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            context.Response.Headers[header.Key] = RewriteProxyResponseHeaderValues(
                context,
                targetBaseAddress,
                header.Key,
                header.Value).ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool ShouldSkipProxyRequestHeader(string headerName, bool hasBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);

        if (headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!hasBody)
            return false;

        return headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> RewriteProxyResponseHeaderValues(
        HttpContext context,
        Uri targetBaseAddress,
        string headerName,
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targetBaseAddress);
        ArgumentNullException.ThrowIfNull(headerName);
        ArgumentNullException.ThrowIfNull(values);

        if (!headerName.Equals("Location", StringComparison.OrdinalIgnoreCase)
            && !headerName.Equals("Content-Location", StringComparison.OrdinalIgnoreCase))
        {
            return values;
        }

        var publicOrigin = new Uri($"{context.Request.Scheme}://{context.Request.Host}");
        var upstreamOrigin = new Uri($"{targetBaseAddress.Scheme}://{targetBaseAddress.Authority}");

        return values.Select(value => RewriteProxyResponseHeaderValue(value, upstreamOrigin, publicOrigin));
    }

    private static string RewriteProxyResponseHeaderValue(string value, Uri upstreamOrigin, Uri publicOrigin)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            || !Uri.Compare(absolute, upstreamOrigin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase).Equals(0))
        {
            return value;
        }

        var builder = new UriBuilder(absolute)
        {
            Scheme = publicOrigin.Scheme,
            Host = publicOrigin.Host,
            Port = publicOrigin.IsDefaultPort ? -1 : publicOrigin.Port
        };

        return builder.Uri.ToString();
    }

    private static MountedMcpHosts StartMountedMcpHosts(string[] args)
    {
        // Strip OtlpCollector / OpenTelemetry args that only the main host needs.
        // Sub-hosts do not configure collector endpoints and must not inherit them,
        // otherwise CreateSlimBuilder(args) may bleed config that causes port
        // collisions or unwanted listener bindings.
        var subHostArgs = FilterArgsForSubHosts(args);

        var agentApp = AgentMcpWebHost.Build(subHostArgs, urls: "http://127.0.0.1:0", routePrefix: AgentMcpHostingExtensions.DefaultRoutePrefix);
        agentApp.StartAsync().GetAwaiter().GetResult();

        var keyVaultApp = KeyVaultMcpWebHost.Build(subHostArgs, urls: "http://127.0.0.1:0", routePrefix: KeyVaultMcpHostingExtensions.DefaultRoutePrefix);
        keyVaultApp.StartAsync().GetAwaiter().GetResult();

        var docsIngestorApp = DocsIngestorMcpWebHost.Build(subHostArgs, urls: "http://127.0.0.1:0", routePrefix: DocsIngestorMcpHostingExtensions.DefaultRoutePrefix);
        docsIngestorApp.StartAsync().GetAwaiter().GetResult();

        return new MountedMcpHosts(
            agentApp,
            ResolveSingleListeningBaseAddress(agentApp, AgentMcpHostingExtensions.DefaultRoutePrefix),
            keyVaultApp,
            ResolveSingleListeningBaseAddress(keyVaultApp, KeyVaultMcpHostingExtensions.DefaultRoutePrefix),
            docsIngestorApp,
            ResolveSingleListeningBaseAddress(docsIngestorApp, DocsIngestorMcpHostingExtensions.DefaultRoutePrefix));
    }

    private static async Task<MountedMcpHosts> StartMountedMcpHostsAsync(string[] args, CancellationToken cancellationToken)
    {
        var subHostArgs = FilterArgsForSubHosts(args);

        var agentApp = AgentMcpWebHost.Build(subHostArgs, urls: "http://127.0.0.1:0", routePrefix: AgentMcpHostingExtensions.DefaultRoutePrefix);
        await agentApp.StartAsync(cancellationToken);

        try
        {
            var keyVaultApp = KeyVaultMcpWebHost.Build(subHostArgs, urls: "http://127.0.0.1:0", routePrefix: KeyVaultMcpHostingExtensions.DefaultRoutePrefix);
            await keyVaultApp.StartAsync(cancellationToken);

            try
            {
                var docsIngestorApp = DocsIngestorMcpWebHost.Build(subHostArgs, urls: "http://127.0.0.1:0", routePrefix: DocsIngestorMcpHostingExtensions.DefaultRoutePrefix);
                await docsIngestorApp.StartAsync(cancellationToken);

                return new MountedMcpHosts(
                    agentApp,
                    ResolveSingleListeningBaseAddress(agentApp, AgentMcpHostingExtensions.DefaultRoutePrefix),
                    keyVaultApp,
                    ResolveSingleListeningBaseAddress(keyVaultApp, KeyVaultMcpHostingExtensions.DefaultRoutePrefix),
                    docsIngestorApp,
                    ResolveSingleListeningBaseAddress(docsIngestorApp, DocsIngestorMcpHostingExtensions.DefaultRoutePrefix));
            }
            catch
            {
                await keyVaultApp.StopAsync(cancellationToken);
                await keyVaultApp.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await agentApp.StopAsync(cancellationToken);
            await agentApp.DisposeAsync();
            throw;
        }
    }

    private static async Task StopMountedMcpHostsAsync(MountedMcpHosts mountedMcpHosts)
    {
        await mountedMcpHosts.AgentApp.StopAsync();
        await mountedMcpHosts.KeyVaultApp.StopAsync();
        await mountedMcpHosts.DocsIngestorApp.StopAsync();
        await mountedMcpHosts.AgentApp.DisposeAsync();
        await mountedMcpHosts.KeyVaultApp.DisposeAsync();
        await mountedMcpHosts.DocsIngestorApp.DisposeAsync();
    }

    /// <summary>
    /// Returns a copy of <paramref name="args"/> with OtlpCollector / OpenTelemetry /
    /// Ingest / TraceDebug args removed and explicit <c>--OtlpCollector:Enabled=false</c>
    /// / <c>--OpenTelemetry:Enabled=false</c> appended.
    /// This prevents mounted MCP sub-hosts from inheriting collector configuration
    /// that only the main host should use.
    /// </summary>
    private static string[] FilterArgsForSubHosts(string[] args)
    {
        static bool IsMainHostOnlyArg(string arg) =>
            arg.StartsWith("--OtlpCollector:", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--OpenTelemetry:", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--Ingest:", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--TraceDebug:", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--Database:", StringComparison.OrdinalIgnoreCase);

        var filtered = args.Where(a => !IsMainHostOnlyArg(a)).ToList();
        filtered.Add("--OtlpCollector:Enabled=false");
        filtered.Add("--OpenTelemetry:Enabled=false");
        return filtered.ToArray();
    }

    private static Uri ResolveSingleListeningBaseAddress(WebApplication app, string routePrefix)
    {
        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses
            .FirstOrDefault();

        if (!Uri.TryCreate(address, UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException($"Could not resolve a listening address for mounted MCP host '{routePrefix}'.");
        }

        return new Uri($"{baseAddress.ToString().TrimEnd('/')}{routePrefix}");
    }

    private static void TryConfigureMountedMcpServer(
        WebApplication app,
        MountedMcpRegistration registration,
        string appBaseAddress)
        => TryConfigureMountedMcpServerFromServices(app.Services, registration, appBaseAddress);

    private static void TryConfigureMountedMcpServerFromServices(
        IServiceProvider services,
        MountedMcpRegistration registration,
        string appBaseAddress)
    {
        try
        {
            var baseAddress = $"{appBaseAddress.TrimEnd('/')}{registration.RoutePrefix}";

            if (string.IsNullOrWhiteSpace(baseAddress))
                return;

            var optionsStore = services.GetRequiredService<LLMRuntimeOptionsStore>();
            var current = optionsStore.Current;
            current.McpServers.TryGetValue(registration.ServerName, out var existing);

            optionsStore.UpsertTransientMcpServer(
                registration.ServerName,
                new McpServerOptions
                {
                    Type = "http",
                    Description = existing?.Description ?? registration.DefaultDescription,
                    Url = baseAddress,
                    ApiKey = existing?.ApiKey,
                    Issuer = existing?.Issuer,
                    ClientId = existing?.ClientId,
                    ClientSecret = existing?.ClientSecret,
                    Scopes = existing?.Scopes
                });
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(registration.LoggerName);
            logger.LogWarning(ex, "Could not repoint the mounted MCP endpoint '{ServerName}'.", registration.ServerName);
        }
    }

    private sealed record MountedMcpRegistration(
        string ServerName,
        string RoutePrefix,
        string DefaultDescription,
        string LoggerName);

    private sealed record MountedMcpHosts(
        WebApplication AgentApp,
        Uri AgentBaseAddress,
        WebApplication KeyVaultApp,
        Uri KeyVaultBaseAddress,
        WebApplication DocsIngestorApp,
        Uri DocsIngestorBaseAddress);

    /// <summary>
    /// Manages the lifecycle of mounted MCP sub-hosts (Agent, KeyVault, DocsIngestor).
    /// Registered as <see cref="IHostedService"/> so sub-hosts only start when the main app
    /// starts and are stopped when the main app stops — preventing resource leaks in tests
    /// that call <c>Build()</c> without <c>StartAsync()</c>.
    /// </summary>
    private sealed class MountedMcpHostsHolder : IHostedService
    {
        private readonly string[] _args;
        private readonly IServiceProvider _services;
        private readonly bool _startInBackground;
        private MountedMcpHosts? _hosts;
        private Task? _startupTask;
        private CancellationTokenSource? _startupCancellation;

        public MountedMcpHostsHolder(string[] args, IServiceProvider services, bool startInBackground)
        {
            _args = args;
            _services = services;
            _startInBackground = startInBackground;
        }

        public MountedMcpHosts? Hosts => _hosts;
        public Uri? AgentBaseAddress => _hosts?.AgentBaseAddress;
        public Uri? KeyVaultBaseAddress => _hosts?.KeyVaultBaseAddress;
        public Uri? DocsIngestorBaseAddress => _hosts?.DocsIngestorBaseAddress;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_startInBackground)
            {
                _startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _startupTask = StartMountedHostsInBackgroundAsync(_startupCancellation.Token);
                return Task.CompletedTask;
            }

            _hosts = StartMountedMcpHosts(_args);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var logger = _services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GnOuGo.Agent.Server.MountedMcpHostsHolder");

            if (_startupCancellation is not null)
            {
                try
                {
                    await _startupCancellation.CancelAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to cancel mounted MCP host startup.");
                    // ignore
                }
            }

            if (_startupTask is not null)
            {
                try
                {
                    await _startupTask;
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogDebug(ex, "Mounted MCP host startup task was cancelled during shutdown.");
                    // ignore
                }
            }

            if (_hosts is not null)
            {
                await StopMountedMcpHostsAsync(_hosts);
                _hosts = null;
            }
        }

        private async Task StartMountedHostsInBackgroundAsync(CancellationToken cancellationToken)
        {
            try
            {
                _hosts = await StartMountedMcpHostsAsync(_args, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var logger = _services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GnOuGo.Agent.Server.MountedMcpHostsHolder");
                logger.LogWarning(ex, "Mounted MCP sub-host startup failed.");
            }
        }
    }


    public sealed record PublishedAgentEndpoints(
        string? AppBaseAddress,
        string? TelemetryGrpcBaseAddress,
        string? TelemetryHttpBaseAddress);

    private static Uri ResolveOtlpExporterEndpoint(
        OpenTelemetrySettings otelSettings,
        OtlpCollectorEndpointSettings collectorEndpointSettings,
        OtlpExportProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(otelSettings);
        ArgumentNullException.ThrowIfNull(collectorEndpointSettings);

        if (collectorEndpointSettings.Enabled)
        {
            return protocol == OtlpExportProtocol.HttpProtobuf
                ? collectorEndpointSettings.GetHttpEndpoint()
                : collectorEndpointSettings.GetGrpcEndpoint();
        }

        return new Uri(otelSettings.OtlpEndpoint);
    }

    private static bool IsTelemetryRequest(HttpRequest request, OtlpCollectorEndpointSettings collectorEndpointSettings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(collectorEndpointSettings);

        return collectorEndpointSettings.Enabled
            && IsCollectorPort(request.Host.Port, collectorEndpointSettings);
    }

    private static bool IsCollectorRequestUri(Uri? requestUri, OtlpCollectorEndpointSettings collectorEndpointSettings)
    {
        if (requestUri is null || !collectorEndpointSettings.Enabled)
            return false;

        if (!IsCollectorPort(requestUri.Port, collectorEndpointSettings))
            return false;

        var requestHost = NormalizePublishedHost(requestUri.Host);
        var clientHost = NormalizePublishedHost(collectorEndpointSettings.GetClientHost());
        return requestHost.Equals(clientHost, StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(requestHost, out var requestIp)
                && IPAddress.TryParse(clientHost, out var clientIp)
                && Equals(requestIp, clientIp));
    }

    private static Uri NormalizePublishedAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalizedHost = NormalizePublishedHost(address.Host);
        if (string.Equals(normalizedHost, address.Host, StringComparison.OrdinalIgnoreCase))
            return address;

        var builder = new UriBuilder(address)
        {
            Host = normalizedHost
        };

        return builder.Uri;
    }

    private static string NormalizePublishedHost(string? host)
    {
        var normalizedHost = (host ?? string.Empty).Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return normalizedHost;

        if (normalizedHost is "0.0.0.0" or "*" or "+" or "::" or "::0")
            return "127.0.0.1";

        if (IPAddress.TryParse(normalizedHost, out var ipAddress)
            && (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress)))
        {
            return "127.0.0.1";
        }

        return normalizedHost;
    }

    private static bool IsCollectorPort(int? port, OtlpCollectorEndpointSettings collectorEndpointSettings)
        => port.HasValue
           && collectorEndpointSettings.Enabled
           && (port.Value == collectorEndpointSettings.GrpcPort || port.Value == collectorEndpointSettings.HttpPort);

    private static void ConfigurePrimaryAndCollectorListeners(
        KestrelServerOptions options,
        string? primaryUrls,
        OtlpCollectorEndpointSettings collectorEndpointSettings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(collectorEndpointSettings);

        foreach (var primaryUrl in SplitUrls(primaryUrls))
            ConfigurePrimaryListener(options, primaryUrl);

        options.ConfigureListener(collectorEndpointSettings.Host, collectorEndpointSettings.GrpcPort, HttpProtocols.Http2);
        options.ConfigureListener(collectorEndpointSettings.Host, collectorEndpointSettings.HttpPort, HttpProtocols.Http1AndHttp2);
    }

    private static void ConfigurePrimaryListener(KestrelServerOptions options, string url)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedUrl = url
            .Trim()
            .Replace("://*", "://0.0.0.0", StringComparison.Ordinal)
            .Replace("://+", "://0.0.0.0", StringComparison.Ordinal);

        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Could not parse primary server URL '{url}'.");

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        options.ConfigureListener(uri.Host, uri.Port, HttpProtocols.Http1AndHttp2, listen =>
        {
            if (isHttps)
                listen.UseHttps();
        });
    }

    private static IEnumerable<string> SplitUrls(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            yield return "http://localhost:5000";
            yield break;
        }

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return url;
    }

    private static string? SelectPreferredHttpAddress(IEnumerable<Uri> addresses, Func<Uri, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(predicate);

        foreach (var address in addresses)
        {
            if (!string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!predicate(address))
                continue;

            if (address.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                return address.ToString().TrimEnd('/');
        }

        foreach (var address in addresses)
        {
            if (!string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                continue;

            if (predicate(address))
                return address.ToString().TrimEnd('/');
        }

        foreach (var address in addresses)
        {
            if (predicate(address))
                return address.ToString().TrimEnd('/');
        }

        return null;
    }

    private sealed class DelegateLogger(string categoryName, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            if (exception is not null)
                message = string.IsNullOrWhiteSpace(message) ? exception.ToString() : $"{message} {exception}";
            write($"[{logLevel}] {categoryName}: {message}");
        }
    }
}
