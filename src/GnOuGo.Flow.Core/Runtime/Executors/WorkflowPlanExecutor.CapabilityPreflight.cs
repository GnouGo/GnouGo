using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const string PlatformExternalWriteConfirmationOperationDescription = "Require explicit human confirmation immediately before the first external write.";
    private const string PlatformExternalWriteConfirmationConstraintDescription = "No external write may execute before explicit human confirmation.";
    private const string SynthesizedEffectDecisionValue = "EFFECT";
    private const string SynthesizedNoEffectDecisionValue = "NO_EFFECT";
    private const string ConditionalExactlyOneActivationMode = "exactly_one";
    private const string ConditionalAllOnValueActivationMode = "all_on_value";
    private const string CapabilityContractCoverageEnforcementKind = "capability_contract";
    private const string WorkflowStructureCoverageEnforcementKind = "workflow_structure";
    private const string IntrinsicPrimitiveMissingCoverageClassification = "intrinsic_primitive_missing";
    private const string WorkflowStructureOnlyCoverageClassification = "workflow_structure_only";
    private const string CapabilityDecisionContractSource = "capability_output";
    private const string StructuredDecisionContractSource = "structured_output";
    private const string LocalDecisionContractSource = "local_decision";
    private const string LocalDecisionStepType = "decision.evaluate";
    private const string CapabilityRelaxationPreserveAnswer = "Preserve the original requirement and stop";
    private const string CapabilityRelaxationAcceptAnswer = "Accept the supported weaker behavior";
    private const string ConditionalWriteRelaxationPreserveAnswer = "Preserve the requested write behavior and stop";
    private const string ConditionalWriteRelaxationReadOnlyAnswer = "Continue read-only without the unresolved external writes";
    private const int CapabilityInventoryRepairCandidateMaxCharacters = 128_000;
    private const int CapabilityArtifactClosureMaxCatalogIds = 32;
    private static readonly string[] CapabilityCoverageStructuralFacets =
    [
        "cardinality",
        "uniqueness",
        "scope_iteration",
        "ordering",
        "condition",
        "confirmation",
        "finalization",
        "failure_cancellation",
        "quality_threshold",
        "runtime_argument",
        "input_representation",
        "local_mapping"
    ];

    private sealed record CapabilityClarificationConfig(bool Enabled, int TimeoutMs);
    private sealed record CapabilityClarificationQuestion(string Name, string Description);

    private sealed class CapabilityInventoryContractException(
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
        : InvalidOperationException("Capability inventory evidence violated its deterministic contract.")
    {
        public IReadOnlyList<CapabilityInventoryContractIssue> Issues { get; } = issues;
    }

    private sealed record CapabilityRequestBinding(string Path, JsonNode? Value);
    private sealed record CapabilityArtifactRequirement(CapabilitySchemaField Field, string Kind);
    private sealed record SharedWriteOccurrence(string OperationId, string CatalogId, bool IsOwnedSource);
    private sealed record ArtifactMaterializerOccurrence(
        string OperationId,
        bool IsOwnedSource,
        IReadOnlySet<string> RequiredArtifactKinds);
    private sealed record ArtifactClosureSearchResult(
        IReadOnlyList<IReadOnlyList<string>> Solutions,
        IReadOnlyList<string> CandidateCatalogIds,
        bool SawCycle,
        bool HitLimit);
    private sealed record ConditionalDecisionGrounding(
        string OperationId,
        string CatalogId,
        string OutputPath,
        IReadOnlyList<string> AllowedValues,
        IReadOnlyList<string> NoEffectValues,
        string ContractSource);

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
        IReadOnlyList<string>? OperationIds = null)
    {
        public IReadOnlyList<string> InputOperationIds { get; init; } = Array.Empty<string>();
    }

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
        public string EffectiveExternalWriteConfirmationPolicy { get; init; } = "unspecified";
        public string ExternalWriteConfirmationPolicySource { get; init; } = "none";

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

                var evidenceSources = BuildCapabilityEvidenceSources(
                    instruction,
                    generatorContext,
                    intentClarification);

                (resolved, constraints) = await InferCapabilitiesAsync(
                    ctx,
                    input,
                    generator,
                    instruction,
                    generatorContext,
                    evidenceSources,
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

            var (confirmationPolicy, confirmationPolicySource) = ResolveEffectiveExternalWriteConfirmationPolicy(
                resolved,
                intentClarification,
                mode);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.external_write_confirmation_policy", confirmationPolicy);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.external_write_confirmation_policy_source", confirmationPolicySource);
            return new CapabilityPreflightResult(mode, discovered, resolved, constraints)
            {
                EffectiveExternalWriteConfirmationPolicy = confirmationPolicy,
                ExternalWriteConfirmationPolicySource = confirmationPolicySource
            };
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
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources,
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
            var inventorySchema = BuildCapabilityInventorySchema();
            var inventoryResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityInventoryPromptWithEvidence(evidenceSources),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = inventorySchema,
                StructuredOutputStrict = true
            }, "workflow.plan.capability_inventory", ct);
            RecordPlannerStructuredOutputProof(ctx, provider, model, inventoryResponse.Json, inventorySchema);
            AddUsageAttributes(inferenceSpan, inventoryResponse.Usage, model, provider);
            inferencePhase = "capability_inventory_parse";
            CapabilityInventory inventory;
            JsonObject? rejectedInventoryCandidate = null;
            IReadOnlyList<CapabilityInventoryContractIssue> initialContractIssues = Array.Empty<CapabilityInventoryContractIssue>();
            IReadOnlyList<CapabilityInventoryContractIssue> finalContractIssues = Array.Empty<CapabilityInventoryContractIssue>();
            try
            {
                rejectedInventoryCandidate = ParseStructuredObject(inventoryResponse, "operation inventory");
                inventory = RemovePlannerBoundaryArtifacts(
                    ParseCapabilityInventory(
                        rejectedInventoryCandidate,
                        evidenceSources),
                    evidenceSources);
                rejectedInventoryCandidate = null;
                RecordCapabilityInventoryContractTelemetry(
                    inferenceSpan,
                    "initial",
                    Array.Empty<CapabilityInventoryContractIssue>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                initialContractIssues = GetCapabilityInventoryContractIssues(ex);
                finalContractIssues = initialContractIssues;
                RecordCapabilityInventoryContractTelemetry(inferenceSpan, "initial", initialContractIssues);
                inventory = BuildInvalidCapabilityInventory(initialContractIssues);
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
                    Prompt = BuildCapabilityInventoryRepairPrompt(
                        evidenceSources,
                        inventory,
                        rejectedInventoryCandidate,
                        initialContractIssues),
                    Reasoning = reasoning,
                    UseBackgroundMode = true,
                    StructuredOutputSchema = inventorySchema.DeepClone(),
                    StructuredOutputStrict = true
                }, "workflow.plan.capability_inventory_repair", ct);
                RecordPlannerStructuredOutputProof(ctx, provider, model, repairedInventoryResponse.Json, inventorySchema);
                AddUsageAttributes(inferenceSpan, repairedInventoryResponse.Usage, model, provider);
                inferencePhase = "capability_inventory_repair_parse";
                try
                {
                    var repairedInventoryCandidate = ParseStructuredObject(
                        repairedInventoryResponse,
                        "operation inventory repair");
                    inventory = RemovePlannerBoundaryArtifacts(
                        ParseCapabilityInventory(
                            repairedInventoryCandidate,
                            evidenceSources),
                        evidenceSources);
                    finalContractIssues = Array.Empty<CapabilityInventoryContractIssue>();
                    RecordCapabilityInventoryContractTelemetry(
                        inferenceSpan,
                        "repair",
                        finalContractIssues);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    finalContractIssues = GetCapabilityInventoryContractIssues(ex);
                    RecordCapabilityInventoryContractTelemetry(inferenceSpan, "repair", finalContractIssues);
                    inventory = BuildInvalidCapabilityInventory(finalContractIssues);
                }
                if (!inventory.Complete)
                {
                    if (finalContractIssues.Count > 0)
                    {
                        ThrowInvalidCapabilityInventoryContract(
                            initialContractIssues,
                            finalContractIssues,
                            inventory);
                    }

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
                            AppendCapabilityClarificationEvidenceSources(evidenceSources, clarification),
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

            inventory = ApplyIntentClarificationExternalWriteConfirmationPolicy(
                inventory,
                evidenceSources,
                intentClarification);
            var evidenceAdjudicatedInventory = inventory;
            var (effectiveConfirmationPolicy, effectiveConfirmationPolicySource) =
                ResolveEffectiveExternalWriteConfirmationPolicy(inventory, evidenceSources);
            inferenceSpan.SetAttribute(
                "gnougo-flow.plan.capability_inventory.external_write_confirmation_policy",
                effectiveConfirmationPolicy);
            inferenceSpan.SetAttribute(
                "gnougo-flow.plan.capability_inventory.external_write_confirmation_policy_source",
                effectiveConfirmationPolicySource);
            inventory = ApplyDefaultExternalWriteConfirmation(inventory);

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
            var matchingSchema = BuildCapabilityMatchingSchema();
            var matchingResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityMatchingPrompt(inventory, catalog),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = matchingSchema,
                StructuredOutputStrict = true
            }, "workflow.plan.capability_matching", ct);
            RecordPlannerStructuredOutputProof(ctx, provider, model, matchingResponse.Json, matchingSchema);
            AddUsageAttributes(inferenceSpan, matchingResponse.Usage, model, provider);
            inferencePhase = "capability_matching_parse";
            CapabilityMatchingEvaluation evaluation;
            try
            {
                evaluation = ParseCapabilityMatchingEvaluation(
                    ParseStructuredObject(matchingResponse, "capability matching"), inventory, catalog);
                evaluation = NormalizeLocalProcessingMatches(evaluation);
                evaluation = NormalizeCapabilityCompositionMatches(evaluation, catalog);
                evaluation = NormalizeConditionalSelectorMatches(evaluation, catalog, inventory);
                evaluation = EnforceCapabilityPrerequisiteClosure(evaluation, catalog);
                evaluation = NormalizePlatformSafetyMatches(evaluation, catalog);
                RecordCapabilityMatchingNormalizationTelemetry(inferenceSpan, evaluation, "initial");
                RecordConditionalGroundingTelemetry(inferenceSpan.Span, evaluation, "initial");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                evaluation = BuildMalformedCapabilityMatchingEvaluation(inventory, ex.Message);
            }
            var repairRequired = RequiresCapabilityMatchingRepair(evaluation);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_matching.repair_attempted", repairRequired);
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_matching.upstream_rewind_attempted", false);
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
                    StructuredOutputSchema = matchingSchema.DeepClone(),
                    StructuredOutputStrict = true
                }, "workflow.plan.capability_matching_repair", ct);
                RecordPlannerStructuredOutputProof(ctx, provider, model, repairedMatchingResponse.Json, matchingSchema);
                AddUsageAttributes(inferenceSpan, repairedMatchingResponse.Usage, model, provider);
                inferencePhase = "capability_matching_repair_parse";
                CapabilityMatchingEvaluation repaired;
                try
                {
                    repaired = ParseCapabilityMatchingEvaluation(
                        ParseStructuredObject(repairedMatchingResponse, "capability matching repair"), inventory, catalog);
                    repaired = NormalizeLocalProcessingMatches(repaired);
                    repaired = NormalizeCapabilityCompositionMatches(repaired, catalog);
                    repaired = NormalizeConditionalSelectorMatches(repaired, catalog, inventory);
                    repaired = EnforceCapabilityPrerequisiteClosure(repaired, catalog);
                    repaired = NormalizePlatformSafetyMatches(repaired, catalog);
                    RecordCapabilityMatchingNormalizationTelemetry(inferenceSpan, repaired, "repair");
                    RecordConditionalGroundingTelemetry(inferenceSpan.Span, repaired, "repair");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    repaired = BuildMalformedCapabilityMatchingEvaluation(inventory, ex.Message);
                }
                evaluation = PreserveValidCapabilityMatches(evaluation, repaired);
            }

            if (HasRequiredCapabilityMatchingBlocker(evaluation)
                && IsCapabilityDiscoveryNarrowed(matchingDiscovery, discovered))
            {
                inferenceSpan.SetAttribute("gnougo-flow.plan.capability_matching.upstream_rewind_attempted", true);
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                        "A required capability-matching blocker remained after narrowed-catalog matching; re-adjudicating once against the complete discovered catalog."),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });

                inferencePhase = "capability_matching_upstream_rewind";
                var expandedCatalog = BuildSchemaAwareCapabilityCatalog(discovered, allowedNativeTypes, discovered);
                var remappedEvaluation = RemapCapabilityMatchingCatalogIds(evaluation, catalog, expandedCatalog);
                var initialBlockers = BuildCapabilityMatchingBlockerIdentities(remappedEvaluation);
                var initialFingerprint = BuildCapabilityMatchingFingerprint(remappedEvaluation);
                var rewindResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
                {
                    Provider = provider,
                    Model = model,
                    Prompt = BuildCapabilityMatchingRepairPrompt(inventory, expandedCatalog, remappedEvaluation),
                    Reasoning = reasoning,
                    UseBackgroundMode = true,
                    StructuredOutputSchema = matchingSchema.DeepClone(),
                    StructuredOutputStrict = true
                }, "workflow.plan.capability_matching_rewind", ct);
                RecordPlannerStructuredOutputProof(ctx, provider, model, rewindResponse.Json, matchingSchema);
                AddUsageAttributes(inferenceSpan, rewindResponse.Usage, model, provider);

                CapabilityMatchingEvaluation rewound;
                try
                {
                    rewound = ParseCapabilityMatchingEvaluation(
                        ParseStructuredObject(rewindResponse, "expanded-catalog capability matching rewind"),
                        inventory,
                        expandedCatalog);
                    rewound = NormalizeLocalProcessingMatches(rewound);
                    rewound = NormalizeCapabilityCompositionMatches(rewound, expandedCatalog);
                    rewound = NormalizeConditionalSelectorMatches(rewound, expandedCatalog, inventory);
                    rewound = EnforceCapabilityPrerequisiteClosure(rewound, expandedCatalog);
                    rewound = NormalizePlatformSafetyMatches(rewound, expandedCatalog);
                    rewound = PreserveValidCapabilityMatches(remappedEvaluation, rewound);
                    RecordCapabilityMatchingNormalizationTelemetry(inferenceSpan, rewound, "upstream_rewind");
                    RecordConditionalGroundingTelemetry(inferenceSpan.Span, rewound, "upstream_rewind");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rewound = BuildMalformedCapabilityMatchingEvaluation(inventory, ex.Message);
                }

                var remainingBlockers = BuildCapabilityMatchingBlockerIdentities(rewound);
                var rewindFingerprint = BuildCapabilityMatchingFingerprint(rewound);
                var blockersStrictlyReduced = remainingBlockers.Count < initialBlockers.Count
                                              && remainingBlockers.IsSubsetOf(initialBlockers);
                var fingerprintChanged = !string.Equals(
                    rewindFingerprint,
                    initialFingerprint,
                    StringComparison.Ordinal);
                var matchingRewindAccepted = rewound.ContractValid
                                             && fingerprintChanged
                                             && blockersStrictlyReduced;
                var inventoryRewindAccepted = false;
                if (!matchingRewindAccepted
                    && TryGetInventoryRewindConstraintIds(
                        evidenceAdjudicatedInventory,
                        remappedEvaluation,
                        out var challengedConstraintIds))
                {
                    inferencePhase = "capability_inventory_upstream_rewind";
                    inferenceSpan.SetAttribute(
                        "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_attempted",
                        true);
                    var inventoryRewindResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
                    {
                        Provider = provider,
                        Model = model,
                        Prompt = BuildCapabilityInventoryMatchingRewindPrompt(
                            evidenceSources,
                            evidenceAdjudicatedInventory,
                            remappedEvaluation,
                            challengedConstraintIds),
                        Reasoning = reasoning,
                        UseBackgroundMode = true,
                        StructuredOutputSchema = inventorySchema.DeepClone(),
                        StructuredOutputStrict = true
                    }, "workflow.plan.capability_inventory_rewind", ct);
                    RecordPlannerStructuredOutputProof(
                        ctx,
                        provider,
                        model,
                        inventoryRewindResponse.Json,
                        inventorySchema);
                    AddUsageAttributes(inferenceSpan, inventoryRewindResponse.Usage, model, provider);

                    try
                    {
                        var candidateEvidenceInventory = RemovePlannerBoundaryArtifacts(
                            ParseCapabilityInventory(
                                ParseStructuredObject(
                                    inventoryRewindResponse,
                                    "capability inventory upstream rewind"),
                                evidenceSources),
                            evidenceSources);
                        var inventoryChanged = !string.Equals(
                            BuildCapabilityInventoryFingerprint(candidateEvidenceInventory),
                            BuildCapabilityInventoryFingerprint(evidenceAdjudicatedInventory),
                            StringComparison.Ordinal);
                        var inventoryContractValid = InventoryRewindPreservesStableContracts(
                            evidenceAdjudicatedInventory,
                            candidateEvidenceInventory,
                            challengedConstraintIds);
                        inferenceSpan.SetAttribute(
                            "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_fingerprint_changed",
                            inventoryChanged);
                        inferenceSpan.SetAttribute(
                            "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_contract_valid",
                            inventoryContractValid);

                        if (inventoryChanged && inventoryContractValid)
                        {
                            var candidateInventory = ApplyDefaultExternalWriteConfirmation(
                                candidateEvidenceInventory);
                            inferencePhase = "capability_matching_after_inventory_rewind";
                            var inventoryMatchingResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
                            {
                                Provider = provider,
                                Model = model,
                                Prompt = BuildCapabilityMatchingPrompt(candidateInventory, expandedCatalog),
                                Reasoning = reasoning,
                                UseBackgroundMode = true,
                                StructuredOutputSchema = matchingSchema.DeepClone(),
                                StructuredOutputStrict = true
                            }, "workflow.plan.capability_matching_after_inventory_rewind", ct);
                            RecordPlannerStructuredOutputProof(
                                ctx,
                                provider,
                                model,
                                inventoryMatchingResponse.Json,
                                matchingSchema);
                            AddUsageAttributes(inferenceSpan, inventoryMatchingResponse.Usage, model, provider);

                            var inventoryRewound = ParseCapabilityMatchingEvaluation(
                                ParseStructuredObject(
                                    inventoryMatchingResponse,
                                    "capability matching after inventory rewind"),
                                candidateInventory,
                                expandedCatalog);
                            inventoryRewound = NormalizeLocalProcessingMatches(inventoryRewound);
                            inventoryRewound = NormalizeCapabilityCompositionMatches(
                                inventoryRewound,
                                expandedCatalog);
                            inventoryRewound = NormalizeConditionalSelectorMatches(
                                inventoryRewound,
                                expandedCatalog,
                                candidateInventory);
                            inventoryRewound = EnforceCapabilityPrerequisiteClosure(
                                inventoryRewound,
                                expandedCatalog);
                            inventoryRewound = NormalizePlatformSafetyMatches(
                                inventoryRewound,
                                expandedCatalog);
                            inventoryRewound = PreserveValidCapabilityMatches(
                                remappedEvaluation,
                                inventoryRewound);

                            var inventoryRemainingBlockers = BuildCapabilityMatchingBlockerIdentities(
                                inventoryRewound);
                            var inventoryMatchingFingerprint = BuildCapabilityMatchingFingerprint(
                                inventoryRewound);
                            var inventoryBlockersStrictlyReduced = inventoryRemainingBlockers.Count
                                                                   < initialBlockers.Count
                                                               && inventoryRemainingBlockers.IsSubsetOf(
                                                                   initialBlockers);
                            var inventoryMatchingChanged = !string.Equals(
                                inventoryMatchingFingerprint,
                                initialFingerprint,
                                StringComparison.Ordinal);
                            inventoryRewindAccepted = inventoryRewound.ContractValid
                                                      && inventoryMatchingChanged
                                                      && inventoryBlockersStrictlyReduced;
                            inferenceSpan.SetAttribute(
                                "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_remaining_blocker_count",
                                inventoryRemainingBlockers.Count);
                            inferenceSpan.SetAttribute(
                                "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_accepted",
                                inventoryRewindAccepted);
                            if (inventoryRewindAccepted)
                            {
                                evidenceAdjudicatedInventory = candidateEvidenceInventory;
                                inventory = candidateInventory;
                                rewound = inventoryRewound;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        inferenceSpan.SetAttribute(
                            "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_contract_valid",
                            false);
                        inferenceSpan.SetAttribute(
                            "gnougo-flow.plan.capability_matching.upstream_rewind.inventory_accepted",
                            false);
                    }
                }
                inferenceSpan.SetAttribute(
                    "gnougo-flow.plan.capability_matching.upstream_rewind.initial_blocker_count",
                    initialBlockers.Count);
                inferenceSpan.SetAttribute(
                    "gnougo-flow.plan.capability_matching.upstream_rewind.remaining_blocker_count",
                    remainingBlockers.Count);
                inferenceSpan.SetAttribute(
                    "gnougo-flow.plan.capability_matching.upstream_rewind.fingerprint_changed",
                    fingerprintChanged);
                inferenceSpan.SetAttribute(
                    "gnougo-flow.plan.capability_matching.upstream_rewind.accepted",
                    matchingRewindAccepted || inventoryRewindAccepted);

                evaluation = matchingRewindAccepted || inventoryRewindAccepted
                    ? rewound
                    : rewound.ContractValid
                        ? remappedEvaluation
                        : MarkCapabilityMatchingRewindNonImproving(rewound, remappedEvaluation);
                catalog = expandedCatalog;
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
                    AppendCapabilityClarificationEvidenceSources(evidenceSources, clarification),
                    discovered,
                    parentSpan,
                    intentClarification,
                    ct,
                    clarificationAllowed: false);
            }

            if (clarificationAllowed
                && intentClarification is not null
                && IsConditionalWriteRelaxationEligible(evaluation))
            {
                await RequestConditionalWriteRelaxationAsync(
                    ctx,
                    intentClarification,
                    evaluation,
                    ct);
            }

            RecordCapabilityMatchingFailureTelemetry(inferenceSpan, evaluation, repairRequired);
            ThrowForUnresolvedCapabilityMatches(evaluation, catalog, repairRequired, intentClarification);
            inferencePhase = "capability_coverage_review";
            evaluation = await ReviewCapabilityCoverageAndRematchAsync(
                ctx,
                input,
                llmClient,
                inventory,
                catalog,
                evaluation,
                provider,
                model,
                reasoning,
                inferenceSpan,
                intentClarification,
                ct);
            evaluation = CanonicalizeSharedStructuredDecisionOutputPaths(evaluation);
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
        CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var matchesByOperationId = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        var operationMatches = new List<CapabilityOperationMatch>(evaluation.OperationMatches.Count);
        var closureIssues = new List<CapabilityMatchingIssue>();
        var resolvedOperationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in evaluation.OperationMatches)
        {
            if (match.Status is not ("matched" or "composed" or "conditional"))
            {
                operationMatches.Add(match);
                continue;
            }

            var preferredProducerIds = GetDeclaredUpstreamOperationIds(match.Operation, matchesByOperationId)
                .SelectMany(operationId => matchesByOperationId.TryGetValue(operationId, out var upstream)
                    ? upstream.CatalogIds
                    : Array.Empty<string>())
                .Where(entries.ContainsKey)
                .ToHashSet(StringComparer.Ordinal);
            var search = SearchArtifactClosure(
                match.CatalogIds,
                catalog.Entries,
                entries,
                preferredProducerIds);
            var minimalSolutions = search.Solutions
                .GroupBy(static solution => solution.Count)
                .OrderBy(static group => group.Key)
                .FirstOrDefault()?
                .OrderBy(static solution => string.Join('\u001f', solution), StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<IReadOnlyList<string>>();
            if (minimalSolutions.Length == 1)
            {
                var selectedIds = minimalSolutions[0];
                var addedProducer = selectedIds.Count > match.CatalogIds.Distinct(StringComparer.Ordinal).Count();
                operationMatches.Add(addedProducer
                    ? match with
                    {
                        Status = match.Status == "conditional" ? "conditional" : selectedIds.Count == 1 ? "matched" : "composed",
                        Reason = "The selected capabilities form the unique minimal prerequisite-closed artifact composition.",
                        CatalogIds = selectedIds,
                        CandidateCatalogIds = Array.Empty<string>(),
                        NormalizationReasonCode = "artifact_closure_resolved"
                    }
                    : match);
                resolvedOperationIds.Add(match.Operation.Id);
                continue;
            }

            var selected = match.CatalogIds.Where(entries.ContainsKey).Select(id => entries[id]).ToArray();
            var missing = GetMissingArtifactRequirements(selected);
            var candidateIds = match.CatalogIds
                .Concat(search.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            var fields = string.Join(", ", missing.Select(static item => item.Field.Path));
            var status = minimalSolutions.Length > 1 ? "ambiguous" : "unavailable";
            var reasonCode = minimalSolutions.Length > 1
                ? "artifact_closure_multiple"
                : search.HitLimit
                    ? "artifact_closure_limit"
                    : search.SawCycle
                        ? "artifact_closure_cycle"
                        : "artifact_closure_unavailable";
            var reason = reasonCode switch
            {
                "artifact_closure_multiple" => $"The selected capability requires operational artifacts at {fields}, and more than one minimal prerequisite-closed producer composition remains valid.",
                "artifact_closure_limit" => $"The selected capability requires operational artifacts at {fields}, but deterministic prerequisite closure exceeded its bounded depth or catalog-ID limit.",
                "artifact_closure_cycle" => $"The selected capability requires operational artifacts at {fields}, but every discovered producer composition contains a dependency cycle.",
                _ => $"The selected capability requires operational artifacts at {fields}, but no discovered acyclic producer composition can supply them."
            };
            var repairedMatch = match with
            {
                Status = status,
                Reason = reason,
                CatalogIds = Array.Empty<string>(),
                CandidateCatalogIds = status == "ambiguous" ? candidateIds : Array.Empty<string>(),
                NormalizationReasonCode = reasonCode
            };
            operationMatches.Add(repairedMatch);
            closureIssues.Add(new CapabilityMatchingIssue(
                match.Operation.Id,
                match.Operation.Description,
                match.Operation.Required,
                status,
                reason,
                repairedMatch.CandidateCatalogIds)
            {
                ReasonCode = reasonCode
            });
        }

        var replacedOperationIds = closureIssues
            .Select(static issue => issue.OperationId)
            .Concat(resolvedOperationIds)
            .ToHashSet(StringComparer.Ordinal);
        var issues = evaluation.Issues
            .Where(issue => !replacedOperationIds.Contains(issue.OperationId))
            .Concat(closureIssues)
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

    private static ArtifactClosureSearchResult SearchArtifactClosure(
        IReadOnlyList<string> initialCatalogIds,
        IReadOnlyList<CapabilityCatalogEntry> catalog,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        IReadOnlySet<string> preferredProducerIds)
    {
        var solutions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var sawCycle = false;
        var hitLimit = false;

        void Explore(IReadOnlyList<string> selectedIds, int depth)
        {
            if (solutions.Count >= 16)
                return;
            var canonicalIds = selectedIds
                .Where(entries.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (canonicalIds.Length > CapabilityArtifactClosureMaxCatalogIds)
            {
                hitLimit = true;
                return;
            }

            var selected = canonicalIds.Select(id => entries[id]).ToArray();
            var missing = GetMissingArtifactRequirements(selected);
            if (missing.Length == 0)
            {
                if (HasArtifactDependencyCycle(selected))
                {
                    sawCycle = true;
                    return;
                }

                var key = string.Join('\u001f', canonicalIds);
                solutions.TryAdd(key, canonicalIds);
                return;
            }
            if (depth >= CapabilitySchemaMaxDepth)
            {
                hitLimit = true;
                return;
            }

            var requirement = missing
                .OrderBy(static item => item.Kind, StringComparer.Ordinal)
                .ThenBy(static item => item.Field.Path, StringComparer.Ordinal)
                .First();
            var producerCandidates = FindCanonicalArtifactProducerCandidates(
                requirement.Kind,
                catalog,
                preferredProducerIds);
            foreach (var producer in producerCandidates)
                candidates.Add(producer.Id);
            foreach (var producer in producerCandidates)
                Explore(canonicalIds.Append(producer.Id).ToArray(), depth + 1);
        }

        Explore(initialCatalogIds, 0);
        return new ArtifactClosureSearchResult(
            solutions.Values.ToArray(),
            candidates.Order(StringComparer.Ordinal).Take(8).ToArray(),
            sawCycle,
            hitLimit);
    }

    private static CapabilityArtifactRequirement[] GetMissingArtifactRequirements(
        IReadOnlyList<CapabilityCatalogEntry> selected)
        => selected
            .SelectMany(GetRequiredArtifactRequirements)
            .Where(item => !selected.Any(entry => CapabilityProducesArtifactKind(entry, item.Kind)))
            .GroupBy(static item => item.Kind, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

    private static IReadOnlyList<CapabilityCatalogEntry> FindCanonicalArtifactProducerCandidates(
        string artifactKind,
        IReadOnlyList<CapabilityCatalogEntry> catalog,
        IReadOnlySet<string> preferredProducerIds)
    {
        var candidates = catalog
            .Where(static entry => string.Equals(entry.Resolution, "mcp", StringComparison.Ordinal))
            .Where(entry => CapabilityProducesArtifactKind(entry, artifactKind))
            .GroupBy(static entry => (entry.Resolution, entry.Server, entry.Kind, entry.Method))
            .SelectMany(group =>
            {
                var preferred = group.Where(entry => preferredProducerIds.Contains(entry.Id)).ToArray();
                if (preferred.Length > 0)
                    return preferred;
                var wholeTool = group.Where(static entry => entry.RequestBindings.Count == 0).Take(1).ToArray();
                return wholeTool.Length == 1 ? wholeTool : group;
            })
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        var preferredCandidates = candidates.Where(entry => preferredProducerIds.Contains(entry.Id)).ToArray();
        return preferredCandidates.Length > 0 ? preferredCandidates : candidates;
    }

    private static bool HasArtifactDependencyCycle(IReadOnlyList<CapabilityCatalogEntry> selected)
    {
        var dependencies = selected.ToDictionary(
            static entry => entry.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var consumer in selected)
        {
            foreach (var requirement in GetRequiredArtifactRequirements(consumer))
            {
                foreach (var producer in selected.Where(entry => CapabilityProducesArtifactKind(entry, requirement.Kind)))
                    dependencies[consumer.Id].Add(producer.Id);
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (visited.Contains(id))
                return false;
            if (!visiting.Add(id))
                return true;
            foreach (var dependency in dependencies[id])
            {
                if (Visit(dependency))
                    return true;
            }
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }

        return dependencies.Keys.Any(Visit);
    }

    private static IReadOnlySet<string> GetDeclaredUpstreamOperationIds(
        CapabilityInventoryOperation operation,
        IReadOnlyDictionary<string, CapabilityOperationMatch> matches)
    {
        var upstream = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(operation.InputOperationIds);
        while (pending.TryPop(out var operationId))
        {
            if (!upstream.Add(operationId) || !matches.TryGetValue(operationId, out var match))
                continue;
            foreach (var inputOperationId in match.Operation.InputOperationIds)
                pending.Push(inputOperationId);
        }
        return upstream;
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

    private static IReadOnlyList<string> RemoveStructurallyRedundantSelectorAncestorEntries(
        IReadOnlyList<string> selected,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        out bool normalized)
    {
        normalized = false;
        var referenced = selected
            .Distinct(StringComparer.Ordinal)
            .Where(entries.ContainsKey)
            .Select(id => entries[id])
            .ToArray();
        var redundantAncestorIds = referenced
            .GroupBy(static entry => (entry.Resolution, entry.Server, entry.Kind, entry.Method))
            .SelectMany(static group => group
                .Where(entry => group.Any(candidate => IsStrictSelectorAncestor(entry, candidate)))
                .Select(static entry => entry.Id))
            .ToHashSet(StringComparer.Ordinal);
        if (redundantAncestorIds.Count == 0)
            return selected;

        var result = selected.Where(id => !redundantAncestorIds.Contains(id)).ToArray();
        normalized = result.Length != selected.Count;
        return result;
    }

    private static bool IsStrictSelectorAncestor(
        CapabilityCatalogEntry possibleAncestor,
        CapabilityCatalogEntry possibleDescendant)
    {
        if (possibleAncestor.RequestBindings.Count >= possibleDescendant.RequestBindings.Count)
            return false;

        var descendantBindings = possibleDescendant.RequestBindings.ToDictionary(
            static binding => binding.Path,
            static binding => binding.Value,
            StringComparer.Ordinal);
        return possibleAncestor.RequestBindings.All(binding =>
            descendantBindings.TryGetValue(binding.Path, out var descendantValue)
            && JsonNode.DeepEquals(binding.Value, descendantValue));
    }

    private static void RecordCapabilityMatchingNormalizationTelemetry(
        TelemetrySpanScope span,
        CapabilityMatchingEvaluation evaluation,
        string attempt)
    {
        var normalized = evaluation.OperationMatches
            .Where(static match => !string.IsNullOrWhiteSpace(match.NormalizationReasonCode)
                                   || !string.IsNullOrWhiteSpace(match.DecisionOutputPathNormalizationReasonCode))
            .ToArray();
        span.SetAttribute(
            $"gnougo-flow.plan.capability_matching.{attempt}.normalization_count",
            normalized.Sum(static match =>
                (string.IsNullOrWhiteSpace(match.NormalizationReasonCode) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(match.DecisionOutputPathNormalizationReasonCode) ? 0 : 1)));
        foreach (var match in normalized)
        {
            if (!string.IsNullOrWhiteSpace(match.NormalizationReasonCode))
            {
                AddCapabilityMatchingNormalizationTelemetryEvent(
                    span,
                    match,
                    attempt,
                    match.NormalizationReasonCode);
            }
            if (!string.IsNullOrWhiteSpace(match.DecisionOutputPathNormalizationReasonCode))
            {
                AddCapabilityMatchingNormalizationTelemetryEvent(
                    span,
                    match,
                    attempt,
                    match.DecisionOutputPathNormalizationReasonCode);
            }
        }
    }

    private static void AddCapabilityMatchingNormalizationTelemetryEvent(
        TelemetrySpanScope span,
        CapabilityOperationMatch match,
        string attempt,
        string reasonCode)
    {
        if (string.Equals(
                reasonCode,
                "conditional_local_decision_contract_synthesized",
                StringComparison.Ordinal))
        {
            span.AddEvent("gnougo-flow.plan.capability_matching.normalization", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", match.Operation.Id),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", reasonCode),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_operation_id", match.DecisionOperationId ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.contract_source", LocalDecisionContractSource)
            });
            return;
        }

        span.AddEvent("gnougo-flow.plan.capability_matching.normalization", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.attempt", attempt),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", match.Operation.Id),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", reasonCode),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.selected_count", match.CatalogIds.Count),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.selected_catalog_ids", string.Join(',', match.CatalogIds.Take(8))),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_operation_id", match.DecisionOperationId ?? string.Empty),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.producer_catalog_id", match.DecisionProducerCatalogId ?? string.Empty),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.contract_source", match.DecisionContractSource ?? string.Empty)
        });
    }

    private static void RecordCapabilityMatchingFailureTelemetry(
        TelemetrySpanScope span,
        CapabilityMatchingEvaluation evaluation,
        bool repairAttempted)
    {
        var blocking = evaluation.Issues.Where(static issue => issue.Required).ToArray();
        if (evaluation.ContractValid && blocking.Length == 0)
            return;
        span.SetAttribute("gnougo-flow.plan.capability_matching.repair_exhausted", repairAttempted);
        span.SetAttribute("gnougo-flow.plan.capability_matching.blocking_issue_count", blocking.Length);
        span.SetAttribute("gnougo-flow.plan.capability_matching.invalid_issue_count",
            blocking.Count(static issue => issue.Status == "invalid"));
        if (repairAttempted)
        {
            span.AddEvent("gnougo-flow.plan.capability_matching.failure", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", "model_repair_exhausted"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.repair_attempted", true),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.blocking_issue_count", blocking.Length)
            });
        }
        foreach (var issue in blocking.Where(static issue => issue.ReasonCode.Length > 0))
        {
            span.AddEvent("gnougo-flow.plan.capability_matching.failure", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", issue.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.status", issue.Status),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.reason_code", issue.ReasonCode),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.repair_attempted", repairAttempted)
            });
        }
    }

    private static CapabilityMatchingEvaluation NormalizeConditionalSelectorMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog,
        CapabilityInventory inventory)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var changedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedIssues = new List<CapabilityMatchingIssue>();
        var operationMatches = evaluation.OperationMatches.Select(match =>
        {
            if (match.Status is "matched" or "conditional" or "local")
                return match;
            if (match.Operation.DecisionSourceOperationId.Length == 0)
                return match;

            var referencedIds = match.CatalogIds.Concat(match.CandidateCatalogIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (referencedIds.Length == 1
                && entries.TryGetValue(referencedIds[0], out var soleEntry)
                && soleEntry.RequestBindings.Count > 0
                && !HasCompatibleConditionalSelectorSibling(
                    soleEntry,
                    entries.Values,
                    match.Operation.AllowNoEffectOutcome))
            {
                changedOperationIds.Add(match.Operation.Id);
                const string reason = "Only one exact selector capability remains, so the discovered catalog cannot implement the declared set of runtime alternatives.";
                normalizedIssues.Add(new CapabilityMatchingIssue(
                    match.Operation.Id,
                    match.Operation.Description,
                    match.Operation.Required,
                    "unavailable",
                    reason,
                    referencedIds));
                return match with
                {
                    Status = "unavailable",
                    Reason = reason,
                    CatalogIds = Array.Empty<string>(),
                    CandidateCatalogIds = Array.Empty<string>(),
                    DecisionOperationId = null,
                    ConditionalActivationMode = string.Empty,
                    NormalizationReasonCode = "conditional_selector_set_insufficient"
                };
            }
            if (referencedIds.Length < 2 || referencedIds.Any(id => !entries.ContainsKey(id)))
                return match;
            var canonicalReferencedIds = RemoveStructurallyRedundantSelectorAncestorEntries(
                referencedIds,
                entries,
                out var selectorAncestorRemoved);
            var canonicalReferenced = canonicalReferencedIds.Select(id => entries[id]).ToArray();
            if (canonicalReferenced.Length < 2)
            {
                if (!selectorAncestorRemoved)
                    return match;

                var soleCanonicalEntry = canonicalReferenced.Single();
                if (HasCompatibleConditionalSelectorSibling(
                        soleCanonicalEntry,
                        entries.Values,
                        match.Operation.AllowNoEffectOutcome))
                    return match;

                changedOperationIds.Add(match.Operation.Id);
                const string reason = "The referenced selector entries collapse to one logical capability, so they cannot implement the declared set of runtime alternatives.";
                normalizedIssues.Add(new CapabilityMatchingIssue(
                    match.Operation.Id,
                    match.Operation.Description,
                    match.Operation.Required,
                    "unavailable",
                    reason,
                    canonicalReferencedIds.Take(8).ToArray()));
                return match with
                {
                    Status = "unavailable",
                    Reason = reason,
                    CatalogIds = Array.Empty<string>(),
                    CandidateCatalogIds = Array.Empty<string>(),
                    DecisionOperationId = null,
                    ConditionalActivationMode = string.Empty,
                    NormalizationReasonCode = "selector_ancestor_chain_insufficient"
                };
            }
            if (!TryBuildConditionalActivation(
                    canonicalReferenced,
                    match.Operation.AllowNoEffectOutcome,
                    match.ConditionalActivationMode,
                    out _,
                    out var conditionalActivationMode))
                return match;

            var decisionOperationId = match.Operation.DecisionSourceOperationId;
            if (inventory.Operations.All(operation => !string.Equals(
                    operation.Id,
                    decisionOperationId,
                    StringComparison.Ordinal)))
                return match;

            var conditionalCandidate = match with
            {
                Status = "conditional",
                CatalogIds = canonicalReferenced.Select(static entry => entry.Id).ToArray(),
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = decisionOperationId,
                ConditionalActivationMode = conditionalActivationMode
            };
            if (!TryGroundConditionalDecision(
                    evaluation,
                    conditionalCandidate,
                    entries,
                    out var decisionOutputPath,
                    out var decisionAllowedValues,
                    out var decisionNoEffectValues,
                    out var decisionContractSource,
                    out var decisionProducerCatalogId,
                    out var decisionProducerOperationId,
                    out var failureCode))
            {
                // Multiple read selectors are safe to keep as an unconditional composition
                // when no declared runtime discriminator proves that they are alternatives.
                if (string.Equals(match.Operation.ExternalEffectKind, "read", StringComparison.Ordinal))
                    return match;

                changedOperationIds.Add(match.Operation.Id);
                const string reason = "Conditional activation has no provider-neutral decision contract that covers every effect branch and declared no-effect outcome.";
                normalizedIssues.Add(new CapabilityMatchingIssue(
                    match.Operation.Id,
                    match.Operation.Description,
                    match.Operation.Required,
                    "contract_gap",
                    reason,
                    canonicalReferenced.Select(static entry => entry.Id).Take(8).ToArray())
                {
                    ReasonCode = failureCode
                });
                return match with
                {
                    Status = "invalid",
                    Reason = reason,
                    DecisionOperationId = decisionProducerOperationId,
                    DecisionGroundingFailureCode = failureCode
                };
            }

            changedOperationIds.Add(match.Operation.Id);
            return match with
            {
                Status = "conditional",
                Reason = "The exact selector subset contains mutually exclusive runtime branches selected by an earlier workflow result; complementary selected capabilities remain unconditional prerequisites.",
                CatalogIds = canonicalReferenced.Select(static entry => entry.Id).ToArray(),
                CandidateCatalogIds = Array.Empty<string>(),
                DecisionOperationId = decisionProducerOperationId,
                DecisionOutputPath = decisionOutputPath,
                DecisionAllowedValues = decisionAllowedValues,
                DecisionNoEffectValues = decisionNoEffectValues,
                DecisionContractSource = decisionContractSource,
                DecisionProducerCatalogId = decisionProducerCatalogId,
                DecisionGroundingFailureCode = null,
                ConditionalActivationMode = conditionalActivationMode,
                NormalizationReasonCode = string.Equals(
                    decisionProducerOperationId,
                    decisionOperationId,
                    StringComparison.Ordinal)
                    ? string.Equals(
                        conditionalActivationMode,
                        ConditionalAllOnValueActivationMode,
                        StringComparison.Ordinal)
                        ? "conditional_composition_canonicalized"
                        : selectorAncestorRemoved || match.CandidateCatalogIds.Count > 0
                            ? "conditional_selector_family_canonicalized"
                            : match.NormalizationReasonCode
                    : "conditional_decision_source_canonicalized"
            };
        }).ToArray();

        if (changedOperationIds.Count == 0)
            return evaluation;

        var issues = evaluation.Issues
            .Where(issue => !changedOperationIds.Contains(issue.OperationId))
            .Concat(normalizedIssues)
            .ToArray();
        var contractValid = operationMatches.All(static match => match.Status != "invalid")
                            && evaluation.ConstraintMatches.All(static match => match.Status != "invalid")
                            && issues.All(static issue => issue.Status is not ("invalid" or "contract_gap"));
        return CanonicalizeSharedStructuredDecisionOutputPaths(evaluation with
        {
            OperationMatches = operationMatches,
            Issues = issues,
            ContractValid = contractValid
        });
    }

    private static bool HasCompatibleConditionalSelectorSibling(
        CapabilityCatalogEntry entry,
        IEnumerable<CapabilityCatalogEntry> candidates,
        bool allowNoEffectOutcome)
        => candidates.Any(candidate =>
            !string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal)
            && string.Equals(candidate.Resolution, entry.Resolution, StringComparison.Ordinal)
            && string.Equals(candidate.Server, entry.Server, StringComparison.Ordinal)
            && string.Equals(candidate.Kind, entry.Kind, StringComparison.Ordinal)
            && string.Equals(candidate.Method, entry.Method, StringComparison.Ordinal)
            && candidate.RequestBindings.Count > 0
            && TryBuildConditionalActivation(
                [entry, candidate],
                allowNoEffectOutcome,
                string.Empty,
                out _,
                out _));

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

    private static IReadOnlyList<CapabilityEvidenceSource> BuildCapabilityEvidenceSources(
        string instruction,
        string context,
        IntentClarificationSession? session)
    {
        var sources = new List<CapabilityEvidenceSource>();
        var rawRequest = session?.RawRequest ?? instruction;
        var callerContext = session?.CallerContext ?? context;
        if (!string.IsNullOrWhiteSpace(rawRequest))
            sources.Add(new CapabilityEvidenceSource("user_request", "user_request", rawRequest));
        if (!string.IsNullOrWhiteSpace(callerContext))
            sources.Add(new CapabilityEvidenceSource("caller_context", "caller_context", callerContext));

        if (session is not null)
        {
            for (var index = 0; index < session.Answers.Count; index++)
            {
                var answer = session.Answers[index];
                sources.Add(new CapabilityEvidenceSource(
                    $"clarification_{index + 1:D4}",
                    "clarification",
                    BuildIntentClarificationEvidenceText(answer)));
            }
        }

        return sources;
    }

    private static string BuildIntentClarificationEvidenceText(IntentClarificationAnswer answer)
    {
        var lines = new List<string>
        {
            $"Question: {answer.Question}",
            $"Answer: {answer.Answer}"
        };
        if (answer.SelectedDescription.Length > 0)
            lines.Add($"Selected option impact: {answer.SelectedDescription}");
        if (!string.Equals(answer.ExternalWriteConfirmationPolicy, "unchanged", StringComparison.Ordinal))
            lines.Add($"External write confirmation policy: {answer.ExternalWriteConfirmationPolicy}.");
        if (answer.IsCustom)
            lines.Add("Answer source: custom user response.");
        return string.Join('\n', lines);
    }

    private static IReadOnlyList<CapabilityEvidenceSource> AppendCapabilityClarificationEvidenceSources(
        IReadOnlyList<CapabilityEvidenceSource> current,
        JsonObject clarification)
    {
        var sources = current.ToList();
        var sequence = sources.Count(static source => string.Equals(
            source.Kind,
            "capability_clarification",
            StringComparison.Ordinal));
        foreach (var property in clarification.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonValue value
                || !value.TryGetValue<string>(out var answer)
                || string.IsNullOrWhiteSpace(answer))
            {
                continue;
            }

            sequence++;
            sources.Add(new CapabilityEvidenceSource(
                $"capability_clarification_{sequence:D4}",
                "capability_clarification",
                $"{property.Key}: {answer}"));
        }
        return sources;
    }

    private static string BuildCapabilityEvidenceCorpus(IReadOnlyList<CapabilityEvidenceSource> sources)
        => string.Join('\n', sources.Select(static source => source.Text));

    private static string BuildCapabilityEvidenceSourcesJson(IReadOnlyList<CapabilityEvidenceSource> sources)
        => new JsonArray(sources.Select(static source => (JsonNode)new JsonObject
        {
            ["source_id"] = source.Id,
            ["source_kind"] = source.Kind,
            ["text"] = source.Text
        }).ToArray()).ToJsonString();

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
                        $"Required MCP-declared artifact of kind {artifact.Kind}.",
                        Array.Empty<string>()),
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

    private static string BuildCapabilityInventoryPrompt(string instruction, string context)
        => BuildCapabilityInventoryPromptWithEvidence(
            BuildCapabilityEvidenceSources(instruction, context, session: null));

    private static string BuildCapabilityInventoryPromptWithEvidence(
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources) => $$"""
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

        Every evidence field is an object with source_id and excerpt. Use empty source_id and excerpt values when a singular evidence field is not applicable. For every external_effect or human_interaction operation, set coverage_requirements to one or more distinct evidence objects whose excerpt is copied from exactly one supplied evidence source. Classify each coverage requirement with enforcement_kind=capability_contract only when the selected capability card itself must document the intrinsic observable primitive (for example, reading, materializing, invoking, publishing, or deleting state). Use the shortest exact excerpt that states that primitive. Use enforcement_kind=workflow_structure for cardinality, uniqueness, per-item or complete-scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation handling, quality thresholds, runtime argument values or instructions, input identifiers or locator syntax, and locally derivable parameter mapping; these guarantees are enforced by generated workflow structure rather than by requiring a generic parameterized capability card to repeat the task-specific rule or consume the caller's original input representation. Split one source sentence into separate overlapping exact excerpts when it contains both an intrinsic primitive and structural context. When they cannot be split without changing meaning, classify the combined excerpt as workflow_structure. A resource identifier or locator alone never proves a capability-contract obligation. Local-processing operations use an empty array. Do not paraphrase, change case or punctuation, combine text from different sources, or derive evidence from provider or tool knowledge.

        Set input_operation_ids to the IDs of every earlier operation whose declared runtime output this operation consumes. Use an empty array when it consumes no earlier operation output. A local validation, normalization, or projection of an external result must name that producer here. Declare only data flow stated by the task; never infer dependencies from descriptions, operation order, provider names, or likely implementations.
        Set decision_source_operation_id to the ID of the earlier operation whose declared runtime result exclusively selects this operation's outcome or selector branch. Use an empty string when the operation is unconditional. A conditional operation may point to a local validation operation, and that local operation must use input_operation_ids to declare the upstream producer whose result it validates. Do not invent a discriminator as a local parsing result and do not use an identifier or locator as evidence of a future choice. If the user requires a runtime-dependent choice but the deciding operation cannot be identified, set complete=false and request that relationship as user clarification.
        Set allow_no_effect_outcome=true only when the user's requested runtime behavior explicitly requires at least one result of that decision source to execute none of this operation's external-effect alternatives, such as abstaining, skipping publication, or performing no decision write when evidence is insufficient. Copy the shortest exact evidence for that outcome into no_effect_outcome_evidence and also include the same or an overlapping excerpt as a workflow_structure coverage requirement for this operation. Otherwise set allow_no_effect_outcome=false and use an empty no_effect_outcome_evidence object. A resource-safety rule, execution-environment restriction, availability check, permission check, or success/failure path does not create a no-effect decision for an otherwise required operation unless the supplied evidence explicitly says that operation is skipped or abstained from. Do not invent an eligibility, trust, or safety decision operation. allow_no_effect_outcome is invalid on unconditional operations. This field permits a safe non-mutating branch; it never permits omission of an independently required effect.
        `required` means that the capability or local obligation must exist in every valid generated plan; it does not mean that its runtime call is unconditional. A conditional operation remains required when any of its branches is required by the task, including when another branch is a no-effect outcome. Set required=false only for enrichment that the user or caller explicitly made optional. For required=false, set optionality_evidence to one non-empty evidence object that explicitly establishes that optionality. For required=true, use an evidence object with empty source_id and excerpt.

        Classify the user/caller policy for human confirmation immediately before external writes in external_write_confirmation_policy:
        - required: the supplied task explicitly requires that confirmation;
        - forbidden: the supplied task explicitly requires unattended execution or prohibits that confirmation;
        - unspecified: neither rule is explicit, so the platform safety default may add confirmation.
        For required or forbidden, set external_write_confirmation_evidence to one non-empty evidence object copied from exactly one supplied evidence source. For unspecified, use an evidence object with empty source_id and excerpt. This policy is about the generated workflow's external writes only; do not derive it from provider, tool, domain, or selector names.
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

        <evidence_sources>
        {{BuildCapabilityEvidenceSourcesJson(evidenceSources)}}
        </evidence_sources>
        """;

    private static string BuildCapabilityInventoryRepairPrompt(
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources,
        CapabilityInventory previous,
        JsonObject? rejectedCandidate,
        IReadOnlyList<CapabilityInventoryContractIssue> contractIssues) => $$"""
        You are a domain-neutral workflow runtime inventory repair analyst. Return only the requested structured JSON.

        A previous inventory was incomplete or violated the deterministic evidence contract. Repair it once by ensuring that every runtime intention expressed by the user is represented as a positive operation or a constraint and every reported contract issue is corrected.

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
        - Every evidence value is an object with source_id and excerpt. Its excerpt must occur within exactly that source after Unicode NFC and whitespace normalization, while case, punctuation, accents, and word order remain exact. Never paraphrase or combine text from different sources.
        - Preserve coverage_requirements as one or more source-addressed evidence objects for every external or human operation. Preserve enforcement_kind=capability_contract only for the shortest exact excerpt that states an intrinsic primitive the selected capability card must document. Use workflow_structure for cardinality, uniqueness, per-item or complete-scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation handling, quality thresholds, runtime argument values or instructions, input identifiers or locator syntax, and locally derivable parameter mapping. Split mixed sentences into separate exact excerpts when possible; otherwise classify the combined excerpt as workflow_structure. A resource identifier or locator alone never proves a capability-contract obligation. Local-processing operations use an empty array.
        - Preserve required=true for every planning obligation, including runtime-conditional operations and branches. required=false is valid only for explicitly optional enrichment and requires a non-empty optionality_evidence object. Required operations use empty source_id and excerpt values.
        - Preserve external_write_confirmation_policy and its source-addressed evidence. Use required or forbidden only when the evidence sources explicitly prove that policy; otherwise use unspecified with empty source_id and excerpt values.
        - Preserve input_operation_ids as the exact earlier-operation data-flow edges. A local validation, normalization, or projection of an external result must identify that producer. Use an empty array when no earlier output is consumed, and never reconstruct a dependency from descriptions, provider names, or operation order alone.
        - Preserve allow_no_effect_outcome=true only with exact no_effect_outcome_evidence that explicitly establishes skipping or abstention for that operation and overlaps one of its workflow_structure coverage requirements. Environment restrictions, availability, permissions, and ordinary success/failure handling do not create a no-effect branch. Otherwise use false with empty evidence. Never invent an eligibility, trust, or safety decision operation.
        - Preserve decision_source_operation_id for runtime-dependent operations. It identifies the earlier operation whose result selects the branch; use an empty string for unconditional operations. A local decision source declares its upstream producer through input_operation_ids. Preserve allow_no_effect_outcome=true only when the user explicitly requires a non-mutating outcome for that conditional operation; it must be false for unconditional operations. Merge mutually exclusive outcome-specific operations into one conditional operation instead of treating every possible branch value as an independently required effect.
        - Preserve intent_origin and derivation_source_operation_id. requested_effect requires an empty derivation source. derived_failure_handling requires the ID of the existing operation whose failure it handles and is never a substitute for a user-requested external effect.
        - Classify constraints with enforcement_kind=exact_denial only for unconditional document-wide prohibitions. Target-, input-, resource-instance-, data-, or branch-dependent restrictions and conditional, ordering, cardinality, coverage, quality, confirmation, and other structural invariants use workflow_policy.
        - Include conditional and optional runtime intentions and mark optional enrichment required=false.
        - Return complete=true and an empty incomplete_reasons array when all requested effects are represented.
        - If the user's requested runtime behavior itself remains genuinely under-specified, return complete=false and concise incomplete_reasons stating what user intent must be clarified. Never cite missing tools, catalogs, selectors, credentials, or implementation knowledge.

        <previous_inventory>
        {{BuildCapabilityInventoryJson(previous)}}
        </previous_inventory>

        <rejected_inventory_candidate>
        {{BuildRejectedCapabilityInventoryCandidate(rejectedCandidate, contractIssues)}}
        </rejected_inventory_candidate>

        <inventory_contract_issues>
        {{BuildCapabilityInventoryContractIssuesJson(contractIssues)}}
        </inventory_contract_issues>

        <evidence_sources>
        {{BuildCapabilityEvidenceSourcesJson(evidenceSources)}}
        </evidence_sources>
        """;

    private static JsonObject BuildCapabilityInventorySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["complete"] = new JsonObject { ["type"] = "boolean" },
            ["external_write_confirmation_policy"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("required", "forbidden", "unspecified")
            },
            ["external_write_confirmation_evidence"] = BuildCapabilityEvidenceReferenceSchema(),
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
                        ["input_operation_ids"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        },
                        ["coverage_requirements"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["maxItems"] = 8,
                            ["items"] = BuildCapabilityCoverageEvidenceReferenceSchema()
                        },
                        ["optionality_evidence"] = BuildCapabilityEvidenceReferenceSchema(),
                        ["decision_source_operation_id"] = new JsonObject { ["type"] = "string" },
                        ["allow_no_effect_outcome"] = new JsonObject { ["type"] = "boolean" },
                        ["no_effect_outcome_evidence"] = BuildCapabilityEvidenceReferenceSchema(),
                        ["intent_origin"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("requested_effect", "derived_failure_handling")
                        },
                        ["derivation_source_operation_id"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "execution_kind", "external_effect_kind", "input_operation_ids", "coverage_requirements", "optionality_evidence", "decision_source_operation_id", "allow_no_effect_outcome", "no_effect_outcome_evidence", "intent_origin", "derivation_source_operation_id"),
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
        ["required"] = new JsonArray("complete", "external_write_confirmation_policy", "external_write_confirmation_evidence", "incomplete_reasons", "operations", "constraints"),
        ["additionalProperties"] = false
    };

    private static CapabilityInventory ParseCapabilityInventory(
        JsonObject json,
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
    {
        if (!TryReadComplete(json, out var complete))
            throw new InvalidOperationException("Capability inventory is missing its completeness decision.");
        var operationNodes = json["operations"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing operations.");
        var constraintNodes = json["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing constraints.");
        var sourcesById = evidenceSources.ToDictionary(static source => source.Id, StringComparer.Ordinal);
        var contractIssues = new List<CapabilityInventoryContractIssue>();
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
            var inputOperationIds = ((node as JsonObject)?["input_operation_ids"] as JsonArray)?
                .Select(static input => input?.GetValue<string>()?.Trim() ?? string.Empty)
                .Where(static input => input.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            if (inputOperationIds.Any(static input => input.Length > 160))
                throw new InvalidOperationException($"Capability inventory operation '{id}' has an invalid input_operation_ids entry.");
            var hasCoverageRequirements = node is JsonObject operationObject
                                          && operationObject.ContainsKey("coverage_requirements");
            var coverageNodes = (node as JsonObject)?["coverage_requirements"] as JsonArray;
            var coverageEvidence = new List<CapabilityEvidenceAnchor>();
            var workflowStructureCoverageRequirementIds = new HashSet<string>(StringComparer.Ordinal);
            if (hasCoverageRequirements && coverageNodes is null)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_shape_invalid",
                    id,
                    "coverage_requirements",
                    null));
            }
            else if (coverageNodes is not null)
            {
                if (coverageNodes.Count > 8)
                {
                    contractIssues.Add(NewCapabilityInventoryContractIssue(
                        "evidence_limit_exceeded",
                        id,
                        "coverage_requirements",
                        null));
                }

                for (var evidenceIndex = 0; evidenceIndex < Math.Min(coverageNodes.Count, 8); evidenceIndex++)
                {
                    var enforcementKind = (coverageNodes[evidenceIndex] as JsonObject)?["enforcement_kind"]?
                        .GetValue<string>()?.Trim().ToLowerInvariant()
                        ?? CapabilityContractCoverageEnforcementKind;
                    if (enforcementKind is not (CapabilityContractCoverageEnforcementKind or WorkflowStructureCoverageEnforcementKind))
                    {
                        contractIssues.Add(NewCapabilityInventoryContractIssue(
                            "coverage_enforcement_kind_invalid",
                            id,
                            "coverage_requirements",
                            evidenceIndex));
                        continue;
                    }
                    var anchor = ResolveCapabilityEvidenceReference(
                        coverageNodes[evidenceIndex],
                        sourcesById,
                        id,
                        "coverage_requirements",
                        evidenceIndex,
                        allowEmpty: false,
                        contractIssues);
                    if (anchor is not null
                        && coverageEvidence.All(existing => !string.Equals(
                            existing.Id,
                            anchor.Id,
                            StringComparison.Ordinal)))
                    {
                        coverageEvidence.Add(anchor);
                        if (string.Equals(
                                enforcementKind,
                                WorkflowStructureCoverageEnforcementKind,
                                StringComparison.Ordinal))
                        {
                            workflowStructureCoverageRequirementIds.Add(anchor.Id);
                        }
                    }
                }
            }

            if (executionKind == "local_processing" && coverageEvidence.Count > 0)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_forbidden_for_local_operation",
                    id,
                    "coverage_requirements",
                    null));
                coverageEvidence.Clear();
            }
            else if (hasCoverageRequirements
                     && executionKind is "external_effect" or "human_interaction"
                     && coverageEvidence.Count == 0
                     && !contractIssues.Any(issue => string.Equals(issue.OperationId, id, StringComparison.Ordinal)
                                                     && string.Equals(issue.Field, "coverage_requirements", StringComparison.Ordinal)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    id,
                    "coverage_requirements",
                    null));
            }

            var optionalityEvidenceAnchor = (node as JsonObject)?.ContainsKey("optionality_evidence") == true
                ? ResolveCapabilityEvidenceReference(
                    (node as JsonObject)?["optionality_evidence"],
                    sourcesById,
                    id,
                    "optionality_evidence",
                    null,
                    allowEmpty: true,
                    contractIssues)
                : null;
            if (required && optionalityEvidenceAnchor is not null)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_forbidden",
                    id,
                    "optionality_evidence",
                    null,
                    optionalityEvidenceAnchor.SourceId,
                    optionalityEvidenceAnchor.Id));
                optionalityEvidenceAnchor = null;
            }
            else if (!required && optionalityEvidenceAnchor is null
                     && !contractIssues.Any(issue => string.Equals(issue.OperationId, id, StringComparison.Ordinal)
                                                     && string.Equals(issue.Field, "optionality_evidence", StringComparison.Ordinal)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    id,
                    "optionality_evidence",
                    null));
            }
            var optionalityEvidence = optionalityEvidenceAnchor?.Excerpt ?? string.Empty;
            var allowNoEffectOutcome = (node as JsonObject)?["allow_no_effect_outcome"]?.GetValue<bool>() ?? false;
            if (allowNoEffectOutcome && decisionSourceOperationId.Length == 0)
                throw new InvalidOperationException($"Capability inventory operation '{id}' cannot allow a no-effect outcome without a decision source.");
            var hasNoEffectOutcomeEvidence = node is JsonObject noEffectOperationObject
                                             && noEffectOperationObject.ContainsKey("no_effect_outcome_evidence");
            var noEffectOutcomeEvidenceAnchor = hasNoEffectOutcomeEvidence
                ? ResolveCapabilityEvidenceReference(
                    (node as JsonObject)?["no_effect_outcome_evidence"],
                    sourcesById,
                    id,
                    "no_effect_outcome_evidence",
                    null,
                    allowEmpty: true,
                    contractIssues)
                : null;
            if (allowNoEffectOutcome
                && hasNoEffectOutcomeEvidence
                && noEffectOutcomeEvidenceAnchor is null
                && !contractIssues.Any(issue => string.Equals(issue.OperationId, id, StringComparison.Ordinal)
                                                && string.Equals(issue.Field, "no_effect_outcome_evidence", StringComparison.Ordinal)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    id,
                    "no_effect_outcome_evidence",
                    null));
            }
            else if (!allowNoEffectOutcome && noEffectOutcomeEvidenceAnchor is not null)
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_forbidden",
                    id,
                    "no_effect_outcome_evidence",
                    null,
                    noEffectOutcomeEvidenceAnchor.SourceId,
                    noEffectOutcomeEvidenceAnchor.Id));
                noEffectOutcomeEvidenceAnchor = null;
            }
            else if (allowNoEffectOutcome
                     && noEffectOutcomeEvidenceAnchor is not null
                     && !coverageEvidence.Any(requirement =>
                         workflowStructureCoverageRequirementIds.Contains(requirement.Id)
                         && CapabilityEvidenceRangesOverlap(requirement, noEffectOutcomeEvidenceAnchor)))
            {
                contractIssues.Add(NewCapabilityInventoryContractIssue(
                    "no_effect_evidence_not_workflow_structure",
                    id,
                    "no_effect_outcome_evidence",
                    null,
                    noEffectOutcomeEvidenceAnchor.SourceId,
                    noEffectOutcomeEvidenceAnchor.Id));
                noEffectOutcomeEvidenceAnchor = null;
            }
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
                derivationSourceOperationId,
                allowNoEffectOutcome,
                optionalityEvidence)
            {
                InputOperationIds = inputOperationIds,
                CoverageRequirements = coverageEvidence.Select(static evidence => evidence.Excerpt).ToArray(),
                CoverageRequirementEvidence = coverageEvidence,
                WorkflowStructureCoverageRequirementIds = workflowStructureCoverageRequirementIds,
                OptionalityEvidenceAnchor = optionalityEvidenceAnchor,
                NoEffectOutcomeEvidenceAnchor = noEffectOutcomeEvidenceAnchor
            };
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
        var operationIndexes = operations
            .Select(static (operation, index) => (operation.Id, Index: index))
            .ToDictionary(static item => item.Id, static item => item.Index, StringComparer.Ordinal);
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
            foreach (var inputOperationId in operation.InputOperationIds)
            {
                if (!operationIndexes.TryGetValue(inputOperationId, out var inputIndex)
                    || inputIndex >= operationIndexes[operation.Id])
                {
                    throw new InvalidOperationException(
                        $"Capability inventory operation '{operation.Id}' references an unknown, self, or later input operation '{inputOperationId}'.");
                }
            }
        }
        var confirmationPolicy = json["external_write_confirmation_policy"]?.GetValue<string>()?.Trim().ToLowerInvariant()
                                 ?? "unspecified";
        if (confirmationPolicy is not ("required" or "forbidden" or "unspecified"))
            throw new InvalidOperationException("Capability inventory has an invalid external-write confirmation policy contract.");
        var confirmationEvidenceAnchor = json.ContainsKey("external_write_confirmation_evidence")
            ? ResolveCapabilityEvidenceReference(
                json["external_write_confirmation_evidence"],
                sourcesById,
                string.Empty,
                "external_write_confirmation_evidence",
                null,
                allowEmpty: true,
                contractIssues)
            : null;
        if (confirmationPolicy == "unspecified" && confirmationEvidenceAnchor is not null)
        {
            contractIssues.Add(NewCapabilityInventoryContractIssue(
                "evidence_forbidden",
                string.Empty,
                "external_write_confirmation_evidence",
                null,
                confirmationEvidenceAnchor.SourceId,
                confirmationEvidenceAnchor.Id));
            confirmationEvidenceAnchor = null;
        }
        else if (confirmationPolicy != "unspecified" && confirmationEvidenceAnchor is null
                 && !contractIssues.Any(static issue => issue.OperationId.Length == 0
                                                        && issue.Field == "external_write_confirmation_evidence"))
            contractIssues.Add(NewCapabilityInventoryContractIssue(
                "evidence_missing",
                string.Empty,
                "external_write_confirmation_evidence",
                null));

        if (contractIssues.Count > 0)
            throw new CapabilityInventoryContractException(contractIssues);

        var confirmationEvidence = confirmationEvidenceAnchor?.Excerpt ?? string.Empty;

        var reasons = ParseCapabilityInventoryReasons(json["incomplete_reasons"] as JsonArray);
        if (complete && reasons.Count > 0)
            throw new InvalidOperationException("A complete capability inventory cannot contain incomplete reasons.");
        return new CapabilityInventory(
            complete,
            operations,
            constraints,
            reasons,
            confirmationPolicy,
            confirmationEvidence)
        {
            ExternalWriteConfirmationEvidenceAnchor = confirmationEvidenceAnchor
        };
    }

    private static CapabilityEvidenceAnchor? ResolveCapabilityEvidenceReference(
        JsonNode? node,
        IReadOnlyDictionary<string, CapabilityEvidenceSource> sourcesById,
        string operationId,
        string field,
        int? index,
        bool allowEmpty,
        List<CapabilityInventoryContractIssue> issues)
    {
        if (node is not JsonObject evidence)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_shape_invalid",
                operationId,
                field,
                index));
            return null;
        }

        var sourceId = evidence["source_id"] is JsonValue sourceValue
                       && sourceValue.TryGetValue<string>(out var sourceText)
            ? sourceText.Trim()
            : string.Empty;
        var excerpt = evidence["excerpt"] is JsonValue excerptValue
                      && excerptValue.TryGetValue<string>(out var excerptText)
            ? excerptText
            : string.Empty;
        if (sourceId.Length == 0 && string.IsNullOrWhiteSpace(excerpt))
        {
            if (!allowEmpty)
            {
                issues.Add(NewCapabilityInventoryContractIssue(
                    "evidence_missing",
                    operationId,
                    field,
                    index));
            }
            return null;
        }

        var rejectedEvidenceId = BuildCapabilityEvidenceId(sourceId, -1, 0, excerpt);
        if (sourceId.Length == 0 || string.IsNullOrWhiteSpace(excerpt))
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_missing",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }
        if (!sourcesById.TryGetValue(sourceId, out var source))
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "source_unknown",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }

        var canonicalExcerpt = CanonicalizeCapabilityEvidenceText(excerpt);
        if (canonicalExcerpt.Length == 0)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_missing",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }
        if (canonicalExcerpt.Length > CapabilityDescriptionMaxCharacters)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "evidence_limit_exceeded",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }

        var canonicalSource = CanonicalizeCapabilityEvidenceText(source.Text);
        var start = canonicalSource.IndexOf(canonicalExcerpt, StringComparison.Ordinal);
        if (start < 0)
        {
            issues.Add(NewCapabilityInventoryContractIssue(
                "excerpt_not_found",
                operationId,
                field,
                index,
                sourceId,
                rejectedEvidenceId));
            return null;
        }

        return new CapabilityEvidenceAnchor(
            BuildCapabilityEvidenceId(sourceId, start, canonicalExcerpt.Length, canonicalExcerpt),
            sourceId,
            start,
            canonicalExcerpt.Length,
            canonicalExcerpt);
    }

    private static string CanonicalizeCapabilityEvidenceText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var whitespacePending = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool CapabilityEvidenceRangesOverlap(
        CapabilityEvidenceAnchor left,
        CapabilityEvidenceAnchor right)
        => string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
           && left.Start < right.Start + right.Length
           && right.Start < left.Start + left.Length;

    private static string BuildCapabilityEvidenceId(
        string sourceId,
        int start,
        int length,
        string excerpt)
    {
        var canonical = sourceId + "\n" + start + "\n" + length + "\n"
                        + CanonicalizeCapabilityEvidenceText(excerpt);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return "evidence_" + hash[..24];
    }

    private static CapabilityInventoryContractIssue NewCapabilityInventoryContractIssue(
        string code,
        string operationId,
        string field,
        int? index,
        string sourceId = "",
        string evidenceId = "")
        => new(code, operationId, field, index, sourceId, evidenceId);

    private static IReadOnlyList<CapabilityInventoryContractIssue> GetCapabilityInventoryContractIssues(
        Exception exception)
        => exception is CapabilityInventoryContractException contractException
            ? contractException.Issues
            : [NewCapabilityInventoryContractIssue(
                "inventory_contract_invalid",
                string.Empty,
                "$",
                null,
                evidenceId: BuildCapabilityEvidenceId(
                    exception.GetType().Name,
                    -1,
                    0,
                    exception.Message))];

    private static CapabilityInventory BuildInvalidCapabilityInventory(
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
        => new(
            false,
            Array.Empty<CapabilityInventoryOperation>(),
            Array.Empty<CapabilityInventoryConstraint>(),
            [new CapabilityInventoryIncompleteReason(
                "inventory_contract_invalid",
                issues.Count == 0
                    ? "The inventory violated its deterministic contract."
                    : $"The inventory violated its deterministic contract with {issues.Count} issue(s).")]);

    private static void RecordCapabilityInventoryContractTelemetry(
        TelemetrySpanScope span,
        string stage,
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
    {
        span.SetAttribute($"gnougo-flow.plan.capability_inventory.{stage}_contract_issue_count", issues.Count);
        span.SetAttribute(
            $"gnougo-flow.plan.capability_inventory.{stage}_contract_issue_codes",
            string.Join(',', issues.Select(static issue => issue.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)));
        foreach (var issue in issues)
        {
            span.AddEvent("gnougo-flow.plan.capability_inventory.contract_issue", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.code", issue.Code),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.operation_id", issue.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.field", issue.Field),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.index", issue.Index),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.source_id", issue.SourceId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_inventory.contract_issue.evidence_id", issue.EvidenceId)
            });
        }
    }

    private static string BuildCapabilityInventoryContractIssuesJson(
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
        => new JsonArray(issues.Select(static issue => (JsonNode)BuildCapabilityInventoryContractIssueJson(issue))
            .ToArray()).ToJsonString();

    private static JsonObject BuildCapabilityInventoryContractIssueJson(
        CapabilityInventoryContractIssue issue)
        => new()
        {
            ["code"] = issue.Code,
            ["operation_id"] = issue.OperationId,
            ["field"] = issue.Field,
            ["index"] = issue.Index,
            ["source_id"] = issue.SourceId,
            ["evidence_id"] = issue.EvidenceId
        };

    private static string BuildRejectedCapabilityInventoryCandidate(
        JsonObject? rejectedCandidate,
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
    {
        if (rejectedCandidate is null)
            return "{}";
        var serialized = rejectedCandidate.ToJsonString();
        if (serialized.Length <= CapabilityInventoryRepairCandidateMaxCharacters)
            return serialized;

        var affectedOperationIds = issues
            .Select(static issue => issue.OperationId)
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var projected = new JsonObject
        {
            ["complete"] = rejectedCandidate["complete"]?.DeepClone(),
            ["external_write_confirmation_policy"] = rejectedCandidate["external_write_confirmation_policy"]?.DeepClone(),
            ["external_write_confirmation_evidence"] = rejectedCandidate["external_write_confirmation_evidence"]?.DeepClone(),
            ["incomplete_reasons"] = rejectedCandidate["incomplete_reasons"]?.DeepClone(),
            ["operations"] = new JsonArray((rejectedCandidate["operations"] as JsonArray)?
                .OfType<JsonObject>()
                .Where(operation => affectedOperationIds.Contains(
                    operation["id"]?.GetValue<string>()?.Trim() ?? string.Empty))
                .Select(static operation => (JsonNode)operation.DeepClone())
                .ToArray() ?? []),
            ["constraints_omitted"] = true
        };
        return projected.ToJsonString();
    }

    private static void ThrowInvalidCapabilityInventoryContract(
        IReadOnlyList<CapabilityInventoryContractIssue> initialIssues,
        IReadOnlyList<CapabilityInventoryContractIssue> finalIssues,
        CapabilityInventory inventory)
    {
        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability inventory inference violated its deterministic evidence contract after one repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_inventory",
                ["classification"] = "model_contract_violation",
                ["repair_attempted"] = true,
                ["attempts"] = 2,
                ["initial_contract_issue_count"] = initialIssues.Count,
                ["contract_issues"] = new JsonArray(finalIssues
                    .Select(static issue => (JsonNode)BuildCapabilityInventoryContractIssueJson(issue))
                    .ToArray()),
                ["operation_count"] = inventory.Operations.Count,
                ["constraint_count"] = inventory.Constraints.Count,
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "retry_or_change_planning_model"
            });
    }

    private static CapabilityInventory RemovePlannerBoundaryArtifacts(
        CapabilityInventory inventory,
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
    {
        var evidenceCorpus = BuildCapabilityEvidenceCorpus(evidenceSources);
        var userConcepts = CountPlannerBoundaryConcepts(evidenceCorpus);
        var operations = inventory.Operations
            .Where(static operation => !string.Equals(
                operation.IntentOrigin,
                "derived_failure_handling",
                StringComparison.Ordinal))
            .Where(static operation => !IsHostInputContractArtifact(operation))
            .Where(operation => !IsUngroundedCleanupArtifact(operation, evidenceSources))
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
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
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
        return !evidenceSources.Any(static source => Regex.IsMatch(
            source.Text.Replace('_', ' '),
            @"\b(clean(?:up|\s+up)|delete|remove|dispose|release|disconnect|close|tear\s*down|destroy|purge)\w*\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
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
        CapabilityInventory inventory)
    {
        if (!inventory.Complete
            || string.Equals(
                inventory.ExternalWriteConfirmationPolicy,
                "forbidden",
                StringComparison.Ordinal)
            || !inventory.Operations.Any(static operation => operation.ExecutionKind == "external_effect"
                && operation.ExternalEffectKind == "write"))
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

    private static CapabilityInventory ApplyIntentClarificationExternalWriteConfirmationPolicy(
        CapabilityInventory inventory,
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources,
        IntentClarificationSession? session)
    {
        if (session is null)
            return inventory;

        var selectedPolicies = session.Answers
            .Select((answer, index) => (Answer: answer, Index: index))
            .Where(static item => item.Answer.ExternalWriteConfirmationPolicy is "required" or "forbidden")
            .ToArray();
        var distinctPolicies = selectedPolicies
            .Select(static item => item.Answer.ExternalWriteConfirmationPolicy)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctPolicies.Length > 1)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.WorkflowPlanCannotPlanSafely,
                "Intent clarification selected conflicting external-write confirmation policies.",
                details: session.BuildSafeMetadata(
                    "capability_inventory",
                    "cannot_plan_safely",
                    "clarify_or_abandon"));
        }
        if (selectedPolicies.Length == 0)
            return inventory;

        var selected = selectedPolicies[0];
        var sourceId = $"clarification_{selected.Index + 1:D4}";
        var source = evidenceSources.First(item => string.Equals(item.Id, sourceId, StringComparison.Ordinal));
        var excerpt = $"External write confirmation policy: {selected.Answer.ExternalWriteConfirmationPolicy}.";
        var canonicalSource = CanonicalizeCapabilityEvidenceText(source.Text);
        var canonicalExcerpt = CanonicalizeCapabilityEvidenceText(excerpt);
        var start = canonicalSource.IndexOf(canonicalExcerpt, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("Selected clarification policy evidence was not preserved in the capability corpus.");

        var anchor = new CapabilityEvidenceAnchor(
            BuildCapabilityEvidenceId(sourceId, start, canonicalExcerpt.Length, canonicalExcerpt),
            sourceId,
            start,
            canonicalExcerpt.Length,
            canonicalExcerpt);
        return inventory with
        {
            ExternalWriteConfirmationPolicy = selected.Answer.ExternalWriteConfirmationPolicy,
            ExternalWriteConfirmationEvidence = canonicalExcerpt,
            ExternalWriteConfirmationEvidenceAnchor = anchor
        };
    }

    private static (string Policy, string Source) ResolveEffectiveExternalWriteConfirmationPolicy(
        CapabilityInventory inventory,
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources)
    {
        if (inventory.ExternalWriteConfirmationPolicy is "required" or "forbidden")
        {
            var sourceKind = inventory.ExternalWriteConfirmationEvidenceAnchor is { } anchor
                ? evidenceSources.FirstOrDefault(source => string.Equals(source.Id, anchor.SourceId, StringComparison.Ordinal))?.Kind
                : null;
            return (inventory.ExternalWriteConfirmationPolicy, sourceKind switch
            {
                "clarification" => "clarification",
                "caller_context" => "caller",
                "user_request" => "explicit_request",
                _ => "validated_evidence"
            });
        }

        var hasExternalWrite = inventory.Operations.Any(static operation =>
            operation.ExecutionKind == "external_effect" && operation.ExternalEffectKind == "write");
        return hasExternalWrite ? ("required", "platform_default") : ("unspecified", "none");
    }

    private static (string Policy, string Source) ResolveEffectiveExternalWriteConfirmationPolicy(
        IReadOnlyList<ResolvedCapability> capabilities,
        IntentClarificationSession? session,
        string mode)
    {
        var clarifiedPolicies = session?.Answers
            .Select(static answer => answer.ExternalWriteConfirmationPolicy)
            .Where(static policy => policy is "required" or "forbidden")
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (clarifiedPolicies.Length == 1)
            return (clarifiedPolicies[0], "clarification");

        var hasPlatformConfirmation = capabilities.Any(static capability =>
            capability.Required
            && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
            && capability.OperationId?.StartsWith("platform_confirm_external_write", StringComparison.Ordinal) == true);
        if (hasPlatformConfirmation)
            return ("required", "platform_default");
        return string.Equals(mode, "explicit", StringComparison.Ordinal)
            ? ("unspecified", "explicit_configuration")
            : ("unspecified", "none");
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
            ["input_operation_ids"] = new JsonArray(operation.InputOperationIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["coverage_requirements"] = new JsonArray(operation.CoverageRequirementEvidence
                .Select(evidence =>
                {
                    var reference = BuildCapabilityEvidenceReferenceJson(evidence);
                    reference["enforcement_kind"] = operation.WorkflowStructureCoverageRequirementIds.Contains(evidence.Id)
                        ? WorkflowStructureCoverageEnforcementKind
                        : CapabilityContractCoverageEnforcementKind;
                    return (JsonNode)reference;
                }).ToArray()),
            ["optionality_evidence"] = BuildCapabilityEvidenceReferenceJson(operation.OptionalityEvidenceAnchor),
            ["decision_source_operation_id"] = operation.DecisionSourceOperationId,
            ["allow_no_effect_outcome"] = operation.AllowNoEffectOutcome,
            ["no_effect_outcome_evidence"] = BuildCapabilityEvidenceReferenceJson(
                operation.NoEffectOutcomeEvidenceAnchor),
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
            ["external_write_confirmation_policy"] = inventory.ExternalWriteConfirmationPolicy,
            ["external_write_confirmation_evidence"] = BuildCapabilityEvidenceReferenceJson(
                inventory.ExternalWriteConfirmationEvidenceAnchor),
            ["incomplete_reasons"] = reasons,
            ["operations"] = operations,
            ["constraints"] = constraints
        }.ToJsonString();
    }

    private static JsonObject BuildCapabilityEvidenceReferenceJson(CapabilityEvidenceAnchor? evidence)
        => new()
        {
            ["source_id"] = evidence?.SourceId ?? string.Empty,
            ["excerpt"] = evidence?.Excerpt ?? string.Empty
        };

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
            ["input_operation_ids"] = new JsonArray(operation.InputOperationIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["coverage_requirements"] = new JsonArray(operation.CoverageRequirementEvidence.Select(evidence => (JsonNode)new JsonObject
            {
                ["requirement"] = evidence.Excerpt,
                ["enforcement_kind"] = operation.WorkflowStructureCoverageRequirementIds.Contains(evidence.Id)
                    ? WorkflowStructureCoverageEnforcementKind
                    : CapabilityContractCoverageEnforcementKind
            }).ToArray()),
            ["decision_source_operation_id"] = operation.DecisionSourceOperationId,
            ["allow_no_effect_outcome"] = operation.AllowNoEffectOutcome
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
            - conditional: another inventory operation determines whether or which external effect executes; select either two or more selector-specific variants as mutually exclusive branches, or—only when allow_no_effect_outcome=true—one or more capabilities that must all execute in catalog_ids order for the single effect value;
            - ambiguous: more than one plausible implementation remains and the catalog does not establish which is correct;
            - unavailable: the catalog contains no sufficient implementation.

            Prefer the smallest sufficient composition. A composition is valid only when every selected capability is necessary for the one operation. For a multi-action tool, choose selector-specific entries whose request_bindings describe the logical operation. Different selector values are distinct capabilities.
            A selector entry with variant_of inherits the description, arguments, outputs, and artifact contract from the whole-tool entry identified by the same server, kind, and method; its compact row intentionally contains only the distinguishing literal request_bindings.
            A whole-tool entry without request_bindings is appropriate when enum-valued arguments are runtime data rather than a fixed logical action. Prefer a combined selector entry over several single-selector entries when one physical call requires all of those fixed literal values.
            Selector bindings form a structural specificity order for one physical capability. When every binding of one entry appears with the same value in a more-specific entry, the broader entry is only an ancestor representation: never select or retain it beside that descendant. Keep every incomparable maximal entry when they are genuine alternatives; keep only the unique maximal entry when all other referenced entries are its ancestors.
            When an operation has a non-empty decision_source_operation_id, use status conditional and copy the locked decision_source_operation_id into decision_operation_id. Trace a local decision source only through its declared input_operation_ids; never infer the producer from descriptions or adjacency. Prefer a selected decision capability that documents one string enum output containing every selector branch value. When allow_no_effect_outcome=true, that enum may contain additional values that intentionally execute no external-effect branch. If no suitable discovered output exists, Flow may synthesize a strict provider-neutral structured-output projection for one selected MCP capability or native llm.call and will validate it after generation. Conditional variants must belong to one physical capability, share the same selector paths and every fixed selector except one mutually exclusive selector path, and use distinct values on that path. A conditional complementary composition is valid only when allow_no_effect_outcome=true: every selected capability is necessary for the single effect outcome, all selected invocations are structurally distinct, catalog_ids order is execution order, and the alternative is the declared no-effect outcome. Keep independently required read variants as composed when no runtime discriminator can be grounded. Include complementary unconditional prerequisites with selector alternatives only when necessary; they execute once outside the exclusive branch. This is runtime control flow, not user ambiguity. Never ask the user to predict a future runtime result.
            Set conditional_mode=exactly_one for mutually exclusive selector variants. Set conditional_mode=all_on_value for a conditional complementary composition whose selected capabilities all execute in catalog_ids order. Use an empty conditional_mode for every non-conditional status.
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
            - matched requires one catalog_ids value; composed requires at least two; conditional requires either exactly one mutually exclusive selector subset of at least two entries plus any necessary complementary prerequisites, or, when allow_no_effect_outcome=true and conditional_mode=all_on_value, one or more entries that all execute in catalog_ids order for the single effect value; local and unavailable require none. candidate_catalog_ids are advisory and are ignored for a final matched, composed, or conditional decision.
            - decision_operation_id is required only for conditional and must exactly equal that operation's non-empty decision_source_operation_id; return an empty string for all other statuses.
            - conditional_mode is required: use exactly_one or all_on_value only for conditional, and an empty string otherwise.
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
                        ["conditional_mode"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(string.Empty, ConditionalExactlyOneActivationMode, ConditionalAllOnValueActivationMode)
                        },
                        ["reason"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("operation_id", "status", "catalog_ids", "candidate_catalog_ids", "decision_operation_id", "conditional_mode", "reason"),
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
                issues.Add(new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, "invalid", reason, Array.Empty<string>())
                {
                    ValidationIssue = "operation_occurrence_invalid",
                    InvalidFields = ["operation_id"]
                });
                continue;
            }

            var node = nodes[0];
            var status = ReadMatchingString(node, "status").ToLowerInvariant();
            var selected = ReadMatchingIds(node["catalog_ids"], 32, out var selectedValid);
            var candidates = ReadMatchingIds(node["candidate_catalog_ids"], 8, out var candidatesValid);
            var reportedStatus = status;
            var reportedSelectedCount = selected.Count;
            var reportedCandidateCount = candidates.Count;
            var reportedSelectedValid = selectedValid;
            var reportedCandidatesValid = candidatesValid;
            var reportedSelectedIdsKnown = selected.All(entries.ContainsKey);
            var reportedCandidateIdsKnown = candidates.All(entries.ContainsKey);
            var decisionOperationId = ResolveMatchingInventoryId(
                ReadMatchingString(node, "decision_operation_id"),
                operationIds);
            var requestedConditionalMode = ReadMatchingString(node, "conditional_mode");
            var reasonText = SanitizeCapabilityInferenceDiagnostic(ReadMatchingString(node, "reason"), 1_000);
            var validStatus = status is "matched" or "composed" or "conditional" or "local" or "ambiguous" or "unavailable";
            string? normalizationReasonCode = null;

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
            else if (selected.Count == 0
                     && (status == "composed" && candidates.Count >= 2
                         || status == "conditional" && candidates.Count >= 1))
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
            if (status == "matched" && selected.Count == 0 && candidates.Count > 1 && knownCandidates)
            {
                var originalCandidates = candidates;
                candidates = RemoveStructurallyRedundantSelectorAncestorEntries(
                    candidates,
                    entries,
                    out var candidateSelectorCanonicalized);
                if (candidateSelectorCanonicalized)
                {
                    normalizationReasonCode = originalCandidates
                        .Except(candidates, StringComparer.Ordinal)
                        .Any(id => entries[id].RequestBindings.Count > 0)
                        ? "selector_ancestor_chain_canonicalized"
                        : "selector_base_variant_canonicalized";
                }
                if (candidates.Count == 1)
                {
                    selected = candidates;
                    candidates = Array.Empty<string>();
                    selectedValid = candidatesValid;
                    candidatesValid = true;
                    knownSelected = true;
                }
            }
            if (status == "matched" && knownSelected)
            {
                var originalSelected = selected;
                selected = RemoveStructurallyRedundantSelectorAncestorEntries(
                    selected,
                    entries,
                    out var selectorCanonicalized);
                if (selectorCanonicalized)
                {
                    normalizationReasonCode = originalSelected
                        .Except(selected, StringComparer.Ordinal)
                        .Any(id => entries[id].RequestBindings.Count > 0)
                        ? "selector_ancestor_chain_canonicalized"
                        : "selector_base_variant_canonicalized";
                }
            }
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
            var shapeValid = validStatus
                             && reportedSelectedValid
                             && reportedCandidatesValid
                             && reportedSelectedIdsKnown
                             && reportedCandidateIdsKnown
                             && selectedValid
                             && candidatesValid
                             && knownSelected
                             && knownCandidates
                             && reasonText.Length > 0;
            var conditionalActivationMode = string.Empty;
            var conditionalTopologyValid = status == "conditional"
                                           && selected.All(entries.ContainsKey)
                                           && TryBuildConditionalActivation(
                                               selected.Select(id => entries[id]).ToArray(),
                                               operation.AllowNoEffectOutcome,
                                               requestedConditionalMode,
                                               out _,
                                               out conditionalActivationMode);
            shapeValid = shapeValid && status switch
            {
                "matched" => selected.Count == 1 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                "composed" => selected.Count >= 2 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                "conditional" => selected.Count >= 1
                                  && candidates.Count == 0
                                  && decisionOperationId.Length > 0
                                 && string.Equals(
                                     decisionOperationId,
                                     operation.DecisionSourceOperationId,
                                     StringComparison.Ordinal)
                                 && inventory.Operations.Any(candidate => string.Equals(candidate.Id, decisionOperationId, StringComparison.Ordinal))
                                  && conditionalTopologyValid,
                "local" => selected.Count == 0 && candidates.Count == 0 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0 && operation.ExecutionKind == "local_processing",
                "ambiguous" => selected.Count == 0 && candidates.Count > 0 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                "unavailable" => selected.Count == 0 && candidates.Count == 0 && decisionOperationId.Length == 0 && requestedConditionalMode.Length == 0,
                _ => false
            };
            if (operation.ExecutionKind != "local_processing" && status == "local"
                || operation.ExecutionKind == "local_processing" && status != "local")
                shapeValid = false;

            if (!shapeValid)
            {
                contractValid = false;
                var diagnostic = BuildInvalidMatchingDiagnostic(
                    reportedStatus,
                    reportedSelectedValid,
                    reportedCandidatesValid,
                    reportedSelectedIdsKnown,
                    reportedCandidateIdsKnown,
                    reasonText.Length > 0,
                    selected.Count,
                    candidates.Count,
                    decisionOperationId,
                    requestedConditionalMode,
                    conditionalTopologyValid,
                    operation);
                status = "invalid";
                reasonText = diagnostic.Reason;
                var issue = new CapabilityMatchingIssue(
                    operation.Id,
                    operation.Description,
                    operation.Required,
                    status,
                    reasonText,
                    selected.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray())
                {
                    ValidationIssue = diagnostic.Code,
                    ReportedStatus = reportedStatus,
                    SelectedCatalogIdCount = reportedSelectedCount,
                    CandidateCatalogIdCount = reportedCandidateCount,
                    InvalidFields = diagnostic.InvalidFields
                };
                issues.Add(issue);
            }
            else if (string.Equals(
                         conditionalActivationMode,
                         ConditionalAllOnValueActivationMode,
                         StringComparison.Ordinal))
            {
                normalizationReasonCode = "conditional_composition_canonicalized";
            }
            operationMatches.Add(new CapabilityOperationMatch(operation, status, reasonText, selected, candidates,
                decisionOperationId.Length > 0 ? decisionOperationId : null)
            {
                NormalizationReasonCode = normalizationReasonCode,
                ConditionalActivationMode = shapeValid ? conditionalActivationMode : requestedConditionalMode
            });
            if (status is "ambiguous" or "unavailable")
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
            var reportedStatus = status;
            var denied = ReadMatchingIds(node["denied_catalog_ids"], 64, out var deniedValid);
            var candidates = ReadMatchingIds(node["candidate_catalog_ids"], 8, out var candidatesValid);
            var reasonText = SanitizeCapabilityInferenceDiagnostic(ReadMatchingString(node, "reason"), 1_000);
            var referencedIds = denied.Concat(candidates).ToArray();
            var referencesOnlyKnownNativeCapabilities = deniedValid
                                                       && candidatesValid
                                                       && referencedIds.Length > 0
                                                       && referencedIds.All(id => entries.TryGetValue(id, out var entry)
                                                                                  && entry.Resolution == "native");
            var normalizedNativePolicyOnly = referencesOnlyKnownNativeCapabilities;
            if (normalizedNativePolicyOnly)
            {
                // Constraint denial contracts intentionally lock only exact MCP alternatives.
                // Native orchestration restrictions remain provider-neutral policy text even
                // when the inventory over-classified one as exact_denial. A native catalog ID
                // can never become an MCP denied alternative.
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
                                 && (normalizedNativePolicyOnly || string.Equals(
                                     constraint.EnforcementKind,
                                     "workflow_policy",
                                     StringComparison.Ordinal)),
                "ambiguous" => denied.Count == 0 && candidates.Count > 0,
                _ => false
            };
            if (!shapeValid)
            {
                var diagnostic = BuildInvalidConstraintMatchingDiagnostic(
                    status,
                    deniedValid,
                    candidatesValid,
                    knownDenied,
                    knownCandidates,
                    reasonText.Length > 0,
                    denied.Count,
                    candidates.Count,
                    constraint,
                    normalizedNativePolicyOnly);
                contractValid = false;
                status = "invalid";
                reasonText = diagnostic.Reason;
                issues.Add(new CapabilityMatchingIssue(
                    constraint.Id,
                    constraint.Description,
                    constraint.Required,
                    status,
                    reasonText,
                    denied.Concat(candidates).Where(entries.ContainsKey).Take(8).ToArray())
                {
                    ValidationIssue = diagnostic.Code,
                    ReportedStatus = reportedStatus,
                    SelectedCatalogIdCount = denied.Count,
                    CandidateCatalogIdCount = candidates.Count,
                    InvalidFields = diagnostic.InvalidFields
                });
            }
            constraintMatches.Add(new CapabilityConstraintMatch(constraint, status, reasonText, denied, candidates));
            if (status == "ambiguous")
                issues.Add(new CapabilityMatchingIssue(constraint.Id, constraint.Description, constraint.Required, status, reasonText,
                    candidates));
        }

        var expectedConstraintIds = inventory.Constraints.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in constraintObjects.Select(ReadConstraintId)
                     .Where(id => id.Length > 0 && !expectedConstraintIds.Contains(id)).Distinct(StringComparer.Ordinal))
        {
            contractValid = false;
            issues.Add(new CapabilityMatchingIssue(unknown, "Unknown constraint identifier.", true, "invalid",
                "The matching response referenced a constraint that was not present in the locked inventory.", Array.Empty<string>()));
        }

        return GroundConditionalCapabilityMatches(
            new CapabilityMatchingEvaluation(operationMatches, constraintMatches, issues, contractValid),
            catalog);
    }

    private static CapabilityMatchingEvaluation GroundConditionalCapabilityMatches(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog catalog)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var issues = evaluation.Issues.ToList();
        var matches = evaluation.OperationMatches.Select(match =>
        {
            if (!string.Equals(match.Status, "conditional", StringComparison.Ordinal))
                return match;
            var declaredDecisionOperationId = match.DecisionOperationId
                                              ?? match.Operation.DecisionSourceOperationId;

            if (TryGroundConditionalDecision(
                    evaluation,
                    match,
                    entries,
                    out var decisionOutputPath,
                    out var decisionAllowedValues,
                    out var decisionNoEffectValues,
                    out var decisionContractSource,
                    out var decisionProducerCatalogId,
                    out var decisionProducerOperationId,
                    out var decisionGroundingFailureCode))
            {
                return match with
                {
                    DecisionOperationId = decisionProducerOperationId,
                    DecisionOutputPath = decisionOutputPath,
                    DecisionAllowedValues = decisionAllowedValues,
                    DecisionNoEffectValues = decisionNoEffectValues,
                    DecisionContractSource = decisionContractSource,
                    DecisionProducerCatalogId = decisionProducerCatalogId,
                    DecisionGroundingFailureCode = null,
                    NormalizationReasonCode = string.Equals(
                        decisionContractSource,
                        LocalDecisionContractSource,
                        StringComparison.Ordinal)
                        ? "conditional_local_decision_contract_synthesized"
                        : string.Equals(
                            decisionProducerOperationId,
                            declaredDecisionOperationId,
                            StringComparison.Ordinal)
                            ? match.NormalizationReasonCode
                            : "conditional_decision_source_canonicalized"
                };
            }

            if (string.Equals(match.Operation.ExternalEffectKind, "read", StringComparison.Ordinal))
            {
                return match with
                {
                    Status = "composed",
                    Reason = "No provider-neutral enum output proves that the selected read variants are mutually exclusive, so every selected read remains an unconditional required call.",
                    DecisionOperationId = null,
                    DecisionOutputPath = null,
                    DecisionAllowedValues = null,
                    DecisionNoEffectValues = null,
                    DecisionContractSource = null,
                    DecisionProducerCatalogId = null,
                    DecisionGroundingFailureCode = decisionGroundingFailureCode
                };
            }

            var reason = "Conditional activation has no provider-neutral decision contract that covers every effect branch and declared no-effect outcome.";
            issues.Add(new CapabilityMatchingIssue(
                match.Operation.Id,
                match.Operation.Description,
                match.Operation.Required,
                "contract_gap",
                reason,
                match.CatalogIds)
            {
                ReasonCode = decisionGroundingFailureCode
            });
            return match with
            {
                Status = "invalid",
                Reason = reason,
                DecisionOperationId = decisionProducerOperationId,
                DecisionOutputPath = null,
                DecisionAllowedValues = null,
                DecisionNoEffectValues = null,
                DecisionContractSource = null,
                DecisionProducerCatalogId = null,
                DecisionGroundingFailureCode = decisionGroundingFailureCode
            };
        }).ToArray();

        return CanonicalizeSharedStructuredDecisionOutputPaths(evaluation with
        {
            OperationMatches = matches,
            Issues = issues,
            ContractValid = evaluation.ContractValid && matches.All(static match => match.Status != "invalid")
        });
    }

    private static CapabilityMatchingEvaluation CanonicalizeSharedStructuredDecisionOutputPaths(
        CapabilityMatchingEvaluation evaluation)
    {
        var collisions = evaluation.OperationMatches
            .Where(static match => string.Equals(
                                       match.DecisionContractSource,
                                       StructuredDecisionContractSource,
                                       StringComparison.Ordinal)
                                   && !string.IsNullOrWhiteSpace(match.DecisionOperationId)
                                   && !string.IsNullOrWhiteSpace(match.DecisionProducerCatalogId)
                                   && !string.IsNullOrWhiteSpace(match.DecisionOutputPath))
            .GroupBy(static match => (
                DecisionOperationId: match.DecisionOperationId!,
                ProducerCatalogId: match.DecisionProducerCatalogId!,
                OutputPath: match.DecisionOutputPath!))
            .Where(static group => group.Select(match => match.Operation.Id)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any())
            .SelectMany(static group => group)
            .Select(static match => match.Operation.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (collisions.Count == 0)
            return evaluation;

        var matches = evaluation.OperationMatches.Select(match =>
        {
            if (!collisions.Contains(match.Operation.Id))
                return match;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match.Operation.Id)))
                .ToLowerInvariant()[..16];
            return match with
            {
                DecisionOutputPath = $"/json/conditional_decision_{hash}",
                DecisionOutputPathNormalizationReasonCode = "conditional_decision_output_path_canonicalized"
            };
        }).ToArray();
        return evaluation with { OperationMatches = matches };
    }

    private static bool TryGroundConditionalDecision(
        CapabilityMatchingEvaluation evaluation,
        CapabilityOperationMatch conditionalMatch,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        out string decisionOutputPath,
        out IReadOnlyList<string> allowedValues,
        out IReadOnlyList<string> noEffectValues,
        out string decisionContractSource,
        out string decisionProducerCatalogId,
        out string decisionProducerOperationId,
        out string failureCode)
    {
        decisionOutputPath = string.Empty;
        allowedValues = Array.Empty<string>();
        noEffectValues = Array.Empty<string>();
        decisionContractSource = string.Empty;
        decisionProducerCatalogId = string.Empty;
        decisionProducerOperationId = string.Empty;
        failureCode = string.Empty;
        var decisionOperationId = conditionalMatch.DecisionOperationId
                                  ?? conditionalMatch.Operation.DecisionSourceOperationId;
        if (string.IsNullOrWhiteSpace(decisionOperationId))
        {
            failureCode = "decision_source_missing";
            return false;
        }
        decisionProducerOperationId = decisionOperationId;

        if (!TryBuildConditionalActivation(
                conditionalMatch.CatalogIds.Where(entries.ContainsKey).Select(id => entries[id]).ToArray(),
                conditionalMatch.Operation.AllowNoEffectOutcome,
                conditionalMatch.ConditionalActivationMode,
                out var branches,
                out _))
        {
            failureCode = "conditional_branch_topology_invalid";
            return false;
        }

        var branchValues = branches.Values
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var decisionMatches = ResolveConditionalDecisionProducerCandidates(evaluation, decisionOperationId);
        if (decisionMatches.Count == 1)
            decisionProducerOperationId = decisionMatches[0].Operation.Id;
        var lockedDecisionMatch = evaluation.OperationMatches.FirstOrDefault(match => string.Equals(
            match.Operation.Id,
            decisionOperationId,
            StringComparison.Ordinal));
        var lockedDecisionIsPhysical = lockedDecisionMatch is { CatalogIds.Count: > 0 };

        IReadOnlyList<ConditionalDecisionGrounding> lockedProjected = Array.Empty<ConditionalDecisionGrounding>();
        if (decisionMatches.Count > 0)
        {
            var typed = decisionMatches
                .SelectMany(match => FindTypedConditionalDecisionGroundings(
                    match,
                    entries,
                    branchValues,
                    conditionalMatch.Operation.AllowNoEffectOutcome))
                .GroupBy(static grounding => (
                    grounding.OperationId,
                    grounding.CatalogId,
                    grounding.OutputPath,
                    grounding.ContractSource))
                .Select(static group => group.First())
                .Take(2)
                .ToArray();
            if (typed.Length == 1)
            {
                return SetConditionalDecisionGrounding(
                    typed[0],
                    out decisionOutputPath,
                    out allowedValues,
                    out noEffectValues,
                    out decisionContractSource,
                    out decisionProducerCatalogId,
                    out decisionProducerOperationId);
            }
            if (typed.Length > 1)
            {
                if (TryCreateLocalDecisionGrounding(
                        evaluation,
                        conditionalMatch,
                        entries,
                        branchValues,
                        out var localGrounding))
                {
                    return SetConditionalDecisionGrounding(
                        localGrounding,
                        out decisionOutputPath,
                        out allowedValues,
                        out noEffectValues,
                        out decisionContractSource,
                        out decisionProducerCatalogId,
                        out decisionProducerOperationId);
                }
                failureCode = "conditional_decision_source_ambiguous";
                return false;
            }

            if (lockedDecisionIsPhysical)
            {
                lockedProjected = FindProjectedConditionalDecisionGroundings(
                    lockedDecisionMatch!,
                    entries,
                    branchValues,
                    conditionalMatch.Operation.AllowNoEffectOutcome);
                if (lockedProjected.Count > 1)
                {
                    failureCode = "conditional_decision_source_ambiguous";
                    return false;
                }
            }
        }

        var declaredUpstream = conditionalMatch.Operation.InputOperationIds
            .Where(operationId => !string.Equals(operationId, decisionOperationId, StringComparison.Ordinal))
            .SelectMany(operationId => ResolveConditionalDecisionProducerCandidates(evaluation, operationId))
            .GroupBy(static match => match.Operation.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        var upstreamTyped = declaredUpstream
            .SelectMany(match => FindTypedConditionalDecisionGroundings(
                match,
                entries,
                branchValues,
                conditionalMatch.Operation.AllowNoEffectOutcome))
            .Take(2)
            .ToArray();
        if (upstreamTyped.Length == 1)
        {
            return SetConditionalDecisionGrounding(
                upstreamTyped[0],
                out decisionOutputPath,
                out allowedValues,
                out noEffectValues,
                out decisionContractSource,
                out decisionProducerCatalogId,
                out decisionProducerOperationId);
        }
        if (upstreamTyped.Length > 1)
        {
            if (TryCreateLocalDecisionGrounding(
                    evaluation,
                    conditionalMatch,
                    entries,
                    branchValues,
                    out var localGrounding))
            {
                return SetConditionalDecisionGrounding(
                    localGrounding,
                    out decisionOutputPath,
                    out allowedValues,
                    out noEffectValues,
                    out decisionContractSource,
                    out decisionProducerCatalogId,
                    out decisionProducerOperationId);
            }
            failureCode = "conditional_decision_source_ambiguous";
            return false;
        }

        if (lockedProjected.Count == 1)
        {
            return SetConditionalDecisionGrounding(
                lockedProjected[0],
                out decisionOutputPath,
                out allowedValues,
                out noEffectValues,
                out decisionContractSource,
                out decisionProducerCatalogId,
                out decisionProducerOperationId);
        }

        var semanticCandidates = lockedDecisionIsPhysical
            ? declaredUpstream
            : decisionMatches.Concat(declaredUpstream)
                .GroupBy(static match => match.Operation.Id, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
        var projected = FindConditionalDecisionSemanticRootGroundings(
            semanticCandidates,
            entries,
            branchValues,
            conditionalMatch.Operation.AllowNoEffectOutcome);
        if (projected.Count == 1)
        {
            return SetConditionalDecisionGrounding(
                projected[0],
                out decisionOutputPath,
                out allowedValues,
                out noEffectValues,
                out decisionContractSource,
                out decisionProducerCatalogId,
                out decisionProducerOperationId);
        }

        if (TryCreateLocalDecisionGrounding(
                evaluation,
                conditionalMatch,
                entries,
                branchValues,
                out var synthesizedLocalGrounding))
        {
            return SetConditionalDecisionGrounding(
                synthesizedLocalGrounding,
                out decisionOutputPath,
                out allowedValues,
                out noEffectValues,
                out decisionContractSource,
                out decisionProducerCatalogId,
                out decisionProducerOperationId);
        }

        failureCode = projected.Count > 1
            ? "conditional_decision_source_ambiguous"
            : "conditional_decision_source_unavailable";
        return false;
    }

    private static bool TryCreateLocalDecisionGrounding(
        CapabilityMatchingEvaluation evaluation,
        CapabilityOperationMatch conditionalMatch,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        IReadOnlyList<string> branchValues,
        out ConditionalDecisionGrounding grounding)
    {
        grounding = null!;
        var decisionOperationId = conditionalMatch.DecisionOperationId
                                  ?? conditionalMatch.Operation.DecisionSourceOperationId;
        var decisionMatch = evaluation.OperationMatches.FirstOrDefault(match =>
            string.Equals(match.Operation.Id, decisionOperationId, StringComparison.Ordinal));
        if (decisionMatch is null
            || !string.Equals(decisionMatch.Status, "local", StringComparison.Ordinal)
            || !string.Equals(decisionMatch.Operation.ExecutionKind, "local_processing", StringComparison.Ordinal))
        {
            return false;
        }

        var upstreamIds = decisionMatch.Operation.InputOperationIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (upstreamIds.Length < 2)
            return false;
        var matchesByOperation = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        if (upstreamIds.Any(id => !matchesByOperation.TryGetValue(id, out var upstream)
                                  || upstream.Status is "invalid" or "ambiguous" or "unavailable"
                                  || string.Equals(upstream.Status, "local", StringComparison.Ordinal)
                                  || upstream.CatalogIds.Count == 0))
        {
            return false;
        }

        var evaluator = entries.Values.SingleOrDefault(static entry =>
            string.Equals(entry.Resolution, "native", StringComparison.Ordinal)
            && string.Equals(entry.Method, LocalDecisionStepType, StringComparison.Ordinal));
        if (evaluator is null)
            return false;

        var noEffectValues = conditionalMatch.Operation.AllowNoEffectOutcome
            ? new[] { CreateUniqueNoEffectDecisionValue(branchValues) }
            : Array.Empty<string>();
        var allowedValues = branchValues.Concat(noEffectValues)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conditionalMatch.Operation.Id)))
            .ToLowerInvariant()[..16];
        grounding = new ConditionalDecisionGrounding(
            decisionMatch.Operation.Id,
            evaluator.Id,
            $"/conditional_decision_{hash}",
            allowedValues,
            noEffectValues,
            LocalDecisionContractSource);
        return true;
    }

    private static IReadOnlyList<ConditionalDecisionGrounding> FindConditionalDecisionSemanticRootGroundings(
        IEnumerable<CapabilityOperationMatch> decisionMatches,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        IReadOnlyList<string> branchValues,
        bool allowNoEffectOutcome)
    {
        var projected = decisionMatches
            .SelectMany(match => FindProjectedConditionalDecisionGroundings(
                match,
                entries,
                branchValues,
                allowNoEffectOutcome))
            .GroupBy(static grounding => (
                grounding.OperationId,
                grounding.CatalogId,
                grounding.OutputPath,
                grounding.ContractSource))
            .Select(static group => group.First())
            .Take(32)
            .ToArray();
        if (projected.Length <= 1)
            return projected;

        var artifactRoots = projected
            .Where(grounding => entries.TryGetValue(grounding.CatalogId, out var entry)
                                && entry.ArtifactContract?.Consumes.Any(static artifact => artifact.Required) == true)
            .Select(grounding => new
            {
                Grounding = grounding,
                RequiredKinds = entries[grounding.CatalogId].ArtifactContract!.Consumes
                    .Where(static artifact => artifact.Required)
                    .Select(static artifact => artifact.Kind)
                    .ToHashSet(StringComparer.Ordinal)
            })
            .ToArray();
        var maximalRoots = artifactRoots
            .Where(candidate => !artifactRoots.Any(other =>
                !ReferenceEquals(candidate, other)
                && other.RequiredKinds.IsProperSupersetOf(candidate.RequiredKinds)))
            .Select(static candidate => candidate.Grounding)
            .Take(2)
            .ToArray();
        return maximalRoots.Length == 1 ? maximalRoots : projected.Take(2).ToArray();
    }

    private static IReadOnlyList<ConditionalDecisionGrounding> FindTypedConditionalDecisionGroundings(
        CapabilityOperationMatch decisionMatch,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        IReadOnlyList<string> branchValues,
        bool allowNoEffectOutcome)
        => decisionMatch.CatalogIds
            .Where(entries.ContainsKey)
            .SelectMany(id => entries[id].Outputs.Select(field => (CatalogId: id, Field: field)))
            .Where(candidate => ConditionalDecisionEnumCoversBranches(
                candidate.Field.EnumValues,
                branchValues,
                allowNoEffectOutcome))
            .GroupBy(static candidate => (candidate.CatalogId, candidate.Field.Path))
            .Select(group =>
            {
                var candidate = group.First();
                var declaredValues = candidate.Field.EnumValues
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new ConditionalDecisionGrounding(
                    decisionMatch.Operation.Id,
                    candidate.CatalogId,
                    candidate.Field.Path,
                    declaredValues,
                    declaredValues.Except(branchValues, StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    CapabilityDecisionContractSource);
            })
            .Take(2)
            .ToArray();

    private static IReadOnlyList<ConditionalDecisionGrounding> FindProjectedConditionalDecisionGroundings(
        CapabilityOperationMatch decisionMatch,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> entries,
        IReadOnlyList<string> branchValues,
        bool allowNoEffectOutcome)
    {
        if (!string.Equals(
                decisionMatch.Operation.ExecutionKind,
                "external_effect",
                StringComparison.Ordinal))
        {
            return Array.Empty<ConditionalDecisionGrounding>();
        }

        var selected = decisionMatch.CatalogIds
            .Distinct(StringComparer.Ordinal)
            .Where(entries.ContainsKey)
            .Select(id => entries[id])
            .ToArray();
        if (selected.Length == 0)
            return Array.Empty<ConditionalDecisionGrounding>();

        var prerequisiteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in selected)
        {
            foreach (var requirement in GetRequiredArtifactRequirements(consumer))
            {
                foreach (var producer in selected.Where(entry => CapabilityProducesArtifactKind(entry, requirement.Kind)))
                    prerequisiteIds.Add(producer.Id);
            }
        }

        var roots = selected
            .Where(entry => !prerequisiteIds.Contains(entry.Id))
            .Where(static entry => !IsArtifactMaterializer(entry))
            .Where(SupportsStructuredDecisionProjection)
            .Take(2)
            .ToArray();
        if (roots.Length == 0
            && selected.Length == 1
            && !IsArtifactMaterializer(selected[0])
            && SupportsStructuredDecisionProjection(selected[0]))
            roots = selected;

        var synthesizedNoEffectValues = allowNoEffectOutcome
            ? new[] { CreateUniqueNoEffectDecisionValue(branchValues) }
            : Array.Empty<string>();
        var allowed = branchValues.Concat(synthesizedNoEffectValues)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return roots.Select(root => new ConditionalDecisionGrounding(
                decisionMatch.Operation.Id,
                root.Id,
                "/json/decision",
                allowed,
                synthesizedNoEffectValues,
                StructuredDecisionContractSource))
            .ToArray();
    }

    private static bool SetConditionalDecisionGrounding(
        ConditionalDecisionGrounding grounding,
        out string decisionOutputPath,
        out IReadOnlyList<string> allowedValues,
        out IReadOnlyList<string> noEffectValues,
        out string decisionContractSource,
        out string decisionProducerCatalogId,
        out string decisionProducerOperationId)
    {
        decisionOutputPath = grounding.OutputPath;
        allowedValues = grounding.AllowedValues;
        noEffectValues = grounding.NoEffectValues;
        decisionContractSource = grounding.ContractSource;
        decisionProducerCatalogId = grounding.CatalogId;
        decisionProducerOperationId = grounding.OperationId;
        return true;
    }

    private static IReadOnlyList<CapabilityOperationMatch> ResolveConditionalDecisionProducerCandidates(
        CapabilityMatchingEvaluation evaluation,
        string decisionOperationId)
    {
        var matches = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        return ResolveConditionalDecisionProducerCandidates(
            matches,
            decisionOperationId,
            new HashSet<string>(StringComparer.Ordinal))
            .GroupBy(static match => match.Operation.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(32)
            .ToArray();
    }

    private static IReadOnlyList<CapabilityOperationMatch> ResolveConditionalDecisionProducerCandidates(
        IReadOnlyDictionary<string, CapabilityOperationMatch> matches,
        string operationId,
        HashSet<string> visited)
    {
        if (!visited.Add(operationId) || !matches.TryGetValue(operationId, out var current))
            return Array.Empty<CapabilityOperationMatch>();
        if (current.CatalogIds.Count > 0)
            return [current];
        if (!string.Equals(current.Status, "local", StringComparison.Ordinal)
            || !string.Equals(current.Operation.ExecutionKind, "local_processing", StringComparison.Ordinal))
        {
            return Array.Empty<CapabilityOperationMatch>();
        }

        IReadOnlyList<string> declaredDependencies = current.Operation.InputOperationIds.Count > 0
            ? current.Operation.InputOperationIds
            : string.IsNullOrWhiteSpace(current.Operation.DecisionSourceOperationId)
                ? Array.Empty<string>()
                : [current.Operation.DecisionSourceOperationId];
        if (declaredDependencies.Count == 0)
            return Array.Empty<CapabilityOperationMatch>();

        return declaredDependencies
            .SelectMany(dependencyId => ResolveConditionalDecisionProducerCandidates(
                matches,
                dependencyId,
                new HashSet<string>(visited, StringComparer.Ordinal)))
            .GroupBy(static match => match.Operation.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(32)
            .ToArray();
    }

    private static bool ConditionalDecisionEnumCoversBranches(
        IReadOnlyList<string> enumValues,
        IReadOnlyList<string> branchValues,
        bool allowNoEffectOutcome)
    {
        var declared = enumValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (declared.Length == 0 || branchValues.Any(value => !declared.Contains(value, StringComparer.Ordinal)))
            return false;

        return allowNoEffectOutcome
            ? declared.Length > branchValues.Count
            : declared.SequenceEqual(branchValues.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool SupportsStructuredDecisionProjection(CapabilityCatalogEntry entry)
        => string.Equals(entry.Resolution, "mcp", StringComparison.Ordinal)
           || string.Equals(entry.Resolution, "native", StringComparison.Ordinal)
           && string.Equals(entry.Method, "llm.call", StringComparison.Ordinal);

    private static string CreateUniqueNoEffectDecisionValue(IReadOnlyList<string> branchValues)
    {
        var candidate = SynthesizedNoEffectDecisionValue;
        var suffix = 2;
        while (branchValues.Contains(candidate, StringComparer.Ordinal))
            candidate = $"{SynthesizedNoEffectDecisionValue}_{suffix++}";
        return candidate;
    }

    private static void RecordConditionalGroundingTelemetry(
        ITelemetrySpan span,
        CapabilityMatchingEvaluation evaluation,
        string attempt)
    {
        var conditionalMatches = evaluation.OperationMatches
            .Where(static match => !string.IsNullOrWhiteSpace(match.DecisionOperationId)
                                   || !string.IsNullOrWhiteSpace(match.Operation.DecisionSourceOperationId))
            .ToArray();
        span.SetAttribute($"gnougo-flow.plan.capability_matching.{attempt}.conditional_count", conditionalMatches.Length);
        span.SetAttribute(
            $"gnougo-flow.plan.capability_matching.{attempt}.conditional_grounded_count",
            conditionalMatches.Count(static match => !string.IsNullOrWhiteSpace(match.DecisionOutputPath)));
        span.SetAttribute(
            $"gnougo-flow.plan.capability_matching.{attempt}.conditional_contract_gap_count",
            conditionalMatches.Count(static match => !string.IsNullOrWhiteSpace(match.DecisionGroundingFailureCode)));

        foreach (var match in conditionalMatches.Take(32))
        {
            var decisionOperationId = match.DecisionOperationId ?? match.Operation.DecisionSourceOperationId;
            var decisionMatch = evaluation.OperationMatches.FirstOrDefault(candidate => string.Equals(
                candidate.Operation.Id,
                decisionOperationId,
                StringComparison.Ordinal));
            span.AddEvent("gnougo-flow.plan.capability_matching.conditional_grounding", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.attempt", attempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.operation_id", SanitizeCapabilityInferenceDiagnostic(match.Operation.Id, 160)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_operation_id", SanitizeCapabilityInferenceDiagnostic(decisionOperationId, 160)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_catalog_ids", string.Join(',', decisionMatch?.CatalogIds.Take(8) ?? Array.Empty<string>())),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.branch_catalog_ids", string.Join(',', match.CatalogIds.Take(8))),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.decision_output_path", match.DecisionOutputPath ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.allowed_values", string.Join('|', match.DecisionAllowedValues ?? Array.Empty<string>())),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.no_effect_values", string.Join('|', match.DecisionNoEffectValues ?? Array.Empty<string>())),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.contract_source", match.DecisionContractSource ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.producer_catalog_id", match.DecisionProducerCatalogId ?? string.Empty),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_matching.grounding_failure_code", match.DecisionGroundingFailureCode ?? string.Empty)
            });
        }
    }

    private async Task<CapabilityMatchingEvaluation> ReviewCapabilityCoverageAndRematchAsync(
        StepExecutionContext ctx,
        JsonObject input,
        ILLMClient llmClient,
        CapabilityInventory inventory,
        CapabilityCatalog catalog,
        CapabilityMatchingEvaluation evaluation,
        string? provider,
        string model,
        string reasoning,
        TelemetrySpanScope inferenceSpan,
        IntentClarificationSession? intentClarification,
        CancellationToken ct)
    {
        var reviewTargets = evaluation.OperationMatches
            .Where(static match => match.Operation.Required
                                   && GetCapabilityContractCoverageRequirements(match.Operation).Count > 0
                                   && match.Status is "matched" or "composed" or "conditional")
            .ToArray();
        if (reviewTargets.Length == 0)
        {
            inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.reviewed_count", 0);
            return evaluation;
        }

        var review = await RequestCapabilityCoverageReviewAsync(
            ctx,
            llmClient,
            catalog,
            reviewTargets,
            provider,
            model,
            reasoning,
            inferenceSpan,
            ct);
        var gaps = review.Diagnostics
            .Where(static diagnostic => string.Equals(diagnostic.Status, "incomplete", StringComparison.Ordinal))
            .ToArray();
        RecordCapabilityCoverageTelemetry(ctx, review.Diagnostics, "initial");
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.reviewed_count", reviewTargets.Length);
        var initiallyIncompleteCount = gaps.Length;
        if (gaps.Length > 0)
        {
            gaps = await RetainIntrinsicCapabilityCoverageGapsAsync(
                ctx,
                llmClient,
                catalog,
                reviewTargets,
                gaps,
                provider,
                model,
                reasoning,
                inferenceSpan,
                "initial",
                ct);
        }
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.incomplete_count", initiallyIncompleteCount);
        inferenceSpan.SetAttribute("gnougo-flow.plan.capability_coverage.intrinsic_gap_count", gaps.Length);
        if (gaps.Length == 0)
            return evaluation;

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message",
                $"Capability coverage review found {gaps.Length} incomplete operation match(es); performing one targeted rematch."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
        });

        var affectedOperationIds = gaps
            .Select(static gap => gap.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var rematchResponse = await ctx.CallLLMAsync(llmClient, new LLMRequest
        {
            Provider = provider,
            Model = model,
            Prompt = BuildCapabilityCoverageRematchPrompt(inventory, catalog, evaluation, gaps),
            Reasoning = reasoning,
            UseBackgroundMode = true,
            StructuredOutputSchema = BuildCapabilityMatchingSchema(),
            StructuredOutputStrict = true
        }, "workflow.plan.capability_coverage_rematch", ct);
        AddUsageAttributes(inferenceSpan, rematchResponse.Usage, model, provider);

        CapabilityMatchingEvaluation rematched;
        try
        {
            rematched = ParseCapabilityMatchingEvaluation(
                ParseStructuredObject(rematchResponse, "capability coverage rematch"),
                inventory,
                catalog);
            rematched = NormalizeLocalProcessingMatches(rematched);
            rematched = NormalizeCapabilityCompositionMatches(rematched, catalog);
            rematched = NormalizeConditionalSelectorMatches(rematched, catalog, inventory);
            var userInstruction = input["raw_prompt"]?.GetValue<string>()
                                  ?? (input["generator"] as JsonObject)?["raw_prompt"]?.GetValue<string>()
                                  ?? (input["generator"] as JsonObject)?["instruction"]?.GetValue<string>()
                                  ?? string.Empty;
            rematched = EnforceCapabilityPrerequisiteClosure(rematched, catalog);
            rematched = NormalizePlatformSafetyMatches(rematched, catalog);
            rematched = PreserveUnaffectedCapabilityMatches(evaluation, rematched, affectedOperationIds);
            rematched = CanonicalizeSharedStructuredDecisionOutputPaths(rematched);
            RecordCapabilityMatchingNormalizationTelemetry(inferenceSpan, rematched, "coverage_rematch");
            RecordConditionalGroundingTelemetry(inferenceSpan.Span, rematched, "coverage_rematch");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability coverage rematching returned an invalid contract.",
                inner: ex,
                details: new JsonObject
                {
                    ["phase"] = "capability_coverage_rematch",
                    ["classification"] = "contract_violation"
                });
        }

        if (!rematched.ContractValid)
            ThrowForUnresolvedCapabilityMatches(rematched, catalog, repairAttempted: true);

        var unresolvedAffected = rematched.OperationMatches
            .Where(match => affectedOperationIds.Contains(match.Operation.Id)
                            && match.Status is not ("matched" or "composed" or "conditional"))
            .ToArray();
        if (unresolvedAffected.Length == 0)
        {
            var rematchedTargets = rematched.OperationMatches
                .Where(match => affectedOperationIds.Contains(match.Operation.Id))
                .ToArray();
            review = await RequestCapabilityCoverageReviewAsync(
                ctx,
                llmClient,
                catalog,
                rematchedTargets,
                provider,
                model,
                reasoning,
                inferenceSpan,
                ct);
            gaps = review.Diagnostics
                .Where(static diagnostic => string.Equals(diagnostic.Status, "incomplete", StringComparison.Ordinal))
                .ToArray();
            RecordCapabilityCoverageTelemetry(ctx, review.Diagnostics, "rematch");
            if (gaps.Length > 0)
            {
                gaps = await RetainIntrinsicCapabilityCoverageGapsAsync(
                    ctx,
                    llmClient,
                    catalog,
                    rematchedTargets,
                    gaps,
                    provider,
                    model,
                    reasoning,
                    inferenceSpan,
                    "rematch",
                    ct);
            }
        }

        if (gaps.Length > 0 || unresolvedAffected.Length > 0)
        {
            await RequestCapabilityRelaxationOrThrowAsync(
                ctx,
                intentClarification,
                gaps,
                unresolvedAffected,
                evaluation,
                ct);
        }

        return rematched;
    }

    private async Task<CapabilityCoverageDiagnostic[]> RetainIntrinsicCapabilityCoverageGapsAsync(
        StepExecutionContext ctx,
        ILLMClient llmClient,
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps,
        string? provider,
        string model,
        string reasoning,
        TelemetrySpanScope inferenceSpan,
        string stage,
        CancellationToken ct)
    {
        var adjudication = await RequestCapabilityCoverageGapAdjudicationAsync(
            ctx,
            llmClient,
            catalog,
            targets,
            gaps,
            provider,
            model,
            reasoning,
            inferenceSpan,
            ct);
        var adjudicationByOperation = adjudication.Adjudications
            .ToDictionary(static item => item.OperationId, StringComparer.Ordinal);
        foreach (var item in adjudication.Adjudications)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.capability_coverage.gap_adjudication", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.operation_id", item.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.requirement_id", item.RequirementId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.catalog_id", item.CatalogId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.classification", item.Classification),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.structural_facets", string.Join(',', item.StructuralFacets)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.reason_code",
                    string.Equals(item.Classification, WorkflowStructureOnlyCoverageClassification, StringComparison.Ordinal)
                        ? "capability_coverage_workflow_structure_canonicalized"
                        : "capability_coverage_intrinsic_gap_confirmed")
            });
        }

        return gaps
            .Where(gap => adjudicationByOperation.TryGetValue(gap.OperationId, out var item)
                          && string.Equals(
                              item.Classification,
                              IntrinsicPrimitiveMissingCoverageClassification,
                              StringComparison.Ordinal))
            .ToArray();
    }

    private static IReadOnlyList<CapabilityEvidenceAnchor> GetCapabilityContractCoverageRequirements(
        CapabilityInventoryOperation operation)
        => operation.CoverageRequirementEvidence
            .Where(requirement => !operation.WorkflowStructureCoverageRequirementIds.Contains(requirement.Id))
            .ToArray();

    private static void RecordCapabilityCoverageTelemetry(
        StepExecutionContext ctx,
        IReadOnlyList<CapabilityCoverageDiagnostic> diagnostics,
        string stage)
    {
        foreach (var diagnostic in diagnostics)
        {
            ctx.AddTelemetryEvent("gnougo-flow.plan.capability_coverage.review", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.operation_id", diagnostic.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.status", diagnostic.Status),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.unsupported_requirement_id", diagnostic.UnsupportedRequirementId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.candidate_catalog_ids", string.Join(',', diagnostic.CandidateCatalogIds)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.evidence_qualified", diagnostic.EvidenceQualified)
            });
        }
    }

    private async Task<CapabilityCoverageReview> RequestCapabilityCoverageReviewAsync(
        StepExecutionContext ctx,
        ILLMClient llmClient,
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        string? provider,
        string model,
        string reasoning,
        TelemetrySpanScope inferenceSpan,
        CancellationToken ct)
    {
        var accepted = new Dictionary<string, CapabilityCoverageDiagnostic>(StringComparer.Ordinal);
        IReadOnlyList<CapabilityOperationMatch> pendingTargets = targets;
        CapabilityCoverageReview? lastReview = null;
        JsonObject? lastCandidate = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityCoverageReviewPrompt(
                    catalog,
                    pendingTargets,
                    lastReview,
                    lastCandidate),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildCapabilityCoverageReviewSchema(catalog, pendingTargets),
                StructuredOutputStrict = true
            }, attempt == 1
                ? "workflow.plan.capability_coverage_review"
                : "workflow.plan.capability_coverage_review_repair", ct);
            AddUsageAttributes(inferenceSpan, response.Usage, model, provider);
            try
            {
                lastCandidate = ParseStructuredObject(response, "capability coverage review");
                var review = ParseCapabilityCoverageReview(
                    lastCandidate,
                    catalog,
                    pendingTargets);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "initial" : "repair",
                    review.Issues);
                foreach (var diagnostic in review.Diagnostics.Where(static diagnostic => diagnostic.EvidenceQualified))
                    accepted[diagnostic.OperationId] = diagnostic;
                if (review.ContractValid)
                {
                    var diagnostics = targets
                        .Select(target => accepted[target.Operation.Id])
                        .ToArray();
                    return new CapabilityCoverageReview(
                        diagnostics,
                        true,
                        Array.Empty<CapabilityCoverageContractIssue>());
                }
                lastReview = review;
                pendingTargets = targets
                    .Where(target => !accepted.ContainsKey(target.Operation.Id))
                    .ToArray();
                if (pendingTargets.Count == 0)
                    pendingTargets = targets;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var issue = new CapabilityCoverageContractIssue(
                    "structured_response_invalid",
                    string.Empty,
                    "$",
                    null,
                    RequirementId: BuildCapabilityEvidenceId(
                        ex.GetType().Name,
                        -1,
                        0,
                        ex.Message));
                lastReview = new CapabilityCoverageReview(
                    Array.Empty<CapabilityCoverageDiagnostic>(),
                    false,
                    [issue]);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "initial" : "repair",
                    lastReview.Issues);
                pendingTargets = targets;
            }
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability coverage review remained invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_coverage_review",
                ["classification"] = "model_contract_violation",
                ["attempts"] = 2,
                ["contract_issues"] = new JsonArray((lastReview?.Issues
                        ?? Array.Empty<CapabilityCoverageContractIssue>())
                    .Select(static issue => (JsonNode)BuildCapabilityCoverageContractIssueJson(issue))
                    .ToArray()),
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "retry_or_change_planning_model"
            });
    }

    private async Task<CapabilityCoverageGapAdjudicationReview> RequestCapabilityCoverageGapAdjudicationAsync(
        StepExecutionContext ctx,
        ILLMClient llmClient,
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps,
        string? provider,
        string model,
        string reasoning,
        TelemetrySpanScope inferenceSpan,
        CancellationToken ct)
    {
        CapabilityCoverageGapAdjudicationReview? lastReview = null;
        JsonObject? lastCandidate = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityCoverageGapAdjudicationPrompt(
                    catalog,
                    targets,
                    gaps,
                    lastReview,
                    lastCandidate),
                Reasoning = reasoning,
                UseBackgroundMode = true,
                StructuredOutputSchema = BuildCapabilityCoverageGapAdjudicationSchema(catalog, targets, gaps),
                StructuredOutputStrict = true
            }, attempt == 1
                ? "workflow.plan.capability_coverage_gap_adjudication"
                : "workflow.plan.capability_coverage_gap_adjudication_repair", ct);
            AddUsageAttributes(inferenceSpan, response.Usage, model, provider);
            try
            {
                lastCandidate = ParseStructuredObject(response, "capability coverage gap adjudication");
                lastReview = ParseCapabilityCoverageGapAdjudication(
                    lastCandidate,
                    catalog,
                    targets,
                    gaps);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "gap_adjudication_initial" : "gap_adjudication_repair",
                    lastReview.Issues);
                if (lastReview.ContractValid)
                    return lastReview;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastReview = new CapabilityCoverageGapAdjudicationReview(
                    Array.Empty<CapabilityCoverageGapAdjudication>(),
                    false,
                    [new CapabilityCoverageContractIssue(
                        "structured_response_invalid",
                        string.Empty,
                        "$",
                        null,
                        RequirementId: BuildCapabilityEvidenceId(
                            ex.GetType().Name,
                            -1,
                            0,
                            ex.Message))]);
                RecordCapabilityCoverageContractTelemetry(
                    inferenceSpan,
                    attempt == 1 ? "gap_adjudication_initial" : "gap_adjudication_repair",
                    lastReview.Issues);
            }
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightInferenceFailed,
            "Capability coverage gap adjudication remained invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_coverage_gap_adjudication",
                ["classification"] = "model_contract_violation",
                ["attempts"] = 2,
                ["contract_issues"] = new JsonArray((lastReview?.Issues
                        ?? Array.Empty<CapabilityCoverageContractIssue>())
                    .Select(static issue => (JsonNode)BuildCapabilityCoverageContractIssueJson(issue))
                    .ToArray()),
                ["planning_outcome"] = "cannot_plan_safely",
                ["recommended_action"] = "retry_or_change_planning_model"
            });
    }

    private static string BuildCapabilityCoverageGapAdjudicationPrompt(
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps,
        CapabilityCoverageGapAdjudicationReview? previous,
        JsonObject? rejectedCandidate)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var targetsById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        var operations = new JsonArray(gaps.Select(gap =>
        {
            var target = targetsById[gap.OperationId];
            return (JsonNode)new JsonObject
            {
                ["operation_id"] = gap.OperationId,
                ["requirement_id"] = gap.UnsupportedRequirementId,
                ["requirement_excerpt"] = gap.UnsupportedRequirement,
                ["prior_weaker_behavior"] = gap.SupportedWeakerBehavior,
                ["selected_catalog_ids"] = new JsonArray(target.CatalogIds
                    .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["selected_cards"] = new JsonArray(target.CatalogIds
                    .Where(entries.ContainsKey)
                    .Select(id => (JsonNode)new JsonObject
                    {
                        ["catalog_id"] = id,
                        ["card"] = BuildCapabilityCoverageCard(entries[id], catalog)
                    }).ToArray()),
                ["prior_evidence"] = new JsonArray(gap.Evidence.Select(static evidence => (JsonNode)new JsonObject
                {
                    ["catalog_id"] = evidence.CatalogId,
                    ["catalog_excerpt"] = evidence.CatalogExcerpt
                }).ToArray())
            };
        }).ToArray());
        var previousNotice = previous is { ContractValid: false }
            ? $$"""
                The previous adjudication violated the deterministic evidence contract. Correct every listed field and return every supplied operation exactly once.
                <previous_contract_issues>
                {{BuildCapabilityCoverageContractIssuesJson(previous.Issues)}}
                </previous_contract_issues>
                <rejected_adjudication_candidate>
                {{BuildRejectedCapabilityCoverageCandidate(rejectedCandidate, previous.Issues)}}
                </rejected_adjudication_candidate>
                """
            : string.Empty;
        return $$"""
            You are a provider-neutral capability coverage gap adjudicator. Return only the requested structured JSON.

            A prior reviewer found a capability-contract gap. Decide whether the selected cards actually omit an intrinsic observable primitive, or whether their documented primitive is sufficient and the only remaining differences belong to workflow structure. Use only the supplied requirement, selected cards, schemas, selectors, outputs, artifact contracts, composition metadata, and prior grounded evidence. Never infer behavior from provider, server, tool, method, product, URL, catalog numbering, operation prose, or domain names.

            Classify intrinsic_primitive_missing only when the required observable state transition itself is absent or a genuinely different primitive is documented. Classify workflow_structure_only when the same intrinsic primitive is documented and every remaining difference is one or more of these structural facets: cardinality, uniqueness, complete-scope or per-item iteration, ordering, conditions, confirmation, finalization, failure/cancellation policy, quality thresholds, caller-specific runtime arguments, input representation, or locally derivable mapping. A generic parameterized capability performs one invocation; workflow structure may invoke it for each item, supply runtime values, order it, guard it, and place it in finalization. Those facts do not make its primitive weaker.

            Return exactly one adjudication per supplied operation_id. Copy requirement_id exactly. For workflow_structure_only, return every applicable structural facet using only the schema enum. For intrinsic_primitive_missing, return an empty structural_facets array. Ground each decision with one exact non-empty catalog_excerpt copied verbatim from one selected card for that operation. Do not paraphrase evidence.

            {{previousNotice}}
            <coverage_gap_operations>
            {{operations.ToJsonString()}}
            </coverage_gap_operations>
            """;
    }

    private static JsonObject BuildCapabilityCoverageGapAdjudicationSchema(
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps)
    {
        var targetById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        var operationIds = gaps.Select(static gap => (JsonNode?)JsonValue.Create(gap.OperationId)).ToArray();
        var requirementIds = gaps.Select(static gap => (JsonNode?)JsonValue.Create(gap.UnsupportedRequirementId)).ToArray();
        var catalogIds = gaps
            .Where(gap => targetById.ContainsKey(gap.OperationId))
            .SelectMany(gap => targetById[gap.OperationId].CatalogIds)
            .Where(id => catalog.Entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["adjudications"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = gaps.Count,
                    ["maxItems"] = gaps.Count,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["operation_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(operationIds)
                            },
                            ["requirement_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(requirementIds)
                            },
                            ["classification"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(
                                    IntrinsicPrimitiveMissingCoverageClassification,
                                    WorkflowStructureOnlyCoverageClassification)
                            },
                            ["structural_facets"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["maxItems"] = CapabilityCoverageStructuralFacets.Length,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JsonArray(CapabilityCoverageStructuralFacets
                                        .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray())
                                }
                            },
                            ["catalog_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(catalogIds)
                            },
                            ["catalog_excerpt"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["minLength"] = 1,
                                ["maxLength"] = CapabilityDescriptionMaxCharacters
                            }
                        },
                        ["required"] = new JsonArray(
                            "operation_id",
                            "requirement_id",
                            "classification",
                            "structural_facets",
                            "catalog_id",
                            "catalog_excerpt"),
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new JsonArray("adjudications"),
            ["additionalProperties"] = false
        };
    }

    private static CapabilityCoverageGapAdjudicationReview ParseCapabilityCoverageGapAdjudication(
        JsonObject json,
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps)
    {
        var issues = new List<CapabilityCoverageContractIssue>();
        var adjudications = new List<CapabilityCoverageGapAdjudication>();
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var targetsById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        var gapsByOperation = gaps.ToDictionary(static gap => gap.OperationId, StringComparer.Ordinal);
        if (json["adjudications"] is not JsonArray nodes)
        {
            return new CapabilityCoverageGapAdjudicationReview(
                adjudications,
                false,
                [new CapabilityCoverageContractIssue("adjudications_shape_invalid", string.Empty, "adjudications", null)]);
        }
        if (nodes.Count != gaps.Count)
            issues.Add(new CapabilityCoverageContractIssue("adjudication_count_mismatch", string.Empty, "adjudications", null));

        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is not JsonObject node)
            {
                issues.Add(new CapabilityCoverageContractIssue("adjudication_shape_invalid", string.Empty, "adjudications", index));
                continue;
            }

            var operationId = ReadCapabilityCoverageString(node, "operation_id");
            if (!gapsByOperation.TryGetValue(operationId, out var gap)
                || !targetsById.TryGetValue(operationId, out var target))
            {
                issues.Add(new CapabilityCoverageContractIssue("operation_unknown", operationId, "operation_id", index));
                continue;
            }
            if (adjudications.Any(item => string.Equals(item.OperationId, operationId, StringComparison.Ordinal)))
            {
                issues.Add(new CapabilityCoverageContractIssue("operation_duplicate", operationId, "operation_id", index));
                continue;
            }

            var itemIssues = new List<CapabilityCoverageContractIssue>();
            var requirementId = ReadCapabilityCoverageString(node, "requirement_id");
            if (!string.Equals(requirementId, gap.UnsupportedRequirementId, StringComparison.Ordinal))
                itemIssues.Add(new CapabilityCoverageContractIssue("requirement_id_mismatch", operationId, "requirement_id", index, RequirementId: requirementId));
            var classification = ReadCapabilityCoverageString(node, "classification").ToLowerInvariant();
            if (classification is not (IntrinsicPrimitiveMissingCoverageClassification or WorkflowStructureOnlyCoverageClassification))
                itemIssues.Add(new CapabilityCoverageContractIssue("classification_invalid", operationId, "classification", index));

            var structuralFacets = ReadCapabilityCoverageIds(
                node["structural_facets"],
                CapabilityCoverageStructuralFacets.Length,
                out var structuralFacetsValid);
            structuralFacetsValid &= structuralFacets.All(facet => CapabilityCoverageStructuralFacets.Contains(facet, StringComparer.Ordinal));
            structuralFacetsValid &= classification switch
            {
                WorkflowStructureOnlyCoverageClassification => structuralFacets.Count > 0,
                IntrinsicPrimitiveMissingCoverageClassification => structuralFacets.Count == 0,
                _ => false
            };
            if (!structuralFacetsValid)
                itemIssues.Add(new CapabilityCoverageContractIssue("structural_facets_invalid", operationId, "structural_facets", index));

            var catalogId = ReadCapabilityCoverageString(node, "catalog_id");
            var catalogExcerpt = CanonicalizeCapabilityEvidenceText(
                ReadCapabilityCoverageString(node, "catalog_excerpt"));
            var catalogKnown = entries.TryGetValue(catalogId, out var entry);
            var catalogSelected = target.CatalogIds.Contains(catalogId, StringComparer.Ordinal);
            var excerptGrounded = catalogKnown
                                  && catalogExcerpt.Length > 0
                                  && catalogExcerpt.Length <= CapabilityDescriptionMaxCharacters
                                  && CanonicalizeCapabilityEvidenceText(BuildCapabilityCoverageCard(entry!, catalog))
                                      .Contains(catalogExcerpt, StringComparison.Ordinal);
            if (!catalogKnown)
                itemIssues.Add(new CapabilityCoverageContractIssue("evidence_catalog_id_unknown", operationId, "catalog_id", index, catalogId, requirementId));
            else if (!catalogSelected)
                itemIssues.Add(new CapabilityCoverageContractIssue("evidence_catalog_id_not_selected", operationId, "catalog_id", index, catalogId, requirementId));
            if (!excerptGrounded)
                itemIssues.Add(new CapabilityCoverageContractIssue(
                    catalogExcerpt.Length == 0 ? "evidence_excerpt_missing" : "evidence_excerpt_not_found",
                    operationId,
                    "catalog_excerpt",
                    index,
                    catalogId,
                    requirementId));

            issues.AddRange(itemIssues);
            adjudications.Add(new CapabilityCoverageGapAdjudication(
                operationId,
                requirementId,
                classification,
                structuralFacets,
                catalogId,
                catalogExcerpt,
                itemIssues.Count == 0));
        }

        foreach (var gap in gaps.Where(gap => adjudications.All(item => !string.Equals(
                     item.OperationId,
                     gap.OperationId,
                     StringComparison.Ordinal))))
        {
            issues.Add(new CapabilityCoverageContractIssue("operation_missing", gap.OperationId, "operation_id", null));
        }

        return new CapabilityCoverageGapAdjudicationReview(
            adjudications,
            issues.Count == 0 && adjudications.Count == gaps.Count,
            issues);
    }

    private static string BuildCapabilityCoverageReviewPrompt(
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets,
        CapabilityCoverageReview? previous,
        JsonObject? rejectedCandidate)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var operations = new JsonArray(targets.Select(match => (JsonNode)new JsonObject
        {
            ["operation_id"] = match.Operation.Id,
            ["description"] = match.Operation.Description,
            ["coverage_requirements"] = new JsonArray(GetCapabilityContractCoverageRequirements(match.Operation)
                .Select(static requirement => (JsonNode)new JsonObject
                {
                    ["requirement_id"] = requirement.Id,
                    ["excerpt"] = requirement.Excerpt
                }).ToArray()),
            ["selected_catalog_ids"] = new JsonArray(match.CatalogIds
                .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["selected_cards"] = new JsonArray(match.CatalogIds
                .Where(entries.ContainsKey)
                .Select(id => (JsonNode)new JsonObject
                {
                    ["catalog_id"] = id,
                    ["card"] = BuildCapabilityCoverageCard(entries[id], catalog)
                }).ToArray())
        }).ToArray());
        var previousNotice = previous is { ContractValid: false }
            ? $$"""
                The previous review violated the deterministic evidence contract. Repair only the operations supplied below. The issue list is authoritative: correct every listed field, return every supplied operation exactly once, and do not repeat operations that are absent from this repair request.
                <previous_contract_issues>
                {{BuildCapabilityCoverageContractIssuesJson(previous.Issues)}}
                </previous_contract_issues>
                <rejected_coverage_candidate>
                {{BuildRejectedCapabilityCoverageCandidate(rejectedCandidate, previous.Issues)}}
                </rejected_coverage_candidate>
                """
            : string.Empty;
        return $$"""
            You are a provider-neutral capability coverage reviewer. Return only the requested structured JSON.

            Independently verify whether the exact selected capability cards document the intrinsic external primitive in every supplied capability-contract coverage requirement. The inventory has a separate workflow-structure class which is not supplied here. Never require a generic parameterized card to repeat caller-specific argument values or instructions, input identifiers or locator representations, locally derivable parameter mapping, cardinality, uniqueness, per-item or complete-scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation policy, or quality thresholds. If a supplied excerpt accidentally retains such structural context, evaluate only its intrinsic primitive; selected-card evidence for that primitive is sufficient. Distinct requested primitives, including alternative create and update effects, must still all be documented by the selected cards. Matching only a general topic or omitting an intrinsic primitive is incomplete. Do not infer behavior from provider, server, tool, method, product, URL, or domain names. Use only documented card text, schemas, selectors, outputs, artifact contracts, and composition metadata.

            Return exactly one diagnostic for every supplied operation_id and no others. Return supported only when every requirement is documented by the selected cards. Return incomplete when any requirement is absent or only a weaker behavior is documented. For incomplete, unsupported_requirement_id must be one exact requirement_id from coverage_requirements. supported_weaker_behavior must be one exact non-empty catalog_excerpt copied from a selected card that states the weaker observable behavior, or be empty when no meaningful relaxation exists. Never use a server, tool, method, selector, or catalog identifier as the weaker behavior. candidate_catalog_ids is advisory only: use an empty array unless one of the supplied selected_catalog_ids is also worth reconsidering. Do not invent or cite an unavailable catalog ID.

            Evidence is mandatory. requirement_id must exactly equal one supplied coverage requirement ID. catalog_id must exactly equal one selected_catalog_id for the same operation. catalog_excerpt must be copied verbatim with the same case and punctuation from that catalog_id's selected card; keep it short and do not paraphrase it. Supported decisions need evidence covering every requirement. Incomplete decisions need evidence for the unsupported requirement showing the selected card's narrower documented behavior. For supported, unsupported_requirement_id and supported_weaker_behavior must both be empty. Never invent an identifier or paraphrase an excerpt.

            {{previousNotice}}
            <coverage_review_operations>
            {{operations.ToJsonString()}}
            </coverage_review_operations>
            """;
    }

    private static JsonObject BuildCapabilityCoverageReviewSchema(
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets)
    {
        var targetIds = targets
            .Select(static target => target.Operation.Id)
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        var requirementIds = targets
            .SelectMany(static target => GetCapabilityContractCoverageRequirements(target.Operation))
            .Select(static requirement => requirement.Id)
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        var selectedCatalogIds = targets
            .SelectMany(static target => target.CatalogIds)
            .Where(id => catalog.Entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(static id => (JsonNode?)JsonValue.Create(id))
            .ToArray();
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["diagnostics"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = targets.Count,
                    ["maxItems"] = targets.Count,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["operation_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(targetIds)
                            },
                            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("supported", "incomplete") },
                            ["unsupported_requirement_id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(
                                    new JsonNode?[] { JsonValue.Create(string.Empty) }
                                        .Concat(requirementIds.Select(static value => value?.DeepClone()))
                                        .ToArray())
                            },
                            ["supported_weaker_behavior"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = CapabilityDescriptionMaxCharacters
                            },
                            ["candidate_catalog_ids"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["maxItems"] = 8,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JsonArray(selectedCatalogIds.Select(static value => value?.DeepClone()).ToArray())
                                }
                            },
                            ["evidence"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["minItems"] = 1,
                                ["maxItems"] = Math.Max(1, targets.Sum(static target =>
                                    GetCapabilityContractCoverageRequirements(target.Operation).Count * Math.Max(1, target.CatalogIds.Count))),
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["catalog_id"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["enum"] = new JsonArray(selectedCatalogIds.Select(static value => value?.DeepClone()).ToArray())
                                        },
                                        ["requirement_id"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["enum"] = new JsonArray(requirementIds.Select(static value => value?.DeepClone()).ToArray())
                                        },
                                        ["catalog_excerpt"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["minLength"] = 1,
                                            ["maxLength"] = CapabilityDescriptionMaxCharacters
                                        }
                                    },
                                    ["required"] = new JsonArray("catalog_id", "requirement_id", "catalog_excerpt"),
                                    ["additionalProperties"] = false
                                }
                            }
                        },
                        ["required"] = new JsonArray("operation_id", "status", "unsupported_requirement_id", "supported_weaker_behavior", "candidate_catalog_ids", "evidence"),
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new JsonArray("diagnostics"),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject BuildCapabilityEvidenceReferenceSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source_id"] = new JsonObject { ["type"] = "string" },
            ["excerpt"] = new JsonObject { ["type"] = "string" }
        },
        ["required"] = new JsonArray("source_id", "excerpt"),
        ["additionalProperties"] = false
    };

    private static JsonObject BuildCapabilityCoverageEvidenceReferenceSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source_id"] = new JsonObject { ["type"] = "string" },
            ["excerpt"] = new JsonObject { ["type"] = "string" },
            ["enforcement_kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    CapabilityContractCoverageEnforcementKind,
                    WorkflowStructureCoverageEnforcementKind)
            }
        },
        ["required"] = new JsonArray("source_id", "excerpt", "enforcement_kind"),
        ["additionalProperties"] = false
    };

    private static CapabilityCoverageReview ParseCapabilityCoverageReview(
        JsonObject json,
        CapabilityCatalog catalog,
        IReadOnlyList<CapabilityOperationMatch> targets)
    {
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var targetById = targets.ToDictionary(static target => target.Operation.Id, StringComparer.Ordinal);
        if (json["diagnostics"] is not JsonArray nodes)
        {
            return new CapabilityCoverageReview(
                Array.Empty<CapabilityCoverageDiagnostic>(),
                false,
                [new CapabilityCoverageContractIssue(
                    "diagnostics_shape_invalid",
                    string.Empty,
                    "diagnostics",
                    null)]);
        }

        var diagnostics = new List<CapabilityCoverageDiagnostic>();
        var issues = new List<CapabilityCoverageContractIssue>();
        if (nodes.Count != targets.Count)
        {
            issues.Add(new CapabilityCoverageContractIssue(
                "diagnostic_count_mismatch",
                string.Empty,
                "diagnostics",
                null));
        }

        for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            if (nodes[nodeIndex] is not JsonObject node)
            {
                issues.Add(new CapabilityCoverageContractIssue(
                    "diagnostic_shape_invalid",
                    string.Empty,
                    "diagnostics",
                    nodeIndex));
                continue;
            }

            var operationId = ReadCapabilityCoverageString(node, "operation_id");
            if (!targetById.ContainsKey(operationId))
            {
                issues.Add(new CapabilityCoverageContractIssue(
                    "operation_unknown",
                    operationId,
                    "operation_id",
                    nodeIndex));
            }
        }

        foreach (var target in targets)
        {
            var matches = nodes
                .Select(static (node, index) => (Node: node as JsonObject, Index: index))
                .Where(item => item.Node is not null && string.Equals(
                    ReadCapabilityCoverageString(item.Node, "operation_id"),
                    target.Operation.Id,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new CapabilityCoverageContractIssue(
                    matches.Length == 0 ? "operation_missing" : "operation_duplicate",
                    target.Operation.Id,
                    "operation_id",
                    null));
                continue;
            }

            var node = matches[0].Node!;
            var diagnosticIndex = matches[0].Index;
            var diagnosticIssues = new List<CapabilityCoverageContractIssue>();
            var status = ReadCapabilityCoverageString(node, "status").ToLowerInvariant();
            if (status is not ("supported" or "incomplete"))
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "status_invalid",
                    target.Operation.Id,
                    "status",
                    diagnosticIndex));
            }
            var requirementsById = GetCapabilityContractCoverageRequirements(target.Operation)
                .ToDictionary(static requirement => requirement.Id, StringComparer.Ordinal);
            var unsupportedId = ReadCapabilityCoverageString(node, "unsupported_requirement_id");
            var unsupported = requirementsById.TryGetValue(unsupportedId, out var unsupportedRequirement)
                ? unsupportedRequirement.Excerpt
                : string.Empty;
            var weaker = CanonicalizeCapabilityEvidenceText(
                ReadCapabilityCoverageString(node, "supported_weaker_behavior"));
            if (weaker.Length > CapabilityDescriptionMaxCharacters)
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "weaker_behavior_limit_exceeded",
                    target.Operation.Id,
                    "supported_weaker_behavior",
                    diagnosticIndex));
            }

            var candidates = ReadCapabilityCoverageIds(
                node["candidate_catalog_ids"],
                8,
                out var candidatesValid);
            if (!candidatesValid)
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "candidate_catalog_ids_invalid",
                    target.Operation.Id,
                    "candidate_catalog_ids",
                    diagnosticIndex));
            }
            foreach (var candidate in candidates.Where(candidate => !entries.ContainsKey(candidate)))
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "candidate_catalog_id_unknown",
                    target.Operation.Id,
                    "candidate_catalog_ids",
                    diagnosticIndex,
                    candidate));
            }
            foreach (var candidate in candidates.Where(candidate =>
                         entries.ContainsKey(candidate)
                         && !target.CatalogIds.Contains(candidate, StringComparer.Ordinal)))
            {
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "candidate_catalog_id_not_selected",
                    target.Operation.Id,
                    "candidate_catalog_ids",
                    diagnosticIndex,
                    candidate));
            }

            var evidence = new List<CapabilityCoverageEvidence>();
            var evidenceValid = true;
            if (node["evidence"] is JsonArray evidenceArray)
            {
                if (evidenceArray.Count == 0)
                {
                    evidenceValid = false;
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "evidence_missing",
                        target.Operation.Id,
                        "evidence",
                        diagnosticIndex));
                }

                for (var evidenceIndex = 0; evidenceIndex < evidenceArray.Count; evidenceIndex++)
                {
                    if (evidenceArray[evidenceIndex] is not JsonObject evidenceNode)
                    {
                        evidenceValid = false;
                        diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                            "evidence_shape_invalid",
                            target.Operation.Id,
                            "evidence",
                            evidenceIndex));
                        continue;
                    }

                    var catalogId = ReadCapabilityCoverageString(evidenceNode, "catalog_id");
                    var requirementId = ReadCapabilityCoverageString(evidenceNode, "requirement_id");
                    var catalogExcerpt = CanonicalizeCapabilityEvidenceText(
                        ReadCapabilityCoverageString(evidenceNode, "catalog_excerpt"));
                    var requirementValid = requirementsById.TryGetValue(requirementId, out var requirement);
                    var catalogKnown = entries.TryGetValue(catalogId, out var entry);
                    var catalogSelected = target.CatalogIds.Contains(catalogId, StringComparer.Ordinal);
                    var excerptPresent = catalogExcerpt.Length > 0;
                    var excerptGrounded = catalogKnown
                                          && excerptPresent
                                          && CanonicalizeCapabilityEvidenceText(
                                                  BuildCapabilityCoverageCard(entry!, catalog))
                                              .Contains(catalogExcerpt, StringComparison.Ordinal);
                    var valid = catalogKnown
                                && catalogSelected
                                && requirementValid
                                && excerptPresent
                                && catalogExcerpt.Length <= CapabilityDescriptionMaxCharacters
                                && excerptGrounded;
                    evidenceValid &= valid;
                    if (valid)
                    {
                        evidence.Add(new CapabilityCoverageEvidence(
                            catalogId,
                            requirementId,
                            requirementsById[requirementId].Excerpt,
                            catalogExcerpt));
                        continue;
                    }

                    var issueCode = !catalogKnown
                        ? "evidence_catalog_id_unknown"
                        : !catalogSelected
                            ? "evidence_catalog_id_not_selected"
                            : !requirementValid
                                ? "evidence_requirement_id_unknown"
                                : !excerptPresent
                                    ? "evidence_excerpt_missing"
                                    : catalogExcerpt.Length > CapabilityDescriptionMaxCharacters
                                        ? "evidence_excerpt_limit_exceeded"
                                        : "evidence_excerpt_not_found";
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        issueCode,
                        target.Operation.Id,
                        "evidence",
                        evidenceIndex,
                        catalogId,
                        requirementId));
                }
            }
            else
            {
                evidenceValid = false;
                diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                    "evidence_shape_invalid",
                    target.Operation.Id,
                    "evidence",
                    diagnosticIndex));
            }

            var coveredRequirements = evidence
                .Select(static item => item.RequirementId)
                .ToHashSet(StringComparer.Ordinal);
            var evidencedCatalogExcerpts = evidence
                .Select(static item => item.CatalogExcerpt)
                .ToHashSet(StringComparer.Ordinal);
            var weakerBehaviorGrounded = weaker.Length == 0
                                         || evidencedCatalogExcerpts.Contains(weaker);
            if (status == "supported")
            {
                if (unsupportedId.Length > 0)
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "unsupported_requirement_forbidden",
                        target.Operation.Id,
                        "unsupported_requirement_id",
                        diagnosticIndex,
                        RequirementId: unsupportedId));
                }
                if (weaker.Length > 0)
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "weaker_behavior_forbidden",
                        target.Operation.Id,
                        "supported_weaker_behavior",
                        diagnosticIndex));
                }
                foreach (var requirementId in requirementsById.Keys.Where(id => !coveredRequirements.Contains(id)))
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "requirement_not_evidenced",
                        target.Operation.Id,
                        "evidence",
                        diagnosticIndex,
                        RequirementId: requirementId));
                }
            }
            else if (status == "incomplete")
            {
                if (!requirementsById.ContainsKey(unsupportedId))
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "unsupported_requirement_id_unknown",
                        target.Operation.Id,
                        "unsupported_requirement_id",
                        diagnosticIndex,
                        RequirementId: unsupportedId));
                }
                if (weaker.Length > 0 && !weakerBehaviorGrounded)
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "weaker_behavior_not_evidenced",
                        target.Operation.Id,
                        "supported_weaker_behavior",
                        diagnosticIndex));
                }
                if (requirementsById.ContainsKey(unsupportedId)
                    && evidence.All(item => !string.Equals(
                        item.RequirementId,
                        unsupportedId,
                        StringComparison.Ordinal)))
                {
                    diagnosticIssues.Add(new CapabilityCoverageContractIssue(
                        "unsupported_requirement_not_evidenced",
                        target.Operation.Id,
                        "evidence",
                        diagnosticIndex,
                        RequirementId: unsupportedId));
                }
            }

            var shapeValid = diagnosticIssues.Count == 0
                             && candidatesValid
                             && candidates.All(candidate =>
                                 entries.ContainsKey(candidate)
                                 && target.CatalogIds.Contains(candidate, StringComparer.Ordinal))
                             && evidenceValid;
            issues.AddRange(diagnosticIssues);
            diagnostics.Add(new CapabilityCoverageDiagnostic(
                target.Operation.Id,
                status,
                unsupportedId,
                unsupported,
                weaker,
                candidates,
                evidence,
                shapeValid));
        }

        return new CapabilityCoverageReview(
            diagnostics,
            issues.Count == 0 && diagnostics.Count == targets.Count,
            issues);
    }

    private static string ReadCapabilityCoverageString(JsonObject node, string property)
        => node[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;

    private static IReadOnlyList<string> ReadCapabilityCoverageIds(
        JsonNode? node,
        int maximum,
        out bool valid)
    {
        valid = node is JsonArray;
        if (node is not JsonArray array)
            return Array.Empty<string>();

        valid &= array.Count <= maximum;
        var result = new List<string>(Math.Min(array.Count, maximum));
        foreach (var item in array.Take(maximum))
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                valid = false;
                continue;
            }
            var id = text.Trim();
            if (!result.Contains(id, StringComparer.Ordinal))
                result.Add(id);
        }
        valid &= result.Count == array.Count;
        return result;
    }

    private static void RecordCapabilityCoverageContractTelemetry(
        TelemetrySpanScope span,
        string stage,
        IReadOnlyList<CapabilityCoverageContractIssue> issues)
    {
        span.SetAttribute($"gnougo-flow.plan.capability_coverage.{stage}_contract_issue_count", issues.Count);
        span.SetAttribute(
            $"gnougo-flow.plan.capability_coverage.{stage}_contract_issue_codes",
            string.Join(',', issues.Select(static issue => issue.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)));
        foreach (var issue in issues.Take(32))
        {
            span.AddEvent("gnougo-flow.plan.capability_coverage.contract_issue", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.stage", stage),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.code", issue.Code),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.operation_id", issue.OperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.field", issue.Field),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.index", issue.Index),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.catalog_id", issue.CatalogId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.capability_coverage.contract_issue.requirement_id", issue.RequirementId)
            });
        }
    }

    private static string BuildCapabilityCoverageContractIssuesJson(
        IReadOnlyList<CapabilityCoverageContractIssue> issues)
        => new JsonArray(issues
            .Take(32)
            .Select(static issue => (JsonNode)BuildCapabilityCoverageContractIssueJson(issue))
            .ToArray()).ToJsonString();

    private static JsonObject BuildCapabilityCoverageContractIssueJson(
        CapabilityCoverageContractIssue issue)
        => new()
        {
            ["code"] = issue.Code,
            ["operation_id"] = issue.OperationId,
            ["field"] = issue.Field,
            ["index"] = issue.Index,
            ["catalog_id"] = issue.CatalogId,
            ["requirement_id"] = issue.RequirementId
        };

    private static string BuildRejectedCapabilityCoverageCandidate(
        JsonObject? rejectedCandidate,
        IReadOnlyList<CapabilityCoverageContractIssue> issues)
    {
        if (rejectedCandidate is null)
            return "{}";
        var serialized = rejectedCandidate.ToJsonString();
        if (serialized.Length <= CapabilityInventoryRepairCandidateMaxCharacters)
            return serialized;

        var affectedOperationIds = issues
            .Select(static issue => issue.OperationId)
            .Where(static operationId => operationId.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var collectionProperty = rejectedCandidate.ContainsKey("diagnostics")
            ? "diagnostics"
            : rejectedCandidate.ContainsKey("adjudications")
                ? "adjudications"
                : string.Empty;
        if (collectionProperty.Length == 0)
            return "{}";
        return new JsonObject
        {
            [collectionProperty] = new JsonArray((rejectedCandidate[collectionProperty] as JsonArray)?
                .OfType<JsonObject>()
                .Where(diagnostic => affectedOperationIds.Contains(
                    ReadCapabilityCoverageString(diagnostic, "operation_id")))
                .Take(32)
                .Select(static diagnostic => (JsonNode)diagnostic.DeepClone())
                .ToArray() ?? [])
        }.ToJsonString();
    }

    private static string BuildCapabilityCoverageCard(
        CapabilityCatalogEntry entry,
        CapabilityCatalog catalog)
    {
        if (entry.RequestBindings.Count == 0)
            return entry.Card;
        var baseEntry = catalog.Entries.FirstOrDefault(candidate =>
            candidate.RequestBindings.Count == 0
            && string.Equals(candidate.Resolution, entry.Resolution, StringComparison.Ordinal)
            && string.Equals(candidate.Server, entry.Server, StringComparison.Ordinal)
            && string.Equals(candidate.Kind, entry.Kind, StringComparison.Ordinal)
            && string.Equals(candidate.Method, entry.Method, StringComparison.Ordinal));
        return baseEntry is null ? entry.Card : baseEntry.Card + Environment.NewLine + entry.Card;
    }

    private static string BuildCapabilityCoverageRematchPrompt(
        CapabilityInventory inventory,
        CapabilityCatalog catalog,
        CapabilityMatchingEvaluation evaluation,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps)
    {
        var affectedIds = gaps.Select(static gap => gap.OperationId).ToHashSet(StringComparer.Ordinal);
        var current = new JsonArray(evaluation.OperationMatches.Select(match => (JsonNode)new JsonObject
        {
            ["operation_id"] = match.Operation.Id,
            ["status"] = match.Status,
            ["catalog_ids"] = new JsonArray(match.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(match.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["decision_operation_id"] = match.DecisionOperationId ?? string.Empty,
            ["conditional_mode"] = match.ConditionalActivationMode,
            ["reason"] = match.Reason,
            ["locked_unless_affected"] = !affectedIds.Contains(match.Operation.Id)
        }).ToArray());
        var diagnostics = new JsonArray(gaps.Select(static gap => (JsonNode)new JsonObject
        {
            ["operation_id"] = gap.OperationId,
            ["unsupported_requirement_id"] = gap.UnsupportedRequirementId,
            ["unsupported_requirement"] = gap.UnsupportedRequirement,
            ["supported_weaker_behavior"] = gap.SupportedWeakerBehavior,
            ["candidate_catalog_ids"] = new JsonArray(gap.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }).ToArray());
        return $$"""
            You are repairing one provider-neutral capability matching contract after an evidence-qualified coverage review. Return the complete capability matching JSON required by the supplied schema.

            Change only operation IDs listed in coverage_gaps. Copy every other operation and every constraint decision exactly, including conditional_mode. For each affected operation, select the smallest documented capability or prerequisite-closed composition that fully implements every capability_contract coverage requirement; workflow_structure requirements are enforced later and must not be demanded from a capability card. Do not retain the previous selection merely because it implements a weaker intrinsic behavior. Use unavailable when the catalog contains no sufficient implementation. For a repaired conditional operation, preserve its decision_operation_id and use conditional_mode=exactly_one for selector alternatives or conditional_mode=all_on_value for an ordered composition with a declared no-effect outcome; use an empty conditional_mode otherwise. Never infer behavior from provider, server, tool, method, product, URL, or domain names.

            <runtime_inventory>
            {{BuildCapabilityInventoryJson(inventory)}}
            </runtime_inventory>
            <current_matching>
            {{current.ToJsonString()}}
            </current_matching>
            <coverage_gaps>
            {{diagnostics.ToJsonString()}}
            </coverage_gaps>
            <capability_catalog>
            {{catalog.Text}}
            </capability_catalog>
            """;
    }

    private static CapabilityMatchingEvaluation PreserveUnaffectedCapabilityMatches(
        CapabilityMatchingEvaluation current,
        CapabilityMatchingEvaluation rematched,
        IReadOnlySet<string> affectedOperationIds)
    {
        var currentOperations = current.OperationMatches.ToDictionary(static match => match.Operation.Id, StringComparer.Ordinal);
        var operations = rematched.OperationMatches
            .Select(match => affectedOperationIds.Contains(match.Operation.Id)
                ? match
                : currentOperations[match.Operation.Id])
            .ToArray();
        var affectedIssues = rematched.Issues
            .Where(issue => affectedOperationIds.Contains(issue.OperationId))
            .ToArray();
        var preservedIssues = current.Issues
            .Where(issue => !affectedOperationIds.Contains(issue.OperationId))
            .ToArray();
        return new CapabilityMatchingEvaluation(
            operations,
            current.ConstraintMatches,
            preservedIssues.Concat(affectedIssues).ToArray(),
            rematched.ContractValid);
    }

    private async Task RequestCapabilityRelaxationOrThrowAsync(
        StepExecutionContext ctx,
        IntentClarificationSession? session,
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps,
        IReadOnlyList<CapabilityOperationMatch> unresolvedAffected,
        CapabilityMatchingEvaluation originalEvaluation,
        CancellationToken ct)
    {
        var gap = gaps.FirstOrDefault(static diagnostic => diagnostic.EvidenceQualified)
                  ?? gaps.FirstOrDefault();
        if (gap is not null
            && !string.IsNullOrWhiteSpace(gap.SupportedWeakerBehavior)
            && session is not null)
        {
            var originalMatch = originalEvaluation.OperationMatches.First(match => string.Equals(
                match.Operation.Id,
                gap.OperationId,
                StringComparison.Ordinal));
            var fingerprint = BuildCapabilityRelaxationFingerprint(gap, originalMatch.CatalogIds);
            if (session.TryBeginCapabilityRelaxation(fingerprint)
                && session.CanAsk(1))
            {
                var question = new IntentClarificationQuestion(
                    "capability_relaxation_" + fingerprint[..12],
                    TruncateIntentClarificationText(
                        "The available capability does not fully support this requested behavior: "
                        + gap.UnsupportedRequirement
                        + ". The documented weaker behavior is: "
                        + gap.SupportedWeakerBehavior
                        + ". Choose whether the workflow may use it.",
                        1_000),
                    [
                        new IntentClarificationOption(
                            CapabilityRelaxationPreserveAnswer,
                            "Keep the requested guarantee. Planning will stop until a capability that fully supports it is available.",
                            true),
                        new IntentClarificationOption(
                            CapabilityRelaxationAcceptAnswer,
                            gap.SupportedWeakerBehavior,
                            false)
                    ]);
                var assessment = new IntentClarificationAssessment(
                    "questions",
                    "A required observable behavior is not fully supported by the available capability catalog.",
                    [question]);
                ctx.SetTelemetryAttribute("gnougo-flow.plan.capability_relaxation.requested", true);
                ctx.SetTelemetryAttribute("gnougo-flow.plan.capability_relaxation.fingerprint", fingerprint);
                await RequestIntentClarificationFormAsync(ctx, session, "capability_relaxation", assessment, ct);
                if (!string.Equals(
                    session.Answers[^1].Answer,
                    CapabilityRelaxationAcceptAnswer,
                    StringComparison.Ordinal))
                {
                    ctx.SetTelemetryAttribute("gnougo-flow.plan.capability_relaxation.outcome", "preserved");
                    throw BuildCapabilityCoverageUnavailable(gaps, unresolvedAffected, session);
                }
                ctx.SetTelemetryAttribute("gnougo-flow.plan.capability_relaxation.outcome", "relaxed");
                throw new WorkflowPlanClarificationRestartException();
            }
        }

        throw BuildCapabilityCoverageUnavailable(gaps, unresolvedAffected, session);
    }

    private static string BuildCapabilityRelaxationFingerprint(
        CapabilityCoverageDiagnostic gap,
        IReadOnlyList<string> selectedCatalogIds)
    {
        var canonical = gap.UnsupportedRequirementId
                        + "\n"
                        + string.Join("\n", selectedCatalogIds.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static WorkflowRuntimeException BuildCapabilityCoverageUnavailable(
        IReadOnlyList<CapabilityCoverageDiagnostic> gaps,
        IReadOnlyList<CapabilityOperationMatch> unresolvedAffected,
        IntentClarificationSession? session)
    {
        var details = new JsonObject
        {
            ["phase"] = "capability_coverage_review",
            ["reason"] = "incomplete_effect_coverage",
            ["planning_outcome"] = "cannot_plan_safely",
            ["recommended_action"] = "install_or_expose_a_sufficient_capability_or_explicitly_relax_the_requirement",
            ["coverage_gaps"] = new JsonArray(gaps.Select(static gap => (JsonNode)new JsonObject
            {
                ["operation_id"] = gap.OperationId,
                ["unsupported_requirement_id"] = gap.UnsupportedRequirementId,
                ["unsupported_requirement"] = gap.UnsupportedRequirement,
                ["supported_weaker_behavior"] = gap.SupportedWeakerBehavior,
                ["candidate_catalog_ids"] = new JsonArray(gap.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["evidence_qualified"] = gap.EvidenceQualified
            }).ToArray()),
            ["unresolved_operation_ids"] = new JsonArray(unresolvedAffected
                .Select(static match => (JsonNode?)JsonValue.Create(match.Operation.Id)).ToArray())
        };
        if (session is not null)
        {
            details["clarification_rounds"] = session.FormsUsed;
            details["clarification_questions"] = session.QuestionsUsed;
        }
        return new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            "A required operation is only partially supported by the available capability catalog. The requested guarantee was preserved and planning stopped.",
            details: details);
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
                new CapabilityMatchingIssue(operation.Id, operation.Description, operation.Required, "invalid", sanitized, Array.Empty<string>())
                {
                    ValidationIssue = "matching_response_malformed",
                    InvalidFields = ["$"]
                })
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
                || string.IsNullOrWhiteSpace(id))
            {
                valid = false;
                continue;
            }

            var normalized = id.Trim();
            if (seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    private static CapabilityMatchingShapeDiagnostic BuildInvalidMatchingDiagnostic(
        string status,
        bool selectedArrayValid,
        bool candidateArrayValid,
        bool selectedIdsKnown,
        bool candidateIdsKnown,
        bool reasonPresent,
        int selectedCount,
        int candidateCount,
        string decisionOperationId,
        string conditionalMode,
        bool conditionalTopologyValid,
        CapabilityInventoryOperation operation)
    {
        static CapabilityMatchingShapeDiagnostic Diagnostic(string code, string reason, params string[] fields)
            => new(code, reason, fields);

        if (status is not ("matched" or "composed" or "conditional" or "local" or "ambiguous" or "unavailable"))
            return Diagnostic("operation_status_invalid", "The operation match returned an unsupported status.", "status");
        if (!selectedArrayValid)
            return Diagnostic("catalog_ids_invalid", "The operation match returned malformed or excessive selected catalog IDs.", "catalog_ids");
        if (!candidateArrayValid)
            return Diagnostic("candidate_catalog_ids_invalid", "The operation match returned malformed or excessive candidate catalog IDs.", "candidate_catalog_ids");
        if (!selectedIdsKnown || !candidateIdsKnown)
            return Diagnostic(
                "catalog_id_unknown",
                "The operation match referenced one or more unknown catalog IDs.",
                !selectedIdsKnown ? "catalog_ids" : "candidate_catalog_ids");
        if (!reasonPresent)
            return Diagnostic("reason_missing", "The operation match omitted its required bounded reason.", "reason");
        if (operation.ExecutionKind == "local_processing" && status != "local"
            || operation.ExecutionKind != "local_processing" && status == "local")
        {
            return Diagnostic(
                "local_status_invalid",
                "The operation status is inconsistent with its locked local-processing classification.",
                "status",
                "catalog_ids",
                "candidate_catalog_ids");
        }

        var decisionExpected = status == "conditional";
        var decisionValid = decisionExpected
            ? decisionOperationId.Length > 0
              && string.Equals(decisionOperationId, operation.DecisionSourceOperationId, StringComparison.Ordinal)
            : decisionOperationId.Length == 0;
        if (!decisionValid)
            return Diagnostic("decision_reference_invalid", "The operation match returned an invalid conditional decision reference.", "decision_operation_id");

        var conditionalModeRecognized = conditionalMode is ConditionalExactlyOneActivationMode or ConditionalAllOnValueActivationMode;
        if (decisionExpected ? !conditionalModeRecognized : conditionalMode.Length > 0)
            return Diagnostic("conditional_mode_invalid", "The operation match returned an invalid conditional activation mode.", "conditional_mode");

        var cardinalityValid = status switch
        {
            "matched" => selectedCount == 1 && candidateCount == 0,
            "composed" => selectedCount >= 2 && candidateCount == 0,
            "conditional" => selectedCount >= 1 && candidateCount == 0,
            "local" or "unavailable" => selectedCount == 0 && candidateCount == 0,
            "ambiguous" => selectedCount == 0 && candidateCount > 0,
            _ => false
        };
        if (!cardinalityValid)
        {
            return Diagnostic(
                "selection_cardinality_invalid",
                "The operation status and selected or candidate catalog ID counts are inconsistent.",
                "status",
                "catalog_ids",
                "candidate_catalog_ids");
        }
        if (status == "conditional" && !conditionalTopologyValid)
        {
            return Diagnostic(
                "conditional_topology_invalid",
                "The selected capabilities do not form the declared conditional activation topology.",
                "catalog_ids",
                "conditional_mode");
        }

        return Diagnostic("matching_shape_invalid", "The operation match violated its structured matching contract.", "operation_match");
    }

    private static CapabilityMatchingShapeDiagnostic BuildInvalidConstraintMatchingDiagnostic(
        string status,
        bool deniedArrayValid,
        bool candidateArrayValid,
        bool deniedIdsKnown,
        bool candidateIdsKnown,
        bool reasonPresent,
        int deniedCount,
        int candidateCount,
        CapabilityInventoryConstraint constraint,
        bool normalizedNativePolicyOnly)
    {
        static CapabilityMatchingShapeDiagnostic Diagnostic(string code, string reason, params string[] fields)
            => new(code, reason, fields);

        if (status is not ("enforced" or "policy_only" or "ambiguous"))
            return Diagnostic("constraint_status_invalid", "The constraint match returned an unsupported status.", "status");
        if (!deniedArrayValid)
            return Diagnostic("denied_catalog_ids_invalid", "The constraint match returned malformed or excessive denied catalog IDs.", "denied_catalog_ids");
        if (!candidateArrayValid)
            return Diagnostic("candidate_catalog_ids_invalid", "The constraint match returned malformed or excessive candidate catalog IDs.", "candidate_catalog_ids");
        if (!deniedIdsKnown || !candidateIdsKnown)
        {
            return Diagnostic(
                "constraint_catalog_id_unknown",
                "The constraint match referenced one or more unknown or non-MCP catalog IDs.",
                !deniedIdsKnown ? "denied_catalog_ids" : "candidate_catalog_ids");
        }
        if (!reasonPresent)
            return Diagnostic("constraint_reason_missing", "The constraint match omitted its required bounded reason.", "reason");
        if (string.Equals(constraint.EnforcementKind, "exact_denial", StringComparison.Ordinal)
            && status == "policy_only"
            && !normalizedNativePolicyOnly)
        {
            return Diagnostic(
                "constraint_enforcement_kind_mismatch",
                "An exact-denial constraint cannot use policy_only; return enforced with exact denied MCP catalog IDs or ambiguous with concrete candidate MCP catalog IDs.",
                "status",
                "denied_catalog_ids",
                "candidate_catalog_ids");
        }

        var cardinalityValid = status switch
        {
            "enforced" => deniedCount > 0 && candidateCount == 0,
            "policy_only" => deniedCount == 0 && candidateCount == 0,
            "ambiguous" => deniedCount == 0 && candidateCount > 0,
            _ => false
        };
        return cardinalityValid
            ? Diagnostic("constraint_matching_shape_invalid", "The constraint match violated its structured matching contract.", "constraint_match")
            : Diagnostic(
                "constraint_selection_cardinality_invalid",
                "The constraint status and denied or candidate catalog ID counts are inconsistent.",
                "status",
                "denied_catalog_ids",
                "candidate_catalog_ids");
    }

    private static bool RequiresCapabilityMatchingRepair(CapabilityMatchingEvaluation evaluation)
        => !evaluation.ContractValid || evaluation.Issues.Any(static issue => issue.Required);

    private static CapabilityMatchingEvaluation PreserveValidCapabilityMatches(
        CapabilityMatchingEvaluation initial,
        CapabilityMatchingEvaluation repaired)
    {
        var dependencyUnlockedOperationIds = GetDependencyUnlockedDecisionOperationIds(initial);
        var lockedOperationIds = initial.OperationMatches
            .Where(static match => match.Status is "matched" or "composed" or "conditional" or "local")
            .Where(match => !dependencyUnlockedOperationIds.Contains(match.Operation.Id))
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

    private static bool HasRequiredCapabilityMatchingBlocker(CapabilityMatchingEvaluation evaluation)
        => evaluation.Issues.Any(static issue => issue.Required);

    private static bool IsCapabilityDiscoveryNarrowed(
        IReadOnlyList<McpServerDiscovery> selected,
        IReadOnlyList<McpServerDiscovery> complete)
    {
        static HashSet<string> Identities(IReadOnlyList<McpServerDiscovery> servers)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in servers)
            {
                foreach (var tool in server.Tools)
                    identities.Add($"{server.Name}\u001ftool\u001f{tool.Name}");
                foreach (var prompt in server.Prompts)
                    identities.Add($"{server.Name}\u001fprompt\u001f{prompt.Name}");
            }
            return identities;
        }

        var selectedIdentities = Identities(selected);
        var completeIdentities = Identities(complete);
        return selectedIdentities.Count < completeIdentities.Count
               && selectedIdentities.IsSubsetOf(completeIdentities);
    }

    private static CapabilityMatchingEvaluation RemapCapabilityMatchingCatalogIds(
        CapabilityMatchingEvaluation evaluation,
        CapabilityCatalog source,
        CapabilityCatalog destination)
    {
        static string Identity(CapabilityCatalogEntry entry)
            => string.Join(
                '\u001f',
                entry.Resolution,
                entry.Server ?? string.Empty,
                entry.Kind ?? string.Empty,
                entry.Method,
                CanonicalizeBindings(entry.RequestBindings));

        var sourceEntries = source.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var destinationIds = destination.Entries
            .GroupBy(Identity, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Id,
                StringComparer.Ordinal);
        string? RemapId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return id;
            return sourceEntries.TryGetValue(id, out var entry)
                   && destinationIds.TryGetValue(Identity(entry), out var destinationId)
                ? destinationId
                : id;
        }

        IReadOnlyList<string> RemapIds(IReadOnlyList<string> ids)
            => ids.Select(RemapId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        var operationMatches = evaluation.OperationMatches.Select(match => match with
        {
            CatalogIds = RemapIds(match.CatalogIds),
            CandidateCatalogIds = RemapIds(match.CandidateCatalogIds),
            DecisionProducerCatalogId = RemapId(match.DecisionProducerCatalogId)
        }).ToArray();
        var constraintMatches = evaluation.ConstraintMatches.Select(match => match with
        {
            DeniedCatalogIds = RemapIds(match.DeniedCatalogIds),
            CandidateCatalogIds = RemapIds(match.CandidateCatalogIds)
        }).ToArray();
        var issues = evaluation.Issues.Select(issue => issue with
        {
            CandidateCatalogIds = RemapIds(issue.CandidateCatalogIds)
        }).ToArray();
        return new CapabilityMatchingEvaluation(
            operationMatches,
            constraintMatches,
            issues,
            evaluation.ContractValid);
    }

    private static HashSet<string> BuildCapabilityMatchingBlockerIdentities(
        CapabilityMatchingEvaluation evaluation)
        => evaluation.Issues
            .Where(static issue => issue.Required)
            .Select(static issue => string.Join(
                '\u001f',
                issue.OperationId,
                issue.Status,
                issue.ValidationIssue,
                issue.ReasonCode))
            .ToHashSet(StringComparer.Ordinal);

    private static string BuildCapabilityMatchingFingerprint(CapabilityMatchingEvaluation evaluation)
    {
        var canonical = new StringBuilder();
        foreach (var match in evaluation.OperationMatches.OrderBy(static match => match.Operation.Id, StringComparer.Ordinal))
        {
            canonical.Append("operation\u001f")
                .Append(match.Operation.Id).Append('\u001f')
                .Append(match.Status).Append('\u001f')
                .Append(string.Join(',', match.CatalogIds.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(string.Join(',', match.CandidateCatalogIds.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(match.DecisionOperationId).Append('\u001f')
                .Append(match.ConditionalActivationMode).AppendLine();
        }
        foreach (var match in evaluation.ConstraintMatches.OrderBy(static match => match.Constraint.Id, StringComparer.Ordinal))
        {
            canonical.Append("constraint\u001f")
                .Append(match.Constraint.Id).Append('\u001f')
                .Append(match.Status).Append('\u001f')
                .Append(string.Join(',', match.DeniedCatalogIds.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(string.Join(',', match.CandidateCatalogIds.Order(StringComparer.Ordinal))).AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string BuildCapabilityInventoryFingerprint(CapabilityInventory inventory)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                BuildCapabilityInventoryJson(inventory))))
            .ToLowerInvariant();

    private static bool TryGetInventoryRewindConstraintIds(
        CapabilityInventory inventory,
        CapabilityMatchingEvaluation evaluation,
        out IReadOnlySet<string> constraintIds)
    {
        var exactDenialIds = inventory.Constraints
            .Where(static constraint => constraint.Required
                                        && string.Equals(
                                            constraint.EnforcementKind,
                                            "exact_denial",
                                            StringComparison.Ordinal))
            .Select(static constraint => constraint.Id)
            .ToHashSet(StringComparer.Ordinal);
        constraintIds = evaluation.Issues
            .Where(static issue => issue.Required)
            .Select(static issue => issue.OperationId)
            .Where(exactDenialIds.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return constraintIds.Count > 0;
    }

    private static bool InventoryRewindPreservesStableContracts(
        CapabilityInventory previous,
        CapabilityInventory candidate,
        IReadOnlySet<string> challengedConstraintIds)
    {
        if (!candidate.Complete
            || candidate.IncompleteReasons.Count != 0
            || !string.Equals(
                candidate.ExternalWriteConfirmationPolicy,
                previous.ExternalWriteConfirmationPolicy,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.ExternalWriteConfirmationEvidenceAnchor?.Id,
                previous.ExternalWriteConfirmationEvidenceAnchor?.Id,
                StringComparison.Ordinal)
            || previous.Operations.Count != candidate.Operations.Count
            || previous.Constraints.Count != candidate.Constraints.Count)
        {
            return false;
        }

        var previousWithoutConstraints = previous with
        {
            Constraints = Array.Empty<CapabilityInventoryConstraint>()
        };
        var candidateWithoutConstraints = candidate with
        {
            Constraints = Array.Empty<CapabilityInventoryConstraint>()
        };
        if (!string.Equals(
                BuildCapabilityInventoryJson(previousWithoutConstraints),
                BuildCapabilityInventoryJson(candidateWithoutConstraints),
                StringComparison.Ordinal))
        {
            return false;
        }

        var candidateConstraints = candidate.Constraints.ToDictionary(
            static constraint => constraint.Id,
            StringComparer.Ordinal);
        var changed = false;
        foreach (var constraint in previous.Constraints)
        {
            if (!candidateConstraints.TryGetValue(constraint.Id, out var candidateConstraint)
                || !string.Equals(constraint.Description, candidateConstraint.Description, StringComparison.Ordinal)
                || constraint.Required != candidateConstraint.Required)
            {
                return false;
            }

            if (!challengedConstraintIds.Contains(constraint.Id))
            {
                if (!string.Equals(
                        constraint.EnforcementKind,
                        candidateConstraint.EnforcementKind,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                continue;
            }

            if (!string.Equals(constraint.EnforcementKind, "exact_denial", StringComparison.Ordinal)
                || !string.Equals(
                    candidateConstraint.EnforcementKind,
                    "workflow_policy",
                    StringComparison.Ordinal))
            {
                return false;
            }
            changed = true;
        }

        return changed;
    }

    private static CapabilityMatchingEvaluation MarkCapabilityMatchingRewindNonImproving(
        CapabilityMatchingEvaluation rewound,
        CapabilityMatchingEvaluation previous)
    {
        var unresolvedIds = previous.Issues
            .Where(static issue => issue.Required)
            .Select(static issue => issue.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var reason = "Expanded-catalog capability re-adjudication did not produce a schema-valid changed contract with a strictly smaller blocker set.";
        var issues = rewound.Issues
            .Where(issue => issue.Required || !unresolvedIds.Contains(issue.OperationId))
            .Select(issue => unresolvedIds.Contains(issue.OperationId)
                ? issue with
                {
                    Status = "invalid",
                    Reason = reason,
                    ReasonCode = "upstream_rewind_non_improving",
                    ValidationIssue = "upstream_rewind_non_improving",
                    InvalidFields = ["operation_match"]
                }
                : issue)
            .ToList();
        foreach (var unresolvedId in unresolvedIds.Where(id => issues.All(issue => !string.Equals(
                     issue.OperationId,
                     id,
                     StringComparison.Ordinal))))
        {
            var previousIssue = previous.Issues.First(issue => string.Equals(
                issue.OperationId,
                unresolvedId,
                StringComparison.Ordinal));
            issues.Add(previousIssue with
            {
                Status = "invalid",
                Reason = reason,
                ReasonCode = "upstream_rewind_non_improving",
                ValidationIssue = "upstream_rewind_non_improving",
                InvalidFields = ["operation_match"]
            });
        }

        var operations = rewound.OperationMatches.Select(match => unresolvedIds.Contains(match.Operation.Id)
            ? match with { Status = "invalid", Reason = reason }
            : match).ToArray();
        return new CapabilityMatchingEvaluation(
            operations,
            rewound.ConstraintMatches,
            issues,
            false);
    }

    private static HashSet<string> GetDependencyUnlockedDecisionOperationIds(
        CapabilityMatchingEvaluation evaluation)
    {
        var matches = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        var pending = new Stack<string>(evaluation.OperationMatches
            .Where(static match => string.Equals(match.Status, "invalid", StringComparison.Ordinal)
                                   && !string.IsNullOrWhiteSpace(match.DecisionGroundingFailureCode))
            .Select(static match => match.DecisionOperationId ?? match.Operation.DecisionSourceOperationId)
            .Where(static operationId => !string.IsNullOrWhiteSpace(operationId))!);
        var unlocked = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var operationId))
        {
            if (!unlocked.Add(operationId) || !matches.TryGetValue(operationId, out var match))
                continue;

            if (!string.IsNullOrWhiteSpace(match.Operation.DecisionSourceOperationId))
                pending.Push(match.Operation.DecisionSourceOperationId);
            foreach (var inputOperationId in match.Operation.InputOperationIds)
                pending.Push(inputOperationId);
        }

        return unlocked;
    }

    private static string BuildCapabilityInventoryMatchingRewindPrompt(
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources,
        CapabilityInventory inventory,
        CapabilityMatchingEvaluation evaluation,
        IReadOnlySet<string> challengedConstraintIds)
    {
        var issues = new JsonArray(evaluation.Issues
            .Where(issue => challengedConstraintIds.Contains(issue.OperationId))
            .Select(issue => (JsonNode)new JsonObject
            {
                ["constraint_id"] = issue.OperationId,
                ["status"] = issue.Status,
                ["validation_issue"] = issue.ValidationIssue,
                ["reported_status"] = issue.ReportedStatus,
                ["selected_catalog_id_count"] = issue.SelectedCatalogIdCount,
                ["candidate_catalog_id_count"] = issue.CandidateCatalogIdCount,
                ["invalid_fields"] = BuildStringArrayJson(issue.InvalidFields.Take(8).ToArray())
            }).ToArray());
        return $$"""
            You are re-adjudicating one provider-neutral workflow capability inventory from its original evidence after exact-denial matching could not establish a valid contract. Return only the complete inventory JSON required by the supplied schema.

            Preserve every operation and constraint ID, description, required flag, evidence reference, dependency, confirmation policy, and all unrelated classifications exactly. Re-evaluate only enforcement_kind for the challenged constraint IDs. Change exact_denial to workflow_policy only when the original evidence describes a target-, input-, resource-, relationship-, data-, or branch-dependent restriction, or another invariant that must be enforced by workflow structure. Keep exact_denial when the evidence unconditionally prohibits an independently identifiable external capability throughout the document. Do not use provider, server, tool, method, catalog, URL, product, or domain names to decide. Do not add, remove, merge, split, rename, or reorder inventory entries.

            <previous_inventory>
            {{BuildCapabilityInventoryJson(inventory)}}
            </previous_inventory>
            <challenged_constraint_ids>
            {{BuildStringArrayJson(challengedConstraintIds.Order(StringComparer.Ordinal).ToArray()).ToJsonString()}}
            </challenged_constraint_ids>
            <matching_contract_issues>
            {{issues.ToJsonString()}}
            </matching_contract_issues>
            <evidence_sources>
            {{BuildCapabilityEvidenceSourcesJson(evidenceSources)}}
            </evidence_sources>
            """;
    }

    private static string BuildCapabilityMatchingRepairPrompt(
        CapabilityInventory inventory,
        CapabilityCatalog catalog,
        CapabilityMatchingEvaluation previous)
    {
        var dependencyUnlockedOperationIds = GetDependencyUnlockedDecisionOperationIds(previous);
        var lockedOperations = previous.OperationMatches
            .Where(static match => match.Status is "matched" or "composed" or "conditional" or "local")
            .Where(match => !dependencyUnlockedOperationIds.Contains(match.Operation.Id))
            .Select(static match => (JsonNode)new JsonObject
            {
                ["operation_id"] = match.Operation.Id,
                ["status"] = match.Status,
                ["catalog_ids"] = new JsonArray(match.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["decision_operation_id"] = match.DecisionOperationId ?? string.Empty,
                ["conditional_mode"] = match.ConditionalActivationMode
            }).ToArray();
        var lockedConstraints = previous.ConstraintMatches
            .Where(static match => match.Status is "enforced" or "policy_only")
            .Select(static match => (JsonNode)new JsonObject
            {
                ["constraint_id"] = match.Constraint.Id,
                ["status"] = match.Status,
                ["denied_catalog_ids"] = new JsonArray(match.DeniedCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
            }).ToArray();
        var previousMatches = previous.OperationMatches.ToDictionary(static match => match.Operation.Id, StringComparer.Ordinal);
        var issues = previous.Issues.Select(issue => (JsonNode)new JsonObject
        {
            ["operation_id"] = issue.OperationId,
            ["status"] = issue.Status,
            ["reported_status"] = issue.ReportedStatus,
            ["reason"] = issue.Reason,
            ["validation_issue"] = issue.ValidationIssue,
            ["selected_catalog_id_count"] = issue.SelectedCatalogIdCount,
            ["candidate_catalog_id_count"] = issue.CandidateCatalogIdCount,
            ["invalid_fields"] = new JsonArray(issue.InvalidFields
                .Take(8)
                .Select(static field => (JsonNode?)JsonValue.Create(field)).ToArray()),
            ["decision_operation_id"] = previousMatches.TryGetValue(issue.OperationId, out var match)
                ? match.DecisionOperationId ?? match.Operation.DecisionSourceOperationId
                : string.Empty,
            ["grounding_failure_code"] = previousMatches.TryGetValue(issue.OperationId, out match)
                ? match.DecisionGroundingFailureCode ?? string.Empty
                : string.Empty,
            ["candidate_catalog_ids"] = new JsonArray(issue.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }).ToArray();
        return $$"""
            You are a domain-neutral capability matcher repairing a previous matching contract. Return only the requested structured JSON.

            Return every operation and constraint exactly once. Preserve all locked decisions exactly. A decision-source operation and every producer reached through its declared input_operation_ids are deliberately absent from the locked set when coupled to a reported conditional grounding issue: repair that declared chain together with the dependent conditional operation, selecting a better typed producer when the catalog provides one. Never infer an undeclared producer from descriptions or adjacency. Resolve each reported issue from the documented catalog only, and correct every listed validation_issue at its invalid_fields rather than repeating the reported_status. For operations use matched for one sufficient ID, composed for two or more necessary complementary IDs, conditional for either one mutually exclusive selector subset chosen by the locked decision_source_operation_id plus any necessary complementary unconditional prerequisites, or—only when allow_no_effect_outcome=true—one or more necessary capabilities that all execute in catalog_ids order for the single effect value; use local only for local_processing, ambiguous for unresolved user intent, and unavailable only when no sufficient implementation exists. Before retaining unavailable, scan every catalog row again, including selector-specific variants: a variant inherits its whole-tool description, arguments, outputs, artifacts, and composition contract, and workflow waiting, repetition, ordering, aggregation, and termination belong to workflow structure rather than capability sufficiency. For conditional, copy decision_source_operation_id exactly into decision_operation_id and set conditional_mode=exactly_one for selector alternatives or conditional_mode=all_on_value for the ordered effect composition; otherwise leave decision_operation_id and conditional_mode empty. Conditional selector variants share one physical capability and the same selector paths, differing on exactly one selector; prerequisites execute once outside the branch. An all_on_value conditional executes every selected capability in order inside its one effect branch and none in its no-effect branch. A runtime-dependent result is not user ambiguity. A complete_operation wrapper replaces its encapsulated phases. For constraints use enforced only when enforcement_kind=exact_denial and exact denied MCP IDs are established; use policy_only only when enforcement_kind=workflow_policy; use ambiguous only for unresolved exact-denial candidates. Select the smallest sufficient composition and never invent IDs.

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
        bool repairAttempted,
        IntentClarificationSession? clarificationSession = null)
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
            ["validation_issue"] = issue.ValidationIssue.Length > 0 ? issue.ValidationIssue : null,
            ["reported_status"] = issue.ReportedStatus.Length > 0 ? issue.ReportedStatus : null,
            ["selected_catalog_id_count"] = issue.SelectedCatalogIdCount,
            ["candidate_catalog_id_count"] = issue.CandidateCatalogIdCount,
            ["invalid_fields"] = new JsonArray(issue.InvalidFields
                .Take(8)
                .Select(static field => (JsonNode?)JsonValue.Create(
                    SanitizeCapabilityInferenceDiagnostic(field, 80))).ToArray()),
            ["reason_code"] = issue.ReasonCode.Length > 0
                ? issue.ReasonCode
                : repairAttempted && issue.Status == "invalid"
                    ? "model_repair_exhausted"
                    : null,
            ["candidate_capabilities"] = new JsonArray(issue.CandidateCatalogIds
                .Where(entryMap.ContainsKey)
                .Take(8)
                .Select(id => (JsonNode)BuildCapabilityCandidateCard(entryMap[id])).ToArray())
        }).ToArray());
        var onlyUnavailable = blocking.All(static issue => issue.Status == "unavailable");
        var onlyContractGaps = blocking.All(static issue => issue.Status == "contract_gap");
        var onlyUnsupported = blocking.All(static issue => issue.Status is "unavailable" or "contract_gap");
        var containsInvalidContract = blocking.Any(static issue => issue.Status == "invalid");
        var unsupported = onlyUnsupported;
        var contractGapOperationIds = blocking
            .Where(static issue => issue.Status == "contract_gap")
            .Select(static issue => issue.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var unavailable = unsupported
            ? evaluation.OperationMatches.Where(match => match.Operation.Required
                                                         && (match.Status == "unavailable"
                                                             || contractGapOperationIds.Contains(match.Operation.Id)))
                .Select(static match => (JsonNode)new JsonObject
                {
                    ["id"] = match.Operation.Id,
                    ["description"] = match.Operation.Description,
                    ["required"] = true,
                    ["reason"] = match.Status == "unavailable"
                        ? "no_matching_discovered_capability"
                        : "conditional_decision_contract_gap",
                    ["grounding_failure_code"] = match.DecisionGroundingFailureCode
                }).ToArray()
            : Array.Empty<JsonNode>();
        throw new WorkflowRuntimeException(
            unsupported ? ErrorCodes.CapabilityPreflightUnavailable : ErrorCodes.CapabilityPreflightInferenceFailed,
            onlyContractGaps
                ? "One or more conditional runtime operations have no safe provider-neutral decision contract."
                : onlyUnavailable
                    ? "One or more required runtime operations have no matching discovered capability."
                    : unsupported
                        ? "One or more required runtime operations have no matching capability or safe provider-neutral decision contract."
                    : "Capability matching remained ambiguous or invalid after one bounded repair attempt.",
            details: new JsonObject
            {
                ["phase"] = "capability_matching",
                ["reason"] = onlyContractGaps ? "conditional_decision_contract_gap" : null,
                ["reason_code"] = repairAttempted ? "model_repair_exhausted" : null,
                ["classification"] = containsInvalidContract ? "model_contract_violation" : null,
                ["repair_attempted"] = repairAttempted,
                ["attempts"] = repairAttempted ? 2 : 1,
                ["clarification_rounds"] = clarificationSession?.FormsUsed ?? 0,
                ["clarification_questions"] = clarificationSession?.QuestionsUsed ?? 0,
                ["matching_issues"] = issueNodes,
                ["unavailable_capabilities"] = new JsonArray(unavailable),
                ["planning_outcome"] = unsupported ? "unsupported" : "cannot_plan_safely",
                ["recommended_action"] = onlyContractGaps
                    ? "configure_decision_contract_or_enable_structured_projection"
                    : onlyUnavailable
                        ? "configure_capability_or_revise_request"
                        : unsupported
                            ? "configure_capability_or_decision_contract_or_revise_request"
                        : containsInvalidContract
                            ? "retry_or_change_planning_model"
                            : "clarify_or_abandon"
            });
    }

    private static bool IsConditionalWriteRelaxationEligible(CapabilityMatchingEvaluation evaluation)
    {
        var blocking = evaluation.Issues.Where(static issue => issue.Required).ToArray();
        if (blocking.Length == 0
            || blocking.Any(static issue => !string.Equals(
                issue.Status,
                "contract_gap",
                StringComparison.Ordinal)))
        {
            return false;
        }

        var blockedIds = blocking.Select(static issue => issue.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var blockedMatches = evaluation.OperationMatches
            .Where(match => blockedIds.Contains(match.Operation.Id))
            .ToArray();
        if (blockedMatches.Length != blockedIds.Count
            || blockedMatches.Any(static match =>
                !string.Equals(match.Operation.ExecutionKind, "external_effect", StringComparison.Ordinal)
                || !string.Equals(match.Operation.ExternalEffectKind, "write", StringComparison.Ordinal)))
        {
            return false;
        }

        if (evaluation.ConstraintMatches.Any(static match =>
                match.Constraint.Required && string.Equals(match.Status, "ambiguous", StringComparison.Ordinal))
            || evaluation.OperationMatches.Any(match =>
                match.Operation.Required
                && !blockedIds.Contains(match.Operation.Id)
                && match.Status is "invalid" or "ambiguous" or "unavailable"))
        {
            return false;
        }

        return evaluation.OperationMatches.Any(match =>
            match.Operation.Required
            && !blockedIds.Contains(match.Operation.Id)
            && string.Equals(match.Operation.ExecutionKind, "external_effect", StringComparison.Ordinal)
            && match.Operation.ExternalEffectKind is "read" or "execute"
            && match.Status is "matched" or "composed" or "conditional");
    }

    private static async Task RequestConditionalWriteRelaxationAsync(
        StepExecutionContext ctx,
        IntentClarificationSession session,
        CapabilityMatchingEvaluation evaluation,
        CancellationToken ct)
    {
        var blockedIds = evaluation.Issues
            .Where(static issue => issue.Required
                                   && string.Equals(issue.Status, "contract_gap", StringComparison.Ordinal))
            .Select(static issue => issue.OperationId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var canonical = "conditional_write_read_only\n" + string.Join('\n', blockedIds);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        if (!session.TryBeginCapabilityRelaxation(fingerprint) || !session.CanAsk(1))
            return;

        var question = new IntentClarificationQuestion(
            "conditional_write_relaxation_" + fingerprint[..12],
            "The requested workflow can safely produce a read-only result, but its conditional external writes cannot currently be generated with a proven runtime decision. Choose whether those writes remain required.",
            [
                new IntentClarificationOption(
                    ConditionalWriteRelaxationPreserveAnswer,
                    "Keep every requested external write. Planning stops until its runtime decision can be proven safely.",
                    true),
                new IntentClarificationOption(
                    ConditionalWriteRelaxationReadOnlyAnswer,
                    "Keep the required reads and processing, return their result, and omit only the unresolved external writes.",
                    false)
            ]);
        var assessment = new IntentClarificationAssessment(
            "questions",
            "A safe read-only result is available if you explicitly relax only the unresolved conditional writes.",
            [question]);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.conditional_write_relaxation.requested", true);
        await RequestIntentClarificationFormAsync(
            ctx,
            session,
            "conditional_write_relaxation",
            assessment,
            ct);
        if (!string.Equals(
                session.Answers[^1].Answer,
                ConditionalWriteRelaxationReadOnlyAnswer,
                StringComparison.Ordinal))
        {
            ctx.SetTelemetryAttribute("gnougo-flow.plan.conditional_write_relaxation.outcome", "preserved");
            return;
        }

        ctx.SetTelemetryAttribute("gnougo-flow.plan.conditional_write_relaxation.outcome", "read_only");
        throw new WorkflowPlanClarificationRestartException();
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
                var localDecisionConsumers = evaluation.OperationMatches
                    .Where(candidate => string.Equals(
                                            candidate.DecisionContractSource,
                                            LocalDecisionContractSource,
                                            StringComparison.Ordinal)
                                        && string.Equals(
                                            candidate.DecisionOperationId,
                                            match.Operation.Id,
                                            StringComparison.Ordinal))
                    .ToArray();
                if (localDecisionConsumers.Length > 0)
                {
                    var producerCatalogIds = localDecisionConsumers
                        .Select(static candidate => candidate.DecisionProducerCatalogId)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (producerCatalogIds.Length != 1
                        || !entries.TryGetValue(producerCatalogIds[0]!, out var producer)
                        || !string.Equals(producer.Resolution, "native", StringComparison.Ordinal)
                        || !string.Equals(producer.Method, LocalDecisionStepType, StringComparison.Ordinal))
                    {
                        throw new WorkflowRuntimeException(
                            ErrorCodes.CapabilityPreflightUnavailable,
                            "The synthesized local decision operation has no single policy-allowed evaluator.");
                    }

                    resolved.Add(new ResolvedCapability(
                        match.Operation.Id,
                        match.Operation.Description,
                        match.Operation.Required,
                        producer.Resolution,
                        producer.Server,
                        producer.Kind,
                        producer.Method,
                        producer.RequestBindings,
                        match.Operation.Id,
                        producer.Id,
                        "matched",
                        match.Operation.ExecutionKind,
                        match.Operation.ExternalEffectKind,
                        CapabilityDescription: producer.Description)
                    {
                        InputOperationIds = match.Operation.InputOperationIds
                    });
                    continue;
                }

                resolved.Add(new ResolvedCapability(match.Operation.Id, match.Operation.Description, match.Operation.Required,
                    "local", null, null, null, Array.Empty<CapabilityRequestBinding>(), match.Operation.Id, null, match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind)
                {
                    InputOperationIds = match.Operation.InputOperationIds
                });
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
            var conditionalActivationMode = string.Empty;
            if (match.Status == "conditional"
                && !TryBuildConditionalActivation(
                    match.CatalogIds.Select(id => entries[id]).ToArray(),
                    match.Operation.AllowNoEffectOutcome,
                    match.ConditionalActivationMode,
                    out conditionalBranches,
                    out conditionalActivationMode))
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
                        conditionalActivationMode,
                        match.Operation.Id,
                        match.DecisionOperationId!,
                        conditionalBranches[catalogId])
                    {
                        DecisionOutputPath = match.DecisionOutputPath ?? string.Empty,
                        AllowedValues = match.DecisionAllowedValues ?? Array.Empty<string>(),
                        NoEffectValues = match.DecisionNoEffectValues ?? Array.Empty<string>(),
                        DecisionContractSource = match.DecisionContractSource ?? CapabilityDecisionContractSource,
                        DecisionProducerCatalogId = match.DecisionProducerCatalogId ?? string.Empty,
                        DecisionInputOperationIds = evaluation.OperationMatches
                            .FirstOrDefault(candidate => string.Equals(
                                candidate.Operation.Id,
                                match.DecisionOperationId,
                                StringComparison.Ordinal))?
                            .Operation.InputOperationIds ?? Array.Empty<string>()
                    }
                    : null;
                resolved.Add(new ResolvedCapability(id, match.Operation.Description, match.Operation.Required,
                    entry.Resolution, entry.Server, entry.Kind, entry.Method, entry.RequestBindings,
                    match.Operation.Id, catalogId, match.Status == "conditional" && !isConditionalBranch ? "composed" : match.Status,
                    match.Operation.ExecutionKind, match.Operation.ExternalEffectKind, activation, entry.Description)
                {
                    InputOperationIds = match.Operation.InputOperationIds
                });
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
                TryBuildConditionalActivation(
                    selectedIds.Select(id => entries[id]).ToArray(),
                    match.Operation.AllowNoEffectOutcome,
                    match.ConditionalActivationMode,
                    out conditionalBranches,
                    out _);

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
        var occurrences = new Dictionary<string, List<ArtifactMaterializerOccurrence>>(StringComparer.Ordinal);
        foreach (var match in evaluation.OperationMatches.Where(static match => match.Status is "matched" or "composed" or "conditional"))
        {
            var selected = match.CatalogIds
                .Where(entries.ContainsKey)
                .Select(id => entries[id])
                .ToArray();
            foreach (var materializer in selected.Where(IsArtifactMaterializer))
            {
                if (!occurrences.TryGetValue(materializer.Id, out var values))
                {
                    values = [];
                    occurrences[materializer.Id] = values;
                }
                // A standalone match proves an independently requested materialization.
                // Inside a larger composition the same catalog entry may be a repeated
                // prerequisite or an unrelated model-selected extra; retain it there only
                // when no standalone owner or stronger declared data-flow owner exists.
                values.Add(new ArtifactMaterializerOccurrence(
                    match.Operation.Id,
                    selected.Length == 1,
                    selected.SelectMany(GetRequiredArtifactRequirements)
                        .Select(static requirement => requirement.Kind)
                        .ToHashSet(StringComparer.Ordinal)));
            }
        }

        var matchesByOperationId = evaluation.OperationMatches.ToDictionary(
            static match => match.Operation.Id,
            StringComparer.Ordinal);
        var decisionProducerOperationIds = evaluation.OperationMatches
            .Where(static match => string.Equals(match.Status, "conditional", StringComparison.Ordinal)
                                   && !string.IsNullOrWhiteSpace(match.DecisionOutputPath))
            .Select(static match => match.DecisionOperationId)
            .Where(static operationId => !string.IsNullOrWhiteSpace(operationId))
            .Select(static operationId => operationId!)
            .ToHashSet(StringComparer.Ordinal);
        var retained = new HashSet<(string OperationId, string CatalogId)>();
        foreach (var (catalogId, catalogOccurrences) in occurrences)
        {
            var distinctOccurrences = catalogOccurrences
                .GroupBy(static occurrence => occurrence.OperationId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            var explicitSources = distinctOccurrences
                .Where(static occurrence => occurrence.IsOwnedSource)
                .ToArray();
            if (explicitSources.Length > 0)
            {
                foreach (var source in explicitSources)
                    retained.Add((source.OperationId, catalogId));
                continue;
            }

            // Prerequisite closure can repeat one physical materializer on multiple
            // downstream operations. Prefer roots proven by the declared operation
            // data-flow graph, then a uniquely grounded decision producer, then one
            // unique maximal artifact consumer. Parallel roots remain independent
            // when contracts do not prove that they share one materialization.
            var operationIds = distinctOccurrences
                .Select(static occurrence => occurrence.OperationId)
                .ToHashSet(StringComparer.Ordinal);
            var rootOccurrences = distinctOccurrences.Where(occurrence =>
            {
                if (!matchesByOperationId.TryGetValue(occurrence.OperationId, out var match))
                    return false;
                var upstreamOperationIds = GetDeclaredUpstreamOperationIds(
                    match.Operation,
                    matchesByOperationId);
                return !upstreamOperationIds.Any(operationIds.Contains);
            }).ToArray();
            if (rootOccurrences.Length == 1)
            {
                retained.Add((rootOccurrences[0].OperationId, catalogId));
                continue;
            }

            var groundedDecisionSources = rootOccurrences
                .Where(occurrence => decisionProducerOperationIds.Contains(occurrence.OperationId))
                .ToArray();
            if (groundedDecisionSources.Length == 1)
            {
                retained.Add((groundedDecisionSources[0].OperationId, catalogId));
                continue;
            }

            var maximalConsumers = rootOccurrences
                .Where(candidate => !rootOccurrences.Any(other =>
                    !ReferenceEquals(candidate, other)
                    && other.RequiredArtifactKinds.IsProperSupersetOf(candidate.RequiredArtifactKinds)))
                .ToArray();
            if (maximalConsumers.Length == 1)
            {
                retained.Add((maximalConsumers[0].OperationId, catalogId));
                continue;
            }

            foreach (var root in rootOccurrences)
                retained.Add((root.OperationId, catalogId));
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
            : BuildCapabilitySchemaFields(
                    McpToolContractEnricher.GetAuthoritativeOutputSchema(tool),
                    requiredOnly: false)
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
                        $"Required MCP-declared artifact of kind {artifact.Kind}.",
                        Array.Empty<string>()),
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
        sb.AppendLine("Capabilities with activation mode exactly_one are mutually exclusive alternatives: put every member in a distinct literal-value case of one expression-based switch. Capabilities with activation mode all_on_value are one ordered conditional composition: put every member in the same branch_value case and execute them once in the listed order. Drive either mode from the named decision operation. Emit every no_effect_value as an explicit non-mutating case and do not put any external write in the default branch. When decision_contract_source=structured_output, the exact producer capability must declare strict structured_output.schema_inline with the decision field and allowed_values enum at decision_output_path. When decision_contract_source=local_decision, implement the one locked decision.evaluate producer with all fields owned by that operation; every declared input operation must participate in its boolean case conditions and any default must be the declared no-effect value. The decision must be computed from runtime results; never ask the human to predict it during generation.");
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
            if (capability.InputOperationIds is { Count: > 0 })
                sb.Append(" input_operation_ids=[").Append(string.Join(",", capability.InputOperationIds)).Append(']');
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
                        .Append(" decision_output_path=").Append(activation.DecisionOutputPath)
                        .Append(" allowed_values=").Append(string.Join('|', activation.AllowedValues))
                        .Append(" no_effect_values=").Append(string.Join('|', activation.NoEffectValues))
                        .Append(" contract_source=").Append(activation.DecisionContractSource)
                        .Append(" producer_catalog_id=").Append(activation.DecisionProducerCatalogId)
                        .Append(" decision_input_operation_ids=").Append(string.Join('|', activation.DecisionInputOperationIds))
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
                ["input_operation_ids"] = BuildStringArrayJson(capability.InputOperationIds ?? Array.Empty<string>()),
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
                        ["decision_output_path"] = capability.Activation.DecisionOutputPath,
                        ["allowed_values"] = new JsonArray(capability.Activation.AllowedValues
                            .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                        ["no_effect_values"] = new JsonArray(capability.Activation.NoEffectValues
                            .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                        ["decision_contract_source"] = capability.Activation.DecisionContractSource,
                        ["decision_producer_catalog_id"] = capability.Activation.DecisionProducerCatalogId,
                        ["decision_input_operation_ids"] = new JsonArray(capability.Activation.DecisionInputOperationIds
                            .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
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
            ["effective_external_write_confirmation_policy"] = preflight.EffectiveExternalWriteConfirmationPolicy,
            ["external_write_confirmation_policy_source"] = preflight.ExternalWriteConfirmationPolicySource,
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

        var workflowSteps = document.Workflows
            .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Select(step => (Workflow: workflow.Key, Step: step)))
            .ToArray();
        var allSteps = workflowSteps.Select(static item => item.Step).ToArray();
        var allCalls = allSteps.Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)).ToArray();

        foreach (var group in groups)
        {
            var capabilities = group.ToArray();
            var activationModes = capabilities
                .Select(static capability => capability.Activation!.Mode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var activationMode = activationModes.Length == 1 ? activationModes[0] : string.Empty;
            var declaredAllowedValues = capabilities[0].Activation?.AllowedValues
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            var declaredNoEffectValues = capabilities[0].Activation?.NoEffectValues
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            var branchValues = capabilities.Select(static capability => capability.Activation!.BranchValue)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var topologyValid = activationMode switch
            {
                ConditionalExactlyOneActivationMode => branchValues.Length == capabilities.Length,
                ConditionalAllOnValueActivationMode => branchValues.Length == 1 && declaredNoEffectValues.Length > 0,
                _ => false
            };
            if (!topologyValid
                || capabilities.Any(static capability => string.IsNullOrWhiteSpace(capability.Activation?.DecisionOutputPath))
                || capabilities.Select(static capability => capability.Activation!.DecisionOutputPath)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || capabilities.Select(static capability => capability.Activation!.DecisionContractSource)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || capabilities.Select(static capability => capability.Activation!.DecisionProducerCatalogId)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || declaredNoEffectValues.Any(value => branchValues.Contains(value, StringComparer.Ordinal))
                || !branchValues.Concat(declaredNoEffectValues).Order(StringComparer.Ordinal)
                    .SequenceEqual(declaredAllowedValues, StringComparer.Ordinal))
            {
                ThrowInvalidConditionalActivation(group.Key,
                    "The locked conditional capability group is malformed or has an invalid activation topology.",
                    capabilities,
                    validationIssue: "conditional_capability_contract_invalid",
                    repairScope: "capability_contract");
            }

            var matchingCalls = allCalls.Where(call => capabilities.Any(capability => McpStepMatchesCapability(
                call,
                capability.Server!,
                capability.Kind!,
                capability.Method!,
                capability.RequestBindings))).ToArray();
            var mutatingDefault = workflowSteps
                .Where(static item => string.Equals(item.Step.Type, "switch", StringComparison.Ordinal))
                .Select(item => EvaluateMutatingConditionalDefault(
                    item.Workflow,
                    item.Step,
                    matchingCalls,
                    call => preflight.RequiredMcpCapabilities
                        .Where(static capability => capability.ExternalEffectKind is "write" or "lifecycle")
                        .Any(capability => McpStepMatchesCapability(
                            call,
                            capability.Server!,
                            capability.Kind!,
                            capability.Method!,
                            capability.RequestBindings))))
                .FirstOrDefault(static evaluation => evaluation != null);
            if (mutatingDefault != null)
            {
                ThrowInvalidConditionalActivation(
                    group.Key,
                    mutatingDefault.Message,
                    capabilities,
                    mutatingDefault.ValidationIssue,
                    mutatingDefault.RepairScope,
                    mutatingDefault.Workflow,
                    mutatingDefault.SwitchId);
            }

            var evaluations = workflowSteps
                .Where(static item => string.Equals(item.Step.Type, "switch", StringComparison.Ordinal))
                .Select(item =>
                {
                    var candidateCalls = EnumerateConditionalSwitchCalls(item.Step)
                        .Where(call => capabilities.Any(capability => McpStepMatchesCapability(
                            call,
                            capability.Server!,
                            capability.Kind!,
                            capability.Method!,
                            capability.RequestBindings)))
                        .ToArray();
                    return new ConditionalSwitchCandidate(
                        EvaluateConditionalSwitch(
                            document,
                            item.Workflow,
                            item.Step,
                            capabilities,
                            candidateCalls,
                            preflight),
                        candidateCalls);
                })
                .ToArray();
            var validSwitches = evaluations.Where(static candidate => candidate.Evaluation.IsValid).ToArray();
            if (validSwitches.Length != 1)
            {
                var failure = evaluations
                    .Select(static candidate => candidate.Evaluation)
                    .Where(static evaluation => evaluation.ContainedGroupCallCount > 0)
                    .OrderByDescending(static evaluation => evaluation.ContainedGroupCallCount)
                    .ThenByDescending(static evaluation => evaluation.ValidationProgress)
                    .FirstOrDefault();
                ThrowInvalidConditionalActivation(group.Key,
                    validSwitches.Length == 0
                        ? failure?.Message
                          ?? "Conditional capabilities must be placed in the declared cases of one expression-based switch with no mutating default branch."
                        : "Conditional capabilities were associated with more than one switch.",
                    capabilities,
                    validationIssue: validSwitches.Length == 0
                        ? failure?.ValidationIssue ?? "conditional_switch_missing"
                        : "conditional_switch_ambiguous",
                    repairScope: validSwitches.Length == 0
                        ? failure?.RepairScope ?? "leaf_topology"
                        : "workflow_topology",
                    workflow: validSwitches.Length == 0 ? failure?.Workflow : null,
                    switchId: validSwitches.Length == 0 ? failure?.SwitchId : null);
            }

            var selectedConditionalCalls = validSwitches[0].Calls
                .ToHashSet(ReferenceEqualityComparer.Instance);
            var otherCapabilities = preflight.RequiredMcpCapabilities
                .Where(capability => !string.Equals(
                    capability.Activation?.Group,
                    group.Key,
                    StringComparison.Ordinal))
                .ToArray();
            var otherCalls = allCalls
                .Where(call => !selectedConditionalCalls.Contains(call))
                .ToArray();
            var attributedOtherCalls = AttributeCapabilityCalls(otherCapabilities, otherCalls);
            var unownedMatchingCalls = matchingCalls
                .Where(call => !selectedConditionalCalls.Contains(call)
                               && !attributedOtherCalls.Contains(call))
                .ToArray();
            if (unownedMatchingCalls.Length > 0)
            {
                var callOwners = workflowSteps
                    .Where(item => unownedMatchingCalls.Contains(item.Step, ReferenceEqualityComparer.Instance))
                    .Select(static item => item.Workflow)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                ThrowInvalidConditionalActivation(group.Key,
                    "Every conditional capability must occur exactly once, and every physically identical call outside its switch must belong to another locked operation.",
                    capabilities,
                    validationIssue: "conditional_call_cardinality_invalid",
                    repairScope: callOwners.Length == 1 ? "leaf_topology" : "workflow_topology",
                    workflow: callOwners.Length == 1 ? callOwners[0] : null);
            }
        }
    }

    private static IEnumerable<StepDef> EnumerateConditionalSwitchCalls(StepDef step)
        => (step.Cases ?? [])
            .SelectMany(static @case => EnumerateSteps(@case.Steps))
            .Concat(EnumerateSteps(step.Default ?? []))
            .Where(static candidate => string.Equals(candidate.Type, "mcp.call", StringComparison.Ordinal));

    private static IReadOnlySet<StepDef> AttributeCapabilityCalls(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<StepDef> calls)
    {
        var capabilityByCall = Enumerable.Repeat(-1, calls.Count).ToArray();
        for (var capabilityIndex = 0; capabilityIndex < capabilities.Count; capabilityIndex++)
        {
            var visitedCalls = new bool[calls.Count];
            _ = TryAttributeCapabilityCall(
                capabilityIndex,
                capabilities,
                calls,
                capabilityByCall,
                visitedCalls);
        }

        var attributed = new HashSet<StepDef>(ReferenceEqualityComparer.Instance);
        for (var callIndex = 0; callIndex < calls.Count; callIndex++)
        {
            if (capabilityByCall[callIndex] >= 0)
                attributed.Add(calls[callIndex]);
        }
        return attributed;
    }

    private static bool TryAttributeCapabilityCall(
        int capabilityIndex,
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<StepDef> calls,
        int[] capabilityByCall,
        bool[] visitedCalls)
    {
        var capability = capabilities[capabilityIndex];
        for (var callIndex = 0; callIndex < calls.Count; callIndex++)
        {
            if (visitedCalls[callIndex]
                || !McpStepMatchesCapability(
                    calls[callIndex],
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    capability.RequestBindings))
            {
                continue;
            }

            visitedCalls[callIndex] = true;
            if (capabilityByCall[callIndex] < 0
                || TryAttributeCapabilityCall(
                    capabilityByCall[callIndex],
                    capabilities,
                    calls,
                    capabilityByCall,
                    visitedCalls))
            {
                capabilityByCall[callIndex] = capabilityIndex;
                return true;
            }
        }
        return false;
    }

    private static ConditionalSwitchEvaluation EvaluateConditionalSwitch(
        WorkflowDocument document,
        string workflowName,
        StepDef step,
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<StepDef> groupCalls,
        CapabilityPreflightResult preflight)
    {
        var structure = EvaluateConditionalSwitchStructure(
            workflowName,
            step,
            capabilities,
            groupCalls,
            static (call, capability) => McpStepMatchesCapability(
                call,
                capability.Server!,
                capability.Kind!,
                capability.Method!,
                capability.RequestBindings),
            static capability => capability.Activation!,
            call => preflight.RequiredMcpCapabilities
                .Where(static capability => capability.ExternalEffectKind is "write" or "lifecycle")
                .Any(capability => McpStepMatchesCapability(
                    call,
                    capability.Server!,
                    capability.Kind!,
                    capability.Method!,
                    capability.RequestBindings)));
        if (!structure.IsValid)
            return structure;

        var activation = capabilities[0].Activation!;
        var decisionOperationId = activation.DecisionOperationId;
        var decisionCapabilities = preflight.Capabilities
            .Where(capability => string.Equals(capability.OperationId, decisionOperationId, StringComparison.Ordinal)
                                 && string.Equals(
                                     capability.CatalogId,
                                     activation.DecisionProducerCatalogId,
                                     StringComparison.Ordinal))
            .ToArray();
        if (decisionCapabilities.Length == 0)
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_decision_producer_missing",
                RepairScope = "decision_producer",
                Message = "The declared conditional decision producer is not present in the generated workflow.",
                ValidationProgress = 70
            };
        }

        var decisionSteps = groupCalls
            .Concat(step.Cases!.SelectMany(static @case => EnumerateSteps(@case.Steps)))
            .Concat(EnumerateSteps(step.Default ?? []))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var sources = document.Workflows
            .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                .Concat(EnumerateSteps(workflow.Value.Finally))
                .Where(candidate => !decisionSteps.Contains(candidate)
                                    && decisionCapabilities.Any(capability => StepMatchesDecisionProducer(
                                        candidate,
                                        capability)))
                .Select(candidate => (Workflow: workflow.Key, Step: candidate)))
            .ToArray();
        if (sources.Length == 0)
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_decision_producer_missing",
                RepairScope = "decision_producer",
                Message = "The conditional switch has no generated step matching its declared decision producer.",
                ValidationProgress = 80
            };
        }

        if (string.Equals(
                activation.DecisionContractSource,
                LocalDecisionContractSource,
                StringComparison.Ordinal)
            && !LocalDecisionConditionsCoverLockedInputs(document, sources, activation, preflight))
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_local_decision_inputs_unproven",
                RepairScope = "decision_producer",
                Message = "The local decision evaluator must contain every locked decision field and derive its boolean conditions from every declared upstream operation.",
                ValidationProgress = 85
            };
        }

        if (!ConditionalDecisionExpressionDependsOnSource(
                document,
                BuildWorkflowArtifactCallerIndex(document),
                sources,
                workflowName,
                step.Expr!,
                [],
                activation))
        {
            return structure with
            {
                IsValid = false,
                ValidationIssue = "conditional_decision_lineage_unproven",
                RepairScope = "main_decision_routing",
                Message = "The switch shape is valid, but its discriminator does not trace unchanged through declared workflow inputs and transparent projections to the locked decision producer.",
                ValidationProgress = 90
            };
        }

        return structure with { ValidationProgress = 100 };
    }

    private static bool LocalDecisionConditionsCoverLockedInputs(
        WorkflowDocument document,
        IReadOnlyList<(string Workflow, StepDef Step)> evaluators,
        McpCapabilityActivation activation,
        CapabilityPreflightResult preflight)
    {
        if (evaluators.Count != 1
            || evaluators[0].Step.Input?["decisions"] is not JsonObject decisions)
        {
            return false;
        }

        var expectedFields = preflight.RequiredMcpCapabilities
            .Where(capability => string.Equals(
                                     capability.Activation?.DecisionContractSource,
                                     LocalDecisionContractSource,
                                     StringComparison.Ordinal)
                                 && string.Equals(
                                     capability.Activation?.DecisionOperationId,
                                     activation.DecisionOperationId,
                                     StringComparison.Ordinal))
            .Select(capability => GetDecisionBoundaryFieldName(capability.Activation!.DecisionOutputPath))
            .Where(static field => field.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!decisions.Select(static item => item.Key).Order(StringComparer.Ordinal)
            .SequenceEqual(expectedFields, StringComparer.Ordinal))
        {
            return false;
        }

        var conditions = decisions
            .SelectMany(static item => (item.Value as JsonObject)?["cases"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(static item => item["when"])
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var expression) ? expression : null)
            .Where(static expression => !string.IsNullOrWhiteSpace(expression))
            .Select(static expression => expression!)
            .ToArray();
        if (conditions.Length == 0)
            return false;

        var workflowCallers = BuildWorkflowArtifactCallerIndex(document);
        foreach (var operationId in activation.DecisionInputOperationIds.Distinct(StringComparer.Ordinal))
        {
            var upstreamCapabilities = preflight.Capabilities
                .Where(capability => capability.Required
                                     && GetResolvedCapabilityOperationIds(capability)
                                         .Contains(operationId, StringComparer.Ordinal)
                                     && capability.Resolution is "mcp" or "native")
                .ToArray();
            if (upstreamCapabilities.Length == 0)
                return false;
            var upstreamSteps = document.Workflows
                .SelectMany(workflow => EnumerateSteps(workflow.Value.Steps)
                    .Concat(EnumerateSteps(workflow.Value.Finally))
                    .Where(step => upstreamCapabilities.Any(capability => StepMatchesDecisionProducer(step, capability)))
                    .Select(step => (Workflow: workflow.Key, Step: step)))
                .ToArray();
            if (upstreamSteps.Length == 0
                || !conditions.Any(condition => LocalDecisionExpressionDependsOnSources(
                    document,
                    workflowCallers,
                    upstreamSteps,
                    evaluators[0].Workflow,
                    condition,
                    new HashSet<string>(StringComparer.Ordinal))))
            {
                return false;
            }
        }
        return true;
    }

    private static bool LocalDecisionExpressionDependsOnSources(
        WorkflowDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<(string Workflow, StepDef Step)> sources,
        string workflowName,
        string expression,
        HashSet<string> visited)
    {
        foreach (Match reference in Regex.Matches(
                     expression,
                     @"data\.(?:steps|inputs)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*",
                     RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            if (LocalDecisionReferenceDependsOnSources(
                    document,
                    workflowCallers,
                    sources,
                    workflowName,
                    reference.Value,
                    [],
                    visited))
            {
                return true;
            }
        }
        return false;
    }

    private static bool LocalDecisionReferenceDependsOnSources(
        WorkflowDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<(string Workflow, StepDef Step)> sources,
        string workflowName,
        string reference,
        IReadOnlyList<string> appendedPath,
        HashSet<string> visited)
    {
        var path = TrimWorkflowExpression(reference);
        if (appendedPath.Count > 0)
            path += "." + string.Join('.', appendedPath);
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
                    && LocalDecisionReferenceDependsOnSources(
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
            if (sourceStep.Type is "set" or "assert.non_null")
            {
                var value = ResolveInstancePath(sourceStep.Input, remainingPath);
                return value is JsonValue setValue
                       && setValue.TryGetValue<string>(out var setExpression)
                       && !string.IsNullOrWhiteSpace(setExpression)
                       && LocalDecisionExpressionDependsOnSources(
                           document,
                           workflowCallers,
                           sources,
                           workflowName,
                           setExpression,
                           visited);
            }

            if (!string.Equals(sourceStep.Type, "workflow.call", StringComparison.Ordinal))
                return sourceStep.Input is not null
                       && EnumerateStringValues(sourceStep.Input).Any(value =>
                           LocalDecisionExpressionDependsOnSources(
                               document,
                               workflowCallers,
                               sources,
                               workflowName,
                               value,
                               visited));

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

            return LocalDecisionReferenceDependsOnSources(
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

    private static IEnumerable<string> EnumerateStringValues(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            yield return text;
            yield break;
        }
        if (node is JsonObject obj)
        {
            foreach (var child in obj.Select(static item => item.Value).Where(static item => item is not null))
                foreach (var nestedText in EnumerateStringValues(child!))
                    yield return nestedText;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static item => item is not null))
                foreach (var nestedText in EnumerateStringValues(child!))
                    yield return nestedText;
        }
    }

    private static ConditionalSwitchEvaluation EvaluateConditionalSwitchStructure<TCapability>(
        string workflowName,
        StepDef step,
        IReadOnlyList<TCapability> capabilities,
        IReadOnlyList<StepDef> groupCalls,
        Func<StepDef, TCapability, bool> matchesCapability,
        Func<TCapability, McpCapabilityActivation> getActivation,
        Func<StepDef, bool> isMutatingCall)
    {
        var nestedCalls = (step.Cases ?? [])
            .SelectMany(static @case => EnumerateSteps(@case.Steps))
            .Concat(EnumerateSteps(step.Default ?? []))
            .Where(static candidate => string.Equals(candidate.Type, "mcp.call", StringComparison.Ordinal))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var containedGroupCallCount = groupCalls.Count(nestedCalls.Contains);
        var initial = new ConditionalSwitchEvaluation(
            false,
            "conditional_switch_shape_invalid",
            "leaf_topology",
            "The conditional capability group requires one expression-based switch with literal value cases.",
            workflowName,
            step.Id,
            containedGroupCallCount,
            10);

        if (string.IsNullOrWhiteSpace(step.Expr)
            || step.Cases == null
            || step.Cases.Any(static @case => !string.IsNullOrWhiteSpace(@case.When)))
        {
            return initial;
        }

        var activation = getActivation(capabilities[0]);
        if (!ConditionalDecisionExpressionMatchesDeclaredPath(step.Expr, activation.DecisionOutputPath))
        {
            return initial with
            {
                ValidationIssue = "conditional_decision_lineage_unproven",
                RepairScope = "main_decision_routing",
                Message = "The switch discriminator does not preserve the declared decision boundary field name.",
                ValidationProgress = 20
            };
        }

        var matchedCalls = new HashSet<StepDef>(ReferenceEqualityComparer.Instance);
        foreach (var capability in capabilities)
        {
            var capabilityActivation = getActivation(capability);
            var matchingCases = step.Cases.Where(@case =>
                    string.Equals(@case.Value, capabilityActivation.BranchValue, StringComparison.Ordinal)
                    && EnumerateSteps(@case.Steps).Any(call =>
                        string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                        && matchesCapability(call, capability)))
                .ToArray();
            if (matchingCases.Length != 1)
            {
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "Each conditional capability must occur in exactly one case whose literal value matches its declared branch value.",
                    ValidationProgress = 30
                };
            }

            var calls = EnumerateSteps(matchingCases[0].Steps)
                .Where(call => string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                               && matchesCapability(call, capability))
                .ToArray();
            if (calls.Length != 1)
            {
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "Each conditional case must contain exactly one occurrence of its matching capability.",
                    ValidationProgress = 30
                };
            }
            matchedCalls.Add(calls[0]);
        }

        if (matchedCalls.Count != groupCalls.Count || groupCalls.Any(call => !matchedCalls.Contains(call)))
        {
            return initial with
            {
                ValidationIssue = "conditional_branch_placement_invalid",
                Message = "Conditional capability calls must not occur outside their one declared switch case.",
                ValidationProgress = 40
            };
        }

        var activationMode = activation.Mode;
        if (string.Equals(activationMode, ConditionalAllOnValueActivationMode, StringComparison.Ordinal))
        {
            var effectValue = activation.BranchValue;
            var effectCases = step.Cases.Where(@case => string.Equals(
                @case.Value,
                effectValue,
                StringComparison.Ordinal)).ToArray();
            if (effectCases.Length != 1)
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "The ordered conditional composition must have exactly one declared effect case.",
                    ValidationProgress = 40
                };
            var orderedCalls = EnumerateSteps(effectCases[0].Steps)
                .Where(candidate => groupCalls.Contains(candidate, ReferenceEqualityComparer.Instance))
                .ToArray();
            if (orderedCalls.Length != capabilities.Count)
                return initial with
                {
                    ValidationIssue = "conditional_branch_placement_invalid",
                    Message = "The ordered conditional composition does not contain every declared capability exactly once.",
                    ValidationProgress = 40
                };
            for (var index = 0; index < capabilities.Count; index++)
            {
                var capability = capabilities[index];
                if (!matchesCapability(orderedCalls[index], capability))
                {
                    return initial with
                    {
                        ValidationIssue = "conditional_branch_order_invalid",
                        Message = "The ordered conditional composition does not preserve its declared capability order.",
                        ValidationProgress = 40
                    };
                }
            }
        }

        var allowedValues = activation.AllowedValues.ToHashSet(StringComparer.Ordinal);
        if (step.Cases.Any(@case => string.IsNullOrWhiteSpace(@case.Value) || !allowedValues.Contains(@case.Value)))
        {
            return initial with
            {
                ValidationIssue = "conditional_case_value_invalid",
                Message = "Every switch case must use one literal value from the declared decision enum.",
                ValidationProgress = 50
            };
        }
        if (allowedValues.Any(value => step.Cases.Count(@case => string.Equals(
                @case.Value,
                value,
                StringComparison.Ordinal)) != 1))
        {
            return initial with
            {
                ValidationIssue = "conditional_case_coverage_invalid",
                Message = "The switch must contain exactly one literal case for every declared decision value.",
                ValidationProgress = 50
            };
        }

        foreach (var noEffectValue in activation.NoEffectValues)
        {
            var noEffectCases = step.Cases.Where(@case => string.Equals(
                @case.Value,
                noEffectValue,
                StringComparison.Ordinal)).ToArray();
            if (noEffectCases.Length != 1
                || EnumerateSteps(noEffectCases[0].Steps).Any(call =>
                    string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                    && isMutatingCall(call)))
            {
                return initial with
                {
                    ValidationIssue = "conditional_no_effect_branch_mutates",
                    Message = "Every declared no-effect value must have exactly one case containing no write or lifecycle capability.",
                    ValidationProgress = 60
                };
            }
        }

        if (EnumerateSteps(step.Default ?? []).Any(call =>
                string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                && isMutatingCall(call)))
        {
            return initial with
            {
                ValidationIssue = "conditional_default_mutates",
                Message = "The conditional switch default branch must not execute a write or lifecycle capability.",
                ValidationProgress = 60
            };
        }

        return initial with
        {
            IsValid = true,
            ValidationIssue = string.Empty,
            Message = string.Empty,
            ValidationProgress = 70
        };
    }

    private static ConditionalSwitchEvaluation? EvaluateMutatingConditionalDefault(
        string workflowName,
        StepDef step,
        IReadOnlyList<StepDef> groupCalls,
        Func<StepDef, bool> isMutatingCall)
    {
        var nestedCalls = (step.Cases ?? [])
            .SelectMany(static @case => EnumerateSteps(@case.Steps))
            .Concat(EnumerateSteps(step.Default ?? []))
            .Where(static candidate => string.Equals(candidate.Type, "mcp.call", StringComparison.Ordinal))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var containedGroupCallCount = groupCalls.Count(nestedCalls.Contains);
        if (containedGroupCallCount == 0
            || !EnumerateSteps(step.Default ?? []).Any(call =>
                string.Equals(call.Type, "mcp.call", StringComparison.Ordinal)
                && isMutatingCall(call)))
        {
            return null;
        }

        return new ConditionalSwitchEvaluation(
            false,
            "conditional_default_mutates",
            "leaf_topology",
            "The conditional switch default branch must not execute a write or lifecycle capability.",
            workflowName,
            step.Id,
            containedGroupCallCount,
            60);
    }

    private sealed record ConditionalSwitchEvaluation(
        bool IsValid,
        string ValidationIssue,
        string RepairScope,
        string Message,
        string Workflow,
        string SwitchId,
        int ContainedGroupCallCount,
        int ValidationProgress);

    private sealed record ConditionalSwitchCandidate(
        ConditionalSwitchEvaluation Evaluation,
        IReadOnlyList<StepDef> Calls);

    private static void ValidateConditionalCapabilityTopologyInLeaf(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument document)
    {
        var groups = spec.PlannedTools
            .Where(static tool => tool.Required && tool.Activation != null)
            .GroupBy(static tool => tool.Activation!.Group, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length == 0)
            return;

        var workflow = document.Workflows[workflowName];
        var allSteps = EnumerateSteps(workflow.Steps)
            .Concat(EnumerateSteps(workflow.Finally))
            .ToArray();
        var allCalls = allSteps
            .Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal))
            .ToArray();
        var mutatingTools = spec.PlannedTools
            .Where(static tool => tool.Required && tool.ExternalEffectKind is "write" or "lifecycle")
            .ToArray();

        foreach (var group in groups)
        {
            var capabilities = group.ToArray();
            var groupCalls = allCalls
                .Where(call => capabilities.Any(capability => WorkflowStepMatchesPlannedMcpToolCall(
                    call,
                    capability)))
                .ToArray();
            var mutatingDefault = allSteps
                .Where(static step => string.Equals(step.Type, "switch", StringComparison.Ordinal))
                .Select(step => EvaluateMutatingConditionalDefault(
                    workflowName,
                    step,
                    groupCalls,
                    call => mutatingTools.Any(tool => WorkflowStepMatchesPlannedMcpToolCall(call, tool))))
                .FirstOrDefault(static evaluation => evaluation != null);
            if (mutatingDefault != null)
            {
                ThrowInvalidLeafConditionalActivation(
                    group.Key,
                    mutatingDefault.Message,
                    capabilities,
                    mutatingDefault.ValidationIssue,
                    workflowName,
                    mutatingDefault.SwitchId);
            }
            if (groupCalls.Length != capabilities.Length)
            {
                ThrowInvalidLeafConditionalActivation(
                    group.Key,
                    "Every conditional capability must occur exactly once in the generated leaf.",
                    capabilities,
                    "conditional_call_cardinality_invalid",
                    workflowName);
            }

            var evaluations = allSteps
                .Where(static step => string.Equals(step.Type, "switch", StringComparison.Ordinal))
                .Select(step => EvaluateConditionalSwitchStructure(
                    workflowName,
                    step,
                    capabilities,
                    groupCalls,
                    static (call, capability) => WorkflowStepMatchesPlannedMcpToolCall(call, capability),
                    static capability => capability.Activation!,
                    call => mutatingTools.Any(tool => WorkflowStepMatchesPlannedMcpToolCall(call, tool))))
                .ToArray();
            var validSwitches = evaluations.Where(static evaluation => evaluation.IsValid).ToArray();
            if (validSwitches.Length == 1)
                continue;

            var failure = evaluations
                .Where(static evaluation => evaluation.ContainedGroupCallCount > 0)
                .OrderByDescending(static evaluation => evaluation.ContainedGroupCallCount)
                .ThenByDescending(static evaluation => evaluation.ValidationProgress)
                .FirstOrDefault();
            ThrowInvalidLeafConditionalActivation(
                group.Key,
                validSwitches.Length == 0
                    ? failure?.Message
                      ?? "Conditional capabilities must be placed in one expression-based switch with exact literal cases and a non-mutating default branch."
                    : "Conditional capabilities were associated with more than one switch.",
                capabilities,
                validSwitches.Length == 0
                    ? failure?.ValidationIssue ?? "conditional_switch_missing"
                    : "conditional_switch_ambiguous",
                workflowName,
                validSwitches.Length == 0 ? failure?.SwitchId : null);
        }
    }

    private static void ThrowInvalidLeafConditionalActivation(
        string group,
        string reason,
        IReadOnlyList<PipelinePlannedTool> capabilities,
        string validationIssue,
        string workflow,
        string? switchId = null)
        => throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Generated leaf workflow does not safely implement conditional capability group '{group}': {reason}",
            details: new JsonObject
            {
                ["phase"] = "pipeline_leaf_generation",
                ["reason"] = "conditional_activation_invalid",
                ["validation_issue"] = validationIssue,
                ["repair_scope"] = "leaf_topology",
                ["activation_group"] = group,
                ["workflow"] = workflow,
                ["switch_id"] = switchId,
                ["decision_operation_id"] = capabilities
                    .Select(static capability => capability.Activation?.DecisionOperationId)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["decision_field"] = capabilities
                    .Select(static capability => GetDecisionBoundaryFieldName(
                        capability.Activation?.DecisionOutputPath ?? string.Empty))
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["message"] = reason,
                ["branches"] = new JsonArray(capabilities.Select(capability => (JsonNode)new JsonObject
                {
                    ["branch_value"] = capability.Activation?.BranchValue,
                    ["operation_ids"] = BuildStringArrayJson(capability.OperationIds),
                    ["catalog_ids"] = BuildStringArrayJson(capability.CatalogIds),
                    ["server"] = capability.Server,
                    ["kind"] = capability.Kind,
                    ["method"] = capability.Method,
                    ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings)
                }).ToArray())
            });

    private static bool StepMatchesDecisionProducer(StepDef step, ResolvedCapability capability)
    {
        if (string.Equals(capability.Resolution, "native", StringComparison.Ordinal))
            return string.Equals(step.Type, capability.Method, StringComparison.Ordinal);

        return string.Equals(capability.Resolution, "mcp", StringComparison.Ordinal)
               && McpStepMatchesCapability(
                   step,
                   capability.Server!,
                   capability.Kind!,
                   capability.Method!,
                   capability.RequestBindings);
    }

    private static bool ConditionalDecisionExpressionMatchesDeclaredPath(
        string expression,
        string decisionOutputPath)
    {
        if (string.IsNullOrWhiteSpace(decisionOutputPath)
            || !decisionOutputPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = string.Join('.', decisionOutputPath.Split('/').Skip(1)
            .Select(DecodeJsonPointerToken)
            .Where(static segment => segment.Length > 0));
        if (suffix.Length == 0)
            return false;

        var path = TrimWorkflowExpression(expression);
        var boundaryField = GetDecisionBoundaryFieldName(decisionOutputPath);
        return string.Equals(path, suffix, StringComparison.Ordinal)
               || path.EndsWith('.' + suffix, StringComparison.Ordinal)
               || boundaryField.Length > 0
               && (string.Equals(path, "data.inputs." + boundaryField, StringComparison.Ordinal)
                   || path.EndsWith('.' + boundaryField, StringComparison.Ordinal));
    }

    private static bool ConditionalDecisionExpressionDependsOnSource(
        WorkflowDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<(string Workflow, StepDef Step)> sources,
        string workflowName,
        string expression,
        IReadOnlyList<string> appendedPath,
        McpCapabilityActivation activation,
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
                        activation,
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
            var remainingPath = stepPath.Skip(1).ToArray();
            if (sources.Any(source => string.Equals(source.Workflow, workflowName, StringComparison.Ordinal)
                                      && ReferenceEquals(source.Step, sourceStep)))
            {
                var declaredPath = activation.DecisionOutputPath.Split('/')
                    .Skip(1)
                    .Select(DecodeJsonPointerToken)
                    .Where(static token => token.Length > 0)
                    .ToArray();
                if (string.Equals(
                        activation.DecisionContractSource,
                        CapabilityDecisionContractSource,
                        StringComparison.Ordinal))
                {
                    declaredPath = ["response", .. declaredPath];
                }
                return remainingPath.SequenceEqual(declaredPath, StringComparer.Ordinal)
                       && DecisionSourceStepDeclaresContract(sourceStep, activation);
            }

            if (sourceStep.Type is "set" or "assert.non_null")
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
                           activation,
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
                activation,
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    private static bool DecisionSourceStepDeclaresContract(
        StepDef sourceStep,
        McpCapabilityActivation activation)
    {
        if (string.Equals(
                activation.DecisionContractSource,
                LocalDecisionContractSource,
                StringComparison.Ordinal))
        {
            return LocalDecisionSourceStepDeclaresContract(sourceStep, activation);
        }

        if (!string.Equals(
                activation.DecisionContractSource,
                StructuredDecisionContractSource,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (sourceStep.Input is not JsonObject input
            || input["structured_output"] is not JsonObject structuredOutput
            || structuredOutput["strict"] is not JsonValue strictValue
            || !strictValue.TryGetValue<bool>(out var strict)
            || !strict
            || structuredOutput["schema_inline"] is not JsonObject schema)
        {
            return false;
        }

        var pointerTokens = activation.DecisionOutputPath.Split('/')
            .Skip(1)
            .Select(DecodeJsonPointerToken)
            .ToArray();
        if (pointerTokens.Length < 2
            || !string.Equals(pointerTokens[0], "json", StringComparison.Ordinal))
        {
            return false;
        }

        JsonNode? current = schema;
        foreach (var token in pointerTokens.Skip(1))
        {
            if (current is not JsonObject currentObject
                || currentObject["properties"] is not JsonObject properties
                || !properties.TryGetPropertyValue(token, out current)
                || currentObject["required"] is not JsonArray required
                || !required.OfType<JsonValue>().Any(value =>
                    value.TryGetValue<string>(out var requiredName)
                    && string.Equals(requiredName, token, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return PipelineConditionalBoundarySchemaMatches(current, activation.AllowedValues);
    }

    private static bool LocalDecisionSourceStepDeclaresContract(
        StepDef sourceStep,
        McpCapabilityActivation activation)
    {
        if (!string.Equals(sourceStep.Type, LocalDecisionStepType, StringComparison.Ordinal)
            || sourceStep.Input is not JsonObject input
            || input.Count != 1
            || input["decisions"] is not JsonObject decisions)
        {
            return false;
        }

        var fieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
        if (fieldName.Length == 0
            || decisions[fieldName] is not JsonObject contract
            || contract.Select(static item => item.Key)
                .Any(static key => key is not ("allowed_values" or "cases" or "default"))
            || contract["allowed_values"] is not JsonArray allowedNodes
            || allowedNodes.Count != activation.AllowedValues.Count)
        {
            return false;
        }

        var allowedValues = allowedNodes
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) ? text : null)
            .ToArray();
        if (allowedValues.Any(static value => string.IsNullOrWhiteSpace(value))
            || !allowedValues!
                .Select(static value => value!)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(activation.AllowedValues.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || contract["cases"] is not JsonArray cases
            || cases.Count == 0)
        {
            return false;
        }

        var expectedCaseValues = activation.AllowedValues
            .Except(activation.NoEffectValues, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var caseValues = new List<string>(cases.Count);
        foreach (var caseNode in cases)
        {
            if (caseNode is not JsonObject decisionCase
                || decisionCase.Count != 2
                || decisionCase["when"] is not JsonValue whenValue
                || !whenValue.TryGetValue<string>(out var whenExpression)
                || string.IsNullOrWhiteSpace(whenExpression)
                || !whenExpression.Contains("${", StringComparison.Ordinal)
                || decisionCase["value"] is not JsonValue valueNode
                || !valueNode.TryGetValue<string>(out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            caseValues.Add(value);
        }
        if (!caseValues.Order(StringComparer.Ordinal)
            .SequenceEqual(expectedCaseValues, StringComparer.Ordinal))
        {
            return false;
        }

        if (activation.NoEffectValues.Count == 0)
            return !contract.ContainsKey("default");
        return activation.NoEffectValues.Count == 1
               && contract["default"] is JsonValue defaultNode
               && defaultNode.TryGetValue<string>(out var defaultValue)
               && string.Equals(defaultValue, activation.NoEffectValues[0], StringComparison.Ordinal);
    }

    private static IReadOnlyList<PipelineStructuredDecisionRequirement> BuildPipelineStructuredDecisionRequirements(
        WorkflowPipelineSubworkflowSpec spec,
        CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled)
            return Array.Empty<PipelineStructuredDecisionRequirement>();

        var requirements = new List<PipelineStructuredDecisionRequirement>();
        foreach (var group in preflight.RequiredMcpCapabilities
                     .Where(static capability => capability.Activation is not null)
                     .GroupBy(static capability => capability.Activation!.Group, StringComparer.Ordinal))
        {
            var activation = group.First().Activation!;
            if (activation.DecisionContractSource is not (StructuredDecisionContractSource or LocalDecisionContractSource)
                || !PipelineSpecOwnsOperation(spec, activation.DecisionOperationId))
            {
                continue;
            }

            var producer = preflight.Capabilities.FirstOrDefault(capability =>
                capability.Required
                && string.Equals(capability.OperationId, activation.DecisionOperationId, StringComparison.Ordinal)
                && string.Equals(capability.CatalogId, activation.DecisionProducerCatalogId, StringComparison.Ordinal));
            if (producer is null)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightUnavailable,
                    $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_PRODUCER_UNAVAILABLE: Decision operation '{activation.DecisionOperationId}' has no locked producer capability '{activation.DecisionProducerCatalogId}'.",
                    details: new JsonObject
                    {
                        ["phase"] = "capability_preflight",
                        ["reason"] = "conditional_decision_producer_unavailable"
                    });
            }

            requirements.Add(new PipelineStructuredDecisionRequirement(producer, activation));
        }

        return requirements;
    }

    private static void EnforceStructuredDecisionProducerContracts(
        WorkflowPipelineSubworkflowSpec spec,
        string workflowName,
        WorkflowDocument document,
        IReadOnlyList<PipelineStructuredDecisionRequirement> requirements)
    {
        if (requirements.Count == 0)
            return;

        var workflow = document.Workflows[workflowName];
        var allSteps = EnumerateSteps(workflow.Steps)
            .Concat(EnumerateSteps(workflow.Finally))
            .ToArray();
        foreach (var requirement in requirements)
        {
            var sources = allSteps
                .Where(step => StepMatchesDecisionProducer(step, requirement.Producer))
                .ToArray();
            if (sources.Length == 1
                && StructuredDecisionProducerLeafContractIsValid(
                    document,
                    workflowName,
                    sources[0],
                    requirement.Activation))
            {
                continue;
            }

            var fieldName = GetDecisionBoundaryFieldName(requirement.Activation.DecisionOutputPath);
            var contractRequirement = string.Equals(
                    requirement.Activation.DecisionContractSource,
                    LocalDecisionContractSource,
                    StringComparison.Ordinal)
                ? $"Its input.decisions must contain field '{fieldName}' with exact allowed_values [{string.Join(", ", requirement.Activation.AllowedValues)}], one boolean-expression case per effect value, and only the declared no-effect default"
                : $"Its input.structured_output must be strict, field '{fieldName}' must be required with the exact enum [{string.Join(", ", requirement.Activation.AllowedValues)}]";
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_PRODUCER_CONTRACT_INVALID: Leaf '{spec.Name}' must contain exactly one '{requirement.Producer.Method}' producer for decision operation '{requirement.Activation.DecisionOperationId}'. {contractRequirement}, and workflow output '{fieldName}' must expose exact path '{requirement.Activation.DecisionOutputPath}' unchanged through only direct expressions or pure set projections.",
                details: new JsonObject
                {
                    ["phase"] = "pipeline_leaf_generation",
                    ["reason"] = "conditional_decision_producer_contract_invalid",
                    ["leaf"] = spec.Name,
                    ["source_count"] = sources.Length
                });
        }
    }

    internal static bool StructuredDecisionProducerLeafContractIsValid(
        WorkflowDocument document,
        string workflowName,
        StepDef sourceStep,
        McpCapabilityActivation activation)
    {
        if (!document.Workflows.TryGetValue(workflowName, out var workflow))
            return false;
        var fieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
        if (fieldName.Length == 0
            || workflow.Outputs == null
            || !workflow.Outputs.TryGetValue(fieldName, out var output)
            || !string.Equals(output.Type, "string", StringComparison.Ordinal)
            || output.Enum == null
            || !output.Enum.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(
                    activation.AllowedValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            return false;
        }

        return ConditionalDecisionExpressionDependsOnSource(
            document,
            BuildWorkflowArtifactCallerIndex(document),
            [(workflowName, sourceStep)],
            workflowName,
            output.Expr,
            [],
            activation);
    }

    private static void ThrowInvalidConditionalActivation(
        string group,
        string reason,
        IReadOnlyList<ResolvedCapability> capabilities,
        string validationIssue,
        string repairScope,
        string? workflow = null,
        string? switchId = null)
        => throw new WorkflowRuntimeException(
            ErrorCodes.CapabilityPreflightUnavailable,
            $"Generated workflow does not safely implement conditional capability group '{group}': {reason}",
            details: new JsonObject
            {
                ["phase"] = "capability_preflight",
                ["reason"] = "conditional_activation_invalid",
                ["validation_issue"] = validationIssue,
                ["repair_scope"] = repairScope,
                ["activation_group"] = group,
                ["workflow"] = workflow,
                ["switch_id"] = switchId,
                ["decision_operation_id"] = capabilities
                    .Select(static capability => capability.Activation?.DecisionOperationId)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["decision_field"] = capabilities
                    .Select(static capability => GetDecisionBoundaryFieldName(
                        capability.Activation?.DecisionOutputPath ?? string.Empty))
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                ["message"] = reason,
                ["branches"] = new JsonArray(capabilities.Select(capability => (JsonNode)new JsonObject
                {
                    ["branch_value"] = capability.Activation?.BranchValue,
                    ["operation_id"] = string.IsNullOrWhiteSpace(capability.OperationId)
                        ? capability.Id
                        : capability.OperationId,
                    ["catalog_id"] = capability.CatalogId,
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
                ["caller_bindings"] = BuildArtifactCallerBindingDiagnostics(
                    workflowCallers,
                    consumer.Workflow,
                    consumer.Value),
                ["expected"] = "An exact compatible producer response value, optionally routed through workflow inputs/outputs, a transparent set alias, or an exact assert.non_null refinement, or an exact caller-provided artifact input."
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
                (JsonNode)JsonValue.Create("A same-path assert.non_null step preserves provenance while refining nullability; route its exact output field without renaming it.")!,
                (JsonNode)JsonValue.Create("Reuse one producer value for every compatible downstream consumer when the task has one source artifact.")!)
        };
        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Generated workflow contains an MCP artifact consumer whose required value has no compatible, unchanged provenance. | repair diagnostics: "
            + WorkflowPlanDiagnostics.ToPromptJson(details),
            details: details);
    }

    private static JsonArray BuildArtifactCallerBindingDiagnostics(
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        string workflowName,
        JsonNode? value)
    {
        var diagnostics = new JsonArray();
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var expression))
        {
            return diagnostics;
        }

        var path = TrimWorkflowExpression(expression);
        const string inputPrefix = "data.inputs.";
        if (!path.StartsWith(inputPrefix, StringComparison.Ordinal))
            return diagnostics;

        var inputPath = path[inputPrefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (inputPath.Length == 0
            || !workflowCallers.TryGetValue(workflowName, out var callers))
        {
            return diagnostics;
        }

        foreach (var caller in callers
                     .OrderBy(static item => item.Workflow, StringComparer.Ordinal)
                     .ThenBy(static item => item.Call.Id, StringComparer.Ordinal))
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["caller_workflow"] = caller.Workflow,
                ["caller_step"] = caller.Call.Id,
                ["argument_path"] = string.Join('.', inputPath),
                ["argument_value"] = ResolveArtifactCallerArgument(
                    caller.Call.Input?["args"],
                    inputPath)?.DeepClone()
            });
        }

        return diagnostics;
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

    private sealed record ArtifactCallerBindingRepair(
        string CallerWorkflow,
        string CallerStep,
        IReadOnlyList<string> ArgumentPath,
        string ProducerExpression);

    private sealed record ArtifactCallerProducerCandidate(
        string CallerStep,
        string OutputName,
        string ProducerIdentity)
    {
        public string Expression => $"${{data.steps.{CallerStep}.outputs.{OutputName}}}";
    }

    private static (WorkflowDocument Document, string Yaml, int ReplacementCount)
        NormalizeGeneratedMcpArtifactCallerBindings(
            WorkflowDocument document,
            string yaml,
            IReadOnlyList<McpServerDiscovery>? discovered)
    {
        if (discovered == null || discovered.Count == 0)
            return (document, yaml, 0);

        var tools = discovered
            .SelectMany(server => server.Tools.Select(tool => (Server: server.Name, Tool: tool)))
            .ToDictionary(
                static item => (item.Server, item.Tool.Name),
                static item => item.Tool,
                EqualityComparer<(string Server, string Name)>.Default);
        if (tools.Count == 0)
            return (document, yaml, 0);

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
        var consumers = new List<(string Workflow, McpConsumedArtifact Artifact, JsonNode? Value)>();

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
                consumers.AddRange(contract.Consumes
                    .Where(static artifact => artifact.Required)
                    .Select(artifact => (
                        workflowName,
                        artifact,
                        ResolveInstancePointer(step.Input?["request"], artifact.Pointer))));
            }
        }

        if (producers.Count == 0 || consumers.Count == 0)
            return (document, yaml, 0);

        var repairs = new Dictionary<string, ArtifactCallerBindingRepair>(StringComparer.Ordinal);
        var ambiguousRepairKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in consumers)
        {
            if (ResolveArtifactValue(
                    document,
                    stepsByWorkflow,
                    workflowCallers,
                    producers,
                    consumer.Workflow,
                    consumer.Value,
                    consumer.Artifact.Kind,
                    new HashSet<string>(StringComparer.Ordinal)).Proven
                || !TryParseArtifactWorkflowInputPath(consumer.Value, out var inputPath)
                || !workflowCallers.TryGetValue(consumer.Workflow, out var callers))
            {
                continue;
            }

            foreach (var caller in callers)
            {
                var currentArgument = ResolveArtifactCallerArgument(caller.Call.Input?["args"], inputPath);
                if (!IsUnprovenTransparentArtifactSetAlias(
                        caller.Workflow,
                        currentArgument,
                        stepsByWorkflow,
                        document,
                        workflowCallers,
                        producers,
                        consumer.Artifact.Kind)
                    || FindUniqueEarlierArtifactProducerCandidate(
                        document,
                        stepsByWorkflow,
                        workflowCallers,
                        producers,
                        caller.Workflow,
                        caller.Call,
                        consumer.Artifact.Kind) is not { } candidate)
                {
                    continue;
                }

                var key = caller.Workflow + "\u001f" + caller.Call.Id + "\u001f" + string.Join('\u001f', inputPath);
                if (ambiguousRepairKeys.Contains(key))
                    continue;

                var repair = new ArtifactCallerBindingRepair(
                    caller.Workflow,
                    caller.Call.Id,
                    inputPath,
                    candidate.Expression);
                if (repairs.TryGetValue(key, out var existing)
                    && !string.Equals(existing.ProducerExpression, repair.ProducerExpression, StringComparison.Ordinal))
                {
                    repairs.Remove(key);
                    ambiguousRepairKeys.Add(key);
                    continue;
                }

                repairs[key] = repair;
            }
        }

        if (repairs.Count == 0)
            return (document, yaml, 0);

        var root = LoadYamlRoot(yaml);
        var workflows = root.GetMapping("workflows");
        var replacementCount = 0;
        if (workflows == null)
            return (document, yaml, 0);

        foreach (var repair in repairs.Values
                     .OrderBy(static item => item.CallerWorkflow, StringComparer.Ordinal)
                     .ThenBy(static item => item.CallerStep, StringComparer.Ordinal)
                     .ThenBy(static item => string.Join('.', item.ArgumentPath), StringComparer.Ordinal))
        {
            if (!workflows.Children.TryGetValue(Scalar(repair.CallerWorkflow), out var workflowNode)
                || workflowNode is not YamlMappingNode workflow
                || FindGeneratedYamlStepById(workflow, repair.CallerStep) is not { } callerStep
                || callerStep.GetMapping("input")?.GetMapping("args") is not { } args
                || !TryReplaceYamlMappingPath(args, repair.ArgumentPath, Scalar(repair.ProducerExpression)))
            {
                continue;
            }

            replacementCount++;
        }

        if (replacementCount == 0)
            return (document, yaml, 0);

        var normalizedYaml = SerializeYamlNode(root);
        return (ParseAndValidateGeneratedWorkflow(normalizedYaml), normalizedYaml, replacementCount);
    }

    private static bool TryParseArtifactWorkflowInputPath(
        JsonNode? value,
        out IReadOnlyList<string> inputPath)
    {
        inputPath = Array.Empty<string>();
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var expression))
        {
            return false;
        }

        var path = TrimWorkflowExpression(expression);
        const string prefix = "data.inputs.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = path[prefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return false;

        inputPath = segments;
        return true;
    }

    private static bool IsUnprovenTransparentArtifactSetAlias(
        string workflowName,
        JsonNode? value,
        IReadOnlyDictionary<string, Dictionary<string, StepDef>> stepsByWorkflow,
        WorkflowDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<PlannedArtifactProducer> producers,
        string artifactKind)
    {
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var expression))
        {
            return false;
        }

        var path = TrimWorkflowExpression(expression);
        const string prefix = "data.steps.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || !stepsByWorkflow.TryGetValue(workflowName, out var workflowSteps))
        {
            return false;
        }

        var segments = path[prefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2
            || !workflowSteps.TryGetValue(segments[0], out var sourceStep)
            || !string.Equals(sourceStep.Type, "set", StringComparison.Ordinal)
            || ResolveInstancePath(sourceStep.Input, segments.Skip(1).ToArray()) == null)
        {
            return false;
        }

        return !ResolveArtifactValue(
            document,
            stepsByWorkflow,
            workflowCallers,
            producers,
            workflowName,
            value,
            artifactKind,
            new HashSet<string>(StringComparer.Ordinal)).Proven;
    }

    private static ArtifactCallerProducerCandidate? FindUniqueEarlierArtifactProducerCandidate(
        WorkflowDocument document,
        IReadOnlyDictionary<string, Dictionary<string, StepDef>> stepsByWorkflow,
        IReadOnlyDictionary<string, IReadOnlyList<(string Workflow, StepDef Call)>> workflowCallers,
        IReadOnlyList<PlannedArtifactProducer> producers,
        string callerWorkflowName,
        StepDef consumerCaller,
        string artifactKind)
    {
        if (!document.Workflows.TryGetValue(callerWorkflowName, out var callerWorkflow))
            return null;

        var consumerIndex = callerWorkflow.Steps.FindIndex(step => ReferenceEquals(step, consumerCaller));
        if (consumerIndex <= 0)
            return null;

        var candidates = new List<ArtifactCallerProducerCandidate>();
        foreach (var candidateCall in callerWorkflow.Steps.Take(consumerIndex).Where(static step =>
                     string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)))
        {
            var targetName = ReadWorkflowCallRefNameFromInput(candidateCall);
            if (string.IsNullOrWhiteSpace(targetName)
                || !document.Workflows.TryGetValue(targetName, out var targetWorkflow)
                || targetWorkflow.Outputs == null)
            {
                continue;
            }

            foreach (var (outputName, output) in targetWorkflow.Outputs)
            {
                var resolution = ResolveArtifactValue(
                    document,
                    stepsByWorkflow,
                    workflowCallers,
                    producers,
                    targetName,
                    JsonValue.Create(output.Expr),
                    artifactKind,
                    new HashSet<string>(StringComparer.Ordinal));
                if (!resolution.Proven || resolution.UsesCallerInput || resolution.Producers.Count == 0)
                    continue;

                var producerIdentity = candidateCall.Id + "\u001e" + string.Join(
                    "\u001e",
                    resolution.Producers
                        .OrderBy(static item => item.Workflow, StringComparer.Ordinal)
                        .ThenBy(static item => item.StepId, StringComparer.Ordinal)
                        .ThenBy(static item => item.Pointer, StringComparer.Ordinal)
                        .Select(static item => item.Workflow + "\u001d" + item.StepId + "\u001d" + item.Pointer));
                candidates.Add(new ArtifactCallerProducerCandidate(
                    candidateCall.Id,
                    outputName,
                    producerIdentity));
            }
        }

        var producerGroups = candidates
            .GroupBy(static candidate => candidate.ProducerIdentity, StringComparer.Ordinal)
            .ToArray();
        if (producerGroups.Length != 1)
            return null;

        return producerGroups[0]
            .OrderBy(static candidate => candidate.OutputName, StringComparer.Ordinal)
            .First();
    }

    private static bool TryReplaceYamlMappingPath(
        YamlMappingNode mapping,
        IReadOnlyList<string> path,
        YamlNode replacement)
    {
        if (path.Count == 0)
            return false;

        var current = mapping;
        for (var index = 0; index < path.Count - 1; index++)
        {
            if (!current.Children.TryGetValue(Scalar(path[index]), out var child)
                || child is not YamlMappingNode childMapping)
            {
                return false;
            }

            current = childMapping;
        }

        var key = Scalar(path[^1]);
        if (!current.Children.ContainsKey(key))
            return false;

        current.Children[key] = replacement;
        return true;
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
                    var argument = ResolveArtifactCallerArgument(caller.Call.Input?["args"], inputPath);
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

            if (sourceStep.Type is "set" or "assert.non_null")
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
            if (remainingPath.Length < outputIndex + 1
                || target.Outputs == null
                || !target.Outputs.TryGetValue(remainingPath[outputIndex], out var output))
            {
                return ArtifactResolution.Unproven;
            }

            var targetExpression = AppendExactArtifactExpressionPath(
                output.Expr,
                remainingPath.Skip(outputIndex + 1).ToArray());
            if (targetExpression == null)
                return ArtifactResolution.Unproven;

            return ResolveArtifactValue(
                document,
                stepsByWorkflow,
                workflowCallers,
                producers,
                targetWorkflow,
                targetExpression,
                artifactKind,
                visited);
        }
        finally
        {
            visited.Remove(visitKey);
        }
    }

    private static JsonNode? AppendExactArtifactExpressionPath(
        string expression,
        IReadOnlyList<string> nestedPath)
    {
        if (nestedPath.Count == 0)
            return JsonValue.Create(expression);

        var text = expression.Trim();
        if (!text.StartsWith("${", StringComparison.Ordinal) || !text.EndsWith('}'))
            return null;

        var path = text[2..^1].Trim();
        var existingSegments = path.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (existingSegments.Length == 0
            || existingSegments.Any(static segment => !IsExactArtifactPathSegment(segment))
            || nestedPath.Any(static segment => !IsExactArtifactPathSegment(segment)))
        {
            return null;
        }

        return JsonValue.Create("${" + path + "." + string.Join('.', nestedPath) + "}");
    }

    private static JsonNode? ResolveArtifactCallerArgument(
        JsonNode? arguments,
        IReadOnlyList<string> path)
    {
        var current = arguments;
        for (var index = 0; index < path.Count; index++)
        {
            if (current is JsonObject obj && obj[path[index]] is { } child)
            {
                current = child;
                continue;
            }

            if (current is JsonValue value
                && value.TryGetValue<string>(out var expression))
            {
                return AppendExactArtifactExpressionPath(expression, path.Skip(index).ToArray());
            }

            return null;
        }

        return current;
    }

    private static bool IsExactArtifactPathSegment(string segment)
        => segment.Length > 0
           && segment.All(static character => char.IsAsciiLetterOrDigit(character)
                                              || character is '_' or '-');

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
        // Extraction validation is retried and patched in bounded cycles. Recompute
        // conditional diagnostics from the current canonicalized contracts instead
        // of carrying a stale boundary/owner error from an earlier candidate.
        var currentDiagnostics = RemoveStaleConditionalDecisionDiagnostics(boundExtraction);
        var errors = currentDiagnostics.ValidationErrors.ToList();
        var rootCauses = currentDiagnostics.RootCauses.ToList();
        var removedConditionalDiagnostics = !ReferenceEquals(currentDiagnostics, boundExtraction);
        ValidateConditionalDecisionBoundaries(boundExtraction, errors, rootCauses);
        if (missingMcp.Count == 0
            && missingNative.Count == 0
            && missingLocal.Length == 0
            && !removedConditionalDiagnostics
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

    private static WorkflowPipelineExtraction RemoveStaleConditionalDecisionDiagnostics(
        WorkflowPipelineExtraction extraction)
    {
        var errors = extraction.ValidationErrors
            .Where(static error => !error.StartsWith(
                "CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_",
                StringComparison.Ordinal))
            .ToArray();
        var rootCauses = extraction.RootCauses
            .Where(static cause => cause.Code?.StartsWith(
                "CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_",
                StringComparison.Ordinal) != true)
            .ToArray();
        return errors.Length == extraction.ValidationErrors.Count
               && rootCauses.Length == extraction.RootCauses.Count
            ? extraction
            : extraction with { ValidationErrors = errors, RootCauses = rootCauses };
    }

    private static void ValidateConditionalDecisionBoundaries(
        WorkflowPipelineExtraction extraction,
        ICollection<string> errors,
        ICollection<PipelineRootCause> rootCauses)
    {
        var leafOwners = extraction.Subworkflows
            .SelectMany(static leaf => (leaf.LocalOperationIds ?? Array.Empty<string>())
                .Concat(leaf.PlannedTools.SelectMany(static tool => tool.OperationIds))
                .Concat((leaf.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
                    .SelectMany(static step => step.OperationIds))
                .Distinct(StringComparer.Ordinal)
                .Select(operationId => (OperationId: operationId, Leaf: leaf)))
            .GroupBy(static owner => owner.OperationId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Select(static owner => owner.Leaf).ToArray(), StringComparer.Ordinal);
        var mainOwners = (extraction.MainLocalOperationIds ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var activationLeaf in extraction.Subworkflows)
        {
            foreach (var activationGroup in activationLeaf.PlannedTools
                         .Where(static tool => tool.Activation != null)
                         .Select(static tool => tool.Activation!)
                         .GroupBy(static activation => activation.Group, StringComparer.Ordinal))
            {
                var activation = activationGroup.First();
                var decisionOperationId = activation.DecisionOperationId;
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

                if (mainOwners.Contains(decisionOperationId))
                {
                    var mainOwnerMessage = $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_BOUNDARY_MISSING: Conditional activation operation '{decisionOperationId}' is assigned to main, but its grounded enum field '{activation.DecisionOutputPath}' must be produced by a typed leaf or explicit workflow input.";
                    errors.Add(mainOwnerMessage);
                    rootCauses.Add(new PipelineRootCause(
                        "conditional_decision_boundary_missing",
                        "pipeline_extraction",
                        activationLeaf.Name,
                        null,
                        $"subworkflows.{activationLeaf.Name}.planned_tools.activation.decision_output_path",
                        "CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_BOUNDARY_MISSING",
                        mainOwnerMessage,
                        true));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(activation.DecisionOutputPath)
                    || activation.AllowedValues.Count < 2
                    || !ConditionalActivationValuesAreValid(
                        activation,
                        activationGroup.Select(static item => item.BranchValue)))
                {
                    var contractMessage = $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_CONTRACT_INVALID: Conditional activation group '{activation.Group}' has no grounded enum path or its allowed values do not exactly cover every branch.";
                    errors.Add(contractMessage);
                    rootCauses.Add(new PipelineRootCause(
                        "conditional_decision_contract_invalid",
                        "pipeline_extraction",
                        activationLeaf.Name,
                        null,
                        $"subworkflows.{activationLeaf.Name}.planned_tools.activation",
                        "CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_CONTRACT_INVALID",
                        contractMessage,
                        true));
                    continue;
                }

                if (string.Equals(owners[0].Name, activationLeaf.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var decisionOwner = owners[0];
                var decisionField = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
                if (decisionField.Length > 0
                    && decisionOwner.OutputSchemas.TryGetValue(decisionField, out var producerSchema)
                    && activationLeaf.InputSchemas.TryGetValue(decisionField, out var consumerSchema)
                    && PipelineConditionalBoundarySchemaMatches(producerSchema, activation.AllowedValues)
                    && PipelineConditionalBoundarySchemaMatches(consumerSchema, activation.AllowedValues))
                {
                    continue;
                }

                var boundaryMessage = $"CAPABILITY_PREFLIGHT_CONDITIONAL_DECISION_BOUNDARY_MISSING: Conditional activation operation '{decisionOperationId}' is owned by leaf '{decisionOwner.Name}' and consumed by leaf '{activationLeaf.Name}', but field '{decisionField}' is not declared as the same enum output/input contract on both leaves. Route that exact typed value through main without recomputing it.";
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

    private static bool PipelineConditionalBoundarySchemaMatches(
        JsonNode? schema,
        IReadOnlyList<string> allowedValues)
    {
        if (schema is not JsonObject obj
            || !string.Equals(GetStringProperty(obj, "type"), "string", StringComparison.Ordinal)
            || obj["enum"] is not JsonArray enumValues)
        {
            return false;
        }

        var actual = enumValues.OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(static value => value != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return actual.SequenceEqual(
            allowedValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static bool ConditionalActivationValuesAreValid(
        McpCapabilityActivation activation,
        IEnumerable<string> branchValues)
    {
        var branches = branchValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var noEffect = activation.NoEffectValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var allowed = activation.AllowedValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var topologyValid = activation.Mode switch
        {
            ConditionalExactlyOneActivationMode => branches.Length >= 2,
            ConditionalAllOnValueActivationMode => branches.Length == 1 && noEffect.Length > 0,
            _ => false
        };
        return topologyValid
               && noEffect.All(value => !branches.Contains(value, StringComparer.Ordinal))
               && branches.Concat(noEffect).Order(StringComparer.Ordinal)
                   .SequenceEqual(allowed, StringComparer.Ordinal);
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
                    capability.Activation)
                {
                    ExternalEffectKind = capability.ExternalEffectKind
                });
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
        var compositionValidationErrors = new List<string>();
        var knownOwnedOperationIds = preflight.Capabilities
            .Where(static capability => capability.Required && !IsMainOrchestrationNativeCapability(capability))
            .SelectMany(GetResolvedCapabilityOperationIds)
            .ToHashSet(StringComparer.Ordinal);
        var declaredOwnedOperations = specs
            .SelectMany((spec, index) => (spec.OwnedOperationIds ?? Array.Empty<string>())
                .Select(operationId => (OperationId: operationId, SpecIndex: index)))
            .ToArray();
        foreach (var unknown in declaredOwnedOperations
                     .Where(item => !knownOwnedOperationIds.Contains(item.OperationId))
                     .Select(static item => item.OperationId)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            compositionValidationErrors.Add(
                $"PIPELINE_EXTRACTION_OWNED_OPERATION_UNKNOWN: owned_operation_ids contains unknown locked operation '{unknown}'.");
        }
        foreach (var duplicate in declaredOwnedOperations
                     .GroupBy(static item => item.OperationId, StringComparer.Ordinal)
                     .Where(static group => group.Select(static item => item.SpecIndex).Distinct().Count() > 1)
                     .Select(static group => group.Key)
                     .Order(StringComparer.Ordinal))
        {
            compositionValidationErrors.Add(
                $"PIPELINE_EXTRACTION_OWNED_OPERATION_AMBIGUOUS: Locked operation '{duplicate}' appears in owned_operation_ids on multiple leaves; declare exactly one owner.");
        }
        // The extractor may place all members of a composed operation on every leaf that
        // discusses the overall operation. Rebuild locked occurrences deterministically:
        // a composition is a multiset of (operation, catalog) pairs and each pair is placed
        // independently on the leaf that best matches the concrete capability.
        foreach (var planned in tools)
        {
            RemoveClaimedOrMatchingLockedToolPlans(planned, preflight.RequiredMcpCapabilities);
            RemoveClaimedNativeCapabilityToolPlans(
                planned,
                preflight.RequiredNativeCapabilities,
                pipelineMcpContext);
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

            var declaredOwners = FindDeclaredNativeCapabilityOwnerIndices(capability, specs);
            if (declaredOwners.Length != 1)
            {
                compositionValidationErrors.Add(
                    "PIPELINE_EXTRACTION_LOCKED_CAPABILITY_OWNER_UNRESOLVED: Native operation(s) "
                    + string.Join(", ", GetResolvedCapabilityOperationIds(capability).Order(StringComparer.Ordinal))
                    + " require exactly one extractor-authored owner before capability locking.");
                continue;
            }
            var targetIndex = declaredOwners[0];
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
            var declaredOwners = FindDeclaredLockedCapabilityOwnerIndices(capabilities, specs);
            var targetIndex = SelectDeclaredLockedCapabilityOwnerIndex(capabilities, declaredOwners);
            if (targetIndex is null)
            {
                var operationIds = capabilities
                    .SelectMany(GetResolvedCapabilityOperationIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);
                var reason = declaredOwners.Length == 0
                    ? "no leaf declared the exact locked capability identity"
                    : "multiple leaves declared a non-conditional locked capability composition";
                compositionValidationErrors.Add(
                    "PIPELINE_EXTRACTION_LOCKED_CAPABILITY_OWNER_UNRESOLVED: Operation(s) "
                    + string.Join(", ", operationIds)
                    + $" have no unambiguous extractor-authored owner because {reason}. Repair the extraction ownership claims before capability locking.");
                continue;
            }

            localOnlySpecIndices.Remove(targetIndex.Value);
            foreach (var capability in capabilities)
            {
                tools[targetIndex.Value].Add(new PipelinePlannedTool(
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
                    capability.Activation)
                {
                    ExternalEffectKind = capability.ExternalEffectKind
                });
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
            RemoveUnlockedPlansSatisfiedByLockedCapabilities(
                tools[index],
                preflight.RequiredMcpCapabilities);
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
                    .Select(BuildConditionalLeafGuidance));
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
        updated = ApplyConditionalDecisionBoundaryContracts(consolidated.Specs);
        updated = ApplyLocalDecisionInputBoundaryContracts(updated);

        var validationErrors = extraction.ValidationErrors.ToList();
        validationErrors.AddRange(compositionValidationErrors);
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
                var activation = group.First().Activation;
                var decisionOperationId = activation.DecisionOperationId;
                var decisionFieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
                var decisionLeafOwners = updated
                    .Where(spec => PipelineSpecOwnsOperation(spec, decisionOperationId))
                    .Select(static spec => spec.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var mainOwnsDecision = localOperationAssignments.MainOperationIds.Contains(
                    decisionOperationId,
                    StringComparer.Ordinal);
                return BuildConditionalDecisionRoutingGuidance(
                    group.Key,
                    decisionOperationId,
                    decisionFieldName,
                    activationOwner,
                    decisionLeafOwners,
                    mainOwnsDecision);
            })
            .Concat(BuildLocalDecisionInputRoutingGuidance(updated))
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

    private static void AddConditionalCapabilityOwnershipTelemetry(
        StepExecutionContext ctx,
        WorkflowPipelineExtraction extraction)
    {
        foreach (var group in extraction.Subworkflows
                     .SelectMany(spec => spec.PlannedTools
                         .Where(static tool => tool.Activation is not null)
                         .Select(tool => (Owner: spec.Name, Activation: tool.Activation!)))
                     .GroupBy(static item => item.Activation.Group, StringComparer.Ordinal))
        {
            var activation = group.First().Activation;
            var effectOwners = group.Select(static item => item.Owner)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var decisionOwners = extraction.Subworkflows
                .Where(spec => PipelineSpecOwnsOperation(spec, activation.DecisionOperationId))
                .Select(static spec => spec.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            ctx.AddTelemetryEvent("gnougo-flow.plan.pipeline.conditional_ownership", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.conditional_group", group.Key),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.effect_owners", string.Join(',', effectOwners)),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.decision_operation_id", activation.DecisionOperationId),
                new KeyValuePair<string, object?>("gnougo-flow.plan.pipeline.decision_owners", string.Join(',', decisionOwners))
            });
        }
    }

    private static string BuildConditionalLeafGuidance(
        IGrouping<string, McpCapabilityActivation> group)
    {
        var activation = group.First();
        var effectValues = string.Join("' or '", group
            .Select(static item => item.BranchValue)
            .Distinct(StringComparer.Ordinal));
        var noEffectGuidance = activation.NoEffectValues.Count == 0
            ? string.Empty
            : $" Emit explicit non-mutating cases for no-effect values '{string.Join("' and '", activation.NoEffectValues)}'.";
        var structuredGuidance = string.Equals(
                activation.DecisionContractSource,
                StructuredDecisionContractSource,
                StringComparison.Ordinal)
            ? $" The unique owner of decision operation '{activation.DecisionOperationId}' implements producer catalog capability '{activation.DecisionProducerCatalogId}' and must declare strict structured_output.schema_inline whose '{GetDecisionBoundaryFieldName(activation.DecisionOutputPath)}' property is a required string enum containing exactly '{string.Join("' and '", activation.AllowedValues)}'. Do not duplicate that producer in this leaf when another leaf owns it; consume the exact typed decision routed through the declared leaf input instead."
            : string.Equals(
                activation.DecisionContractSource,
                LocalDecisionContractSource,
                StringComparison.Ordinal)
                ? $" The unique owner of decision operation '{activation.DecisionOperationId}' implements one decision.evaluate step. In input.decisions field '{GetDecisionBoundaryFieldName(activation.DecisionOutputPath)}', declare allowed_values exactly '{string.Join("' and '", activation.AllowedValues)}', one boolean-expression case for every effect value, and only the declared no-effect value as default. The same evaluator must own every locked field for this decision operation, and its conditions must consume results from every input operation '{string.Join("' and '", activation.DecisionInputOperationIds)}'. Do not duplicate it in another leaf."
            : string.Empty;
        var effectGuidance = string.Equals(
                activation.Mode,
                ConditionalAllOnValueActivationMode,
                StringComparison.Ordinal)
            ? $"execute every locked capability exactly once in listed order inside the single '{effectValues}' effect case"
            : $"drive exactly one '{effectValues}' effect branch and execute its one matching capability";
        return $"This leaf owns conditional capability group '{group.Key}': {effectGuidance} from enum field '{activation.DecisionOutputPath}' produced by decision operation '{activation.DecisionOperationId}' in one switch, with no mutating default branch.{noEffectGuidance}{structuredGuidance}";
    }

    private static IReadOnlyList<WorkflowPipelineSubworkflowSpec> ApplyConditionalDecisionBoundaryContracts(
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var updated = specs.ToArray();
        foreach (var group in specs
                     .SelectMany((spec, index) => spec.PlannedTools
                         .Where(static tool => tool.Activation != null)
                         .Select(tool => (Index: index, Activation: tool.Activation!)))
                     .GroupBy(static item => item.Activation.Group, StringComparer.Ordinal))
        {
            var activation = group.First().Activation;
            var ownerIndexes = updated
                .Select((spec, index) => (Spec: spec, Index: index))
                .Where(item => PipelineSpecOwnsOperation(item.Spec, activation.DecisionOperationId))
                .Select(static item => item.Index)
                .Distinct()
                .ToArray();
            if (ownerIndexes.Length != 1
                || string.IsNullOrWhiteSpace(activation.DecisionOutputPath)
                || activation.AllowedValues.Count < 2)
            {
                continue;
            }

            var fieldName = GetDecisionBoundaryFieldName(activation.DecisionOutputPath);
            if (fieldName.Length == 0)
                continue;

            var ownerIndex = ownerIndexes[0];
            updated[ownerIndex] = AddOrStrengthenConditionalBoundaryField(
                updated[ownerIndex],
                fieldName,
                activation.AllowedValues,
                output: true);

            foreach (var activationIndex in group.Select(static item => item.Index).Distinct())
            {
                if (activationIndex == ownerIndex)
                    continue;
                updated[activationIndex] = AddOrStrengthenConditionalBoundaryField(
                    updated[activationIndex],
                    fieldName,
                    activation.AllowedValues,
                    output: false);
            }
        }

        return updated.Select(spec => spec with
        {
            GenerationPrompt = BuildSubworkflowGenerationPrompt(spec)
        }).ToArray();
    }

    private sealed record LocalDecisionInputBoundary(
        string DecisionOperationId,
        string InputOperationId,
        string ProducerLeaf,
        string ProducerOutput,
        string DecisionOwnerLeaf,
        string DecisionOwnerInput,
        string Type,
        JsonNode? Schema);

    private static IReadOnlyList<WorkflowPipelineSubworkflowSpec> ApplyLocalDecisionInputBoundaryContracts(
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var boundaries = BuildLocalDecisionInputBoundaries(specs);
        if (boundaries.Count == 0)
            return specs;

        var updated = specs.ToArray();
        foreach (var ownerGroup in boundaries.GroupBy(static boundary => boundary.DecisionOwnerLeaf, StringComparer.Ordinal))
        {
            var ownerIndex = Array.FindIndex(updated, spec => string.Equals(
                spec.Name,
                ownerGroup.Key,
                StringComparison.Ordinal));
            if (ownerIndex < 0)
                continue;

            var owner = updated[ownerIndex];
            var inputs = owner.Inputs.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            var inputSchemas = owner.InputSchemas.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value?.DeepClone(),
                StringComparer.Ordinal);
            var guidance = new List<string>();
            foreach (var boundary in ownerGroup
                         .OrderBy(static item => item.InputOperationId, StringComparer.Ordinal)
                         .ThenBy(static item => item.ProducerLeaf, StringComparer.Ordinal)
                         .ThenBy(static item => item.ProducerOutput, StringComparer.Ordinal))
            {
                inputs[boundary.DecisionOwnerInput] = boundary.Type;
                var schema = boundary.Schema is JsonObject sourceSchema
                    ? (JsonObject)sourceSchema.DeepClone()
                    : new JsonObject { ["type"] = boundary.Type };
                schema["required"] = true;
                schema["nullable"] = false;
                inputSchemas[boundary.DecisionOwnerInput] = schema;
                guidance.Add(
                    $"Input '{boundary.DecisionOwnerInput}' is the unchanged typed result of locked upstream operation '{boundary.InputOperationId}' from leaf '{boundary.ProducerLeaf}' output '{boundary.ProducerOutput}'; every decision.evaluate condition set must use this input as runtime evidence.");
            }

            var guidanceText = string.Join(' ', guidance);
            var content = owner.Content.Contains(guidanceText, StringComparison.Ordinal)
                ? owner.Content
                : owner.Content.TrimEnd() + Environment.NewLine + guidanceText;
            updated[ownerIndex] = owner with
            {
                Inputs = inputs,
                InputSchemas = inputSchemas,
                Content = content
            };
        }

        return updated.Select(spec => spec with
        {
            GenerationPrompt = BuildSubworkflowGenerationPrompt(spec)
        }).ToArray();
    }

    private static IReadOnlyList<string> BuildLocalDecisionInputRoutingGuidance(
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
        => BuildLocalDecisionInputBoundaries(specs)
            .OrderBy(static boundary => boundary.DecisionOperationId, StringComparer.Ordinal)
            .ThenBy(static boundary => boundary.InputOperationId, StringComparer.Ordinal)
            .ThenBy(static boundary => boundary.ProducerLeaf, StringComparer.Ordinal)
            .ThenBy(static boundary => boundary.ProducerOutput, StringComparer.Ordinal)
            .Select(static boundary =>
                $"Call leaf '{boundary.ProducerLeaf}' before decision owner leaf '{boundary.DecisionOwnerLeaf}' and route output '{boundary.ProducerOutput}' unchanged to its exact input '{boundary.DecisionOwnerInput}'. This typed edge supplies locked upstream operation '{boundary.InputOperationId}' to decision operation '{boundary.DecisionOperationId}'.")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<LocalDecisionInputBoundary> BuildLocalDecisionInputBoundaries(
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var boundaries = new List<LocalDecisionInputBoundary>();
        foreach (var activation in specs
                     .SelectMany(static spec => spec.PlannedTools)
                     .Where(static tool => string.Equals(
                         tool.Activation?.DecisionContractSource,
                         LocalDecisionContractSource,
                         StringComparison.Ordinal))
                     .Select(static tool => tool.Activation!)
                     .GroupBy(static activation => activation.DecisionOperationId, StringComparer.Ordinal)
                     .Select(static group => group.First()))
        {
            var decisionOwners = specs
                .Where(spec => PipelineSpecOwnsOperation(spec, activation.DecisionOperationId))
                .ToArray();
            if (decisionOwners.Length != 1)
                continue;
            var decisionOwner = decisionOwners[0];

            foreach (var inputOperationId in activation.DecisionInputOperationIds
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                var producerOwners = specs
                    .Where(spec => PipelineSpecOwnsOperation(spec, inputOperationId))
                    .ToArray();
                if (producerOwners.Length != 1
                    || string.Equals(producerOwners[0].Name, decisionOwner.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var producer = producerOwners[0];
                var output = producer.OutputSchemas
                    .Where(static pair => pair.Value is JsonObject schema
                                          && !string.Equals(
                                              GetStringProperty(schema, "type"),
                                              "any",
                                              StringComparison.Ordinal))
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(output.Key))
                    continue;

                var type = producer.Outputs.TryGetValue(output.Key, out var declaredType)
                    ? declaredType
                    : GetStringProperty((JsonObject)output.Value!, "type") ?? "any";
                boundaries.Add(new LocalDecisionInputBoundary(
                    activation.DecisionOperationId,
                    inputOperationId,
                    producer.Name,
                    output.Key,
                    decisionOwner.Name,
                    BuildStableLocalDecisionInputFieldName(
                        activation.DecisionOperationId,
                        inputOperationId,
                        producer.Name,
                        output.Key),
                    type,
                    output.Value?.DeepClone()));
            }
        }

        return boundaries;
    }

    private static string BuildStableLocalDecisionInputFieldName(
        string decisionOperationId,
        string inputOperationId,
        string producerLeaf,
        string producerOutput)
    {
        var identity = string.Join(
            '\u001f',
            decisionOperationId,
            inputOperationId,
            producerLeaf,
            producerOutput);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return "decision_input_" + digest[..16];
    }

    private static WorkflowPipelineSubworkflowSpec AddOrStrengthenConditionalBoundaryField(
        WorkflowPipelineSubworkflowSpec spec,
        string fieldName,
        IReadOnlyList<string> allowedValues,
        bool output)
    {
        var simple = (output ? spec.Outputs : spec.Inputs)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var schemas = (output ? spec.OutputSchemas : spec.InputSchemas)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value?.DeepClone(), StringComparer.Ordinal);
        simple.TryAdd(fieldName, "string");
        schemas.TryGetValue(fieldName, out var existing);
        var schema = existing is JsonObject current
                     && string.Equals(GetStringProperty(current, "type"), "string", StringComparison.Ordinal)
            ? (JsonObject)current.DeepClone()
            : new JsonObject();
        schema["type"] = "string";
        schema["required"] = true;
        schema["nullable"] = false;
        schema["enum"] = new JsonArray(allowedValues
            .Distinct(StringComparer.Ordinal)
            .Select(static value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
        schemas[fieldName] = schema;

        return output
            ? spec with { Outputs = simple, OutputSchemas = schemas }
            : spec with { Inputs = simple, InputSchemas = schemas };
    }

    private static bool PipelineSpecOwnsOperation(
        WorkflowPipelineSubworkflowSpec spec,
        string operationId)
        => (spec.LocalOperationIds ?? Array.Empty<string>()).Contains(operationId, StringComparer.Ordinal)
           || spec.PlannedTools.Any(tool => tool.OperationIds.Contains(operationId, StringComparer.Ordinal))
           || (spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
               .Any(step => step.OperationIds.Contains(operationId, StringComparer.Ordinal));

    private static string GetDecisionBoundaryFieldName(string pointer)
    {
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            return string.Empty;
        var token = pointer.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(token) ? string.Empty : DecodeJsonPointerToken(token);
    }

    private static string BuildConditionalDecisionRoutingGuidance(
        string group,
        string decisionOperationId,
        string decisionFieldName,
        string activationOwner,
        IReadOnlyList<string> decisionLeafOwners,
        bool mainOwnsDecision)
    {
        var fieldGuidance = string.IsNullOrWhiteSpace(decisionFieldName)
            ? "the declared typed decision field"
            : $"the exact typed field '{decisionFieldName}'";
        if (mainOwnsDecision && decisionLeafOwners.Count == 0)
        {
            return $"Main owns and computes decision operation '{decisionOperationId}', then passes {fieldGuidance} unchanged to the identically named input of leaf '{activationOwner}'; that leaf executes exactly one branch of conditional capability group '{group}'. Do not alias this boundary as 'result'.";
        }

        if (!mainOwnsDecision && decisionLeafOwners.Count == 1)
        {
            var decisionOwner = decisionLeafOwners[0];
            if (string.Equals(decisionOwner, activationOwner, StringComparison.Ordinal))
            {
                return $"Leaf '{decisionOwner}' owns and derives decision operation '{decisionOperationId}', then uses {fieldGuidance} directly to execute exactly one branch of conditional capability group '{group}'; main must not recompute or rename it.";
            }

            return $"Leaf '{decisionOwner}' owns and derives decision operation '{decisionOperationId}' and exposes {fieldGuidance}; main routes that exact field unchanged to the identically named input of leaf '{activationOwner}', which executes exactly one branch of conditional capability group '{group}', without recomputing or aliasing the decision as 'result'.";
        }

        return $"Decision operation '{decisionOperationId}' for conditional capability group '{group}' must be derived by its exactly one immutable owner and {fieldGuidance} must be routed unchanged to the identically named input of leaf '{activationOwner}'; main must not claim ownership or alias the field as 'result' unless the immutable main operation contract assigns it there.";
    }

    private static void AppendLockedMainNativeStepGuidance(
        StringBuilder sb,
        CapabilityPreflightResult capabilityPreflight)
    {
        var lockedMainSteps = capabilityPreflight.RequiredNativeCapabilities
            .Where(IsMainOrchestrationNativeCapability)
            .ToArray();
        sb.AppendLine();
        if (lockedMainSteps.Length == 0)
        {
            sb.AppendLine("No native main-orchestration step is locked. Do not add human confirmation, emit, or another native interaction to main.");
            return;
        }

        sb.AppendLine("The following native main-orchestration steps are locked and exact; include every occurrence and do not invent another:");
        foreach (var capability in lockedMainSteps)
        {
            sb.Append("- ").Append(capability.Method);
            var operationIds = GetResolvedCapabilityOperationIds(capability);
            if (operationIds.Count > 0)
                sb.Append(" (operation_ids: ").Append(string.Join(", ", operationIds)).Append(')');
            sb.AppendLine();
        }
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

                working[ownerIndex] = MergeConditionalSpecs(
                    working[ownerIndex],
                    working[index],
                    group.Key,
                    groupCapabilities[0].Activation?.Mode ?? ConditionalExactlyOneActivationMode);
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
        => MergeConditionalSpecs(
            owner,
            variant,
            activationGroup,
            ConditionalExactlyOneActivationMode);

    private static WorkflowPipelineSubworkflowSpec MergeConditionalSpecs(
        WorkflowPipelineSubworkflowSpec owner,
        WorkflowPipelineSubworkflowSpec variant,
        string activationGroup,
        string activationMode)
    {
        var inputs = MergeConditionalVariantStringContracts(owner.Inputs, variant.Inputs);
        var outputs = MergeConditionalVariantStringContracts(owner.Outputs, variant.Outputs);
        var inputSchemas = MergeConditionalVariantSchemas(owner.InputSchemas, variant.InputSchemas);
        var outputSchemas = MergeConditionalVariantSchemas(owner.OutputSchemas, variant.OutputSchemas);
        var isConditionalComposition = string.Equals(
            activationMode,
            ConditionalAllOnValueActivationMode,
            StringComparison.Ordinal);
        var merged = owner with
        {
            Goal = isConditionalComposition
                ? $"Execute the ordered runtime-selected composition of conditional capability group '{activationGroup}'."
                : $"Execute exactly one runtime-selected branch of conditional capability group '{activationGroup}'.",
            Description = isConditionalComposition
                ? "Execute every capability of one conditional composition in order from a runtime decision."
                : "Execute one mutually exclusive conditional capability branch from a runtime decision.",
            ConcreteOutcome = isConditionalComposition
                ? "One typed result after every selected conditional composition member executes in order."
                : "One typed result from exactly one selected conditional capability branch.",
            Inputs = inputs,
            Outputs = outputs,
            InputSchemas = inputSchemas,
            OutputSchemas = outputSchemas,
            ExtractReason = string.Join(' ', new[] { owner.ExtractReason, variant.ExtractReason }
                .Where(static value => !string.IsNullOrWhiteSpace(value))),
            Content = $"Conditional branch contract '{owner.Name}': {owner.Content.Trim()} "
                      + $"Conditional branch contract '{variant.Name}': {variant.Content.Trim()} "
                      + (isConditionalComposition
                          ? $"Execute them sequentially in locked order inside the one effect case of activation group '{activationGroup}'."
                          : $"Execute them as mutually exclusive alternatives of activation group '{activationGroup}', never sequentially."),
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

    private static int[] FindDeclaredLockedCapabilityOwnerIndices(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var operationIds = capabilities
            .SelectMany(GetResolvedCapabilityOperationIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var explicitOwners = specs
            .Select((spec, index) => new { spec, index })
            .Where(item => operationIds.All(operationId =>
                (item.spec.OwnedOperationIds ?? Array.Empty<string>()).Contains(operationId, StringComparer.Ordinal)))
            .Select(static item => item.index)
            .ToArray();
        if (explicitOwners.Length > 0)
            return explicitOwners;

        return specs
            .Select((spec, index) => new { spec, index })
            .Where(item => item.spec.PlannedTools.Any(tool => capabilities.Any(capability =>
                PlannedToolMatchesCapability(tool, capability)
                && PlannedToolCarriesCapabilityIdentity(tool, capability))))
            .Select(static item => item.index)
            .Distinct()
            .ToArray();
    }

    private static int[] FindDeclaredNativeCapabilityOwnerIndices(
        ResolvedCapability capability,
        IReadOnlyList<WorkflowPipelineSubworkflowSpec> specs)
    {
        var operationIds = GetResolvedCapabilityOperationIds(capability);
        var explicitOwners = specs
            .Select((spec, index) => new { spec, index })
            .Where(item => operationIds.All(operationId =>
                (item.spec.OwnedOperationIds ?? Array.Empty<string>()).Contains(operationId, StringComparer.Ordinal)))
            .Select(static item => item.index)
            .ToArray();
        if (explicitOwners.Length > 0)
            return explicitOwners;

        return specs
            .Select((spec, index) => new { spec, index })
            .Where(item => (item.spec.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
                               .Any(step => PlannedNativeStepClaimsCoalescedCapability(step, capability, operationIds))
                           || item.spec.PlannedTools.Any(tool => IsClaimedNativeCapability(tool, capability)))
            .Select(static item => item.index)
            .Distinct()
            .ToArray();
    }

    private static int? SelectDeclaredLockedCapabilityOwnerIndex(
        IReadOnlyList<ResolvedCapability> capabilities,
        IReadOnlyList<int> declaredOwnerIndices)
        => capabilities.Any(static capability => capability.Activation is not null)
            ? declaredOwnerIndices.Order().Cast<int?>().FirstOrDefault()
            : declaredOwnerIndices.Count == 1
                ? declaredOwnerIndices[0]
                : null;

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

    private static void RemoveUnlockedPlansSatisfiedByLockedCapabilities(
        List<PipelinePlannedTool> plannedTools,
        IReadOnlyList<ResolvedCapability> lockedCapabilities)
    {
        plannedTools.RemoveAll(tool => tool.OperationIds.Count == 0
                                       && tool.CatalogIds.Count == 0
                                       && lockedCapabilities.Any(capability =>
                                           PlannedToolWouldSatisfyCapability(tool, capability)));
    }

    private static bool PlannedToolWouldSatisfyCapability(
        PipelinePlannedTool tool,
        ResolvedCapability capability)
        => string.Equals(tool.Server, capability.Server, StringComparison.Ordinal)
           && string.Equals(tool.Kind, capability.Kind, StringComparison.Ordinal)
           && string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)
           && capability.RequestBindings.All(required => tool.RequestBindings.Any(actual =>
               string.Equals(actual.Path, required.Path, StringComparison.Ordinal)
               && JsonNode.DeepEquals(actual.Value, required.Value)));

    private static void RemoveClaimedNativeCapabilityToolPlans(
        List<PipelinePlannedTool> plannedTools,
        IReadOnlyList<ResolvedCapability> lockedCapabilities,
        PipelineMcpContext pipelineMcpContext)
    {
        plannedTools.RemoveAll(tool => lockedCapabilities.Any(capability =>
            string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
            && (IsClaimedNativeCapability(tool, capability)
                || string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)
                && !PlannedToolExistsInMcpContext(tool, pipelineMcpContext))));
    }

    private static bool IsClaimedNativeCapability(
        PipelinePlannedTool tool,
        ResolvedCapability capability)
        => !string.IsNullOrWhiteSpace(capability.CatalogId)
           && tool.CatalogIds.Contains(capability.CatalogId, StringComparer.Ordinal)
           || string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)
           && tool.OperationIds.Any(GetResolvedCapabilityOperationIds(capability).Contains);

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
            var declaredOwners = specs
                .Select((spec, index) => new { spec, index })
                .Where(item => (item.spec.OwnedOperationIds ?? Array.Empty<string>())
                    .Contains(operationId, StringComparer.Ordinal))
                .Select(static item => item.index)
                .ToArray();
            if (declaredOwners.Length == 1)
            {
                assignments[declaredOwners[0]] = assignments.TryGetValue(declaredOwners[0], out var declared)
                    ? declared
                    : [];
                assignments[declaredOwners[0]].Add(operationId);
                continue;
            }
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
