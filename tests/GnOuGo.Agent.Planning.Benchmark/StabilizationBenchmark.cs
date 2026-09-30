using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;
using GnOuGo.Workspace;

// One runner for both production revisions. Private evidence stays in the existing encrypted journal.
internal static class StabilizationBenchmark
{
    internal static readonly string[] Cases = [.. PlanningBenchmarkCases.Names, .. LocalExecutionCases.Names];
    internal static string Complexity(string name) => name is "local" or "read_transform" or "files_read" ? "simple"
        : name is "nullable_defaults" or "conditional" or "protected_cleanup" or "files_copy" ? "medium" : "complex";
    private const string Collection = "planning-stabilization-runs";
    internal static async Task RunAsync(string[] args)
    {
        string? Option(string name) => Array.IndexOf(args, name) is var i && i >= 0 ? args.ElementAtOrDefault(i + 1) ?? throw new ArgumentException("Missing " + name) : null;
        var campaignId = Option("--campaign") ?? throw new ArgumentException("--campaign is required.");
        var root = Directory.GetCurrentDirectory();
        var campaign = new BenchmarkCampaign(KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root), campaignId);
        if (Option("--inspect") is { } inspect)
        {
            var saved = await campaign.LoadAsync(Collection, inspect) ?? throw new ArgumentException("Unknown run.");
            Console.WriteLine((args.Contains("--private-evidence") ? saved : saved["result"])?.ToJsonString()); return;
        }
        if (Option("--compare") is { } baseline)
        {
            var candidate = Option("--candidate") ?? throw new ArgumentException("--candidate is required.");
            var before = new List<JsonObject>(); var after = new List<JsonObject>();
            foreach (var name in Cases) for (var i = 1; i <= 3; i++)
            {
                if (await campaign.LoadAsync(Collection, baseline + ":baseline:" + name + ":" + i) is { } b) before.Add(b["result"]!.AsObject());
                if (await campaign.LoadAsync(Collection, candidate + ":final:" + name + ":" + i) is { } a) after.Add(a["result"]!.AsObject());
            }
            var comparison = Compare(before, after); comparison["campaign_accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign);
            Console.WriteLine(comparison.ToJsonString()); Environment.ExitCode = comparison["passed"]!.GetValue<bool>() ? 0 : 1; return;
        }
        if (Git("status", "--porcelain").Length != 0) throw new InvalidOperationException("Commit the complete harness and production source before collecting evidence.");
        var source = Option("--source") ?? Git("rev-parse", "HEAD");
        if (source.Length != 40 || !source.All(Uri.IsHexDigit) || Git("diff", "--name-only", source, "HEAD", "--", "src", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json").Length != 0)
            throw new ArgumentException("--source must identify the exact unchanged production tree.");
        var cohort = Option("--cohort") ?? "diagnostic";
        if (cohort is not ("baseline" or "diagnostic" or "final")) throw new ArgumentException("Unknown cohort.");
        var selected = Option("--cases")?.Split(',', StringSplitOptions.TrimEntries) ?? Cases;
        if (selected.Length == 0 || selected.Distinct().Count() != selected.Length || selected.Any(n => !Cases.Contains(n))) throw new ArgumentException("Unknown or repeated cases.");
        var repetitions = cohort == "diagnostic" ? 1 : 3;
        var executable = Path.GetFullPath(Option("--cmd-executable") ?? "src/GnOuGo.Cmd.Mcp/bin/Release/net10.0/GnOuGo.Cmd.Mcp");
        if (!File.Exists(executable)) throw new ArgumentException("Build the Release Cmd MCP executable first.");
        var manifest = Manifest(executable);
        var leasePath = GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/planning-evaluation/" + campaignId + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        await using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pinned = await campaign.LoadAsync("planning-evaluation-configuration", "stabilization-manifest");
        if (pinned is not null && !JsonNode.DeepEquals(pinned, manifest)) throw new InvalidOperationException("Harness, cases, oracle, metadata or environment differs from the frozen campaign manifest.");
        if (pinned is null) await campaign.SaveAsync("planning-evaluation-configuration", "stabilization-manifest", manifest);
        using var model = await KeyVaultBenchmarkModel.CreateAsync("OpenAi", Option("--model"), campaign, root, CancellationToken.None);
        var manifestHash = PlanningGraphCompiler.Fingerprint(manifest.ToJsonString());
        Console.WriteLine(new JsonObject { ["manifest"] = manifest, ["source"] = source, ["harness_commit"] = Git("rev-parse", "HEAD"), ["provider"] = model.Provider, ["model"] = model.Model }.ToJsonString());
        foreach (var name in selected) for (var i = 1; i <= repetitions; i++)
        {
            var key = source + ":" + cohort + ":" + name + ":" + i;
            var run = await campaign.LoadAsync(Collection, key);
            if (run?["result"] is JsonObject retained) { Console.WriteLine(retained.ToJsonString()); continue; }
            var row = await ExecuteAsync(campaign, model, source, cohort, name, i, manifestHash, executable, run);
            Console.WriteLine(row.ToJsonString());
            if (campaign.StopReason is not null || row["usage_bounded"]?.GetValue<bool>() != true)
            { Console.WriteLine((await BenchmarkHttpJournal.AccountingAsync(campaign)).ToJsonString()); Environment.ExitCode = 2; return; }
        }
        Console.WriteLine((await BenchmarkHttpJournal.AccountingAsync(campaign)).ToJsonString());
    }
    private static async Task<JsonObject> ExecuteAsync(BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, string source, string cohort,
        string name, int repetition, string manifest, string executable, JsonObject? retained)
    {
        var key = source + ":" + cohort + ":" + name + ":" + repetition;
        var id = PlanningGraphCompiler.Fingerprint(campaign.Id + ":stabilization:" + key);
        var run = retained ?? new JsonObject { ["diagnostic_history"] = new JsonArray(), ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true,
            ["execution_usage"] = new JsonObject { ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true }, ["discovery_reads"] = 0 };
        var state = run["session"]?.Deserialize(PlanningJsonContext.Default.PlanningSession) ?? new() { Request = new()
        { TenantId = "benchmark", SessionId = id, Name = name, Mode = PlanningMode.Auto,
            Prompt = LocalExecutionCases.Names.Contains(name) ? LocalExecutionCases.Prompt(name) : PlanningBenchmarkCases.Prompt(name),
            MaxModelCalls = 8, MaxReplanAttempts = 2, Generation = new() { Reasoning = "medium", MaxInputTokensPerRequest = 96_000, MaxOutputTokens = 32_768 } } };
        var localRoot = Path.Combine(GnOuGoWorkspace.ResolveWorkflowWorkspacesDirectory(GnOuGoWorkspace.ResolveDefaultWorkingDirectory()), "planner-benchmark-" + id);
        Directory.CreateDirectory(localRoot);
        await using var local = LocalExecutionCases.Names.Contains(name) ? new LocalExecutionCases(localRoot, executable) : null;
        var environment = new PlanningBenchmarkCases.Environment(name);
        var engine = new WorkflowEngine { McpClientFactory = local?.Factory ?? (IMcpClientFactory)environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
        var runtime = new InstrumentedRuntime(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask), model, run, async s =>
        {
            run["session"] = JsonSerializer.SerializeToNode(s, PlanningJsonContext.Default.PlanningSession);
            PlanningBenchmarkMeasurements.Capture(run, s); await campaign.SaveAsync(Collection, key, run);
        });
        var planner = new HybridWorkflowPlanner(); var clock = Stopwatch.StartNew(); var variants = new JsonArray();
        var planningMs = 0L; var executionMs = 0L; string? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            if (run["execution_started"]?.GetValue<bool>() == true) throw new InvalidOperationException("Interrupted execution is retained, never silently repeated.");
            local?.Prepare(name, "nominal");
            var initialFiles = local?.Snapshot();
            for (var advance = 0; advance < 40 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
                state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, deadline.Token);
            planningMs = clock.ElapsedMilliseconds;
            if (environment.Effects.Count > 0 || local is not null && (local.Calls.Count > 0 || !JsonNode.DeepEquals(initialFiles, local.Snapshot()))) throw new InvalidOperationException("Planning produced effects.");
            if (state.Status == PlanningStatus.FinalReview)
            {
                state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, deadline.Token);
                if (state.Status == PlanningStatus.Approved)
                {
                    run["execution_started"] = true; await campaign.SaveAsync(Collection, key, run);
                    var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!)); var executionClock = Stopwatch.StartNew();
                    foreach (var variant in local is not null ? LocalExecutionCases.Variants(name) : Variants(name))
                    {
                        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        var sample = new PlanningBenchmarkCases.Environment(name, variant); if (variant == "cancelled") sample.CancelDuringWork(cancellation);
                        var denied = variant is "denied" or "workflow_denied" or "permission_unavailable";
                        var executionModel = new ExecutionModel(model, run["execution_usage"]!.AsObject(), id + "-execution-" + variant, () => campaign.SaveAsync(Collection, key, run));
                        var runner = new WorkflowEngine { McpClientFactory = local?.Factory ?? (IMcpClientFactory)sample.Factory(), LLMClient = executionModel,
                            LlmDefaults = new() { Provider = model.Provider, Model = model.Model }, HumanInputProvider = variant == "permission_unavailable" ? null : new PlanningCorpus.Human(!denied) };
                        RunResult? result = null; string? error = null;
                        try { result = await runner.ExecuteAsync(doc.Workflows[doc.Entrypoint!], local?.Prepare(name, variant) ?? PlanningBenchmarkCases.Inputs(name, variant), cancellation.Token); }
                        catch (Exception ex) { error = ex.GetType().Name; }
                        variants.Add(local?.Observe(name, variant, result, error) ?? Observe(sample, name, variant, result, error));
                        if (campaign.StopReason is not null) break;
                    }
                    executionMs = executionClock.ElapsedMilliseconds;
                }
            }
        }
        catch (Exception ex) { failure = ex.GetType().Name; run["private_failure"] = ex.ToString(); }
        var expected = local is not null ? LocalExecutionCases.Variants(name).Length : Variants(name).Length;
        var usage = PlanningBenchmarkMeasurements.Usage(run, true); var executionUsage = PlanningBenchmarkMeasurements.Usage(run["execution_usage"]!.AsObject(), true);
        var row = new JsonObject { ["case"] = name, ["complexity"] = Complexity(name), ["integration"] = local is null ? "mocked" : "real_local",
            ["source"] = source, ["harness_commit"] = Git("rev-parse", "HEAD"), ["manifest"] = manifest, ["cohort"] = cohort, ["repetition"] = repetition,
            ["campaign"] = campaign.Id, ["provider"] = model.Provider, ["model"] = model.Model, ["status"] = state.Status,
            ["execution_correct"] = variants.Count == expected && variants.All(v => v!["correct"]!.GetValue<bool>()),
            ["execution_variants"] = variants, ["nominal_success"] = variants.FirstOrDefault()?["runtime_success"]?.DeepClone(),
            ["logical_planning_calls"] = state.ModelCalls, ["planning_attempts"] = state.ModelCalls + PlanningBenchmarkMeasurements.ExtraTransportCalls(run),
            ["execution_attempts"] = run["execution_usage"]!["usage_receipts"]!.AsObject().Sum(p => p.Value?["transport_attempts"]?.GetValue<int>() ?? 1),
            ["repairs"] = state.ReplanAttempts, ["discovery_reads"] = run["discovery_reads"]!.DeepClone(),
            ["planning_ms"] = planningMs, ["execution_ms"] = executionMs, ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["planning_usage"] = usage, ["execution_usage"] = executionUsage,
            ["usage_bounded"] = usage["usage_bounded"]?.GetValue<bool>() == true && executionUsage["usage_bounded"]?.GetValue<bool>() == true,
            ["failure"] = failure, ["termination_reason"] = campaign.StopReason,
            ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => (JsonNode?)JsonValue.Create(d.Code)).ToArray()) };
        row["total_attempts"] = row["planning_attempts"]!.GetValue<int>() + row["execution_attempts"]!.GetValue<int>();
        row["total_input_tokens"] = usage["input_tokens"] is null || executionUsage["input_tokens"] is null ? null : usage["input_tokens"]!.GetValue<long>() + executionUsage["input_tokens"]!.GetValue<long>();
        row["total_output_tokens"] = usage["output_tokens"] is null || executionUsage["output_tokens"] is null ? null : usage["output_tokens"]!.GetValue<long>() + executionUsage["output_tokens"]!.GetValue<long>();
        run["result"] = row.DeepClone(); run["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
        await campaign.SaveAsync(Collection, key, run); if (local is null) Directory.Delete(localRoot, true); return row;
    }
    private static string[] Variants(string name) => name.StartsWith("review_", StringComparison.Ordinal) ? ["nominal", "failure", "incomplete", "rejected", "head_changed", "cancelled", "workflow_denied", "permission_unavailable"]
        : name == "protected_cleanup" ? ["nominal", "failure", "cancelled", "workflow_denied", "permission_unavailable"] : ["nominal", "alternate"];
    private static JsonObject Observe(PlanningBenchmarkCases.Environment sample, string name, string variant, RunResult? result, string? error)
    {
        var denied = variant is "workflow_denied" or "permission_unavailable";
        var correct = denied ? result?.Success == false && sample.Effects.Count == 0
            : variant == "cancelled" ? result?.Success == false && sample.Violations.Count == 0 && sample.Effects.LastOrDefault() == (name == "protected_cleanup" ? "cleanup" : "remove_workspace") && !sample.Effects.Contains("publish_review")
            : result is not null && sample.Verify(result);
        return new() { ["variant"] = variant, ["correct"] = correct, ["runtime_success"] = result?.Success, ["error"] = error ?? result?.Error?.Code,
            ["effects"] = new JsonArray(sample.Effects.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["safety_violations"] = new JsonArray(sample.Violations.Concat(denied && sample.Effects.Count > 0 ? ["effect_without_permission"] : []).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
    }
    internal static JsonObject Compare(IReadOnlyList<JsonObject> before, IReadOnlyList<JsonObject> after)
    {
        bool Complete(IReadOnlyList<JsonObject> rows) => rows.Count == Cases.Length * 3 && Cases.All(n => Enumerable.Range(1, 3).All(i => rows.Count(r => r["case"]?.ToString() == n && r["repetition"]?.GetValue<int>() == i) == 1)) && rows.Select(r => r["source"]?.ToString()).Distinct().Count() == 1;
        var all = before.Concat(after).ToArray(); var complete = Complete(before) && Complete(after);
        var comparable = complete && new[] { "manifest", "provider", "model", "campaign" }.All(k => all.All(r => !string.IsNullOrWhiteSpace(r[k]?.ToString())) && all.Select(r => r[k]!.ToString()).Distinct().Count() == 1) && all.All(r => r["usage_bounded"]?.GetValue<bool>() == true);
        var correct = after.Count == 33 && after.All(r => r["execution_correct"]?.GetValue<bool>() == true && r["nominal_success"]?.GetValue<bool>() == true && r["execution_variants"]!.AsArray().All(v => v!["safety_violations"] is JsonArray { Count: 0 }));
        var simple = after.Where(r => r["complexity"]?.ToString() == "simple").All(r => r["logical_planning_calls"]?.GetValue<int>() == 1 && r["repairs"]?.GetValue<int>() == 0);
        return new() { ["passed"] = comparable && correct && simple, ["status"] = !comparable ? "inconclusive" : correct && simple ? "passed" : "failed",
            ["complete"] = complete, ["comparable"] = comparable, ["all_execution_oracles_pass"] = correct, ["simple_one_call_zero_repair"] = simple,
            ["baseline"] = Summary(before), ["candidate"] = Summary(after) };
    }
    private static JsonObject Summary(IReadOnlyList<JsonObject> rows)
    {
        var result = new JsonObject { ["runs"] = rows.Count, ["execution_correct"] = rows.Count(r => r["execution_correct"]?.GetValue<bool>() == true) };
        foreach (var field in new[] { "logical_planning_calls", "planning_attempts", "execution_attempts", "total_attempts", "total_input_tokens", "total_output_tokens", "repairs", "discovery_reads", "planning_ms", "execution_ms", "elapsed_ms" })
        {
            var values = rows.Select(r => double.TryParse(r[field]?.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? (double?)n : null).OfType<double>().Order().ToArray();
            result[field] = new JsonObject { ["median"] = values.Length == 0 ? null : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2,
                ["p95"] = values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * .95) - 1] };
        }
        foreach (var role in new[] { "planning_usage", "execution_usage" })
            result[role] = new JsonObject { ["known_input_tokens"] = rows.Sum(r => r[role]?["known_input_tokens"]?.GetValue<long>() ?? 0),
                ["known_output_tokens"] = rows.Sum(r => r[role]?["known_output_tokens"]?.GetValue<long>() ?? 0), ["usage_complete"] = rows.All(r => r[role]?["usage_complete"]?.GetValue<bool>() == true) };
        if (rows.Select(r => r["complexity"]?.ToString()).Distinct().Count() > 1)
            result["by_complexity"] = new JsonObject(rows.GroupBy(r => r["complexity"]!.ToString()).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, Summary(g.ToArray()))));
        if (rows.Select(r => r["case"]?.ToString()).Distinct().Count() > 1)
            result["by_case"] = new JsonObject(rows.GroupBy(r => r["case"]!.ToString()).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, Summary(g.ToArray()))));
        return result;
    }
    private static JsonObject Manifest(string executable)
    {
        var paths = new[] { "tests/GnOuGo.Agent.Planning.Benchmark/StabilizationBenchmark.cs", "tests/GnOuGo.Agent.Planning.Benchmark/LocalExecutionCases.cs",
            "tests/GnOuGo.Agent.Planning.Benchmark/BenchmarkCampaign.cs", "tests/GnOuGo.Agent.Planning.Benchmark/BenchmarkHttpJournal.cs", "tests/GnOuGo.Agent.Planning.Benchmark/KeyVaultBenchmarkModel.cs",
            "tests/Shared/PlanningBenchmarkCases.cs", "tests/Shared/PlanningBenchmarkMeasurements.cs", "tests/Shared/PlanningCorpus.cs", "src/GnOuGo.Cmd.Mcp/appsettings.json" };
        return new() { ["version"] = 1, ["files"] = new JsonObject(paths.Select(p => new KeyValuePair<string, JsonNode?>(p, JsonValue.Create(Hash(p))))),
            ["cmd_assembly"] = Hash(Path.ChangeExtension(executable, ".dll")), ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), ["runtime"] = Environment.Version.ToString(),
            ["calls"] = 8, ["repairs"] = 2, ["input_tokens"] = 96_000, ["output_tokens"] = 32_768, ["reasoning"] = "medium", ["ceiling_eur"] = 50 };
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot identify source revision."); return output;
    }
    private sealed class InstrumentedRuntime(IPlanningRuntime inner, KeyVaultBenchmarkModel model, JsonObject run, Func<PlanningSession, Task> checkpoint) : IPlanningRuntime
    {
        public ICapabilityCatalog Capabilities { get; } = new CountingCatalog(inner.Capabilities, run);
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => inner.DiscoverAsync(request, ct);
        public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            try { var response = await model.CallAsync(request, ct); PlanningBenchmarkMeasurements.RecordUsage(run, request.ClientRequestId!, response.Usage as JsonObject); return response; }
            catch { PlanningBenchmarkMeasurements.RecordUsage(run, request.ClientRequestId!, await model.PartialUsageAsync(request.ClientRequestId!, CancellationToken.None)); throw; }
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => inner.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => inner.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => checkpoint(state);
    }
    private sealed class CountingCatalog(ICapabilityCatalog inner, JsonObject run) : ICapabilityCatalog
    {
        private void Count() => run["discovery_reads"] = run["discovery_reads"]!.GetValue<int>() + 1;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) { Count(); return inner.ListSourcesAsync(ct); }
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { Count(); return inner.ListAsync(sourceId, cursor, ct, query, producedArtifactKind); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) { Count(); return inner.ResolveAsync(summary, ct); }
    }
    private sealed class ExecutionModel(KeyVaultBenchmarkModel inner, JsonObject usage, string prefix, Func<Task> checkpoint) : ILLMClient
    {
        private int _calls;
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            request.ClientRequestId = prefix + ":" + Interlocked.Increment(ref _calls); request.MaxTokens = Math.Min(request.MaxTokens ?? 32_768, 32_768);
            try { var response = await inner.CallAsync(request, ct); PlanningBenchmarkMeasurements.RecordUsage(usage, request.ClientRequestId, response.Usage as JsonObject); return response; }
            catch { PlanningBenchmarkMeasurements.RecordUsage(usage, request.ClientRequestId, await inner.PartialUsageAsync(request.ClientRequestId, CancellationToken.None)); throw; }
            finally { await checkpoint(); }
        }
    }
}
