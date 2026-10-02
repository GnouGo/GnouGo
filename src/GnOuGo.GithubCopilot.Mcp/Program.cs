using GnOuGo.GithubCopilot.Mcp;
using GnOuGo.Mcp.Core;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using GnOuGo.Observability.Core;
using Microsoft.Extensions.Configuration;

var builder = CodeHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.AddGnOuGoOpenTelemetry("GnOuGo.GithubCopilot.Mcp", settings =>
{
    settings.ActivitySources = [.. settings.ActivitySources, "GnOuGo.GithubCopilot.Mcp.Copilot"];
});

var keyVaultReader = KeyVaultSecretReaderFactory.CreateWorkspaceCatalogReader(
    builder.Configuration["KeyVault:DatabasePath"],
    AppContext.BaseDirectory);
var keyVaultOverlay = await CopilotKeyVaultConfigurationOverlay.LoadAsync(keyVaultReader);
if (keyVaultOverlay.Values.Count > 0)
    builder.Configuration.AddInMemoryCollection(keyVaultOverlay.Values);

builder.Services.AddSingleton<IConfigureOptions<CodeServerSettings>, CodeServerSettingsOptionsConfigurator>();
builder.Services.AddHttpClient(nameof(ConfigurationCopilotProviderConfigResolver));
builder.Services.AddSingleton<IKeyVaultSecretCatalogReader>(keyVaultReader);
builder.Services.AddSingleton<IKeyVaultSecretReader>(sp =>
    sp.GetRequiredService<IKeyVaultSecretCatalogReader>());
builder.Services.AddSingleton<IKeyVaultRecordStore>(_ =>
    KeyVaultRecordStoreFactory.CreateWorkspaceStore(
        builder.Configuration["KeyVault:DatabasePath"],
        AppContext.BaseDirectory));
builder.Services.AddSingleton<ICopilotProviderConfigResolver, ConfigurationCopilotProviderConfigResolver>();
builder.Services.AddSingleton<ICopilotProviderResolver, CoreCopilotProviderResolver>();
builder.Services.AddHttpClient(nameof(CopilotInferenceProxyHandler), client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<ICopilotSdkClientFactory>(sp =>
{
    var endpoint = sp.GetRequiredService<IOptions<CodeServerSettings>>().Value.Copilot.InferenceProxyEndpoint;
    return new GitHubCopilotSdkClientFactory(sp.GetRequiredService<ILoggerFactory>(), endpoint is null ? null :
        new CopilotInferenceProxyHandler(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CopilotInferenceProxyHandler)), new Uri(endpoint, UriKind.Absolute)));
});
builder.Services.AddSingleton<McpCopilotHumanInputProvider>();
builder.Services.AddSingleton<ICopilotHumanInputProvider>(sp => sp.GetRequiredService<McpCopilotHumanInputProvider>());
builder.Services.AddSingleton<KeyVaultCopilotPermissionGrantStore>();
builder.Services.AddSingleton<ICopilotPermissionGrantStore>(sp => sp.GetRequiredService<KeyVaultCopilotPermissionGrantStore>());
builder.Services.AddSingleton<McpCopilotPermissionEventSink>();
builder.Services.AddSingleton<ICopilotPermissionEventSink>(sp => sp.GetRequiredService<McpCopilotPermissionEventSink>());
builder.Services.AddSingleton<CopilotSessionManager>();
builder.Services.AddSingleton<CopilotReviewManager>();
builder.Services.AddSingleton<CodePolicy>();
builder.Services.AddSingleton<CodeProjectService>();
builder.Services.AddSingleton<CodeMcpTraceContextAccessor>();
builder.Services.AddSingleton<CodeProgressReporter>();
builder.Services.AddSingleton<ICopilotSessionFileSystemFactory, LocalProjectSessionFsFactory>();
builder.Services.AddSingleton<CopilotMcpConfiguration>();
builder.Services.AddSingleton<CopilotCodeService>();
builder.Services.AddTransient<CodeTools>();
builder.Services.AddTransient<CopilotTools>();
builder.Services.AddSingleton<BoundedCopilotTasks>();
builder.Services.AddTransient<BoundedCopilotTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.GithubCopilot.Mcp",
            Version = "1.0.0"
        };
        options.AddGnOuGoToolErrorNormalizer();
        CopilotAttachmentContract.Configure(options);
        CopilotListContract.Configure(options);
        options.Filters.Request.ListToolsFilters.Add(next => async (request, ct) =>
        {
            var result = await next(request, ct);
            foreach (var tool in result.Tools)
                McpEffectMetadata.Publish(tool, tool.Name switch
                {
                    "code_get_policy" or "code_project_summary" or "code_read_file" or "code_search_text" or "code_suggest_change" or
                    "copilot_get_capabilities" or "copilot_connectivity" or "copilot_status" or "copilot_auth_status" or "copilot_list_models" or
                    "copilot_session_list" or "copilot_session_get_configuration" or "copilot_session_history" or "copilot_session_get_mode" or
                    "copilot_plan_read" or "copilot_session_get_foreground" or "copilot_workspace_list_files" or "copilot_workspace_read_file" or
                    "copilot_review" or "copilot_review_analyze_batch" or "copilot_task_contract" or "copilot_task_validate" or "copilot_task_inspect" or "copilot_permission_grants_list" => "read",
                    "code_write_file" or "copilot_plan_update" or "copilot_workspace_create_file" => "write",
                    "code_agent_edit" or "copilot_one_shot" or "copilot_interactive_one_shot" or "copilot_session_send" or "copilot_task_run" => "execute",
                    "copilot_session_create" or "copilot_session_resume" or "copilot_session_disconnect" or "copilot_session_delete" or "copilot_session_abort" or
                    "copilot_session_set_model" or "copilot_session_set_mode" or "copilot_session_set_foreground" or "copilot_plan_delete" or
                    "copilot_review_start" or "copilot_review_finish" or "copilot_permission_grant_create" or "copilot_permission_grant_revoke" or "copilot_permission_grants_revoke_agent" => "lifecycle",
                    _ => null
                });
            return result;
        });
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancellationToken) =>
        {
            var accessor = request.Services is null ? null : request.Services.GetService<CodeMcpTraceContextAccessor>();
            var context = CodeMcpTraceContext.FromMcpMeta(request.Params.Meta);
            using var scope = accessor?.Push(context);
            return await next(request, cancellationToken);
        });
    })
    .WithStdioServerTransport()
    .WithTools<CodeTools>(CodeMcpJson.SerializerOptions)
    .WithTools<CopilotTools>(CodeMcpJson.SerializerOptions)
    .WithTools<BoundedCopilotTools>(CodeMcpJson.SerializerOptions);

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.GithubCopilot.Mcp.Startup");
if (keyVaultOverlay.Warning is not null)
    logger.LogWarning("{KeyVaultConfigurationWarning}", keyVaultOverlay.Warning);
