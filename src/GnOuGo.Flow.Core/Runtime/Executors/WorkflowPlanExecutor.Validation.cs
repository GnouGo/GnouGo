using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

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
        WorkflowDocument generatedDoc;
        try
        {
            generatedDoc = Parsing.WorkflowParser.Parse(yaml);
        }
        catch (Exception ex)
        {
            var details = WorkflowPlanDiagnostics.BuildExceptionDetails(
                WorkflowPlanDiagnostics.InferPlanErrorCode(ex.Message),
                "parse",
                ex.Message,
                ex);
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Generated workflow parse failed: {ex.Message} | repair diagnostics: {WorkflowPlanDiagnostics.ToPromptJson(details)}",
                inner: ex,
                details: details);
        }

        if (generatedDoc.Workflows.Count == 0)
        {
            var details = WorkflowPlanDiagnostics.BuildExceptionDetails(
                "MISSING_ROOT_KEY_WORKFLOWS",
                "validation",
                "required root key 'workflows' must be a non-empty object.");
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                "Validation failed: required root key 'workflows' must be a non-empty object. | repair diagnostics: " + WorkflowPlanDiagnostics.ToPromptJson(details),
                details: details);
        }

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

        var details = WorkflowPlanDiagnostics.BuildValidationFailureDetails(
            errors,
            semanticException,
            compilationException);

        throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan,
            "Generated workflow validation failed: "
            + string.Join(" | ", diagnostics)
            + " | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
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

        var dryRunValidation = validate?["dry_run"]?.GetValue<bool>() ?? false;
        validationSpan.SetAttribute("gnougo-flow.plan.validation.mode", "strict");
        validationSpan.SetAttribute("gnougo-flow.plan.compile_validation", true);
        validationSpan.SetAttribute("gnougo-flow.plan.compile_validation_forced", true);
        ValidateGeneratedWorkflowForPlan(generatedDoc, validationDiscovered, ctx.Engine.Registry);

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
            ThrowMcpDiscoveryCoverageError(
                "MCP_DISCOVERY_REQUIRED",
                "generated workflow contains mcp.call tool steps, but no MCP tool catalog was discovered. Validation is fail-closed.",
                "Run MCP discovery for this plan, remove mcp.call steps, or add an mcp.list discovery step before tool execution.");
        }

        foreach (var step in toolCalls)
        {
            var serverName = ReadMcpCallInputString(step, "server");
            if (string.IsNullOrWhiteSpace(serverName) || serverName.Contains("${", StringComparison.Ordinal))
            {
                ThrowMcpDiscoveryCoverageError(
                    "MCP_SERVER_DYNAMIC_UNVERIFIABLE",
                    $"mcp.call step '{step.Id}' must use a literal discovered server name during workflow.plan validation.",
                    "Use an exact server name from the discovered MCP catalog in input.server.");
            }

            var server = discovered.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, serverName, StringComparison.Ordinal));
            if (server == null)
            {
                ThrowMcpDiscoveryCoverageError(
                    "MCP_SERVER_UNKNOWN",
                    $"mcp.call step '{step.Id}' references server '{serverName}', which is absent from the discovered MCP catalog.",
                    "Change input.server to one of the discovered server names, or do not generate this mcp.call.");
            }

            if (!server.Discovered)
            {
                ThrowMcpDiscoveryCoverageError(
                    "MCP_DISCOVERY_FAILED",
                    $"MCP server '{serverName}' is referenced by step '{step.Id}', but tools/list did not succeed after all discovery attempts.",
                    "Do not rely on this server in the generated workflow unless discovery succeeds.");
            }

            if (server.Tools.Count == 0)
            {
                ThrowMcpDiscoveryCoverageError(
                    "MCP_TOOL_CATALOG_EMPTY",
                    $"MCP server '{serverName}' is referenced by step '{step.Id}', but its discovered tool catalog is empty.",
                    "Remove the mcp.call or select a discovered server with tools.");
            }
        }
    }

    [DoesNotReturn]
    private static void ThrowMcpDiscoveryCoverageError(string code, string message, string hint)
    {
        var diagnostics = new JsonArray();
        diagnostics.Add((JsonNode)new JsonObject
        {
            ["code"] = code,
            ["phase"] = "mcp_discovery_coverage",
            ["message"] = message,
            ["location"] = "mcp.discovery",
            ["hint"] = hint,
            ["llm_guidance"] = hint
        });

        var guidance = new JsonArray();
        guidance.Add((JsonNode)JsonValue.Create("Use only MCP servers and tools that were discovered for this workflow.plan run.")!);
        guidance.Add((JsonNode)JsonValue.Create("When discovery is unavailable, avoid generating mcp.call steps that require a catalog contract.")!);

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "validation",
            ["summary"] = "1 diagnostic(s): " + code,
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = guidance
        };

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"{code}: {message} | repair diagnostics: {WorkflowPlanDiagnostics.ToPromptJson(details)}",
            details: details);
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

    private static string FormatValidationErrors(IReadOnlyList<ValidationError> errors) =>
        WorkflowPlanDiagnostics.FormatValidationErrors(errors);
}
