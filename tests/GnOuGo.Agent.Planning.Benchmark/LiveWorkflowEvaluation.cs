using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Persistence;

// Explicit live test host. It never changes a saved workflow or host permission
// policy and never connects the GitHub publication server.
internal static class LiveWorkflowEvaluation
{
    internal const string AmazonPrompt = "En entrée accepte un article a rechercher, puis va sur amazon pour rechercher l’article en question, (ex: chaussure geox homme 45) liste les articles, ensuite navigue sur la page de chaque article et extraits dans un fichier Excel: le nom, description et prix que tu sauvegarde sur le disque.";
    internal const string CodePrompt = "A partir d’une url d’une pull request GitHub et d’un texte qui explique ce qu’il faut reviewer, réalise une review automatique. Réalise un unique clone du projet et utilise uniquement ce répertoire. Via GitHub Copilot, installe toutes les dépendances, joue les linters, tests unitaires et tests d’intégration si présents, puis réalise la revue de code demandée. Le feedback doit expliquer acceptation ou refus avec commentaires de diff. Quoi qu’il arrive supprime le répertoire cloné pour nettoyer.";
    internal const string Head = "f2f6bda0e02a6c5c363e88cd6cb5d4eb7c313488", Base = "8f1779dc25c1843e9ce4d4ec76d02e066ae892c4";