var policy = host.Services.GetRequiredService<CodePolicy>();
var settings = host.Services.GetRequiredService<IOptions<CodeServerSettings>>().Value;
if (settings.Copilot.InferenceProxyEndpoint is { } inferenceProxy)
{
    // Resolve the configured SDK factory before attesting that its interception is enabled.
    _ = host.Services.GetRequiredService<ICopilotSdkClientFactory>();
    var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CopilotInferenceProxyHandler));
    using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var ready = await client.PostAsync(inferenceProxy.TrimEnd('/') + "/ready", new StringContent("sdk-http-interception-v1"), readyTimeout.Token);
    ready.EnsureSuccessStatusCode();
}
var info = policy.DescribePolicy();

logger.LogInformation(
    "Code MCP configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, defaultWorkingDirectory={DefaultWorkingDirectory}, allowedRoots={AllowedRoots}, allowedExtensions={AllowedExtensions}, allowWrites={AllowWrites}, copilotProvider={CopilotProvider}, copilotModel={CopilotModel}, copilotMode={CopilotMode}, copilotReasoningEffort={CopilotReasoningEffort}, copilotForwardTraceContext={CopilotForwardTraceContext}, copilotTelemetryEnabled={CopilotTelemetryEnabled}, hasToken={HasToken}, useLoggedInUser={UseLoggedInUser}, requestTimeoutSeconds={RequestTimeoutSeconds}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    info.DefaultWorkingDirectory,
    string.Join(", ", info.AllowedWorkingRoots),
    string.Join(", ", info.AllowedExtensions),
    info.AllowWrites,
    info.CopilotProvider,
    info.CopilotModel,
    info.CopilotMode,
    settings.Copilot.ReasoningEffort,
    info.CopilotForwardTraceContext,
    info.CopilotTelemetryEnabled,
    info.HasConfiguredToken,
    settings.Copilot.UseLoggedInUser,
    settings.Copilot.RequestTimeoutSeconds);

await host.RunAsync();
