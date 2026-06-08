using System.Text.Json.Nodes;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Exposes persisted Agent MCP workflows as dynamic workflow.route candidates.
/// </summary>
public sealed class DatabaseAgentWorkflowCandidateProvider : IWorkflowCandidateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseAgentWorkflowCandidateProvider> _logger;

    public DatabaseAgentWorkflowCandidateProvider(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseAgentWorkflowCandidateProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowRouteCandidate>> GetCandidatesAsync(
        WorkflowRouteCandidateQuery query,
        CancellationToken ct)
    {
        if (!string.Equals(query.Kind, "database", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<WorkflowRouteCandidate>();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var agents = await repository.ListAgentsAsync(ct);

        var candidates = new List<WorkflowRouteCandidate>();
        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();

            WorkflowSkillDef? skill = null;
            try
            {
                skill = WorkflowParser.ParseSkill(agent.Workflow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse skill metadata for agent '{AgentName}'.", agent.Name);
            }

            var tags = skill?.Tags ?? new List<string>();
            if (!MatchesTags(tags, query))
                continue;

            candidates.Add(new WorkflowRouteCandidate
            {
                Id = $"database:{agent.Name}",
                Name = agent.Name,
                Ref = new JsonObject
                {
                    ["kind"] = "database",
                    ["agent"] = agent.Name
                },
                Description = skill?.Description ?? agent.OriginalPrompt,
                Tags = tags.ToList(),
                Inputs = skill?.Inputs is { Count: > 0 } ? JsonSchemaConverter.InputsToJsonSchema(skill.Inputs) : null,
                Outputs = skill?.Outputs is { Count: > 0 } ? JsonSchemaConverter.OutputsToJsonSchema(skill.Outputs) : null
            });

            if (query.Limit is > 0 && candidates.Count >= query.Limit.Value)
                break;
        }

        return candidates;
    }

    private static bool MatchesTags(IReadOnlyList<string> tags, WorkflowRouteCandidateQuery query)
    {
        var tagSet = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (query.ExcludeTags.Count > 0 && query.ExcludeTags.Any(tagSet.Contains))
            return false;

        if (query.TagsAll.Count > 0 && !query.TagsAll.All(tagSet.Contains))
            return false;

        if (query.TagsAny.Count > 0 && !query.TagsAny.Any(tagSet.Contains))
            return false;

        return true;
    }
}
