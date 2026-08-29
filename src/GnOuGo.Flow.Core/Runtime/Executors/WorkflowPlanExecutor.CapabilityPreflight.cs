using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const string PlatformExternalWriteConfirmationOperationDescription = "Require explicit human confirmation immediately before the first external write.";
    private const string PlatformExternalWriteConfirmationConstraintDescription = "No external write may execute before explicit human confirmation.";

    private sealed record CapabilityClarificationConfig(bool Enabled, int TimeoutMs);
    private sealed record CapabilityClarificationQuestion(string Name, string Description);

    private sealed record CapabilityRequestBinding(string Path, JsonNode? Value);
    private sealed record CapabilityArtifactRequirement(CapabilitySchemaField Field, string Kind);
    private sealed record SharedWriteOccurrence(string OperationId, string CatalogId, bool IsOwnedSource);

    private sealed record CapabilityAlternative(
        string Server,
        string Kind,
        string Method,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings);

    private sealed record CapabilityRequirement(
        string Id,
        string Description,
        bool Required,
        IReadOnlyList<CapabilityAlternative> Alternatives);

    private sealed record CapabilityConstraint(
        string Id,
        string Description,
        bool Required,
        IReadOnlyList<CapabilityAlternative> DeniedAlternatives);

    private sealed record ResolvedCapability(
        string Id,
        string Description,
        bool Required,
        string Resolution,
        string? Server,
        string? Kind,
        string? Method,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings,
        string? OperationId = null,
        string? CatalogId = null,
        string MatchStatus = "matched",
        string? ExecutionKind = null,
        string? ExternalEffectKind = null,
        McpCapabilityActivation? Activation = null,
        string? CapabilityDescription = null,
        IReadOnlyList<string>? OperationIds = null);

    private sealed record CapabilityPreflightResult(
        string Mode,
        IReadOnlyList<McpServerDiscovery> DiscoveredServers,
        IReadOnlyList<ResolvedCapability> Capabilities,
        IReadOnlyList<CapabilityConstraint> Constraints)
    {
        public static CapabilityPreflightResult Off { get; } = new(
            "off",
            Array.Empty<McpServerDiscovery>(),
            Array.Empty<ResolvedCapability>(),
            Array.Empty<CapabilityConstraint>());

        public bool Enabled => !string.Equals(Mode, "off", StringComparison.Ordinal);

        public IReadOnlyList<ResolvedCapability> RequiredMcpCapabilities => Capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "mcp", StringComparison.Ordinal)
                                        && !string.IsNullOrWhiteSpace(capability.Server)
                                        && !string.IsNullOrWhiteSpace(capability.Kind)
                                        && !string.IsNullOrWhiteSpace(capability.Method))
            .ToArray();

        public IReadOnlyList<ResolvedCapability> RequiredNativeCapabilities => Capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
                                        && !string.IsNullOrWhiteSpace(capability.Method))
            .ToArray();

        public IReadOnlyList<ResolvedCapability> RequiredLocalOperations => Capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "local", StringComparison.Ordinal))
            .ToArray();
    }

    private async Task<CapabilityPreflightResult> RunCapabilityPreflightAsync(
        StepExecutionContext ctx,
        JsonObject input,
        IntentClarificationSession? intentClarification,
        CancellationToken ct)
    {
        var preflight = input["capability_preflight"] as JsonObject;
        var mode = preflight?["mode"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "off";
        if (mode == "off")
            return CapabilityPreflightResult.Off;
        if (mode is not ("infer" or "explicit"))
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan capability_preflight mode '{mode}' is invalid. Use off, infer, or explicit.");

        var generator = input["generator"] as JsonObject ?? new JsonObject();
        var instruction = input["raw_prompt"]?.GetValue<string>()
                          ?? generator["raw_prompt"]?.GetValue<string>()
                          ?? generator["instruction"]?.GetValue<string>()
                          ?? string.Empty;
        var generatorContext = generator["context"]?.GetValue<string>() ?? string.Empty;
        _ = ParseCapabilityClarificationConfig(preflight);

        using var span = ctx.BeginTelemetrySpan("workflow.plan.capability_preflight", "capability_preflight", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_preflight.mode", mode)
        });

        try
        {
            var discovered = await DiscoverMcpServersAsync(
                                 ctx.Engine.McpClientFactory,
                                 ctx.Engine.McpCache,
                                 ctx.Engine.Logger,
                                 ctx,
                                 candidateServers: null,
                                 span.Span,
                                 ct)
                             ?? new List<McpServerDiscovery>();

            span.SetAttribute("mcp.servers_total", discovered.Count);
            span.SetAttribute("mcp.servers_discovered", discovered.Count(static server => server.Discovered));
            span.SetAttribute("mcp.tools_total", discovered.Sum(static server => server.Tools.Count));

            IReadOnlyList<ResolvedCapability> resolved;
            IReadOnlyList<CapabilityConstraint> constraints;
            if (mode == "explicit")
            {
                var requirements = ParseExplicitCapabilityRequirements(preflight?["requirements"] as JsonArray);
                constraints = ParseCapabilityConstraints(preflight?["constraints"] as JsonArray);
                ValidateExplicitCapabilityBindings(requirements, constraints, discovered);
                var unresolvedDiscoveryServers = requirements
                    .Where(static requirement => requirement.Required)
                    .Where(requirement => !HasExactDiscoveredAlternative(requirement, discovered))
                    .SelectMany(static requirement => requirement.Alternatives)
                    .Select(alternative => discovered.FirstOrDefault(server => string.Equals(server.Name, alternative.Server, StringComparison.Ordinal)))
                    .Where(static server => server is { Discovered: false })
                    .Select(static server => server!.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (unresolvedDiscoveryServers.Length > 0)
                    ThrowCapabilityPreflightFailure(
                        ErrorCodes.CapabilityPreflightDiscoveryFailed,
                        "Capability requirements cannot be validated because one or more referenced MCP catalogs could not be discovered.",
                        unresolvedDiscoveryServers,
                        Array.Empty<ResolvedCapability>());
                resolved = ResolveExplicitCapabilities(requirements, discovered);
            }
            else
            {
                var failedServers = discovered.Where(static server => !server.Discovered).Select(static server => server.Name).ToArray();
                if (failedServers.Length > 0)
                    ThrowCapabilityPreflightFailure(
                        ErrorCodes.CapabilityPreflightDiscoveryFailed,
                        "Capability inference cannot be complete because one or more configured MCP catalogs could not be discovered.",
                        failedServers,
                        Array.Empty<ResolvedCapability>());

                ValidateDiscoveredArtifactContracts(discovered);

                (resolved, constraints) = await InferCapabilitiesAsync(
                    ctx,
                    input,
                    generator,
                    instruction,
                    generatorContext,
                    discovered,
                    span.Span,
                    intentClarification,
                    ct);
            }

            ValidateResolvedCapabilities(resolved, discovered, ctx, input);
            ValidateCapabilityConstraints(constraints, discovered);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.requirement_count", resolved.Count);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.constraint_count", constraints.Count);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.required_count", resolved.Count(static capability => capability.Required));
            span.SetAttribute("gnougo-flow.plan.capability_preflight.resolved_count", resolved.Count(static capability => !string.Equals(capability.Resolution, "unavailable", StringComparison.Ordinal)));
            span.Complete();

            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                    $"Capability preflight complete: {resolved.Count(static capability => capability.Required)} required operation(s) resolved."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
            });

            return new CapabilityPreflightResult(mode, discovered, resolved, constraints);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkflowPlanClarificationRestartException)
        {
            span.Complete();
            throw;
        }
        catch (WorkflowRuntimeException ex)
        {
            span.Fail(ex);
            throw;
        }
        catch (Exception ex)
        {
            span.Fail(ex);
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability preflight could not produce a valid capability contract.",
                inner: ex,
                details: new JsonObject
                {
                    ["phase"] = "capability_preflight",
                    ["mode"] = mode,
                    ["reason"] = ex.GetType().Name
                });
        }
    }

    private static void ValidateDiscoveredArtifactContracts(
        IReadOnlyList<McpServerDiscovery> discovered)
    {
        foreach (var server in discovered)
        {
            foreach (var tool in server.Tools)
            {
                _ = GetValidatedMcpArtifactContract(tool, server.Name);
                _ = GetValidatedMcpCompositionContract(tool, server.Name);
            }
        }
    }

    private static IReadOnlyList<CapabilityRequirement> ParseExplicitCapabilityRequirements(JsonArray? requirements)
    {
        if (requirements == null || requirements.Count == 0)
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight explicit mode requires at least one requirement.");

        var parsed = new List<CapabilityRequirement>(requirements.Count);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in requirements)
        {
            if (node is not JsonObject requirement)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Each capability_preflight requirement must be an object.");

            var id = requirement["id"]?.GetValue<string>()?.Trim();
            var description = requirement["description"]?.GetValue<string>()?.Trim();
            var required = requirement["required"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Capability requirement ids must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(description))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Capability requirement '{id}' requires a non-empty description.");

            var alternatives = ParseCapabilityAlternatives(
                requirement["alternatives"] as JsonArray,
                $"Capability requirement '{id}'");

            if (required && alternatives.Count == 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Required capability '{id}' must declare at least one alternative.");

            parsed.Add(new CapabilityRequirement(id, description, required, alternatives));
        }

        return parsed;
    }

    private static IReadOnlyList<CapabilityConstraint> ParseCapabilityConstraints(JsonArray? constraints)
    {
        if (constraints == null || constraints.Count == 0)
            return Array.Empty<CapabilityConstraint>();

        var parsed = new List<CapabilityConstraint>(constraints.Count);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in constraints)
        {
            if (node is not JsonObject constraint)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Each capability_preflight constraint must be an object.");

            var id = constraint["id"]?.GetValue<string>()?.Trim();
            var description = constraint["description"]?.GetValue<string>()?.Trim();
            var required = constraint["required"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Capability constraint ids must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(description))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Capability constraint '{id}' requires a non-empty description.");

            var denied = ParseCapabilityAlternatives(
                constraint["denied_alternatives"] as JsonArray,
                $"Capability constraint '{id}'");
            parsed.Add(new CapabilityConstraint(id, description, required, denied));
        }

        return parsed;
    }

    private static IReadOnlyList<CapabilityAlternative> ParseCapabilityAlternatives(JsonArray? nodes, string owner)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<CapabilityAlternative>();

        var alternatives = new List<CapabilityAlternative>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not JsonObject alternative)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} contains an invalid capability alternative.");
            var server = alternative["server"]?.GetValue<string>()?.Trim();
            var kind = alternative["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            var method = alternative["method"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(server) || kind is not ("tool" or "prompt") || string.IsNullOrWhiteSpace(method))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} alternatives require server, tool|prompt kind, and method.");
            var bindings = ParseCapabilityRequestBindings(
                alternative["request_bindings"] as JsonArray,
                $"{owner} alternative '{server}/{method}'");
            if (kind == "prompt" && bindings.Count > 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} prompt alternatives cannot declare request_bindings.");
            alternatives.Add(new CapabilityAlternative(server, kind, method, bindings));
        }

        return alternatives;
    }

    private static IReadOnlyList<CapabilityRequestBinding> ParseCapabilityRequestBindings(JsonArray? nodes, string owner)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<CapabilityRequestBinding>();

        var result = new List<CapabilityRequestBinding>(nodes.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is not JsonObject binding)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} contains an invalid request binding.");
            var path = binding["path"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !IsValidJsonPointer(path) || !paths.Add(path))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} request binding paths must be unique RFC 6901 JSON Pointers.");
            if (!binding.ContainsKey("value") || !IsJsonScalar(binding["value"]))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"{owner} request binding '{path}' must contain a JSON scalar value.");
            result.Add(new CapabilityRequestBinding(path, binding["value"]?.DeepClone()));
        }

        return result;
    }

    private static IReadOnlyList<ResolvedCapability> ResolveExplicitCapabilities(
        IReadOnlyList<CapabilityRequirement> requirements,
        IReadOnlyList<McpServerDiscovery> discovered)
    {
        var resolved = new List<ResolvedCapability>(requirements.Count);
        foreach (var requirement in requirements)
        {
            CapabilityAlternative? match = null;
            foreach (var alternative in requirement.Alternatives)
            {
                var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
                if (server == null || !server.Discovered)
                    continue;
                var exists = alternative.Kind == "prompt"
                    ? server.Prompts.Any(prompt => string.Equals(prompt.Name, alternative.Method, StringComparison.Ordinal))
                    : server.Tools.Any(tool => string.Equals(tool.Name, alternative.Method, StringComparison.Ordinal));
                if (exists && AlternativeBindingsMatchSchema(alternative, server))
                {
                    match = alternative;
                    break;
                }
            }

            resolved.Add(match == null
                ? new ResolvedCapability(requirement.Id, requirement.Description, requirement.Required, "unavailable", null, null, null, Array.Empty<CapabilityRequestBinding>())
                : new ResolvedCapability(requirement.Id, requirement.Description, requirement.Required, "mcp", match.Server, match.Kind, match.Method, match.RequestBindings));
        }

        return resolved;
    }

    private static void ValidateExplicitCapabilityBindings(
        IReadOnlyList<CapabilityRequirement> requirements,
        IReadOnlyList<CapabilityConstraint> constraints,
        IReadOnlyList<McpServerDiscovery> discovered)
    {
        var alternatives = requirements.SelectMany(static requirement => requirement.Alternatives)
            .Concat(constraints.SelectMany(static constraint => constraint.DeniedAlternatives));
        foreach (var alternative in alternatives.Where(static item => item.RequestBindings.Count > 0))
        {
            var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
            if (server?.Discovered != true)
                continue;
            if (!AlternativeBindingsMatchSchema(alternative, server))
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.InputValidation,
                    $"Capability alternative '{alternative.Server}/{alternative.Method}' contains request_bindings that are not documented scalar selectors in the discovered input schema.",
                    details: new JsonObject
                    {
                        ["phase"] = "capability_preflight",
                        ["server"] = alternative.Server,
                        ["kind"] = alternative.Kind,
                        ["method"] = alternative.Method,
                        ["request_bindings"] = BuildRequestBindingsJson(alternative.RequestBindings)
                    });
            }
        }
    }

    private static bool HasExactDiscoveredAlternative(
        CapabilityRequirement requirement,
        IReadOnlyList<McpServerDiscovery> discovered)
    {
        foreach (var alternative in requirement.Alternatives)
        {
            var server = discovered.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
            if (server?.Discovered != true)
                continue;
            var exists = alternative.Kind == "prompt"
                ? server.Prompts.Any(prompt => string.Equals(prompt.Name, alternative.Method, StringComparison.Ordinal))
                : server.Tools.Any(tool => string.Equals(tool.Name, alternative.Method, StringComparison.Ordinal));
            if (exists && AlternativeBindingsMatchSchema(alternative, server))
                return true;
        }
        return false;
    }

    private async Task<(IReadOnlyList<ResolvedCapability> Capabilities, IReadOnlyList<CapabilityConstraint> Constraints)> InferCapabilitiesAsync(
        StepExecutionContext ctx,
        JsonObject input,
        JsonObject generator,
        string instruction,
        string generatorContext,
        IReadOnlyList<McpServerDiscovery> discovered,
        ITelemetrySpan? parentSpan,
        IntentClarificationSession? intentClarification,
        CancellationToken ct,
        bool clarificationAllowed = true)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.CapabilityPreflightInferenceFailed, "Capability inference requires an LLM client.");
        var (provider, resolvedModel) = ctx.Engine.ResolveLlmTarget(
            generator["provider"]?.GetValue<string>(),
            generator["model"]?.GetValue<string>());
        var model = resolvedModel ?? "gpt-4";
        var reasoning = generator["reasoning"]?.GetValue<string>() ?? "low";
        var allowedNativeTypes = ResolveAllowedNativeStepTypes(ctx, input);

        using var inferenceSpan = ctx.BeginTelemetrySpan(parentSpan!, "workflow.plan.capability_preflight.infer", "capability_preflight_infer", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_catalog.full_server_count", discovered.Count),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_catalog.full_tool_count", discovered.Sum(static server => server.Tools.Count))
        });

        var inferencePhase = "capability_inventory_call";
        try
        {
            var inventoryResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityInventoryPrompt(instruction, generatorContext),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildCapabilityInventorySchema(),
                StructuredOutputStrict = true
            }, "workflow.plan.capability_inventory", ct);
            AddUsageAttributes(inferenceSpan, inventoryResponse.Usage, model, provider);
            inferencePhase = "capability_inventory_parse";
            CapabilityInventory inventory;
            try
            {
                inventory = RemovePlannerBoundaryArtifacts(
                    ParseCapabilityInventory(ParseStructuredObject(inventoryResponse, "operation inventory")),
                    instruction);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                inventory = new CapabilityInventory(
                    false,
                    Array.Empty<CapabilityInventoryOperation>(),
                    Array.Empty<CapabilityInventoryConstraint>(),
                    [new CapabilityInventoryIncompleteReason(
                        "inventory_contract_invalid",
                        SanitizeCapabilityInferenceDiagnostic(ex.Message, 1_000))]);
            }
            if (!inventory.Complete)
            {
                inferenceSpan.SetAttribute("gnougo-flow.plan.capability_inventory.repair_attempted", true);
                inferenceSpan.SetAttribute("gnougo-flow.plan.capability_inventory.initial_reason_count", inventory.IncompleteReasons.Count);
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        "Capability inventory was incomplete; performing one bounded repair attempt."),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                inferencePhase = "capability_inventory_repair_call";
                var repairedInventoryResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
                {
                    Provider = provider,
                    Model = model,
                    Prompt = BuildCapabilityInventoryRepairPrompt(instruction, generatorContext, inventory),
                    Reasoning = reasoning,
                    UseBackgroundMode = true,
                    StructuredOutputSchema = BuildCapabilityInventorySchema(),
                    StructuredOutputStrict = true
                }, "workflow.plan.capability_inventory_repair", ct);
                AddUsageAttributes(inferenceSpan, repairedInventoryResponse.Usage, model, provider);
                inferencePhase = "capability_inventory_repair_parse";
                try
                {
                    inventory = RemovePlannerBoundaryArtifacts(
                        ParseCapabilityInventory(ParseStructuredObject(repairedInventoryResponse, "operation inventory repair")),
                        instruction);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    inventory = new CapabilityInventory(
                        false,
                        Array.Empty<CapabilityInventoryOperation>(),
                        Array.Empty<CapabilityInventoryConstraint>(),
                        [new CapabilityInventoryIncompleteReason(
                            "inventory_contract_invalid",
                            SanitizeCapabilityInferenceDiagnostic(ex.Message, 1_000))]);
                }
                if (!inventory.Complete)
                {
                    if (clarificationAllowed
                        && intentClarification != null
                        && IsInventoryClarificationEligible(inventory))
                    {
                        await RequestReactiveIntentClarificationAsync(
                            ctx,
                            input,
                            intentClarification,
                            "capability_inventory",
                            BuildCapabilityClarificationContext(inventory, evaluation: null, catalog: null),
                            ct);
                        throw new WorkflowPlanClarificationRestartException();
                    }

                    if (clarificationAllowed
                        && intentClarification == null
                        && IsInventoryClarificationEligible(inventory)
                        && ParseCapabilityClarificationConfig(input["capability_preflight"] as JsonObject).Enabled)
                    {
                        var clarification = await RequestCapabilityClarificationAsync(
                            ctx,
                            input,
                            inventory,
                            evaluation: null,
                            catalog: null,
                            ct);
                        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_clarification.requested", true);
                        inferenceSpan.Complete();
                        return await InferCapabilitiesAsync(
                            ctx,
                            input,
                            generator,
                            AppendCapabilityClarification(instruction, clarification),
                            generatorContext,
                            discovered,
                            parentSpan,
                            intentClarification,
                            ct,
                            clarificationAllowed: false);
                    }

                    ThrowIncompleteCapabilityInventory(inventory);
                }
            }
            else
            {
                inferenceSpan.SetAttribute("gnougo-flow.plan.capability_inventory.repair_attempted", false);
            }

            inventory = ApplyDefaultExternalWriteConfirmation(inventory, instruction);

            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_inventory.operation_count", inventory.Operations.Count);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_inventory.constraint_count", inventory.Constraints.Count);

            inferencePhase = "physical_capability_candidate_selection";
            var matchingDiscovery = IsCapabilityCandidateSelectionEnabled(generator)
                ? await SelectPhysicalCapabilityCandidatesAsync(
                    llmClient,
                    inventory,
                    discovered,
                    instruction,
                    generatorContext,
                    provider,
                    model,
                    reasoning,
                    ctx,
                    inferenceSpan,
                    ct)
                : discovered.Select(CloneDiscovery).ToList();

            inferencePhase = "capability_catalog_expansion";
            var catalog = BuildSchemaAwareCapabilityCatalog(matchingDiscovery, allowedNativeTypes, discovered);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_catalog.entry_count", catalog.Entries.Count);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_catalog.character_count", catalog.Text.Length);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_catalog.selected_server_count", matchingDiscovery.Count);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_catalog.selected_tool_count", matchingDiscovery.Sum(static server => server.Tools.Count));
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_catalog.selected_prompt_count", matchingDiscovery.Sum(static server => server.Prompts.Count));

            inferencePhase = "capability_matching_call";
            var matchingResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityMatchingPrompt(inventory, catalog),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildCapabilityMatchingSchema(),
                StructuredOutputStrict = true
            }, "workflow.plan.capability_matching", ct);
            AddUsageAttributes(inferenceSpan, matchingResponse.Usage, model, provider);
            inferencePhase = "capability_matching_parse";
            CapabilityMatchingEvaluation evaluation;
            try
            {
                evaluation = ParseCapabilityMatchingEvaluation(
                    ParseStructuredObject(matchingResponse, "capability matching"), inventory, catalog);
                evaluation = NormalizeLocalProcessingMatches(evaluation);
                evaluation = NormalizeCapabilityCompositionMatches(evaluation, catalog);
                evaluation = NormalizeExactSelectorMatches(evaluation, catalog);
                evaluation = NormalizeConditionalSelectorMatches(evaluation, catalog, inventory);
                evaluation = EnforceCapabilityPrerequisiteClosure(evaluation, catalog, instruction);
                evaluation = NormalizePlatformSafetyMatches(evaluation, catalog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                evaluation = BuildMalformedCapabilityMatchingEvaluation(inventory, ex.Message);
            }
            var repairRequired = RequiresCapabilityMatchingRepair(evaluation);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_matching.repair_attempted", repairRequired);
            if (repairRequired)
            {
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        "Capability matching contained unresolved operation decisions; performing one bounded repair attempt."),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });
                inferencePhase = "capability_matching_repair_call";
                var repairedMatchingResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
                {
                    Provider = provider,
                    Model = model,
                    Prompt = BuildCapabilityMatchingRepairPrompt(inventory, catalog, evaluation),
                    Reasoning = reasoning,
                    UseBackgroundMode = true,
                    StructuredOutputSchema = BuildCapabilityMatchingSchema(),
                    StructuredOutputStrict = true
                }, "workflow.plan.capability_matching_repair", ct);
                AddUsageAttributes(inferenceSpan, repairedMatchingResponse.Usage, model, provider);
                inferencePhase = "capability_matching_repair_parse";
                CapabilityMatchingEvaluation repaired;
                try
                {
                    repaired = ParseCapabilityMatchingEvaluation(
                        ParseStructuredObject(repairedMatchingResponse, "capability matching repair"), inventory, catalog);
                    repaired = NormalizeLocalProcessingMatches(repaired);
                    repaired = NormalizeCapabilityCompositionMatches(repaired, catalog);
                    repaired = NormalizeExactSelectorMatches(repaired, catalog);
                    repaired = NormalizeConditionalSelectorMatches(repaired, catalog, inventory);
                    repaired = EnforceCapabilityPrerequisiteClosure(repaired, catalog, instruction);
                    repaired = NormalizePlatformSafetyMatches(repaired, catalog);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    repaired = BuildMalformedCapabilityMatchingEvaluation(inventory, ex.Message);
                }
                evaluation = PreserveValidCapabilityMatches(evaluation, repaired);
            }

            if (clarificationAllowed
                && intentClarification != null
                && IsMatchingClarificationEligible(evaluation))
            {
                await RequestReactiveIntentClarificationAsync(
                    ctx,
                    input,
                    intentClarification,
                    "capability_matching",
                    BuildCapabilityClarificationContext(inventory, evaluation, catalog),
                    ct);
                throw new WorkflowPlanClarificationRestartException();
            }

            if (clarificationAllowed
                && intentClarification == null
                && IsMatchingClarificationEligible(evaluation)
                && ParseCapabilityClarificationConfig(input["capability_preflight"] as JsonObject).Enabled)
            {
                var clarification = await RequestCapabilityClarificationAsync(
                    ctx,
                    input,
                    inventory,
                    evaluation,
                    catalog,
                    ct);
                inferenceSpan.SetAttribute("gnougo-flow.plan.capability_clarification.requested", true);
                inferenceSpan.Complete();
                return await InferCapabilitiesAsync(
                    ctx,
                    input,
                    generator,
                    AppendCapabilityClarification(instruction, clarification),
                    generatorContext,
                    discovered,
                    parentSpan,
                    intentClarification,
                    ct,
                    clarificationAllowed: false);
            }

            ThrowForUnresolvedCapabilityMatches(evaluation, catalog, repairRequired);
            var (resolved, constraints) = ResolveCapabilityMatches(evaluation, catalog);

            inferenceSpan.Complete();
            return (resolved, constraints);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkflowPlanClarificationRestartException)
        {
            inferenceSpan.Complete();
            throw;
        }
        catch (WorkflowRuntimeException ex)
        {
            inferenceSpan.Fail(ex);
            throw;
        }
        catch (Exception ex)
        {
            inferenceSpan.Fail(ex);
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability inference returned an invalid or incomplete contract.",
                inner: ex,
                details: new JsonObject
                {
                    ["phase"] = "capability_inference",
                    ["inference_phase"] = inferencePhase,
                    ["inference_error"] = SanitizeCapabilityInferenceDiagnostic(ex.Message, 1_000),
                    ["reason"] = ex.GetType().Name
                });
        }
    }

    private static CapabilityMatchingEvaluation EnforceCapabilityPrerequisiteClosure(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog,
        string userInstruction)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var operationMatches = new List<CapabilityOperationMatch>(evaluation.OperationMatches.Count);
        var closureIssues = new List<CapabilityMatchingIssue>();

        foreach (var match in evaluation.OperationMatches)
        {
            if (match.Status is not ("matched" or "composed" or "conditional"))
            {
                operationMatches.Add(match);
                continue;
            }

            var selected = match.CatalogIds
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            var missing = selected
                .SelectMany(GetRequiredArtifactRequirements)
                .Where(item => !IsExplicitCallerArtifactInput(userInstruction, item.Field, item.Kind))
                .Where(item => !selected.Any(entry => CapabilityProducesArtifactKind(entry, item.Kind)))
                .GroupBy(static item => item.Kind, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            if (missing.Length == 0)
            {
                operationMatches.Add(match);
                continue;
            }

            var producerCandidates = catalog.Entries
                .Where(static entry => string.Equals(entry.Resolution, "mcp", StringComparison.Ordinal))
                .Where(entry => missing.Any(item => CapabilityProducesArtifactKind(entry, item.Kind!)))
                .Where(entry => missing.All(item => !CapabilityRequiresArtifactKind(entry, item.Kind!)))
                .Select(static entry => entry.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(7)
                .ToArray();
            var candidates = match.CatalogIds
                .Concat(producerCandidates)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            var fields = string.Join(", ", missing.Select(static item => item.Field.Path));
            var status = producerCandidates.Length == 0 ? "unavailable" : "ambiguous";
            var reason = producerCandidates.Length == 0
                ? $"The selected capability composition requires an existing operational artifact at {fields}, but the user did not declare it as a runtime input and no discovered producer exposes a compatible output."
                : $"The selected capability composition requires an existing operational artifact at {fields}, but it contains no compatible producer. Compose the selected capability with one of the discovered producer candidates.";
            var repairedMatch = match with
            {
                Status = status,
                Reason = reason,
                CatalogIds = Array.Empty<string>(),
                CandidateCatalogIds = producerCandidates.Length == 0 ? Array.Empty<string>() : candidates
            };
            operationMatches.Add(repairedMatch);
            closureIssues.Add(new CapabilityMatchingIssue(
                match.Operation.Id,
                match.Operation.Description,
                match.Operation.Required,
                status,
                reason,
                repairedMatch.CandidateCatalogIds));
        }

        var replacedOperationIds = closureIssues
            .Select(static issue => issue.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var issues = evaluation.Issues
            .Where(issue => !replacedOperationIds.Contains(issue.OperationId))
            .Concat(closureIssues)
            .ToArray();
        return evaluation with { OperationMatches = operationMatches, Issues = issues };
    }

    private static CapabilityMatchingEvaluation NormalizeCapabilityCompositionMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var normalizedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operationMatches = evaluation.OperationMatches.Select(match =>
        {
            var referenced = match.CatalogIds.Concat(match.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            if (referenced.Length == 0)
                return match;

            var wrappers = referenced
                .Where(static entry => entry.CompositionContract is
                {
                    Kind: McpCapabilityCompositionConventions.CompleteOperationKind,
                    Encapsulates.Count: > 0
                })
                .ToArray();
            if (wrappers.Length != 1)
                return match;

            var wrapper = wrappers[0];
            var requiredKinds = GetRequiredArtifactRequirements(wrapper)
                .Select(static requirement => requirement.Kind)
                .ToHashSet(StringComparer.Ordinal);
            var retainedProducers = referenced
                .Where(candidate => !string.Equals(candidate.Id, wrapper.Id, StringComparison.Ordinal))
                .Where(candidate => requiredKinds.Any(kind => CapabilityProducesArtifactKind(candidate, kind)))
                .ToArray();
            var unrelated = referenced
                .Where(candidate => !string.Equals(candidate.Id, wrapper.Id, StringComparison.Ordinal))
                .Where(candidate => retainedProducers.All(producer => !string.Equals(producer.Id, candidate.Id, StringComparison.Ordinal)))
                .Where(candidate => !string.Equals(candidate.Resolution, "mcp", StringComparison.Ordinal)
                                    || !string.Equals(candidate.Server, wrapper.Server, StringComparison.Ordinal)
                                    || !wrapper.CompositionContract!.Encapsulates.Any(encapsulated =>
                                        string.Equals(encapsulated.Kind, candidate.Kind, StringComparison.Ordinal)
                                        && string.Equals(encapsulated.Method, candidate.Method, StringComparison.Ordinal)))
                .ToArray();
            if (unrelated.Length > 0 && match.Status is not ("ambiguous" or "invalid"))
                return match;

            var normalizedIds = new[] { wrapper.Id }
                .Concat(retainedProducers.Select(static producer => producer.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            normalizedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = normalizedIds.Length == 1 ? "matched" : "composed",
                Reason = "The selected complete-operation capability encapsulates the referenced lower-level phases.",
                CatalogIds = normalizedIds,
                CandidateCatalogIds = Array.Empty<string>()
            };
        }).ToArray();

        if (normalizedOperationIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues
            .Where(issue => !normalizedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operationMatches.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operationMatches,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    private static CapabilityMatchingEvaluation NormalizeLocalProcessingMatches(
        CapabilityMatchingEvaluation evaluation)
    {
        var normalizedOperationIds = evaluation.OperationMatches
            .Where(static match => string.Equals(
                match.Operation.ExecutionKind,
                "local_processing",
                StringComparison.Ordinal))
            .Select(static match => match.Operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (normalizedOperationIds.Count == 0)
            return evaluation;

        var operations = evaluation.OperationMatches.Select(match =>
            normalizedOperationIds.Contains(match.Operation.Id)
                ? match with
                {
                    Status = "local",
                    Reason = "The locked inventory classifies this operation as provider-neutral local processing.",
                    CatalogIds = Array.Empty<string>(),
                    CandidateCatalogIds = Array.Empty<string>(),
                    DecisionOperationId = null
                }
                : match).ToArray();
        var issues = evaluation.Issues
            .Where(issue => !normalizedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operations.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operations,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    private static CapabilityMatchingEvaluation NormalizeExactSelectorMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var normalizedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operationMatches = evaluation.OperationMatches.Select(match =>
        {
            if (match.Status is not ("composed" or "ambiguous" or "invalid"))
                return match;

            var referenced = match.CatalogIds.Concat(match.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            var referencedPhysicalCapabilities = referenced
                .Select(static entry => (entry.Resolution, entry.Server, entry.Kind, entry.Method))
                .ToHashSet();
            var exactSelectors = catalog.Entries
                .Where(entry => referencedPhysicalCapabilities.Contains((entry.Resolution, entry.Server, entry.Kind, entry.Method)))
                .Where(static entry => entry.RequestBindings.Count > 0)
                .Where(entry => SelectorBindingsMatchOperationIntent(entry.RequestBindings, match.Operation.Description))
                .ToArray();
            if (exactSelectors.Length == 0)
                return match;

            var maximumSpecificity = exactSelectors.Max(static entry => entry.RequestBindings.Count);
            exactSelectors = exactSelectors
                .Where(entry => entry.RequestBindings.Count == maximumSpecificity)
                .ToArray();
            if (exactSelectors.Length != 1)
                return match;

            var selector = exactSelectors[0];
            var requiredKinds = GetRequiredArtifactRequirements(selector)
                .Select(static requirement => requirement.Kind)
                .ToHashSet(StringComparer.Ordinal);
            var retainedProducers = referenced
                .Where(candidate => !string.Equals(candidate.Id, selector.Id, StringComparison.Ordinal))
                .Where(candidate => requiredKinds.Any(kind => CapabilityProducesArtifactKind(candidate, kind)))
                .ToArray();
            var normalizedIds = new[] { selector.Id }
                .Concat(retainedProducers.Select(static producer => producer.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            normalizedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = normalizedIds.Length == 1 ? "matched" : "composed",
                Reason = "The exact documented selector satisfies the operation; unrelated whole-tool candidates were removed.",
                CatalogIds = normalizedIds,
                CandidateCatalogIds = Array.Empty<string>()
            };
        }).ToArray();

        if (normalizedOperationIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues
            .Where(issue => !normalizedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operationMatches.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operationMatches,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    private static CapabilityMatchingEvaluation NormalizeConditionalSelectorMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog,
        CapabilityInventory inventory)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var normalizedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operationMatches = evaluation.OperationMatches.Select(match =>
        {
            if (match.Status is "matched" or "conditional" or "local")
                return match;
            if (match.Operation.DecisionSourceOperationId.Length == 0)
                return match;

            var referenced = match.CatalogIds.Concat(match.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            if (match.CatalogIds.Count < 2
                || !TryBuildConditionalCompositionBranchValues(referenced, out _))
                return match;

            var decisionOperationId = match.Operation.DecisionSourceOperationId;
            if (inventory.Operations.All(operation => !string.Equals(
                    operation.Id,
                    decisionOperationId,
                    StringComparison.Ordinal)))
                return match;

            normalizedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = "conditional",
                Reason = "The exact selector subset contains mutually exclusive runtime branches selected by an earlier workflow result; complementary selected capabilities remain unconditional prerequisites.",
                CatalogIds = referenced.Select(static entry => entry.Id).ToArray(),
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = decisionOperationId
            };
        }).ToArray();

        if (normalizedOperationIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues
            .Where(issue => !normalizedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operationMatches.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operationMatches,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    private static CapabilityMatchingEvaluation NormalizePlatformSafetyMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog)
    {
        var confirmation = catalog.Entries.FirstOrDefault(static entry =>
            string.Equals(entry.Resolution, "native", StringComparison.Ordinal)
            && string.Equals(entry.Method, "human.input", StringComparison.Ordinal));
        if (confirmation == null)
            return evaluation;

        var normalizedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operations = evaluation.OperationMatches.Select(match =>
        {
            var isPlatformConfirmation = match.Operation.Id.StartsWith(
                                             "platform_confirm_external_write",
                                             StringComparison.Ordinal)
                                         && string.Equals(
                                             match.Operation.Description,
                                             PlatformExternalWriteConfirmationOperationDescription,
                                             StringComparison.Ordinal);
            var isDeclaredHumanInteraction = string.Equals(
                match.Operation.ExecutionKind,
                "human_interaction",
                StringComparison.Ordinal);
            if (!isPlatformConfirmation && !isDeclaredHumanInteraction)
                return match;

            normalizedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = "matched",
                Reason = isPlatformConfirmation
                    ? "The platform-owned external-write safety gate uses the registered native human.input step."
                    : "The locked inventory classifies this operation as provider-neutral human interaction implemented by the registered native human.input step.",
                CatalogIds = [confirmation.Id],
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = null
            };
        }).ToArray();

        var normalizedConstraintIds = new HashSet<string>(StringComparer.Ordinal);
        var constraints = evaluation.ConstraintMatches.Select(match =>
        {
            if (!match.Constraint.Id.StartsWith("platform_external_write_after_confirmation", StringComparison.Ordinal)
                || !string.Equals(
                    match.Constraint.Description,
                    PlatformExternalWriteConfirmationConstraintDescription,
                    StringComparison.Ordinal))
            {
                return match;
            }

            normalizedConstraintIds.Add(match.Constraint.Id);
            return match with
            {
                Status = "policy_only",
                Reason = "The platform-owned confirmation ordering rule is enforced by workflow topology.",
                DeniedCatalogIds = Array.Empty<string>(),
                CandidateCatalogIds = Array.Empty<string>()
            };
        }).ToArray();

        if (normalizedOperationIds.Count == 0 && normalizedConstraintIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues.Where(issue =>
                !normalizedOperationIds.Contains(issue.OperationId)
                && !normalizedConstraintIds.Contains(issue.OperationId))
            .ToArray();
        var contractValid = operations.All(static match => match.Status != "invalid")
                            && constraints.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status != "invalid");
        return evaluation with
        {
            OperationMatches = operations,
            ConstraintMatches = constraints,
            Issues = issues,
            ContractValid = contractValid
        };
    }

    private static bool SelectorBindingsMatchOperationIntent(
        IReadOnlyList<CapabilityRequestBinding> bindings,
        string operationDescription)
    {
        var selectorText = string.Join(' ', bindings
            .Select(static binding => binding.Value is JsonValue value && value.TryGetValue<string>(out var text)
                ? text.Replace('_', ' ').Replace('-', ' ')
                : string.Empty));
        var selectorTokens = ExtractIntentTokens(selectorText);
        if (selectorTokens.Count == 0)
            return false;
        var operationTokens = ExtractIntentTokens(operationDescription.Replace('_', ' ').Replace('-', ' '));
        return selectorTokens.All(operationTokens.Contains);
    }

    private static CapabilityClarificationConfig ParseCapabilityClarificationConfig(JsonObject? preflight)
    {
        if (preflight?["clarification"] is null)
            return new CapabilityClarificationConfig(false, HumanInputContract.DefaultTimeoutMs);
        if (preflight["clarification"] is not JsonObject clarification)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight.clarification must be an object.");
        }

        var enabled = clarification["enabled"] switch
        {
            null => false,
            JsonValue value when value.TryGetValue<bool>(out var parsed) => parsed,
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight.clarification.enabled must be a boolean.")
        };
        var timeoutMs = clarification["timeout_ms"] switch
        {
            null => HumanInputContract.DefaultTimeoutMs,
            JsonValue value when value.TryGetValue<int>(out var parsed) && parsed > 0 => parsed,
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan capability_preflight.clarification.timeout_ms must be a positive 32-bit integer.")
        };
        return new CapabilityClarificationConfig(enabled, timeoutMs);
    }

    private static bool IsInventoryClarificationEligible(CapabilityInventory inventory)
        => !inventory.Complete
           && inventory.IncompleteReasons.Count > 0
           && inventory.IncompleteReasons.All(static reason =>
               !string.Equals(reason.Id, "inventory_contract_invalid", StringComparison.Ordinal));

    private static bool IsMatchingClarificationEligible(CapabilityMatchingEvaluation evaluation)
    {
        var blocking = evaluation.Issues.Where(static issue => issue.Required).ToArray();
        return evaluation.ContractValid
               && blocking.Length > 0
               && blocking.All(static issue => issue.Status == "ambiguous");
    }

    private static string AppendCapabilityClarification(string instruction, JsonObject clarification)
    {
        var payload = clarification.ToJsonString();
        return $"{instruction}\n\n<user_capability_clarification_json>\n{payload}\n</user_capability_clarification_json>";
    }

    private static async Task<JsonObject> RequestCapabilityClarificationAsync(
        StepExecutionContext ctx,
        JsonObject input,
        CapabilityInventory inventory,
        CapabilityMatchingEvaluation? evaluation,
        CapabilityCatalog? catalog,
        CancellationToken ct)
    {
        var config = ParseCapabilityClarificationConfig(input["capability_preflight"] as JsonObject);
        var provider = ctx.Engine.HumanInputProvider
            ?? throw BuildCapabilityClarificationFailure(
                "clarification_provider_unavailable",
                "Capability inference needs user clarification, but no human-input provider is configured.");

        var context = BuildCapabilityClarificationContext(inventory, evaluation, catalog);
        var questions = BuildCapabilityClarificationQuestions(inventory, evaluation);
        var request = new HumanInputRequest
        {
            RunId = string.IsNullOrWhiteSpace(ctx.Limits.RunId) ? Guid.NewGuid().ToString("N") : ctx.Limits.RunId!,
            StepId = $"{ctx.Step.Id}:capability_clarification:{Guid.NewGuid():N}",
            Prompt = "Clarify every unresolved design-time aspect in this single form. Define runtime rules and decision sources, but never predict a result that will only be known while the workflow runs. You may state that planning should be abandoned if the requested behavior is unsupported or unsafe.",
            Mode = HumanInputContract.ModeForm,
            Context = context,
            Fields = questions.Select(static question => new HumanInputFieldDef
            {
                Name = question.Name,
                Type = "textarea",
                Required = true,
                Description = question.Description
            }).ToList(),
            TimeoutMs = config.TimeoutMs,
            AllowAbandon = true
        };

        var requestPayload = HumanInputContract.BuildRequestPayload(request);
        ctx.AddTelemetryEvent("gnougo-flow.step.waiting_for_human", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.prompt", request.Prompt),
            new KeyValuePair<string, object?>("gnougo-flow.human.request", requestPayload.ToJsonString()),
            new KeyValuePair<string, object?>("gnougo-flow.human.purpose", "capability_clarification")
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);
        JsonNode? response;
        try
        {
            response = await provider.RequestInputAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw BuildCapabilityClarificationFailure(
                "clarification_timeout",
                $"Capability clarification timed out after {config.TimeoutMs}ms.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BuildCapabilityClarificationFailure(
                "clarification_provider_failed",
                "Capability clarification failed closed because the human-input provider returned an error.",
                ex);
        }

        if (HumanInputContract.IsAbandoned(response))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.WorkflowPlanAborted,
                "Workflow planning was explicitly abandoned by the user.",
                details: new JsonObject
                {
                    ["planning_outcome"] = "aborted",
                    ["clarification_stage"] = "capability_preflight",
                    ["recommended_action"] = "none"
                });
        }

        if (response is not JsonObject responseObject)
        {
            throw BuildCapabilityClarificationFailure(
                "clarification_invalid_response",
                "Capability clarification must return the complete structured form.");
        }

        var answers = new JsonObject();
        var totalCharacters = 0;
        foreach (var question in questions)
        {
            var answer = responseObject[question.Name] is JsonValue value
                         && value.TryGetValue<string>(out var text)
                ? text.Trim()
                : string.Empty;
            if (answer.Length == 0 || answer.Length > 8_000)
            {
                throw BuildCapabilityClarificationFailure(
                    "clarification_invalid_response",
                    $"Capability clarification field '{question.Name}' must contain a non-empty answer of at most 8000 characters.");
            }

            totalCharacters += answer.Length;
            answers[question.Name] = answer;
        }
        if (totalCharacters > 32_000)
        {
            throw BuildCapabilityClarificationFailure(
                "clarification_invalid_response",
                "Capability clarification exceeds the 32000-character total limit.");
        }

        ctx.AddTelemetryEvent("gnougo-flow.step.human_input_resumed", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.run_id", request.RunId),
            new KeyValuePair<string, object?>("gnougo-flow.human.step_id", request.StepId),
            new KeyValuePair<string, object?>("gnougo-flow.human.purpose", "capability_clarification")
        });
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Capability clarification received; restarting bounded inference once."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
        });
        return answers;
    }

    private static IReadOnlyList<CapabilityClarificationQuestion> BuildCapabilityClarificationQuestions(
        CapabilityInventory inventory,
        CapabilityMatchingEvaluation? evaluation)
    {
        var questions = new List<CapabilityClarificationQuestion>();
        if (evaluation == null)
        {
            var index = 1;
            foreach (var reason in inventory.IncompleteReasons)
            {
                questions.Add(new CapabilityClarificationQuestion(
                    $"unresolved_intent_{index++}",
                    $"Clarify this missing design-time intent: {SanitizeCapabilityInferenceDiagnostic(reason.Description, 1_000)}"));
            }
        }
        else
        {
            var index = 1;
            foreach (var issue in evaluation.Issues
                         .Where(static issue => issue.Required && issue.Status == "ambiguous"))
            {
                questions.Add(new CapabilityClarificationQuestion(
                    $"unresolved_choice_{index++}",
                    $"For this unresolved requirement, specify the intended implementation or runtime rule using the candidates shown in context: {SanitizeCapabilityInferenceDiagnostic(issue.Description, 1_000)}"));
            }
        }

        questions.AddRange([
            new CapabilityClarificationQuestion(
                "intended_outcome_and_scope",
                "State the observable final outcome, required scope, and anything explicitly outside scope."),
            new CapabilityClarificationQuestion(
                "runtime_decision_rules",
                "Identify choices that depend on future runtime data and the earlier result that must drive each choice. Describe the rule, not the future result."),
            new CapabilityClarificationQuestion(
                "external_effect_boundaries",
                "List allowed external reads and writes, the condition for each write, and any external effect that must never occur."),
            new CapabilityClarificationQuestion(
                "success_criteria",
                "State measurable completion, quality, coverage, cardinality, ordering, and exclusivity criteria that the workflow must enforce."),
            new CapabilityClarificationQuestion(
                "failure_policy",
                "State what should happen when planning or execution cannot safely satisfy a required capability: stop, ask again, retry, compensate, or abandon.")
        ]);

        return questions;
    }

    private static JsonObject BuildCapabilityClarificationContext(
        CapabilityInventory inventory,
        CapabilityMatchingEvaluation? evaluation,
        CapabilityCatalog? catalog)
    {
        if (evaluation == null)
        {
            return new JsonObject
            {
                ["phase"] = "capability_inventory",
                ["planning_outcome"] = "clarification_required",
                ["issues"] = new JsonArray(inventory.IncompleteReasons.Select(static reason => (JsonNode)new JsonObject
                {
                    ["id"] = SanitizeCapabilityInferenceDiagnostic(reason.Id, 160),
                    ["description"] = SanitizeCapabilityInferenceDiagnostic(reason.Description, 1_000)
                }).ToArray())
            };
        }

        var entries = catalog!.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        return new JsonObject
        {
            ["phase"] = "capability_matching",
            ["planning_outcome"] = "clarification_required",
            ["issues"] = new JsonArray(evaluation.Issues
                .Where(static issue => issue.Required && issue.Status == "ambiguous")
                .Select(issue => (JsonNode)new JsonObject
                {
                    ["id"] = SanitizeCapabilityInferenceDiagnostic(issue.OperationId, 160),
                    ["description"] = SanitizeCapabilityInferenceDiagnostic(issue.Description, 1_000),
                    ["reason"] = SanitizeCapabilityInferenceDiagnostic(issue.Reason, 1_000),
                    ["candidate_capabilities"] = new JsonArray(issue.CandidateCatalogIds
                        .Where(entries.ContainsKey)
                        .Select(id => (JsonNode)BuildCapabilityCandidateCard(entries[id]))
                        .ToArray())
                }).ToArray())
        };
    }

    private static WorkflowRuntimeException BuildCapabilityClarificationFailure(
        string reason,
        string message,
        Exception? inner = null)
        => new(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            message,
            inner: inner,
            details: new JsonObject
            {
                ["phase"] = "capability_clarification",
                ["reason"] = reason,
                ["fail_closed"] = true,
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "clarify_or_abandon"
            });

    private static bool CapabilityProducesArtifactKind(CapabilityCatalogEntry entry, string kind)
        => entry.ArtifactContract != null
            ? entry.ArtifactContract.Produces.Any(artifact =>
                string.Equals(artifact.Kind, kind, StringComparison.Ordinal)
                && string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal))
            : entry.Outputs.Any(field => string.Equals(GetOperationalArtifactKind(field), kind, StringComparison.Ordinal)
                                         && ArtifactOutputDescriptionProvesExistence(field.Description));

    private static bool CapabilityRequiresArtifactKind(CapabilityCatalogEntry entry, string kind)
        => entry.ArtifactContract != null
            ? entry.ArtifactContract.Consumes.Any(artifact =>
                artifact.Required && string.Equals(artifact.Kind, kind, StringComparison.Ordinal))
            : entry.RequiredInputs.Any(field => string.Equals(GetOperationalArtifactKind(field), kind, StringComparison.Ordinal));

    private static IReadOnlyList<CapabilityArtifactRequirement> GetRequiredArtifactRequirements(
        CapabilityCatalogEntry entry)
        => entry.ArtifactContract != null
            ? entry.ArtifactContract.Consumes
                .Where(static artifact => artifact.Required)
                .Select(static artifact => new CapabilityArtifactRequirement(
                    new CapabilitySchemaField(
                        artifact.Pointer,
                        "string",
                        $"Required MCP-declared artifact of kind {artifact.Kind}."),
                    artifact.Kind))
                .ToArray()
            : entry.RequiredInputs
                .Select(static field => new CapabilityArtifactRequirement(
                    field,
                    GetOperationalArtifactKind(field) ?? string.Empty))
                .Where(static requirement => requirement.Kind.Length > 0)
                .ToArray();

    private static bool ArtifactOutputDescriptionProvesExistence(string description)
        => Regex.IsMatch(
            description,
            @"\b(created|cloned|checked[ -]?out|materialized|existing|produced)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? GetOperationalArtifactKind(CapabilitySchemaField field)
    {
        if (!string.Equals(field.Type, "string", StringComparison.OrdinalIgnoreCase))
            return null;

        var leaf = field.Path[(field.Path.LastIndexOf('/') + 1)..]
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        var normalized = Regex.Replace(leaf, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();
        if (normalized.StartsWith("target", StringComparison.Ordinal)
            || normalized.StartsWith("destination", StringComparison.Ordinal)
            || normalized.StartsWith("output", StringComparison.Ordinal)
            || normalized.StartsWith("temp", StringComparison.Ordinal))
        {
            return null;
        }

        if (normalized.Contains("projectroot", StringComparison.Ordinal)
            || normalized.Contains("workspaceroot", StringComparison.Ordinal)
            || normalized is "workdir" or "cwd")
        {
            return McpArtifactContractConventions.WorkspaceDirectoryKind;
        }

        if (normalized.Contains("directory", StringComparison.Ordinal)
            || normalized.Contains("folder", StringComparison.Ordinal))
        {
            return McpArtifactContractConventions.WorkspaceDirectoryKind;
        }

        if (normalized.EndsWith("handle", StringComparison.Ordinal))
            return "handle";

        return null;
    }

    private static bool IsExplicitCallerArtifactInput(
        string instruction,
        CapabilitySchemaField field,
        string kind)
    {
        var normalizedInstruction = NormalizeCapabilityIntentText(instruction);
        if (normalizedInstruction.Length == 0)
            return false;

        var leaf = field.Path[(field.Path.LastIndexOf('/') + 1)..]
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        var spacedLeaf = Regex.Replace(leaf, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace('_', ' ')
            .Replace('-', ' ');
        var phrases = new[]
        {
            NormalizeCapabilityIntentText(spacedLeaf),
            kind.Replace('_', ' ').Replace('.', ' ')
        }.Where(static phrase => phrase.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return phrases.Any(phrase => Regex.IsMatch(
            normalizedInstruction,
            $@"\b(accepts?|takes?|inputs?|provided|supplied|existing)\b.{{0,80}}\b{Regex.Escape(phrase)}\b|\b{Regex.Escape(phrase)}\b.{{0,80}}\b(as an? input|provided|supplied by (the )?user)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static string NormalizeCapabilityIntentText(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"\s+", " ").Trim();

    private static JsonObject ParseStructuredObject(LLMResponse response, string phase)
    {
        var json = response.Json as JsonObject;
        if (json == null && !string.IsNullOrWhiteSpace(response.Text))
            json = JsonNode.Parse(StripMarkdownFences(response.Text).Trim()) as JsonObject;
        return json ?? throw new InvalidOperationException($"Capability {phase} returned no structured object.");
    }

    private static HashSet<string> ResolveAllowedNativeStepTypes(StepExecutionContext ctx, JsonObject? input)
    {
        var available = ctx.Engine.Registry.RegisteredTypes.ToHashSet(StringComparer.Ordinal);
        var policy = input?["policy"] as JsonObject;
        if (policy?["allowed_step_types"] is JsonArray allowed)
            available.IntersectWith(allowed.Select(static node => node?.GetValue<string>() ?? string.Empty));
        if (policy?["denied_step_types"] is JsonArray denied)
            available.ExceptWith(denied.Select(static node => node?.GetValue<string>() ?? string.Empty));
        available.Remove("workflow.plan");
        available.Remove("workflow.execute");
        return available;
    }

    private static string BuildCapabilityInventoryPrompt(string instruction, string context) => $$"""
        You are a domain-neutral workflow runtime analyst. Return only the requested structured JSON.

        Pass 1 has no tool catalog. Enumerate every distinct positive operation that the generated workflow itself must perform at runtime to satisfy the task. Include required external reads, external writes, resource creation, cleanup, recovery, and user interactions. Do not guess implementation names.

        Separately enumerate constraints: prohibitions, safety rules, ordering requirements, and invariants. A prohibition is never a positive operation.

        Inventory only intentions expressed in the user task. The runtime-boundary bullets below are analyst instructions, not user intentions: never copy, paraphrase, or restate them as operations or constraints.

        Completeness means that every runtime intention expressed by the user is represented as an operation or constraint. It does not mean that an implementation, tool, selector, or available capability is already known. Capability availability and exact matching belong exclusively to Pass 2. Unknown implementation details, tool availability, selector choice, or capability support must never make this inventory incomplete. Preserve an explicitly requested effect as a domain-neutral operation even when its implementation is unknown.

        Classify every operation by execution_kind:
        - external_effect: an external read, write, AI execution, or resource lifecycle effect that needs a discovered capability;
        - human_interaction: an explicit user confirmation or additional user input;
        - local_processing: parsing, validation, filtering, transformation, aggregation, control flow, or orchestration performed inside the workflow. Local processing is a planning obligation and must not be forced onto an arbitrary tool or native step here.

        Also classify external_effect_kind:
        - read: obtains external state without changing it;
        - write: creates, changes, publishes, sends, submits, or deletes state outside the workflow;
        - execute: invokes AI or another computation without itself publishing or mutating external state;
        - lifecycle: creates or cleans an isolated runtime resource owned by this workflow;
        - none: required for human_interaction and local_processing.
        Use write for every operation whose intended result changes an external system, even when a later confirmation is expected.

        Set decision_source_operation_id to the ID of the earlier operation whose runtime result exclusively selects this operation's outcome or selector branch. Use an empty string when the operation is unconditional. Do not guess a future result; describe the dependency between operations. If the user requires a runtime-dependent choice but the deciding operation cannot be identified, set complete=false and request that relationship as user clarification.
        Represent all mutually exclusive outcomes of one runtime choice as one conditional external operation with that decision source. Never inventory one positive operation per possible branch value, because those branches are alternative implementations of one runtime effect rather than independently required effects.

        Classify every operation's intent_origin. Use requested_effect only for an independently observable runtime effect requested by the user or caller context. Use derived_failure_handling when an operation is introduced only as implementation handling for another operation's failure and is not an independently requested effect; set derivation_source_operation_id to that existing operation ID. Notifications, logging, escalation, compensation, retries, and fallback actions are not requested effects merely because the workflow must fail safely. Use an empty derivation_source_operation_id for requested_effect. Derived failure handling is not a positive capability requirement and will be removed from the locked inventory.

        Classify every constraint by enforcement_kind:
        - exact_denial: an unconditional prohibition that can safely ban exact external capabilities throughout the generated workflow;
        - workflow_policy: a conditional, ordering, cardinality, coverage, quality, confirmation, or other invariant that must be enforced by workflow structure rather than a document-wide capability denial.

        Runtime boundary rules:
        - When the task supplies only an external resource locator or identifier, separate literal local parsing from external state resolution. If a requested downstream effect requires current content, revisions, attributes, status, or other state not literally encoded in that locator, inventory one required external read that resolves the state. Never classify retrieval of external state as local parsing.
        - Preserve independently observable requested effects as distinct operations even when the same actor, AI, or external service may perform several of them. A user enumeration of preparation, execution, verification, analysis, publication, and cleanup outcomes must not be collapsed into one operation merely because a future capability could be prompted to attempt all of them.
        - Keep one operation when the task requests one atomic effect whose internal phases are not independently requested outcomes. Pass 2 may select a declared complete-operation wrapper or one prerequisite-closed composition; Pass 1 must neither decompose documented internals nor merge separate user-visible outcomes.
        - Exclude host configuration already supplied to the workflow runtime.
        - Treat declared workflow inputs supplied when execution starts as the public input contract, not as a separate human-interaction operation. Use human_interaction only when execution must pause after it starts for confirmation or additional information.
        - Exclude credentials, provider selection, secret-vault lookup, authentication, and connection setup performed internally by whichever runtime capability is selected later.
        - Exclude persistence, registration, or provisioning of the generated workflow/agent when that happens outside the generated workflow after planning.
        - Include cleanup only when the user explicitly requests cleanup as runtime behavior. Do not invent a generic cleanup operation merely because an unknown future implementation might allocate a resource; cleanup encapsulated inside a selected capability is not a separate workflow operation.
        - When the task names one external source, inventory at most one owned resource-materialization operation for that source. Preparation, analysis, verification, and publication phases consume the same resource; they are not separate requests to materialize phase-specific copies. Inventory multiple materializations only when the user explicitly requests distinct source resources.
        - A deterministic retry, backoff, fallback, or failure-handling policy for an already inventoried external operation is local_processing or a workflow_policy and reuses the original operation's capability. However, a separately requested runtime action performed by an AI, agent, service, or tool during that fallback remains external_effect/execute. Distinguish the local rule that decides when fallback is needed from the external actor that must inspect, choose, analyze, or generate a new runtime value; do not classify the latter as local processing merely because it occurs on a fallback path.
        - A restriction whose applicability depends on a target, input value, resource instance, runtime condition, or selected branch is workflow_policy even when it uses words such as only or never. Use exact_denial only when the prohibited capability must be banned for every possible invocation throughout the workflow.
        - Mark optional enrichment required=false.
        - Set complete=true once every explicit runtime intention is represented, including conditional and optional intentions.
        - Set complete=false only when ambiguity in the user's requested runtime behavior prevents you from identifying the intended operation or constraint. When false, provide concise incomplete_reasons describing the missing user intent and what must be clarified. Do not cite tool or catalog uncertainty as a reason.
        - Return an empty incomplete_reasons array when complete=true.

        {{BuildUserTaskBlock(instruction, context)}}
        """;

    private static string BuildCapabilityInventoryRepairPrompt(
        string instruction,
        string context,
        CapabilityInventory previous) => $$"""
        You are a domain-neutral workflow runtime inventory repair analyst. Return only the requested structured JSON.

        A previous inventory declared itself incomplete. Repair it once by ensuring that every runtime intention expressed by the user is represented as a positive operation or a constraint.

        Completeness is about enumerating requested runtime intent only. It is not a claim that an implementation, tool, selector, credential, or available capability is known. Capability availability and exact matching happen later. Unknown implementation details, tool availability, selector choice, or capability support must never make this inventory incomplete. Represent the intended effect in domain-neutral language instead.

        Preserve the runtime boundary:
        - When the task supplies only an external resource locator or identifier, separate literal local parsing from external state resolution. If a downstream requested effect needs current content, revisions, attributes, status, or other state not literally encoded in that locator, preserve one required external read that resolves the state. Never replace that external read with local parsing.
        - Split independently observable requested effects into distinct operations even when one actor, AI, or service could attempt several. Do not collapse separate preparation, execution, verification, analysis, publication, or cleanup outcomes into one unmatchable compound operation.
        - Do not split one atomic requested effect into speculative implementation phases. Later capability metadata decides whether a complete-operation wrapper replaces internal phases.
        - Exclude host configuration already supplied to the workflow runtime.
        - Exclude credentials, provider selection, secret-vault lookup, authentication, and connection setup performed internally by a later capability.
        - Exclude persistence, registration, or provisioning performed outside the generated workflow after planning.
        - Preserve cleanup only when the user explicitly requested it as runtime behavior. Never invent generic cleanup for resources that are not part of the user's intention.
        - Preserve one owned materialization for one external source and let later operations consume it. Do not turn workflow phases into additional source-materialization intentions unless the user explicitly requested distinct source resources.
        - Classify deterministic retry, backoff, fallback, and failure-handling policies for an existing external operation as local_processing or workflow_policy. A separately requested fallback action performed at runtime by an AI, agent, service, or tool remains external_effect/execute. Preserve the distinction between the local rule that selects the fallback path and the external actor that inspects, chooses, analyzes, or generates a new runtime value.
        - Keep prohibitions, ordering requirements, safety rules, and invariants as constraints rather than positive operations.
        - Inventory only intentions expressed in the user task. Do not copy, paraphrase, or restate these repair or runtime-boundary instructions as operations or constraints.
        - Preserve execution_kind and external_effect_kind for every operation. External writes use external_effect/write; external reads use external_effect/read; AI or other non-mutating execution uses external_effect/execute; owned resource setup/cleanup uses external_effect/lifecycle; human and local work use none.
        - Preserve decision_source_operation_id for runtime-dependent operations. It identifies the earlier operation whose result selects the branch; use an empty string for unconditional operations. Merge mutually exclusive outcome-specific operations into one conditional operation instead of treating every possible branch value as an independently required effect.
        - Preserve intent_origin and derivation_source_operation_id. requested_effect requires an empty derivation source. derived_failure_handling requires the ID of the existing operation whose failure it handles and is never a substitute for a user-requested external effect.
        - Classify constraints with enforcement_kind=exact_denial only for unconditional document-wide prohibitions. Target-, input-, resource-instance-, data-, or branch-dependent restrictions and conditional, ordering, cardinality, coverage, quality, confirmation, and other structural invariants use workflow_policy.
        - Include conditional and optional runtime intentions and mark optional enrichment required=false.
        - Return complete=true and an empty incomplete_reasons array when all requested effects are represented.
        - If the user's requested runtime behavior itself remains genuinely under-specified, return complete=false and concise incomplete_reasons stating what user intent must be clarified. Never cite missing tools, catalogs, selectors, credentials, or implementation knowledge.

        <previous_inventory>
        {{BuildCapabilityInventoryJson(previous)}}
        </previous_inventory>

        {{BuildUserTaskBlock(instruction, context)}}
        """;

    private static JsonObject BuildCapabilityInventorySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["complete"] = new JsonObject { ["type"] = "boolean" },
            ["incomplete_reasons"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("id", "description"),
                    ["additionalProperties"] = false
                }
            },
            ["operations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["required"] = new JsonObject { ["type"] = "boolean" },
                        ["execution_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("external_effect", "human_interaction", "local_processing")
                        },
                        ["external_effect_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("read", "write", "execute", "lifecycle", "none")
                        },
                        ["decision_source_operation_id"] = new JsonObject { ["type"] = "string" },
                        ["intent_origin"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("requested_effect", "derived_failure_handling")
                        },
                        ["derivation_source_operation_id"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "execution_kind", "external_effect_kind", "decision_source_operation_id", "intent_origin", "derivation_source_operation_id"),
                    ["additionalProperties"] = false
                }
            },
            ["constraints"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["required"] = new JsonObject { ["type"] = "boolean" },
                        ["enforcement_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("exact_denial", "workflow_policy")
                        }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "enforcement_kind"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("complete", "incomplete_reasons", "operations", "constraints"),
        ["additionalProperties"] = false
    };

    private static CapabilityInventory ParseCapabilityInventory(JsonObject json)
    {
        if (!TryReadComplete(json, out var complete))
            throw new InvalidOperationException("Capability inventory is missing its completeness decision.");
        var operationNodes = json["operations"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing operations.");
        var constraintNodes = json["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing constraints.");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var operations = operationNodes.Select(node =>
        {
            var (id, description, required) = ParseInventoryItem(node, identifiers, "operation");
            var executionKind = (node as JsonObject)?["execution_kind"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                ?? "external_effect";
            if (executionKind is not ("external_effect" or "human_interaction" or "local_processing"))
                throw new InvalidOperationException($"Capability inventory operation '{id}' has invalid execution_kind '{executionKind}'.");
            var externalEffectKind = (node as JsonObject)?["external_effect_kind"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                     ?? (executionKind == "external_effect" ? "execute" : "none");
            if (externalEffectKind is not ("read" or "write" or "execute" or "lifecycle" or "none")
                || executionKind != "external_effect" && externalEffectKind != "none"
                || executionKind == "external_effect" && externalEffectKind == "none")
            {
                throw new InvalidOperationException($"Capability inventory operation '{id}' has incompatible external_effect_kind '{externalEffectKind}'.");
            }
            var decisionSourceOperationId = (node as JsonObject)?["decision_source_operation_id"]?.GetValue<string>()?.Trim()
                                            ?? string.Empty;
            if (decisionSourceOperationId.Length > 160)
                throw new InvalidOperationException($"Capability inventory operation '{id}' has an invalid decision_source_operation_id.");
            var intentOrigin = (node as JsonObject)?["intent_origin"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                               ?? "requested_effect";
            if (intentOrigin is not ("requested_effect" or "derived_failure_handling"))
                throw new InvalidOperationException($"Capability inventory operation '{id}' has invalid intent_origin '{intentOrigin}'.");
            var derivationSourceOperationId = (node as JsonObject)?["derivation_source_operation_id"]?.GetValue<string>()?.Trim()
                                              ?? string.Empty;
            if (derivationSourceOperationId.Length > 160
                || intentOrigin == "requested_effect" && derivationSourceOperationId.Length > 0
                || intentOrigin == "derived_failure_handling" && derivationSourceOperationId.Length == 0)
            {
                throw new InvalidOperationException($"Capability inventory operation '{id}' has an incompatible derivation_source_operation_id.");
            }
            return new CapabilityInventoryOperation(
                id,
                description,
                required,
                executionKind,
                externalEffectKind,
                decisionSourceOperationId,
                intentOrigin,
                derivationSourceOperationId);
        }).ToArray();
        var constraints = constraintNodes.Select(node =>
        {
            var (id, description, required) = ParseInventoryItem(node, identifiers, "constraint");
            var enforcementKind = (node as JsonObject)?["enforcement_kind"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                  ?? "exact_denial";
            if (enforcementKind is not ("exact_denial" or "workflow_policy"))
                throw new InvalidOperationException($"Capability inventory constraint '{id}' has invalid enforcement_kind '{enforcementKind}'.");
            return new CapabilityInventoryConstraint(id, description, required, enforcementKind);
        }).ToArray();
        foreach (var operation in operations)
        {
            if (operation.DecisionSourceOperationId.Length > 0
                && (string.Equals(operation.Id, operation.DecisionSourceOperationId, StringComparison.Ordinal)
                || operations.All(candidate => !string.Equals(
                    candidate.Id,
                    operation.DecisionSourceOperationId,
                    StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    $"Capability inventory operation '{operation.Id}' references an unknown or self decision source '{operation.DecisionSourceOperationId}'.");
            }
            if (operation.DerivationSourceOperationId.Length > 0
                && (string.Equals(operation.Id, operation.DerivationSourceOperationId, StringComparison.Ordinal)
                    || operations.All(candidate => !string.Equals(
                        candidate.Id,
                        operation.DerivationSourceOperationId,
                        StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    $"Capability inventory operation '{operation.Id}' references an unknown or self derivation source '{operation.DerivationSourceOperationId}'.");
            }
        }
        var reasons = ParseCapabilityInventoryReasons(json["incomplete_reasons"] as JsonArray);
        if (complete && reasons.Count > 0)
            throw new InvalidOperationException("A complete capability inventory cannot contain incomplete reasons.");
        return new CapabilityInventory(complete, operations, constraints, reasons);
    }

    private static CapabilityInventory RemovePlannerBoundaryArtifacts(
        CapabilityInventory inventory,
        string userInstruction)
    {
        var userConcepts = CountPlannerBoundaryConcepts(userInstruction);
        var operations = inventory.Operations
            .Where(static operation => !string.Equals(
                operation.IntentOrigin,
                "derived_failure_handling",
                StringComparison.Ordinal))
            .Where(static operation => !IsHostInputContractArtifact(operation))
            .Where(operation => !IsUngroundedCleanupArtifact(operation, userInstruction))
            .ToArray();
        var constraints = inventory.Constraints
            .Where(constraint => CountPlannerBoundaryConcepts(constraint.Description) < 2 || userConcepts >= 2)
            .ToArray();
        var filtered = constraints.Length == inventory.Constraints.Count && operations.Length == inventory.Operations.Count
            ? inventory
            : inventory with { Operations = operations, Constraints = constraints };
        return filtered;
    }

    private static bool IsHostInputContractArtifact(CapabilityInventoryOperation operation)
    {
        if (operation.ExecutionKind != "local_processing")
            return false;
        var text = $"{operation.Id} {operation.Description}".Replace('_', ' ');
        return Regex.IsMatch(
            text,
            @"\b(accept|collect|gather|obtain|receive|request)\w*\b.{0,80}\b(runtime|workflow|declared|initial)\w*\b.{0,40}\binputs?\b|\b(runtime|workflow|declared|initial)\w*\b.{0,40}\binputs?\b.{0,80}\b(accept|collect|gather|obtain|receive|request)\w*\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsUngroundedCleanupArtifact(
        CapabilityInventoryOperation operation,
        string userInstruction)
    {
        if (!string.Equals(operation.ExecutionKind, "external_effect", StringComparison.Ordinal)
            || !string.Equals(operation.ExternalEffectKind, "lifecycle", StringComparison.Ordinal))
        {
            return false;
        }

        var operationText = $"{operation.Id} {operation.Description}".Replace('_', ' ');
        if (!Regex.IsMatch(
                operationText,
                @"\b(clean(?:up|\s+up)|delete|remove|dispose|release|disconnect|close|tear\s*down|destroy|purge)\w*\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        // Pass 1 is an inventory of user runtime intent, not of a tool implementation
        // that has not been selected yet. Keep an explicitly requested lifecycle effect,
        // but discard speculative "clean up any runtime resources" artifacts.
        return !Regex.IsMatch(
            userInstruction.Replace('_', ' '),
            @"\b(clean(?:up|\s+up)|delete|remove|dispose|release|disconnect|close|tear\s*down|destroy|purge)\w*\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int CountPlannerBoundaryConcepts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var count = 0;
        if (Regex.IsMatch(value, @"\bhost\b.{0,40}\bconfigur", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            count++;
        if (Regex.IsMatch(value, @"\b(credential|secret|vault|authentication)\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            count++;
        if (Regex.IsMatch(value, @"\b(connection|provider)\w*\b.{0,40}\b(resolve|resolution|setup|select|configuration)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            count++;
        if (Regex.IsMatch(value, @"\b(persist|register|provision)\w*\b.{0,80}\b(agent|workflow)\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            count++;
        return count;
    }

    private static CapabilityInventory ApplyDefaultExternalWriteConfirmation(
        CapabilityInventory inventory,
        string userInstruction)
    {
        if (!inventory.Complete
            || IsUnattendedExecutionExplicitlyRequested(userInstruction)
            || !inventory.Operations.Any(static operation => operation.ExecutionKind == "external_effect"
                && operation.ExternalEffectKind == "write")
            || inventory.Operations.Any(static operation => operation.Required
                && operation.ExecutionKind == "human_interaction"
                && Regex.IsMatch(operation.Description, @"\b(confirm|confirmation|approve|approval|authoriz|consent)\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && Regex.IsMatch(operation.Description, @"\b(external|write|publish|post|send|submit|create|update|delete|change|mutat)\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            return inventory;
        }

        var identifiers = inventory.Operations.Select(static operation => operation.Id)
            .Concat(inventory.Constraints.Select(static constraint => constraint.Id))
            .ToHashSet(StringComparer.Ordinal);
        var operationId = CreateUniqueInventoryId("platform_confirm_external_write", identifiers);
        identifiers.Add(operationId);
        var constraintId = CreateUniqueInventoryId("platform_external_write_after_confirmation", identifiers);
        return inventory with
        {
            Operations = inventory.Operations.Concat([
                new CapabilityInventoryOperation(
                    operationId,
                    PlatformExternalWriteConfirmationOperationDescription,
                    true,
                    "human_interaction",
                    "none")
            ]).ToArray(),
            Constraints = inventory.Constraints.Concat([
                new CapabilityInventoryConstraint(
                    constraintId,
                    PlatformExternalWriteConfirmationConstraintDescription,
                    true,
                    "workflow_policy")
            ]).ToArray()
        };
    }

    private static string CreateUniqueInventoryId(string preferred, IReadOnlySet<string> identifiers)
    {
        if (!identifiers.Contains(preferred))
            return preferred;
        for (var suffix = 2; suffix < 1_000; suffix++)
        {
            var candidate = $"{preferred}_{suffix}";
            if (!identifiers.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Capability inventory contains too many colliding platform policy identifiers.");
    }

    private static bool IsUnattendedExecutionExplicitlyRequested(string instruction)
        => Regex.IsMatch(
            instruction,
            @"\b(unattended|headless)\b|\bwithout\s+(human\s+)?(confirmation|approval)\b|\b(no|disable|skip)\s+(human\s+)?(confirmation|approval)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (string Id, string Description, bool Required) ParseInventoryItem(
        JsonNode? node,
        HashSet<string> identifiers,
        string kind)
    {
        if (node is not JsonObject item)
            throw new InvalidOperationException($"Capability inventory {kind} must be an object.");
        var id = item["id"]?.GetValue<string>()?.Trim();
        var description = item["description"]?.GetValue<string>()?.Trim();
        var required = item["required"]?.GetValue<bool>() ?? true;
        if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id) || string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Capability inventory ids must be unique and descriptions must be non-empty.");
        return (id, description, required);
    }

    private static IReadOnlyList<CapabilityInventoryIncompleteReason> ParseCapabilityInventoryReasons(JsonArray? nodes)
    {
        if (nodes == null || nodes.Count == 0)
            return Array.Empty<CapabilityInventoryIncompleteReason>();
        if (nodes.Count > 16)
            throw new InvalidOperationException("Capability inventory returned too many incomplete reasons.");

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new List<CapabilityInventoryIncompleteReason>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not JsonObject item)
                throw new InvalidOperationException("Capability inventory incomplete reasons must be objects.");
            var id = item["id"]?.GetValue<string>()?.Trim();
            var description = item["description"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 || !identifiers.Add(id)
                || string.IsNullOrWhiteSpace(description) || description.Length > 1_000)
                throw new InvalidOperationException("Capability inventory incomplete reasons must have unique bounded ids and non-empty bounded descriptions.");
            reasons.Add(new CapabilityInventoryIncompleteReason(id, description));
        }
        return reasons;
    }

    private static string BuildCapabilityInventoryJson(CapabilityInventory inventory)
    {
        var operations = new JsonArray(inventory.Operations.Select(static operation => (JsonNode)new JsonObject
        {
            ["id"] = operation.Id,
            ["description"] = operation.Description,
            ["required"] = operation.Required,
            ["execution_kind"] = operation.ExecutionKind,
            ["external_effect_kind"] = operation.ExternalEffectKind,
            ["decision_source_operation_id"] = operation.DecisionSourceOperationId,
            ["intent_origin"] = operation.IntentOrigin,
            ["derivation_source_operation_id"] = operation.DerivationSourceOperationId
        }).ToArray());
        var constraints = new JsonArray(inventory.Constraints.Select(static constraint => (JsonNode)new JsonObject
        {
            ["id"] = constraint.Id,
            ["description"] = constraint.Description,
            ["required"] = constraint.Required,
            ["enforcement_kind"] = constraint.EnforcementKind
        }).ToArray());
        var reasons = new JsonArray(inventory.IncompleteReasons.Select(static reason => (JsonNode)new JsonObject
        {
            ["id"] = reason.Id,
            ["description"] = reason.Description
        }).ToArray());
        return new JsonObject
        {
            ["complete"] = inventory.Complete,
            ["incomplete_reasons"] = reasons,
            ["operations"] = operations,
            ["constraints"] = constraints
        }.ToJsonString();
    }

    private static void ThrowIncompleteCapabilityInventory(CapabilityInventory inventory)
    {
        IReadOnlyList<CapabilityInventoryIncompleteReason> reasons = inventory.IncompleteReasons.Count > 0
            ? inventory.IncompleteReasons
            : [new CapabilityInventoryIncompleteReason(
                "inventory_uncertain",
                "The inventory remained incomplete, but the inference model did not identify a specific user clarification.")];
        var reasonArray = new JsonArray(reasons.Select(static reason => (JsonNode)new JsonObject
        {
            ["id"] = SanitizeCapabilityInferenceDiagnostic(reason.Id, 160),
            ["description"] = SanitizeCapabilityInferenceDiagnostic(reason.Description, 1_000)
        }).ToArray());

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability inference could not produce a complete runtime operation inventory after one repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_inventory",
                ["repair_attempted"] = true,
                ["attempts"] = 2,
                ["operation_count"] = inventory.Operations.Count,
                ["constraint_count"] = inventory.Constraints.Count,
                ["incomplete_reasons"] = reasonArray,
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "clarify_or_abandon"
            });
    }

    private static string SanitizeCapabilityInferenceDiagnostic(string value, int limit)
        => WorkflowTelemetrySourceFormatter.Format(value, limit).Text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string BuildCapabilityMatchingPrompt(CapabilityInventory inventory, CapabilityCatalog catalog)
    {
        var operations = new JsonArray(inventory.Operations.Select(static operation => (JsonNode)new JsonObject
        {
            ["id"] = operation.Id,
            ["description"] = operation.Description,
            ["required"] = operation.Required,
            ["execution_kind"] = operation.ExecutionKind,
            ["external_effect_kind"] = operation.ExternalEffectKind,
            ["decision_source_operation_id"] = operation.DecisionSourceOperationId
        }).ToArray());
        var constraints = new JsonArray(inventory.Constraints.Select(static constraint => (JsonNode)new JsonObject
        {
            ["id"] = constraint.Id,
            ["description"] = constraint.Description,
            ["required"] = constraint.Required,
            ["enforcement_kind"] = constraint.EnforcementKind
        }).ToArray());
        return $$"""
            You are a domain-neutral capability matcher. Return only the requested structured JSON.

            Decide every positive runtime operation independently:
            - matched: exactly one catalog capability is sufficient;
            - composed: two or more complementary catalog capabilities are jointly required;
            - local: the inventory classified the operation as local_processing, so no catalog capability is selected;
            - conditional: two or more selector-specific variants of one physical capability are required as mutually exclusive runtime branches, another inventory operation determines which branch executes, and the same selection may also contain complementary unconditional prerequisites;
            - ambiguous: more than one plausible implementation remains and the catalog does not establish which is correct;
            - unavailable: the catalog contains no sufficient implementation.

            Prefer the smallest sufficient composition. A composition is valid only when every selected capability is necessary for the one operation. For a multi-action tool, choose selector-specific entries whose request_bindings describe the logical operation. Different selector values are distinct capabilities.
            A selector entry with variant_of inherits the description, arguments, outputs, and artifact contract from the whole-tool entry identified by the same server, kind, and method; its compact row intentionally contains only the distinguishing literal request_bindings.
            A whole-tool entry without request_bindings is appropriate when enum-valued arguments are runtime data rather than a fixed logical action. Prefer a combined selector entry over several single-selector entries when one physical call requires all of those fixed literal values.
            When an operation has a non-empty decision_source_operation_id, select every exact selector variant required by that runtime-dependent outcome with status conditional. Copy the locked decision_source_operation_id into decision_operation_id. Conditional variants must belong to one physical capability, share the same selector paths and every fixed selector except one mutually exclusive selector path, and use distinct values on that path. Include complementary unconditional prerequisite capabilities in the same conditional catalog_ids only when they are necessary for that operation; they execute once outside the exclusive branch. This is runtime control flow, not user ambiguity. Never ask the user to predict a future runtime result.
            A complete_operation composition entry encapsulates its listed lower-level phases. Select the complete operation alone when it is sufficient; never compose it with a phase it already encapsulates.

            Capability sufficiency includes input provenance and data flow:
            - Read each selected card's required arguments and bounded output fields. A required argument must be supplied by a semantically compatible workflow runtime input, a documented host-internal/default value, a literal selector binding, or an output of a selected producer capability.
            - When a selected capability requires an existing external artifact such as a workspace, project root, directory, file, handle, or exact comparison payload, include the necessary producer capability or capabilities in the same composed match unless the user explicitly supplies that pre-existing artifact as a runtime input.
            - A producer output may feed any number of operations. Selecting the same materializer as a prerequisite for several operations represents one shared locked occurrence unless the inventory contains distinct source-materialization operations.
            - Ordinary scalar request values, identifiers, and selector-independent fields may be parsed or derived locally from declared runtime inputs or reused from an already selected upstream read. Do not add another external read to every composed match merely to resupply those values.
            - A complementary producer in the same match must satisfy a documented artifact-contract dependency or another concrete multi-call prerequisite of that operation. Do not retain unrelated reads, broad selector entries, or alternative implementations alongside one sufficient exact selector.
            - Use documented output fields to identify producers. Do not assume that local parsing, transformation, a URL, an identifier, or an invented string can create or prove an external artifact.
            - A high-level capability may stand alone only when its documented contract encapsulates its prerequisites. Otherwise select the smallest prerequisite-closed composition.

            For each constraint classified enforcement_kind=exact_denial, use enforced with every exact MCP catalog capability it unconditionally prohibits, or ambiguous when several exact denials are plausible. For enforcement_kind=workflow_policy, always use policy_only with no catalog IDs because the invariant must be enforced by workflow structure. Do not reinterpret constraint prose or deny a whole multi-action tool when only one selector-specific operation is prohibited.
            Native Flow catalog IDs are never denied_catalog_ids or constraint candidate_catalog_ids. A constraint involving native orchestration remains policy_only; positive required interaction belongs in operation_matches.

            Rules:
            - Return only catalog IDs shown below; never invent server, tool, prompt, method, or selector names.
            - Do not infer behavior from server names, product names, URLs, brands, or undocumented semantics.
            - Every inventory operation and constraint ID must occur exactly once.
            - matched requires one catalog_ids value; composed requires at least two; conditional requires exactly one mutually exclusive selector subset of at least two entries and may additionally contain necessary complementary prerequisites; local and unavailable require none. candidate_catalog_ids are advisory and are ignored for a final matched, composed, or conditional decision.
            - decision_operation_id is required only for conditional and must exactly equal that operation's non-empty decision_source_operation_id; return an empty string for all other statuses.
            - local is valid only for execution_kind=local_processing. External effects and human interaction must use a documented catalog or be unresolved.
            - candidate_catalog_ids contain at most eight alternatives. They are required for ambiguous decisions and advisory for final decisions; catalog_ids alone define the selected implementation.
            - Give a concise decision reason. Do not expose hidden reasoning or repeat task/repository content.

            <runtime_inventory>
            {{new JsonObject { ["operations"] = operations, ["constraints"] = constraints }.ToJsonString()}}
            </runtime_inventory>

            <capability_catalog>
            {{catalog.Text}}
            </capability_catalog>
            """;
    }

    private static JsonObject BuildCapabilityMatchingSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["operation_matches"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["operation_id"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("matched", "composed", "conditional", "local", "ambiguous", "unavailable") },
                        ["catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["candidate_catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["decision_operation_id"] = new JsonObject { ["type"] = "string" },
                        ["reason"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("operation_id", "status", "catalog_ids", "candidate_catalog_ids", "decision_operation_id", "reason"),
                    ["additionalProperties"] = false
                }
            },
            ["constraint_matches"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["constraint_id"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("enforced", "policy_only", "ambiguous") },
                        ["denied_catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["candidate_catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["reason"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("constraint_id", "status", "denied_catalog_ids", "candidate_catalog_ids", "reason"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("operation_matches", "constraint_matches"),
        ["additionalProperties"] = false
    };

    private static CapabilityMatchingEvaluation ParseCapabilityMatchingEvaluation(
        JsonObject json,
        CapabilityInventory inventory,
        CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var operationIds = inventory.Operations.Select(static operation => operation.Id).ToArray();
        var constraintIds = inventory.Constraints.Select(static constraint => constraint.Id).ToArray();
        string ReadOperationId(JsonObject node)
            => ResolveMatchingInventoryId(ReadMatchingString(node, "operation_id"), operationIds);
        string ReadConstraintId(JsonObject node)
            => ResolveMatchingInventoryId(ReadMatchingString(node, "constraint_id"), constraintIds);
        var issues = new List<CapabilityMatchingIssue>();
        var contractValid = true;
        var operationNodes = json["operation_matches"] as JsonArray;
        if (operationNodes == null)
        {
            operationNodes = new JsonArray();
            contractValid = false;
        }
        var operationObjects = operationNodes.OfType<JsonObject>().ToArray();
        if (operationObjects.Length != operationNodes.Count)
            contractValid = false;
        foreach (var unknown in operationObjects
                     .Select(ReadOperationId)
                     .Where(id => id.Length > 0 && inventory.Operations.All(operation => !string.Equals(operation.Id, id, StringComparison.Ordinal)))
                     .Distinct(StringComparer.Ordinal))
        {
            contractValid = false;
            issues.Add(new CapabilityMatchingIssue(unknown, "Unknown operation identifier.", true, "invalid",
                "The matching response referenced an operation that was not present in the locked inventory.", Array.Empty<string>()));
        }

        var operationMatches = new List<CapabilityOperationMatch>(inventory.Operations.Count);
        foreach (var operation in inventory.Operations)
        {
            var nodes = operationObjects.Where(node => string.Equals(ReadOperationId(node), operation.Id, StringComparison.Ordinal)).ToArray();
            if (nodes.Length != 1)
            {
                contractValid = false;
                var reason = nodes.Length == 0
                    ? "The matching response omitted this locked operation."
                    : "The matching response returned this locked operation more than once.";
                operationMatches.Add(new CapabilityOperationMatch(operation, "invalid", reason, Array.Empty<string>(), Array.Empty<string>()));
                issues.Add(new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, "invalid", reason, Array.Empty<string>()));
                continue;
            }

            var node = nodes[0];
            var status = ReadMatchingString(node, "status").ToLowerInvariant();
            var selected = ReadMatchingIds(node["catalog_ids"], 32, out var selectedValid);
            var candidates = ReadMatchingIds(node["candidate_catalog_ids"], 8, out var candidatesValid);
            var decisionOperationId = ResolveMatchingInventoryId(
                ReadMatchingString(node, "decision_operation_id"),
                operationIds);
            var reasonText = SanitizeCapabilityInferenceDiagnostic(ReadMatchingString(node, "reason"), 1_000);
            var validStatus = status is "matched" or "composed" or "conditional" or "local" or "ambiguous" or "unavailable";

            // Structured model responses occasionally repeat advisory candidates in
            // catalog_ids, or place an explicit ambiguous set in catalog_ids. Preserve
            // the declared semantic status while normalizing that bounded field-placement
            // error; unknown IDs and every other malformed shape still fail closed.
            if (status == "matched" && selected.Count == 0 && candidates.Count == 1)
            {
                selected = candidates;
                candidates = Array.Empty<string>();
                selectedValid = candidatesValid;
                candidatesValid = true;
            }
            else if (status is ("composed" or "conditional") && selected.Count == 0 && candidates.Count >= 2)
            {
                selected = candidates;
                candidates = Array.Empty<string>();
                selectedValid = candidatesValid;
                candidatesValid = true;
            }
            else if (status is ("matched" or "composed" or "conditional") && selected.Count > 0)
            {
                candidates = Array.Empty<string>();
                candidatesValid = true;
            }
            else if (status == "ambiguous" && selected.Count > 0)
            {
                var mergedCandidates = selected.Concat(candidates)
                    .Distinct(StringComparer.Ordinal)
                    .Take(9)
                    .ToArray();
                if (mergedCandidates.Length <= 8)
                {
                    selected = Array.Empty<string>();
                    candidates = mergedCandidates;
                    selectedValid = true;
                    candidatesValid = true;
                }
            }

            var knownSelected = selected.All(entries.ContainsKey);
            var knownCandidates = candidates.All(entries.ContainsKey);
            if (status == "matched"
                && selected.Count > 1
                && knownSelected
                && IsDeclaredArtifactComposition(selected.Select(id => entries[id]).ToArray()))
            {
                status = "composed";
            }
            else if (status == "matched"
                     && selected.Count == 0
                     && candidates.Count > 1
                     && knownCandidates
                     && IsDeclaredArtifactComposition(candidates.Select(id => entries[id]).ToArray()))
            {
                selected = candidates;
                candidates = Array.Empty<string>();
                selectedValid = candidatesValid;
                candidatesValid = true;
                knownSelected = true;
                knownCandidates = true;
                status = "composed";
            }
            var shapeValid = validStatus && selectedValid && candidatesValid && knownSelected && knownCandidates && reasonText.Length > 0;
            shapeValid = shapeValid && status switch
            {
                "matched" => selected.Count == 1 && decisionOperationId.Length == 0,
                "composed" => selected.Count >= 2 && decisionOperationId.Length == 0,
                "conditional" => selected.Count >= 2
                                  && candidates.Count == 0
                                  && decisionOperationId.Length > 0
                                 && string.Equals(
                                     decisionOperationId,
                                     operation.DecisionSourceOperationId,
                                     StringComparison.Ordinal)
                                 && inventory.Operations.Any(candidate => string.Equals(candidate.Id, decisionOperationId, StringComparison.Ordinal))
                                  && TryBuildConditionalCompositionBranchValues(selected.Select(id => entries[id]).ToArray(), out _),
                "local" => selected.Count == 0 && candidates.Count == 0 && decisionOperationId.Length == 0 && operation.ExecutionKind == "local_processing",
                "ambiguous" => selected.Count == 0 && candidates.Count > 0 && decisionOperationId.Length == 0,
                "unavailable" => selected.Count == 0 && candidates.Count == 0 && decisionOperationId.Length == 0,
                _ => false
            };
            if (operation.ExecutionKind != "local_processing" && status == "local"
                || operation.ExecutionKind == "local_processing" && status != "local")
                shapeValid = false;

            if (!shapeValid)
            {
                contractValid = false;
                status = "invalid";
                reasonText = BuildInvalidMatchingReason(validStatus, selectedValid && candidatesValid, knownSelected && knownCandidates, operation.ExecutionKind);
            }
            operationMatches.Add(new CapabilityOperationMatch(operation, status, reasonText, selected, candidates,
                decisionOperationId.Length > 0 ? decisionOperationId : null));
            if (status is "ambiguous" or "unavailable" or "invalid")
                issues.Add(new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, status, reasonText,
                    status == "ambiguous" ? candidates : selected.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray()));
        }

        var constraintNodes = json["constraint_matches"] as JsonArray;
        if (constraintNodes == null)
        {
            constraintNodes = new JsonArray();
            contractValid = false;
        }
        var constraintObjects = constraintNodes.OfType<JsonObject>().ToArray();
        if (constraintObjects.Length != constraintNodes.Count)
            contractValid = false;
        var constraintMatches = new List<CapabilityConstraintMatch>(inventory.Constraints.Count);
        foreach (var constraint in inventory.Constraints)
        {
            var nodes = constraintObjects.Where(node => string.Equals(ReadConstraintId(node), constraint.Id, StringComparison.Ordinal)).ToArray();
            if (nodes.Length != 1)
            {
                if (nodes.Length == 0
                    && string.Equals(constraint.EnforcementKind, "workflow_policy", StringComparison.Ordinal))
                {
                    constraintMatches.Add(new CapabilityConstraintMatch(
                        constraint,
                        "policy_only",
                        "The omitted match is normalized to policy_only because this locked conditional, ordering, or coverage rule cannot be represented as an unconditional exact capability denial.",
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                    continue;
                }

                contractValid = false;
                var reason = nodes.Length == 0
                    ? "The matching response omitted this locked constraint."
                    : "The matching response returned this locked constraint more than once.";
                constraintMatches.Add(new CapabilityConstraintMatch(constraint, "invalid", reason, Array.Empty<string>(), Array.Empty<string>()));
                issues.Add(new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, "invalid", reason, Array.Empty<string>()));
                continue;
            }

            var node = nodes[0];
            var status = ReadMatchingString(node, "status").ToLowerInvariant();
            var denied = ReadMatchingIds(node["denied_catalog_ids"], 64, out var deniedValid);
            var candidates = ReadMatchingIds(node["candidate_catalog_ids"], 8, out var candidatesValid);
            var reasonText = SanitizeCapabilityInferenceDiagnostic(ReadMatchingString(node, "reason"), 1_000);
            var referencedIds = denied.Concat(candidates).ToArray();
            var referencesOnlyKnownNativeCapabilities = deniedValid
                                                       && candidatesValid
                                                       && referencedIds.Length > 0
                                                       && referencedIds.All(id => entries.TryGetValue(id, out var entry)
                                                                                  && entry.Resolution == "native");
            if (referencesOnlyKnownNativeCapabilities
                && string.Equals(constraint.EnforcementKind, "workflow_policy", StringComparison.Ordinal))
            {
                // Constraint denial contracts intentionally lock only exact MCP alternatives.
                // Native orchestration restrictions remain policy text; treating a native-only
                // candidate as policy_only is lossless and avoids an unnecessary inference repair.
                status = "policy_only";
                denied = Array.Empty<string>();
                candidates = Array.Empty<string>();
                reasonText = "The constraint is preserved as an orchestration policy because native Flow steps are not exact denied MCP alternatives.";
            }
            if (string.Equals(constraint.EnforcementKind, "workflow_policy", StringComparison.Ordinal))
            {
                // Exact denied alternatives are unconditional document-wide bans. They cannot
                // represent a capability that is allowed after a gate, before a deadline, or
                // only under another condition without rejecting the valid guarded call too.
                status = "policy_only";
                denied = Array.Empty<string>();
                candidates = Array.Empty<string>();
                reasonText = "The constraint is preserved as a conditional or ordering policy because an exact denial would prohibit valid guarded use of the capability.";
            }
            var validStatus = status is "enforced" or "policy_only" or "ambiguous";
            var knownDenied = denied.All(id => entries.TryGetValue(id, out var entry) && entry.Resolution == "mcp");
            var knownCandidates = candidates.All(id => entries.TryGetValue(id, out var entry) && entry.Resolution == "mcp");
            var shapeValid = validStatus && deniedValid && candidatesValid && knownDenied && knownCandidates && reasonText.Length > 0;
            shapeValid = shapeValid && status switch
            {
                "enforced" => denied.Count > 0,
                "policy_only" => denied.Count == 0
                                 && candidates.Count == 0
                                 && string.Equals(
                                     constraint.EnforcementKind,
                                     "workflow_policy",
                                     StringComparison.Ordinal),
                "ambiguous" => denied.Count == 0 && candidates.Count > 0,
                _ => false
            };
            if (!shapeValid)
            {
                contractValid = false;
                status = "invalid";
                reasonText = "The constraint match used an invalid status, unknown catalog ID, duplicate ID, or incompatible denial shape.";
            }
            constraintMatches.Add(new CapabilityConstraintMatch(constraint, status, reasonText, denied, candidates));
            if (status is "ambiguous" or "invalid")
                issues.Add(new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, status, reasonText,
                    status == "ambiguous" ? candidates : denied.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray()));
        }

        var expectedConstraintIds = inventory.Constraints.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in constraintObjects.Select(ReadConstraintId)
                     .Where(id => id.Length > 0 && !expectedConstraintIds.Contains(id)).Distinct(StringComparer.Ordinal))
        {
            contractValid = false;
            issues.Add(new CapabilityMatchingIssue(unknown, "Unknown constraint identifier.", true, "invalid",
                "The matching response referenced a constraint that was not present in the locked inventory.", Array.Empty<string>()));
        }

        return new CapabilityMatchingEvaluation(operationMatches, constraintMatches, issues, contractValid);
    }

    private static bool IsDeclaredArtifactComposition(IReadOnlyList<CapabilityCatalogEntry> selected)
    {
        if (selected.Count < 2)
            return false;

        var adjacency = Enumerable.Range(0, selected.Count)
            .Select(static _ => new HashSet<int>())
            .ToArray();
        for (var consumerIndex = 0; consumerIndex < selected.Count; consumerIndex++)
        {
            foreach (var requirement in GetRequiredArtifactRequirements(selected[consumerIndex]))
            {
                var producers = Enumerable.Range(0, selected.Count)
                    .Where(index => index != consumerIndex
                                    && CapabilityProducesArtifactKind(selected[index], requirement.Kind))
                    .Take(2)
                    .ToArray();
                if (producers.Length > 1)
                    return false;
                if (producers.Length != 1)
                    continue;

                adjacency[consumerIndex].Add(producers[0]);
                adjacency[producers[0]].Add(consumerIndex);
            }
        }

        if (adjacency.Any(static neighbors => neighbors.Count == 0))
            return false;

        var visited = new HashSet<int> { 0 };
        var pending = new Queue<int>();
        pending.Enqueue(0);
        while (pending.TryDequeue(out var current))
        {
            foreach (var neighbor in adjacency[current])
            {
                if (visited.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }

        return visited.Count == selected.Count;
    }

    private static string ResolveMatchingInventoryId(string candidate, IReadOnlyList<string> knownIds)
    {
        if (candidate.Length == 0 || knownIds.Contains(candidate, StringComparer.Ordinal))
            return candidate;

        var canonical = CanonicalizeMatchingInventoryId(candidate);
        if (canonical.Length == 0)
            return candidate;
        var matches = knownIds.Where(id => string.Equals(
                CanonicalizeMatchingInventoryId(id),
                canonical,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length == 1)
            return matches[0];

        var stem = StripMatchingInventoryKindPrefix(canonical);
        var stemMatches = knownIds.Where(id => string.Equals(
                StripMatchingInventoryKindPrefix(CanonicalizeMatchingInventoryId(id)),
                stem,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return stemMatches.Length == 1 ? stemMatches[0] : candidate;
    }

    private static string CanonicalizeMatchingInventoryId(string value)
        => Regex.Replace(value, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();

    private static string StripMatchingInventoryKindPrefix(string value)
    {
        foreach (var prefix in new[] { "operation", "constraint", "policy" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length)
                return value[prefix.Length..];
        }

        return value;
    }

    private static CapabilityMatchingEvaluation BuildMalformedCapabilityMatchingEvaluation(
        CapabilityInventory inventory,
        string reason)
    {
        var sanitized = SanitizeCapabilityInferenceDiagnostic(reason, 1_000);
        if (sanitized.Length == 0)
            sanitized = "The matching response was not a valid structured object.";
        var operationMatches = inventory.Operations.Select(operation =>
            new CapabilityOperationMatch(operation, "invalid", sanitized, Array.Empty<string>(), Array.Empty<string>())).ToArray();
        var constraintMatches = inventory.Constraints.Select(constraint =>
            new CapabilityConstraintMatch(constraint, "invalid", sanitized, Array.Empty<string>(), Array.Empty<string>())).ToArray();
        var issues = inventory.Operations.Select(operation =>
                new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, "invalid", sanitized, Array.Empty<string>()))
            .Concat(inventory.Constraints.Select(constraint =>
                new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, "invalid", sanitized, Array.Empty<string>())))
            .ToArray();
        return new CapabilityMatchingEvaluation(operationMatches, constraintMatches, issues, false);
    }

    private static string ReadMatchingString(JsonObject node, string property)
        => node[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

    private static IReadOnlyList<string> ReadMatchingIds(JsonNode? node, int maximum, out bool valid)
    {
        valid = node is JsonArray;
        if (node is not JsonArray values || values.Count > maximum)
        {
            valid = false;
            return Array.Empty<string>();
        }
        var result = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var id)
                || string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim()))
            {
                valid = false;
                continue;
            }
            result.Add(id.Trim());
        }
        return result;
    }

    private static string BuildInvalidMatchingReason(bool validStatus, bool arraysValid, bool idsKnown, string executionKind)
    {
        if (!validStatus)
            return "The operation match returned an unsupported status.";
        if (!arraysValid)
            return "The operation match returned malformed, duplicate, or excessive catalog IDs.";
        if (!idsKnown)
            return "The operation match referenced one or more unknown catalog IDs.";
        if (executionKind == "local_processing")
            return "A local-processing operation must use status local with no catalog IDs.";
        return "The operation status and selected or candidate catalog ID counts are inconsistent.";
    }

    private static bool RequiresCapabilityMatchingRepair(CapabilityMatchingEvaluation evaluation)
        => !evaluation.ContractValid || evaluation.Issues.Any(static issue => issue.Required);

    private static CapabilityMatchingEvaluation PreserveValidCapabilityMatches(
        CapabilityMatchingEvaluation initial,
        CapabilityMatchingEvaluation repaired)
    {
        var lockedOperationIds = initial.OperationMatches
            .Where(static match => match.Status is "matched" or "composed" or "conditional" or "local")
            .Select(static match => match.Operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        var lockedConstraintIds = initial.ConstraintMatches
            .Where(static match => match.Status is "enforced" or "policy_only")
            .Select(static match => match.Constraint.Id)
            .ToHashSet(StringComparer.Ordinal);
        var initialOperations = initial.OperationMatches.ToDictionary(static match => match.Operation.Id, StringComparer.Ordinal);
        var initialConstraints = initial.ConstraintMatches.ToDictionary(static match => match.Constraint.Id, StringComparer.Ordinal);
        var operations = repaired.OperationMatches
            .Select(match => lockedOperationIds.Contains(match.Operation.Id) ? initialOperations[match.Operation.Id] : match)
            .ToArray();
        var constraints = repaired.ConstraintMatches
            .Select(match => lockedConstraintIds.Contains(match.Constraint.Id) ? initialConstraints[match.Constraint.Id] : match)
            .ToArray();
        var issues = repaired.Issues
            .Where(issue => !lockedOperationIds.Contains(issue.OperationId) && !lockedConstraintIds.Contains(issue.OperationId))
            .ToArray();
        var mergedContractValid = operations.All(static match => match.Status != "invalid")
                                  && constraints.All(static match => match.Status != "invalid")
                                  && issues.All(static issue => issue.Status != "invalid");
        return new CapabilityMatchingEvaluation(operations, constraints, issues, mergedContractValid);
    }

    private static string BuildCapabilityMatchingRepairPrompt(
        CapabilityInventory inventory,
        CapabilityCatalog catalog,
        CapabilityMatchingEvaluation previous)
    {
        var lockedOperations = previous.OperationMatches
            .Where(static match => match.Status is "matched" or "composed" or "conditional" or "local")
            .Select(static match => (JsonNode)new JsonObject
            {
                ["operation_id"] = match.Operation.Id,
                ["status"] = match.Status,
                ["catalog_ids"] = new JsonArray(match.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["decision_operation_id"] = match.DecisionOperationId ?? string.Empty
            }).ToArray();
        var lockedConstraints = previous.ConstraintMatches
            .Where(static match => match.Status is "enforced" or "policy_only")
            .Select(static match => (JsonNode)new JsonObject
            {
                ["constraint_id"] = match.Constraint.Id,
                ["status"] = match.Status,
                ["denied_catalog_ids"] = new JsonArray(match.DeniedCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
            }).ToArray();
        var issues = previous.Issues.Select(static issue => (JsonNode)new JsonObject
        {
            ["operation_id"] = issue.OperationId,
            ["status"] = issue.Status,
            ["reason"] = issue.Reason,
            ["candidate_catalog_ids"] = new JsonArray(issue.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }).ToArray();
        return $$"""
            You are a domain-neutral capability matcher repairing a previous matching contract. Return only the requested structured JSON.

            Return every operation and constraint exactly once. Preserve all locked decisions exactly. Resolve each reported issue from the documented catalog only. For operations use matched for one sufficient ID, composed for two or more necessary complementary IDs, conditional for one mutually exclusive selector subset chosen by the locked decision_source_operation_id plus any necessary complementary unconditional prerequisites, local only for local_processing, ambiguous for unresolved user intent, and unavailable only when no sufficient implementation exists. For conditional, copy decision_source_operation_id exactly into decision_operation_id; otherwise leave decision_operation_id empty. Conditional variants share one physical capability and the same selector paths, differing on exactly one selector; prerequisites execute once outside the branch. A runtime-dependent result is not user ambiguity. A complete_operation wrapper replaces its encapsulated phases. For constraints use enforced only when enforcement_kind=exact_denial and exact denied MCP IDs are established; use policy_only only when enforcement_kind=workflow_policy; use ambiguous only for unresolved exact-denial candidates. Select the smallest sufficient composition and never invent IDs.

            A repaired match must also be prerequisite-closed. Check required arguments and bounded output fields on every selected catalog card. If a capability requires an existing external artifact that is not a semantically compatible workflow runtime input or documented host-internal/default value, include the producer capability whose documented output supplies it. Local processing, URLs, identifiers, and invented strings do not create or prove workspaces, project roots, directories, files, handles, or exact comparison payloads. Ordinary scalar values and identifiers may still be parsed from declared inputs or reused from an already selected upstream read, so never repeat a read in every match merely to resupply them. Retain a complementary producer only for a documented artifact dependency or concrete multi-call prerequisite, and prefer the unique most-specific exact selector over its broader or partial selector entries. A high-level capability is sufficient alone only when its documented contract encapsulates those prerequisites.

            <locked_valid_operations>
            {{new JsonArray(lockedOperations).ToJsonString()}}
            </locked_valid_operations>
            <locked_valid_constraints>
            {{new JsonArray(lockedConstraints).ToJsonString()}}
            </locked_valid_constraints>
            <matching_issues>
            {{new JsonArray(issues).ToJsonString()}}
            </matching_issues>
            <runtime_inventory>
            {{BuildCapabilityInventoryJson(inventory)}}
            </runtime_inventory>
            <capability_catalog>
            {{catalog.Text}}
            </capability_catalog>
            """;
    }

    private static void ThrowForUnresolvedCapabilityMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog,
        bool repairAttempted)
    {
        var blocking = evaluation.Issues.Where(static issue => issue.Required).Take(64).ToArray();
        if (evaluation.ContractValid && blocking.Length == 0)
            return;

        if (blocking.Length == 0)
        {
            blocking = [new CapabilityMatchingIssue("matching_contract", "Capability matching contract", true, "invalid",
                "The matching response remained malformed after validation.", Array.Empty<string>())];
        }
        var entryMap = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var issueNodes = new JsonArray(blocking.Select(issue => (JsonNode)new JsonObject
        {
            ["operation_id"] = SanitizeCapabilityInferenceDiagnostic(issue.OperationId, 160),
            ["description"] = SanitizeCapabilityInferenceDiagnostic(issue.Description, 1_000),
            ["required"] = issue.Required,
            ["status"] = issue.Status,
            ["reason"] = SanitizeCapabilityInferenceDiagnostic(issue.Reason, 1_000),
            ["candidate_capabilities"] = new JsonArray(issue.CandidateCatalogIds
                .Where(entryMap.ContainsKey)
                .Take(8)
                .Select(id => (JsonNode)BuildCapabilityCandidateCard(entryMap[id])).ToArray())
        }).ToArray());
        var onlyUnavailable = evaluation.ContractValid && blocking.All(static issue => issue.Status == "unavailable");
        var unavailable = onlyUnavailable
            ? evaluation.OperationMatches.Where(static match => match.Operation.Required && match.Status == "unavailable")
                .Select(static match => (JsonNode)new JsonObject
                {
                    ["id"] = match.Operation.Id,
                    ["description"] = match.Operation.Description,
                    ["required"] = true,
                    ["reason"] = "no_matching_discovered_capability"
                }).ToArray()
            : Array.Empty<JsonNode>();
        throw new WorkflowRuntimeException(
            onlyUnavailable ? ErrorCodes.CapabilityPreflightUnavailable : ErrorCodes.CapabilityPreflightInferenceFailed,
            onlyUnavailable
                ? "One or more required runtime operations have no matching discovered capability."
                : "Capability matching remained ambiguous or invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_matching",
                ["repair_attempted"] = repairAttempted,
                ["attempts"] = repairAttempted ? 2 : 1,
                ["matching_issues"] = issueNodes,
                ["unavailable_capabilities"] = new JsonArray(unavailable),
                ["planning_outcome"] = onlyUnavailable ? "unsupported" : "cannot_plan_safely",
                ["recommended_action"] = onlyUnavailable ? "configure_capability_or_revise_request" : "clarify_or_abandon"
            });
    }

    private static JsonObject BuildCapabilityCandidateCard(CapabilityCatalogEntry entry) => new()
    {
        ["catalog_id"] = entry.Id,
        ["resolution"] = entry.Resolution,
        ["server"] = entry.Server,
        ["kind"] = entry.Kind,
        ["method"] = entry.Method,
        ["request_bindings"] = BuildRequestBindingsJson(entry.RequestBindings)
    };

    private static (IReadOnlyList<ResolvedCapability>, IReadOnlyList<CapabilityConstraint>) ResolveCapabilityMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var retainedMaterializerOccurrences = FindRetainedMaterializerOccurrences(evaluation, entries);
        var retainedSharedWriteOccurrences = FindRetainedSharedWriteOccurrences(evaluation, entries);
        var resolved = new List<ResolvedCapability>();
        foreach (var match in evaluation.OperationMatches)
        {
            if (match.Status == "local")
            {
                resolved.Add(new ResolvedCapability(match.Operation.Id, match.Operation.Description, match.Operation.Required,
                    "local", null, null, null, Array.Empty<CapabilityRequestBinding>(), match.Operation.Id, null, match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind));
                continue;
            }
            if (match.Status == "unavailable")
            {
                resolved.Add(new ResolvedCapability(match.Operation.Id, match.Operation.Description, match.Operation.Required,
                    "unavailable", null, null, null, Array.Empty<CapabilityRequestBinding>(), match.Operation.Id, null, match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind));
                continue;
            }
            IReadOnlyDictionary<string, string> conditionalBranches = new Dictionary<string, string>(StringComparer.Ordinal);
            if (match.Status == "conditional"
                && !TryBuildConditionalCompositionBranchValues(match.CatalogIds.Select(id => entries[id]).ToArray(), out conditionalBranches))
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    $"Conditional capability operation '{match.Operation.Id}' does not contain valid mutually exclusive selector variants.");
            }

            foreach (var catalogId in match.CatalogIds)
            {
                var entry = entries[catalogId];
                if (IsArtifactMaterializer(entry)
                    && !retainedMaterializerOccurrences.Contains((match.Operation.Id, catalogId)))
                {
                    continue;
                }
                if (string.Equals(match.Operation.ExternalEffectKind, "write", StringComparison.Ordinal)
                    && !retainedSharedWriteOccurrences.Contains((match.Operation.Id, catalogId)))
                {
                    continue;
                }
                var id = match.CatalogIds.Count == 1 ? match.Operation.Id : $"{match.Operation.Id}::{catalogId}";
                var isConditionalBranch = match.Status == "conditional" && conditionalBranches.ContainsKey(catalogId);
                var activation = isConditionalBranch
                    ? new McpCapabilityActivation(
                        "exactly_one",
                        match.Operation.Id,
                        match.DecisionOperationId!,
                        conditionalBranches[catalogId])
                    : null;
                resolved.Add(new ResolvedCapability(id, match.Operation.Description, match.Operation.Required,
                    entry.Resolution, entry.Server, entry.Kind, entry.Method, entry.RequestBindings,
                    match.Operation.Id, catalogId, match.Status == "conditional" && !isConditionalBranch ? "composed" : match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind, activation, entry.Description));
            }
        }

        var constraints = new List<CapabilityConstraint>(evaluation.ConstraintMatches.Count);
        foreach (var match in evaluation.ConstraintMatches)
        {
            var alternatives = match.Status == "enforced"
                ? match.DeniedCatalogIds.Select(id => entries[id])
                    .Select(static entry => new CapabilityAlternative(entry.Server!, entry.Kind!, entry.Method, entry.RequestBindings)).ToArray()
                : Array.Empty<CapabilityAlternative>();
            constraints.Add(new CapabilityConstraint(match.Constraint.Id, match.Constraint.Description, match.Constraint.Required, alternatives));
        }
        return (CoalescePlatformConfirmationCapabilities(resolved), constraints);
    }

    private static IReadOnlyList<ResolvedCapability> CoalescePlatformConfirmationCapabilities(
        IReadOnlyList<ResolvedCapability> capabilities)
    {
        var platformConfirmations = capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
                                        && capability.OperationId?.StartsWith(
                                            "platform_confirm_external_write",
                                            StringComparison.Ordinal) == true
                                        && string.Equals(
                                            capability.Description,
                                            PlatformExternalWriteConfirmationOperationDescription,
                                            StringComparison.Ordinal))
            .ToArray();
        if (platformConfirmations.Length != 1)
            return capabilities;

        var platformConfirmation = platformConfirmations[0];
        var compatibleExisting = capabilities
            .Where(capability => !ReferenceEquals(capability, platformConfirmation)
                                 && capability.Required
                                 && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
                                 && string.Equals(capability.Method, platformConfirmation.Method, StringComparison.Ordinal)
                                 && string.Equals(capability.CatalogId, platformConfirmation.CatalogId, StringComparison.Ordinal)
                                 && capability.Activation == null
                                 && (string.Equals(capability.ExecutionKind, "human_interaction", StringComparison.Ordinal)
                                     || string.Equals(capability.ExternalEffectKind, "write", StringComparison.Ordinal)))
            .ToArray();
        if (compatibleExisting.Length != 1)
            return capabilities;

        var existing = compatibleExisting[0];
        var operationIds = GetResolvedCapabilityOperationIds(existing)
            .Concat(GetResolvedCapabilityOperationIds(platformConfirmation))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var coalesced = existing with { OperationIds = operationIds };
        return capabilities
            .Where(capability => !ReferenceEquals(capability, existing)
                                 && !ReferenceEquals(capability, platformConfirmation))
            .Append(coalesced)
            .ToArray();
    }

    private static IReadOnlyList<string> GetResolvedCapabilityOperationIds(ResolvedCapability capability)
        => capability.OperationIds is { Count: > 0 }
            ? capability.OperationIds
            : !string.IsNullOrWhiteSpace(capability.OperationId)
                ? [capability.OperationId]
                : [capability.Id];

    private static HashSet<(string OperationId, string CatalogId)> FindRetainedSharedWriteOccurrences(
        CapabilityMatchingEvaluation evaluation,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries)
    {
        var occurrences = new List<SharedWriteOccurrence>();
        foreach (var match in evaluation.OperationMatches.Where(static match =>
                     match.Status is "matched" or "composed" or "conditional"
                     && string.Equals(match.Operation.ExternalEffectKind, "write", StringComparison.Ordinal)))
        {
            var selectedIds = match.CatalogIds.Where(entries.ContainsKey).ToArray();
            IReadOnlyDictionary<string, string> conditionalBranches = new Dictionary<string, string>(StringComparer.Ordinal);
            if (match.Status == "conditional")
                TryBuildConditionalCompositionBranchValues(selectedIds.Select(id => entries[id]).ToArray(), out conditionalBranches);

            foreach (var catalogId in selectedIds)
            {
                // A single-capability operation owns its invocation. A selector variant
                // in an exactly-one conditional group also owns its terminal invocation.
                // The same catalog entry repeated only as an unconditional member of a
                // larger composition is a shared prerequisite and must reuse that owner.
                occurrences.Add(new SharedWriteOccurrence(
                    match.Operation.Id,
                    catalogId,
                    selectedIds.Length == 1 || conditionalBranches.ContainsKey(catalogId)));
            }
        }

        return SelectRetainedSharedWriteOccurrences(occurrences);
    }

    private static HashSet<(string OperationId, string CatalogId)> SelectRetainedSharedWriteOccurrences(
        IReadOnlyList<SharedWriteOccurrence> occurrences)
    {
        var retained = new HashSet<(string OperationId, string CatalogId)>();
        foreach (var group in occurrences.GroupBy(static value => value.CatalogId, StringComparer.Ordinal))
        {
            var ownedSources = group.Where(static value => value.IsOwnedSource).ToArray();
            if (ownedSources.Length == 0)
            {
                foreach (var value in group)
                    retained.Add((value.OperationId, value.CatalogId));
                continue;
            }

            foreach (var owner in ownedSources)
                retained.Add((owner.OperationId, owner.CatalogId));
        }
        return retained;
    }

    private static HashSet<(string OperationId, string CatalogId)> FindRetainedMaterializerOccurrences(
        CapabilityMatchingEvaluation evaluation,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries)
    {
        var occurrences = new Dictionary<string, List<(string OperationId, bool IsOwnedSource)>>(StringComparer.Ordinal);
        foreach (var match in evaluation.OperationMatches.Where(static match => match.Status is "matched" or "composed" or "conditional"))
        {
            var selected = match.CatalogIds
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            foreach (var materializer in selected.Where(IsArtifactMaterializer))
            {
                var producedKinds = GetMaterializedArtifactKinds(materializer);
                var isPrerequisite = selected.Any(other =>
                    !string.Equals(other.Id, materializer.Id, StringComparison.Ordinal)
                    && producedKinds.Any(kind => CapabilityRequiresArtifactKind(other, kind)));
                if (!occurrences.TryGetValue(materializer.Id, out var values))
                {
                    values = [];
                    occurrences[materializer.Id] = values;
                }
                values.Add((match.Operation.Id, !isPrerequisite));
            }
        }

        var retained = new HashSet<(string OperationId, string CatalogId)>();
        foreach (var (catalogId, values) in occurrences)
        {
            var ownedSources = values.Where(static value => value.IsOwnedSource).ToArray();
            if (ownedSources.Length > 0)
            {
                foreach (var owner in ownedSources)
                    retained.Add((owner.OperationId, catalogId));
                continue;
            }

            var sharedPrerequisite = values.First();
            retained.Add((sharedPrerequisite.OperationId, catalogId));
        }
        return retained;
    }

    private static bool IsArtifactMaterializer(CapabilityCatalogEntry entry)
        => GetMaterializedArtifactKinds(entry).Count > 0;

    private static IReadOnlyList<string> GetMaterializedArtifactKinds(CapabilityCatalogEntry entry)
        => entry.ArtifactContract?.Produces
               .Where(static artifact => string.Equals(
                   artifact.Mode,
                   McpArtifactContractConventions.MaterializeMode,
                   StringComparison.Ordinal))
               .Select(static artifact => artifact.Kind)
               .Distinct(StringComparer.Ordinal)
               .ToArray()
           ?? Array.Empty<string>();

    private static bool TryReadComplete(JsonObject json, out bool complete)
    {
        complete = false;
        return json["complete"] is JsonValue value && value.TryGetValue(out complete);
    }

    private static void ValidateCapabilityConstraints(
        IReadOnlyList<CapabilityConstraint> constraints,
        IReadOnlyList<McpServerDiscovery> discovered)
    {
        foreach (var constraint in constraints)
        {
            foreach (var alternative in constraint.DeniedAlternatives)
            {
                var server = discovered.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, alternative.Server, StringComparison.Ordinal));
                var exists = server?.Discovered == true && (alternative.Kind == "prompt"
                    ? server.Prompts.Any(prompt => string.Equals(prompt.Name, alternative.Method, StringComparison.Ordinal))
                    : server.Tools.Any(tool => string.Equals(tool.Name, alternative.Method, StringComparison.Ordinal)));
                if (!exists || !AlternativeBindingsMatchSchema(alternative, server!))
                {
                    throw new WorkflowRuntimeException(
                        ErrorCodes.CapabilityPreflightInferenceFailed,
                        $"Capability constraint '{constraint.Id}' references an exact denied capability that was not discovered.");
                }
            }
        }
    }

    private static void ValidateResolvedCapabilities(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<McpServerDiscovery> discovered,
        StepExecutionContext ctx,
        JsonObject input)
    {
        var unavailable = new List<ResolvedCapability>();
        var allowedNativeTypes = ResolveAllowedNativeStepTypes(ctx, input);
        foreach (var capability in capabilities)
        {
            if (!capability.Required)
                continue;

            if (capability.Resolution == "local")
                continue;

            if (capability.Resolution == "native")
            {
                if (string.IsNullOrWhiteSpace(capability.Method) || !allowedNativeTypes.Contains(capability.Method))
                    unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null, RequestBindings = Array.Empty<CapabilityRequestBinding>() });
                continue;
            }

            if (capability.Resolution != "mcp"
                || string.IsNullOrWhiteSpace(capability.Server)
                || capability.Kind is not ("tool" or "prompt")
                || string.IsNullOrWhiteSpace(capability.Method))
            {
                unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null, RequestBindings = Array.Empty<CapabilityRequestBinding>() });
                continue;
            }

            var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, capability.Server, StringComparison.Ordinal));
            var exists = server?.Discovered == true && (capability.Kind == "prompt"
                ? server.Prompts.Any(prompt => string.Equals(prompt.Name, capability.Method, StringComparison.Ordinal))
                : server.Tools.Any(tool => string.Equals(tool.Name, capability.Method, StringComparison.Ordinal)));
            var bindingsValid = capability.Kind != "tool" || server != null && AlternativeBindingsMatchSchema(
                new CapabilityAlternative(capability.Server!, capability.Kind, capability.Method!, capability.RequestBindings), server);
            if (!exists || !bindingsValid)
                unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null, RequestBindings = Array.Empty<CapabilityRequestBinding>() });
        }

        if (unavailable.Count > 0)
            ThrowCapabilityPreflightFailure(
                ErrorCodes.CapabilityPreflightUnavailable,
                "One or more required operations cannot be satisfied by the discovered MCP capabilities or allowed native steps.",
                Array.Empty<string>(),
                unavailable);
    }

    private static void ThrowCapabilityPreflightFailure(
        string code,
        string message,
        IReadOnlyList<string> unavailableServers,
        IReadOnlyList<ResolvedCapability> unavailableCapabilities,
        string reason = "no_matching_discovered_capability")
    {
        var serverArray = new JsonArray();
        foreach (var server in unavailableServers)
            serverArray.Add((JsonNode?)JsonValue.Create(server));
        var capabilityArray = new JsonArray();
        foreach (var capability in unavailableCapabilities)
        {
            capabilityArray.Add((JsonNode)new JsonObject
            {
                ["id"] = capability.Id,
                ["description"] = capability.Description,
                ["required"] = capability.Required,
                ["reason"] = reason,
                ["operation_id"] = capability.OperationId,
                ["catalog_id"] = capability.CatalogId,
                ["resolution"] = capability.Resolution,
                ["server"] = capability.Server,
                ["kind"] = capability.Kind,
                ["method"] = capability.Method,
                ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings)
            });
        }

        throw new WorkflowRuntimeException(
            code,
            message,
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["unavailable_servers"] = serverArray,
                ["unavailable_capabilities"] = capabilityArray,
                ["planning_outcome"] = "unsupported",
                ["recommended_action"] = "configure_capability_or_revise_request"
            });
    }

    private static string FormatResolvedCapabilityReference(ResolvedCapability capability)
    {
        var operationId = capability.OperationId ?? capability.Id;
        if (string.Equals(capability.Resolution, "native", StringComparison.Ordinal))
            return $"{operationId} -> native/{capability.Method}";

        var bindings = capability.RequestBindings.Count == 0
            ? string.Empty
            : $" [{FormatBindingsCompact(capability.RequestBindings)}]";
        return $"{operationId} -> {capability.Server}/{capability.Method}{bindings}";
    }

    private static List<McpServerDiscovery> MergeLockedCapabilitiesIntoDiscovery(
        IReadOnlyList<McpServerDiscovery>? selected,
        IReadOnlyList<McpServerDiscovery> complete,
        CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled)
            return selected?.ToList() ?? new List<McpServerDiscovery>();

        var requiredByServer = preflight.RequiredMcpCapabilities
            .GroupBy(static capability => capability.Server!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var result = selected?.Select(CloneDiscovery).ToList() ?? new List<McpServerDiscovery>();
        foreach (var (serverName, capabilities) in requiredByServer)
        {
            var source = complete.First(server => string.Equals(server.Name, serverName, StringComparison.Ordinal));
            var index = result.FindIndex(server => string.Equals(server.Name, serverName, StringComparison.Ordinal));
            var existing = index >= 0 ? result[index] : null;
            var tools = (existing?.Tools ?? Array.Empty<McpToolInfo>()).ToList();
            var prompts = (existing?.Prompts ?? Array.Empty<McpPromptInfo>()).ToList();
            foreach (var capability in capabilities)
            {
                if (capability.Kind == "prompt")
                {
                    var prompt = source.Prompts.First(item => string.Equals(item.Name, capability.Method, StringComparison.Ordinal));
                    if (prompts.All(item => !string.Equals(item.Name, prompt.Name, StringComparison.Ordinal)))
                        prompts.Add(prompt);
                }
                else
                {
                    var tool = source.Tools.First(item => string.Equals(item.Name, capability.Method, StringComparison.Ordinal));
                    if (tools.All(item => !string.Equals(item.Name, tool.Name, StringComparison.Ordinal)))
                        tools.Add(tool);
                }
            }

            var merged = new McpServerDiscovery
            {
                Name = source.Name,
                Description = source.Description,
                CallTimeoutSeconds = source.CallTimeoutSeconds,
                Discovered = source.Discovered,
                Tools = tools,
                Prompts = prompts
            };
            if (index >= 0)
                result[index] = merged;
            else
                result.Add(merged);
        }

        return result;
    }

    private static List<McpServerDiscovery> ExpandSelectedOperationalArtifactPrerequisites(
        IReadOnlyList<McpServerDiscovery>? selected,
        IReadOnlyList<McpServerDiscovery> complete,
        string userInstruction)
    {
        var result = selected?.Select(CloneDiscovery).ToList() ?? new List<McpServerDiscovery>();
        if (result.Count == 0 || complete.Count == 0)
            return result;

        // A prefilter is allowed to choose a high-level consumer without knowing that
        // one of its required arguments denotes an already materialized resource. Keep
        // every documented producer candidate visible so extraction can compose the
        // prerequisite instead of fabricating a public input or locator.
        for (var pass = 0; pass < CapabilitySchemaMaxDepth; pass++)
        {
            var missingKinds = result
                .SelectMany(static server => server.Tools)
                .SelectMany(GetRequiredArtifactRequirements)
                .Where(item => !IsExplicitCallerArtifactInput(userInstruction, item.Field, item.Kind))
                .Where(item => !SelectedDiscoveryProducesArtifactKind(result, item.Kind))
                .Select(static item => item.Kind)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missingKinds.Length == 0)
                break;

            var added = false;
            foreach (var kind in missingKinds)
            {
                var candidates = complete
                    .SelectMany(server => server.Tools.Select(tool => (Server: server, Tool: tool)))
                    .Where(item => ToolProducesArtifactKind(item.Tool, kind))
                    .Where(item => !ToolRequiresArtifactKind(item.Tool, kind))
                    .OrderBy(static item => item.Server.Name, StringComparer.Ordinal)
                    .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
                foreach (var candidate in candidates)
                    added |= AddToolToDiscovery(result, candidate.Server, candidate.Tool);
            }

            if (!added)
                break;
        }

        return result;
    }

    private static bool SelectedDiscoveryProducesArtifactKind(
        IReadOnlyList<McpServerDiscovery> selected,
        string kind)
        => selected.SelectMany(static server => server.Tools)
            .Any(tool => ToolProducesArtifactKind(tool, kind)
                         && !ToolRequiresArtifactKind(tool, kind));

    private static bool ToolProducesArtifactKind(McpToolInfo tool, string kind)
    {
        var contract = GetValidatedMcpArtifactContract(tool);
        return contract != null
            ? contract.Produces.Any(artifact =>
                string.Equals(artifact.Kind, kind, StringComparison.Ordinal)
                && string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal))
            : BuildCapabilitySchemaFields(tool.OutputSchema, requiredOnly: false)
                .Any(field => string.Equals(GetOperationalArtifactKind(field), kind, StringComparison.Ordinal)
                              && ArtifactOutputDescriptionProvesExistence(field.Description));
    }

    private static bool ToolRequiresArtifactKind(McpToolInfo tool, string kind)
    {
        var contract = GetValidatedMcpArtifactContract(tool);
        return contract != null
            ? contract.Consumes.Any(artifact =>
                artifact.Required && string.Equals(artifact.Kind, kind, StringComparison.Ordinal))
            : BuildCapabilitySchemaFields(tool.InputSchema, requiredOnly: true)
                .Any(field => string.Equals(GetOperationalArtifactKind(field), kind, StringComparison.Ordinal));
    }

    private static IReadOnlyList<CapabilityArtifactRequirement> GetRequiredArtifactRequirements(McpToolInfo tool)
    {
        var contract = GetValidatedMcpArtifactContract(tool);
        return contract != null
            ? contract.Consumes
                .Where(static artifact => artifact.Required)
                .Select(static artifact => new CapabilityArtifactRequirement(
                    new CapabilitySchemaField(
                        artifact.Pointer,
                        "string",
                        $"Required MCP-declared artifact of kind {artifact.Kind}."),
                    artifact.Kind))
                .ToArray()
            : BuildCapabilitySchemaFields(tool.InputSchema, requiredOnly: true)
                .Select(static field => new CapabilityArtifactRequirement(
                    field,
                    GetOperationalArtifactKind(field) ?? string.Empty))
                .Where(static requirement => requirement.Kind.Length > 0)
                .ToArray();
    }

    private static McpArtifactContract? GetValidatedMcpArtifactContract(
        McpToolInfo tool,
        string? serverName = null)
    {
        var validation = tool.ArtifactContract;
        if (validation == null)
            return null;
        if (validation.Errors.Count == 0)
            return validation.Contract;

        var identity = string.IsNullOrWhiteSpace(serverName)
            ? tool.Name
            : serverName + "/" + tool.Name;
        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"MCP tool '{identity}' advertises an invalid GnOuGo artifact contract.",
            details: new JsonObject
            {
                ["phase"] = "mcp_artifact_contract",
                ["server"] = serverName,
                ["tool"] = tool.Name,
                ["errors"] = new JsonArray(validation.Errors.Select(static error => (JsonNode)JsonValue.Create(error)!).ToArray())
            });
    }

    private static McpCapabilityComposition? GetValidatedMcpCompositionContract(
        McpToolInfo tool,
        string? serverName = null)
    {
        var validation = tool.CompositionContract;
        if (validation == null)
            return null;
        if (validation.Errors.Count == 0 && validation.Contract is { } contract)
        {
            if (contract.Encapsulates.Any(capability =>
                    string.Equals(capability.Kind, "tool", StringComparison.Ordinal)
                    && string.Equals(capability.Method, tool.Name, StringComparison.Ordinal)))
            {
                validation = validation with
                {
                    Errors = ["A composition contract cannot encapsulate the declaring tool itself."]
                };
            }
            else
            {
                return contract;
            }
        }

        var identity = string.IsNullOrWhiteSpace(serverName)
            ? tool.Name
            : serverName + "/" + tool.Name;
        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"MCP tool '{identity}' advertises an invalid GnOuGo composition contract.",
            details: new JsonObject
            {
                ["phase"] = "mcp_composition_contract",
                ["server"] = serverName,
                ["tool"] = tool.Name,
                ["errors"] = new JsonArray(validation.Errors.Select(static error => (JsonNode)JsonValue.Create(error)!).ToArray())
            });
    }

    private static bool AddToolToDiscovery(
        List<McpServerDiscovery> result,
        McpServerDiscovery source,
        McpToolInfo tool)
    {
        var index = result.FindIndex(server => string.Equals(server.Name, source.Name, StringComparison.Ordinal));
        var existing = index >= 0 ? result[index] : null;
        if (existing?.Tools.Any(item => string.Equals(item.Name, tool.Name, StringComparison.Ordinal)) == true)
            return false;

        var tools = (existing?.Tools ?? Array.Empty<McpToolInfo>()).ToList();
        tools.Add(tool);
        var merged = new McpServerDiscovery
        {
            Name = source.Name,
            Description = source.Description,
            CallTimeoutSeconds = source.CallTimeoutSeconds,
            Discovered = source.Discovered,
            Tools = tools,
            Prompts = existing?.Prompts ?? Array.Empty<McpPromptInfo>()
        };
        if (index >= 0)
            result[index] = merged;
        else
            result.Add(merged);
        return true;
    }

    private static WorkflowPipelineExtraction ValidatePlannedToolArtifactPrerequisites(
        WorkflowPipelineExtraction extraction,
        PipelineMcpContext mcpContext,
        string userInstruction)
    {
        var plannedTools = extraction.Subworkflows
            .SelectMany(spec => spec.PlannedTools.Select(tool => (Spec: spec, Tool: tool)))
            .ToArray();
        if (plannedTools.Length == 0 || mcpContext.Servers.Count == 0)
            return extraction;

        var discovered = mcpContext.Servers
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Tool: tool)))
            .ToArray();
        var plannedDiscovered = plannedTools
            .Select(planned =>
            {
                var info = discovered.FirstOrDefault(item =>
                    string.Equals(item.Server, planned.Tool.Server, StringComparison.Ordinal)
                    && string.Equals(item.Tool.Name, planned.Tool.Method, StringComparison.Ordinal));
                return (planned.Spec, planned.Tool, Info: info.Tool);
            })
            .Where(static item => item.Info != null)
            .ToArray();
        if (plannedDiscovered.Length == 0)
            return extraction;

        var errors = extraction.ValidationErrors.ToList();
        var rootCauses = extraction.RootCauses.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in plannedDiscovered)
        {
            var requiredArtifacts = GetRequiredArtifactRequirements(consumer.Info!)
                .Where(item => !IsExplicitCallerArtifactInput(userInstruction, item.Field, item.Kind))
                .ToArray();
            foreach (var requirement in requiredArtifacts)
            {
                if (plannedDiscovered.Any(producer =>
                        ToolProducesArtifactKind(producer.Info!, requirement.Kind)
                        && !ToolRequiresArtifactKind(producer.Info!, requirement.Kind)))
                {
                    continue;
                }

                var key = consumer.Spec.Name + "\u001f" + consumer.Tool.Server + "\u001f"
                          + consumer.Tool.Method + "\u001f" + requirement.Kind;
                if (!seen.Add(key))
                    continue;

                var candidates = discovered
                    .Where(item => ToolProducesArtifactKind(item.Tool, requirement.Kind))
                    .Where(item => !ToolRequiresArtifactKind(item.Tool, requirement.Kind))
                    .Select(static item => item.Server + "/" + item.Tool.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
                var candidateText = candidates.Length == 0
                    ? "No compatible producer is present in the selected MCP context."
                    : "Documented producer candidates: " + string.Join(", ", candidates) + ".";
                var message = $"PIPELINE_EXTRACTION_ARTIFACT_PREREQUISITE_MISSING: Leaf '{consumer.Spec.Name}' plans {consumer.Tool.Server}/{consumer.Tool.Method}, which requires an existing operational artifact at '{requirement.Field.Path}', but no planned producer creates it and the user did not supply it as a runtime input. {candidateText} Add a producer leaf and route its documented response field; do not invent or expose the locator as a new public input.";
                errors.Add(message);
                rootCauses.Add(new PipelineRootCause(
                    "artifact_prerequisite_missing",
                    "pipeline_extraction",
                    consumer.Spec.Name,
                    null,
                    requirement.Field.Path,
                    "PIPELINE_EXTRACTION_ARTIFACT_PREREQUISITE_MISSING",
                    message,
                    true));
            }
        }

        return errors.Count == extraction.ValidationErrors.Count
            ? extraction
            : extraction with { ValidationErrors = errors, RootCauses = rootCauses };
    }

    private static McpServerDiscovery CloneDiscovery(McpServerDiscovery source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        CallTimeoutSeconds = source.CallTimeoutSeconds,
        Discovered = source.Discovered,
        Tools = source.Tools,
        Prompts = source.Prompts
    };

    private static string FormatLockedCapabilities(CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled || preflight.Capabilities.Count == 0 && preflight.Constraints.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("The following operation and capability decisions are locked by preflight. Required MCP capabilities must appear as exact direct mcp.call operations; do not omit, collapse, rename, or replace them. Repeated unconditional entries are separate invocation obligations even when they select the same physical capability.");
        sb.AppendLine("Capabilities with activation mode exactly_one are mutually exclusive alternatives, not sequential calls. Put every member of one activation group in a distinct literal-value case of one expression-based switch driven by the named decision operation. Use each branch_value as the exact case value, execute exactly one matching call per case, and do not put any external write in the default branch. The decision must be computed from runtime results; never ask the human to predict it during generation.");
        foreach (var capability in preflight.Capabilities.Where(static item => item.Required))
        {
            sb.Append("- ").Append(capability.Id).Append(": ").Append(capability.Resolution);
            var operationIds = GetResolvedCapabilityOperationIds(capability);
            if (operationIds.Count == 1)
                sb.Append(" operation_id=").Append(operationIds[0]);
            else if (operationIds.Count > 1)
                sb.Append(" operation_ids=[").Append(string.Join(",", operationIds)).Append(']');
            if (!string.IsNullOrWhiteSpace(capability.CatalogId))
                sb.Append(" catalog_id=").Append(capability.CatalogId);
            if (capability.Resolution == "mcp")
            {
                sb.Append(' ').Append(capability.Server).Append('/').Append(capability.Method).Append(" (").Append(capability.Kind).Append(')');
                if (capability.RequestBindings.Count > 0)
                    sb.Append(" request_bindings=[").Append(FormatBindingsCompact(capability.RequestBindings)).Append(']');
                if (capability.Activation is { } activation)
                {
                    sb.Append(" activation=[mode=").Append(activation.Mode)
                        .Append(" group=").Append(activation.Group)
                        .Append(" decision_operation_id=").Append(activation.DecisionOperationId)
                        .Append(" branch_value=").Append(activation.BranchValue).Append(']');
                }
            }
            else if (capability.Resolution == "native")
                sb.Append(' ').Append(capability.Method);
            else if (capability.Resolution == "local")
                sb.Append(" (preserve this local-processing obligation in the blueprint; choose its concrete Flow implementation during decomposition)");
            sb.Append(" — ").AppendLine(capability.Description);
        }
        if (preflight.Constraints.Count > 0)
        {
            sb.AppendLine("The following task constraints are also locked. They are invariants, not operations. Never invent a tool call to satisfy a prohibition.");
            foreach (var constraint in preflight.Constraints.Where(static item => item.Required))
            {
                sb.Append("- ").Append(constraint.Id).Append(": ").AppendLine(constraint.Description);
                foreach (var denied in constraint.DeniedAlternatives)
                {
                    sb.Append("  denied: ").Append(denied.Server).Append('/').Append(denied.Method).Append(" (").Append(denied.Kind).AppendLine(")");
                    if (denied.RequestBindings.Count > 0)
                        sb.Append("    request_bindings: [").Append(FormatBindingsCompact(denied.RequestBindings)).AppendLine("]");
                }
            }
        }
        return sb.ToString();
    }

    private static JsonObject BuildCapabilityPreflightJson(CapabilityPreflightResult preflight)
    {
        var capabilities = new JsonArray();
        foreach (var capability in preflight.Capabilities)
        {
            capabilities.Add((JsonNode)new JsonObject
            {
                ["id"] = capability.Id,
                ["description"] = capability.Description,
                ["required"] = capability.Required,
                ["resolution"] = capability.Resolution,
                ["operation_id"] = capability.OperationId,
                ["operation_ids"] = BuildStringArrayJson(GetResolvedCapabilityOperationIds(capability)),
                ["catalog_id"] = capability.CatalogId,
                ["match_status"] = capability.MatchStatus,
                ["execution_kind"] = capability.ExecutionKind,
                ["external_effect_kind"] = capability.ExternalEffectKind,
                ["server"] = capability.Server,
                ["kind"] = capability.Kind,
                ["method"] = capability.Method,
                ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings),
                ["activation"] = capability.Activation == null
                    ? null
                    : new JsonObject
                    {
                        ["mode"] = capability.Activation.Mode,
                        ["group"] = capability.Activation.Group,
                        ["decision_operation_id"] = capability.Activation.DecisionOperationId,
                        ["branch_value"] = capability.Activation.BranchValue
                    }
            });
        }

        var constraints = new JsonArray();
        foreach (var constraint in preflight.Constraints)
        {
            var denied = new JsonArray();
            foreach (var alternative in constraint.DeniedAlternatives)
            {
                denied.Add((JsonNode)new JsonObject
                {
                    ["server"] = alternative.Server,
                    ["kind"] = alternative.Kind,
                    ["method"] = alternative.Method,
                    ["request_bindings"] = BuildRequestBindingsJson(alternative.RequestBindings)
                });
            }
            constraints.Add((JsonNode)new JsonObject
            {
                ["id"] = constraint.Id,
                ["description"] = constraint.Description,
                ["required"] = constraint.Required,
                ["denied_alternatives"] = denied
            });
        }

        return new JsonObject
        {
            ["mode"] = preflight.Mode,
            ["capabilities"] = capabilities,
            ["constraints"] = constraints
        };
    }

    private static void ValidateLockedCapabilitiesInDocument(
        WorkflowDocument document,
        CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled
            || preflight.RequiredMcpCapabilities.Count == 0
            && preflight.RequiredNativeCapabilities.Count == 0
            && preflight.RequiredLocalOperations.Count == 0
            && preflight.Constraints.All(static constraint => !constraint.Required || constraint.DeniedAlternatives.Count == 0))
            return;

        var steps = document.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
            .ToArray();
        var calls = steps.Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)).ToArray();
        var missing = FindMissingMcpCapabilityInvocations(preflight.RequiredMcpCapabilities, calls)
            .Concat(FindMissingNativeCapabilityInvocations(preflight.RequiredNativeCapabilities, steps)).ToArray();

        if (missing.Length > 0)
        {
            var missingSummary = string.Join(", ", missing.Select(FormatResolvedCapabilityReference));
            ThrowCapabilityPreflightFailure(
                ErrorCodes.CapabilityPreflightUnavailable,
                $"Generated workflow omitted required capabilities locked by capability preflight: {missingSummary}.",
                Array.Empty<string>(),
                missing,
                "generated_required_capability_omitted");
        }

        ValidateNoRedundantArtifactMaterializers(preflight, calls);
        ValidateMcpArtifactDataflow(document, preflight);
        ValidateConditionalCapabilityActivation(document, preflight);

        var deniedCalls = preflight.Constraints
            .Where(static constraint => constraint.Required)
            .SelectMany(static constraint => constraint.DeniedAlternatives.Select(alternative => (constraint, alternative)))
            .Where(item => calls.Any(step => McpStepMatchesCapability(
                step,
                item.alternative.Server,
                item.alternative.Kind,
                item.alternative.Method,
                item.alternative.RequestBindings)))
            .ToArray();
        if (deniedCalls.Length > 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightUnavailable,
                "Generated workflow invoked one or more capabilities denied by locked task constraints.",
                details: new JsonObject
                {
                    ["phase"] = "capability_preflight",
                    ["violated_constraints"] = new JsonArray(deniedCalls
                        .Select(static item => (JsonNode)new JsonObject
                        {
                            ["id"] = item.constraint.Id,
                            ["description"] = item.constraint.Description,
                            ["server"] = item.alternative.Server,
                            ["kind"] = item.alternative.Kind,
                            ["method"] = item.alternative.Method,
                            ["request_bindings"] = BuildRequestBindingsJson(item.alternative.RequestBindings)
                        }).ToArray())
                });
        }
    }

    private static void ValidateNoRedundantArtifactMaterializers(
        CapabilityPreflightResult preflight,
        IReadOnlyList<StepDef> calls)
    {
        var materializers = preflight.DiscoveredServers
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Tool: tool)))
            .Where(item => GetValidatedMcpArtifactContract(item.Tool, item.Server)?.Produces.Any(artifact =>
                string.Equals(artifact.Mode, McpArtifactContractConventions.MaterializeMode, StringComparison.Ordinal)) == true)
            .ToDictionary(
                static item => (item.Server, item.Tool.Name),
                static item => item.Tool,
                EqualityComparer<(string Server, string Name)>.Default);
        if (materializers.Count == 0)
            return;

        var remainingAllowances = preflight.RequiredMcpCapabilities
            .Where(capability => materializers.ContainsKey((capability.Server!, capability.Method!)))
            .ToList();
        var redundant = new List<JsonObject>();
        foreach (var call in calls)
        {
            var server = ReadMcpCallInputString(call, "server");
            var kind = ReadMcpCallInputString(call, "kind") ?? "tool";
            if (string.IsNullOrWhiteSpace(server) || !string.Equals(kind, "tool", StringComparison.Ordinal))
                continue;

            var methods = new List<string>();
            var method = ReadMcpCallInputString(call, "method");
            if (!string.IsNullOrWhiteSpace(method))
                methods.Add(method);
            if (call.Input?["methods"] is JsonArray methodArray)
            {
                methods.AddRange(methodArray
                    .OfType<JsonValue>()
                    .Select(static value => value.TryGetValue<string>(out var candidate) ? candidate : null)
                    .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Select(static candidate => candidate!));
            }

            foreach (var candidate in methods.Distinct(StringComparer.Ordinal))
            {
                if (!materializers.ContainsKey((server, candidate)))
                    continue;

                var allowanceIndex = remainingAllowances.FindIndex(capability =>
                    string.Equals(capability.Server, server, StringComparison.Ordinal)
                    && string.Equals(capability.Method, candidate, StringComparison.Ordinal)
                    && McpStepMatchesCapability(
                        call,
                        capability.Server!,
                        capability.Kind!,
                        capability.Method!,
                        capability.RequestBindings));
                if (allowanceIndex >= 0)
                {
                    remainingAllowances.RemoveAt(allowanceIndex);
                    continue;
                }

                redundant.Add(new JsonObject
                {
                    ["step_id"] = call.Id,
                    ["server"] = server,
                    ["method"] = candidate
                });
            }
        }

        if (redundant.Count == 0)
            return;

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightRedundantArtifactProducer,
            "Generated workflow contains an MCP artifact materializer with no corresponding locked capability occurrence.",
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["reason"] = "redundant_artifact_materializer",
                ["redundant_calls"] = new JsonArray(redundant.Select(static item => (JsonNode)item).ToArray())
            });
    }

    private static void ValidateConditionalCapabilityActivation(
        WorkflowDocument document,
        CapabilityPreflightResult preflight)
    {
        var groups = preflight.RequiredMcpCapabilities
            .Where(static capability => capability.Activation is not null)
            .GroupBy(static capability => capability.Activation!.Group, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length == 0)
            return;

        var workflows = document.Workflows.Values.ToArray();
        var allSteps = workflows
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
            .ToArray();
        var allCalls = allSteps.Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)).ToArray();

        foreach (var group in groups)
        {
            var capabilities = group.ToArray();
            if (capabilities.Length < 2
                || capabilities.Any(static capability => capability.Activation?.Mode != "exactly_one")
                || capabilities.Select(static capability => capability.Activation!.BranchValue)
                    .Distinct(StringComparer.Ordinal).Count() != capabilities.Length)
            {
                ThrowInvalidConditionalActivation(group.Key,
                    "The locked conditional capability group is malformed or does not contain distinct branches.",
                    capabilities);
            }

            var groupCalls = allCalls.Where(call => capabilities.Any(capability => McpStepMatchesCapability(
                call,
                capability.Server!,
                capability.Kind!,
                capability.Method!,
                capability.RequestBindings))).ToArray();
            if (groupCalls.Length != capabilities.Length)
            {
                ThrowInvalidConditionalActivation(group.Key,
                    "Every conditional selector variant must occur exactly once in the generated workflow.",
                    capabilities);
            }

            var validSwitches = allSteps
                .Where(static step => string.Equals(step.Type, "switch", StringComparison.Ordinal))
                .Where(step => ConditionalSwitchMatches(document, step, capabilities, groupCalls, preflight))
                .ToArray();
            if (validSwitches.Length != 1)
            {
                ThrowInvalidConditionalActivation(group.Key,
                    validSwitches.Length == 0
                        ? "Conditional selector variants must be placed in distinct cases of one expression-based switch with no mutating default branch."
                        : "Conditional selector variants were associated with more than one switch.",
                    capabilities);
            }
        }
    }

    private static bool ConditionalSwitchMatches(
        WorkflowDocument document,
        StepDef step,
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<StepDef> groupCalls,
        CapabilityPreflightResult preflight)
    {
        if (string.IsNullOrWhiteSpace(step.Expr)
            || step.Cases == null
            || step.Cases.Any(static @case => !string.IsNullOrWhiteSpace(@case.When)))
        {
            return false;
        }

        var matchedCalls = new HashSet<StepDef>(ReferenceEqualityComparer.Instance);
        foreach (var capability in capabilities)
        {
            var matchingCases = step.Cases.Where(@case =>
                    string.Equals(@case.Value, capability.Activation!.BranchValue, StringComparison.Ordinal)
                    && EnumerateSteps(@case.Steps).Any(call =>
                        string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                        && McpStepMatchesCapability(
                            call,
                            capability.Server!,
                            capability.Kind!,
                            capability.Method!,
                            capability.RequestBindings)))
                .ToArray();
            if (matchingCases.Length != 1)
                return false;

            var calls = EnumerateSteps(matchingCases[0].Steps)
                .Where(call => string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                               && McpStepMatchesCapability(
                                   call,
                                   capability.Server!,
                                   capability.Kind!,
                                   capability.Method!,
                                   capability.RequestBindings))
                .ToArray();
            if (calls.Length != 1)
                return false;
            matchedCalls.Add(calls[0]);
        }

        if (matchedCalls.Count != groupCalls.Count || groupCalls.Any(call => !matchedCalls.Contains(call)))
            return false;

        var mutatingCapabilities = preflight.RequiredMcpCapabilities
            .Where(static capability => capability.ExternalEffectKind is "write" or "lifecycle")
            .ToArray();
        if (EnumerateSteps(step.Default ?? []).Any(call =>
                string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                && mutatingCapabilities.Any(capability => McpStepMatchesCapability(
                    call,
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    capability.RequestBindings))))
        {
            return false;
        }

        var decisionOperationId = capabilities[0].Activation!.DecisionOperationId;
        var decisionCapabilities = preflight.Capabilities
            .Where(capability => string.Equals(capability.OperationId, decisionOperationId, StringComparison.Ordinal)
                                 && capability.Resolution == "mcp")
            .ToArray();
        if (decisionCapabilities.Length == 0)
            return true;

        var decisionSteps = groupCalls
            .Concat(step.Cases.SelectMany(static @case => EnumerateSteps(@case.Steps)))
            .Concat(EnumerateSteps(step.Default ?? []))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var sources = document.Workflows
            .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Where(candidate => !decisionSteps.Contains(candidate)
                                    && decisionCapabilities.Any(capability => McpStepMatchesCapability(
                                        candidate,
                                        capability.Server!,
                                        capability.Kind!,
                                        capability.Method!,
                                        capability.RequestBindings)))
                .Select(candidate => (Workflow: workflow.Key, Step: candidate)))
            .ToArray();
        if (sources.Length == 0)
            return true;

        var owner = document.Workflows.FirstOrDefault(workflow =>
            EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Any(candidate => ReferenceEquals(candidate, step)));
        return !string.IsNullOrWhiteSpace(owner.Key)
               && ConditionalDecisionExpressionDependsOnSource(
                   document,
                   BuildWorkflowArtifactCallerIndex(document),
                   sources,
                   owner.Key,
                   step.Expr!,
                   []);
    }

    private static bool ConditionalDecisionExpressionDependsOnSource(
        WorkflowDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<(string Workflow, StepDef Step)> sources,
        string workflowName,
        string expression,
        IReadOnlyList<string> appendedPath,
        HashSet<string>? visited = null)
    {
        var path = TrimWorkflowExpression(expression);
        if (appendedPath.Count > 0)
            path += "." + string.Join('.', appendedPath);
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        var visitKey = workflowName + "\u001f" + path;
        if (!visited.Add(visitKey))
            return false;

        try
        {
            const string inputPrefix = "data.inputs.";
            if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
            {
                var inputPath = path[inputPrefix.Length..]
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (inputPath.Length == 0
                    || !workflowCallers.TryGetValue(workflowName, out var callers)
                    || callers.Count == 0)
                {
                    return false;
                }

                return callers.All(caller =>
                    caller.Call.Input?["args"]?[inputPath[0]] is JsonValue argument
                    && argument.TryGetValue<string>(out var argumentExpression)
                    && !string.IsNullOrWhiteSpace(argumentExpression)
                    && ConditionalDecisionExpressionDependsOnSource(
                        document,
                        workflowCallers,
                        sources,
                        caller.Workflow,
                        argumentExpression,
                        inputPath.Skip(1).ToArray(),
                        visited));
            }

            const string stepPrefix = "data.steps.";
            if (!path.StartsWith(stepPrefix, StringComparison.Ordinal)
                || !document.Workflows.TryGetValue(workflowName, out var workflow))
            {
                return false;
            }

            var stepPath = path[stepPrefix.Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stepPath.Length < 2)
                return false;
            var sourceStep = EnumerateSteps(workflow.Steps)
                .Concat(EnumerateSteps(workflow.Finally))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, stepPath[0], StringComparison.Ordinal)
                                             || string.Equals(candidate.Output, stepPath[0], StringComparison.Ordinal));
            if (sourceStep == null)
                return false;
            if (sources.Any(source => string.Equals(source.Workflow, workflowName, StringComparison.Ordinal)
                                      && ReferenceEquals(source.Step, sourceStep)))
            {
                return true;
            }

            var remainingPath = stepPath.Skip(1).ToArray();
            if (string.Equals(sourceStep.Type, "set", StringComparison.Ordinal))
            {
                var value = ResolveInstancePath(sourceStep.Input, remainingPath);
                return value is JsonValue setValue
                       && setValue.TryGetValue<string>(out var setExpression)
                       && !string.IsNullOrWhiteSpace(setExpression)
                       && ConditionalDecisionExpressionDependsOnSource(
                           document,
                           workflowCallers,
                           sources,
                           workflowName,
                           setExpression,
                           [],
                           visited);
            }

            if (!string.Equals(sourceStep.Type, "workflow.call", StringComparison.Ordinal))
                return false;
            var targetName = ReadWorkflowCallRefNameFromInput(sourceStep);
            if (string.IsNullOrWhiteSpace(targetName)
                || !document.Workflows.TryGetValue(targetName, out var target))
            {
                return false;
            }

            var outputIndex = string.Equals(remainingPath[0], "outputs", StringComparison.Ordinal) ? 1 : 0;
            if (remainingPath.Length <= outputIndex
                || target.Outputs == null
                || !target.Outputs.TryGetValue(remainingPath[outputIndex], out var output))
            {
                return false;
            }

            return ConditionalDecisionExpressionDependsOnSource(
                document,
                workflowCallers,
                sources,
                targetName,
                output.Expr,
                remainingPath.Skip(outputIndex + 1).ToArray(),
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    private static void ThrowInvalidConditionalActivation(
        string group,
        string reason,
        IReadOnlyList<ResolvedCapability> capabilities)
        => throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"Generated workflow does not safely implement conditional capability group '{group}': {reason}",
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["reason"] = "conditional_activation_invalid",
                ["activation_group"] = group,
                ["decision_operation_id"] = capabilities
                    .Select(static capability => capability.Activation?.DecisionOperationId)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["message"] = reason,
                ["branches"] = new JsonArray(capabilities.Select(capability => (JsonNode)new JsonObject
                {
                    ["branch_value"] = capability.Activation?.BranchValue,
                    ["server"] = capability.Server,
                    ["kind"] = capability.Kind,
                    ["method"] = capability.Method,
                    ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings)
                }).ToArray())
            });

    private sealed record PlannedArtifactProducer(
        string Workflow,
        string StepId,
        string Kind,
        string Pointer);

    private sealed record ArtifactResolution(
        bool Proven,
        IReadOnlySet<PlannedArtifactProducer> Producers,
        bool UsesCallerInput)
    {
        public static ArtifactResolution Unproven { get; } = new(
            false,
            new HashSet<PlannedArtifactProducer>(),
            false);
    }

    private static void ValidateMcpArtifactDataflow(
        WorkflowDocument document,
        CapabilityPreflightResult preflight)
    {
        var tools = preflight.DiscoveredServers
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Tool: tool)))
            .ToDictionary(
                static item => (item.Server, item.Tool.Name),
                static item => item.Tool,
                EqualityComparer<(string Server, string Name)>.Default);
        if (tools.Count == 0)
            return;

        var stepsByWorkflow = document.Workflows.ToDictionary(
            static workflow => workflow.Key,
            static workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Where(static step => !string.IsNullOrWhiteSpace(step.Id))
                .GroupBy(static step => step.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal),
            StringComparer.Ordinal);
        var workflowCallers = BuildWorkflowArtifactCallerIndex(document);
        var producers = new List<PlannedArtifactProducer>();
        var consumers = new List<(string Workflow, StepDef Step, McpConsumedArtifact Artifact, JsonNode? Value)>();

        foreach (var (workflowName, workflowSteps) in stepsByWorkflow)
        {
            foreach (var step in workflowSteps.Values.Where(static item =>
                         string.Equals(item.Type, "mcp.call", StringComparison.Ordinal)))
            {
                var server = ReadMcpCallInputString(step, "server");
                var kind = ReadMcpCallInputString(step, "kind") ?? "tool";
                var method = ReadMcpCallInputString(step, "method");
                if (!string.Equals(kind, "tool", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(server)
                    || string.IsNullOrWhiteSpace(method)
                    || !tools.TryGetValue((server, method), out var tool))
                {
                    continue;
                }

                var contract = GetValidatedMcpArtifactContract(tool, server);
                if (contract == null)
                    continue;

                producers.AddRange(contract.Produces
                    .Where(static artifact => string.Equals(
                        artifact.Mode,
                        McpArtifactContractConventions.MaterializeMode,
                        StringComparison.Ordinal))
                    .Select(artifact => new PlannedArtifactProducer(
                        workflowName,
                        step.Id,
                        artifact.Kind,
                        artifact.Pointer)));
                foreach (var artifact in contract.Consumes.Where(static artifact => artifact.Required))
                {
                    consumers.Add((
                        workflowName,
                        step,
                        artifact,
                        ResolveInstancePointer(step.Input?["request"], artifact.Pointer)));
                }
            }
        }

        if (consumers.Count == 0)
            return;

        var diagnostics = new JsonArray();
        foreach (var consumer in consumers)
        {
            var resolution = ResolveArtifactValue(
                document,
                stepsByWorkflow,
                workflowCallers,
                producers,
                consumer.Workflow,
                consumer.Value,
                consumer.Artifact.Kind,
                new HashSet<string>(StringComparer.Ordinal));
            if (resolution.Proven)
                continue;

            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "MCP_ARTIFACT_PROVENANCE_UNPROVEN",
                ["workflow"] = consumer.Workflow,
                ["consumer_step"] = consumer.Step.Id,
                ["artifact_kind"] = consumer.Artifact.Kind,
                ["request_pointer"] = consumer.Artifact.Pointer,
                ["value"] = consumer.Value?.DeepClone(),
                ["expected"] = "An exact compatible producer response value, optionally routed through workflow inputs/outputs or a transparent set alias, or an exact caller-provided artifact input."
            });
        }

        if (diagnostics.Count == 0)
            return;

        var details = new JsonObject
        {
            ["phase"] = "mcp_artifact_dataflow",
            ["reason"] = "unproven_artifact_provenance",
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(
                (JsonNode)JsonValue.Create("Route the exact field declared by a compatible MCP artifact producer to the consumer request pointer.")!,
                (JsonNode)JsonValue.Create("Do not invent, concatenate, cast, normalize, or otherwise transform artifact values.")!,
                (JsonNode)JsonValue.Create("Reuse one producer value for every compatible downstream consumer when the task has one source artifact.")!)
        };
        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Generated workflow contains an MCP artifact consumer whose required value has no compatible, unchanged provenance. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>>
        BuildWorkflowArtifactCallerIndex(WorkflowDocument document)
    {
        var callers = new Dictionary<string, List<(string Workflow, StepDef Call)>>(StringComparer.Ordinal);
        foreach (var (workflowName, workflow) in document.Workflows)
        {
            foreach (var call in EnumerateSteps(workflow.Steps)
                         .Concat(EnumerateSteps(workflow.Finally))
                         .Where(static step => string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)))
            {
                var target = ReadWorkflowCallRefNameFromInput(call);
                if (string.IsNullOrWhiteSpace(target))
                    continue;
                if (!callers.TryGetValue(target, out var targetCallers))
                    callers[target] = targetCallers = [];
                targetCallers.Add((workflowName, call));
            }
        }

        return callers.ToDictionary(
            static item => item.Key,
            static item => (IReadOnlyList<(string Workflow, StepDef Call)>)item.Value,
            StringComparer.Ordinal);
    }

    private static ArtifactResolution ResolveArtifactValue(
        WorkflowDocument document,
        IReadOnlyDictionary<string, Dictionary<string, StepDef>> stepsByWorkflow,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<PlannedArtifactProducer> producers,
        string workflowName,
        JsonNode? value,
        string artifactKind,
        HashSet<string> visited)
    {
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var expression)
            || string.IsNullOrWhiteSpace(expression))
        {
            return ArtifactResolution.Unproven;
        }

        var visitKey = workflowName + "\u001f" + artifactKind + "\u001f" + expression;
        if (!visited.Add(visitKey))
            return ArtifactResolution.Unproven;
        try
        {
            var path = TrimWorkflowExpression(expression);
            const string inputPrefix = "data.inputs.";
            if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
            {
                var inputPath = path[inputPrefix.Length..]
                    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (inputPath.Length == 0)
                    return ArtifactResolution.Unproven;

                if (!workflowCallers.TryGetValue(workflowName, out var callers) || callers.Count == 0)
                {
                    return new ArtifactResolution(
                        true,
                        new HashSet<PlannedArtifactProducer>(),
                        true);
                }

                var combined = new HashSet<PlannedArtifactProducer>();
                var usesCallerInput = false;
                foreach (var caller in callers)
                {
                    var argument = ResolveInstancePath(caller.Call.Input?["args"], inputPath);
                    var resolved = ResolveArtifactValue(
                        document,
                        stepsByWorkflow,
                        workflowCallers,
                        producers,
                        caller.Workflow,
                        argument,
                        artifactKind,
                        visited);
                    if (!resolved.Proven)
                        return ArtifactResolution.Unproven;
                    combined.UnionWith(resolved.Producers);
                    usesCallerInput |= resolved.UsesCallerInput;
                }

                return new ArtifactResolution(true, combined, usesCallerInput);
            }

            const string stepPrefix = "data.steps.";
            if (!path.StartsWith(stepPrefix, StringComparison.Ordinal)
                || !stepsByWorkflow.TryGetValue(workflowName, out var workflowSteps))
            {
                return ArtifactResolution.Unproven;
            }

            var stepPath = path[stepPrefix.Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stepPath.Length < 2 || !workflowSteps.TryGetValue(stepPath[0], out var sourceStep))
                return ArtifactResolution.Unproven;
            var remainingPath = stepPath.Skip(1).ToArray();

            var matchingProducer = producers.FirstOrDefault(producer =>
                string.Equals(producer.Workflow, workflowName, StringComparison.Ordinal)
                && string.Equals(producer.StepId, sourceStep.Id, StringComparison.Ordinal)
                && string.Equals(producer.Kind, artifactKind, StringComparison.Ordinal)
                && remainingPath.SequenceEqual(
                    new[] { "response" }.Concat(DecodeArtifactPointer(producer.Pointer)),
                    StringComparer.Ordinal));
            if (matchingProducer != null)
            {
                return new ArtifactResolution(
                    true,
                    new HashSet<PlannedArtifactProducer> { matchingProducer },
                    false);
            }

            if (string.Equals(sourceStep.Type, "set", StringComparison.Ordinal))
            {
                return ResolveArtifactValue(
                    document,
                    stepsByWorkflow,
                    workflowCallers,
                    producers,
                    workflowName,
                    ResolveInstancePath(sourceStep.Input, remainingPath),
                    artifactKind,
                    visited);
            }

            if (!string.Equals(sourceStep.Type, "workflow.call", StringComparison.Ordinal))
                return ArtifactResolution.Unproven;
            var targetWorkflow = ReadWorkflowCallRefNameFromInput(sourceStep);
            if (string.IsNullOrWhiteSpace(targetWorkflow)
                || !document.Workflows.TryGetValue(targetWorkflow, out var target)
                || remainingPath.Length == 0)
            {
                return ArtifactResolution.Unproven;
            }

            var outputIndex = string.Equals(remainingPath[0], "outputs", StringComparison.Ordinal) ? 1 : 0;
            if (remainingPath.Length != outputIndex + 1
                || target.Outputs == null
                || !target.Outputs.TryGetValue(remainingPath[outputIndex], out var output))
            {
                return ArtifactResolution.Unproven;
            }

            return ResolveArtifactValue(
                document,
                stepsByWorkflow,
                workflowCallers,
                producers,
                targetWorkflow,
                JsonValue.Create(output.Expr),
                artifactKind,
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    private static JsonNode? ResolveInstancePointer(JsonNode? root, string pointer)
        => ResolveInstancePath(root, DecodeArtifactPointer(pointer));

    private static JsonNode? ResolveInstancePath(JsonNode? root, IReadOnlyList<string> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (current == null)
                return null;
        }

        return current;
    }

    private static IReadOnlyList<string> DecodeArtifactPointer(string pointer)
        => pointer[1..]
            .Split('/', StringSplitOptions.None)
            .Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();

    private static WorkflowPipelineExtraction ValidateLockedCapabilitiesInExtraction(
        WorkflowPipelineExtraction extraction,
        CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled
            || preflight.RequiredMcpCapabilities.Count == 0
            && preflight.RequiredNativeCapabilities.Count == 0
            && preflight.RequiredLocalOperations.Count == 0)
            return extraction;

        var boundExtraction = extraction;
        IReadOnlyList<ResolvedCapability> missingMcp = Array.Empty<ResolvedCapability>();
        if (preflight.RequiredMcpCapabilities.Count > 0)
            (boundExtraction, missingMcp) = BindPlannedCapabilityInvocations(extraction, preflight.RequiredMcpCapabilities);

        var declaredNativeSteps = boundExtraction.Subworkflows
            .SelectMany(static spec => spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
            .Concat(boundExtraction.MainNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
            .Where(static step => step.Required)
            .Select(static step => step.Method)
            .ToList();
        var missingNative = new List<ResolvedCapability>();
        foreach (var capability in preflight.RequiredNativeCapabilities)
        {
            var index = declaredNativeSteps.FindIndex(method => string.Equals(method, capability.Method, StringComparison.Ordinal));
            if (index < 0)
                missingNative.Add(capability);
            else
                declaredNativeSteps.RemoveAt(index);
        }

        var declaredLocalOperationIds = boundExtraction.Subworkflows
            .SelectMany(static spec => spec.LocalOperationIds ?? Array.Empty<string>())
            .Concat(boundExtraction.MainLocalOperationIds ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        var missingLocal = preflight.RequiredLocalOperations
            .Where(operation => !declaredLocalOperationIds.Contains(operation.OperationId ?? operation.Id))
            .ToArray();
        var errors = boundExtraction.ValidationErrors.ToList();
        var rootCauses = boundExtraction.RootCauses.ToList();
        ValidateConditionalDecisionBoundaries(boundExtraction, errors, rootCauses);
        if (missingMcp.Count == 0
            && missingNative.Count == 0
            && missingLocal.Length == 0
            && errors.Count == boundExtraction.ValidationErrors.Count)
        {
            return boundExtraction;
        }

        foreach (var capability in missingMcp)
        {
            var bindingText = capability.RequestBindings.Count == 0
                ? string.Empty
                : $" and literal request_bindings [{FormatBindingsCompact(capability.RequestBindings)}]";
            var message = $"CAPABILITY_PREFLIGHT_REQUIRED_CAPABILITY_OMITTED: Required capability '{capability.Id}' must be assigned to one external-work leaf as exact planned tool {capability.Server}/{capability.Method} ({capability.Kind}) with required=true{bindingText}.";
            errors.Add(message);
            rootCauses.Add(new PipelineRootCause(
                "required_capability_omitted",
                "pipeline_extraction",
                null,
                null,
                $"capability_preflight.{capability.Id}",
                "CAPABILITY_PREFLIGHT_REQUIRED_CAPABILITY_OMITTED",
                message,
                true));
        }

        foreach (var capability in missingNative)
        {
            var operationId = capability.OperationId ?? capability.Id;
            var message = $"CAPABILITY_PREFLIGHT_NATIVE_OPERATION_OMITTED: Required native operation '{operationId}' must be assigned to one leaf as exact Flow step type '{capability.Method}'.";
            errors.Add(message);
            rootCauses.Add(new PipelineRootCause(
                "required_native_operation_omitted",
                "pipeline_extraction",
                null,
                null,
                $"capability_preflight.{operationId}",
                "CAPABILITY_PREFLIGHT_NATIVE_OPERATION_OMITTED",
                message,
                true));
        }

        foreach (var operation in missingLocal)
        {
            var operationId = operation.OperationId ?? operation.Id;
            var message = $"CAPABILITY_PREFLIGHT_LOCAL_OPERATION_OMITTED: Required local-processing operation '{operationId}' must remain locked in a leaf or in main orchestration.";
            errors.Add(message);
            rootCauses.Add(new PipelineRootCause(
                "required_local_operation_omitted",
                "pipeline_extraction",
                null,
                null,
                $"capability_preflight.{operationId}",
                "CAPABILITY_PREFLIGHT_LOCAL_OPERATION_OMITTED",
                message,
                true));
        }

        return boundExtraction with
        {
            ValidationErrors = errors.Distinct(StringComparer.Ordinal).ToArray(),
            RootCauses = rootCauses
                .DistinctBy(static cause => string.Join(
                    "|",
                    cause.Category,
                    cause.Phase,
                    cause.LeafName,
                    cause.OutputName,
                    cause.InvalidPath,
                    cause.Code,
                    cause.Message,
                    cause.Primary), StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static void ValidateConditionalDecisionBoundaries(
        WorkflowPipelineExtraction extraction,
        ICollection<string> errors,
        ICollection<PipelineRootCause> rootCauses)
    {
        var leafOwners = extraction.Subworkflows
            .SelectMany(static leaf => (leaf.LocalOperationIds ?? Array.Empty<string>())
                .Select(operationId => (OperationId: operationId, Leaf: leaf)))
            .GroupBy(static owner => owner.OperationId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Select(static owner => owner.Leaf).ToArray(), StringComparer.Ordinal);
        var mainOwners = (extraction.MainLocalOperationIds ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var activationLeaf in extraction.Subworkflows)
        {
            foreach (var decisionOperationId in activationLeaf.PlannedTools
                         .Where(static tool => tool.Activation != null)
                         .Select(static tool => tool.Activation!.DecisionOperationId)
                         .Distinct(StringComparer.Ordinal))
            {
                leafOwners.TryGetValue(decisionOperationId, out var owners);
                owners ??= Array.Empty<WorkflowPipelineSubworkflowSpec>();
                var ownerCount = owners.Length + (mainOwners.Contains(decisionOperationId) ? 1 : 0);
                if (ownerCount != 1)
                {
                    var ownershipMessage = $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_OWNER_INVALID: Conditional activation operation '{decisionOperationId}' must have exactly one immutable local owner; found {ownerCount}.";
                    errors.Add(ownershipMessage);
                    rootCauses.Add(new PipelineRootCause(
                        "conditional_decision_owner_invalid",
                        "pipeline_extraction",
                        activationLeaf.Name,
                        null,
                        $"subworkflows.{activationLeaf.Name}.planned_tools.activation.decision_operation_id",
                        "CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_OWNER_INVALID",
                        ownershipMessage,
                        true));
                    continue;
                }

                if (mainOwners.Contains(decisionOperationId)
                    || string.Equals(owners[0].Name, activationLeaf.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var decisionOwner = owners[0];
                var sharedFields = decisionOwner.Outputs.Keys
                    .Intersect(activationLeaf.Inputs.Keys, StringComparer.Ordinal)
                    .Where(field => PipelineBoundaryTypesAreCompatible(
                        decisionOwner.Outputs[field],
                        activationLeaf.Inputs[field]))
                    .ToArray();
                if (sharedFields.Length > 0)
                    continue;

                var boundaryMessage = $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_BOUNDARY_MISSING: Conditional activation operation '{decisionOperationId}' is owned by leaf '{decisionOwner.Name}' and consumed by leaf '{activationLeaf.Name}', but those leaves have no same-named compatible output/input field for the runtime decision. Add that typed boundary and route it through main without recomputing the decision.";
                errors.Add(boundaryMessage);
                rootCauses.Add(new PipelineRootCause(
                    "conditional_decision_boundary_missing",
                    "pipeline_extraction",
                    activationLeaf.Name,
                    null,
                    $"subworkflows.{activationLeaf.Name}.inputs",
                    "CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_BOUNDARY_MISSING",
                    boundaryMessage,
                    true));
            }
        }
    }

    private static bool PipelineBoundaryTypesAreCompatible(string producerType, string consumerType)
    {
        var producer = NormalizeWorkflowSchemaType(producerType);
        var consumer = NormalizeWorkflowSchemaType(consumerType);
        return string.Equals(producer, consumer, StringComparison.Ordinal)
               || string.Equals(producer, "any", StringComparison.Ordinal)
               || string.Equals(consumer, "any", StringComparison.Ordinal);
    }

    private static StructuredPipelineExtractionMetadata ComposeLockedCapabilitiesIntoPipelineMetadata(
        WorkflowPipelineExtraction extraction,
        StructuredPipelineExtractionMetadata metadata,
        CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled || preflight.RequiredMcpCapabilities.Count == 0
            || !metadata.IsStructuredResponse || metadata.Subworkflows.Count == 0)
            return metadata;

        var specsByName = extraction.Subworkflows.ToDictionary(static spec => spec.Name, StringComparer.Ordinal);
        var updated = metadata.Subworkflows.ToDictionary(
            static item => item.Key,
            static item => item.Value with { PlannedTools = item.Value.PlannedTools.ToArray() },
            StringComparer.Ordinal);
        var remaining = preflight.RequiredMcpCapabilities.ToList();

        // Consume already-declared invocations as a multiset before composing missing locked calls.
        foreach (var item in updated.Values)
        {
            foreach (var tool in item.PlannedTools.Where(static tool => tool.Required))
            {
                var index = remaining.FindIndex(capability => string.Equals(tool.Server, capability.Server, StringComparison.Ordinal)
                                                              && string.Equals(tool.Kind, capability.Kind, StringComparison.Ordinal)
                                                              && string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)
                                                              && RequestBindingsEqual(tool.RequestBindings, capability.RequestBindings));
                if (index >= 0)
                    remaining.RemoveAt(index);
            }
        }
        if (remaining.Count == 0)
            return metadata;

        foreach (var capabilityGroup in remaining.GroupBy(
                     static capability => GetPipelineCapabilityCompositionGroup(capability),
                     StringComparer.Ordinal))
        {
            var capabilities = capabilityGroup.ToArray();
            var targetName = capabilities
                .Select(static capability => capability.Activation?.Group)
                .Where(static group => !string.IsNullOrWhiteSpace(group))
                .SelectMany(group => updated
                    .Where(item => item.Value.PlannedTools.Any(tool => string.Equals(
                        tool.Activation?.Group,
                        group,
                        StringComparison.Ordinal)))
                    .Select(static item => item.Key))
                .FirstOrDefault()
                ?? SelectPipelineCapabilityTarget(capabilities, specsByName, updated);
            if (targetName == null)
                continue;
            var target = updated[targetName];
            var planned = target.PlannedTools.ToList();
            foreach (var capability in capabilities)
            {
                planned.Add(new PipelinePlannedTool(
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    true,
                    capability.Description,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    capability.RequestBindings,
                    string.IsNullOrWhiteSpace(capability.OperationId) ? [capability.Id] : [capability.OperationId],
                    string.IsNullOrWhiteSpace(capability.CatalogId) ? Array.Empty<string>() : [capability.CatalogId],
                    capability.Activation));
            }
            updated[targetName] = target with { PlannedTools = planned };
        }

        return metadata with { Subworkflows = updated };
    }

    private static WorkflowPipelineExtraction ComposeLockedCapabilitiesIntoPipelineExtraction(
        WorkflowPipelineExtraction extraction,
        CapabilityPreflightResult preflight,
        PipelineMcpContext pipelineMcpContext)
    {
        if (extraction.Subworkflows.Count == 0)
            return extraction;

        var specs = extraction.Subworkflows.ToArray();
        var localOperationAssignments = AssignLocalOperationsToPipelineSpecs(
            preflight.RequiredLocalOperations,
            specs);
        var localOnlySpecIndices = specs
            .Select((spec, index) => new { spec, index })
            .Where(item => DeclaresNoExternalCalls(BuildPipelineSpecIntentText(item.spec))
                           && !ContainsExternalWorkIntent(BuildPipelineSpecIntentText(item.spec))
                           || HasStrongLocalProcessingIntent(item.spec)
                           && !SpecMentionsExactDiscoveredCapability(item.spec, pipelineMcpContext)
                           && !ContainsExternalWorkIntent(BuildPipelineSpecIntentText(item.spec))
                           || localOperationAssignments.LeafAssignments.ContainsKey(item.index)
                           && (item.spec.PlannedNativeSteps?.Count ?? 0) == 0
                           && !SpecMentionsExactDiscoveredCapability(item.spec, pipelineMcpContext)
                           && !ContainsExternalWorkIntent(BuildPipelineSpecIntentText(item.spec)))
            .Select(static item => item.index)
            .ToHashSet();
        var tools = specs.Select((spec, index) => localOnlySpecIndices.Contains(index)
            ? new List<PipelinePlannedTool>()
            : spec.PlannedTools.ToList()).ToArray();
        // The extractor may place all members of a composed operation on every leaf that
        // discusses the overall operation. Rebuild locked occurrences deterministically:
        // a composition is a multiset of (operation, catalog) pairs and each pair is placed
        // independently on the leaf that best matches the concrete capability.
        foreach (var planned in tools)
        {
            RemoveClaimedOrMatchingLockedToolPlans(planned, preflight.RequiredMcpCapabilities);
        }

        var nativeSteps = specs
            .Select(static spec => (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()).ToList())
            .ToArray();
        var mainNativeSteps = (extraction.MainNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>()).ToList();
        NormalizeCoalescedMainNativeCapabilityPlans(
            nativeSteps,
            mainNativeSteps,
            preflight.RequiredNativeCapabilities);
        var remainingNative = preflight.RequiredNativeCapabilities.ToList();
        for (var specIndex = 0; specIndex < nativeSteps.Length; specIndex++)
        {
            foreach (var step in nativeSteps[specIndex].Where(static step => step.Required))
            {
                var index = remainingNative.FindIndex(capability => string.Equals(
                    capability.Method, step.Method, StringComparison.Ordinal));
                if (index >= 0)
                    remainingNative.RemoveAt(index);
            }
        }
        foreach (var step in mainNativeSteps.Where(static step => step.Required))
        {
            var index = remainingNative.FindIndex(capability => string.Equals(
                capability.Method, step.Method, StringComparison.Ordinal));
            if (index >= 0)
                remainingNative.RemoveAt(index);
        }

        foreach (var capability in remainingNative)
        {
            if (IsMainOrchestrationNativeCapability(capability))
            {
                mainNativeSteps.Add(new PipelinePlannedNativeStep(
                    capability.Method!,
                    true,
                    capability.Description,
                    GetResolvedCapabilityOperationIds(capability),
                    string.IsNullOrWhiteSpace(capability.CatalogId) ? Array.Empty<string>() : [capability.CatalogId]));
                continue;
            }

            var targetIndex = SelectPipelineCapabilityTargetIndex(
                [capability], specs, tools, localOnlySpecIndices);
            localOnlySpecIndices.Remove(targetIndex);
            nativeSteps[targetIndex].Add(new PipelinePlannedNativeStep(
                capability.Method!,
                true,
                capability.Description,
                GetResolvedCapabilityOperationIds(capability),
                string.IsNullOrWhiteSpace(capability.CatalogId) ? Array.Empty<string>() : [capability.CatalogId]));
        }

        foreach (var capabilityGroup in preflight.RequiredMcpCapabilities
                     .GroupBy(GetPipelineCapabilityCompositionGroup, StringComparer.Ordinal))
        {
            var capabilities = capabilityGroup.ToArray();
            var explicitlyRejectedOwners = specs
                .Select((spec, index) => new { spec, index })
                .Where(item => ExplicitlyRejectsLockedCapabilityOwnership(capabilities, item.spec))
                .Select(static item => item.index)
                .ToHashSet();
            var targetIndex = FindUniqueDeclaredLockedCapabilityOwnerIndex(capabilities, specs)
                              ?? SelectPipelineCapabilityTargetIndex(
                                  capabilities,
                                  specs,
                                  tools,
                                  localOnlySpecIndices,
                                  explicitlyRejectedOwners);
            localOnlySpecIndices.Remove(targetIndex);
            foreach (var capability in capabilities)
            {
                tools[targetIndex].Add(new PipelinePlannedTool(
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    true,
                    capability.Description,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    capability.RequestBindings,
                    string.IsNullOrWhiteSpace(capability.OperationId) ? [capability.Id] : [capability.OperationId],
                    string.IsNullOrWhiteSpace(capability.CatalogId) ? Array.Empty<string>() : [capability.CatalogId],
                    capability.Activation));
            }
        }

        // Technical decomposition may introduce an exact call that was not an independent
        // user-level operation. Reconcile only literal discovered method/selector mentions;
        // never derive support from a product name, URL, or free-form keyword.
        for (var index = 0; index < specs.Length; index++)
        {
            if (!localOnlySpecIndices.Contains(index)
                && !DeclaresNoExternalCalls(BuildPipelineSpecIntentText(specs[index])))
                AddExplicitDiscoveredCapabilityMentions(specs[index], tools[index], pipelineMcpContext);
            RefineUnlockedAdvisorySelectorBindings(
                specs[index],
                tools[index],
                pipelineMcpContext);
            tools[index] = NormalizeAdvisoryPlannedToolRequestBindings(
                    tools[index],
                    pipelineMcpContext)
                .ToList();
            RemoveEncapsulatedUnlockedToolPlans(
                tools[index],
                preflight.RequiredMcpCapabilities,
                pipelineMcpContext);
            RemoveRedundantUnlockedWholeToolPlans(tools[index], preflight.RequiredMcpCapabilities);
            RemoveDuplicateLockedToolOccurrences(tools[index]);
        }

        IReadOnlyList<WorkflowPipelineSubworkflowSpec> updated = specs.Select((spec, index) =>
        {
            var specIntent = BuildPipelineSpecIntentText(spec);
            var requiresUnownedExternalAction = tools[index].Count == 0
                                                && nativeSteps[index].Count == 0
                                                && IsExternalWorkSpec(spec)
                                                && !HasStrongLocalProcessingIntent(spec);
            var explicitlyInternal = localOnlySpecIndices.Contains(index)
                                     || DeclaresNoExternalCalls(specIntent)
                                     || tools[index].Count == 0
                                     && nativeSteps[index].Count == 0
                                     && HasStrongLocalProcessingIntent(spec)
                                     && !requiresUnownedExternalAction;
            var conditionalActivations = tools[index]
                .Where(static tool => tool.Activation is not null)
                .Select(static tool => tool.Activation!)
                .ToArray();
            var conditionalGuidance = conditionalActivations.Length == 0
                ? null
                : string.Join(' ', conditionalActivations
                    .GroupBy(static activation => activation.Group, StringComparer.Ordinal)
                    .Select(group =>
                        $"This leaf owns conditional capability group '{group.Key}': drive exactly one '{string.Join("' or '", group.Select(static activation => activation.BranchValue).Distinct(StringComparer.Ordinal))}' branch from decision operation '{group.First().DecisionOperationId}' in one switch, with no mutating default branch."));
            var withTools = spec with
            {
                PlannedTools = tools[index],
                PlannedNativeSteps = nativeSteps[index],
                Content = conditionalGuidance == null
                    ? spec.Content
                    : spec.Content.TrimEnd() + Environment.NewLine + conditionalGuidance,
                LocalOperationIds = localOperationAssignments.LeafAssignments.TryGetValue(index, out var operationIds)
                    ? operationIds
                    : spec.LocalOperationIds,
                WorkKind = explicitlyInternal
                    ? PipelineWorkKindDeterministicShaping
                    : tools[index].Count > 0 || nativeSteps[index].Count > 0
                        ? PipelineWorkKindExternalWork
                        : requiresUnownedExternalAction
                            ? PipelineWorkKindExternalWork
                            : spec.WorkKind,
                ContractRole = explicitlyInternal
                    ? PipelineContractRoleAlgorithmicTransform
                    : tools[index].Count > 0 || nativeSteps[index].Count > 0
                        ? PipelineContractRoleExternalAction
                        : requiresUnownedExternalAction
                            ? PipelineContractRoleExternalAction
                            : spec.ContractRole
            };
            return withTools with { GenerationPrompt = BuildSubworkflowGenerationPrompt(withTools) };
        }).ToArray();

        var consolidated = ConsolidateSplitConditionalVariantSpecs(
            specs,
            updated,
            preflight.RequiredMcpCapabilities,
            extraction.MainWorkflowPrompt);
        updated = consolidated.Specs;

        var validationErrors = extraction.ValidationErrors.ToList();
        var rootCauses = extraction.RootCauses.ToList();
        foreach (var spec in updated)
        {
            ValidatePlannedToolsAgainstMcpContext(spec.Name, spec.PlannedTools, pipelineMcpContext, validationErrors);
            if (preflight.Enabled)
                ValidateRequiredLeafToolContracts(spec, pipelineMcpContext, validationErrors, rootCauses);
        }
        var conditionalOwners = updated
            .SelectMany(spec => spec.PlannedTools
                .Where(static tool => tool.Activation is not null)
                .Select(tool => (Spec: spec, Activation: tool.Activation!)))
            .GroupBy(static item => item.Activation.Group, StringComparer.Ordinal)
            .Select(group =>
            {
                var activationOwner = group.First().Spec.Name;
                var decisionOperationId = group.First().Activation.DecisionOperationId;
                var decisionLeafOwners = updated
                    .Where(spec => (spec.LocalOperationIds ?? Array.Empty<string>())
                        .Contains(decisionOperationId, StringComparer.Ordinal))
                    .Select(static spec => spec.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var mainOwnsDecision = localOperationAssignments.MainOperationIds.Contains(
                    decisionOperationId,
                    StringComparer.Ordinal);
                return BuildConditionalDecisionRoutingGuidance(
                    group.Key,
                    decisionOperationId,
                    activationOwner,
                    decisionLeafOwners,
                    mainOwnsDecision);
            })
            .ToArray();
        return extraction with
        {
            Subworkflows = updated,
            ValidationErrors = validationErrors,
            RootCauses = rootCauses,
            MainWorkflowPrompt = conditionalOwners.Length == 0
                ? consolidated.MainWorkflowPrompt
                : consolidated.MainWorkflowPrompt.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, conditionalOwners),
            MainLocalOperationIds = localOperationAssignments.MainOperationIds,
            MainNativeSteps = mainNativeSteps
        };
    }

    private static string BuildConditionalDecisionRoutingGuidance(
        string group,
        string decisionOperationId,
        string activationOwner,
        IReadOnlyList<string> decisionLeafOwners,
        bool mainOwnsDecision)
    {
        if (mainOwnsDecision && decisionLeafOwners.Count == 0)
        {
            return $"Main owns and computes decision operation '{decisionOperationId}', then passes its typed result to leaf '{activationOwner}'; that leaf executes exactly one branch of conditional capability group '{group}'.";
        }

        if (!mainOwnsDecision && decisionLeafOwners.Count == 1)
        {
            var decisionOwner = decisionLeafOwners[0];
            if (string.Equals(decisionOwner, activationOwner, StringComparison.Ordinal))
            {
                return $"Leaf '{decisionOwner}' owns and derives decision operation '{decisionOperationId}', then uses that typed result directly to execute exactly one branch of conditional capability group '{group}'; main must not recompute it.";
            }

            return $"Leaf '{decisionOwner}' owns and derives decision operation '{decisionOperationId}' and exposes its typed result; main routes that result unchanged to leaf '{activationOwner}', which executes exactly one branch of conditional capability group '{group}', without recomputing the decision.";
        }

        return $"Decision operation '{decisionOperationId}' for conditional capability group '{group}' must be derived by its exactly one immutable owner and routed unchanged to leaf '{activationOwner}'; main must not claim ownership unless the immutable main operation contract assigns it there.";
    }

    private static void NormalizeCoalescedMainNativeCapabilityPlans(
        IReadOnlyList<List<PipelinePlannedNativeStep>> leafNativeSteps,
        List<PipelinePlannedNativeStep> mainNativeSteps,
        IReadOnlyList<ResolvedCapability> requiredNativeCapabilities)
    {
        foreach (var capability in requiredNativeCapabilities.Where(static capability =>
                     capability.OperationIds is { Count: > 1 }))
        {
            if (!IsMainOrchestrationNativeCapability(capability))
                continue;

            var operationIds = GetResolvedCapabilityOperationIds(capability);
            foreach (var steps in leafNativeSteps)
            {
                steps.RemoveAll(step => PlannedNativeStepClaimsCoalescedCapability(
                    step,
                    capability,
                    operationIds));
            }
            mainNativeSteps.RemoveAll(step => PlannedNativeStepClaimsCoalescedCapability(
                step,
                capability,
                operationIds));
            mainNativeSteps.Add(new PipelinePlannedNativeStep(
                capability.Method!,
                true,
                capability.Description,
                operationIds,
                string.IsNullOrWhiteSpace(capability.CatalogId)
                    ? Array.Empty<string>()
                    : [capability.CatalogId]));
        }
    }

    private static bool PlannedNativeStepClaimsCoalescedCapability(
        PipelinePlannedNativeStep step,
        ResolvedCapability capability,
        IReadOnlyList<string> operationIds)
        => string.Equals(step.Method, capability.Method, StringComparison.Ordinal)
           && (step.OperationIds.Any(operationIds.Contains)
               || !string.IsNullOrWhiteSpace(capability.CatalogId)
               && step.CatalogIds.Contains(capability.CatalogId, StringComparer.Ordinal));

    private static (IReadOnlyList<WorkflowPipelineSubworkflowSpec> Specs, string MainWorkflowPrompt)
        ConsolidateSplitConditionalVariantSpecs(
            IReadOnlyList<WorkflowPipelineSubworkflowSpec> originalSpecs,
            IReadOnlyList<WorkflowPipelineSubworkflowSpec> reassignedSpecs,
            IReadOnlyList<ResolvedCapability> capabilities,
            string mainWorkflowPrompt)
    {
        var working = reassignedSpecs.ToArray();
        var removed = new HashSet<int>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in capabilities
                     .Where(static capability => capability.Activation is not null)
                     .GroupBy(static capability => capability.Activation!.Group, StringComparer.Ordinal))
        {
            var groupCapabilities = group.ToArray();
            var owners = working
                .Select((spec, index) => new { Spec = spec, Index = index })
                .Where(item => item.Spec.PlannedTools.Any(tool => string.Equals(
                    tool.Activation?.Group,
                    group.Key,
                    StringComparison.Ordinal)))
                .Select(static item => item.Index)
                .Distinct()
                .ToArray();
            if (owners.Length != 1)
                continue;

            var ownerIndex = owners[0];
            for (var index = 0; index < originalSpecs.Count; index++)
            {
                if (index == ownerIndex
                    || removed.Contains(index)
                    || !IsConsolidatableConditionalVariant(
                        originalSpecs[index],
                        working[index],
                        groupCapabilities))
                {
                    continue;
                }

                working[ownerIndex] = MergeConditionalVariantSpec(
                    working[ownerIndex],
                    working[index],
                    group.Key);
                aliases[working[index].Name] = working[ownerIndex].Name;
                removed.Add(index);
            }
        }

        if (removed.Count == 0)
            return (reassignedSpecs, mainWorkflowPrompt);

        foreach (var (alias, owner) in aliases)
        {
            mainWorkflowPrompt = Regex.Replace(
                mainWorkflowPrompt,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(alias)}(?![A-Za-z0-9_])",
                owner,
                RegexOptions.CultureInvariant);
        }

        return (
            working.Where((_, index) => !removed.Contains(index)).ToArray(),
            mainWorkflowPrompt);
    }

    private static bool IsConsolidatableConditionalVariant(
        WorkflowPipelineSubworkflowSpec original,
        WorkflowPipelineSubworkflowSpec reassigned,
        IReadOnlyList<ResolvedCapability> groupCapabilities)
    {
        if (reassigned.PlannedTools.Count > 0
            || (reassigned.PlannedNativeSteps?.Count ?? 0) > 0
            || (reassigned.LocalOperationIds?.Count ?? 0) > 0)
        {
            return false;
        }

        var identifiedPlans = original.PlannedTools
            .Where(static tool => tool.Required
                                  && (tool.OperationIds.Count > 0 || tool.CatalogIds.Count > 0))
            .ToArray();
        return identifiedPlans.Length > 0
               && identifiedPlans.All(tool => groupCapabilities.Any(capability =>
                   PlannedToolMatchesCapability(tool, capability)
                   && PlannedToolCarriesCapabilityIdentity(tool, capability)));
    }

    private static WorkflowPipelineSubworkflowSpec MergeConditionalVariantSpec(
        WorkflowPipelineSubworkflowSpec owner,
        WorkflowPipelineSubworkflowSpec variant,
        string activationGroup)
    {
        var inputs = MergeConditionalVariantStringContracts(owner.Inputs, variant.Inputs);
        var outputs = MergeConditionalVariantStringContracts(owner.Outputs, variant.Outputs);
        var inputSchemas = MergeConditionalVariantSchemas(owner.InputSchemas, variant.InputSchemas);
        var outputSchemas = MergeConditionalVariantSchemas(owner.OutputSchemas, variant.OutputSchemas);
        var merged = owner with
        {
            Goal = $"Execute exactly one runtime-selected branch of conditional capability group '{activationGroup}'.",
            Description = "Execute one mutually exclusive conditional capability branch from a runtime decision.",
            ConcreteOutcome = "One typed result from exactly one selected conditional capability branch.",
            Inputs = inputs,
            Outputs = outputs,
            InputSchemas = inputSchemas,
            OutputSchemas = outputSchemas,
            ExtractReason = string.Join(' ', new[] { owner.ExtractReason, variant.ExtractReason }
                .Where(static value => !string.IsNullOrWhiteSpace(value))),
            Content = $"Conditional branch contract '{owner.Name}': {owner.Content.Trim()} "
                      + $"Conditional branch contract '{variant.Name}': {variant.Content.Trim()} "
                      + $"Execute them as mutually exclusive alternatives of activation group '{activationGroup}', never sequentially.",
            ExtractionScore = null
        };
        return merged with { GenerationPrompt = BuildSubworkflowGenerationPrompt(merged) };
    }

    private static IReadOnlyDictionary<string, string> MergeConditionalVariantStringContracts(
        IReadOnlyDictionary<string, string> owner,
        IReadOnlyDictionary<string, string> variant)
    {
        var merged = owner.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, value) in variant)
            merged.TryAdd(name, value);
        return merged;
    }

    private static IReadOnlyDictionary<string, JsonNode?> MergeConditionalVariantSchemas(
        IReadOnlyDictionary<string, JsonNode?> owner,
        IReadOnlyDictionary<string, JsonNode?> variant)
    {
        var merged = owner.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value?.DeepClone(),
            StringComparer.Ordinal);
        foreach (var (name, schema) in variant)
            merged.TryAdd(name, schema?.DeepClone());
        return merged;
    }

    private static void RefineUnlockedAdvisorySelectorBindings(
        WorkflowPipelineSubworkflowSpec spec,
        List<PipelinePlannedTool> plannedTools,
        PipelineMcpContext pipelineMcpContext)
    {
        for (var index = 0; index < plannedTools.Count; index++)
        {
            var planned = plannedTools[index];
            if (planned.RequestBindings.Count > 0
                || planned.OperationIds.Count > 0
                || planned.CatalogIds.Count > 0
                || !string.Equals(planned.Kind, "tool", StringComparison.Ordinal))
            {
                continue;
            }

            var tool = pipelineMcpContext.Servers
                .FirstOrDefault(server => string.Equals(server.Name, planned.Server, StringComparison.Ordinal))?
                .Tools.FirstOrDefault(candidate => string.Equals(candidate.Name, planned.Method, StringComparison.Ordinal));
            if (tool?.InputSchema == null)
                continue;

            var evidence = string.Join('\n', new[]
            {
                BuildPipelineSpecIntentText(spec),
                planned.Purpose
            }.Where(static value => !string.IsNullOrWhiteSpace(value))!);
            var variants = SelectAdvisorySelectorVariants(tool.InputSchema, evidence);
            if (variants.Count != 1)
                continue;

            plannedTools[index] = planned with { RequestBindings = variants[0].Bindings };
        }
    }

    private static IReadOnlyList<SelectorVariant> SelectAdvisorySelectorVariants(
        JsonNode? inputSchema,
        string evidence)
    {
        var assigned = SelectClauseSelectorVariants(inputSchema, evidence);
        if (assigned.Count > 0)
            return assigned;

        var mentioned = ExtractSelectorVariants(inputSchema)
            .Where(static variant => variant.Bindings.Count > 0)
            .Where(variant => variant.Bindings.All(binding =>
                ContainsPositiveSelectorValueMention(evidence, binding)))
            .OrderByDescending(static variant => variant.Bindings.Count)
            .ThenBy(static variant => CanonicalizeBindings(variant.Bindings), StringComparer.Ordinal)
            .ToArray();
        return mentioned
            .Where(candidate => !mentioned.Any(other => other.Bindings.Count > candidate.Bindings.Count
                                                        && IsBindingSubset(candidate.Bindings, other.Bindings)))
            .ToArray();
    }

    private static string GetPipelineCapabilityCompositionGroup(ResolvedCapability capability)
        => capability.Activation?.Group
           ?? (string.Equals(capability.ExternalEffectKind, "write", StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(capability.OperationId)
               ? capability.OperationId
               : capability.Id);

    private static bool IsMainOrchestrationNativeCapability(ResolvedCapability capability)
        => capability.Method is "human.input" or "emit";

    private static bool PlannedToolMatchesCapability(
        PipelinePlannedTool tool,
        ResolvedCapability capability)
        => string.Equals(tool.Server, capability.Server, StringComparison.Ordinal)
           && string.Equals(tool.Kind, capability.Kind, StringComparison.Ordinal)
           && string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)
           && RequestBindingsEqual(tool.RequestBindings, capability.RequestBindings);

    private static int? FindUniqueDeclaredLockedCapabilityOwnerIndex(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var owners = specs
            .Select((spec, index) => new { spec, index })
            .Where(item => item.spec.PlannedTools.Any(tool => capabilities.Any(capability =>
                PlannedToolMatchesCapability(tool, capability)
                && PlannedToolCarriesCapabilityIdentity(tool, capability))))
            .Where(item => !ExplicitlyRejectsLockedCapabilityOwnership(capabilities, item.spec))
            .Select(static item => item.index)
            .Distinct()
            .Take(2)
            .ToArray();
        return owners.Length == 1 ? owners[0] : null;
    }

    private static bool ExplicitlyRejectsLockedCapabilityOwnership(
        IReadOnlyList<ResolvedCapability> capabilities,
        WorkflowPipelineSubworkflowSpec spec)
    {
        var intent = BuildPipelineSpecIntentText(spec);
        if (DeclaresNoExternalCalls(intent))
            return true;

        return capabilities.Any(capability => ExplicitlyRejectsCapabilityOwnership(
            intent,
            capability.Method,
            capability.ExternalEffectKind,
            string.Join(' ', new[]
            {
                capability.Description,
                capability.Method,
                capability.CapabilityDescription
            }.Where(static value => !string.IsNullOrWhiteSpace(value))!)));
    }

    private static bool ExplicitlyRejectsCapabilityOwnership(
        string intent,
        string? method,
        string? externalEffectKind,
        string capabilityText)
    {
        if (!string.IsNullOrWhiteSpace(method)
            && ContainsOnlyNegatedCapabilityMentions(intent, method))
        {
            return true;
        }

        var normalized = intent.Replace('_', ' ').Replace('-', ' ');
        if (string.Equals(externalEffectKind, "write", StringComparison.Ordinal)
            && Regex.IsMatch(
                normalized,
                @"\b(?:side effect free|no side effects?|without side effects?|no (?:external )?writes?|without (?:external )?writes?|must not (?:publish|post|comment|submit|write|add|create|send))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        foreach (var pattern in CapabilityActionFamilyPatterns.Where(pattern => Regex.IsMatch(
                     capabilityText.Replace('_', ' ').Replace('-', ' '),
                     pattern,
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            var mentioningClauses = SplitCapabilityMentionClauses(normalized)
                .Where(clause => Regex.IsMatch(
                    clause,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToArray();
            if (mentioningClauses.Length > 0
                && mentioningClauses.All(clause => !ContainsPositiveActionFamilyMention(clause, pattern)))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveClaimedOrMatchingLockedToolPlans(
        List<PipelinePlannedTool> plannedTools,
        IReadOnlyList<ResolvedCapability> lockedCapabilities)
        => plannedTools.RemoveAll(tool => lockedCapabilities.Any(capability =>
            PlannedToolMatchesCapability(tool, capability)
            || PlannedToolCarriesCapabilityIdentity(tool, capability)));

    private static void RemoveRedundantUnlockedWholeToolPlans(
        List<PipelinePlannedTool> plannedTools,
        IReadOnlyList<ResolvedCapability> lockedCapabilities)
    {
        plannedTools.RemoveAll(candidate => candidate.RequestBindings.Count == 0
                                            && plannedTools.Any(other => !ReferenceEquals(other, candidate)
                                                                         && other.RequestBindings.Count > 0
                                                                         && string.Equals(other.Server, candidate.Server, StringComparison.Ordinal)
                                                                         && string.Equals(other.Kind, candidate.Kind, StringComparison.Ordinal)
                                                                         && string.Equals(other.Method, candidate.Method, StringComparison.Ordinal))
                                            && !lockedCapabilities.Any(locked => locked.RequestBindings.Count == 0
                                                                                && string.Equals(locked.Server, candidate.Server, StringComparison.Ordinal)
                                                                                && string.Equals(locked.Kind, candidate.Kind, StringComparison.Ordinal)
                                                                                && string.Equals(locked.Method, candidate.Method, StringComparison.Ordinal)));
    }

    private static void RemoveDuplicateLockedToolOccurrences(List<PipelinePlannedTool> plannedTools)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        plannedTools.RemoveAll(tool =>
        {
            if (tool.OperationIds.Count != 1 || tool.CatalogIds.Count != 1)
                return false;
            var identity = tool.OperationIds[0] + "\u001f" + tool.CatalogIds[0];
            return !seen.Add(identity);
        });
    }

    private static void RemoveEncapsulatedUnlockedToolPlans(
        List<PipelinePlannedTool> plannedTools,
        IReadOnlyList<ResolvedCapability> lockedCapabilities,
        PipelineMcpContext pipelineMcpContext)
    {
        foreach (var wrapper in lockedCapabilities.Where(static capability =>
                     string.Equals(capability.Resolution, "mcp", StringComparison.Ordinal)
                     && string.Equals(capability.Kind, "tool", StringComparison.Ordinal)))
        {
            var server = pipelineMcpContext.Servers.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, wrapper.Server, StringComparison.Ordinal));
            var wrapperTool = server?.Tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, wrapper.Method, StringComparison.Ordinal));
            if (wrapperTool == null
                || GetValidatedMcpCompositionContract(wrapperTool, server!.Name) is not
                {
                    Kind: McpCapabilityCompositionConventions.CompleteOperationKind,
                    Encapsulates.Count: > 0
                } composition)
            {
                continue;
            }

            plannedTools.RemoveAll(candidate =>
                string.Equals(candidate.Server, wrapper.Server, StringComparison.Ordinal)
                && composition.Encapsulates.Any(encapsulated =>
                    string.Equals(encapsulated.Kind, candidate.Kind, StringComparison.Ordinal)
                    && string.Equals(encapsulated.Method, candidate.Method, StringComparison.Ordinal))
                && !lockedCapabilities.Any(locked => PlannedToolMatchesCapability(candidate, locked)));
        }
    }

    private static (
        IReadOnlyDictionary<int, IReadOnlyList<string>> LeafAssignments,
        IReadOnlyList<string> MainOperationIds) AssignLocalOperationsToPipelineSpecs(
        IReadOnlyList<ResolvedCapability> localOperations,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var assignments = new Dictionary<int, List<string>>();
        var mainOperationIds = new List<string>();
        foreach (var operation in localOperations)
        {
            var operationId = operation.OperationId ?? operation.Id;
            var operationText = string.Join(' ', new[]
            {
                operation.OperationId,
                operation.Id,
                operation.Description
            }.Where(static value => !string.IsNullOrWhiteSpace(value))!);
            if (Regex.IsMatch(
                    operationText.Replace('_', ' '),
                    @"\b(handle|branch|short[ -]?circuit|stop|skip|cancel|reject|fallback|orchestrat|coordinat|sequence|route|fan[ -]?(out|in))\w*\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                mainOperationIds.Add(operationId);
                continue;
            }
            var operationTokens = ExtractIntentTokens(operationText);
            var selected = specs.Select((spec, index) =>
                {
                    var nameOverlap = ExtractIntentTokens(spec.Name).Count(operationTokens.Contains);
                    var actionOverlap = CountLocalActionFamilyMatches(operationText, spec.Name);
                    var goalOverlap = ExtractIntentTokens(string.Join(' ', new[] { spec.Goal, spec.Description }
                        .Where(static value => !string.IsNullOrWhiteSpace(value))!)).Count(operationTokens.Contains);
                    var contentOverlap = ExtractIntentTokens(string.Join(' ', new[] { spec.ExtractReason, spec.Content }
                        .Where(static value => !string.IsNullOrWhiteSpace(value))!)).Count(operationTokens.Contains);
                    var explicitlyLocal = DeclaresNoExternalCalls(BuildPipelineSpecIntentText(spec));
                    return new
                    {
                        Index = index,
                        NameOverlap = nameOverlap,
                        ActionOverlap = actionOverlap,
                        GoalOverlap = goalOverlap,
                        ExplicitlyLocal = explicitlyLocal,
                        Score = actionOverlap * 60 + nameOverlap * 30 + goalOverlap * 15 + contentOverlap * 2
                                + (explicitlyLocal ? 80 : 0)
                                - spec.PlannedTools.Count * 5,
                        spec.Name
                    };
                })
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
                .First();
            // Local obligations are cheap to preserve in main. Assign one to a leaf only
            // when the leaf's action family actually matches (parse, validate, map, filter,
            // aggregate, and so on). Shared nouns such as result, comment, record, or item
            // are not sufficient because they commonly describe an external consumer leaf.
            if (selected.ActionOverlap == 0)
            {
                mainOperationIds.Add(operationId);
                continue;
            }

            if (!assignments.TryGetValue(selected.Index, out var ids))
            {
                ids = new List<string>();
                assignments[selected.Index] = ids;
            }
            ids.Add(operationId);
        }

        return (
            assignments.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray()),
            mainOperationIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static int CountLocalActionFamilyMatches(string operationText, string leafName)
    {
        var operation = operationText.Replace('_', ' ');
        var leaf = leafName.Replace('_', ' ');
        var families = new[]
        {
            @"\b(filter|select|rank|score|sort|prioriti[sz])\w*\b",
            @"\b(map|mapping|project|projection|shape|shaping)\w*\b",
            @"\b(transform|transformation|convert|conversion|translate|translation|adapt|adaptation)\w*\b",
            @"\b(prepare|preparation|assemble|assembly|build|construct)\w*\b",
            @"\b(parse|parsing|validate|validation|normalize|normalise)\w*\b",
            @"\b(deduplicate|deduplication|unique|merge|reconcile)\w*\b",
            @"\b(aggregate|aggregation|group|summari[sz]e|summary)\w*\b"
        };
        return families.Count(pattern => Regex.IsMatch(operation, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                                         && Regex.IsMatch(leaf, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool SpecMentionsExactDiscoveredCapability(
        WorkflowPipelineSubworkflowSpec spec,
        PipelineMcpContext pipelineMcpContext)
    {
        var intent = BuildPipelineSpecIntentText(spec);
        var clauses = SplitCapabilityMentionClauses(intent);
        return pipelineMcpContext.Servers
            .Where(static server => server.Discovered)
            .Any(server => server.Tools.Any(tool => clauses.Any(clause => ContainsExactCapabilityMethod(
                                                                       clause, server.Name, tool.Name, pipelineMcpContext)
                                                                   && IsPositiveCapabilityInvocationClause(clause, tool.Name)))
                           || server.Prompts.Any(prompt => clauses.Any(clause => ContainsExactCapabilityMethod(
                                                                                 clause, server.Name, prompt.Name, pipelineMcpContext)
                                                                             && IsPositiveCapabilityInvocationClause(clause, prompt.Name))));
    }

    private static void AddExplicitDiscoveredCapabilityMentions(
        WorkflowPipelineSubworkflowSpec spec,
        List<PipelinePlannedTool> plannedTools,
        PipelineMcpContext pipelineMcpContext)
    {
        if (pipelineMcpContext.Servers.Count == 0)
            return;

        var intent = BuildPipelineSpecIntentText(spec);
        var clauses = SplitCapabilityMentionClauses(intent);
        foreach (var server in pipelineMcpContext.Servers.Where(static item => item.Discovered))
        {
            foreach (var tool in server.Tools)
            {
                var mentionIndexes = clauses
                    .Select(static (clause, index) => (Clause: clause, Index: index))
                    .Where(item => ContainsExactCapabilityMethod(item.Clause, server.Name, tool.Name, pipelineMcpContext))
                    .Where(item => IsPositiveCapabilityInvocationClause(item.Clause, tool.Name))
                    .Where(item => !IsExplicitlyOptionalCapabilityInvocation(item.Clause, tool.Name))
                    .Select(static item => item.Index)
                    .ToArray();
                if (mentionIndexes.Length == 0)
                    continue;

                var variants = mentionIndexes
                    .SelectMany(index => SelectInvocationSelectorVariants(
                        tool.InputSchema,
                        clauses,
                        index,
                        pipelineMcpContext))
                    .GroupBy(static variant => CanonicalizeBindings(variant.Bindings), StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .ToArray();

                if (variants.Length == 0)
                {
                    if (plannedTools.Any(existing => string.Equals(existing.Server, server.Name, StringComparison.Ordinal)
                                                     && string.Equals(existing.Kind, "tool", StringComparison.Ordinal)
                                                     && string.Equals(existing.Method, tool.Name, StringComparison.Ordinal)
                                                     && existing.RequestBindings.Count > 0))
                    {
                        continue;
                    }
                    AddExplicitPlannedToolIfMissing(
                        plannedTools,
                        server.Name,
                        "tool",
                        tool.Name,
                        Array.Empty<CapabilityRequestBinding>(),
                        spec.Name);
                    continue;
                }

                foreach (var variant in variants)
                {
                    AddExplicitPlannedToolIfMissing(
                        plannedTools,
                        server.Name,
                        "tool",
                        tool.Name,
                        variant.Bindings,
                        spec.Name);
                }
            }

            foreach (var prompt in server.Prompts)
            {
                if (clauses.Any(clause => ContainsExactCapabilityMethod(clause, server.Name, prompt.Name, pipelineMcpContext)
                                          && IsPositiveCapabilityInvocationClause(clause, prompt.Name)))
                {
                    AddExplicitPlannedToolIfMissing(
                        plannedTools,
                        server.Name,
                        "prompt",
                        prompt.Name,
                        Array.Empty<CapabilityRequestBinding>(),
                        spec.Name);
                }
            }
        }
    }

    private static IReadOnlyList<SelectorVariant> SelectInvocationSelectorVariants(
        JsonNode? inputSchema,
        IReadOnlyList<string> clauses,
        int invocationIndex,
        PipelineMcpContext pipelineMcpContext)
    {
        var direct = SelectClauseSelectorVariants(inputSchema, clauses[invocationIndex]);
        if (direct.Count > 0)
            return direct;

        // Structured planners often render an invocation and its request selector on
        // adjacent YAML-like lines. Extend only until the next concrete invocation;
        // selectors from two calls to the same multi-action tool must never combine.
        var block = new List<string> { clauses[invocationIndex] };
        for (var index = invocationIndex + 1; index < clauses.Count; index++)
        {
            if (ClauseInvokesAnyDiscoveredCapability(clauses[index], pipelineMcpContext))
                break;
            block.Add(clauses[index]);
        }
        return SelectClauseSelectorVariants(inputSchema, string.Join('\n', block));
    }

    private static bool ClauseInvokesAnyDiscoveredCapability(
        string clause,
        PipelineMcpContext pipelineMcpContext)
        => pipelineMcpContext.Servers
            .Where(static server => server.Discovered)
            .Any(server => server.Tools.Any(tool => ContainsExactCapabilityMethod(
                                                       clause, server.Name, tool.Name, pipelineMcpContext)
                                                   && IsPositiveCapabilityInvocationClause(clause, tool.Name))
                           || server.Prompts.Any(prompt => ContainsExactCapabilityMethod(
                                                             clause, server.Name, prompt.Name, pipelineMcpContext)
                                                         && IsPositiveCapabilityInvocationClause(clause, prompt.Name)));

    private static IReadOnlyList<string> SplitCapabilityMentionClauses(string text)
        => Regex.Split(text, @"(?:\r?\n|;|\bthen\b|\bafterwards\b)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static clause => clause.Trim())
            .Where(static clause => clause.Length > 0)
            .ToArray();

    private static IReadOnlyList<PipelinePlannedTool> RemoveToolsMentionedOnlyAsProhibitions(
        WorkflowPipelineSubworkflowSpec spec,
        StructuredPipelineSubworkflowMetadata? structured,
        IReadOnlyList<PipelinePlannedTool> plannedTools)
    {
        if (plannedTools.Count == 0)
            return plannedTools;

        var intent = string.Join('\n', new[]
        {
            spec.Goal,
            spec.ExtractReason,
            spec.Content,
            structured?.Description,
            structured?.ConcreteOutcome
        }.Where(static value => !string.IsNullOrWhiteSpace(value))!);
        return plannedTools
            .Where(tool => !ContainsOnlyNegatedCapabilityMentions(intent, tool.Method))
            .ToArray();
    }

    private static bool ContainsOnlyNegatedCapabilityMentions(string text, string method)
    {
        var clauses = SplitCapabilityMentionClauses(text)
            .Where(clause => ContainsIntentToken(clause, method))
            .ToArray();
        return clauses.Length > 0 && clauses.All(clause => IsNegatedCapabilityMention(clause, method));
    }

    private static bool IsNegatedCapabilityMention(string clause, string method)
    {
        var match = Regex.Match(
            clause,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var prefixStart = Math.Max(0, match.Index - 160);
        var prefix = clause[prefixStart..match.Index];
        return Regex.IsMatch(
            prefix,
            @"\b(?:no|not|never|without|avoid|avoids|forbid|forbids|forbidden|prohibit|prohibits|prohibited|exclude|excludes|excluded|cannot|can't|don't|doesn't|mustn't|shouldn't)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsPositiveCapabilityInvocationClause(string clause, string method)
    {
        var matches = Regex.Matches(
            clause,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            var windowStart = Math.Max(0, match.Index - 180);
            var windowEnd = Math.Min(clause.Length, match.Index + match.Length + 120);
            var prefix = clause[windowStart..match.Index];
            var suffix = clause[(match.Index + match.Length)..windowEnd];
            var window = clause[windowStart..windowEnd];

            if (IsNegatedCapabilityMention(window, method))
                continue;

            // A local producer may document the schema expected by a later capability
            // and explicitly say that the capability must not be called in this leaf.
            // Treat that as a data contract, not as an invocation. The prohibition can
            // follow the method mention ("compatible with a later X request. Do not call
            // that tool here"), so prefix-only negation detection is insufficient.
            if (Regex.IsMatch(
                    window,
                    $@"\b(?:compatible|suitable|formatted|shaped|prepared|intended)\b[^.;\n]{{0,100}}\b(?:for|with)\b[^.;\n]{{0,80}}\b(?:later|subsequent|downstream)\b[^.;\n]{{0,80}}(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    suffix,
                    @"^[\s\S]{0,100}\b(?:do\s+not|does\s+not|must\s+not|should\s+not|never)\s+(?:call|invoke|execute|run|use)\s+(?:it|this\s+(?:tool|method|capability)|that\s+(?:tool|method|capability)|the\s+(?:tool|method|capability))\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            // A method name may legitimately appear only as a producer/consumer contract
            // (for example "files_json is required by analyze_records"). Such a reference
            // is data-flow documentation, not another invocation of the capability.
            if (Regex.IsMatch(
                    window,
                    $@"\b(?:input|output|result|response|value|data|payload|record|records|item|items|field|fields)\w*\b[^.;\n]{{0,100}}\b(?:required|needed|consumed|used|accepted)\s+by\s+(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    window,
                    $@"\b(?:input\s+to|output\s+from|result\s+of|response\s+from|produced\s+by|returned\s+by|consumed\s+by|provided\s+to)\s+(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    window,
                    $@"(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])(?:'s)?\s+(?:output|result|response|data|payload)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (Regex.IsMatch(
                    prefix,
                    @"\b(?:call|invoke|execute|run|use|using|request|query|dispatch|perform|trigger|start|resume|open|connect|read|retrieve|fetch|list|compare|analy[sz]e|inspect|publish|post|submit|write|add|create|send|delete|remove|cleanup|dispose)\w*\b[^.;\n]{0,120}$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    prefix,
                    @"(?:^|[\s{,])[\x22'\x60]?(?:method|tool|prompt)[\x22'\x60]?\s*[:=]\s*[\x22'\x60]*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    prefix,
                    @"\b(?:via|through|with)\s+$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    suffix,
                    @"^\s*[\x22'\x60]*\s*(?:\(|with\b|using\b|via\b|through\b|to\b|for\b|request\b|method\b)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitlyOptionalCapabilityInvocation(string clause, string method)
    {
        var match = Regex.Match(
            clause,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(method)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var windowStart = Math.Max(0, match.Index - 160);
        var windowEnd = Math.Min(clause.Length, match.Index + match.Length + 160);
        var window = clause[windowStart..windowEnd];
        return Regex.IsMatch(
            window,
            @"\b(?:optional|optionally|may|might|could|if\s+[^.;\n]{0,60}\b(?:is\s+)?(?:available|supported|implemented|desired)|when\s+(?:available|supported)|best[- ]effort)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<SelectorVariant> SelectClauseSelectorVariants(JsonNode? inputSchema, string clause)
    {
        var variants = ExtractSelectorVariants(inputSchema)
            .Where(variant => variant.Bindings.Count > 0
                              && variant.Bindings.All(binding => ContainsLiteralSelectorAssignment(clause, binding)))
            .OrderByDescending(static variant => variant.Bindings.Count)
            .ThenBy(static variant => CanonicalizeBindings(variant.Bindings), StringComparer.Ordinal)
            .ToArray();
        return variants
            .Where(candidate => !variants.Any(other => other.Bindings.Count > candidate.Bindings.Count
                                                       && IsBindingSubset(candidate.Bindings, other.Bindings)))
            .ToArray();
    }

    private static bool ContainsExactCapabilityMethod(
        string text,
        string server,
        string method,
        PipelineMcpContext pipelineMcpContext)
    {
        if (!ContainsIntentToken(text, method))
            return false;

        var methodOccurrences = pipelineMcpContext.Servers.Sum(item =>
            item.Tools.Count(tool => string.Equals(tool.Name, method, StringComparison.Ordinal))
            + item.Prompts.Count(prompt => string.Equals(prompt.Name, method, StringComparison.Ordinal)));
        return methodOccurrences == 1 || ContainsIntentToken(text, server);
    }

    private static bool ContainsIntentToken(string text, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return Regex.IsMatch(
            text,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(value)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ContainsLiteralSelectorAssignment(string text, CapabilityRequestBinding binding)
    {
        var literal = binding.Value is JsonValue scalar && scalar.TryGetValue<string>(out var stringValue)
            ? stringValue
            : binding.Value?.ToJsonString();
        if (string.IsNullOrWhiteSpace(literal))
            return false;

        var pathSegment = binding.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(pathSegment))
            return false;
        pathSegment = pathSegment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        var quotedLiteral = "[\\\"'`]?(?:" + Regex.Escape(literal) + ")[\\\"'`]?";
        var assignment = $"(?<![A-Za-z0-9_]){Regex.Escape(pathSegment)}(?![A-Za-z0-9_])[\\\"'`]?\\s*(?:(?:=|:)\\s*|(?:is|equals|set\\s+to|of)?\\s+){quotedLiteral}(?![A-Za-z0-9_])";
        return Regex.IsMatch(text, assignment, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ContainsPositiveLiteralSelectorAssignment(string text, CapabilityRequestBinding binding)
        => SplitCapabilityMentionClauses(text).Any(clause =>
            ContainsLiteralSelectorAssignment(clause, binding)
            && !Regex.IsMatch(
                clause,
                @"\b(?:no|not|never|without|avoid|forbid|prohibit|exclude|cannot|can't|don't|doesn't|mustn't|shouldn't)\b|\b(?:do|does|must|should)\s+not\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static bool ContainsPositiveSelectorValueMention(string text, CapabilityRequestBinding binding)
    {
        if (binding.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalizedValue = value.Replace('_', ' ').Replace('-', ' ');
        var escapedValue = Regex.Escape(normalizedValue)
            .Replace("\\ ", "\\s+", StringComparison.Ordinal);
        var pattern = $@"(?<![A-Za-z0-9]){escapedValue}(?![A-Za-z0-9])";
        return SplitCapabilityMentionClauses(text.Replace('_', ' ').Replace('-', ' ')).Any(clause =>
            Regex.IsMatch(clause, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                clause,
                @"\b(?:no|not|never|without|avoid|forbid|prohibit|exclude|cannot|can't|don't|doesn't|mustn't|shouldn't)\b|\b(?:do|does|must|should)\s+not\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool IsBindingSubset(
        IReadOnlyList<CapabilityRequestBinding> subset,
        IReadOnlyList<CapabilityRequestBinding> superset)
        => subset.All(left => superset.Any(right => string.Equals(left.Path, right.Path, StringComparison.Ordinal)
                                                   && JsonNode.DeepEquals(left.Value, right.Value)));

    private static void AddExplicitPlannedToolIfMissing(
        List<PipelinePlannedTool> plannedTools,
        string server,
        string kind,
        string method,
        IReadOnlyList<CapabilityRequestBinding> bindings,
        string leafName)
    {
        if (plannedTools.Any(tool => string.Equals(tool.Server, server, StringComparison.Ordinal)
                                     && string.Equals(tool.Kind, kind, StringComparison.Ordinal)
                                     && string.Equals(tool.Method, method, StringComparison.Ordinal)
                                     && RequestBindingsEqual(tool.RequestBindings, bindings)))
        {
            return;
        }

        // A more specific occurrence already proves the same prose invocation.
        // Do not add a second, selector-incomplete tool merely because a nearby
        // clause mentions one member of the complete selector set (for example an
        // event next to a method+event variant).
        if (bindings.Count > 0
            && plannedTools.Any(tool => string.Equals(tool.Server, server, StringComparison.Ordinal)
                                        && string.Equals(tool.Kind, kind, StringComparison.Ordinal)
                                        && string.Equals(tool.Method, method, StringComparison.Ordinal)
                                        && tool.RequestBindings.Count > bindings.Count
                                        && IsBindingSubset(bindings, tool.RequestBindings)))
        {
            return;
        }

        var refinableIndex = plannedTools.FindIndex(tool =>
            string.Equals(tool.Server, server, StringComparison.Ordinal)
            && string.Equals(tool.Kind, kind, StringComparison.Ordinal)
            && string.Equals(tool.Method, method, StringComparison.Ordinal)
            && tool.RequestBindings.Count < bindings.Count
            && IsBindingSubset(tool.RequestBindings, bindings));
        if (refinableIndex >= 0)
        {
            var refinable = plannedTools[refinableIndex];
            // Locked occurrences are exact (operation_id, catalog_id) identities.
            // Prose may require additional request fields, but changing the locked
            // selector bindings would erase the catalog occurrence from deterministic
            // multiset validation. Keep it exact; the leaf content still carries the
            // additional request detail for generation.
            if (refinable.OperationIds.Count > 0 || refinable.CatalogIds.Count > 0)
                return;

            plannedTools[refinableIndex] = refinable with
            {
                RequestBindings = bindings
            };
            return;
        }

        plannedTools.Add(new PipelinePlannedTool(
            server,
            kind,
            method,
            true,
            $"Required by the concrete discovered capability named in leaf '{leafName}'.",
            Array.Empty<string>(),
            Array.Empty<string>(),
            bindings,
            Array.Empty<string>(),
            Array.Empty<string>()));
    }

    private static int SelectPipelineCapabilityTargetIndex(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs,
        IReadOnlyList<List<PipelinePlannedTool>> tools,
        IReadOnlySet<int> localSpecIndices,
        IReadOnlySet<int>? explicitlyRejectedSpecIndices = null)
    {
        var operationIntentText = string.Join(' ', capabilities
            .Select(static capability => capability.Description)
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        var concreteCapabilityText = string.Join(' ', capabilities.SelectMany(static capability => new[]
        {
            capability.Method,
            capability.CapabilityDescription
        }).Where(static value => !string.IsNullOrWhiteSpace(value))!);
        var operationText = string.Join(' ', capabilities.SelectMany(static capability => new[]
        {
            capability.OperationId,
            capability.Id,
            capability.Description,
            capability.Server,
            capability.Kind,
            capability.Method,
            capability.CapabilityDescription,
            capability.RequestBindings.Count == 0 ? null : FormatBindingsCompact(capability.RequestBindings)
        }).Where(static value => !string.IsNullOrWhiteSpace(value))!);
        var operationTokens = ExtractIntentTokens(operationText);
        var operationIntentTokens = ExtractIntentTokens(operationIntentText);
        var semanticOperationTokens = operationIntentTokens.Count > 0 ? operationIntentTokens : operationTokens;
        var concreteActionFamilyCount = CapabilityActionFamilyPatterns.Count(pattern => Regex.IsMatch(
            concreteCapabilityText.Replace('_', ' ').Replace('-', ' '),
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        var candidates = specs.Select((spec, index) =>
            {
                var intent = BuildPipelineSpecIntentText(spec);
                var nameTokens = ExtractIntentTokens(spec.Name);
                var goalTokens = ExtractIntentTokens(string.Join(' ', new[] { spec.Goal, spec.Description }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))!));
                var contentTokens = ExtractIntentTokens(string.Join(' ', new[] { spec.ExtractReason, spec.Content }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))!));
                var nameOverlap = nameTokens.Count(semanticOperationTokens.Contains);
                var goalOverlap = goalTokens.Count(semanticOperationTokens.Contains);
                var contentOverlap = contentTokens.Count(operationTokens.Contains);
                var actionOverlap = CountFocusedCapabilityActionFamilyMatches(
                    operationIntentText,
                    concreteCapabilityText,
                    intent);
                var extraneousActionFamilies = CountExtraneousFocusedCapabilityActionFamilies(
                    operationIntentText,
                    concreteCapabilityText,
                    intent);
                var normalizedName = spec.Name.Replace('_', ' ').Replace('-', ' ');
                var nameActionOverlap = CountFocusedCapabilityActionFamilyMatches(
                    operationIntentText,
                    concreteCapabilityText,
                    normalizedName);
                var concreteNameActionOverlap = CountPositiveCapabilityActionFamilyMatches(
                    concreteCapabilityText,
                    normalizedName);
                var concreteActionOverlap = CountPositiveCapabilityActionFamilyMatches(
                    concreteCapabilityText,
                    intent);
                var nameExtraneousActionFamilies = CountExtraneousFocusedCapabilityActionFamilies(
                    operationIntentText,
                    concreteCapabilityText,
                    normalizedName);
                var clauses = SplitCapabilityMentionClauses(intent);
                var exactCapabilityMentions = capabilities.Count(capability =>
                    !string.IsNullOrWhiteSpace(capability.Method)
                    && clauses.Any(clause => ContainsIntentToken(clause, capability.Method!)
                                             && IsPositiveCapabilityInvocationClause(clause, capability.Method!)));
                var selectorMatches = capabilities.Count(capability =>
                    capability.RequestBindings.Count > 0
                    && capability.RequestBindings.All(binding =>
                        ContainsPositiveLiteralSelectorAssignment(intent, binding)
                        || ContainsPositiveSelectorValueMention(
                            string.Join(' ', new[] { spec.Name, spec.Goal, spec.Description, spec.ConcreteOutcome }
                                .Where(static value => !string.IsNullOrWhiteSpace(value))!),
                            binding)));
                var lockedIdentityMentions = capabilities.Count(capability =>
                    ContainsLockedCapabilityIdentity(intent, capability.Id, capability.CatalogId));
                var external = IsExternalWorkSpec(spec) || tools[index].Count > 0;
                var explicitlyInternal = localSpecIndices.Contains(index)
                                         && !ContainsExternalWorkIntent(intent)
                                         || DeclaresNoExternalCalls(intent) && !ContainsExternalWorkIntent(intent)
                                         || explicitlyRejectedSpecIndices?.Contains(index) == true;
                return new
                {
                    Index = index,
                    LockedIdentityMentions = lockedIdentityMentions,
                    SelectorMatches = selectorMatches,
                    ExactCapabilityMentions = exactCapabilityMentions,
                    NameIntentOverlap = nameOverlap,
                    ConcreteNameActionOverlap = concreteNameActionOverlap,
                    ConcreteActionOverlap = concreteActionOverlap,
                    NameActionOverlap = nameActionOverlap,
                    NameExtraneousActionFamilies = nameExtraneousActionFamilies,
                    ActionOverlap = actionOverlap,
                    ExtraneousActionFamilies = extraneousActionFamilies,
                    Score = nameOverlap * 100 + goalOverlap * 20 + contentOverlap
                            + (external ? 3 : 0)
                            - (explicitlyInternal ? 100 : 0),
                    PlannedCount = tools[index].Count,
                    ExplicitlyInternal = explicitlyInternal,
                    spec.Name
                };
            })
            .ToArray();
        var externalCandidates = candidates.Where(static item => !item.ExplicitlyInternal).ToArray();
        var strongestOverallNameActionOverlap = candidates.Max(static item => item.NameActionOverlap);
        var strongestExternalNameActionOverlap = externalCandidates.Length == 0
            ? -1
            : externalCandidates.Max(static item => item.NameActionOverlap);
        var strongestOverallNameIntentOverlap = candidates.Max(static item => item.NameIntentOverlap);
        var strongestExternalNameIntentOverlap = externalCandidates.Length == 0
            ? -1
            : externalCandidates.Max(static item => item.NameIntentOverlap);
        var strongerActionOwner = ShouldOverrideAdvisoryLocalClassification(
            strongestOverallNameActionOverlap,
            strongestExternalNameActionOverlap);
        var strongerIntentOwner = ShouldOverrideAdvisoryLocalClassification(
            strongestOverallNameIntentOverlap,
            strongestExternalNameIntentOverlap);
        var eligible = strongerActionOwner
            ? candidates.Where(item => item.NameActionOverlap == strongestOverallNameActionOverlap)
            : strongerIntentOwner
                ? candidates.Where(item => item.NameIntentOverlap == strongestOverallNameIntentOverlap)
            : externalCandidates.Length > 0
                ? externalCandidates.AsEnumerable()
                : candidates.AsEnumerable();
        var hasSemanticOwnershipEvidence = false;
        var selectorCapabilityCount = capabilities.Count(static capability => capability.RequestBindings.Count > 0);
        if (selectorCapabilityCount > 0
            && eligible.Any(item => item.SelectorMatches == selectorCapabilityCount))
        {
            // A documented literal selector is stronger than lexical similarity. This is
            // what keeps logical variants of one multi-action MCP tool on distinct leaves.
            eligible = eligible.Where(item => item.SelectorMatches == selectorCapabilityCount);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumConcreteNameActionOverlap = eligible.Max(static item => item.ConcreteNameActionOverlap);
        if (concreteActionFamilyCount == 1 && maximumConcreteNameActionOverlap > 0)
        {
            // Concrete physical capability semantics own prerequisite placement. A broad
            // operation such as review may require read capabilities, but those reads
            // still belong to the dedicated read/context leaf when one exists.
            eligible = eligible.Where(item => item.ConcreteNameActionOverlap == maximumConcreteNameActionOverlap);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumConcreteActionOverlap = eligible.Max(static item => item.ConcreteActionOverlap);
        if (concreteActionFamilyCount == 1 && maximumConcreteActionOverlap > 0)
        {
            eligible = eligible.Where(item => item.ConcreteActionOverlap == maximumConcreteActionOverlap);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumNameActionOverlap = eligible.Max(static item => item.NameActionOverlap);
        if (maximumNameActionOverlap > 0)
        {
            eligible = eligible.Where(item => item.NameActionOverlap == maximumNameActionOverlap);
            var minimumNameExtraneousActionFamilies = eligible.Min(static item => item.NameExtraneousActionFamilies);
            eligible = eligible.Where(item => item.NameExtraneousActionFamilies == minimumNameExtraneousActionFamilies);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumNameIntentOverlap = eligible.Max(static item => item.NameIntentOverlap);
        if (maximumNameIntentOverlap > 0)
        {
            // Once the concrete action family is cohesive, leaf-name intent distinguishes
            // its semantic owner from neighboring leaves that mention the same resource.
            eligible = eligible.Where(item => item.NameIntentOverlap == maximumNameIntentOverlap);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumActionOverlap = eligible.Max(static item => item.ActionOverlap);
        if (maximumActionOverlap > 0)
        {
            // Capability occurrence IDs are model-authored extraction hints. Prefer a
            // cohesive leaf whose action family matches the locked operation before
            // trusting an opaque ID that may have been grouped under a broad review leaf.
            eligible = eligible.Where(item => item.ActionOverlap == maximumActionOverlap);
            var minimumExtraneousActionFamilies = eligible.Min(static item => item.ExtraneousActionFamilies);
            eligible = eligible.Where(item => item.ExtraneousActionFamilies == minimumExtraneousActionFamilies);
            hasSemanticOwnershipEvidence = true;
        }
        if (!hasSemanticOwnershipEvidence && eligible.Any(static item => item.ExactCapabilityMentions > 0))
        {
            // An explicit positive invocation is authoritative. Consumer references and
            // prohibitions were removed clause-by-clause before this point, so lexical
            // similarity must not move the call to a leaf that merely discusses its data.
            eligible = eligible.Where(static item => item.ExactCapabilityMentions > 0);
            hasSemanticOwnershipEvidence = true;
        }
        if (!hasSemanticOwnershipEvidence && eligible.Any(static item => item.LockedIdentityMentions > 0))
            eligible = eligible.Where(static item => item.LockedIdentityMentions > 0);
        return eligible
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.PlannedCount)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .First().Index;
    }

    private static bool ShouldOverrideAdvisoryLocalClassification(
        int strongestOverallNameIntentOverlap,
        int strongestExternalNameIntentOverlap)
        => strongestOverallNameIntentOverlap > 0
           && strongestOverallNameIntentOverlap > strongestExternalNameIntentOverlap;

    private static int CountPositiveCapabilityActionFamilyMatches(string capabilityText, string leafText)
    {
        var capability = capabilityText.Replace('_', ' ').Replace('-', ' ');
        var leafClauses = SplitCapabilityMentionClauses(leafText.Replace('_', ' ').Replace('-', ' '));
        return CapabilityActionFamilyPatterns.Count(pattern => Regex.IsMatch(
                                             capability,
                                             pattern,
                                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                                         && leafClauses.Any(clause => ContainsPositiveActionFamilyMention(clause, pattern)));
    }

    private static int CountFocusedCapabilityActionFamilyMatches(
        string operationIntentText,
        string concreteCapabilityText,
        string leafText)
    {
        var leafClauses = SplitCapabilityMentionClauses(leafText.Replace('-', ' '));
        return SelectFocusedCapabilityActionFamilies(operationIntentText, concreteCapabilityText)
            .Count(pattern => leafClauses.Any(clause => ContainsPositiveActionFamilyMention(clause, pattern)));
    }

    private static int CountExtraneousFocusedCapabilityActionFamilies(
        string operationIntentText,
        string concreteCapabilityText,
        string leafText)
    {
        var focused = SelectFocusedCapabilityActionFamilies(operationIntentText, concreteCapabilityText)
            .ToHashSet(StringComparer.Ordinal);
        var leafClauses = SplitCapabilityMentionClauses(leafText.Replace('-', ' '));
        return CapabilityActionFamilyPatterns.Count(pattern => !focused.Contains(pattern)
            && leafClauses.Any(clause => ContainsPositiveActionFamilyMention(clause, pattern)));
    }

    private static IReadOnlyList<string> SelectFocusedCapabilityActionFamilies(
        string operationIntentText,
        string concreteCapabilityText)
    {
        var operation = operationIntentText.Replace('_', ' ').Replace('-', ' ');
        var concrete = concreteCapabilityText.Replace('_', ' ').Replace('-', ' ');
        var operationFamilies = CapabilityActionFamilyPatterns
            .Where(pattern => Regex.IsMatch(operation, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        var shared = operationFamilies
            .Where(pattern => Regex.IsMatch(concrete, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        if (shared.Length > 0)
            return shared;
        if (operationFamilies.Length > 0)
            return operationFamilies;
        return CapabilityActionFamilyPatterns
            .Where(pattern => Regex.IsMatch(concrete, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
    }

    private static readonly string[] CapabilityActionFamilyPatterns =
    [
        @"\b(clone|materiali[sz]e|checkout|download|copy)\w*\b",
        @"\b(compare|comparison|diff|patch)\w*\b",
        @"\b(read|retrieve|get|list|fetch|query|check|status)\w*\b",
        @"\b(analy[sz]e|analysis|inspect|evaluate|review)\w*\b",
        @"\b(publish|post|comment|submit|write|add|create|send)\w*\b",
        @"\b(delete|remove|cleanup|clean\s+up|dispose)\w*\b",
        @"\b(start|open|resume|connect|disconnect|abort|cancel|close)\w*\b",
        @"\b(install|restore|dependency|dependencies|package|packages)\w*\b",
        @"\b(test|tests|testing|unit\s+test|integration\s+test)\w*\b",
        @"\b(lint|linter|format|formatter|static\s+analysis)\w*\b",
        @"\b(build|compile|compilation)\w*\b",
        @"\b(parse|validate|normalize|deduplicate|filter|map|shape|project)\w*\b"
    ];

    private static bool ContainsLockedCapabilityIdentity(
        string text,
        string? occurrenceId,
        string? catalogId)
    {
        var identities = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(catalogId))
            identities.Add(catalogId);
        // Plain operation IDs can be shared by every member of a composition. Only an
        // occurrence-form ID is sufficiently specific to determine one capability owner.
        if (!string.IsNullOrWhiteSpace(occurrenceId)
            && occurrenceId.Contains("::", StringComparison.Ordinal))
        {
            identities.Add(occurrenceId);
        }

        return identities.Any(identity => ContainsPositiveLockedCapabilityIdentityMention(text, identity));
    }

    private static bool ContainsPositiveLockedCapabilityIdentityMention(string text, string identity)
    {
        var matches = Regex.Matches(
            text,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(identity)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = text.IndexOf('\n', match.Index + match.Length);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            var clause = text[lineStart..lineEnd];
            if (IsNegatedCapabilityMention(clause, identity)
                || Regex.IsMatch(
                    clause,
                    @"\b(?:must|should|does|do)\s+not\s+(?:perform|own|include|assign|use|call|invoke|execute|run)|\b(?:belongs?|assigned)\s+to\s+(?:the\s+)?(?:caller|main|another|other)\b|\bnot\s+(?:in|inside|owned\s+by|assigned\s+to)\s+(?:this|the)\s+leaf\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            // Annotated extraction commonly uses a section heading followed by a
            // bullet list of opaque occurrence IDs. Preserve that local context so
            // an ID listed under "must not perform" is not mistaken for ownership,
            // while an ID under "owns exactly" remains authoritative.
            var contextStart = Math.Max(0, match.Index - 900);
            var prefix = text[contextStart..match.Index];
            var positiveMarkers = Regex.Matches(
                prefix,
                @"\b(?:owns?\s+(?:exactly\s+)?|owned\s+by|planned\s+direct|required\s+(?:locked\s+)?(?:capabilit(?:y|ies)|occurrences?)|direct\s+mcp\.call)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var negativeMarkers = Regex.Matches(
                prefix,
                @"\b(?:(?:must|should|does|do)\s+not\s+(?:perform|own|include|assign|use|call|invoke|execute|run)|without\s+(?:calling|using|invoking)|prohibited\s+(?:capabilit(?:y|ies)|operations?)|excluded\s+(?:capabilit(?:y|ies)|operations?))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var lastPositive = positiveMarkers.Count == 0 ? -1 : positiveMarkers[positiveMarkers.Count - 1].Index;
            var lastNegative = negativeMarkers.Count == 0 ? -1 : negativeMarkers[negativeMarkers.Count - 1].Index;
            if (lastNegative > lastPositive)
                continue;
            if (lastPositive >= 0)
                return true;

            if (Regex.IsMatch(
                clause,
                @"\b(?:call|invoke|execute|run|use|using|request|query|perform|trigger|read|retrieve|fetch|list|compare|analy[sz]e|inspect|publish|post|submit|write|add|create|send|delete|remove|cleanup|dispose|own|owns|owned|required|planned|direct|capabilit(?:y|ies)|occurrence)\w*\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPositiveActionFamilyMention(string clause, string pattern)
    {
        foreach (Match match in Regex.Matches(
                     clause,
                     pattern,
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (match.Value.Contains('_', StringComparison.Ordinal))
                continue;
            var prefixStart = Math.Max(0, match.Index - 160);
            var prefix = clause[prefixStart..match.Index];
            if (!Regex.IsMatch(
                    prefix,
                    @"\b(?:no|not|never|without|avoid|forbid|prohibit|exclude|cannot|can't|don't|doesn't|mustn't|shouldn't)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DeclaresNoExternalCalls(string content)
    {
        var normalized = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        return Regex.IsMatch(
                   normalized,
                   @"\b(?:do|does|must|should)\s+not\s+(?:use|call|invoke|execute|run)\s+(?:(?:any|a|an|the)\s+)?(?:mcp|llm|external)\b",
                   RegexOptions.CultureInvariant)
               || Regex.IsMatch(
                   normalized,
                   @"\b(?:without|no)\s+(?:(?:any|a|an|the)\s+)?(?:mcp|llm|external)\s+(?:calls?|tools?|operations?|work)\b",
                   RegexOptions.CultureInvariant)
               || normalized.Contains("no mcp", StringComparison.Ordinal)
               || normalized.Contains("without mcp", StringComparison.Ordinal)
               || normalized.Contains("do not use mcp", StringComparison.Ordinal)
               || normalized.Contains("does not use mcp", StringComparison.Ordinal)
               || normalized.Contains("must not use mcp", StringComparison.Ordinal)
               || normalized.Contains("should not use mcp", StringComparison.Ordinal)
               || normalized.Contains("do not invoke mcp", StringComparison.Ordinal)
               || normalized.Contains("must not invoke mcp", StringComparison.Ordinal)
               || normalized.Contains("should not invoke mcp", StringComparison.Ordinal)
               || normalized.Contains("do not call mcp", StringComparison.Ordinal)
               || normalized.Contains("does not call mcp", StringComparison.Ordinal)
               || normalized.Contains("must not call mcp", StringComparison.Ordinal)
               || normalized.Contains("should not call mcp", StringComparison.Ordinal)
               || normalized.Contains("local deterministic processing", StringComparison.Ordinal)
                  && (normalized.Contains("no calls", StringComparison.Ordinal)
                      || normalized.Contains("without calls", StringComparison.Ordinal)
                      || normalized.Contains("no tools", StringComparison.Ordinal))
               || normalized.Contains("deterministic local processing", StringComparison.Ordinal)
                  && (normalized.Contains("no calls", StringComparison.Ordinal)
                      || normalized.Contains("without calls", StringComparison.Ordinal)
                      || normalized.Contains("no tools", StringComparison.Ordinal))
               || normalized.Contains("no external call", StringComparison.Ordinal)
               || normalized.Contains("no external tool", StringComparison.Ordinal)
               || normalized.Contains("without external call", StringComparison.Ordinal)
               || normalized.Contains("without external tool", StringComparison.Ordinal)
               || normalized.Contains("do not call external", StringComparison.Ordinal)
               || normalized.Contains("does not call external", StringComparison.Ordinal)
               || normalized.Contains("must not call external", StringComparison.Ordinal)
               || normalized.Contains("should not call external", StringComparison.Ordinal)
               || normalized.Contains("do not invoke external", StringComparison.Ordinal)
               || normalized.Contains("must not invoke external", StringComparison.Ordinal)
               || normalized.Contains("no llm", StringComparison.Ordinal)
               || normalized.Contains("without llm", StringComparison.Ordinal);
    }

    private static string? SelectPipelineCapabilityTarget(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyDictionary<string, WorkflowPipelineSubworkflowSpec> specs,
        IReadOnlyDictionary<string, StructuredPipelineSubworkflowMetadata> metadata)
    {
        var operationIntentText = string.Join(' ', capabilities
            .Select(static capability => capability.Description)
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        var concreteCapabilityText = string.Join(' ', capabilities.SelectMany(static capability => new[]
        {
            capability.Method,
            capability.CapabilityDescription
        }).Where(static value => !string.IsNullOrWhiteSpace(value))!);
        var operationText = string.Join(' ', capabilities.SelectMany(static capability => new[]
        {
            capability.OperationId,
            capability.Id,
            capability.Description,
            capability.Server,
            capability.Kind,
            capability.Method,
            capability.CapabilityDescription,
            capability.RequestBindings.Count == 0 ? null : FormatBindingsCompact(capability.RequestBindings)
        }).Where(static value => !string.IsNullOrWhiteSpace(value))!);
        var operationTokens = ExtractIntentTokens(operationText);
        var operationIntentTokens = ExtractIntentTokens(operationIntentText);
        var semanticOperationTokens = operationIntentTokens.Count > 0 ? operationIntentTokens : operationTokens;
        var candidates = metadata.Values
            .Where(item => specs.ContainsKey(item.Name))
            .Select(item =>
            {
                var spec = specs[item.Name];
                var intent = BuildPipelineSpecIntentText(spec);
                var tokens = ExtractIntentTokens(intent);
                var overlap = tokens.Count(operationTokens.Contains);
                var nameOverlap = ExtractIntentTokens(spec.Name).Count(semanticOperationTokens.Contains);
                var goalOverlap = ExtractIntentTokens(string.Join(' ', new[] { spec.Goal, spec.Description }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))!)).Count(semanticOperationTokens.Contains);
                var actionOverlap = CountFocusedCapabilityActionFamilyMatches(
                    operationIntentText,
                    concreteCapabilityText,
                    intent);
                var extraneousActionFamilies = CountExtraneousFocusedCapabilityActionFamilies(
                    operationIntentText,
                    concreteCapabilityText,
                    intent);
                var normalizedName = spec.Name.Replace('_', ' ').Replace('-', ' ');
                var nameActionOverlap = CountFocusedCapabilityActionFamilyMatches(
                    operationIntentText,
                    concreteCapabilityText,
                    normalizedName);
                var nameExtraneousActionFamilies = CountExtraneousFocusedCapabilityActionFamilies(
                    operationIntentText,
                    concreteCapabilityText,
                    normalizedName);
                var external = string.Equals(item.WorkKind, PipelineWorkKindExternalWork, StringComparison.Ordinal)
                               || string.Equals(item.ContractRole, PipelineContractRoleExternalAction, StringComparison.Ordinal)
                               || IsExternalWorkSpec(spec)
                               || ContainsExternalWorkIntent(intent)
                               || item.PlannedTools.Count > 0;
                var clauses = SplitCapabilityMentionClauses(intent);
                var exactCapabilityMentions = capabilities.Count(capability =>
                    !string.IsNullOrWhiteSpace(capability.Method)
                    && clauses.Any(clause => ContainsIntentToken(clause, capability.Method!)
                                             && IsPositiveCapabilityInvocationClause(clause, capability.Method!)));
                var selectorMatches = capabilities.Count(capability =>
                    capability.RequestBindings.Count > 0
                    && capability.RequestBindings.All(binding =>
                        ContainsPositiveLiteralSelectorAssignment(intent, binding)
                        || ContainsPositiveSelectorValueMention(
                            string.Join(' ', new[] { spec.Name, spec.Goal, spec.Description, spec.ConcreteOutcome }
                                .Where(static value => !string.IsNullOrWhiteSpace(value))!),
                            binding)));
                var lockedIdentityMentions = capabilities.Count(capability =>
                    ContainsLockedCapabilityIdentity(intent, capability.Id, capability.CatalogId));
                return new
                {
                    item.Name,
                    LockedIdentityMentions = lockedIdentityMentions,
                    SelectorMatches = selectorMatches,
                    ExactCapabilityMentions = exactCapabilityMentions,
                    NameIntentOverlap = nameOverlap,
                    NameActionOverlap = nameActionOverlap,
                    NameExtraneousActionFamilies = nameExtraneousActionFamilies,
                    ActionOverlap = actionOverlap,
                    ExtraneousActionFamilies = extraneousActionFamilies,
                    Score = nameOverlap * 100 + goalOverlap * 20 + overlap + (external ? 1 : 0),
                    External = external,
                    PlannedCount = item.PlannedTools.Count
                };
            })
            .Where(static candidate => candidate.External)
            .ToArray();
        var selectorCapabilityCount = capabilities.Count(static capability => capability.RequestBindings.Count > 0);
        var eligible = selectorCapabilityCount > 0
                       && candidates.Any(item => item.SelectorMatches == selectorCapabilityCount)
            ? candidates.Where(item => item.SelectorMatches == selectorCapabilityCount)
            : candidates.AsEnumerable();
        var hasSemanticOwnershipEvidence = selectorCapabilityCount > 0
                                           && candidates.Any(item => item.SelectorMatches == selectorCapabilityCount);
        if (!eligible.Any())
            return null;
        var maximumNameIntentOverlap = eligible.Max(static item => item.NameIntentOverlap);
        if (maximumNameIntentOverlap > 0)
        {
            eligible = eligible.Where(item => item.NameIntentOverlap == maximumNameIntentOverlap);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumNameActionOverlap = eligible.Max(static item => item.NameActionOverlap);
        if (maximumNameActionOverlap > 0)
        {
            eligible = eligible.Where(item => item.NameActionOverlap == maximumNameActionOverlap);
            var minimumNameExtraneousActionFamilies = eligible.Min(static item => item.NameExtraneousActionFamilies);
            eligible = eligible.Where(item => item.NameExtraneousActionFamilies == minimumNameExtraneousActionFamilies);
            hasSemanticOwnershipEvidence = true;
        }
        var maximumActionOverlap = eligible.Max(static item => item.ActionOverlap);
        if (maximumActionOverlap > 0)
        {
            eligible = eligible.Where(item => item.ActionOverlap == maximumActionOverlap);
            var minimumExtraneousActionFamilies = eligible.Min(static item => item.ExtraneousActionFamilies);
            eligible = eligible.Where(item => item.ExtraneousActionFamilies == minimumExtraneousActionFamilies);
            hasSemanticOwnershipEvidence = true;
        }
        if (!hasSemanticOwnershipEvidence && eligible.Any(static item => item.ExactCapabilityMentions > 0))
        {
            eligible = eligible.Where(static item => item.ExactCapabilityMentions > 0);
            hasSemanticOwnershipEvidence = true;
        }
        if (!hasSemanticOwnershipEvidence && eligible.Any(static item => item.LockedIdentityMentions > 0))
            eligible = eligible.Where(static item => item.LockedIdentityMentions > 0);
        return eligible
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.PlannedCount)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault()?.Name;
    }

    private static (WorkflowPipelineExtraction Extraction, ResolvedCapability[] Missing) BindPlannedCapabilityInvocations(
        WorkflowPipelineExtraction extraction,
        IReadOnlyList<ResolvedCapability> required)
    {
        var specs = extraction.Subworkflows.ToArray();
        var tools = specs.Select(static spec => spec.PlannedTools.ToArray()).ToArray();
        var available = new List<(int Spec, int Tool)>();
        for (var specIndex = 0; specIndex < tools.Length; specIndex++)
        for (var toolIndex = 0; toolIndex < tools[specIndex].Length; toolIndex++)
            if (tools[specIndex][toolIndex].Required)
                available.Add((specIndex, toolIndex));

        var remaining = required.ToList();
        // Preserve exact identities already locked by deterministic composition. Required
        // capabilities form a multiset, so consume one concrete occurrence at a time.
        for (var capabilityIndex = remaining.Count - 1; capabilityIndex >= 0; capabilityIndex--)
        {
            var capability = remaining[capabilityIndex];
            var availableIndex = available.FindIndex(position =>
            {
                var tool = tools[position.Spec][position.Tool];
                return PlannedToolMatchesCapability(tool, capability)
                       && PlannedToolCarriesCapabilityIdentity(tool, capability);
            });
            if (availableIndex < 0)
                continue;

            available.RemoveAt(availableIndex);
            remaining.RemoveAt(capabilityIndex);
        }

        var missing = new List<ResolvedCapability>();
        foreach (var capability in remaining)
        {
            var availableIndex = available.FindIndex(position =>
            {
                var tool = tools[position.Spec][position.Tool];
                return tool.OperationIds.Count == 0
                       && tool.CatalogIds.Count == 0
                       && PlannedToolMatchesCapability(tool, capability);
            });
            if (availableIndex < 0)
            {
                missing.Add(capability);
                continue;
            }

            var selected = available[availableIndex];
            available.RemoveAt(availableIndex);
            var tool = tools[selected.Spec][selected.Tool];
            tools[selected.Spec][selected.Tool] = tool with
            {
                OperationIds = string.IsNullOrWhiteSpace(capability.OperationId)
                    ? [capability.Id]
                    : [capability.OperationId],
                CatalogIds = string.IsNullOrWhiteSpace(capability.CatalogId)
                    ? Array.Empty<string>()
                    : [capability.CatalogId]
            };
        }

        var rebound = specs.Select((spec, index) => spec with { PlannedTools = tools[index] }).ToArray();
        return (extraction with { Subworkflows = rebound }, missing.ToArray());
    }

    private static bool PlannedToolCarriesCapabilityIdentity(
        PipelinePlannedTool tool,
        ResolvedCapability capability)
    {
        var operationId = string.IsNullOrWhiteSpace(capability.OperationId)
            ? capability.Id
            : capability.OperationId;
        if (!tool.OperationIds.Contains(operationId, StringComparer.Ordinal))
            return false;

        return string.IsNullOrWhiteSpace(capability.CatalogId)
               || tool.CatalogIds.Contains(capability.CatalogId, StringComparer.Ordinal);
    }

    private static IReadOnlyList<ResolvedCapability> FindMissingMcpCapabilityInvocations(
        IReadOnlyList<ResolvedCapability> required,
        IReadOnlyList<StepDef> calls)
    {
        var remaining = calls.ToList();
        var missing = new List<ResolvedCapability>();
        foreach (var capability in required)
        {
            var index = remaining.FindIndex(step => McpStepMatchesCapability(
                step, capability.Server!, capability.Kind!, capability.Method!, capability.RequestBindings));
            if (index < 0)
                missing.Add(capability);
            else
                remaining.RemoveAt(index);
        }
        return missing;
    }

    private static IReadOnlyList<ResolvedCapability> FindMissingNativeCapabilityInvocations(
        IReadOnlyList<ResolvedCapability> required,
        IReadOnlyList<StepDef> steps)
    {
        var remaining = steps.ToList();
        var missing = new List<ResolvedCapability>();
        foreach (var capability in required)
        {
            var index = remaining.FindIndex(step => string.Equals(step.Type, capability.Method, StringComparison.Ordinal));
            if (index < 0)
                missing.Add(capability);
            else
                remaining.RemoveAt(index);
        }
        return missing;
    }

    private static bool McpStepMatchesCapability(
        StepDef step,
        string server,
        string kind,
        string method,
        IReadOnlyList<CapabilityRequestBinding> bindings)
    {
        if (!string.Equals(ReadMcpCallInputString(step, "server"), server, StringComparison.Ordinal)
            || !string.Equals(ReadMcpCallInputString(step, "kind") ?? "tool", kind, StringComparison.Ordinal))
            return false;
        var methodMatches = string.Equals(ReadMcpCallInputString(step, "method"), method, StringComparison.Ordinal)
                            || step.Input?["methods"] is JsonArray methods
                            && methods.Any(node => node is JsonValue value
                                                   && value.TryGetValue<string>(out var candidate)
                                                   && string.Equals(candidate, method, StringComparison.Ordinal));
        return methodMatches && RequestContainsLiteralBindings(step.Input?["request"], bindings);
    }

    private static bool RequestBindingsEqual(
        IReadOnlyList<CapabilityRequestBinding> left,
        IReadOnlyList<CapabilityRequestBinding> right)
        => string.Equals(CanonicalizeBindings(left), CanonicalizeBindings(right), StringComparison.Ordinal);
}
