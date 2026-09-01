using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Components;
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
using GnOuGo.AI.Local;
using GnOuGo.DocIngestor.Mcp;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Files.Server;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Mcp;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Mcp.Core;
using GnOuGo.Workspace;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using OtlpTenantCollector.Hosting;
using OtlpTenantCollector.Web;

namespace GnOuGo.Agent.Server.Hosting;

public static class GnOuGoAgentWebHost
{
    public static WebApplication Build(
        string[] args,
        string? urls = null,
        string? contentRoot = null,
        bool enableHttpsRedirection = true,
        Action<IServiceCollection>? configureServices = null)
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
        if (!llmOptions.Models.Values.Any(static provider =>
                string.Equals(provider.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase)))
        {
            llmOptions.Models["Local"] = new ModelProviderOptions
            {
                Type = LocalLLMProvider.Type,
                Url = "embedded://llamasharp"
            };
        }
        llmOptions.ModelOverrides.TryAdd(
            LocalModelCatalog.Qwen3Id,
            LocalModelCatalog.CreateMetadata(LocalModelCatalog.Qwen3));
        LLMOptionsValidation.ValidateAndThrow(llmOptions);

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
        builder.Services.Configure<WorkflowTraceExportSettings>(
            builder.Configuration.GetSection(WorkflowTraceExportSettings.SectionName));
        builder.Services.Configure<OtlpCollectorEndpointSettings>(
            builder.Configuration.GetSection(OtlpCollectorEndpointSettings.SectionName));

        var otelSettings = builder.Configuration
            .GetSection(OpenTelemetrySettings.SectionName)
            .Get<OpenTelemetrySettings>() ?? new OpenTelemetrySettings();
        var devModeEnabled = bool.TryParse(builder.Configuration["DevMode:Enabled"], out var configuredDevMode)
            && configuredDevMode;

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
                        .AddSource(AgentOTelTelemetry.ActivitySourceName)
                        .AddSource("GnOuGo.AI.Core.Routing")
                        .AddSource("GnOuGo.AI.Local.Models")
                        .AddSource("GnOuGo.AI.Local.Inference");

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
                        .AddMeter("GnOuGo.AI.Core.Routing")
                        .AddMeter("GnOuGo.AI.Local.Models")
                        .AddMeter("GnOuGo.AI.Local.Inference")
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
        builder.Services.Configure<McpCapabilityCacheSettings>(
            builder.Configuration.GetSection(McpCapabilityCacheSettings.SectionName));
        builder.Services.Configure<WorkflowMermaidMarkdownOptions>(
            builder.Configuration.GetSection(WorkflowMermaidMarkdownOptions.SectionName));
        builder.Services.Configure<BundledMcpSettings>(
            builder.Configuration.GetSection(BundledMcpSettings.SectionName));
        builder.Services.Configure<KeyVaultSettings>(
            builder.Configuration.GetSection(KeyVaultSettings.SectionName));
        builder.Services.Configure<LocalLLMOptions>(
            builder.Configuration.GetSection(LocalLLMOptions.SectionName));
        builder.Services.AddOtlpCollectorCore(builder.Configuration);
        builder.Services.AddGnOuGoFilesServer(builder.Configuration);

