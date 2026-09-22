using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Observability.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;
using GnOuGo.ProxyCopilot.Server.Protocols;
using GnOuGo.ProxyCopilot.Server.Traffic;

namespace GnOuGo.ProxyCopilot.Server;

public static class ProxyApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5087");
        configure?.Invoke(builder);
        var listenUrls = builder.Configuration["urls"] ?? "http://127.0.0.1:5087";
        if (listenUrls.Split(';').Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            || builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("ProxyCopilot requires loopback Urls; Kestrel endpoint overrides are not supported.");
        var options = new ProxyOptions();
        builder.Configuration.GetSection("ProxyCopilot").Bind(options, binding => binding.ErrorOnUnknownConfiguration = true);
        var registry = new ModelRegistry(options);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBytes);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, ProxyJsonContext.Default));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IModelRegistry>(registry);
        builder.Services.AddSingleton<CredentialRedactor>();
        builder.Services.AddSingleton<ITrafficStore, TrafficStore>();
        builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(30)
        }) { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton<IProxyAuthentication, ProxyAuthenticationService>();
        builder.Services.AddSingleton<IProxyAdapter>(new OpenAiAdapter());
        builder.Services.AddSingleton<IProxyAdapter>(new OpenAiAdapter("copilot"));
        builder.Services.AddSingleton<IProxyAdapter, AnthropicAdapter>();
        builder.Services.AddSingleton<IProxyAdapter, OllamaAdapter>();
        builder.Services.AddSingleton<ProxyRelay>();
        builder.AddGnOuGoOpenTelemetry("GnOuGo.ProxyCopilot.Server", telemetry =>
        {
            telemetry.TenantId = options.TenantId;
            telemetry.ActivitySources = [ProxyRelay.TelemetryName];
            telemetry.Meters = [ProxyRelay.TelemetryName];
            // Token acquisition and raw HTTP URLs must not enter generic HTTP telemetry.
            telemetry.IncludeHttpClientInstrumentation = false;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            try
            {
                if (!IsLocalRequest(context)) throw new ProxyException(403, "local_access_only", "This application accepts same-origin loopback access only.");
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (BadHttpRequestException ex) { await WriteError(context, new ProxyException(ex.StatusCode, "invalid_request", "Invalid or oversized HTTP request.")); }
            catch (ProxyException ex) { await WriteError(context, ex); }
            catch (Exception) { await WriteError(context, new ProxyException(500, "proxy_error", "The proxy could not process the request.")); }
        });
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) { app.UseDefaultFiles(); app.UseStaticFiles(); }
        app.MapGet("/", () => Results.Redirect("/ui/"));
        app.MapGet("/health", () => Json(new JsonObject { ["status"] = "ok" }));
        app.MapGet("/v1/models", (IModelRegistry models) => Json(Models(models)));
        app.MapPost("/v1/chat/completions", (HttpContext context, ProxyRelay relay) => relay.Handle(context));
        app.MapGet("/api/setup", (HttpContext context, IModelRegistry models) => Json(Setup(models, context.Request.Host)));
        app.MapGet("/api/traffic", (ITrafficStore store) => Results.Json(store.Snapshot(), ProxyJsonContext.Default.TrafficSnapshot));
        app.MapGet("/api/traffic/{id}", (string id, ITrafficStore store) => store.Detail(id) is { } detail
            ? Results.Json(detail, ProxyJsonContext.Default.TrafficDetail) : Results.NotFound());
        app.MapDelete("/api/traffic", (ITrafficStore store) => { store.Clear(); return Results.NoContent(); });
        app.MapGet("/api/traffic/events", Events);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (app.Urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback))
            {
                app.Logger.LogError("ProxyCopilot requires loopback listen addresses.");
                app.Lifetime.StopApplication();
            }
        });
        return app;
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;
        var host = context.Request.Host.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip))) return false;
        if (context.Request.Headers["Sec-Fetch-Site"] is { Count: > 0 } site && site.ToString() is not ("same-origin" or "none")) return false;
        if (context.Request.Headers.Origin is { Count: > 0 } origins)
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)) return false;
            var expected = $"{context.Request.Scheme}://{context.Request.Host}";
            if (!origin.GetLeftPart(UriPartial.Authority).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static IResult Json(JsonObject value) => Results.Json(value, ProxyJsonContext.Default.JsonObject);

    public static JsonObject Models(IModelRegistry registry) => new()
    {
        ["object"] = "list", ["data"] = new JsonArray(registry.Models.Select(route => (JsonNode)new JsonObject {
            ["id"] = route.Id, ["object"] = "model", ["created"] = 0, ["owned_by"] = route.Model.Metadata.OwnedBy ?? route.Provider,
            ["name"] = route.Model.Metadata.DisplayName ?? route.Id,
            ["max_input_tokens"] = route.Model.Metadata.MaxInputTokens, ["max_output_tokens"] = route.Model.Metadata.MaxOutputTokens,
            ["capabilities"] = new JsonObject { ["tool_calling"] = route.SupportsTools, ["vision"] = false }
        }).ToArray())
    };

    public static JsonObject Setup(IModelRegistry registry, HostString host)
    {
        var models = new JsonArray(registry.Models.Select(route => (JsonNode)EditorModel(route, host)).ToArray());
        return new JsonObject { ["configuration"] = new JsonArray(new JsonObject { ["name"] = "GnOuGo Proxy", ["vendor"] = "customendpoint", ["apiType"] = "chat-completions", ["models"] = models }) };
    }

    private static JsonObject EditorModel(ModelRoute route, HostString host)
    {
        var metadata = route.Model.Metadata;
        // VS Code reserves maxOutputTokens from its context budget. A model's
        // independent maximum output ceiling is not a suitable editor default.
        var output = Math.Min(8192, Math.Min(metadata.MaxOutputTokens!.Value,
            route.Options.Connection.RequestPolicy.MaxOutputTokensCap ?? int.MaxValue));
        if (metadata.ContextWindowTokens is { } context) output = Math.Min(output, Math.Max(1, context / 2));
        var model = new JsonObject {
            ["id"] = route.Id, ["name"] = metadata.DisplayName ?? route.Id,
            ["url"] = $"http://{host}/v1/chat/completions", ["toolCalling"] = route.SupportsTools, ["vision"] = false,
            ["maxInputTokens"] = metadata.MaxInputTokens, ["maxOutputTokens"] = output
        };
        if (metadata.ContextWindowTokens is { } window) model["contextWindow"] = window;
        if (route.SupportsTools) model["editTools"] = new JsonArray("find-replace", "multi-find-replace");
        return model;
    }

    private static async Task Events(HttpContext context, ITrafficStore store)
    {
        using var subscription = store.Subscribe();
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var ct = context.RequestAborted;
        await foreach (var version in subscription.Reader.ReadAllAsync(ct))
        {
            await context.Response.WriteAsync($"event: changed\ndata: {{\"version\":{version}}}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
            // Notifications are invalidations, not payload storage. A reconnect always
            // starts with one, and viewers fetch current snapshots after any missed event.
            await Task.Delay(250, ct);
        }
    }

    public static async Task WriteError(HttpContext context, ProxyException error)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = error.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new JsonObject { ["error"] = new JsonObject {
            ["message"] = error.Message, ["type"] = error.StatusCode < 500 ? "invalid_request_error" : "api_error", ["code"] = error.Code
        } };
        await context.Response.WriteAsync(body.ToJsonString(ProxyJsonContext.Default.Options), context.RequestAborted);
    }
}
