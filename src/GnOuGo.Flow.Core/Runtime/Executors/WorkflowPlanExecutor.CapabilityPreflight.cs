using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private sealed record CapabilityAlternative(string Server, string Kind, string Method);

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
        string? Method);

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

            var alternatives = new List<CapabilityAlternative>();
            if (requirement["alternatives"] is JsonArray alternativesArray)
            {
                foreach (var alternativeNode in alternativesArray)
                {
                    if (alternativeNode is not JsonObject alternative)
                        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Capability requirement '{id}' contains an invalid alternative.");
                    var server = alternative["server"]?.GetValue<string>()?.Trim();
                    var kind = alternative["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
                    var method = alternative["method"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(server) || kind is not ("tool" or "prompt") || string.IsNullOrWhiteSpace(method))
                        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Capability requirement '{id}' alternatives require server, tool|prompt kind, and method.");
                    alternatives.Add(new CapabilityAlternative(server, kind, method));
                }
            }

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
            alternatives.Add(new CapabilityAlternative(server, kind, method));
        }

        return alternatives;
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
                if (exists)
                {
                    match = alternative;
                    break;
                }
            }

            resolved.Add(match == null
                ? new ResolvedCapability(requirement.Id, requirement.Description, requirement.Required, "unavailable", null, null, null)
                : new ResolvedCapability(requirement.Id, requirement.Description, requirement.Required, "mcp", match.Server, match.Kind, match.Method));
        }

        return resolved;
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
            if (exists)
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
        var prompt = BuildCapabilityInferencePrompt(instruction, generatorContext, discovered, allowedNativeTypes);

        using var inferenceSpan = ctx.BeginTelemetrySpan(parentSpan!, "workflow.plan.capability_preflight.infer", "capability_preflight_infer", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unknown"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model)
        });

        try
        {
            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                StructuredOutputSchema = BuildCapabilityInferenceSchema(),
                StructuredOutputStrict = true
            }, ct);
            AddUsageAttributes(inferenceSpan, response.Usage, model, provider);

            var json = response.Json as JsonObject;
            if (json == null && !string.IsNullOrWhiteSpace(response.Text))
                json = JsonNode.Parse(StripMarkdownFences(response.Text).Trim()) as JsonObject;
            if (json == null || json["complete"] is not JsonValue completeValue || !completeValue.TryGetValue<bool>(out var complete))
                throw new InvalidOperationException("Capability inference response is missing the complete flag.");
            if (!complete)
                throw new WorkflowRuntimeException(
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    "Capability inference reported that it could not produce a complete operation inventory.");

            var operations = json["operations"] as JsonArray
                ?? throw new InvalidOperationException("Capability inference response is missing operations.");
            var resolved = new List<ResolvedCapability>(operations.Count);
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in operations)
            {
                if (node is not JsonObject operation)
                    throw new InvalidOperationException("Capability inference operation must be an object.");
                var id = operation["id"]?.GetValue<string>()?.Trim();
                var description = operation["description"]?.GetValue<string>()?.Trim();
                var required = operation["required"]?.GetValue<bool>() ?? true;
                var resolution = operation["resolution"]?.GetValue<string>()?.Trim().ToLowerInvariant();
                var server = EmptyToNull(operation["server"]?.GetValue<string>());
                var kind = EmptyToNull(operation["kind"]?.GetValue<string>())?.ToLowerInvariant();
                var method = EmptyToNull(operation["method"]?.GetValue<string>());
                if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id) || string.IsNullOrWhiteSpace(description))
                    throw new InvalidOperationException("Capability inference operation ids must be unique and descriptions must be non-empty.");
                if (resolution is not ("mcp" or "native" or "unavailable"))
                    throw new InvalidOperationException($"Capability inference operation '{id}' has invalid resolution '{resolution}'.");
                if (resolution == "mcp"
                    && (string.IsNullOrWhiteSpace(server) || kind is not ("tool" or "prompt") || string.IsNullOrWhiteSpace(method)))
                    throw new InvalidOperationException($"Capability inference operation '{id}' has an incomplete MCP resolution.");
                if (resolution == "native"
                    && (!string.IsNullOrWhiteSpace(server) || !string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(method)))
                    throw new InvalidOperationException($"Capability inference operation '{id}' has an invalid native resolution.");
                if (resolution == "unavailable"
                    && (!string.IsNullOrWhiteSpace(server) || !string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(method)))
                    throw new InvalidOperationException($"Capability inference operation '{id}' has an invalid unavailable resolution.");
                resolved.Add(new ResolvedCapability(id, description, required, resolution, server, kind, method));
            }

            var constraints = ParseInferredCapabilityConstraints(json["constraints"] as JsonArray, identifiers);

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

    private static IReadOnlyList<CapabilityConstraint> ParseInferredCapabilityConstraints(
        JsonArray? constraints,
        HashSet<string> identifiers)
    {
        if (constraints == null || constraints.Count == 0)
            return Array.Empty<CapabilityConstraint>();

        var parsed = new List<CapabilityConstraint>(constraints.Count);
        foreach (var node in constraints)
        {
            if (node is not JsonObject constraint)
                throw new InvalidOperationException("Capability inference constraint must be an object.");
            var id = constraint["id"]?.GetValue<string>()?.Trim();
            var description = constraint["description"]?.GetValue<string>()?.Trim();
            var required = constraint["required"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id) || string.IsNullOrWhiteSpace(description))
                throw new InvalidOperationException("Capability inference ids must be unique and constraint descriptions must be non-empty.");

            IReadOnlyList<CapabilityAlternative> denied;
            try
            {
                denied = ParseCapabilityAlternatives(
                    constraint["denied_alternatives"] as JsonArray,
                    $"Capability constraint '{id}'");
            }
            catch (WorkflowRuntimeException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            parsed.Add(new CapabilityConstraint(id, description, required, denied));
        }

        return parsed;
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

    private static string BuildCapabilityInferencePrompt(
        string instruction,
        string context,
        IReadOnlyList<McpServerDiscovery> discovered,
        IReadOnlySet<string> nativeStepTypes)
    {
        var catalog = new StringBuilder();
        foreach (var server in discovered)
        {
            catalog.AppendLine($"server: {server.Name}");
            if (!string.IsNullOrWhiteSpace(server.Description))
                catalog.AppendLine($"  description: {server.Description}");
            foreach (var tool in server.Tools)
                catalog.AppendLine($"  tool: {tool.Name} — {tool.Description}");
            foreach (var prompt in server.Prompts)
                catalog.AppendLine($"  prompt: {prompt.Name} — {prompt.Description}");
        }

        return $$"""
            You are a domain-neutral workflow capability analyst. Return only the requested structured JSON.

            Enumerate every distinct positive operation required to satisfy the task before workflow generation. Include external reads, external writes, side effects, resource creation, resource cleanup, and recovery actions. Do not omit an operation merely because no matching capability exists.

            Separately enumerate constraints: prohibitions, safety rules, ordering requirements, and invariants that describe what must not happen or what must remain true. A prohibition is not a positive operation and must never be marked unavailable merely because abstaining does not require a tool. When a constraint forbids one or more exact catalog capabilities, list those exact entries in denied_alternatives. Leave denied_alternatives empty for constraints that cannot be represented as exact catalog denials.

            Resolution rules:
            - Use `mcp` only with one exact server, kind, and method from the catalog.
            - Use `native` only with one exact allowed native step type in method; server and kind must be empty strings.
            - Use `unavailable` when no listed capability can perform the operation; server, kind, and method must be empty strings.
            - Mark an operation required when omitting it would violate the task. Optional enrichment may be required=false.
            - Constraints do not require a capability resolution and never make capability availability fail by themselves.
            - denied_alternatives may contain only exact server/kind/method entries from the catalog.
            - Do not infer support from names, URLs, brands, or undocumented behavior.
            - Set complete=false if the operation inventory itself is uncertain or incomplete.

            <allowed_native_step_types>
            {{string.Join("\n", nativeStepTypes.OrderBy(static value => value, StringComparer.Ordinal))}}
            </allowed_native_step_types>

            <mcp_catalog>
            {{catalog}}
            </mcp_catalog>

            {{BuildUserTaskBlock(instruction, context)}}
            """;
    }

    private static JsonObject BuildCapabilityInferenceSchema() => new()
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
                        ["required"] = new JsonObject { ["type"] = "boolean" },
                        ["resolution"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("mcp", "native", "unavailable")
                        },
                        ["server"] = new JsonObject { ["type"] = "string" },
                        ["kind"] = new JsonObject { ["type"] = "string" },
                        ["method"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "resolution", "server", "kind", "method"),
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
                        ["denied_alternatives"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["server"] = new JsonObject { ["type"] = "string" },
                                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("tool", "prompt") },
                                    ["method"] = new JsonObject { ["type"] = "string" }
                                },
                                ["required"] = new JsonArray("server", "kind", "method"),
                                ["additionalProperties"] = false
                            }
                        }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "denied_alternatives"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("complete", "operations", "constraints"),
        ["additionalProperties"] = false
    };

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
                if (!exists)
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
                    unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null });
                continue;
            }

            if (capability.Resolution != "mcp"
                || string.IsNullOrWhiteSpace(capability.Server)
                || capability.Kind is not ("tool" or "prompt")
                || string.IsNullOrWhiteSpace(capability.Method))
            {
                unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null });
                continue;
            }

            var server = discovered.FirstOrDefault(candidate => string.Equals(candidate.Name, capability.Server, StringComparison.Ordinal));
            var exists = server?.Discovered == true && (capability.Kind == "prompt"
                ? server.Prompts.Any(prompt => string.Equals(prompt.Name, capability.Method, StringComparison.Ordinal))
                : server.Tools.Any(tool => string.Equals(tool.Name, capability.Method, StringComparison.Ordinal)));
            if (!exists)
                unavailable.Add(capability with { Resolution = "unavailable", Server = null, Kind = null, Method = null });
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
                ["reason"] = "no_matching_discovered_capability"
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
                sb.Append(' ').Append(capability.Server).Append('/').Append(capability.Method).Append(" (").Append(capability.Kind).Append(')');
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
                    sb.Append("  denied: ").Append(denied.Server).Append('/').Append(denied.Method).Append(" (").Append(denied.Kind).AppendLine(")");
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
                ["method"] = capability.Method
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
                    ["method"] = alternative.Method
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
        {
            var server = ReadMcpCallInputString(step, "server");
            var kind = ReadMcpCallInputString(step, "kind") ?? "tool";
            if (!string.Equals(server, capability.Server, StringComparison.Ordinal)
                || !string.Equals(kind, capability.Kind, StringComparison.Ordinal))
                return false;
            var method = ReadMcpCallInputString(step, "method");
            if (string.Equals(method, capability.Method, StringComparison.Ordinal))
                return true;
            return step.Input?["methods"] is JsonArray methods
                   && methods.Any(node => node is JsonValue value
                                          && value.TryGetValue<string>(out var candidate)
                                          && string.Equals(candidate, capability.Method, StringComparison.Ordinal));
        })).Concat(preflight.RequiredNativeCapabilities.Where(capability =>
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
            .Where(item => calls.Any(step =>
            {
                var server = ReadMcpCallInputString(step, "server");
                var kind = ReadMcpCallInputString(step, "kind") ?? "tool";
                var method = ReadMcpCallInputString(step, "method");
                return string.Equals(server, item.alternative.Server, StringComparison.Ordinal)
                       && string.Equals(kind, item.alternative.Kind, StringComparison.Ordinal)
                       && (string.Equals(method, item.alternative.Method, StringComparison.Ordinal)
                           || step.Input?["methods"] is JsonArray methods
                           && methods.Any(node => node is JsonValue value
                                                && value.TryGetValue<string>(out var candidate)
                                                && string.Equals(candidate, item.alternative.Method, StringComparison.Ordinal)));
            }))
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
                            ["method"] = item.alternative.Method
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
                                                       && string.Equals(tool.Method, capability.Method, StringComparison.Ordinal)))
            .ToArray();
        if (missing.Length == 0)
            return extraction;

        var errors = extraction.ValidationErrors.ToList();
        var rootCauses = extraction.RootCauses.ToList();
        foreach (var capability in missing)
        {
            var message = $"CAPABILITY_PREFLIGHT_REQUIRED_CAPABILITY_OMITTED: Required capability '{capability.Id}' must be assigned to one external-work leaf as exact planned tool {capability.Server}/{capability.Method} ({capability.Kind}) with required=true.";
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

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
