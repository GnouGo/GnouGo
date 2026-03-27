using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GnOuGo.Agent.Server.Endpoints;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Shared;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

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
        // LLM + MCP configuration (same structure as GnOuGo.Flow.Server)
        var llmOptions = builder.Configuration.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();

        // AOT-friendly JSON for Minimal APIs
        builder.Services.ConfigureHttpJsonOptions(static o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ChatJsonContext.Default);
        });

        // --- services ---
        builder.Services.AddSingleton<ILLMClient>(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var routingClient = new RoutingLLMClient(http, llmOptions);
            return new RoutingLLMClientAdapter(routingClient);
        });
        builder.Services.AddSingleton<IMcpClientFactory>(_ =>
        {
            if (llmOptions.McpServers.Count > 0)
                return new ConfiguredMcpClientFactory(llmOptions.McpServers);
            return new InMemoryMcpClientFactory();
        });
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<AgentHumanInputProvider>();
        builder.Services.AddSingleton<ConfigureProvidersService>();
        builder.Services.AddSingleton<SmartFlowService>();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddSingleton<WordChunker>();

        var app = builder.Build();

        if (isDesktopHosted)
        {
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? "<null>";
                var shouldTrace =
                    path == "/" ||
                    path == "/health" ||
                    path.StartsWith("/desktop/boot-log", StringComparison.OrdinalIgnoreCase) ||
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
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/desktop/boot-log/{token}", (string token, string? step, string? detail) =>
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                var safeStep = string.IsNullOrWhiteSpace(step) ? "<unknown>" : step.Trim();
                var safeDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
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
}
