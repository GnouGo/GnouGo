using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    /// <summary>
    /// Strips markdown code fences (```yaml ... ``` or ``` ... ```) from LLM output.
    /// </summary>
    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            // Remove first line (```yaml or ```)
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            // Remove trailing ```
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }
        return trimmed;
    }

    private static WorkflowDocument ParseAndValidateGeneratedWorkflow(string yaml)
    {
        var generatedDoc = Parsing.WorkflowParser.Parse(yaml);

        if (generatedDoc.Workflows.Count == 0)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Validation failed: required root key 'workflows' must be a non-empty object.");

        return generatedDoc;
    }

    private static void ValidateGeneratedWorkflowForPlan(
        WorkflowDocument generatedDoc,
        IReadOnlyList<McpServerDiscovery>? discovered,
        StepExecutorRegistry registry)
    {
        var validator = new WorkflowValidator(registry);
        var errors = validator.Validate(generatedDoc);
        WorkflowSemanticValidationException? semanticException = null;
        Exception? compilationException = null;
        var mcpToolContracts = BuildMcpToolOutputContracts(discovered);

        WorkflowPlanSemanticValidator.NormalizeMcpCallInputRequests(generatedDoc, mcpToolContracts);

        try
        {
            WorkflowPlanSemanticValidator.ValidateWithStepContracts(generatedDoc, mcpToolContracts, registry.GetContracts());
        }
        catch (WorkflowSemanticValidationException ex)
        {
            semanticException = ex;
        }

        if (!errors.Any(IsFatalCompilerValidationError))
        {
            try
            {
                var compiler = new WorkflowCompiler();
                compiler.Compile(generatedDoc);
            }
            catch (WorkflowCompilationException ex)
            {
                compilationException = ex;
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }
        }

        if (errors.Count == 0 && semanticException == null && compilationException == null)
            return;

        var diagnostics = new List<string>();
        if (errors.Count > 0)
            diagnostics.Add("workflow validation: " + FormatValidationErrors(errors));
        if (semanticException != null)
            diagnostics.Add("semantic validation: " + semanticException.Message);
        if (compilationException != null)
            diagnostics.Add("compilation: " + compilationException.Message);

        throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan,
            "Generated workflow validation failed: " + string.Join(" | ", diagnostics));
    }

    private static async Task RunStandardPlanValidationSequenceAsync(
        WorkflowDocument generatedDoc,
        JsonObject? policy,
        JsonObject? limits,
        JsonObject? validate,
        IReadOnlyList<McpServerDiscovery>? validationDiscovered,
        StepExecutionContext ctx,
        ITelemetrySpan validationSpan,
        CancellationToken ct)
    {
        if (policy != null)
            EnforcePolicy(generatedDoc, policy);

        if (limits != null)
            EnforceLimits(generatedDoc, limits);

        var compileValidation = validate?["compile"]?.GetValue<bool>() ?? true;
        var dryRunValidation = validate?["dry_run"]?.GetValue<bool>() ?? false;
        if (compileValidation)
        {
            validationSpan.SetAttribute("gnougo-flow.plan.compile_validation", true);
            ValidateGeneratedWorkflowForPlan(generatedDoc, validationDiscovered, ctx.Engine.Registry);
        }

        if (compileValidation || dryRunValidation)
            ValidateMcpDiscoveryCoverage(generatedDoc, validationDiscovered);

        if (dryRunValidation)
        {
            validationSpan.SetAttribute("gnougo-flow.plan.dry_run", true);
            await WorkflowPlanDryRunValidator.ValidateAsync(
                generatedDoc,
                BuildDryRunMcpClientFactory(validationDiscovered),
                ctx.Engine.Logger,
                ct);
        }
    }

    private static void ValidateMcpDiscoveryCoverage(
        WorkflowDocument generatedDoc,
        IReadOnlyList<McpServerDiscovery>? discovered)
    {
        var toolCalls = generatedDoc.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps))
            .Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal))
            .Where(static step => !string.Equals(ReadMcpCallInputString(step, "kind"), "prompt", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (toolCalls.Length == 0)
            return;

        if (discovered == null || discovered.Count == 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                "MCP_DISCOVERY_REQUIRED: generated workflow contains mcp.call tool steps, but no MCP tool catalog was discovered. Validation is fail-closed.");
        }

        foreach (var step in toolCalls)
        {
            var serverName = ReadMcpCallInputString(step, "server");
            if (string.IsNullOrWhiteSpace(serverName) || serverName.Contains("${", StringComparison.Ordinal))
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.TemplatePlan,
                    $"MCP_SERVER_DYNAMIC_UNVERIFIABLE: mcp.call step '{step.Id}' must use a literal discovered server name during workflow.plan validation.");
            }

            var server = discovered.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, serverName, StringComparison.Ordinal));
            if (server == null)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.TemplatePlan,
                    $"MCP_SERVER_UNKNOWN: mcp.call step '{step.Id}' references server '{serverName}', which is absent from the discovered MCP catalog.");
            }

            if (!server.Discovered)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.TemplatePlan,
                    $"MCP_DISCOVERY_FAILED: MCP server '{serverName}' is referenced by step '{step.Id}', but tools/list did not succeed after all discovery attempts.");
            }

            if (server.Tools.Count == 0)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.TemplatePlan,
                    $"MCP_TOOL_CATALOG_EMPTY: MCP server '{serverName}' is referenced by step '{step.Id}', but its discovered tool catalog is empty.");
            }
        }
    }

    private static string? ReadMcpCallInputString(StepDef step, string fieldName)
    {
        return step.Input is JsonObject input
            && input[fieldName] is JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;
    }

    private static bool IsFatalCompilerValidationError(ValidationError error) =>
        error.Code is ErrorCodes.ExprParse
            or "DSL_VERSION"
            or "NO_WORKFLOWS"
            or ErrorCodes.WorkflowCycleDetected
            or "INVALID_ENTRYPOINT"
            or "DUPLICATE_STEP_ID";

    private static IReadOnlyList<McpToolOutputContract> BuildMcpToolOutputContracts(IReadOnlyList<McpServerDiscovery>? discovered)
    {
        if (discovered == null || discovered.Count == 0)
            return Array.Empty<McpToolOutputContract>();

        var contracts = new List<McpToolOutputContract>();
        foreach (var server in discovered)
        {
            foreach (var tool in server.Tools)
            {
                contracts.Add(new McpToolOutputContract(
                    server.Name,
                    tool.Name,
                    tool.InputSchema?.DeepClone(),
                    tool.OutputSchema?.DeepClone(),
                    tool.ExampleResponse?.DeepClone()));
            }
        }

        return contracts;
    }

    private static IMcpClientFactory? BuildDryRunMcpClientFactory(IReadOnlyList<McpServerDiscovery>? discovered)
    {
        if (discovered == null || discovered.Count == 0)
            return null;

        var factory = new InMemoryMcpClientFactory();
        foreach (var server in discovered)
        {
            var config = new MockMcpServerConfig
            {
                Description = server.Description,
                CallTimeoutSeconds = server.CallTimeoutSeconds,
                Tools = server.Tools.Select(tool => new McpToolInfo
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema?.DeepClone(),
                    OutputSchema = tool.OutputSchema?.DeepClone(),
                    ExampleResponse = tool.ExampleResponse?.DeepClone()
                }).ToList(),
                Prompts = server.Prompts.Select(prompt => new McpPromptInfo
                {
                    Name = prompt.Name,
                    Description = prompt.Description,
                    Arguments = prompt.Arguments?.Select(argument => new McpPromptArgument
                    {
                        Name = argument.Name,
                        Description = argument.Description,
                        Required = argument.Required
                    }).ToList()
                }).ToList()
            };

            foreach (var tool in server.Tools)
            {
                var outputSchema = tool.OutputSchema?.DeepClone();
                var exampleResponse = tool.ExampleResponse?.DeepClone();
                config.ToolHandlers[tool.Name] = _ => new McpCallResult
                {
                    IsError = false,
                    Content = exampleResponse?.DeepClone()
                        ?? WorkflowPlanDryRunValidator.CreateSampleFromJsonSchema(outputSchema),
                    Model = "dry-run-mcp",
                    Usage = new JsonObject
                    {
                        ["prompt_tokens"] = 1,
                        ["completion_tokens"] = 1,
                        ["total_tokens"] = 2
                    }
                };
            }

            factory.RegisterServer(server.Name, config);
        }

        return factory;
    }

    private static string FormatValidationErrors(IReadOnlyList<ValidationError> errors)
    {
        return string.Join("; ", errors.Select(error =>
        {
            var location = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(error.WorkflowName))
                location.Append($"workflow '{error.WorkflowName}'");
            if (!string.IsNullOrWhiteSpace(error.StepId))
            {
                if (location.Length > 0)
                    location.Append(", ");
                location.Append($"step '{error.StepId}'");
            }
            if (!string.IsNullOrWhiteSpace(error.Field))
            {
                if (location.Length > 0)
                    location.Append(", ");
                location.Append($"field '{error.Field}'");
            }

            var prefix = location.Length > 0 ? $"[{location}] " : "";
            return $"{prefix}{error.Code}: {error.Message}";
        }));
    }
}
