using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.GithubCopilot.Mcp;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Flow.Copilot;

/// <summary>A code task uses the managed SDK session and the same durable service as external callers.</summary>
public sealed class CopilotAgentTaskRunner(IConfiguration configuration, IConfiguration? executionPolicy = null) : IAgentTaskRunner
{
    public bool SupportsActionLimit => true;
    public string Description => "Explore, review or edit an approved project with a bounded Copilot SDK session.";

    public Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct) => InvokeAsync(null, async service =>
    {
        var response = await service.ContractAsync(ct);
        return response["contract"].Deserialize(AgentTaskJsonContext.Default.AgentTaskRunnerContract)
            ?? throw new InvalidDataException("Missing code task contract.");
    }, ct);

    public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) =>
        InvokeAsync(context, service => service.ValidateAsync(context, ct), ct);

    public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct) =>
        InvokeAsync(context, service => service.RunAsync(context, ct), ct);

    public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) =>
        InvokeAsync(context, service => service.InspectAsync(context, ct), ct);

    private async Task<T> InvokeAsync<T>(AgentTaskContext? context, Func<BoundedCopilotTasks, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var database = configuration["KeyVault:DatabasePath"];
        var reader = KeyVaultSecretReaderFactory.CreateWorkspaceCatalogReader(database, AppContext.BaseDirectory);
        var overlay = await CopilotKeyVaultConfigurationOverlay.LoadAsync(reader);
        var builder = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(overlay.Values);
        if (executionPolicy is not null) builder.AddConfiguration(executionPolicy);
        var settings = builder.Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(settings);
        services.AddSingleton<IKeyVaultRecordStore>(KeyVaultRecordStoreFactory.CreateWorkspaceStore(database, AppContext.BaseDirectory));
        services.AddSingleton<IKeyVaultSecretCatalogReader>(reader);
        services.AddSingleton<IKeyVaultSecretReader>(reader);
        services.AddSingleton<CodeMcpTraceContextAccessor>();
        if (context?.Execution?.Engine.HumanInputProvider is { } human)
            services.AddSingleton<ICopilotHumanInputProvider>(new WorkflowCopilotHumanInput(context, human, ct));
        services.AddCodeTaskServices();
        await using var provider = services.BuildServiceProvider();
        var trace = new CodeMcpTraceContext(null, null, null, null, null, context?.ExecutionId, context?.RunId, context?.InvocationId,
            "agent.run", null, null, null) { TenantId = context?.TenantId, ExecutionId = context?.ExecutionId,
            AgentId = context?.AgentId, AgentName = context?.AgentName };
        using var owner = provider.GetRequiredService<CodeMcpTraceContextAccessor>().Push(trace);
        if (settings["Code:Copilot:InferenceProxyEndpoint"] is { Length: > 0 })
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            await provider.GetRequiredService<CopilotInferenceProxyHandler>().VerifyReadyAsync(ready.Token);
        }
        return await operation(provider.GetRequiredService<BoundedCopilotTasks>());
    }
}
