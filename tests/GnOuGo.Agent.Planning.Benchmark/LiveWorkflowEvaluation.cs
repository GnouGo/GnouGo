using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;

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
        if (label.Length > 80 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid run identity.");
        var key = "run:" + label;
        var retained = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, key);
        if (phase == "inspect-run") { Console.WriteLine(retained?.ToJsonString() ?? "No run."); return; }
        await using var proxy = await CampaignInferenceProxy.StartAsync(model, label);
        var configurations = Configuration(model.McpServers, scenario, proxy.Endpoint);
        await using var transport = new ConfiguredMcpClientFactory(configurations, new PendingHuman(), model.Provider, model.Model);
        var run = new JsonObject { ["scenario"] = scenario, ["label"] = label, ["source"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"),
            ["phase"] = label.StartsWith("final-", StringComparison.Ordinal) ? "final" : "diagnostic", ["events"] = new JsonArray() };
        var observed = new ObservedMcp(transport, async e => { run["events"]!.AsArray().Add(e); await Save(); });
        var measured = new ExecutionModel(model, label);
        var engine = new WorkflowEngine { McpClientFactory = observed, LLMClient = measured,
            HumanInputProvider = new PendingHuman(), LlmDefaults = new() { Model = model.Model, Provider = model.Provider } };
        if (scenario == "code") engine.WithCopilotRunners(configurations.Keys.Where(k => k.Contains("GithubCopilot", StringComparison.Ordinal)).Select(k => new KeyValuePair<string, string>("coding", k)));
        var runtime = new WorkflowPlanningRuntime(engine, async (s, _) =>
        {
            run["session"] = JsonSerializer.SerializeToNode(s, PlanningJsonContext.Default.PlanningSession);
            await Save(); Console.WriteLine($"{label}: revision={s.Revision}, status={s.Status}, calls={s.ModelCalls}, repairs={s.ReplanAttempts}");
        });
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
            return;
        }
        if (retained is not null) throw new InvalidOperationException("Run already retained; inspect it instead of overwriting.");
        var relative = "workflows/" + campaign.Id + "/" + label;
        var prompt = scenario == "amazon"
            ? AmazonPrompt + "\nContraintes de cette évaluation autorisée: utilise Amazon.fr, au maximum les trois premiers produits; une seule entrée publique nommée query. Sauvegarde le classeur à " + relative + "/products.xlsx. Ferme le navigateur même en cas d’échec. CAPTCHA, prix ou données absents restent explicites, jamais inventés."
            : CodePrompt + "\nÉvaluation autorisée: deux entrées publiques nommées pullRequestUrl et reviewText. Cible SmartGuide PR #610, https://github.com/AxaFrance/SmartGuide/pull/610 ; head " + Head + " et base " + Base + ". Destination fixe du clone: " + relative + "/repository. Utilise les toolchains et checks déclarés par ce dépôt. Tous les feedbacks, décisions et commentaires de diff restent LOCAUX: aucune publication GitHub. Sauvegarde les preuves exactes de commandes et leurs codes de sortie dans " + relative + "/review.json, puis nettoie le clone même en cas d’échec. Une capacité absente ou un test échoué reste explicite. Aucune extension de permission/sandbox.";
        run["prompt"] = prompt; run["prompt_hash"] = PlanningGraphCompiler.Fingerprint(prompt);
        run["oracle_version"] = "real-workflows-v1"; run["workspace_relative"] = relative;
        run["inputs"] = scenario == "amazon" ? new JsonObject { ["query"] = "chaussure geox homme 45" }
            : new JsonObject { ["pullRequestUrl"] = "https://github.com/AxaFrance/SmartGuide/pull/610", ["reviewText"] = "Review correctness, regressions, security and test coverage of the pinned pull-request diff. Report checks truthfully and keep proposed comments local." };
        await Save();
        var clock = Stopwatch.StartNew(); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var state = new PlanningSession { Request = new() { SessionId = label, TenantId = "benchmark", Name = "Live " + scenario,
            Prompt = prompt, Mode = PlanningMode.Auto, MaxModelCalls = 8, MaxReplanAttempts = 2,
            Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768, Reasoning = "medium" },
            Options = new() { ["generator"] = new JsonObject { ["provider"] = model.Provider, ["model"] = model.Model } } } };
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
        run["result"] = new JsonObject { ["status"] = state.Status, ["planning_ms"] = clock.ElapsedMilliseconds,
            ["calls"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts, ["discovery_reads"] = observed.DiscoveryReads,
            ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
            ["execution_success"] = false, ["execution_oracle"] = false, ["execution_status"] = "not_started",
            ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign, label + ":") };
        await Save(); Console.WriteLine(run["result"]!.ToJsonString());

        Task Save() => campaign.SaveAsync(SchemaPortabilityCampaign.Collection, key, run, CancellationToken.None);
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
            if (name == "GithubCopilot") clone.EnvironmentVariables["Code__Copilot__InferenceProxyEndpoint"] = inferenceEndpoint;
            result.Add(key, clone);
        }
        return result;
    }

    private sealed class PendingHuman : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Live evaluation requires an explicit answer; no recommendation or permission is accepted automatically.");
    }

    private sealed class ExecutionModel(KeyVaultBenchmarkModel model, string run) : ILLMClient
    {
        private int _calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            request.ClientRequestId ??= run + "-execution:" + (++_calls) + ":" + PlanningGraphCompiler.Fingerprint(request.Prompt);
            return model.CallAsync(request, ct);
        }
    }

    internal sealed class ObservedMcp(IMcpClientFactory inner, Func<JsonObject, Task> save) : IMcpClientFactory
    {
        private readonly Func<JsonObject, Task> _save = save;
        internal int DiscoveryReads;
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
