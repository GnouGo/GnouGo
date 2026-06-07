using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Server-side workflow.call resolver that adds persisted agent workflows.
/// </summary>
public sealed class AgentDatabaseWorkflowCallResolver : DefaultWorkflowCallResolver
{
    private readonly IAgentRepository? _agentRepository;
    private readonly IServiceScopeFactory? _scopeFactory;

    public AgentDatabaseWorkflowCallResolver(
        IAgentRepository agentRepository,
        string? workspaceRoot = null,
        IEnumerable<string>? allowedHostnames = null)
        : base(workspaceRoot, allowedHostnames)
    {
        _agentRepository = agentRepository;
    }

    public AgentDatabaseWorkflowCallResolver(
        IServiceScopeFactory scopeFactory,
        string? workspaceRoot = null,
        IEnumerable<string>? allowedHostnames = null)
        : base(workspaceRoot, allowedHostnames)
    {
        _scopeFactory = scopeFactory;
    }

    public override async Task<WorkflowCallResolution> ResolveAsync(WorkflowCallResolutionContext context, CancellationToken ct)
    {
        if (!string.Equals(context.Kind, "database", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.Kind, "datatabase", StringComparison.OrdinalIgnoreCase))
        {
            return await base.ResolveAsync(context, ct);
        }

        var agentName = GetString(context.Ref, "agent") ?? GetString(context.Ref, "name")
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Database workflow.call requires 'agent' or 'name'");

        var callStackKey = $"database:{agentName}";
        if (context.CallStack.Contains(callStackKey))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Cycle detected: agent workflow '{agentName}' already in call stack");

        var workflowYaml = await LoadAgentWorkflowAsync(agentName, ct);
        var resolution = CompileDocumentReference(workflowYaml, context.Ref, $"database:{agentName}");

        return new WorkflowCallResolution
        {
            Workflow = resolution.Workflow,
            WorkflowName = agentName,
            CallStackKey = callStackKey
        };
    }

    private async Task<string> LoadAgentWorkflowAsync(string agentName, CancellationToken ct)
    {
        if (_agentRepository is not null)
            return await LoadAgentWorkflowAsync(_agentRepository, agentName, ct);

        if (_scopeFactory is null)
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchNetwork, "No agent repository configured for database workflow.call");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        return await LoadAgentWorkflowAsync(repository, agentName, ct);
    }

    private static async Task<string> LoadAgentWorkflowAsync(IAgentRepository repository, string agentName, CancellationToken ct)
    {
        var agent = await repository.GetByNameAsync(agentName, ct)
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Agent '{agentName}' not found");

        if (string.IsNullOrWhiteSpace(agent.Workflow))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Agent '{agentName}' does not contain a workflow definition");

        return agent.Workflow;
    }
}

