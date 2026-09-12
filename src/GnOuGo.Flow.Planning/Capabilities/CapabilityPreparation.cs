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

using static GnOuGo.Flow.Planning.Capabilities.CapabilityCatalogBuilder;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractLocking;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContractValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityDecisionGrounding;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityDiscovery;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryContext;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchAssessment;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchRecovery;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatching;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPolicy;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityPrerequisites;
using static GnOuGo.Flow.Planning.Capabilities.CapabilitySelectionValidation;
using static GnOuGo.Flow.Planning.Capabilities.PlanningArtifactValidation;
using static GnOuGo.Flow.Planning.Capabilities.PreparationTelemetry;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityPreparation
{
    internal static string TypedCapabilityIdentity(ResolvedCapability capability, string stepType)
    {
        // Identity follows the issued operation and executable binding. Evidence,
        // descriptions and contract revisions are fingerprinted separately.
        var identity = new JsonObject
        {
            ["operations"] = new JsonArray(GetResolvedCapabilityOperationIds(capability).Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["type"] = stepType, ["resolution"] = capability.Resolution, ["server"] = capability.Server,
            ["kind"] = capability.Kind, ["method"] = capability.Method,
            ["arguments"] = new JsonObject(capability.RequestBindings.OrderBy(b => b.Path, StringComparer.Ordinal)
                .Select(b => new KeyValuePair<string, JsonNode?>(b.Path, b.Value?.DeepClone())))
        };
        return "capability_" + PlanningGraphCompiler.Fingerprint(identity.ToJsonString())[..24];
    }

    internal static async Task<CapabilityPreflightResult> RunCapabilityPreflightAsync(
        StepExecutionContext ctx, JsonObject input, PlanningSnapshot snapshot, IPlanningRuntime runtime, CancellationToken ct)
    {
        var preflight = input["capability_preflight"] as JsonObject;
        var mode = preflight?["requirements"] is JsonArray { Count: > 0 } ? "explicit" : "infer";

        var generator = input["generator"] as JsonObject ?? new JsonObject();
        var instruction = input["raw_prompt"]?.GetValue<string>() ?? string.Empty;
        var generatorContext = string.Empty;
        _ = ParseCapabilityClarificationConfig(preflight);

        using var span = ctx.BeginTelemetrySpan("workflow.plan.capability_preflight", "capability_preflight", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.plan.capability_preflight.mode", mode)
        });

        try
        {
            var checkpoint = ctx.PreparationCheckpoint;
            var refreshDiscovery = checkpoint?.RefreshDiscovery == true;
            var previousDiscovery = checkpoint?.ValidatedResults["discovery"];
            var discovered = !refreshDiscovery && previousDiscovery is JsonNode discoveryCheckpoint
                ? JsonSerializer.Deserialize(discoveryCheckpoint, TypedContractJsonContext.Default.ListMcpServerDiscovery)!
                : await DiscoverMcpServersAsync(
                                 ctx.Engine.McpClientFactory,
                                 ctx.Engine.McpCache,
                                 ctx.Engine.Logger,
                                 ctx,
                                 candidateServers: null,
                                 span.Span,
                                 ct,
                                 refresh: refreshDiscovery)
                             ?? new List<McpServerDiscovery>();

            if (discovered.All(server => server.Discovered))
            {
                var currentDiscovery = JsonSerializer.SerializeToNode(discovered, TypedContractJsonContext.Default.ListMcpServerDiscovery);
                if (refreshDiscovery && checkpoint is not null)
                {
                    // Intent inventory is independent of available providers. Selections and
                    // matching candidates are valid only against the catalog that produced them.
                    if (!JsonNode.DeepEquals(previousDiscovery, currentDiscovery))
                    {
                        checkpoint.ValidatedResults.Remove("selection");
                        checkpoint.ValidatedResults.Remove("physical_candidates");
                        checkpoint.ValidatedResults.Remove("matching_candidate");
                    }
                    checkpoint.RefreshDiscovery = false;
                }
                await SaveTypedPreparationResultAsync(ctx, "discovery", currentDiscovery, ct);
            }

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
                var unavailable = resolved.Where(c => c.Required && c.Resolution == "unavailable").ToArray();
                if (unavailable.Length > 0)
                {
                    var proof = PlanningReferences.Register(snapshot, "explicit_requirements", "capability_requirement", preflight!.ToJsonString());
                    snapshot.Outcome = new PlanningUnsupported(unavailable.Select(c => new PlanningUnsupportedObligation(c.Id,
                        "DECLARED_ALTERNATIVES_UNAVAILABLE", proof.Select(r => r.Id).ToList())).ToList());
                }
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
                    generatorContext);

                (resolved, constraints) = await InferCapabilitiesAsync(
                    ctx,
                    input,
                    generator,
                    instruction,
                    generatorContext,
                    evidenceSources,
                    snapshot, runtime, discovered,
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

            var (confirmationPolicy, confirmationPolicySource) = ResolveEffectiveExternalWriteConfirmationPolicy(
                resolved,
                mode);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.external_write_confirmation_policy", confirmationPolicy);
            span.SetAttribute("gnougo-flow.plan.capability_preflight.external_write_confirmation_policy_source", confirmationPolicySource);
            return new CapabilityPreflightResult(discovered, resolved, constraints)
            {
                EffectiveExternalWriteConfirmationPolicy = confirmationPolicy,
                ExternalWriteConfirmationPolicySource = confirmationPolicySource
            };
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

    internal static async Task<(IReadOnlyList<ResolvedCapability> Capabilities, IReadOnlyList<CapabilityConstraint> Constraints)> InferCapabilitiesAsync(
        StepExecutionContext ctx, JsonObject input, JsonObject generator, string instruction, string generatorContext, IReadOnlyList<CapabilityEvidenceSource> evidenceSources, PlanningSnapshot snapshot, IPlanningRuntime runtime, IReadOnlyList<McpServerDiscovery> discovered, ITelemetrySpan? parentSpan, CancellationToken ct, bool clarificationAllowed = true)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.CapabilityPreflightInferenceFailed, "Capability inference requires an LLM client.");
        var (provider, resolvedModel) = ctx.Engine.ResolveLlmTarget(
            generator["provider"]?.GetValue<string>(),
            generator["model"]?.GetValue<string>());
        var model = resolvedModel ?? "unknown";
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
            CapabilityInventory inventory;
            if (ctx.PreparationCheckpoint?.ValidatedResults["inventory"] is JsonNode inventoryCheckpoint)
                inventory = JsonSerializer.Deserialize(inventoryCheckpoint, TypedContractJsonContext.Default.CapabilityInventory)!;
            else
            {
                inventory = await CapabilityInventoryDecisions.BuildAsync(snapshot, runtime, ct);
                await SaveTypedPreparationResultAsync(ctx, "inventory", JsonSerializer.SerializeToNode(inventory, TypedContractJsonContext.Default.CapabilityInventory), ct);
            }

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

            inferencePhase = "capability_matching";
            var catalog = BuildSchemaAwareCapabilityCatalog(discovered, allowedNativeTypes, discovered);
            var matching = await CapabilityDecisionPages.MatchAsync(snapshot, runtime, inventory, catalog, ct);
            var evaluation = ParseCapabilityMatchingEvaluation(matching, inventory, catalog);
            evaluation = NormalizeLocalProcessingMatches(evaluation);
            evaluation = NormalizeCapabilityCompositionMatches(evaluation, catalog);
            evaluation = NormalizeConditionalSelectorMatches(evaluation, catalog, inventory);
            evaluation = EnforceCapabilityPrerequisiteClosure(evaluation, catalog);
            evaluation = NormalizePlatformSafetyMatches(evaluation, catalog);
            RecordCapabilityMatchingNormalizationTelemetry(inferenceSpan, evaluation, "decisions");
            RecordConditionalGroundingTelemetry(inferenceSpan.Span, evaluation, "decisions");
            ThrowForUnresolvedCapabilityMatches(evaluation, catalog);
            await SaveTypedPreparationResultAsync(ctx, "matching_candidate", TypedMatchingCandidate(evaluation), ct);
            evaluation = CanonicalizeSharedStructuredDecisionOutputPaths(evaluation);
            var (resolved, constraints) = ResolveCapabilityMatches(evaluation, catalog);

            inferenceSpan.Complete();
            return (resolved, constraints);
        }
        catch (OperationCanceledException)
        {
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

    /// <summary>Reuses the established exact-capability inventory and validation boundary.</summary>
    public static async Task<PlanningPreparation> PrepareTypedContractsAsync(StepExecutionContext ctx, PlanningRequest request, PlanningSnapshot snapshot, IPlanningRuntime runtime, CancellationToken ct)
    {
        var input = (JsonObject)request.Options.DeepClone();
        input["raw_prompt"] = request.Prompt;
        input["capability_preflight"] ??= new JsonObject();
        input["generator"] ??= new JsonObject();
        var preflight = await RunCapabilityPreflightAsync(ctx, input, snapshot, runtime, ct);
        var state = JsonSerializer.SerializeToNode(preflight, TypedContractJsonContext.Default.CapabilityPreflightResult) as JsonObject
            ?? throw new InvalidOperationException("Could not serialize the planning contract.");
        var allowed = (input["policy"]?["allowed_step_types"] as JsonArray)?.Select(v => v!.GetValue<string>()).ToList()
            ?? ctx.Engine.Registry.RegisteredTypes.ToList();
        var denied = (input["policy"]?["denied_step_types"] as JsonArray)?.Select(v => v!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        allowed.RemoveAll(type => denied.Contains(type) || type is "workflow.plan" or "workflow.execute");
        var declaredStepContracts = ctx.Engine.Registry.GetContracts();
        var capabilities = new List<PlanningCapability>();
        foreach (var capability in preflight.Capabilities.Where(c => c.Resolution != "unavailable"))
        {
            var tool = preflight.DiscoveredServers.FirstOrDefault(s => s.Name == capability.Server)?.Tools.FirstOrDefault(t => t.Name == capability.Method);
            var prompt = capability.Kind == "prompt" ? preflight.DiscoveredServers.FirstOrDefault(s => s.Name == capability.Server)?.Prompts.FirstOrDefault(p => p.Name == capability.Method) : null;
            var stepType = capability.Resolution == "mcp" ? "mcp.call" : capability.Resolution == "native" ? capability.Method ?? "" : "set";
            var contract = declaredStepContracts.GetValueOrDefault(stepType);
            capabilities.Add(new PlanningCapability
            {
                Id = TypedCapabilityIdentity(capability, stepType),
                Description = capability.Description,
                StepType = stepType,
                Server = capability.Server,
                Method = capability.Method,
                Kind = capability.Kind,
                Required = capability.Required,
                DeclarationFingerprint = prompt is not null ? TypedPromptFingerprint(prompt) : tool is null ? null : TypedDeclarationFingerprint(tool),
                EffectKind = capability.ExternalEffectKind ?? (capability.Resolution == "mcp" ? "unknown" : "none"),
                ArtifactContract = tool is null ? null : GetValidatedMcpArtifactContract(tool, capability.Server),
                Activation = capability.Activation,
                CatalogId = capability.CatalogId,
                Resolution = capability.Resolution,
                OperationIds = GetResolvedCapabilityOperationIds(capability).ToList(),
                InputOperationIds = (capability.InputOperationIds ?? []).ToList(),
                InputSchema = prompt is not null ? TypedPromptInputSchema(prompt) : (tool?.InputSchema?.DeepClone() ?? contract?.InputSchema.DeepClone()) as JsonObject ?? new JsonObject(),
                OutputSchema = prompt is not null ? new JsonObject() : (tool is null ? contract?.OutputSchema.DeepClone() : McpToolContractEnricher.GetAuthoritativeOutputSchema(tool)?.DeepClone()) as JsonObject ?? new JsonObject(),
                RequestBindings = capability.RequestBindings.Select(b => new PlanningLiteralBinding(b.Path, b.Value?.DeepClone())).ToList()
            });
        }
        var locked = BuildCapabilityPreflightJson(preflight);
        var stepContracts = new JsonObject(allowed.Select(type => new KeyValuePair<string, JsonNode?>(type,
            declaredStepContracts.GetValueOrDefault(type) is { } contract ? new JsonObject
            {
                ["input"] = contract.InputSchema.DeepClone(),
                ["output"] = contract.OutputSchema.DeepClone()
            } : null)));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString() + input["policy"]?.ToJsonString() + stepContracts.ToJsonString())));
        var preparation = new PlanningPreparation
        {
            Fingerprint = fingerprint,
            LockedContract = locked,
            RuntimeState = state,
            Capabilities = capabilities,
            AllowedStepTypes = allowed,
            StepContracts = stepContracts
        };
        PopulateTypedDecisions(preparation);
        return preparation;
    }

    internal static void PopulateTypedDecisions(PlanningPreparation preparation)
    {
        preparation.DecisionContractVersion = PlanningDecisionContract.CurrentVersion;
        preparation.Decisions.Clear(); preparation.Interactions.Clear();
        foreach (var group in preparation.Capabilities.Where(c => c.Activation is not null).GroupBy(c => c.Activation!.Group))
        {
            var activation = group.First().Activation!;
            var source = preparation.Capabilities.Single(c => c.CatalogId == activation.DecisionProducerCatalogId && c.OperationIds.Contains(activation.DecisionOperationId));
            var confirmation = activation.DecisionContractSource == PlanningDecisionContract.HumanConfirmation;
            preparation.Decisions.Add(new()
            {
                Group = group.Key,
                SourceOperationId = activation.DecisionOperationId,
                SourceCapabilityId = source.Id,
                SourcePointer = activation.DecisionOutputPath,
                ContractSource = activation.DecisionContractSource,
                ResponseSchema = confirmation ? new JsonObject { ["type"] = "boolean" } : new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(activation.AllowedValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) },
                AllowedValues = activation.AllowedValues.ToList(),
                NoEffectValues = activation.NoEffectValues.ToList(),
                EffectOperationIds = group.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal).ToList(),
                InputOperationIds = activation.DecisionInputOperationIds.ToList(),
                PermissionOperationIds = confirmation ? [activation.DecisionOperationId] : activation.DecisionInputOperationIds.Where(id =>
                    preparation.Capabilities.Any(c => c.StepType == "human.input" && c.OperationIds.Contains(id))).ToList()
            });
            if (confirmation)
            {
                var interaction = new PlanningInteractionContract { OperationId = activation.DecisionOperationId, CapabilityId = source.Id };
                source.OutputSchema = interaction.OutputSchema.DeepClone().AsObject();
                if (!preparation.Interactions.Any(i => i.OperationId == interaction.OperationId)) preparation.Interactions.Add(interaction);
            }
        }
    }
}
