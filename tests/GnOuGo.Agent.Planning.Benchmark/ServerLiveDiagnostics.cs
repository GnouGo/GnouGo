using GnOuGo.Flow.Planning.Capabilities;
using GnOuGo.Flow.Integrations.Planning;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.AI.Core;
using GnOuGo.Planning.Examples;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Integrations;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

// Explicit live harness: actual Agent.Server, actual configured provider, existing campaign ledger.
// No credentials or bootstrap links are exported. The campaign lease is held by the caller.
internal static class ServerLiveDiagnostics
{
    internal static bool RequiresInteraction(string[] args)
        => args.ElementAtOrDefault(1) == "server-live-execute" ||
            args.ElementAtOrDefault(1) == "server-live-plan" && args.Contains("--await-delegated-review", StringComparer.Ordinal);

    internal static async Task RunAsync(string[] args, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, string workspace)
    {
        var run = SchemaPortabilityCampaign.RequiredOption(args, "--run");
        var planning = args.ElementAtOrDefault(1) == "server-live-plan";
        var inspectionOnly = args.ElementAtOrDefault(1) == "server-live-inspect";
        var executionOnly = args.ElementAtOrDefault(1) == "server-live-execute";
        var revalidationOnly = args.ElementAtOrDefault(1) == "server-live-revalidate";
        var retained = inspectionOnly || executionOnly || revalidationOnly;
        var mcpReadiness = args.ElementAtOrDefault(1) == "server-live-mcp-readiness";
        var delegatedScenario = planning && SchemaPortabilityCampaign.Option(args, "--case") is "amazon" or "code"
            ? SchemaPortabilityCampaign.Option(args, "--case") : null;
        var browserObservationPolicy = AmazonObservedRedirectPolicy.Capture(args, delegatedScenario ?? "");
        var sdkChatToolsReasoningEffort = SchemaPortabilityCampaign.Option(args, "--sdk-chat-tools-reasoning");
        KeyVaultBenchmarkModel.ValidateSdkChatToolsReasoningEffort(sdkChatToolsReasoningEffort);
        if (sdkChatToolsReasoningEffort is not null && (retained || delegatedScenario != "code"))
            throw new ArgumentException("SDK tool reasoning can be captured only for a fresh Code plan; retained runs use their original capture.");
        var sdkWireApi = SchemaPortabilityCampaign.Option(args, "--sdk-wire-api");
        CampaignInferenceProxy.ValidateWireApi(sdkWireApi);
        if ((sdkWireApi is not null && (retained || delegatedScenario != "code")) || (sdkWireApi == "responses" && sdkChatToolsReasoningEffort is not null))
            throw new ArgumentException("Capture the SDK wire API only on a fresh Code plan; Responses uses the existing medium reasoning setting.");
        var fundingScope = await campaign.FreshAllocationAsync(CancellationToken.None);
        if (!retained && !mcpReadiness && await campaign.HasUncertainRequestAsync(CancellationToken.None))
            throw BenchmarkCampaign.AdmissionFailure("campaign_uncertain_request_requires_recovery");
        if (planning || !retained && !mcpReadiness) model.RetainedExternalReservationsEur = decimal.Parse(
            SchemaPortabilityCampaign.RequiredOption(args, "--retained-external-reservations-eur"), System.Globalization.CultureInfo.InvariantCulture);

        if (run.Length > 70 || !run.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid fresh run identity.");
        if (!retained && await campaign.LoadAsync("planning-evaluation-runs", run) is not null) throw new PlanningConflictException("This live run already exists; start a fresh independent identity.");
        var root = Directory.GetCurrentDirectory();
        var stateRoot = Path.Combine(workspace, ".GnOuGo", "data", "live-server", run); Directory.CreateDirectory(stateRoot);
        var hostArgs = new[]
        {
            "--DevMode:Enabled=true", "--ApplicationLlmBudget:Mode=limited",
            "--TypedWorkflowPlanning:DiagnosticEnabled=true", "--TypedWorkflowPlanning:DefaultThresholdMode=diagnostic",
            "--TypedWorkflowPlanning:DiagnosticModelMaximum=false", "--TypedWorkflowPlanning:OpenLocalOperatorBrowser=false",
            "--TypedWorkflowPlanning:BackgroundProcessingEnabled=" + planning.ToString(), "--OpenTelemetry:Enabled=true", "--OpenTelemetry:TenantId=benchmark",
            "--OtlpCollector:Enabled=false", "--Logging:LogLevel:Default=Warning",
            "--KeyVault:DatabasePath=" + KeyVaultBenchmarkModel.WorkspaceDatabasePath(workspace, null, KeyVaultDatabasePathResolver.DefaultRelativePath),
            "--Agent:DatabasePath=" + KeyVaultBenchmarkModel.WorkspaceDatabasePath(workspace, null, ".GnOuGo/data/gnougo-agent.db"),
            "--Database:Path=" + Path.Combine(stateRoot, "telemetry.db"),
            "--TypedWorkflowPlanning:DatabasePath=" + Path.Combine(stateRoot, "planning.db"),
            "--DocsIngestorMcp:DatabasePath=" + Path.Combine(stateRoot, "docs.db"),
            "--DocsIngestorMcp:VectorDatabasePath=" + Path.Combine(stateRoot, "vectors.sqlite"),
            "--DocsIngestorMcp:OriginalsDirectory=" + Path.Combine(stateRoot, "originals")
        };
        if (fundingScope is not null)
            hostArgs = hostArgs.Concat(new[] { "--WorkflowPlanningBudget:Currency=EUR",
                "--WorkflowPlanningBudget:Amount=" + fundingScope["current_authorized_ceiling_eur"]!.GetValue<decimal>().ToString("G29", System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
        var financialForecastMode = SchemaPortabilityCampaign.Option(args, "--financial-forecast-mode") ?? "strict";
        if (financialForecastMode is not ("strict" or "experimental_warning")) throw new ArgumentException("Invalid financial forecast mode.");
        model.AdvisoryFinancialForecasts = financialForecastMode == "experimental_warning";
        hostArgs = hostArgs.Append("--ApplicationLlmBudget:FinancialForecastMode=" + financialForecastMode).ToArray();
        var phaseFile = SchemaPortabilityCampaign.Option(args, "--server-phase-policy");
        var transportFile = SchemaPortabilityCampaign.Option(args, "--server-transport-policy");
        if (transportFile is not null)
        {
            var transport = JsonSerializer.Deserialize(await File.ReadAllTextAsync(transportFile), PlanningJsonContext.Default.PlanningTransportPolicy)
                ?? throw new ArgumentException("Missing transport policy.");
            PlanningTransportPolicies.Validate(transport);
            hostArgs = hostArgs.Concat(new[] {
                $"--TypedWorkflowPlanning:TransportPolicy:Version={transport.Version}",
                $"--TypedWorkflowPlanning:TransportPolicy:MaxAttempts={transport.MaxAttempts}",
                $"--TypedWorkflowPlanning:TransportPolicy:MaxUncertainRetries={transport.MaxUncertainRetries}",
                $"--TypedWorkflowPlanning:TransportPolicy:AttemptTimeoutMilliseconds={transport.AttemptTimeoutMilliseconds}",
                $"--TypedWorkflowPlanning:TransportPolicy:BaseDelayMilliseconds={transport.BaseDelayMilliseconds}",
                $"--TypedWorkflowPlanning:TransportPolicy:MaxDelayMilliseconds={transport.MaxDelayMilliseconds}",
                $"--TypedWorkflowPlanning:TransportPolicy:MaxTotalDelayMilliseconds={transport.MaxTotalDelayMilliseconds}" }).ToArray();
            if (transport.IndependentGenerationMaxElapsedMilliseconds is { } total)
                hostArgs = hostArgs.Append($"--TypedWorkflowPlanning:TransportPolicy:IndependentGenerationMaxElapsedMilliseconds={total}").ToArray();
        }
        if (phaseFile is not null)
        {
            var phases = JsonSerializer.Deserialize(await File.ReadAllTextAsync(phaseFile), PlanningJsonContext.Default.PlanningConstructionPhasePolicy)
                ?? throw new ArgumentException("Missing phase policy.");
            PlanningConstructionPhasePolicies.Validate(phases);
            hostArgs = hostArgs.Concat(new[] { ("Navigation", phases.Navigation), ("Draft", phases.Draft), ("Review", phases.Review) }.SelectMany(pair => new[] {
                $"--TypedWorkflowPlanning:PhasePolicy:{pair.Item1}:TargetInputTokens={pair.Item2.TargetInputTokens}",
                $"--TypedWorkflowPlanning:PhasePolicy:{pair.Item1}:MaxInputTokens={pair.Item2.MaxInputTokens}",
                $"--TypedWorkflowPlanning:PhasePolicy:{pair.Item1}:OutputTokens={pair.Item2.OutputTokens}",
                $"--TypedWorkflowPlanning:PhasePolicy:{pair.Item1}:Reasoning={pair.Item2.Reasoning}" })).ToArray();
        }
        await using var app = GnOuGoAgentWebHost.Build(hostArgs, "http://127.0.0.1:0", Path.Combine(root, "src/GnOuGo.Agent.Server"), false,
            services => {
                services.AddSingleton<IPlanningDiscoveryRecoveryGuard>(new RetainedDiscoveryGuard(campaign));
                if (!planning) return;
                services.AddSingleton<SecureWorkflowRuntimeFactory>(sp =>
                {
                    IMcpClientFactory? discovery = null;
                    if (delegatedScenario is not null)
                    {
                        var configurations = LiveWorkflowEvaluation.Configuration(model.McpServers, delegatedScenario, null, "Release", browserObservationPolicy: browserObservationPolicy);
                        var policyHash = DelegatedDiscoveryPolicyHash();
                        discovery = new DelegatedPlanningDiscovery(new ConfiguredMcpClientFactory(configurations, defaultLlmProvider: model.Provider, defaultLlmModel: model.Model), delegatedScenario, policyHash,
                            async ct =>
                            {
                                // Resolve after Server has bound its mounted HTTP listener. Never
                                // convert the host's Agent MCP into a standalone stdio process.
                                var current = await sp.GetRequiredService<LlmDefaultsResolver>().ResolveAsync(ct);
                                var agent = current.McpServers["GnOuGo.Agent.Mcp"];
                                return new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions> { ["GnOuGo.Agent.Mcp"] = agent });
                            });
                    }
                    return new(sp.GetRequiredService<LLMRuntimeOptionsStore>(),
                        sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                        llmClientOverride: new CampaignClient(model, sp.GetRequiredService<IPlanningSessionStore>(),
                            sp.GetRequiredService<LlmTraceCapture>(), sp.GetRequiredService<LLMRuntimeOptionsStore>()),
                        mcpClientFactoryOverride: discovery, defaults: sp.GetRequiredService<LlmDefaultsResolver>());
                });
            });
        if (!planning)
            app.Use(async (context, next) =>
            {
                var safeProbe = !retained && !mcpReadiness && context.Request.Path == "/api/planning/live-provider-probe";
                if (context.Request.Path.StartsWithSegments("/api/planning") && !safeProbe &&
                    !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                { context.Response.StatusCode = 403; return; }
                await next(context);
            });
        var options = await app.Services.GetRequiredService<LlmDefaultsResolver>().ResolveAsync();
        if (options.DefaultProvider != model.Provider || options.DefaultModel != model.Model ||
            options.ResolveProvider(model.Provider)!.Url != model.CampaignConfiguration()["endpoint"]!.ToString() ||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(options.ResolveProvider(model.Provider)!.RequestPolicy), model.CampaignConfiguration()["request_policy"]))
            throw new PlanningConflictException("Server and campaign effective provider/model/endpoint/request settings differ; no inference started.");
        var report = new JsonObject { ["source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"), ["run"] = run,
            ["campaign"] = campaign.Id, ["campaign_configuration"] = campaign.ConfigurationId,
            ["provider"] = options.DefaultProvider, ["model"] = options.DefaultModel, ["host"] = "GnOuGo.Agent.Server",
            ["budget_mode"] = "limited", ["financial_forecast_mode"] = financialForecastMode, ["retained_external_reservations_eur"] = model.RetainedExternalReservationsEur, ["started_utc"] = DateTimeOffset.UtcNow, ["phase"] = planning ? "server_planning" : "provider_probe",
            ["working_directory"] = root, ["launch_arguments"] = SafeLaunchArguments(),
            ["loaded_assemblies"] = new JsonObject { ["entry"] = AssemblyEvidence(System.Reflection.Assembly.GetEntryAssembly()!),
                ["server"] = AssemblyEvidence(typeof(GnOuGoAgentWebHost).Assembly) },
            ["configuration_sources"] = model.ConfigurationSources.DeepClone(),
            ["server_model_source"] = app.Services.GetRequiredService<LLMRuntimeOptionsStore>().DefaultModelSource };
        if (sdkChatToolsReasoningEffort is not null) report["sdk_chat_tools_reasoning_effort"] = sdkChatToolsReasoningEffort;
        if (sdkWireApi is not null) report["sdk_wire_api"] = sdkWireApi;
        if (fundingScope is not null) report["funding_scope"] = fundingScope.DeepClone();
        if (browserObservationPolicy is not null)
        {
            report["browser_observation_policy"] = browserObservationPolicy;
            report["browser_scope_approval"] = AmazonObservedRedirectPolicy.ApprovalFor(browserObservationPolicy);
        }
        if (phaseFile is not null) report["phase_policy_sha256"] = PlanningGraphCompiler.Fingerprint(await File.ReadAllTextAsync(phaseFile));
        if (delegatedScenario is not null) report["planning_discovery_authority"] = "existing_delegated_tool_allowlist_v1";
        if (!retained) await campaign.SaveAsync("planning-evaluation-runs", run, report);
        var invoked = 0;
        if (!planning && !retained && !mcpReadiness) app.MapPost("/api/planning/live-provider-probe", async (HttpContext context, LocalOperatorAccess access, IAntiforgery antiforgery, LlmTraceCapture capture) =>
        {
            if (!await access.CanInspectAsync()) return Results.Forbid();
            await antiforgery.ValidateRequestAsync(context);
            if (Interlocked.CompareExchange(ref invoked, 1, 0) != 0) return Results.Conflict("A fresh independent run is required; this probe cannot replay.");
            var nonce = Guid.NewGuid().ToString("N");
            var sourceRequest = SchemaPortabilityCampaign.Option(args, "--probe-source-request");
            var original = sourceRequest is null ? null :
                (await campaign.LoadAsync("planning-evaluation-requests", sourceRequest, context.RequestAborted))?.Deserialize(PlanningJsonContext.Default.LLMRequest)
                ?? throw new ArgumentException("The exact owned source request is unavailable.");
            var request = IndependentProbe(original, model.Provider, model.Model, run, nonce,
                SchemaPortabilityCampaign.Option(args, "--probe-reasoning"),
                SchemaPortabilityCampaign.Option(args, "--probe-output-tokens") is { } allocation
                    ? int.Parse(allocation, System.Globalization.CultureInfo.InvariantCulture) : original?.MaxTokens ?? 1024,
                SchemaPortabilityCampaign.Option(args, "--probe-structured-strict") is { } strict ? bool.Parse(strict) : null);
            if (SchemaPortabilityCampaign.Option(args, "--probe-prompt-amendment") is { } amendmentFile)
            {
                if (original is null || args.Any(a => a.StartsWith("--probe-", StringComparison.Ordinal) &&
                    a is not ("--probe-source-request" or "--probe-prompt-amendment")))
                    throw new ArgumentException("A prompt-evidence experiment requires an exact source and no other probe override.");
                var amendment = JsonNode.Parse(await File.ReadAllTextAsync(amendmentFile, context.RequestAborted)) as JsonObject
                    ?? throw new ArgumentException("A prompt-evidence amendment object is required.");
                report["prompt_evidence_amendment"] = AmendProbePrompt(request, run, nonce, amendment);
            }
            if (args.Contains("--probe-share-schema-evidence", StringComparer.Ordinal))
            {
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.ShareDuplicateContractSchemas(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["schema_evidence_compaction"] = new JsonObject { ["original_prompt_bytes"] = System.Text.Encoding.UTF8.GetByteCount(before),
                    ["compacted_prompt_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt), ["response_schema_unchanged"] = true };
            }
            if (args.Contains("--probe-present-response-schema", StringComparer.Ordinal))
            {
                PresentProbeResponseSchema(request, run, nonce);
                report["response_schema_presented_in_prompt"] = true;
            }
            if (args.Contains("--probe-intent-declaration-fields", StringComparer.Ordinal))
            {
                if (args.Contains("--probe-present-response-schema", StringComparer.Ordinal))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                PresentProbeIntentDeclarationFields(request, run, nonce);
                report["intent_declaration_fields_presented_in_prompt"] = true;
            }
            if (args.Contains("--probe-business-dependency-fields", StringComparer.Ordinal))
            {
                if (args.Contains("--probe-present-response-schema", StringComparer.Ordinal) || args.Contains("--probe-intent-declaration-fields", StringComparer.Ordinal))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                PresentProbeBusinessDependencyFields(request, run, nonce);
                report["business_dependency_fields_presented_in_prompt"] = true;
            }
            if (args.Contains("--probe-discovery-response-fields", StringComparer.Ordinal))
            {
                if (args.Contains("--probe-present-response-schema", StringComparer.Ordinal) ||
                    args.Contains("--probe-intent-declaration-fields", StringComparer.Ordinal) ||
                    args.Contains("--probe-business-dependency-fields", StringComparer.Ordinal))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentDiscoveryResponseFields(before, request.StructuredOutputSchema!);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["discovery_response_field_guide"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            if (args.Contains("--probe-delivery-channel-guidance", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence"))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentDeliveryChannelGuidance(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["delivery_channel_guidance"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            if (args.Contains("--probe-compact-json-guidance", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance"))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentCompactJsonGuidance(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["compact_json_guidance"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            if (args.Contains("--probe-specific-array-branches", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance" or "--probe-compact-json-guidance"))
                    throw new ArgumentException("Choose one representation factor for this probe.");
                var before = request.StructuredOutputSchema!;
                request.StructuredOutputSchema = PlanningPromptPresentation.OrderBoundedArrayAlternativesFirst(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["specific_array_branch_order"] = new JsonObject { ["active"] = !JsonNode.DeepEquals(before, request.StructuredOutputSchema),
                    ["logical_constraints_preserved"] = true, ["prompt_unchanged"] = true };
            }
            if (args.Contains("--probe-materialization-guidance", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance" or "--probe-compact-json-guidance" or "--probe-specific-array-branches"))
                    throw new ArgumentException("Choose one representation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentContractMaterializationGuidance(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["contract_materialization_guidance"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["schema_unchanged"] = true, ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            if (args.Contains("--probe-group-composition-guidance", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance" or "--probe-compact-json-guidance" or "--probe-specific-array-branches" or "--probe-materialization-guidance"))
                    throw new ArgumentException("Choose one representation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentGroupCompositionGuidance(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["group_composition_guidance"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["schema_unchanged"] = true, ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            if (args.Contains("--probe-evidence-selection-objects", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance" or "--probe-compact-json-guidance" or "--probe-specific-array-branches" or
                    "--probe-materialization-guidance" or "--probe-group-composition-guidance"))
                    throw new ArgumentException("Choose one representation factor for this probe.");
                var before = request.StructuredOutputSchema!;
                request.StructuredOutputSchema = PlanningPromptPresentation.PresentEvidenceSelectionObjects(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["evidence_selection_object_encoding"] = new JsonObject { ["active"] = !JsonNode.DeepEquals(before, request.StructuredOutputSchema),
                    ["canonical_choices_and_bounds_preserved"] = true, ["prompt_unchanged"] = true };
            }
            if (args.Contains("--probe-runtime-prerequisite-guidance", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance" or "--probe-compact-json-guidance" or "--probe-specific-array-branches" or
                    "--probe-materialization-guidance" or "--probe-group-composition-guidance" or "--probe-evidence-selection-objects"))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentRuntimePrerequisiteGuidance(before);
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["runtime_prerequisite_guidance"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["schema_unchanged"] = true, ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            if (args.Contains("--probe-native-control-guidance", StringComparer.Ordinal))
            {
                if (args.Any(option => option is "--probe-present-response-schema" or "--probe-intent-declaration-fields" or
                    "--probe-business-dependency-fields" or "--probe-discovery-response-fields" or "--probe-share-schema-evidence" or
                    "--probe-delivery-channel-guidance" or "--probe-compact-json-guidance" or "--probe-specific-array-branches" or
                    "--probe-materialization-guidance" or "--probe-group-composition-guidance" or "--probe-evidence-selection-objects" or
                    "--probe-runtime-prerequisite-guidance"))
                    throw new ArgumentException("Choose one presentation factor for this probe.");
                var before = request.Prompt;
                request.Prompt = PlanningPromptPresentation.PresentNativeControlGuidance(before, request.StructuredOutputSchema!);
                if (request.Prompt == before) throw new ArgumentException("The source request does not offer a local control; no native-control probe can be dispatched.");
                request.ClientRequestId = null;
                request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                report["native_control_guidance"] = new JsonObject { ["active"] = request.Prompt != before,
                    ["schema_unchanged"] = true, ["added_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(request.Prompt) - System.Text.Encoding.UTF8.GetByteCount(before) };
            }
            report["request_id"] = request.ClientRequestId; report["requested_output_tokens"] = request.MaxTokens;
            if (args.Contains("--probe-check-responses", StringComparer.Ordinal))
                report["responses_route_inspection"] = await model.InspectResponsesRouteAsync(run, context.RequestAborted);
            report["requested_reasoning"] = request.Reasoning;
            report["requested_structured_output_strict"] = request.StructuredOutputStrict;
            report["independent_probe"] = new JsonObject { ["source_request"] = sourceRequest,
                ["source_request_hash"] = original is null ? null : PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest)),
                ["prompt_hash"] = PlanningGraphCompiler.Fingerprint(request.Prompt),
                ["schema_hash"] = PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema!.ToJsonString()),
                ["applies_candidate"] = false, ["executes_business_tools"] = false };
            await campaign.SaveAsync("planning-evaluation-runs", run, report);
            using var activity = new Activity("live.provider.structured_output_probe").Start();
            try
            {
                // An isolated probe has no planning-session retry capture. Use the
                // existing single-attempt inference journal, never planning defaults.
                var result = await capture.CallAsync(request, options, (r, ct) => model.CallExecutionAsync(r, ct), context.RequestAborted);
                var received = result.Json ?? (result.Text is null ? null : JsonNode.Parse(result.Text));
                var schemaValid = PlanningContractValidation.ValidateInstance(received, request.StructuredOutputSchema!).Count == 0;
                report["schema_valid"] = schemaValid;
                report["executable_extension_returned"] = received?["extension"] is JsonObject;
                report["passed"] = result.CompletionStatus is null && schemaValid && (original is null
                    ? received?["ok"]?.GetValue<bool>() == true && received?["nonce"]?.ToString() == nonce
                    : !args.Contains("--probe-require-extension", StringComparer.Ordinal) || received?["extension"] is JsonObject);
                report["completion_status"] = result.CompletionStatus; report["usage"] = result.Usage?.DeepClone();
            }
            catch (Exception error)
            {
                report["passed"] = false; report["exception_type"] = error.GetType().FullName;
                report["exception_stack"] = error.StackTrace; report["campaign_stop_reason"] = campaign.StopReason;
                if (error is LLMClientException failure)
                {
                    report["failure_kind"] = failure.Kind.ToString(); report["http_status"] = failure.StatusCode;
                    report["safe_provider_code"] = failure.SafeProviderCode; report["provider_stage"] = failure.Stage.ToString();
                }
                if (error.Data[BenchmarkCampaign.BudgetRefusalData] is JsonObject refusal) report["financial_refusal"] = refusal.DeepClone();
                if (error is GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException runtimeFailure)
                { report["failure_code"] = runtimeFailure.Code; report["financial_refusal"] = runtimeFailure.Details?.DeepClone(); }
                // Exception messages and provider bodies remain in protected encrypted evidence.
            }
            var transportEvidence = await campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, CancellationToken.None);
            report["physical_http_attempts"] = transportEvidence?["transport"]?["Attempts"] is JsonArray attempts ? attempts.Count :
                report["financial_refusal"]?["dispatch_status"]?.ToString() == "not_started" ? 0 : null;
            report["durable_receipt"] = await campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId!, CancellationToken.None) is not null;
            if (transportEvidence?["transport"] is JsonObject wire)
            {
                var body = JsonNode.Parse(wire["RequestBody"]!.ToString())!;
                report["effective_http"] = new JsonObject { ["method"] = wire["RequestMethod"]?.DeepClone(),
                    ["endpoint_sha256"] = PlanningGraphCompiler.Fingerprint(wire["Endpoint"]!.ToString()),
                    ["body_sha256"] = PlanningGraphCompiler.Fingerprint(wire["RequestBody"]!.ToString()),
                    ["model"] = body["model"]?.DeepClone(), ["reasoning_effort"] = body["reasoning_effort"]?.DeepClone(),
                    ["max_completion_tokens"] = body["max_completion_tokens"]?.DeepClone(), ["stream"] = body["stream"]?.DeepClone(),
                    ["tool_count"] = (body["tools"] as JsonArray)?.Count ?? 0,
                    ["captured_transport"] = transportEvidence["effective_transport_policy"]?.DeepClone() };
            }
            report["finished_utc"] = DateTimeOffset.UtcNow; report["trace_id"] = activity.TraceId.ToHexString();
            await campaign.SaveAsync("planning-evaluation-runs", run, report, CancellationToken.None);
            return Results.Json(report);
        });
        await app.StartAsync();
        try
        {
            var url = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app).AppBaseAddress!;
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(12) };
            var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(client.BaseAddress);
            using var auth = await client.PostAsync("/local-operator/session", new StringContent(launch.Fragment[1..])); auth.EnsureSuccessStatusCode();
            var csrf = await client.GetFromJsonAsync<JsonObject>("/api/planning/antiforgery");
            client.DefaultRequestHeaders.Add(csrf!["headerName"]!.ToString(), csrf["requestToken"]!.ToString());
            if (!retained && !mcpReadiness)
            {
                await VerifyFrontendAsync(app, root, url, run, CancellationToken.None);
                report["frontend_ready"] = true;
                await campaign.SaveAsync("planning-evaluation-runs", run, report);
            }
            if (!retained)
            {
                using var limited = await client.PostAsJsonAsync("/api/application-llm-budget", new JsonObject { ["mode"] = "limited", ["financialForecastMode"] = financialForecastMode }); limited.EnsureSuccessStatusCode();
            }
            if (retained)
            {
                var saved = await campaign.LoadAsync("planning-evaluation-runs", run) ?? throw new PlanningConflictException("No retained live run.");
                await ExportInspectionAsync(client, saved["session_id"]!.ToString(), root, run);
                if (executionOnly || revalidationOnly)
                {
                    if (saved["execution_started"] is not null) throw new PlanningConflictException("Execution was already started; uncertain effects cannot be replayed.");
                    await campaign.RequireRunNotRetainedAsync(saved["session_id"]!.ToString(), CancellationToken.None);
                    RequireSameProductionSource(saved["source"]!.ToString());
                    var state = await app.Services.GetRequiredService<PlanningSessionService>().GetAsync(saved["session_id"]!.ToString(), CancellationToken.None)
                        ?? throw new PlanningConflictException("No retained Server session.");
                    if (revalidationOnly)
                    {
                        await RevalidateRetainedArtifactAsync(app, campaign, model, state, saved, args);
                        Console.WriteLine(new JsonObject { ["run"] = run, ["revalidated"] = true, ["provider_dispatches"] = 0 }.ToJsonString());
                        return;
                    }
                    if (state.Status != PlanningStatus.FinalReview || state.ComputeArtifactHash() != saved["session"]?.Deserialize(PlanningJsonContext.Default.PlanningSession)?.ComputeArtifactHash())
                        throw new PlanningConflictException("Only the exact unexecuted review-ready artifact may continue.");
                    saved["execution_harness_source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD");
                    saved["execution_harness_assembly"] = AssemblyEvidence(System.Reflection.Assembly.GetEntryAssembly()!);
                    model.RetainedExternalReservationsEur = saved["retained_external_reservations_eur"]?.GetValue<decimal>() ?? 0;
                    if (!await ExecuteReviewedLocalAsync(app, campaign, model, state, saved, root, workspace))
                        throw new PlanningConflictException("The retained artifact was revised; execution requires its new captured review-ready state.");
                }
                Console.WriteLine(new JsonObject { ["run"] = run, ["inspection_only"] = inspectionOnly, ["provider_dispatches"] = 0 }.ToJsonString());
            }
            else if (mcpReadiness)
            {
                using (var denied = await client.PostAsJsonAsync("/api/planning", new GnOuGo.Agent.Shared.PlanningStartDto("forbidden-metadata-inference", "No inference is authorized by metadata readiness.")))
                {
                    if (denied.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("Metadata readiness exposed a planning mutation endpoint.");
                    report["planning_mutations_blocked"] = true;
                }
                var server = SchemaPortabilityCampaign.RequiredOption(args, "--mcp-server");
                await using var runtime = await app.Services.GetRequiredService<SecureWorkflowRuntimeFactory>().CreateAsync(CancellationToken.None);
                var config = runtime.Options.McpServers[server];
                report["phase"] = "mcp_readiness"; report["mcp_server"] = server; report["transport"] = config.Type;
                report["command"] = config.Command;
                report["command_file_exists_at_entry_base"] = config.Command is { } command && File.Exists(Path.GetFullPath(command, AppContext.BaseDirectory));
                report["argument_count"] = config.Args?.Count;
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    if (args.Contains("--snapshot-pagination", StringComparer.Ordinal))
                    {
                        var catalog = new CapabilityDiscovery(new() { McpClientFactory = runtime.McpClientFactory });
                        var snapshot = await catalog.CaptureAsync(CapabilityDiscovery.SourceId(server), deadline.Token);
                        var journal = new PlanningCapabilitySnapshotJournal(app.Services.GetRequiredService<GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore>());
                        var action = Guid.NewGuid().ToString("N");
                        var reference = await journal.PublishAsync("benchmark", run, action, snapshot, deadline.Token);
                        var expectedCount = snapshot.Capabilities.Count;
                        var seen = new HashSet<string>(StringComparer.Ordinal); string? cursor = null; var pages = 0; var recreations = 0;
                        do
                        {
                            var retainedSnapshot = await journal.ReadAsync(reference, "benchmark", run, action, reference.SourceId, deadline.Token);
                            var page = catalog.ReadPage(retainedSnapshot, cursor, "Inspect the declared operations of this configured source.");
                            if (page.Capabilities.Any(c => !seen.Add(c.Id))) throw new InvalidOperationException("Duplicate capability in snapshot pagination.");
                            pages++; cursor = page.NextCursor;
                            if (cursor is not null && pages % 4 == 0)
                            {
                                catalog = new(new() { McpClientFactory = runtime.McpClientFactory });
                                journal = new(app.Services.GetRequiredService<GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore>());
                                recreations++;
                            }
                        } while (cursor is not null);
                        if (seen.Count != expectedCount) throw new InvalidOperationException("Snapshot pagination did not preserve exact coverage.");
                        report["capability_count"] = expectedCount; report["distinct_capabilities"] = seen.Count;
                        report["page_count"] = pages; report["runtime_recreations"] = recreations;
                        report["snapshot_version"] = reference.Version; report["provider_dispatches"] = 0;
                    }
                    else
                    {
                        var connection = await runtime.McpClientFactory.GetClientAsync(server, deadline.Token);
                        var tools = await connection.ListToolsAsync(deadline.Token);
                        report["tool_count"] = tools.Count;
                    }
                    report["passed"] = true;
                }
                catch (Exception error)
                {
                    var formatted = runtime.McpClientFactory is IMcpExecutionHooks hooks ? hooks.FormatFailureDiagnostics(server, error) : error.ToString();
                    formatted = app.Services.GetRequiredService<LlmTraceContentStore>().Redact(formatted);
                    foreach (var secret in new[] { config.ApiKey, config.ClientSecret })
                        if (!string.IsNullOrEmpty(secret)) formatted = formatted.Replace(secret, "[redacted]", StringComparison.Ordinal);
                    report["passed"] = false; report["exception_type"] = error.GetType().Name; report["diagnostics"] = formatted;
                    Environment.ExitCode = 1;
                }
                report["provider_dispatches"] = 0; report["finished_utc"] = DateTimeOffset.UtcNow;
                await campaign.SaveAsync("planning-evaluation-runs", run, report);
                Console.WriteLine(report.ToJsonString());
            }
            else if (planning)
            {
                var scenario = SchemaPortabilityCampaign.Option(args, "--case") ?? "local";
                var relative = "workflows/" + campaign.Id + "/" + run;
                var prompt = scenario switch { "local" => PlanningBenchmarkCases.Prompt("local"),
                    "collections" => PlanningBenchmarkCases.Prompt("collections"),
                    ReviewBoundaryQualification.Scenario => ReviewBoundaryQualification.Prompt,
                    "amazon" => LiveWorkflowEvaluation.AmazonEvaluationPrompt(relative, 3, browserObservationPolicy),
                    "code" => LiveWorkflowEvaluation.CodeEvaluationPrompt(relative), _ => throw new ArgumentException("Unknown live scenario.") };
                report["scenario"] = scenario;
                report["workspace_relative"] = relative;
                report["inputs"] = scenario == "amazon" ? new JsonObject { ["query"] = "chaussure geox homme 45" } : scenario == "code"
                    ? new JsonObject { ["pullRequestUrl"] = "https://github.com/AxaFrance/SmartGuide/pull/610", ["reviewText"] = "Review correctness, regressions, security and test coverage of the pinned pull-request diff. Report checks truthfully and keep proposed comments local." } : new JsonObject();
                report["max_products"] = 3;
                if (scenario is "amazon" or "code")
                {
                    report["oracle_version"] = scenario == "amazon" ? browserObservationPolicy is null ? LiveWorkflowOracles.AmazonBusinessVersion :
                        browserObservationPolicy == AmazonAaxProductPolicy.Id ? LiveWorkflowOracles.AmazonAaxProductVersion :
                        browserObservationPolicy == AmazonSourceCoveragePolicy.Id ? LiveWorkflowOracles.AmazonSourceCoverageVersion :
                        browserObservationPolicy == AmazonStoreListPolicy.Id ? LiveWorkflowOracles.AmazonStoreListVersion :
                        browserObservationPolicy == AmazonCardReviewPolicy.Id ? LiveWorkflowOracles.AmazonCardReviewVersion :
                        browserObservationPolicy == AmazonReviewProofPolicy.Id ? LiveWorkflowOracles.AmazonReviewProofVersion :
                        browserObservationPolicy == AmazonResolvedProductPolicy.Id ? LiveWorkflowOracles.AmazonResolvedProductVersion :
                        browserObservationPolicy == AmazonResolvedStorePolicy.Id ? LiveWorkflowOracles.AmazonResolvedStoreVersion :
                        browserObservationPolicy == AmazonProductContextPolicy.Id ? LiveWorkflowOracles.AmazonProductContextVersion :
                        browserObservationPolicy == AmazonSponsoredHostPolicy.Id ? LiveWorkflowOracles.AmazonSponsoredHostVersion : LiveWorkflowOracles.AmazonObservedRedirectVersion : LiveWorkflowOracles.CodeBusinessVersion;
                    report["mcp_builds"] = LiveWorkflowEvaluation.CaptureMcpBuilds(LiveWorkflowEvaluation.Configuration(model.McpServers, scenario, null, "Release", browserObservationPolicy: browserObservationPolicy), report["source"]!.ToString());
                }
                using var started = await client.PostAsJsonAsync("/api/planning", new JsonObject { ["name"] = run, ["prompt"] = prompt,
                    ["mode"] = "auto", ["thresholdMode"] = "diagnostic" });
                started.EnsureSuccessStatusCode();
                var dto = await started.Content.ReadFromJsonAsync<JsonObject>(); var id = dto!["id"]!.ToString();
                report["session_id"] = id; report["operator_url"] = url.TrimEnd('/') + "/planning/" + id;
                Console.WriteLine(new JsonObject { ["run"] = run, ["session_id"] = id, ["operator_url"] = report["operator_url"]!.DeepClone() }.ToJsonString());
                using var observer = StartObserver(app, root, url, id, run);
                long revision = -1;
                while (true)
                {
                    var state = await app.Services.GetRequiredService<PlanningSessionService>().GetAsync(id, CancellationToken.None)
                        ?? throw new PlanningConflictException("The live Server session disappeared.");
                    if (state.Revision != revision)
                    {
                        revision = state.Revision;
                        if (scenario == ReviewBoundaryQualification.Scenario) report["unchanged_plan_review"] = ReviewBoundaryQualification.Evidence(state);
                        report["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
                        report["result"] = new JsonObject { ["status"] = state.Status, ["calls"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts,
                            ["execution_status"] = "not_started", ["execution_correct"] = false };
                        await campaign.SaveAsync("planning-evaluation-runs", run, report);
                        Console.WriteLine(new JsonObject { ["session_id"] = id, ["status"] = state.Status, ["phase"] = state.Phase,
                            ["revision"] = revision, ["calls"] = state.ModelCalls,
                            ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => (JsonNode)JsonValue.Create(d.Code)).ToArray()) }.ToJsonString());
                    }
                    if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status))
                    {
                        report["passed"] = state.Status == PlanningStatus.FinalReview; report["finished_utc"] = DateTimeOffset.UtcNow;
                        await ExportInspectionAsync(client, id, root, run);
                        await campaign.SaveAsync("planning-evaluation-runs", run, report);
                        if (report["passed"]!.GetValue<bool>() != true) Environment.ExitCode = 1;
                        if (observer is not null)
                        {
                            try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                            finally { if (!observer.HasExited) observer.Kill(entireProcessTree: true); }
                            report["browser_observation_exit_code"] = observer.ExitCode;
                            await campaign.SaveAsync("planning-evaluation-runs", run, report);
                        }
                        if (state.Status == PlanningStatus.FinalReview && args.Contains("--await-delegated-review", StringComparer.Ordinal) &&
                            !await ExecuteReviewedLocalAsync(app, campaign, model, state, report, root, workspace))
                        {
                            (report["review_wait_history"] ??= new JsonArray()).AsArray().Add(new JsonObject
                            {
                                ["revision"] = state.Revision, ["artifact_hash"] = state.ComputeArtifactHash(),
                                ["disposition"] = "returned_to_session_monitor_without_execution", ["observed_at_utc"] = DateTimeOffset.UtcNow
                            });
                            report["passed"] = false;
                            revision = -1;
                            continue;
                        }
                        break;
                    }
                    await Task.Delay(2000);
                }
            }
            else
            {
                using var response = await client.PostAsync("/api/planning/live-provider-probe", new StringContent("{}")); response.EnsureSuccessStatusCode();
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                if (report["passed"]?.GetValue<bool>() != true) Environment.ExitCode = 1;
            }
        }
        finally { await app.StopAsync(); }
    }

    internal static LLMRequest IndependentProbe(LLMRequest? original, string provider, string model, string run, string nonce, string? reasoning, int outputTokens,
        bool? structuredStrict = null)
    {
        if (outputTokens <= 0 || reasoning is not null && string.IsNullOrWhiteSpace(reasoning))
            throw new ArgumentException("A positive output allocation and a valid optional reasoning override are required.");
        if (original is not null && (original.Provider != provider || original.Model != model || original.Tools is { Count: > 0 } ||
            original.UseBackgroundMode || original.StructuredOutputSchema is null))
            throw new ArgumentException("Only exact same-deployment tool-free foreground structured generation can seed an independent probe.");
        var request = original is null ? new LLMRequest { Provider = provider, Model = model,
            Prompt = "Return the requested JSON object. Set ok to true and nonce to " + nonce + ". No tools or external actions.",
            StructuredOutputSchema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" },
                    ["nonce"] = new JsonObject { ["type"] = "string", ["const"] = nonce } }, ["required"] = new JsonArray("ok", "nonce") } }
            : JsonSerializer.Deserialize(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        request.ClientRequestId = null;
        request.Reasoning = reasoning ?? (original is null ? "medium" : original.Reasoning);
        request.MaxTokens = outputTokens; request.RequireOutputTokenLimit = true;
        if (structuredStrict is { } strict) request.StructuredOutputStrict = strict;
        request.DisableTransportRetries = true; request.RequiredReviewCalls = null;
        request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        return request;
    }
    // One hash-bound presentation experiment, never applied to its source session.
    internal static JsonObject AmendProbePrompt(LLMRequest request, string run, string nonce, JsonObject amendment)
    {
        var before = amendment["before"]?.GetValue<string>(); var after = amendment["after"]?.GetValue<string>();
        var count = amendment["occurrences"]?.GetValue<int>() ?? 0;
        var hash = PlanningGraphCompiler.Fingerprint(request.Prompt);
        var schemaHash = request.StructuredOutputSchema is null ? null : PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema.ToJsonString());
        var responseSchema = amendment["responseSchema"] as JsonObject;
        if (request.Tools is { Count: > 0 } || request.UseBackgroundMode || request.StructuredOutputSchema is null ||
            string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after) || before == after || count is < 1 or > 32 ||
            amendment["expectedPromptHash"]?.ToString() != hash ||
            request.Prompt.Split(before, StringSplitOptions.None).Length - 1 != count)
            throw new ArgumentException("The tool-free prompt experiment must match its exact owned baseline and occurrence count.");
        if (amendment.ContainsKey("responseSchema") || amendment.ContainsKey("expectedSchemaHash"))
            if (responseSchema is null || amendment["expectedSchemaHash"]?.ToString() != schemaHash ||
                PlanningContractValidation.ValidateSchema(responseSchema, strict: true).Count != 0)
                throw new ArgumentException("A captured response-schema experiment requires its exact source schema hash and a valid strict schema.");
        var changed = request.Prompt.Replace(before, after, StringComparison.Ordinal);
        var evidence = new JsonObject { ["before_prompt_hash"] = hash, ["after_prompt_hash"] = PlanningGraphCompiler.Fingerprint(changed),
            ["before_evidence_hash"] = PlanningGraphCompiler.Fingerprint(before), ["after_evidence_hash"] = PlanningGraphCompiler.Fingerprint(after),
            ["occurrences"] = count, ["source_session_modified"] = false, ["response_schema_changed"] = responseSchema is not null };
        if (responseSchema is not null)
        {
            evidence["before_schema_hash"] = schemaHash;
            evidence["after_schema_hash"] = PlanningGraphCompiler.Fingerprint(responseSchema.ToJsonString());
            request.StructuredOutputSchema = responseSchema.DeepClone();
        }
        request.Prompt = changed; request.ClientRequestId = null;
        request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        return evidence;
    }

    internal static void PresentProbeResponseSchema(LLMRequest request, string run, string nonce)
    {
        if (request.StructuredOutputSchema is null || request.Tools is { Count: > 0 } || request.UseBackgroundMode)
            throw new ArgumentException("An independent tool-free foreground structured request is required.");
        request.Prompt = PlanningPromptPresentation.PresentResponseSchema(request.Prompt, request.StructuredOutputSchema);
        request.ClientRequestId = null;
        request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }
    internal static void PresentProbeIntentDeclarationFields(LLMRequest request, string run, string nonce)
    {
        if (request.StructuredOutputSchema?["title"]?.ToString() != "construction_intent" ||
            request.Tools is { Count: > 0 } || request.UseBackgroundMode)
            throw new ArgumentException("An exact tool-free foreground intent request is required.");
        request.Prompt = PlanningPromptPresentation.PresentIntentDeclarationFields(request.Prompt);
        request.ClientRequestId = null;
        request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }
    internal static void PresentProbeBusinessDependencyFields(LLMRequest request, string run, string nonce)
    {
        if (request.StructuredOutputSchema?["title"]?.ToString() != "construction_select" ||
            request.Tools is { Count: > 0 } || request.UseBackgroundMode)
            throw new ArgumentException("An exact tool-free foreground business-selection request is required.");
        request.Prompt = PlanningPromptPresentation.PresentBusinessDependencyFields(request.Prompt);
        request.ClientRequestId = null;
        request.ClientRequestId = run + ":probe:" + nonce + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }
    private static async Task ExportInspectionAsync(HttpClient client, string id, string root, string run)
    {
        var output = Path.Combine(root, "artifacts/live-server-20261106", run); Directory.CreateDirectory(output);
        var summary = await client.GetFromJsonAsync<JsonObject>("/api/planning/" + id + "/inspection") ?? throw new InvalidOperationException("No inspection metadata.");
        await File.WriteAllTextAsync(Path.Combine(output, "protected-inspection.json"), summary.ToJsonString());
        foreach (var call in (summary["calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = call["key"]!.ToString();
            using var response = await client.GetAsync("/api/planning/" + id + "/calls/export?key=" + Uri.EscapeDataString(key), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var destination = File.Create(Path.Combine(output, "exchange-" + PlanningGraphCompiler.Fingerprint(key) + ".json"));
            await response.Content.CopyToAsync(destination);
        }
    }

    private static void RequireSameProductionSource(string original)
    {
        if (original.Length != 40 || !original.All(char.IsAsciiHexDigit) ||
            SchemaPortabilityCampaign.Git("diff", "--name-only", original, "HEAD", "--", "src").Length != 0)
            throw new PlanningConflictException("Retained execution requires unchanged production source and the original frozen tools.");
    }

    private static async Task RequireNotCancelledAsync(WebApplication app, PlanningSession state, CancellationToken ct)
    {
        if (await app.Services.GetRequiredService<GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore>().GetAsync(
            "agent-planning-cancellations-v1", state.Request.TenantId, state.Request.SessionId, EfPlanningSessionStore.Author, ct) is not null)
            throw new PlanningConflictException("Cancellation is pending; artifact revalidation or execution is forbidden.");
    }

    private static async Task RevalidateRetainedArtifactAsync(WebApplication app, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model,
        PlanningSession current, JsonObject report, string[] args)
    {
        var expectedRevision = long.Parse(SchemaPortabilityCampaign.RequiredOption(args, "--expected-revision"), System.Globalization.CultureInfo.InvariantCulture);
        var expectedHash = SchemaPortabilityCampaign.RequiredOption(args, "--artifact-hash");
        var reviewed = report["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)
            ?? throw new PlanningConflictException("No retained review-ready artifact.");
        var scenario = report["scenario"]!.ToString();
        if (scenario is not ("amazon" or "code")) throw new PlanningConflictException("This recovery requires a retained live MCP artifact.");
        await using var lease = await PlanningSessionLease.TryAcquireAsync(app.Services.GetRequiredService<IDbContextFactory<PlanningDbContext>>(),
            current.Request.TenantId, current.Request.SessionId, CancellationToken.None) ?? throw new PlanningConflictException("The retained session has another active owner.");
        var store = app.Services.GetRequiredService<IPlanningSessionStore>();
        current = await store.LoadAsync(current.Request.TenantId, current.Request.SessionId, CancellationToken.None)
            ?? throw new PlanningConflictException("The retained session disappeared.");
        await RequireNotCancelledAsync(app, current, CancellationToken.None);
        var configurations = LiveWorkflowEvaluation.Configuration(model.McpServers, scenario, null, "Release", report["working_directory"]!.ToString(), report["browser_observation_policy"]?.ToString());
        if (!JsonNode.DeepEquals(LiveWorkflowEvaluation.CaptureMcpBuilds(configurations, report["source"]!.ToString()), report["mcp_builds"]))
            throw new PlanningConflictException("The original selected tool binaries changed; exact revalidation is forbidden.");
        await using var metadata = new DelegatedPlanningDiscovery(new ConfiguredMcpClientFactory(configurations), scenario,
            DelegatedDiscoveryPolicyHash());
        var previousRevision = current.Revision;
        var runtime = ServerLiveArtifactValidation.Runtime(metadata, current.Request.Policy, async (state, ct) =>
        {
            await RequireNotCancelledAsync(app, state, ct);
            if (state.Revision <= previousRevision) state.Revision = previousRevision + 1;
            if (!await store.TrySaveAsync(state, previousRevision, ct)) throw new PlanningConflictException("The revalidated artifact changed concurrently.");
            previousRevision = state.Revision;
        });
        var restored = await ServerLiveArtifactValidation.RevalidateAsync(current, reviewed, expectedRevision, expectedHash, runtime, CancellationToken.None);
        report["approval_validation_recovery"] = new JsonObject
        {
            ["harness_source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"),
            ["harness_assembly"] = AssemblyEvidence(System.Reflection.Assembly.GetEntryAssembly()!),
            ["reviewed_session"] = JsonSerializer.SerializeToNode(reviewed, PlanningJsonContext.Default.PlanningSession),
            ["failed_session"] = JsonSerializer.SerializeToNode(current, PlanningJsonContext.Default.PlanningSession),
            ["restored_revision"] = restored.Revision, ["artifact_hash"] = restored.ComputeArtifactHash(),
            ["revalidated_at_utc"] = DateTimeOffset.UtcNow,
            ["model_calls_unchanged"] = restored.ModelCalls == current.ModelCalls, ["provider_dispatches"] = 0,
            ["execution_started"] = false
        };
        report["session"] = JsonSerializer.SerializeToNode(restored, PlanningJsonContext.Default.PlanningSession);
        report["result"]!["status"] = restored.Status;
        await campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report);
    }

    private static async Task<bool> ExecuteReviewedLocalAsync(WebApplication app, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, PlanningSession state,
        JsonObject report, string root, string workspace)
    {
        if (report["execution_started"] is not null) throw new PlanningConflictException("An execution-start marker forbids another execution.");
        if (await campaign.HasUncertainRequestAsync(CancellationToken.None)) throw BenchmarkCampaign.AdmissionFailure("campaign_uncertain_request_requires_recovery");
        var scenario = report["scenario"]!.ToString();
        if (scenario is not ("local" or "collections" or "amazon" or "code" or ReviewBoundaryQualification.Scenario)) throw new PlanningConflictException("Unknown reviewed execution scenario.");
        var output = Path.Combine(root, "artifacts/live-server-20261106", report["run"]!.ToString());
        Console.WriteLine(new JsonObject { ["awaiting_delegated_review"] = true, ["session_id"] = state.Request.SessionId,
            ["revision"] = state.Revision, ["artifact_hash"] = state.ComputeArtifactHash() }.ToJsonString());
        // Review files acknowledge the already generated immutable artifact. They cannot edit it.
        var authorizationPath = Path.Combine(output, "authorization.json"); var reviewPath = Path.Combine(output, "review.json");
        if (!await ServerLiveReviewWait.WaitAsync(state,
            () => app.Services.GetRequiredService<PlanningSessionService>().GetAsync(state.Request.SessionId, CancellationToken.None),
            () => File.Exists(authorizationPath) || File.Exists(reviewPath),
            () => File.Exists(authorizationPath) && File.Exists(reviewPath),
            () => RequireNotCancelledAsync(app, state, CancellationToken.None), app.Lifetime.ApplicationStopping)) return false;
        await using var lease = await PlanningSessionLease.TryAcquireAsync(app.Services.GetRequiredService<IDbContextFactory<PlanningDbContext>>(),
            state.Request.TenantId, state.Request.SessionId, CancellationToken.None) ?? throw new PlanningConflictException("The reviewed session has another active owner.");
        var latest = await app.Services.GetRequiredService<IPlanningSessionStore>().LoadAsync(state.Request.TenantId, state.Request.SessionId, CancellationToken.None);
        if (latest?.Revision != state.Revision || latest.Status != PlanningStatus.FinalReview || latest.ComputeArtifactHash() != state.ComputeArtifactHash())
            throw new PlanningConflictException("The review-ready session changed while awaiting delegated review.");
        await RequireNotCancelledAsync(app, state, CancellationToken.None);
        var authorization = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(output, "authorization.json")), DelegatedTestJsonContext.Default.DelegatedTestAuthorization)!;
        var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(output, "review.json")), DelegatedTestJsonContext.Default.DelegatedTestReviewDocument)!;
        var authority = new DelegatedTestReviewAuthority(authorization, document, campaign.Id, state.Request.SessionId, scenario is "local" or "collections" or ReviewBoundaryQualification.Scenario ? "synthetic" : scenario, workspace, report["source"]!.ToString(),
            scenario is "amazon" or "code" ? report["workspace_relative"]!.ToString() : null, report["browser_observation_policy"]?.ToString());
        authority.Validate(state); await authority.PinAsync(campaign, CancellationToken.None);
        var sourceRoot = report["working_directory"]!.ToString();
        var metadataConfigurations = scenario is "amazon" or "code"
            ? LiveWorkflowEvaluation.Configuration(model.McpServers, scenario, null, "Release", sourceRoot, report["browser_observation_policy"]?.ToString()) : null;
        if (metadataConfigurations is not null && !JsonNode.DeepEquals(LiveWorkflowEvaluation.CaptureMcpBuilds(metadataConfigurations, report["source"]!.ToString()), report["mcp_builds"]))
            throw new PlanningConflictException("The frozen selected tools changed before approval.");
        await using var metadata = metadataConfigurations is null ? null : new DelegatedPlanningDiscovery(
            new ConfiguredMcpClientFactory(metadataConfigurations), scenario, DelegatedDiscoveryPolicyHash());
        var previousRevision = state.Revision;
        var runtime = ServerLiveArtifactValidation.Runtime(metadata, state.Request.Policy, async (s, ct) => {
            await RequireNotCancelledAsync(app, s, ct);
            if (s.Revision <= previousRevision) s.Revision = previousRevision + 1;
            if (!await app.Services.GetRequiredService<IPlanningSessionStore>().TrySaveAsync(s, previousRevision, ct)) throw new PlanningConflictException("The reviewed Server artifact changed.");
            previousRevision = s.Revision;
        });
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, authority.Command(), new DelegatedTestReviewAuthority.Runtime(runtime, authority), CancellationToken.None);
        PlanningArtifactApproval.Verify(state, authority.Receipt);
        if (state.Status != PlanningStatus.Approved) throw new PlanningConflictException("Artifact-bound delegated approval failed.");
        report["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
        report["execution_artifact_hash"] = state.ComputeArtifactHash();
        report["review_origin"] = "delegated_test_reviewed";
        await campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report);
        await RequireNotCancelledAsync(app, state, CancellationToken.None);
        await lease.DisposeAsync(); // The execution journal owns effects after the approval transition.
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        if (scenario is "local" or "collections" or ReviewBoundaryQualification.Scenario)
        {
            report["execution_started"] = DateTimeOffset.UtcNow;
            await campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report);
            var variants = new JsonArray();
            foreach (var variant in scenario == "local" ? new[] { "nominal" } : new[] { "nominal", "alternate" })
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var engine = new WorkflowEngine { HumanInputProvider = new DelegatedTestReviewAuthority.Input(campaign, state.Request.SessionId, authority) };
                var inputs = scenario == "local" ? new JsonObject() : scenario == ReviewBoundaryQualification.Scenario
                    ? ReviewBoundaryQualification.Inputs(variant) : PlanningBenchmarkCases.Inputs("collections", variant);
                var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], inputs, cancellation.Token);
                var correct = scenario == "local" ? NativeOutputOracle(result.Success, result.Outputs) :
                    scenario == ReviewBoundaryQualification.Scenario ? ReviewBoundaryQualification.Outputs(result.Success, result.Outputs, variant) &&
                        ReviewBoundaryQualification.Artifact(state.Plan, state.Catalog).Count == 0 :
                    new PlanningBenchmarkCases.Environment("collections", variant).Verify(result) &&
                    SyntheticArtifactOracle.Validate("collections", state.Plan, state.Catalog).Count == 0;
                variants.Add(new JsonObject { ["variant"] = variant, ["success"] = result.Success, ["outputs"] = result.Outputs?.DeepClone(), ["oracle_passed"] = correct });
            }
            var boundary = scenario == ReviewBoundaryQualification.Scenario ? ReviewBoundaryQualification.Evidence(state) : null;
            var passed = variants.All(v => v!["oracle_passed"]!.GetValue<bool>()) && (boundary is null || boundary["observed"]!.GetValue<bool>());
            if (boundary is not null) report["unchanged_plan_review"] = boundary;
            report["execution"] = new JsonObject { ["success"] = passed, ["variants"] = variants };
            report["oracle"] = new JsonObject { ["version"] = scenario == "local" ? "native-result42-v1" : scenario == ReviewBoundaryQualification.Scenario ? ReviewBoundaryQualification.Version : "collections-native-variants-v1", ["independent"] = true, ["passed"] = passed };
        }
        else
        {
            model.CaptureTokenPolicy(state.Request);
            await using var proxy = await CampaignInferenceProxy.StartAsync(model, state.Request.SessionId,
                report["sdk_chat_tools_reasoning_effort"]?.GetValue<string>(), report["sdk_wire_api"]?.GetValue<string>());
            var configurations = LiveWorkflowEvaluation.Configuration(model.McpServers, scenario, proxy.Endpoint, "Release", sourceRoot, report["browser_observation_policy"]?.ToString());
            if (scenario == "code") CampaignInferenceProxy.ConfigureWireApi(configurations["GnOuGo.GithubCopilot.Mcp"], model.Provider, report["sdk_wire_api"]?.GetValue<string>());
            var builds = LiveWorkflowEvaluation.CaptureMcpBuilds(configurations, report["source"]!.ToString());
            if (!JsonNode.DeepEquals(builds, report["mcp_builds"])) throw new PlanningConflictException("The frozen execution tools changed after generation.");
            var human = new DelegatedTestReviewAuthority.Input(campaign, state.Request.SessionId, authority);
            await using var transport = new ConfiguredMcpClientFactory(configurations, human, model.Provider, model.Model);
            report["events"] = new JsonArray();
            async Task Save() => await campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report);
            var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, async e => {
                report["events"]!.AsArray().Add(e);
                if (scenario == "code") await LiveCodeEvidence.ObserveCheckoutAsync(workspace, report);
                await Save();
            }, authority, scenario);
            foreach (var server in configurations.Keys) _ = await (await observed.GetClientAsync(server, CancellationToken.None)).ListToolsAsync(CancellationToken.None);
            if (scenario == "code" && !proxy.Ready) throw new PlanningConflictException("SDK inference interception is not attested; execution is forbidden.");
            var options = app.Services.GetRequiredService<LLMRuntimeOptionsStore>().Current;
            var engine = new WorkflowEngine { McpClientFactory = observed, HumanInputProvider = human,
                LLMClient = new LiveWorkflowEvaluation.ExecutionModel(model, state.Request.SessionId), LLMCapabilities = model,
                ModelUsageCostEstimator = new ModelMetadataUsageCostEstimator(options), RunStore = LiveWorkflowEvaluation.ExecutionStore(workspace),
                LLMUsageBudget = LiveWorkflowEvaluation.ModelBudget("execute", state.Request),
                LlmDefaults = new() { Provider = model.Provider, Model = model.Model },
                Limits = new() { TenantId = "benchmark", RunId = state.Request.SessionId, AgentId = campaign.Id + "-" + scenario, MaxMappingInputTokens = 96000 } };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            report["execution_started"] = DateTimeOffset.UtcNow; await Save();
            proxy.ExecutionEnabled = true;
            try
            {
                var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], report["inputs"]!.DeepClone(), cancellation.Token);
                report["execution"] = new JsonObject { ["success"] = result.Success, ["outputs"] = result.Outputs?.DeepClone(), ["error"] = result.Error is null ? null : JsonSerializer.SerializeToNode(result.Error) };
            }
            finally { proxy.ExecutionEnabled = false; await Save(); }
            report["oracle"] = await LiveWorkflowOracles.VerifyAsync(scenario, workspace, report, observed, observed.ProbeBrowserClosureAsync);
        }
        var accepted = report["oracle"]!["passed"]!.GetValue<bool>();
        report["result"]!["execution_correct"] = accepted; report["result"]!["execution_status"] = "completed";
        await campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report);
        Console.WriteLine(new JsonObject { ["session_id"] = state.Request.SessionId, ["execution_success"] = report["execution"]!["success"]!.DeepClone(), ["oracle_passed"] = accepted }.ToJsonString());
        if (!accepted) Environment.ExitCode = 1;
        return true;
    }

    internal static bool NativeOutputOracle(bool success, JsonNode? result) => success && result is JsonObject outputs && outputs.Count == 1 &&
        outputs["result"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
        decimal.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == 42m;

    internal static async Task VerifyFrontendAsync(WebApplication app, string root, string url, string run, CancellationToken ct)
    {
        using var process = StartObserver(app, root, url, null, run);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(45), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        if (process.ExitCode != 0) throw new PlanningConflictException(
            "Agent.Server frontend readiness failed before session creation or provider admission. Build ClientApp (pnpm run build), verify /ui/app.js and /ui/app.css, and inspect the browser readiness artifacts.",
            "frontend_not_ready");
    }
    private static JsonArray SafeLaunchArguments()
    {
        var command = Environment.GetCommandLineArgs(); var result = new JsonArray();
        var safeValue = false;
        for (var i = 0; i < command.Length; i++)
        {
            var value = command[i];
            if (i == 0) result.Add(value);
            else if (value.StartsWith("--", StringComparison.Ordinal))
            {
                safeValue = value is "--schema-portability" or "--run" or "--case" or "--model" or "--campaign-configuration" or "--workspace" or "--campaign" or "--retained-external-reservations-eur" or "--server-phase-policy" or "--financial-forecast-mode" or "--sdk-chat-tools-reasoning" or "--sdk-wire-api" or "--amazon-scope-approval";
                result.Add(safeValue ? value : "[unrecorded option]");
            }
            else { result.Add(safeValue ? value : "[withheld]"); safeValue = false; }
        }
        return result;
    }
    private static JsonObject AssemblyEvidence(System.Reflection.Assembly assembly) => new() {
        ["path"] = assembly.Location, ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))) };

    private static Process StartObserver(WebApplication app, string root, string url, string? id, string run)
    {
        var module = Environment.GetEnvironmentVariable("PLAYWRIGHT_MODULE_PATH") ?? Path.Combine(AppContext.BaseDirectory, "tools/GnOuGo.Browser.Mcp/.playwright/package/index.mjs");
        if (!File.Exists(module)) throw new PlanningConflictException("The staged Playwright module is missing; build bundled browser tools before live qualification.", "frontend_not_ready");
        var output = Path.Combine(root, "artifacts/live-server-20261106", run); Directory.CreateDirectory(output);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/observe-live-planning.mjs")); start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        var process = Process.Start(start)!;
        var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(new Uri(url));
        string Redact(string value) => value.Replace(launch.AbsoluteUri, "[protected bootstrap]", StringComparison.Ordinal)
            .Replace(launch.Fragment[1..], "[redacted]", StringComparison.Ordinal);
        // Drain both pipes; authority-bearing browser input is never logged, including on child failure.
        _ = Task.Run(async () => await File.WriteAllTextAsync(Path.Combine(output, id is null ? "readiness.stdout.log" : "observer.stdout.log"), Redact(await process.StandardOutput.ReadToEndAsync())));
        _ = Task.Run(async () => await File.WriteAllTextAsync(Path.Combine(output, id is null ? "readiness.stderr.log" : "observer.stderr.log"), Redact(await process.StandardError.ReadToEndAsync())));
        process.StandardInput.Write(JsonSerializer.Serialize(new { launch = launch.AbsoluteUri, url = url.TrimEnd('/'), session = id, output, mode = id is null ? "readiness" : "observe" }));
        process.StandardInput.Close(); return process;
    }
    private sealed class CampaignClient(KeyVaultBenchmarkModel model, IPlanningSessionStore sessions,
        LlmTraceCapture capture, LLMRuntimeOptionsStore options) : ILLMClient
    {
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var id = request.ClientRequestId?.Split(':')[0] ?? throw new PlanningConflictException("A captured Server request identity is required.");
            var state = await sessions.LoadAsync("benchmark", id, ct) ?? throw new PlanningConflictException("No owned live Server session.");
            return await capture.CallAsync(request, options.Current, (r, token) => model.CallServerPlanningAsync(r, state, token), ct);
        }
    }
    internal static string DelegatedDiscoveryPolicyHash() =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(DelegatedTestReviewAuthority).Assembly.Location)));

    internal sealed class DelegatedPlanningDiscovery(IMcpClientFactory inner, string scenario, string policyHash,
        Func<CancellationToken, Task<IMcpClientFactory>>? hostLookupFactory = null)
        : IMcpClientFactory, IMcpSourceConfigurationFingerprint, IAsyncDisposable
    {
        private readonly LiveWorkflowEvaluation.ObservedMcp _filtered = new(inner, _ => Task.CompletedTask, discoveryScenario: scenario);
        private const string AgentServer = "GnOuGo.Agent.Mcp";
        public IReadOnlyList<McpServerMetadata> ServerMetadata => _filtered.ServerMetadata.Where(s => s.Name != AgentServer).ToArray();
        public string SourceConfigurationFingerprint(string serverName) => inner is IMcpSourceConfigurationFingerprint source
            ? PlanningGraphCompiler.Fingerprint(source.SourceConfigurationFingerprint(serverName) + "\ndelegated-planning-discovery-v1\n" + scenario + "\n" + policyHash)
            : throw new InvalidOperationException("Delegated discovery requires an exact configured source fingerprint.");
        public async Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
        {
            if (serverName != AgentServer || hostLookupFactory is null)
                return new ReadOnlySession(serverName == AgentServer ? await inner.GetClientAsync(serverName, ct) : await _filtered.GetClientAsync(serverName, ct));
            var factory = await hostLookupFactory(ct);
            if (factory is not IAsyncDisposable owner) throw new InvalidOperationException("The host lookup transport must have an owned lifetime.");
            try { return new ReadOnlySession(await factory.GetClientAsync(serverName, ct), owner); }
            catch { await owner.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => inner is IAsyncDisposable owned ? owned.DisposeAsync() : ValueTask.CompletedTask;
        private sealed class ReadOnlySession(IMcpSession inner, IAsyncDisposable? owner = null) : IMcpSession
        {
            public string ServerName => inner.ServerName;
            public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => ServerName == AgentServer
                ? Task.FromResult<IReadOnlyList<McpToolInfo>>([]) : inner.ListToolsAsync(ct);
            public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => ServerName == AgentServer
                ? Task.FromResult<IReadOnlyList<McpResourceInfo>>([]) : inner.ListResourcesAsync(ct);
            public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
            public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
                => throw new InvalidOperationException("Delegated planning permits metadata discovery only.");
            public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
                => ServerName == AgentServer && toolName == "agent_get_by_name" && arguments is JsonObject { Count: 1 } value &&
                   value["name"] is JsonValue name && name.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                    ? inner.CallToolAsync(toolName, arguments, ct)
                    : throw new InvalidOperationException("Business tools require the later artifact-bound delegated execution; only the host agent-name lookup is permitted before planning.");
            public ValueTask DisposeAsync() => owner is null ? inner.DisposeAsync() : owner.DisposeAsync();
        }
    }
    private sealed class RetainedDiscoveryGuard(BenchmarkCampaign campaign) : IPlanningDiscoveryRecoveryGuard
    {
        public Task VerifyAsync(PlanningSession session, CancellationToken ct) => campaign.RequireRunNotRetainedAsync(session.Request.SessionId, ct);
    }

}