    internal static async Task RunAsync(string[] args, string phase, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, string root)
    {
        var scenario = SchemaPortabilityCampaign.Option(args, "--case") ?? "amazon";
        if (scenario is not ("amazon" or "code")) throw new ArgumentException("Choose amazon or code.");
        var label = SchemaPortabilityCampaign.Option(args, "--run") ?? (phase == "readiness" ? "readiness-" : "diagnostic-") + scenario + "-1";
        var cohort = SchemaPortabilityCampaign.Option(args, "--cohort") ?? "final";
        if (cohort.Length > 30 || !cohort.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Invalid cohort.");
        if (label.Length > 80 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid run identity.");
        var key = "run:" + label;
        var retained = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, key);
        if (phase == "inspect-run") { Console.WriteLine(retained?.ToJsonString() ?? "No run."); return; }
        var sourceRevision = SchemaPortabilityCampaign.Git("rev-parse", "HEAD");
        var continuing = phase is "execute" or "revise";
        if (continuing && retained?["source"]?.ToString() != sourceRevision)
            throw new InvalidOperationException("Continuation must use the retained planning build.");
        var manifest = label.StartsWith(cohort + "-", StringComparison.Ordinal)
            ? await LiveCampaignEvidence.PinAsync(campaign, sourceRevision, model.Provider, model.Model, model.ConfigurationFingerprint, cohort) : null;
        await using var proxy = await CampaignInferenceProxy.StartAsync(model, label);
        var configurations = Configuration(model.McpServers, scenario, proxy.Endpoint);
        var human = new ConsoleHuman(campaign, label);
        await using var transport = new ConfiguredMcpClientFactory(configurations, human, model.Provider, model.Model);
        var run = continuing ? retained!.DeepClone().AsObject() : new JsonObject { ["scenario"] = scenario, ["label"] = label, ["source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"),
            ["phase"] = manifest is not null ? "final" : "diagnostic", ["events"] = new JsonArray(), ["manifest"] = manifest };
        var observed = new ObservedMcp(transport, async e =>
        {
            run["events"]!.AsArray().Add(e);
            if (scenario == "code" && run["workspace_relative"] is not null) await LiveCodeEvidence.ObserveCheckoutAsync(root, run);
            await Save();
        });
        var measured = new ExecutionModel(model, label);
        var mappingTelemetry = new MappingLiveEvaluation.MappingTelemetry();
        var engine = new WorkflowEngine { McpClientFactory = observed, LLMClient = phase == "execute" ? measured : model,
            Telemetry = mappingTelemetry,
            RunStore = EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: root),
            Limits = new() { TenantId = "benchmark", RunId = label, AgentId = campaign.Id + "-" + scenario, AgentName = "Live evaluation " + scenario, MaxMappingInputTokens = 96000 },
            HumanInputProvider = human, LlmDefaults = new() { Model = model.Model, Provider = model.Provider } };
        if (scenario == "code") engine.WithCopilotRunners(configurations.Keys.Where(k => k.Contains("GithubCopilot", StringComparison.Ordinal)).Select(k => new KeyValuePair<string, string>("coding", k)));
        JsonObject? revisionSnapshot = null;
        var runtime = new WorkflowPlanningRuntime(engine, async (s, _) =>
        {
            if (revisionSnapshot is not null)
            {
                run["planning_revisions"] ??= new JsonArray();
                run["planning_revisions"]!.AsArray().Add(revisionSnapshot); revisionSnapshot = null;
                run["artifact_hash"] = null; run["failure"] = null;
            }
            run["session"] = JsonSerializer.SerializeToNode(s, PlanningJsonContext.Default.PlanningSession);
            await Save(); Console.WriteLine($"{label}: revision={s.Revision}, status={s.Status}, calls={s.ModelCalls}, repairs={s.ReplanAttempts}");
        });
        if (phase == "execute")
        {
            var reviewPath = SchemaPortabilityCampaign.Option(args, "--review-command")
                ?? throw new ArgumentException("Review the accepted requirements and actual TaskPlan, then supply an explicit approval with --review-command <file>.");
            var command = JsonSerializer.Deserialize(await File.ReadAllTextAsync(reviewPath), PlanningJsonContext.Default.PlanningCommand)
                ?? throw new ArgumentException("The review command must be an explicit approval.");
            var session = await ApproveAsync(run, command, runtime, CancellationToken.None);
            if (scenario == "code" && !proxy.Ready) throw new InvalidOperationException("SDK inference interception is not attested.");
            proxy.ExecutionEnabled = true;
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!));
            run["execution_started"] = DateTimeOffset.UtcNow.ToString("O"); await Save();
            var timer = Stopwatch.StartNew(); using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], run["inputs"]!.DeepClone(), deadline.Token);
                run["execution"] = new JsonObject { ["success"] = result.Success, ["outputs"] = result.Outputs?.DeepClone(),
                    ["error"] = result.Error is null ? null : JsonSerializer.SerializeToNode(result.Error) };
            }
            catch (Exception ex) { run["execution"] = new JsonObject { ["success"] = false, ["exception"] = ex.ToString() }; }
            finally { proxy.ExecutionEnabled = false; run["execution_ms"] = timer.ElapsedMilliseconds;
                run["mapping_telemetry"] = mappingTelemetry.Mappings; await Save(); }
            var oracle = await LiveWorkflowOracles.VerifyAsync(scenario, root, run, observed);
            run["oracle"] = oracle;
            run["result"]!["execution_success"] = run["execution"]!["success"]!.DeepClone();
            run["result"]!["execution_oracle"] = oracle["passed"]!.DeepClone();
            run["result"]!["execution_status"] = "completed";
            run["result"]!["execution_ms"] = timer.ElapsedMilliseconds;
            run["result"]!["total_ms"] = run["result"]!["planning_ms"]!.GetValue<long>() + timer.ElapsedMilliseconds;
            run["result"]!["accounting"] = await LiveCampaignEvidence.AccountingAsync(campaign, label);
            await Save(); Console.WriteLine(run["result"]!.ToJsonString()); Console.WriteLine(oracle.ToJsonString()); return;
        }
        if (phase == "readiness")
        {
            foreach (var server in configurations.Keys)
            {
                var session = await observed.GetClientAsync(server, CancellationToken.None);
                var tools = await session.ListToolsAsync(CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new { server, tools = tools.Select(t => new { t.Name, t.EffectKind }) }));
            }
            var sources = await runtime.Capabilities.ListSourcesAsync(CancellationToken.None);
            foreach (var source in sources.Where(s => s.Id.StartsWith("runner_", StringComparison.Ordinal)))
            {
                var page = await runtime.Capabilities.ListAsync(source.Id, null, CancellationToken.None);
                foreach (var c in page.Capabilities) Console.WriteLine(JsonSerializer.SerializeToNode(c, PlanningJsonContext.Default.CapabilitySummary));
            }
            if (scenario == "amazon") await LiveExecutionReadiness.VerifyAsync(observed, root);
            return;
        }
        if (retained is not null && phase != "revise") throw new InvalidOperationException("Run already retained; inspect it instead of overwriting.");
        var relative = "workflows/" + campaign.Id + "/" + label;
        var prompt = scenario == "amazon"
            ? AmazonPrompt + "\nContraintes de cette évaluation autorisée: utilise Amazon.fr, au maximum les trois premiers produits; une seule entrée publique nommée query. Sauvegarde le classeur à " + relative + "/products.xlsx. Ferme le navigateur même en cas d’échec. CAPTCHA, prix ou données absents restent explicites, jamais inventés."
            : CodePrompt + "\nÉvaluation autorisée: deux entrées publiques nommées pullRequestUrl et reviewText. Cible SmartGuide PR #610, https://github.com/AxaFrance/SmartGuide/pull/610 ; head " + Head + " et base " + Base + ". Destination fixe du clone: " + relative + "/repository. Utilise les toolchains et checks déclarés par ce dépôt. Tous les feedbacks, décisions et commentaires de diff restent LOCAUX: aucune publication GitHub. Sauvegarde les preuves exactes de commandes et leurs codes de sortie dans " + relative + "/review.json, puis nettoie le clone même en cas d’échec. Une capacité absente ou un test échoué reste explicite. Aucune extension de permission/sandbox. Le rapport review.json contient decision (approve ou request_changes), findings (liste), et les commandes/codes de sortie observés. Vérifie explicitement node --version, pnpm --version et python --version avant les checks. Ne présente pas une capacité manquante ou un prérequis indisponible comme un test réussi.";
        if (phase != "revise")
        {
            run["prompt"] = prompt; run["prompt_hash"] = PlanningGraphCompiler.Fingerprint(prompt);
            run["oracle_version"] = "real-workflows-v1"; run["workspace_relative"] = relative;
            run["inputs"] = scenario == "amazon" ? new JsonObject { ["query"] = "chaussure geox homme 45" }
                : new JsonObject { ["pullRequestUrl"] = "https://github.com/AxaFrance/SmartGuide/pull/610", ["reviewText"] = "Review correctness, regressions, security and test coverage of the pinned pull-request diff. Report checks truthfully and keep proposed comments local." };
            await Save();
        }
        var priorMilliseconds = phase == "revise" ? run["result"]!["planning_ms"]!.GetValue<long>() : 0;
        var priorReads = phase == "revise" ? run["result"]!["discovery_reads"]!.GetValue<int>() : 0;
        var remainingTime = TimeSpan.FromMinutes(30) - TimeSpan.FromMilliseconds(priorMilliseconds);
        if (remainingTime <= TimeSpan.Zero) throw new InvalidOperationException("Planning time is exhausted; revision cannot reset it.");
        var clock = Stopwatch.StartNew(); using var timeout = new CancellationTokenSource(remainingTime);
        var state = phase == "revise" ? run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)! : new PlanningSession { Request = new() { SessionId = label, TenantId = "benchmark", Name = "Live " + scenario,
            Prompt = prompt, Mode = PlanningMode.Auto, MaxModelCalls = 8, MaxReplanAttempts = 2,
            Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768, Reasoning = "medium" },
            Options = new() { ["generator"] = new JsonObject { ["provider"] = model.Provider, ["model"] = model.Model } } } };
        if (phase == "revise")
        {
            var commandPath = SchemaPortabilityCampaign.Option(args, "--revision-command") ?? throw new ArgumentException("Supply --revision-command <file> with concrete review feedback.");
            var command = JsonSerializer.Deserialize(await File.ReadAllTextAsync(commandPath), PlanningJsonContext.Default.PlanningCommand)
                ?? throw new ArgumentException("Supply a revision command.");
            revisionSnapshot = new() { ["session"] = run["session"]!.DeepClone(), ["result"] = run["result"]!.DeepClone(),
                ["artifact_hash"] = run["artifact_hash"]?.DeepClone(), ["command"] = JsonSerializer.SerializeToNode(command, PlanningJsonContext.Default.PlanningCommand) };
            state = await ReviseAsync(run, command, runtime, timeout.Token);
        }
        try
        {
            for (var i = 0; i < 40 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            {
                state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, timeout.Token);
                await runtime.CheckpointAsync(state, CancellationToken.None);
                if (campaign.StopReason is not null) break;
            }
            if (state.Status == PlanningStatus.FinalReview) PlanningArtifactApproval.Verify(state);
        }
        catch (Exception ex) { run["failure"] = ex.ToString(); }
        run["result"] = new JsonObject { ["status"] = state.Status, ["planning_ms"] = priorMilliseconds + clock.ElapsedMilliseconds,
            ["calls"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts, ["discovery_reads"] = priorReads + observed.DiscoveryReads,
            ["review_revisions"] = run["planning_revisions"]?.AsArray().Count ?? 0,
            ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
            ["execution_success"] = false, ["execution_oracle"] = false, ["execution_status"] = "not_started",
            ["accounting"] = await LiveCampaignEvidence.AccountingAsync(campaign, label) };
        run["artifact_hash"] = state.Yaml is null ? null : state.ComputeArtifactHash();
        await Save(); Console.WriteLine(run["result"]!.ToJsonString());
        if (run["artifact_hash"] is not null) Console.WriteLine("artifact_hash=" + run["artifact_hash"]);

        Task Save() => campaign.SaveAsync(SchemaPortabilityCampaign.Collection, key, run, CancellationToken.None);
    }

    internal static async Task<PlanningSession> ApproveAsync(JsonObject run, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (run["execution_started"] is not null) throw new InvalidOperationException("Execution already started; never rerun an uncertain workflow.");
        if (command.Kind != "approve") throw new ArgumentException("The review command must be an explicit approval.");
        var session = run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        // The reviewer supplies the revision, hash and acknowledgments. Never infer them from the retained plan.
        session = await new HybridWorkflowPlanner().AdvanceAsync(session, command, runtime, ct);
        if (session.Status != PlanningStatus.Approved) throw new InvalidOperationException("Artifact approval failed.");
        return session;
    }

    internal static Task<PlanningSession> ReviseAsync(JsonObject run, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (run["execution_started"] is not null) throw new InvalidOperationException("Execution already started; never revise or replay it.");
        if (command.Kind != "revise" || command.PreserveRequirements != true)
            throw new ArgumentException("Review corrections must use revise with preserveRequirements=true.");
        var session = run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        if (session.Request.TenantId != "benchmark" || session.Request.SessionId != run["label"]?.ToString())
            throw new PlanningConflictException("The retained session does not belong to this evaluation run.");
        return new HybridWorkflowPlanner().AdvanceAsync(session, command, runtime, ct);
    }

    internal static Dictionary<string, McpServerOptions> Configuration(IReadOnlyDictionary<string, McpServerOptions> configured, string scenario, string inferenceEndpoint)
    {
        var names = scenario == "amazon" ? new[] { "Browser", "Document" } : ["Git", "Cmd", "GithubCopilot", "Document"];
        var result = new Dictionary<string, McpServerOptions>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var key = "GnOuGo." + name + ".Mcp";
            if (!configured.TryGetValue(key, out var settings)) throw new InvalidOperationException("Missing configured MCP: " + key);
            var clone = JsonSerializer.Deserialize<McpServerOptions>(JsonSerializer.Serialize(settings))!;
            var assembly = Path.GetFullPath(Path.Combine("src", key, "bin", "Debug", "net10.0", key + ".dll"));
            if (!File.Exists(assembly)) throw new InvalidOperationException("Build the current MCP first: " + key);
            clone.Type = "stdio"; clone.Command = "dotnet"; clone.Args = [assembly];
            clone.EnvironmentVariables ??= new();
            // This disposable evaluation must exercise the workflow's cleanup.
            // Override only the spawned browser's debug setting, not saved host policy.
            if (name == "Browser") clone.EnvironmentVariables["Browser__KeepBrowserOpen"] = "false";
            if (name == "GithubCopilot") clone.EnvironmentVariables["Code__Copilot__InferenceProxyEndpoint"] = inferenceEndpoint;
            result.Add(key, clone);
        }
        return result;
    }

    private sealed class ConsoleHuman(BenchmarkCampaign campaign, string run) : IHumanInputProvider
    {
        private int _count;
        public async Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            var key = run + ":dialog:" + (++_count);
            var payload = HumanInputContract.BuildRequestPayload(request);
            await campaign.SaveAsync("planning-evaluation-dialogs", key, new() { ["request"] = payload }, ct);
            Console.WriteLine("Explicit interaction required; submit one JSON response. No default is accepted:");
            Console.WriteLine(payload.ToJsonString());
            var answer = await ReadHumanAnswerAsync(payload, Console.In, Console.Out, ct);
            await campaign.SaveAsync("planning-evaluation-dialogs", key, new() { ["request"] = payload.DeepClone(), ["answer"] = answer?.DeepClone() }, CancellationToken.None);
            return answer;
        }
    }

    internal static async Task<JsonNode?> ReadHumanAnswerAsync(JsonObject payload, TextReader input, TextWriter output, CancellationToken ct)
    {
        var schema = HumanInputContract.ResolveOutputSchema(payload);
        await output.WriteLineAsync("Required response schema: " + schema.ToJsonString());
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await input.ReadLineAsync(ct) ?? throw new InvalidOperationException("No interaction provider is attached.");
            JsonNode? answer;
            try { answer = JsonNode.Parse(line); }
            catch (JsonException) { await output.WriteLineAsync("Invalid JSON. Submit an explicit response matching the displayed schema."); continue; }
            if (PlanningContractValidation.ValidateInstance(answer, schema).Count == 0) return answer;
            await output.WriteLineAsync("Response rejected; no approval submitted. Correct it using the displayed schema.");
        }
    }

    internal static LLMRequest ExecutionRequest(LLMRequest request, string run, int call)
    {
        var copy = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.Deserialize(PlanningJsonContext.Default.LLMRequest)!;
        copy.ClientRequestId = run + "-execution:" + (request.ClientRequestId ?? call + ":" + PlanningGraphCompiler.Fingerprint(request.Prompt));
        copy.Reasoning = "medium"; copy.MaxTokens = Math.Min(copy.MaxTokens ?? 32768, 32768);
        return copy;
    }

    private sealed class ExecutionModel(KeyVaultBenchmarkModel model, string run) : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string name, int outputTokens, CancellationToken ct)
            => model.InputTokenAllowanceAsync(provider, name, outputTokens, ct);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string name, CancellationToken ct)
            => model.SupportsStructuredOutputAsync(provider, name, ct);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string name, CancellationToken ct)
            => model.SupportedReasoningLevelsAsync(provider, name, ct);
        private int _calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            return model.CallExecutionAsync(ExecutionRequest(request, run, Interlocked.Increment(ref _calls)), ct);
        }
    }

    internal sealed class ObservedMcp(IMcpClientFactory inner, Func<JsonObject, Task> save) : IMcpClientFactory, IMcpExecutionHooks
    {
        private readonly Func<JsonObject, Task> _save = save;
        internal int DiscoveryReads;
        public IDisposable BeginCall(McpCallExecutionContext context) => ((IMcpExecutionHooks)inner).BeginCall(context);
        public string FormatFailureDiagnostics(string serverName, Exception exception) => ((IMcpExecutionHooks)inner).FormatFailureDiagnostics(serverName, exception);
        public IReadOnlyList<McpServerMetadata> ServerMetadata => inner.ServerMetadata;
        public async Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => new Session(this, await inner.GetClientAsync(serverName, ct));
        private sealed class Session(ObservedMcp owner, IMcpSession inner) : IMcpSession
        {
            public string ServerName => inner.ServerName;
            public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) { owner.DiscoveryReads++; return inner.ListToolsAsync(ct); }
            public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => inner.ListResourcesAsync(ct);
            public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => inner.ListPromptsAsync(ct);
            public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct) => inner.GetPromptAsync(name, arguments, ct);
            public async Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
            {
                if (toolName is "git_push" or "git_delete_remote_branch")
                    throw new InvalidOperationException("Remote publication is outside this live campaign.");
                var evt = new JsonObject { ["server"] = ServerName, ["tool"] = toolName, ["arguments"] = arguments?.DeepClone(), ["started"] = DateTimeOffset.UtcNow.ToString("O") };
                var clock = Stopwatch.StartNew();
                try { var result = await inner.CallToolAsync(toolName, arguments, ct); evt["error"] = result.IsError; evt["result"] = result.Content?.DeepClone(); return result; }
                catch (Exception ex) { evt["exception"] = ex.GetType().Name; throw; }
                finally { evt["latency_ms"] = clock.ElapsedMilliseconds; await owner._save(evt); }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
