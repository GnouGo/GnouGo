using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core;
using Expressions = GnOuGo.Flow.Core.Expressions;
using Parsing = GnOuGo.Flow.Core.Parsing;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

using static GnOuGo.Flow.Planning.Capabilities.CapabilityArtifactValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractLocking;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryContext;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class PlanningArtifactValidation
{

    internal static string? ReadWorkflowCallRefNameFromInput(StepDef step)
        => step.Input?["ref"] is JsonObject refObj
            ? GetStringProperty(refObj, "name")
            : null;

    internal static string TrimWorkflowExpression(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'))
            return text[2..^1].Trim();
        return text;
    }

    internal static string? GetStringProperty(JsonObject? obj, string name)
    {
        if (obj == null || obj[name] is not JsonValue value)
            return null;

        return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    internal static JsonArray BuildStringArrayJson(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode)JsonValue.Create(value)!);
        return array;
    }
    internal static void EnforcePolicy(WorkflowDocument doc, JsonObject policy)
    {
        var allowed = policy["allowed_step_types"] as JsonArray;
        var denied = policy["denied_step_types"] as JsonArray;
        var allowedSet = allowed?.Select(a => a?.GetValue<string>() ?? "").ToHashSet();
        var deniedSet = denied?.Select(a => a?.GetValue<string>() ?? "").ToHashSet();

        foreach (var step in doc.Workflows.Values.SelectMany(wf => EnumerateSteps(wf.Steps).Concat(EnumerateSteps(wf.Finally))))
        {
            if (allowedSet != null && !allowedSet.Contains(step.Type))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Step type '{step.Type}' not allowed by policy");
            if (deniedSet != null && deniedSet.Contains(step.Type))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Step type '{step.Type}' denied by policy");
        }

        var allowRemote = policy["allow_remote_workflow_refs"]?.GetValue<bool>() ?? false;
        if (!allowRemote)
        {
            foreach (var step in doc.Workflows.Values.SelectMany(wf => EnumerateSteps(wf.Steps).Concat(EnumerateSteps(wf.Finally))))
            {
                if (step.Type == "workflow.call" && step.Input is JsonObject inputObj)
                {
                    var refObj = inputObj["ref"] as JsonObject;
                    if (refObj?["kind"]?.GetValue<string>() == "url")
                        throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                            "Remote workflow references not allowed by policy");
                }
            }
        }
    }

    internal static void EnforceLimits(WorkflowDocument doc, JsonObject limits)
    {
        var maxSteps = limits["max_steps_total"]?.GetValue<int>();
        if (maxSteps.HasValue)
        {
            var totalSteps = doc.Workflows.Values.Sum(wf => CountSteps(wf.Steps) + CountSteps(wf.Finally));
            if (totalSteps > maxSteps.Value)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Total steps ({totalSteps}) exceeds limit ({maxSteps.Value})");
        }
    }

    internal static int CountSteps(List<StepDef> steps)
    {
        var count = steps.Count;
        foreach (var step in steps)
        {
            if (step.Steps != null) count += CountSteps(step.Steps);
            if (step.Branches != null)
                count += step.Branches.Sum(b => CountSteps(b.Steps));
            if (step.Cases != null)
                count += step.Cases.Sum(c => CountSteps(c.Steps));
            if (step.Default != null) count += CountSteps(step.Default);
        }
        return count;
    }

    internal static IEnumerable<StepDef> EnumerateSteps(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Steps != null)
            {
                foreach (var child in EnumerateSteps(step.Steps))
                    yield return child;
            }

            if (step.Branches != null)
            {
                foreach (var child in step.Branches.SelectMany(branch => EnumerateSteps(branch.Steps)))
                    yield return child;
            }

            if (step.Cases != null)
            {
                foreach (var child in step.Cases.SelectMany(@case => EnumerateSteps(@case.Steps)))
                    yield return child;
            }

            if (step.Default != null)
            {
                foreach (var child in EnumerateSteps(step.Default))
                    yield return child;
            }
        }
    }

    internal static string BuildUserTaskBlock(string instruction, string? context)
    {
        var sb = new StringBuilder();
        AppendUserTaskBlock(sb, instruction, context);
        return sb.ToString().TrimEnd();
    }

    internal static void AppendUserTaskBlock(StringBuilder sb, string instruction, string? context)
    {
        AppendPromptSectionStart(sb, "task");
        sb.AppendLine("<user_prompt>");
        sb.AppendLine(instruction);
        sb.AppendLine("</user_prompt>");

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine("<user_context>");
            sb.AppendLine(context);
            sb.AppendLine("</user_context>");
        }

        AppendPromptSectionEnd(sb, "task");
    }

    internal static void AppendPromptSectionStart(StringBuilder sb, string tagName)
        => sb.AppendLine($"<{tagName}>");

    internal static void AppendPromptSectionEnd(StringBuilder sb, string tagName)
        => sb.AppendLine($"</{tagName}>");

    public static Task<IReadOnlyList<PlanningDiagnostic>> ValidateTypedArtifactAsync(
        StepExecutionContext ctx, string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct, IReadOnlyList<PlanningArtifactBinding> bindings)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Validate());
        IReadOnlyList<PlanningDiagnostic> Validate()
        {
            var stage = PlanningValidationStage.RuntimeContracts;
            try
            {
                var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
                    ?? throw new InvalidOperationException("The persisted capability contract is missing.");
                var document = ParseAndValidateGeneratedWorkflow(yaml);
                RunStandardPlanValidationSequence(document, request.Options["policy"] as JsonObject,
                    request.Options["limits"] as JsonObject, preflight.DiscoveredServers, ctx);
                stage = PlanningValidationStage.CapabilityContracts;
                var owners = ResolveTypedArtifactOwners(document, preflight, preparation, bindings);
                ValidateLockedCapabilitiesInDocument(document, preflight, current => stage = current, owners);
                return [];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (ex is Expressions.WorkflowRuntimeException runtime && runtime.Details?["diagnostics"] is JsonArray diagnostics)
                    return diagnostics.OfType<JsonObject>().Select(d => TypedArtifactDiagnostic(d, runtime.Code, stage)).ToArray();
                if (ex is Expressions.WorkflowRuntimeException detailed && detailed.Details is JsonObject details)
                {
                    if (details["redundant_calls"] is JsonArray calls)
                        return calls.OfType<JsonObject>().Select(call => TypedArtifactDiagnostic(new JsonObject
                        {
                            ["step"] = call["step_id"]?.DeepClone(),
                            ["message"] = "This operation invokes an artifact materializer beyond the locked occurrence allowance. Correct the behavior's capability binding; changing arguments cannot turn a producer into another lifecycle action."
                        }, detailed.Code, stage)).ToArray();
                    var finding = details.DeepClone().AsObject(); finding["message"] ??= detailed.Message;
                    if (details["unavailable_capabilities"] is JsonArray missing)
                        finding["message"] = detailed.Message + "\nMissing obligations: " + string.Join("; ", missing.OfType<JsonObject>().Select(c => c["description"]?.ToString() ?? c["id"]?.ToString()));
                    if (details["validation_issue"]?.GetValue<string>() == "conditional_local_decision_inputs_unproven")
                        RetargetTypedDecisionFinding(finding, WorkflowParser.Parse(yaml));
                    return [TypedArtifactDiagnostic(finding, detailed.Code, stage)];
                }
                return [new PlanningDiagnostic(ex is Expressions.WorkflowRuntimeException failure ? failure.Code : "PLANNING_VALIDATION", "$", ex.Message, ValidationStage: stage)];
            }
        }
    }

    internal static IReadOnlyDictionary<StepDef, ResolvedCapability> ResolveTypedArtifactOwners(WorkflowDocument document, CapabilityPreflightResult preflight, PlanningPreparation preparation, IReadOnlyList<PlanningArtifactBinding> bindings)
    {
        var calls = document.Workflows.SelectMany(w => EnumerateSteps(w.Value.Steps).Concat(EnumerateSteps(w.Value.Finally))
            .Select(n => (Workflow: w.Key, Step: n))).ToArray();
        var owners = new Dictionary<StepDef, ResolvedCapability>(ReferenceEqualityComparer.Instance);
        foreach (var binding in bindings)
        {
            var nodes = calls.Where(c => c.Workflow == binding.Workflow && c.Step.Id == binding.Step).ToArray();
            var declared = preparation.Capabilities.Where(c => c.Id == binding.CapabilityId).ToArray();
            var matches = declared.Length == 1 ? preflight.Capabilities.Where(c => c.Resolution == declared[0].Resolution &&
                c.CatalogId == declared[0].CatalogId && c.Server == declared[0].Server && c.Kind == declared[0].Kind && c.Method == declared[0].Method &&
                GetResolvedCapabilityOperationIds(c).ToHashSet(StringComparer.Ordinal).SetEquals(declared[0].OperationIds) &&
                c.RequestBindings.Count == declared[0].RequestBindings.Count && c.RequestBindings.All(b => declared[0].RequestBindings.Any(d => d.Path == b.Path && JsonNode.DeepEquals(d.Value, b.Value)))).ToArray() : [];
            if (nodes.Length != 1 || matches.Length != 1 || owners.ContainsKey(nodes[0].Step) ||
                matches[0].Resolution != "local" && nodes[0].Step.Type != declared[0].StepType || matches[0].Resolution == "mcp" &&
                !McpStepMatchesCapability(nodes[0].Step, matches[0].Server!, matches[0].Kind!, matches[0].Method!, matches[0].RequestBindings))
                throw Invalid(binding.Workflow, binding.Step);
            owners.Add(nodes[0].Step, matches[0]);
        }
        if (calls.FirstOrDefault(c => c.Step.Type == "mcp.call" && !owners.ContainsKey(c.Step)) is { Step: not null } missing)
            throw Invalid(missing.Workflow, missing.Step.Id);
        return owners;

        static Expressions.WorkflowRuntimeException Invalid(string workflow, string step) => new("ARTIFACT_OWNERSHIP_INVALID",
            "Every executable MCP call must have one compiler-derived owner matching its locked capability and request bindings.",
            details: new JsonObject { ["workflow"] = workflow, ["step"] = step });
    }

    internal static void RetargetTypedDecisionFinding(JsonObject finding, WorkflowDocument document)
    {
        if (finding["decision_field"] is not JsonValue field || !field.TryGetValue<string>(out var name)) return;
        var producers = document.Workflows.SelectMany(w => EnumerateSteps(w.Value.Steps).Concat(EnumerateSteps(w.Value.Finally))
            .Where(n => n.Type == "decision.evaluate" && n.Input?["decisions"] is JsonObject fields && fields.ContainsKey(name))
            .Select(n => (Workflow: w.Key, Node: n))).ToArray();
        if (producers.Length != 1) return;
        finding["workflow"] = producers[0].Workflow; finding["step"] = producers[0].Node.Id; finding["field"] = "input.decisions." + name;
        finding["hint"] = "Repair this decision producer's conditions. Bind declared upstream results directly or through provable field projections; an opaque computed object cannot establish which result field depends on an input. Preserve every required permission.";
    }

    internal static PlanningDiagnostic TypedArtifactDiagnostic(JsonObject diagnostic, string fallbackCode, string stage)
    {
        string? Read(string key) => diagnostic[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var parts = new List<string>();
        if (Read("workflow") is { } workflow) parts.Add("workflow:" + workflow);
        if ((Read("step") ?? Read("consumer_step") ?? Read("switch_id")) is { } step) parts.Add("step:" + step);
        var targetField = Read("field") ?? (Read("request_pointer") is { } pointer ? "input.request" + pointer.Replace('/', '.') : null);
        if (targetField is null && Read("switch_id") is not null && Read("validation_issue") == "conditional_decision_lineage_unproven") targetField = "expr";
        if (targetField is { } field) parts.Add("field:" + field);
        var message = Read("message") ?? Read("expected") ?? Read("reason") ?? "The generated artifact violates its contract.";
        if (Read("artifact_kind") is { } kind) message = "Required artifact '" + kind + "': " + message;
        if (Read("invalid_path") is { Length: > 0 } invalidPath) message += "\nInvalid reference: " + invalidPath;
        if (diagnostic["allowed_paths"] is JsonArray paths && paths.Count > 0) message += "\nAllowed paths: " + paths.ToJsonString();
        if ((Read("hint") ?? Read("llm_guidance")) is { Length: > 0 } guidance) message += "\nRepair: " + guidance;
        if (Read("validation_issue") is { } issue) message += "\nContract finding: " + issue;
        if (Read("decision_field") is { } decisionField) message += "\nDeclared decision field: " + decisionField;
        return new(Read("code") ?? fallbackCode, Read("location") ?? (parts.Count == 0 ? "$" : string.Join("/", parts)), message, ValidationStage: stage);
    }

    internal static async Task SaveTypedPreparationResultAsync(StepExecutionContext ctx, string stage, JsonNode? value, CancellationToken ct)
    {
        if (ctx.PreparationCheckpoint is not { } checkpoint || value is null) return;
        checkpoint.ValidatedResults[stage] = value.DeepClone(); checkpoint.Stage = stage;
        if (ctx.PersistPreparation is not null) await ctx.PersistPreparation(ct);
    }

    public static Task<IReadOnlyList<PlanningScenarioResult>> ValidateTypedScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct, JsonObject inputs, JsonObject loopItemSchemas, JsonObject observations)
    {
        var preflight = JsonSerializer.Deserialize(preparation.RuntimeState, TypedContractJsonContext.Default.CapabilityPreflightResult)
            ?? throw new InvalidOperationException("The persisted capability contract is missing.");
        return WorkflowPlanScenarioValidator.ValidateAsync(WorkflowParser.Parse(yaml), BuildDryRunMcpClientFactory(preflight.DiscoveredServers), ct, inputs, loopItemSchemas, observations);
    }

    public static async Task<IReadOnlyList<PlanningDiagnostic>> ValidateTypedCatalogAsync(WorkflowEngine engine, PlanningPreparation preparation, CancellationToken ct)
    {
        var diagnostics = new List<PlanningDiagnostic>();
        var currentSteps = engine.Registry.GetContracts();
        foreach (var (type, contract) in preparation.StepContracts)
            if (contract is not null && (!currentSteps.TryGetValue(type, out var current) ||
                !JsonNode.DeepEquals(contract["input"], current.InputSchema) || !JsonNode.DeepEquals(contract["output"], current.OutputSchema)))
                diagnostics.Add(new("CATALOG_CHANGED", type, "A declared step contract changed. Revise the plan before approval."));
        foreach (var group in preparation.Capabilities.Where(c => c.StepType == "mcp.call").GroupBy(c => c.Server, StringComparer.Ordinal))
        {
            if (engine.McpClientFactory is null || group.Key is null) return [new("CATALOG_UNAVAILABLE", "$", "The current capability catalog is unavailable.")];
            await using var session = await engine.McpClientFactory.GetClientAsync(group.Key, ct);
            var tools = await session.ListToolsAsync(ct);
            var prompts = group.Any(c => c.Kind == "prompt") ? await session.ListPromptsAsync(ct) : [];
            foreach (var capability in group)
            {
                var tool = tools.SingleOrDefault(t => t.Name == capability.Method);
                var prompt = prompts.SingleOrDefault(p => p.Name == capability.Method);
                var current = capability.Kind == "prompt" ? (prompt is null ? null : TypedPromptFingerprint(prompt)) : tool is null ? null : TypedDeclarationFingerprint(tool);
                if (current is null || capability.DeclarationFingerprint is null || current != capability.DeclarationFingerprint)
                    diagnostics.Add(new("CATALOG_CHANGED", capability.Id, "A selected capability's declared contract changed. Revise the plan against the current catalog before approval."));
            }
        }
        return diagnostics;
    }

    internal static string TypedDeclarationFingerprint(McpToolInfo tool) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["description"] = tool.Description,
        ["input"] = tool.InputSchema?.DeepClone(),
        ["output"] = McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone(),
        ["meta"] = tool.Meta?.DeepClone()
    }.ToJsonString())));

    internal static JsonObject TypedPromptInputSchema(McpPromptInfo prompt) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject((prompt.Arguments ?? []).Select(argument => new KeyValuePair<string, JsonNode?>(argument.Name, new JsonObject { ["type"] = "string", ["description"] = argument.Description }))),
        ["required"] = new JsonArray((prompt.Arguments ?? []).Where(argument => argument.Required).Select(argument => (JsonNode?)JsonValue.Create(argument.Name)).ToArray())
    };
    internal static string TypedPromptFingerprint(McpPromptInfo prompt) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.Description + TypedPromptInputSchema(prompt).ToJsonString())));

    internal static WorkflowDocument ParseAndValidateGeneratedWorkflow(string yaml)
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

    internal static void ValidateGeneratedWorkflowForPlan(
        WorkflowDocument generatedDoc, IReadOnlyList<McpServerDiscovery>? discovered, StepExecutorRegistry registry)
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

    internal static void RunStandardPlanValidationSequence(WorkflowDocument document, JsonObject? policy,
        JsonObject? limits, IReadOnlyList<McpServerDiscovery> discovered, StepExecutionContext context)
    {
        if (policy is not null) EnforcePolicy(document, policy);
        if (limits is not null) EnforceLimits(document, limits);
        ValidateGeneratedWorkflowStrongOutputSchemas(document);
        ValidateGeneratedWorkflowForPlan(document, discovered, context.Engine.Registry);
        ValidateMcpDiscoveryCoverage(document, discovered);
    }

    internal static void ValidateGeneratedWorkflowStrongOutputSchemas(WorkflowDocument generatedDoc)
    {
        var diagnostics = new JsonArray();

        if (generatedDoc.Skill?.Outputs != null)
        {
            foreach (var (name, output) in generatedDoc.Skill.Outputs)
                WorkflowOutputContractValidator.CollectWeakOutputSchemaDiagnostics(output, $"skill.outputs.{name}", diagnostics, allowSkillScalarTypeShorthand: true);
        }

        foreach (var (workflowName, workflow) in generatedDoc.Workflows)
        {
            if (workflow.Outputs == null)
                continue;

            foreach (var (name, output) in workflow.Outputs)
                WorkflowOutputContractValidator.CollectWeakOutputSchemaDiagnostics(output, $"workflows.{workflowName}.outputs.{name}", diagnostics, allowSkillScalarTypeShorthand: false);
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["ok"] = false,
            ["phase"] = "output_schema_validation",
            ["summary"] = $"{diagnostics.Count} weak generated output schema diagnostic(s)",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Every generated skill output and workflow output must be strongly typed.")!,
                (JsonNode)JsonValue.Create("Replace any, bare object, and bare array outputs with concrete schemas. Arrays require items; object items require properties.")!)
        };

        var invalidPaths = diagnostics
            .Select(static diagnostic => diagnostic?["location"]?.GetValue<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Take(8)
            .ToArray();

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Generated workflow uses weak output schemas: {string.Join(", ", invalidPaths)} | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    internal static void ValidateMcpDiscoveryCoverage(
        WorkflowDocument generatedDoc, IReadOnlyList<McpServerDiscovery>? discovered)
    {
        var toolCalls = generatedDoc.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
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
    internal static void ThrowMcpDiscoveryCoverageError(string code, string message, string hint)
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

    internal static string? ReadMcpCallInputString(StepDef step, string fieldName)
    {
        return step.Input is JsonObject input
            && input[fieldName] is JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;
    }

    internal static bool IsFatalCompilerValidationError(ValidationError error) =>
        error.Code is ErrorCodes.ExprParse
            or "DSL_VERSION"
            or "NO_WORKFLOWS"
            or ErrorCodes.WorkflowCycleDetected
            or "INVALID_ENTRYPOINT"
            or "DUPLICATE_STEP_ID";

    internal static IReadOnlyList<McpToolOutputContract> BuildMcpToolOutputContracts(IReadOnlyList<McpServerDiscovery>? discovered)
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
                    McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone(),
                    tool.ExampleResponse?.DeepClone()));
            }
        }

        return contracts;
    }

    internal static IMcpClientFactory? BuildDryRunMcpClientFactory(IReadOnlyList<McpServerDiscovery>? discovered)
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
                    Meta = tool.Meta?.DeepClone(),
                    OutputSchema = tool.OutputSchema?.DeepClone(),
                    ExampleResponse = tool.ExampleResponse?.DeepClone(),
                    ArtifactContract = tool.ArtifactContract,
                    CompositionContract = tool.CompositionContract,
                    OutputContract = tool.OutputContract
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
                var outputSchema = McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone();
                var exampleResponse = tool.ExampleResponse?.DeepClone();
                config.ToolHandlers[tool.Name] = _ => new McpCallResult
                {
                    IsError = false,
                    Content = exampleResponse?.DeepClone()
                        ?? WorkflowPlanDryRunValidator.CreateArtifactSample(outputSchema, tool.ArtifactContract?.Contract),
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

    internal static string FormatValidationErrors(IReadOnlyList<ValidationError> errors) =>
        WorkflowPlanDiagnostics.FormatValidationErrors(errors);
}
