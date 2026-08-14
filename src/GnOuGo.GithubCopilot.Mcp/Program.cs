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
builder.Services.AddSingleton<ICopilotSdkClientFactory, GitHubCopilotSdkClientFactory>();
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
builder.Services.AddSingleton<ICodeAssistantClient, GitHubCopilotCodeClient>();
builder.Services.AddTransient<CodeTools>();
builder.Services.AddTransient<CopilotTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.GithubCopilot.Mcp",
            Version = "1.0.0"
        };
        options.AddGnOuGoToolErrorNormalizer();
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
    .WithTools<CopilotTools>(CodeMcpJson.SerializerOptions);

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.GithubCopilot.Mcp.Startup");
if (keyVaultOverlay.Warning is not null)
    logger.LogWarning("{KeyVaultConfigurationWarning}", keyVaultOverlay.Warning);
var policy = host.Services.GetRequiredService<CodePolicy>();
var settings = host.Services.GetRequiredService<IOptions<CodeServerSettings>>().Value;
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
