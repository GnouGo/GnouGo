using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private sealed record CapabilityRequestBinding(string Path, JsonNode? Value);

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
        IReadOnlyList<CapabilityRequestBinding> RequestBindings);

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
    }

    private async Task<CapabilityPreflightResult> RunCapabilityPreflightAsync(
        StepExecutionContext ctx,
        JsonObject input,
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

                (resolved, constraints) = await InferCapabilitiesAsync(
                    ctx,
                    input,
                    generator,
                    instruction,
                    generatorContext,
                    discovered,
                    span.Span,
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
        CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.CapabilityPreflightInferenceFailed, "Capability inference requires an LLM client.");
        var (provider, resolvedModel) = ctx.Engine.ResolveLlmTarget(
            generator["provider"]?.GetValue<string>(),
            generator["model"]?.GetValue<string>());
        var model = resolvedModel ?? "gpt-4";
        var reasoning = generator["reasoning"]?.GetValue<string>() ?? "low";
        var allowedNativeTypes = ResolveAllowedNativeStepTypes(ctx, input);
        var catalog = BuildSchemaAwareCapabilityCatalog(discovered, allowedNativeTypes);

        using var inferenceSpan = ctx.BeginTelemetrySpan(parentSpan!, "workflow.plan.capability_preflight.infer", "capability_preflight_infer", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_catalog.entry_count", catalog.Entries.Count)
        });

        try
        {
            var inventoryResponse = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityInventoryPrompt(instruction, generatorContext),
                Reasoning = reasoning,
                StructuredOutputSchema = BuildCapabilityInventorySchema(),
                StructuredOutputStrict = true
            }, ct);
            AddUsageAttributes(inferenceSpan, inventoryResponse.Usage, model, provider);
            var inventory = ParseCapabilityInventory(ParseStructuredObject(inventoryResponse, "operation inventory"));

            var matchingResponse = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = BuildCapabilityMatchingPrompt(inventory, catalog),
                Reasoning = reasoning,
                StructuredOutputSchema = BuildCapabilityMatchingSchema(),
                StructuredOutputStrict = true
            }, ct);
            AddUsageAttributes(inferenceSpan, matchingResponse.Usage, model, provider);
            var (resolved, constraints) = ParseCapabilityMatches(
                ParseStructuredObject(matchingResponse, "capability matching"), inventory, catalog);

            inferenceSpan.Complete();
            return (resolved, constraints);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkflowRuntimeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            inferenceSpan.Fail(ex);
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability inference returned an invalid or incomplete contract.",
                inner: ex);
        }
    }

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

        Runtime boundary rules:
        - Exclude host configuration already supplied to the workflow runtime.
        - Exclude credentials, provider selection, secret-vault lookup, authentication, and connection setup performed internally by whichever runtime capability is selected later.
        - Exclude persistence, registration, or provisioning of the generated workflow/agent when that happens outside the generated workflow after planning.
        - Include cleanup only when the generated workflow must execute it at runtime.
        - Mark optional enrichment required=false.
        - Set complete=false if the runtime inventory is uncertain or incomplete.

        {{BuildUserTaskBlock(instruction, context)}}
        """;

    private static JsonObject BuildCapabilityInventorySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["complete"] = new JsonObject { ["type"] = "boolean" },
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
                        ["required"] = new JsonObject { ["type"] = "boolean" }
                    },
                    ["required"] = new JsonArray("id", "description", "required"),
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
                        ["required"] = new JsonObject { ["type"] = "boolean" }
                    },
                    ["required"] = new JsonArray("id", "description", "required"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("complete", "operations", "constraints"),
        ["additionalProperties"] = false
    };

    private static CapabilityInventory ParseCapabilityInventory(JsonObject json)
    {
        if (!TryReadComplete(json, out var complete) || !complete)
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability inference reported that it could not produce a complete runtime operation inventory.");
        var operationNodes = json["operations"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing operations.");
        var constraintNodes = json["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Capability inventory is missing constraints.");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var operations = operationNodes.Select(node =>
        {
            var (id, description, required) = ParseInventoryItem(node, identifiers, "operation");
            return new CapabilityInventoryOperation(id, description, required);
        }).ToArray();
        var constraints = constraintNodes.Select(node =>
        {
            var (id, description, required) = ParseInventoryItem(node, identifiers, "constraint");
            return new CapabilityInventoryConstraint(id, description, required);
        }).ToArray();
        return new CapabilityInventory(operations, constraints);
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

    private static string BuildCapabilityMatchingPrompt(CapabilityInventory inventory, CapabilityCatalog catalog)
    {
        var operations = new JsonArray(inventory.Operations.Select(static operation => (JsonNode)new JsonObject
        {
            ["id"] = operation.Id,
            ["description"] = operation.Description,
            ["required"] = operation.Required
        }).ToArray());
        var constraints = new JsonArray(inventory.Constraints.Select(static constraint => (JsonNode)new JsonObject
        {
            ["id"] = constraint.Id,
            ["description"] = constraint.Description,
            ["required"] = constraint.Required
        }).ToArray());
        return $$"""
            You are a domain-neutral capability matcher. Return only the requested structured JSON.

            Match each positive runtime operation to exactly one catalog ID, or mark it unavailable. For a multi-action tool, choose the selector-specific catalog entry whose request_bindings describe the exact logical operation. Different selector values are different capabilities.

            For each constraint, return every exact catalog ID it prohibits. Do not deny a whole multi-action tool when only one selector-specific operation is prohibited. Return an empty list when a constraint is not representable as exact capability denials.

            Rules:
            - Return only catalog IDs shown below; never invent server, tool, prompt, method, or selector names.
            - Use resolution=mcp or native only when the referenced entry has that resolution.
            - Use resolution=unavailable with an empty catalog_id when no exact entry is documented.
            - Do not infer behavior from server names, product names, URLs, brands, or undocumented semantics.
            - Every inventory operation and constraint ID must occur exactly once.
            - Set complete=false for uncertainty, incomplete matching, or ambiguous selector choice.

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
            ["complete"] = new JsonObject { ["type"] = "boolean" },
            ["operation_matches"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["operation_id"] = new JsonObject { ["type"] = "string" },
                        ["resolution"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("mcp", "native", "unavailable") },
                        ["catalog_id"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("operation_id", "resolution", "catalog_id"),
                    ["additionalProperties"] = false
                }
            },
            ["constraint_denials"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["constraint_id"] = new JsonObject { ["type"] = "string" },
                        ["catalog_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                    },
                    ["required"] = new JsonArray("constraint_id", "catalog_ids"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("complete", "operation_matches", "constraint_denials"),
        ["additionalProperties"] = false
    };

    private static (IReadOnlyList<ResolvedCapability>, IReadOnlyList<CapabilityConstraint>) ParseCapabilityMatches(
        JsonObject json,
        CapabilityInventory inventory,
        CapabilityCatalog catalog)
    {
        if (!TryReadComplete(json, out var complete) || !complete)
            throw new WorkflowRuntimeException(
                ErrorCodes.CapabilityPreflightInferenceFailed,
                "Capability matching reported uncertainty or an incomplete match.");
        var entries = catalog.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var operationMatches = json["operation_matches"] as JsonArray
            ?? throw new InvalidOperationException("Capability matching is missing operation_matches.");
        var matchesById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in operationMatches)
        {
            if (node is not JsonObject match)
                throw new InvalidOperationException("Capability operation match must be an object.");
            var operationId = match["operation_id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(operationId) || !matchesById.TryAdd(operationId, match))
                throw new InvalidOperationException("Capability operation matches must use unique inventory ids.");
        }
        if (matchesById.Count != inventory.Operations.Count
            || matchesById.Keys.Any(id => inventory.Operations.All(operation => !string.Equals(operation.Id, id, StringComparison.Ordinal))))
            throw new InvalidOperationException("Capability matching did not match every inventory operation exactly once.");

        var resolved = new List<ResolvedCapability>(inventory.Operations.Count);
        foreach (var operation in inventory.Operations)
        {
            var match = matchesById[operation.Id];
            var resolution = match["resolution"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            var catalogId = match["catalog_id"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (resolution == "unavailable" && catalogId.Length == 0)
            {
                resolved.Add(new ResolvedCapability(operation.Id, operation.Description, operation.Required,
                    "unavailable", null, null, null, Array.Empty<CapabilityRequestBinding>()));
                continue;
            }
            if (resolution is not ("mcp" or "native") || !entries.TryGetValue(catalogId, out var entry)
                || !string.Equals(resolution, entry.Resolution, StringComparison.Ordinal))
                throw new InvalidOperationException($"Capability operation '{operation.Id}' references unknown or incompatible catalog ID '{catalogId}'.");
            resolved.Add(new ResolvedCapability(operation.Id, operation.Description, operation.Required,
                entry.Resolution, entry.Server, entry.Kind, entry.Method, entry.RequestBindings));
        }

        var denialNodes = json["constraint_denials"] as JsonArray
            ?? throw new InvalidOperationException("Capability matching is missing constraint_denials.");
        var denialsById = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var node in denialNodes)
        {
            if (node is not JsonObject denial)
                throw new InvalidOperationException("Capability constraint denial must be an object.");
            var constraintId = denial["constraint_id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(constraintId) || denial["catalog_ids"] is not JsonArray ids
                || !denialsById.TryAdd(constraintId, ids))
                throw new InvalidOperationException("Capability constraint denials must use unique inventory ids.");
        }
        if (denialsById.Count != inventory.Constraints.Count
            || denialsById.Keys.Any(id => inventory.Constraints.All(constraint => !string.Equals(constraint.Id, id, StringComparison.Ordinal))))
            throw new InvalidOperationException("Capability matching did not match every inventory constraint exactly once.");

        var constraints = new List<CapabilityConstraint>(inventory.Constraints.Count);
        foreach (var constraint in inventory.Constraints)
        {
            var alternatives = new List<CapabilityAlternative>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var idNode in denialsById[constraint.Id])
            {
                var id = idNode?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id) || !entries.TryGetValue(id, out var entry)
                    || entry.Resolution != "mcp" || entry.Server == null || entry.Kind == null)
                    throw new InvalidOperationException($"Capability constraint '{constraint.Id}' references unknown or invalid catalog ID '{id}'.");
                alternatives.Add(new CapabilityAlternative(entry.Server, entry.Kind, entry.Method, entry.RequestBindings));
            }
            constraints.Add(new CapabilityConstraint(constraint.Id, constraint.Description, constraint.Required, alternatives));
        }
        return (resolved, constraints);
    }

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
        IReadOnlyList<ResolvedCapability> unavailableCapabilities)
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
                ["reason"] = "no_matching_discovered_capability",
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
                ["unavailable_capabilities"] = capabilityArray
            });
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
        sb.AppendLine("The following capability decisions are locked by preflight. Required MCP capabilities must appear as exact direct mcp.call operations; do not omit, rename, or replace them.");
        foreach (var capability in preflight.Capabilities.Where(static item => item.Required))
        {
            sb.Append("- ").Append(capability.Id).Append(": ").Append(capability.Resolution);
            if (capability.Resolution == "mcp")
            {
                sb.Append(' ').Append(capability.Server).Append('/').Append(capability.Method).Append(" (").Append(capability.Kind).Append(')');
                if (capability.RequestBindings.Count > 0)
                    sb.Append(" request_bindings=[").Append(FormatBindingsCompact(capability.RequestBindings)).Append(']');
            }
            else if (capability.Resolution == "native")
                sb.Append(' ').Append(capability.Method);
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
                ["server"] = capability.Server,
                ["kind"] = capability.Kind,
                ["method"] = capability.Method,
                ["request_bindings"] = BuildRequestBindingsJson(capability.RequestBindings)
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
            && preflight.Constraints.All(static constraint => !constraint.Required || constraint.DeniedAlternatives.Count == 0))
            return;

        var steps = document.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Steps).Concat(EnumerateSteps(workflow.Finally)))
            .ToArray();
        var calls = steps.Where(static step => string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)).ToArray();
        var missing = preflight.RequiredMcpCapabilities.Where(capability => !calls.Any(step =>
            McpStepMatchesCapability(step, capability.Server!, capability.Kind!, capability.Method!, capability.RequestBindings)))
            .Concat(preflight.RequiredNativeCapabilities.Where(capability =>
            !steps.Any(step => string.Equals(step.Type, capability.Method, StringComparison.Ordinal)))).ToArray();

        if (missing.Length > 0)
            ThrowCapabilityPreflightFailure(
                ErrorCodes.CapabilityPreflightUnavailable,
                "Generated workflow omitted one or more required capabilities locked by capability preflight.",
                Array.Empty<string>(),
                missing);

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

    private static WorkflowPipelineExtraction ValidateLockedCapabilitiesInExtraction(
        WorkflowPipelineExtraction extraction,
        CapabilityPreflightResult preflight)
    {
        if (!preflight.Enabled || preflight.RequiredMcpCapabilities.Count == 0)
            return extraction;

        var planned = extraction.Subworkflows.SelectMany(static spec => spec.PlannedTools).ToArray();
        var missing = preflight.RequiredMcpCapabilities
            .Where(capability => !planned.Any(tool => tool.Required
                                                       && string.Equals(tool.Server, capability.Server, StringComparison.Ordinal)
                                                       && string.Equals(tool.Kind, capability.Kind, StringComparison.Ordinal)
                                                       && string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)
                                                       && RequestBindingsEqual(tool.RequestBindings, capability.RequestBindings)))
            .ToArray();
        if (missing.Length == 0)
            return extraction;

        var errors = extraction.ValidationErrors.ToList();
        var rootCauses = extraction.RootCauses.ToList();
        foreach (var capability in missing)
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

        return extraction with
        {
            ValidationErrors = errors,
            RootCauses = rootCauses
        };
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
