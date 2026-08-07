using System.Text.RegularExpressions;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Pipeline intent classification and MCP capability matching.
/// </summary>
public sealed partial class WorkflowPlanExecutor
{
    private static string? NormalizePipelineWorkKind(string? workKind)
    {
        if (string.IsNullOrWhiteSpace(workKind))
            return null;

        var normalized = workKind.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            PipelineWorkKindOrchestration => PipelineWorkKindOrchestration,
            PipelineWorkKindDeterministicShaping => PipelineWorkKindDeterministicShaping,
            PipelineWorkKindExternalWork => PipelineWorkKindExternalWork,
            _ => null
        };
    }

    private static string? NormalizePipelineContractRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return null;

        var normalized = role.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            PipelineContractRoleExternalAction => PipelineContractRoleExternalAction,
            PipelineContractRoleTypedDataProducer => PipelineContractRoleTypedDataProducer,
            PipelineContractRoleAlgorithmicTransform => PipelineContractRoleAlgorithmicTransform,
            PipelineContractRoleDeterministicGlue => PipelineContractRoleDeterministicGlue,
            PipelineContractRoleOrchestration => PipelineContractRoleOrchestration,
            PipelineContractRoleAbstractPolicy => PipelineContractRoleAbstractPolicy,
            _ => null
        };
    }

    private static string InferPipelineWorkKind(WorkflowPipelineSubworkflowSpec spec)
    {
        var intentText = BuildPipelineSpecIntentText(spec);
        if (ContainsExternalWorkIntent(intentText))
            return PipelineWorkKindExternalWork;

        if (ContainsDeterministicShapingIntent(intentText))
            return PipelineWorkKindDeterministicShaping;

        return PipelineWorkKindOrchestration;
    }

    private static string InferPipelineContractRole(WorkflowPipelineSubworkflowSpec spec)
    {
        if (IsExternalWorkSpec(spec) || spec.PlannedTools.Any(static tool => tool.Required))
            return PipelineContractRoleExternalAction;

        var intentText = BuildPipelineSpecIntentText(spec);
        if (ContainsAlgorithmicExtractionIntent(intentText))
            return PipelineContractRoleAlgorithmicTransform;

        if (string.Equals(spec.WorkKind, PipelineWorkKindDeterministicShaping, StringComparison.Ordinal)
            || ContainsDeterministicShapingIntent(intentText))
            return PipelineContractRoleDeterministicGlue;

        if (string.Equals(spec.WorkKind, PipelineWorkKindOrchestration, StringComparison.Ordinal))
            return PipelineContractRoleOrchestration;

        if (HasConcreteTypedOutputContract(spec))
            return PipelineContractRoleTypedDataProducer;

        return PipelineContractRoleAbstractPolicy;
    }

    private static bool IsExternalWorkSpec(WorkflowPipelineSubworkflowSpec spec)
    {
        if (string.Equals(spec.WorkKind, PipelineWorkKindDeterministicShaping, StringComparison.Ordinal))
            return false;
        if (spec.PlannedTools.Count > 0 || (spec.PlannedNativeSteps?.Count ?? 0) > 0)
            return true;
        return string.Equals(spec.WorkKind, PipelineWorkKindExternalWork, StringComparison.Ordinal)
               || ContainsExternalWorkIntent(BuildPipelineSpecIntentText(spec));
    }

    private static bool HasStrongLocalProcessingIntent(WorkflowPipelineSubworkflowSpec spec)
        => StrongLocalProcessingLeafRegex().IsMatch(string.Join(' ', new[]
            {
                spec.Name,
                spec.Goal,
                spec.Description,
                spec.ConcreteOutcome
            }.Where(static value => !string.IsNullOrWhiteSpace(value))!)
            .Replace('_', ' ')
            .Replace('-', ' '));

    private static string BuildPipelineSpecIntentText(WorkflowPipelineSubworkflowSpec spec)
        => string.Join('\n', new[]
            {
                spec.Name,
                spec.Goal,
                spec.Description,
                spec.ExtractReason,
                spec.Content
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value)))!;

    private static bool ContainsExternalWorkIntent(string text)
        => ExternalWorkIntentRegex().IsMatch(text);

    private static bool ContainsDeterministicShapingIntent(string text)
        => DeterministicShapingIntentRegex().IsMatch(text);

    private static bool ContainsAlgorithmicExtractionIntent(string text)
        => AlgorithmicExtractionIntentRegex().IsMatch(text);

    private static IReadOnlyList<PipelineMcpCapabilityMatch> FindLikelyMcpCapabilityMatches(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
    {
        if (pipelineMcpContext.Servers.Count == 0)
            return Array.Empty<PipelineMcpCapabilityMatch>();

        var specTokens = ExtractIntentTokens(BuildPipelineSpecIntentText(spec));
        if (specTokens.Count == 0)
            return Array.Empty<PipelineMcpCapabilityMatch>();

        var matches = new List<PipelineMcpCapabilityMatch>();
        foreach (var server in pipelineMcpContext.Servers)
        {
            foreach (var tool in server.Tools)
            {
                if (CapabilityTextMatchesIntent(specTokens, tool.Name, tool.Description, server.Name, server.Description))
                    matches.Add(new PipelineMcpCapabilityMatch(server.Name, "tool", tool.Name));
            }

            foreach (var prompt in server.Prompts)
            {
                if (CapabilityTextMatchesIntent(specTokens, prompt.Name, prompt.Description, server.Name, server.Description))
                    matches.Add(new PipelineMcpCapabilityMatch(server.Name, "prompt", prompt.Name));
            }
        }

        return matches
            .DistinctBy(static match => match.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CapabilityTextMatchesIntent(
        IReadOnlySet<string> specTokens,
        string name,
        string? description,
        string serverName,
        string? serverDescription)
    {
        var capabilityTokens = ExtractIntentTokens(string.Join(' ', new[] { name, description, serverName, serverDescription }
            .Where(static value => !string.IsNullOrWhiteSpace(value)))!);
        if (capabilityTokens.Count == 0)
            return false;

        return capabilityTokens.Any(specTokens.Contains);
    }

    private static IReadOnlySet<string> ExtractIntentTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in IntentTokenRegex().Matches(text))
        {
            var token = match.Value.Trim().ToLowerInvariant();
            if (token.Length < 4 || PipelineIntentStopWords.Contains(token))
                continue;

            tokens.Add(token);
        }

        return tokens;
    }
}