        var agentDbRelativePath = builder.Configuration["Agent:DatabasePath"]
            ?? AgentMcpHostingExtensions.DefaultDatabasePath;
        var agentDbPath = AgentMcpHostingExtensions.ResolveDatabasePath(agentDbRelativePath, applicationBasePath);
        var keyVaultDbRelativePath = builder.Configuration["KeyVault:DatabasePath"]
            ?? ".GnOuGo/data/gnougo-keyvault.db";
        var keyVaultDbPath = KeyVaultDatabasePathResolver.Resolve(keyVaultDbRelativePath, applicationBasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(agentDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(keyVaultDbPath)!);

        // --- services ---
        // LLMRuntimeOptionsStore: holds the live LLMOptions hydrated from appsettings + KeyVault.
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(TraceDebugService.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        builder.Services.AddHttpClient("GnOuGo.AI.Local.Models", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        var localModelsDirectory = GnOuGoWorkspace.ResolveLocalModelsDirectory(applicationBasePath);
        builder.Services.AddSingleton<ILocalLLMRuntime>(sp => new LlamaSharpLocalLLMRuntime(
            localModelsDirectory,
            sp.GetRequiredService<IOptions<LocalLLMOptions>>(),
            sp.GetRequiredService<ILogger<LlamaSharpLocalLLMRuntime>>()));
        builder.Services.AddSingleton<ILocalModelManager>(sp => new LocalModelManager(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("GnOuGo.AI.Local.Models"),
            localModelsDirectory,
            ((LlamaSharpLocalLLMRuntime)sp.GetRequiredService<ILocalLLMRuntime>()).UnloadAsync,
            sp.GetRequiredService<ILogger<LocalModelManager>>()));
        builder.Services.AddSingleton<LocalLLMModelCatalogProvider>();
        builder.Services.AddSingleton<AppVersionInfo>();
        builder.Services.AddSingleton<LocalTraceDebugStore>();
        builder.Services.AddSingleton<IWorkflowTraceFileExporter, WorkflowTraceFileExporter>();
        builder.Services.AddSingleton<LLMRuntimeOptionsStore>(sp =>
        {
            var initialOptions = sp.GetRequiredService<IOptions<LLMOptions>>();
            var logger = sp.GetRequiredService<ILogger<LLMRuntimeOptionsStore>>();
            return new LLMRuntimeOptionsStore(initialOptions, logger);
        });
        builder.Services.AddSingleton<AgentUserConfigMcpClient>();
        builder.Services.AddAgentMcpPersistence(agentDbPath);
        builder.Services.AddKeyVaultMcpPersistence(keyVaultDbPath);
        builder.Services.AddDocsIngestorMcpServices(builder.Configuration, applicationBasePath);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Agent.Server.Mcp",
                    Version = "1.0.0"
                };
                options.AddGnOuGoToolErrorNormalizer();
            })
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
                options.ConfigureSessionOptions = MountedMcpEndpointCatalog.ConfigureSessionOptionsAsync;
            })
            .WithAgentMcpTools()
            .WithKeyVaultMcpTools()
            .WithDocsIngestorMcpTools();
        builder.Services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();
        // Do not let DI inject the singleton IMcpClientFactory here. That
        // factory is a startup snapshot, while /mcp add and /mcp edit update
        // the encrypted runtime configuration without restarting the host.
        // SecureWorkflowRuntimeFactory must build a fresh MCP factory from the
        // latest KeyVault-backed options for every workflow execution.
        builder.Services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new SecureWorkflowRuntimeFactory(
            sp.GetRequiredService<LLMRuntimeOptionsStore>(),
            sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(),
            sp.GetRequiredService<ILoggerFactory>(),
            llmClientOverride: null,
            mcpClientFactoryOverride: null,
            backgroundModeCache: sp.GetRequiredService<IMemoryCache>(),
            llmCapabilityResolver: sp.GetService<ILLMCapabilityResolver>(),
            humanInputProvider: sp.GetRequiredService<AgentHumanInputProvider>(),
            localRuntime: sp.GetRequiredService<ILocalLLMRuntime>()));
        builder.Services.AddSingleton<CollectorTracePersistence>();
        builder.Services.AddSingleton<ILoggerProvider, CollectorLoggerProvider>();


        builder.Services.AddSingleton<ILLMClient>(sp =>
        {
            var store = sp.GetRequiredService<LLMRuntimeOptionsStore>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var cache = sp.GetRequiredService<IMemoryCache>();
            var sslLogger = loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
            var dangerousCert = store.Current.DangerousAcceptAnyServerCertificate;
            var http = LLMHttpClientFactory.Create(dangerousCert, LLMHttpClientDefaults.MinimumTimeout, sslLogger);
            // DynamicRoutingLLMClientAdapter reads the LATEST options from the store on every call,
            // so a /llm wizard update takes effect for the very next message.
            return new DynamicRoutingLLMClientAdapter(
                http,
                store,
                loggerFactory,
                cache,
                sp.GetRequiredService<ILocalLLMRuntime>());
        });
        builder.Services.AddSingleton<CachedLlmModelCatalog>(sp =>
        {
            var store = sp.GetRequiredService<LLMRuntimeOptionsStore>();
            var cache = sp.GetRequiredService<IMemoryCache>();
            var settings = sp.GetRequiredService<IOptions<ModelCatalogCacheSettings>>().Value;
            var logger = sp.GetRequiredService<ILogger<CachedLlmModelCatalog>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sslLogger = loggerFactory.CreateLogger("GnOuGo.AI.Core.SSL");
            var dangerousCert = store.Current.DangerousAcceptAnyServerCertificate;
            var http = LLMHttpClientFactory.Create(dangerousCert, TimeSpan.FromMinutes(2), sslLogger);
            var innerCatalog = new DynamicRoutingLLMModelCatalogAdapter(
                http,
                store,
                loggerFactory,
                sp.GetRequiredService<LocalLLMModelCatalogProvider>());
            return new CachedLlmModelCatalog(innerCatalog, store, cache, settings, logger);
        });
        builder.Services.AddSingleton<ILLMModelCatalog>(sp => sp.GetRequiredService<CachedLlmModelCatalog>());
        builder.Services.AddSingleton<ILlmModelCatalogCacheInvalidator>(sp => sp.GetRequiredService<CachedLlmModelCatalog>());
        builder.Services.AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>();
        builder.Services.AddSingleton<IMcpClientFactory>(sp =>
        {
            var runtimeOptions = sp.GetRequiredService<LLMRuntimeOptionsStore>().Current;
            if (runtimeOptions.McpServers.Count > 0)
                return new ConfiguredMcpClientFactory(
                    runtimeOptions.McpServers,
                    sp.GetService<IHumanInputProvider>(),
                    runtimeOptions.DefaultProvider,
                    runtimeOptions.DefaultModel);
            return new InMemoryMcpClientFactory();
        });
        builder.Services.AddSingleton<AgentHumanInputProvider>();
        builder.Services.AddSingleton<AgentOTelTelemetry>();
        builder.Services.AddSingleton<IWorkflowCandidateProvider, DatabaseAgentWorkflowCandidateProvider>();
        builder.Services.AddSingleton<ConfigureProvidersService>();
        builder.Services.AddSingleton<LocalModelsService>();
        builder.Services.AddSingleton<ConfigureAgentsService>();
        builder.Services.AddSingleton<SmartFlowService>();
        builder.Services.AddSingleton<TraceDebugService>();

        AddAgentRazorComponents(builder.Services);

        builder.Services.AddSingleton<WordChunker>();

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();

        app.Services.InitializeAgentMcpAsync().GetAwaiter().GetResult();
        app.Services.InitializeGnOuGoFilesServerAsync().GetAwaiter().GetResult();

        app.Services.InitializeKeyVaultMcpAsync().GetAwaiter().GetResult();
        app.Services.InitializeDocsIngestorMcpAsync().GetAwaiter().GetResult();

        HydrateRuntimeOptionsFromKeyVaultAsync(app.Services).GetAwaiter().GetResult();
        ValidateLocalFallback(app.Services.GetRequiredService<LLMRuntimeOptionsStore>().Current);

        app.Services.InitializeOtlpCollectorAsync().GetAwaiter().GetResult();

        app.Lifetime.ApplicationStarted.Register(() => _ = InitializeMountedMcpServicesAsync(app));

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

        if (isDesktopHosted)
        {
            // In desktop mode, log unhandled exceptions to desktop.log so AOT / trim
            // failures are always visible — the default exception handler swallows them.
            app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    Log($"UnhandledException on {context.Request.Method} {context.Request.Path}: {ex}");
                    throw; // re-throw so the normal handler still runs
                }
            });
        }

        if (!app.Environment.IsDevelopment())
        {
            if (isDesktopHosted)
            {
                // In desktop mode, show the full developer exception page so AOT/trim
                // crashes are immediately visible in the embedded WebView.
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
            }
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
        app.MapGet("/api/chat/conversations", ChatEndpoints.ListConversations);
        app.MapGet("/api/chat/conversations/{conversationId}", ChatEndpoints.GetConversation);
        app.MapGnOuGoFilesServer(includeHealthEndpoint: false);
        app.MapGet("/api/version", (AppVersionInfo versionInfo) => versionInfo.ToDto());
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

        MountedMcpEndpointCatalog.MapEndpoints(app);
        app.MapGet("/health", () => Results.Text("{\"status\":\"ok\"}", "application/json"));
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
        // In published Desktop builds the static web assets manifest
        // may be absent; MapStaticAssets() is only called when the manifest exists,
        // but interactive SSR is always available.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Interactive Server Blazor is intentionally retained in partial-trim publishes and its health, static assets, and negotiate endpoint are verified from the published application.")]
    private static void AddAgentRazorComponents(IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents(options =>
            {
                options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(12);
                options.DisconnectedCircuitMaxRetained = 256;
            });
    }

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

    private static async Task InitializeMountedMcpServicesAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            var publishedEndpoints = ResolvePublishedEndpoints(app);
            if (string.IsNullOrWhiteSpace(publishedEndpoints.AppBaseAddress))
                return;

            foreach (var registration in MountedMcpEndpointCatalog.Registrations)
                TryConfigureMountedMcpServer(app, registration, publishedEndpoints.AppBaseAddress);

            await InitializeMountedAgentServicesFromServicesAsync(app.Services);
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Normal shutdown while post-start initialization is running.
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GnOuGo.Agent.Server.MountedMcpInitialization");
            logger.LogError(ex, "The directly mounted MCP endpoints could not be published to runtime configuration.");
        }
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

    private static void TryConfigureMountedMcpServer(
        WebApplication app,
        MountedMcpEndpointRegistration registration,
        string appBaseAddress)
        => TryConfigureMountedMcpServerFromServices(app.Services, registration, appBaseAddress);

    private static void TryConfigureMountedMcpServerFromServices(
        IServiceProvider services,
        MountedMcpEndpointRegistration registration,
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

    internal static void ValidateLocalFallback(LLMOptions options)
    {
        if (options.Fallback is null)
            return;
        if (string.IsNullOrWhiteSpace(options.Fallback.Provider)
            || string.IsNullOrWhiteSpace(options.Fallback.Model))
            throw new InvalidOperationException("LLM fallback configuration requires both Provider and Model.");

        var matchingProviderKeys = options.Models.Keys.Count(key =>
            string.Equals(key, options.Fallback.Provider, StringComparison.OrdinalIgnoreCase));
        if (matchingProviderKeys > 1)
            throw new InvalidOperationException("The configured LLM fallback provider is ambiguous.");

        var fallbackProvider = options.ResolveProvider(options.Fallback.Provider)
            ?? throw new InvalidOperationException("The configured LLM fallback provider does not exist.");
        if (string.IsNullOrWhiteSpace(fallbackProvider.Url))
            throw new InvalidOperationException("The configured LLM fallback provider has no endpoint.");
        if (string.Equals(fallbackProvider.ResolvedType, LocalLLMProvider.Type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured LLM fallback must use a non-local provider.");
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
