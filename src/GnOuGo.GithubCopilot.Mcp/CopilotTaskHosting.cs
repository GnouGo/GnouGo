using System.Text.Json.Nodes;
using ModelContextProtocol;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

internal static class CopilotTaskHosting
{
    internal static IMcpServerBuilder WithCopilotTasks(this IMcpServerBuilder builder,
        KeyVaultCopilotTaskStore store, CodeMcpTraceContextAccessor trace)
    {
        builder.Services.Configure<McpServerOptions>(options => ConfigureContext(options, store, trace));
        return builder.WithTasks(store, options => options.ExecutionModeSelector = request =>
        {
            // Malformed arguments remain on the synchronous validation path, which
            // returns the existing INVALID_INPUT envelope before tenant/session work.
            try { CopilotListContract.ValidateArguments(request.Params); CopilotAttachmentContract.ValidateArguments(request.Params); }
            catch (Exception error) when (error is ArgumentException or McpException) { return McpTaskExecutionMode.Synchronous; }
            return request.Params.Name is "copilot_interactive_one_shot" or "code_agent_edit" && !string.IsNullOrWhiteSpace(trace.Current?.TenantId)
                ? McpTaskExecutionMode.Required : McpTaskExecutionMode.Synchronous;
        });
    }
    internal static void ConfigureContext(McpServerOptions options, KeyVaultCopilotTaskStore store, CodeMcpTraceContextAccessor trace)
    {
        // Tasks are created before CallTool filters and polled through different methods.
        // Establish authenticated host metadata at the common message boundary.
        options.Filters.Message.IncomingFilters.Add(next => async (context, ct) =>
        {
            var request = context.JsonRpcMessage as JsonRpcRequest;
            using var tenant = trace.Push(CodeMcpTraceContext.FromMcpMeta(request?.Params?["_meta"] as JsonObject));
            using var invocation = request?.Method == "tools/call" ? store.BeginInvocation() : null;
            await next(context, ct);
        });
    }
}
