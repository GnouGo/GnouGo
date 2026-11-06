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

// Explicit live harness: actual Agent.Server, actual configured provider, existing campaign ledger.
// No credentials or bootstrap links are exported. The campaign lease is held by the caller.
internal static class ServerLiveDiagnostics
{
    internal static async Task RunAsync(string[] args, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, string workspace)
    {
        var run = SchemaPortabilityCampaign.RequiredOption(args, "--run");
        var planning = args.ElementAtOrDefault(1) == "server-live-plan";
        var inspectionOnly = args.ElementAtOrDefault(1) == "server-live-inspect";
        var executionOnly = args.ElementAtOrDefault(1) == "server-live-execute";
        var retained = inspectionOnly || executionOnly;
        var mcpReadiness = args.ElementAtOrDefault(1) == "server-live-mcp-readiness";
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
        var phaseFile = SchemaPortabilityCampaign.Option(args, "--server-phase-policy");
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
                services.AddSingleton<SecureWorkflowRuntimeFactory>(sp => new(sp.GetRequiredService<LLMRuntimeOptionsStore>(),
                    sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(), sp.GetRequiredService<ILoggerFactory>(),
                    llmClientOverride: new CampaignClient(model, sp.GetRequiredService<IPlanningSessionStore>(),
                        sp.GetRequiredService<LlmTraceCapture>(), sp.GetRequiredService<LLMRuntimeOptionsStore>()),
                    defaults: sp.GetRequiredService<LlmDefaultsResolver>()));
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
            ["budget_mode"] = "limited", ["retained_external_reservations_eur"] = model.RetainedExternalReservationsEur, ["started_utc"] = DateTimeOffset.UtcNow, ["phase"] = planning ? "server_planning" : "provider_probe" };
        if (phaseFile is not null) report["phase_policy_sha256"] = PlanningGraphCompiler.Fingerprint(await File.ReadAllTextAsync(phaseFile));
        if (!retained) await campaign.SaveAsync("planning-evaluation-runs", run, report);
        var invoked = 0;
        if (!planning && !retained && !mcpReadiness) app.MapPost("/api/planning/live-provider-probe", async (HttpContext context, LocalOperatorAccess access, IAntiforgery antiforgery, LlmTraceCapture capture) =>
        {
            if (!await access.CanInspectAsync()) return Results.Forbid();
            await antiforgery.ValidateRequestAsync(context);
            if (Interlocked.CompareExchange(ref invoked, 1, 0) != 0) return Results.Conflict("A fresh independent run is required; this probe cannot replay.");
            var nonce = Guid.NewGuid().ToString("N");
            var request = new LLMRequest { Provider = model.Provider, Model = model.Model, Reasoning = "medium", MaxTokens = 1024,
                UseBackgroundMode = false, DisableTransportRetries = true,
                Prompt = "Return the requested JSON object. Set ok to true and nonce to " + nonce + ". No tools or external actions.",
                StructuredOutputSchema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" },
                        ["nonce"] = new JsonObject { ["type"] = "string", ["const"] = nonce } },
                    ["required"] = new JsonArray("ok", "nonce") } };
            request.ClientRequestId = run + ":probe:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            report["request_id"] = request.ClientRequestId; report["requested_output_tokens"] = request.MaxTokens;
            await campaign.SaveAsync("planning-evaluation-runs", run, report);
            using var activity = new Activity("live.provider.structured_output_probe").Start();
            try
            {
                var result = await capture.CallAsync(request, options, (r, ct) => model.CallAsync(r, ct), context.RequestAborted);
                var received = result.Json ?? (result.Text is null ? null : JsonNode.Parse(result.Text));
                report["passed"] = result.CompletionStatus != "output_limit" && received?["ok"]?.GetValue<bool>() == true && received?["nonce"]?.ToString() == nonce;
                report["completion_status"] = result.CompletionStatus; report["usage"] = result.Usage?.DeepClone();
            }
            catch (Exception error)
            {
                report["passed"] = false; report["exception_type"] = error.GetType().Name;
                if (error is LLMClientException failure)
                {
                    report["failure_kind"] = failure.Kind.ToString(); report["http_status"] = failure.StatusCode;
                    report["safe_provider_code"] = failure.SafeProviderCode; report["provider_stage"] = failure.Stage.ToString();
                }
                // Exception messages and provider bodies remain in protected encrypted evidence.
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
            if (!retained)
            {
                using var limited = await client.PostAsJsonAsync("/api/application-llm-budget", new JsonObject { ["mode"] = "limited" }); limited.EnsureSuccessStatusCode();
            }
            if (retained)
            {
                var saved = await campaign.LoadAsync("planning-evaluation-runs", run) ?? throw new PlanningConflictException("No retained live run.");
                await ExportInspectionAsync(client, saved["session_id"]!.ToString(), root, run);
                if (executionOnly)
                {
                    if (saved["execution_started"] is not null) throw new PlanningConflictException("Execution was already started; uncertain effects cannot be replayed.");
                    var state = await app.Services.GetRequiredService<PlanningSessionService>().GetAsync(saved["session_id"]!.ToString(), CancellationToken.None)
                        ?? throw new PlanningConflictException("No retained Server session.");
                    if (state.Status != PlanningStatus.FinalReview || state.ComputeArtifactHash() != saved["session"]?.Deserialize(PlanningJsonContext.Default.PlanningSession)?.ComputeArtifactHash())
                        throw new PlanningConflictException("Only the exact unexecuted review-ready artifact may continue.");
                    saved["execution_harness_source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD");
                    model.RetainedExternalReservationsEur = saved["retained_external_reservations_eur"]?.GetValue<decimal>() ?? 0;
                    await ExecuteReviewedLocalAsync(app, campaign, model, state, saved, root, workspace);
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
                    "amazon" => LiveWorkflowEvaluation.AmazonEvaluationPrompt(relative, 3),
                    "code" => LiveWorkflowEvaluation.CodeEvaluationPrompt(relative), _ => throw new ArgumentException("Unknown live scenario.") };
                report["scenario"] = scenario;
                report["workspace_relative"] = relative;
                report["inputs"] = scenario == "amazon" ? new JsonObject { ["query"] = "chaussure geox homme 45" } : scenario == "code"
                    ? new JsonObject { ["pullRequestUrl"] = "https://github.com/AxaFrance/SmartGuide/pull/610", ["reviewText"] = "Review correctness, regressions, security and test coverage of the pinned pull-request diff. Report checks truthfully and keep proposed comments local." } : new JsonObject();
                report["max_products"] = 3;
                if (scenario is "amazon" or "code")
                {
                    report["oracle_version"] = scenario == "amazon" ? LiveWorkflowOracles.AmazonBusinessVersion : LiveWorkflowOracles.CodeBusinessVersion;
                    report["mcp_builds"] = LiveWorkflowEvaluation.CaptureMcpBuilds(LiveWorkflowEvaluation.Configuration(model.McpServers, scenario, "preflight-only", "Release"), report["source"]!.ToString());
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
                        revision = state.Revision; report["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
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
                        if (state.Status == PlanningStatus.FinalReview && args.Contains("--await-delegated-review", StringComparer.Ordinal))
                            await ExecuteReviewedLocalAsync(app, campaign, model, state, report, root, workspace);
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

    private static async Task ExecuteReviewedLocalAsync(WebApplication app, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, PlanningSession state,
        JsonObject report, string root, string workspace)
    {
        if (report["execution_started"] is not null) throw new PlanningConflictException("An execution-start marker forbids another execution.");
        var scenario = report["scenario"]!.ToString();
        if (scenario is not ("local" or "collections" or "amazon" or "code")) throw new PlanningConflictException("Unknown reviewed execution scenario.");
        var output = Path.Combine(root, "artifacts/live-server-20261106", report["run"]!.ToString());
        Console.WriteLine(new JsonObject { ["awaiting_delegated_review"] = true, ["session_id"] = state.Request.SessionId,
            ["revision"] = state.Revision, ["artifact_hash"] = state.ComputeArtifactHash() }.ToJsonString());
        // Review files acknowledge the already generated immutable artifact. They cannot edit it.
        while (!File.Exists(Path.Combine(output, "authorization.json")) || !File.Exists(Path.Combine(output, "review.json"))) await Task.Delay(1000);
        var authorization = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(output, "authorization.json")), DelegatedTestJsonContext.Default.DelegatedTestAuthorization)!;
        var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(output, "review.json")), DelegatedTestJsonContext.Default.DelegatedTestReviewDocument)!;
        var authority = new DelegatedTestReviewAuthority(authorization, document, campaign.Id, state.Request.SessionId, scenario is "local" or "collections" ? "synthetic" : scenario, workspace, report["source"]!.ToString(),
            scenario is "amazon" or "code" ? report["workspace_relative"]!.ToString() : null);
        authority.Validate(state); await authority.PinAsync(campaign, CancellationToken.None);
        var previousRevision = state.Revision;
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { PlanningPolicy = state.Request.Policy }, async (s, ct) => {
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
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        if (scenario is "local" or "collections")
        {
            report["execution_started"] = DateTimeOffset.UtcNow;
            await campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report);
            var variants = new JsonArray();
            foreach (var variant in scenario == "local" ? new[] { "nominal" } : new[] { "nominal", "alternate" })
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var engine = new WorkflowEngine { HumanInputProvider = new DelegatedTestReviewAuthority.Input(campaign, state.Request.SessionId, authority) };
                var inputs = scenario == "local" ? new JsonObject() : PlanningBenchmarkCases.Inputs("collections", variant);
                var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], inputs, cancellation.Token);
                var correct = scenario == "local" ? NativeOutputOracle(result.Success, result.Outputs) :
                    new PlanningBenchmarkCases.Environment("collections", variant).Verify(result) &&
                    SyntheticArtifactOracle.Validate("collections", state.Plan, state.Catalog).Count == 0;
                variants.Add(new JsonObject { ["variant"] = variant, ["success"] = result.Success, ["outputs"] = result.Outputs?.DeepClone(), ["oracle_passed"] = correct });
            }
            var passed = variants.All(v => v!["oracle_passed"]!.GetValue<bool>());
            report["execution"] = new JsonObject { ["success"] = passed, ["variants"] = variants };
            report["oracle"] = new JsonObject { ["version"] = scenario == "local" ? "native-result42-v1" : "collections-native-variants-v1", ["independent"] = true, ["passed"] = passed };
        }
        else
        {
            model.CaptureTokenPolicy(state.Request);
            await using var proxy = await CampaignInferenceProxy.StartAsync(model, state.Request.SessionId);
            var configurations = LiveWorkflowEvaluation.Configuration(model.McpServers, scenario, proxy.Endpoint, "Release");
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
    }

    internal static bool NativeOutputOracle(bool success, JsonNode? result) => success && result is JsonObject outputs && outputs.Count == 1 &&
        outputs["result"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
        decimal.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == 42m;

    private static Process? StartObserver(WebApplication app, string root, string url, string id, string run)
    {
        var module = Path.Combine(root, "src/GnOuGo.Browser.Mcp/bin/Release/net10.0/.playwright/package/index.mjs");
        if (!File.Exists(module)) return null;
        var output = Path.Combine(root, "artifacts/live-server-20261106", run); Directory.CreateDirectory(output);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "scripts/observe-live-planning.mjs")); start.Environment["PLAYWRIGHT_MODULE_PATH"] = module;
        var process = Process.Start(start)!;
        // Drain diagnostics into private artifacts; do not print authority-bearing browser input.
        _ = Task.Run(async () => await File.WriteAllTextAsync(Path.Combine(output, "observer.stdout.log"), await process.StandardOutput.ReadToEndAsync()));
        _ = Task.Run(async () => await File.WriteAllTextAsync(Path.Combine(output, "observer.stderr.log"), await process.StandardError.ReadToEndAsync()));
        var launch = app.Services.GetRequiredService<LocalOperatorBootstrap>().CreateLaunchAddress(new Uri(url));
        process.StandardInput.Write(JsonSerializer.Serialize(new { launch = launch.AbsoluteUri, url = url.TrimEnd('/'), session = id, output }));
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
    private sealed class RetainedDiscoveryGuard(BenchmarkCampaign campaign) : IPlanningDiscoveryRecoveryGuard
    {
        public Task VerifyAsync(PlanningSession session, CancellationToken ct) => campaign.RequireRunNotRetainedAsync(session.Request.SessionId, ct);
    }

}
