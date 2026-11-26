using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal static class CodeTaskServices
{
    internal static IServiceCollection AddCodeTaskServices(this IServiceCollection services)
    {
        services.AddOptions();
        services.AddSingleton<IConfigureOptions<CodeServerSettings>, CodeServerSettingsOptionsConfigurator>();
        services.AddHttpClient(nameof(ConfigurationCopilotProviderConfigResolver));
        services.AddSingleton<ICopilotProviderConfigResolver, ConfigurationCopilotProviderConfigResolver>();
        services.AddSingleton<ICopilotProviderResolver, CoreCopilotProviderResolver>();
        services.AddHttpClient(nameof(CopilotInferenceProxyHandler), client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        services.AddSingleton(sp =>
        {
            var endpoint = sp.GetRequiredService<IOptions<CodeServerSettings>>().Value.Copilot.InferenceProxyEndpoint;
            return new CopilotInferenceProxyHandler(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CopilotInferenceProxyHandler)),
                new Uri(endpoint ?? throw new InvalidOperationException("The inference proxy is not configured."), UriKind.Absolute));
        });
        services.AddSingleton<ICopilotSdkClientFactory>(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<CodeServerSettings>>().Value.Copilot.InferenceProxyEndpoint is not null;
            return new GitHubCopilotSdkClientFactory(sp.GetRequiredService<ILoggerFactory>(),
                configured ? sp.GetRequiredService<CopilotInferenceProxyHandler>() : null,
                config => CopilotStructuredToolAdapter.NormalizeTransport(config.InferenceTransport) is null ? null : new CopilotStructuredToolAdapter());
        });
        services.AddSingleton<KeyVaultCopilotPermissionGrantStore>();
        services.AddSingleton<ICopilotPermissionGrantStore>(sp => sp.GetRequiredService<KeyVaultCopilotPermissionGrantStore>());
        services.AddSingleton<CopilotSessionManager>();
        services.AddSingleton<CodePolicy>();
        services.AddSingleton<CodeProgressReporter>();
        services.AddSingleton<ICopilotSessionFileSystemFactory, LocalProjectSessionFsFactory>();
        services.AddSingleton<CopilotMcpConfiguration>();
        services.AddSingleton<BoundedCopilotTasks>();
        return services;
    }
}
