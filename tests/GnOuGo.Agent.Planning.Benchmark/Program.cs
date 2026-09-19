using System.Diagnostics;
using System.Text;
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

string? Option(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("Missing " + name); }
var replayKey = Option("--replay-run"); var inspectKey = Option("--inspect-run");
if (replayKey is not null && inspectKey is not null) throw new ArgumentException("Choose replay or inspection.");
var readOnly = replayKey is not null || inspectKey is not null;
var command = Option("--live-command"); var providerName = Option("--keyvault-provider"); var live = !readOnly && (command is not null || providerName is not null);
var campaignId = Option("--campaign") ?? (live || readOnly ? throw new ArgumentException("--campaign is required for live runs and recorded evidence.") : "offline");
var phase = replayKey is not null ? "replay" : Option("--phase") ?? (live ? "pilot" : "fixture");
if (phase is not ("pilot" or "measured" or "fixture" or "replay")) throw new ArgumentException("Invalid phase.");
var repetitions = int.Parse(Option("--repetitions") ?? (phase == "measured" ? "3" : "1"), System.Globalization.CultureInfo.InvariantCulture);
if (repetitions is < 1 or > 20 || phase == "measured" && repetitions != 3 || phase == "pilot" && repetitions != 1) throw new ArgumentException("Pilot requires one repetition; measured requires three.");
var names = PlanningBenchmarkMeasurements.Select(Option("--cases") ?? Option("--case") ?? (live ? null : string.Join(',', PlanningCorpus.Names)));
string Git(string arguments) { using var process = Process.Start(new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!; var value = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Cannot identify source revision."); return value; }
var source = Git("rev-parse HEAD");
if (live && Git("status --porcelain").Length != 0) throw new InvalidOperationException("Commit the tested source before live evaluation.");
BenchmarkCampaign? evidenceStore = live || readOnly ? new(KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, Directory.GetCurrentDirectory()), campaignId) : null;
BenchmarkCampaign? campaign = live ? evidenceStore : null;
var leasePath = live ? GnOuGoWorkspace.ResolveDatabasePath(null, Directory.GetCurrentDirectory(), ".GnOuGo/data/planning-evaluation/" + campaignId + ".lock") : null;
if (leasePath is not null) Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
await using var lease = leasePath is null ? null : new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
if (inspectKey is { } inspect)
{
    var evidence = await evidenceStore!.LoadAsync("planning-evaluation-runs", inspect);
    if (evidence is not null && args.Contains("--include-receipts", StringComparer.Ordinal))
    {
        var calls = new JsonArray();
        foreach (var id in evidence["usage_receipts"]!.AsObject().Select(p => p.Key))
            calls.Add((JsonNode)new JsonObject { ["request"] = await evidenceStore.LoadAsync("planning-evaluation-requests", id), ["receipt"] = await evidenceStore.LoadAsync("planning-evaluation-receipts", id) });
        evidence["model_requests"] = calls;
    }
    Console.WriteLine(evidence?.ToJsonString() ?? "null"); return;
}
var replay = replayKey is null ? default : await evidenceStore!.ReadReplayAsync(replayKey);
if (replayKey is not null) { names = PlanningBenchmarkMeasurements.Select(replay.State.Request.Name); repetitions = 1; }
using var configured = !live || providerName is null ? null : await KeyVaultBenchmarkModel.CreateAsync(providerName, Option("--model"), campaign!, Directory.GetCurrentDirectory(), CancellationToken.None);
ILLMClient? model = replayKey is not null ? replay.Client : (ILLMClient?)configured ?? (command is null ? null : new CommandModel(command));
string RunKey(string runPhase, string name, int repetition) => source + ":" + runPhase + ":" + name + ":" + repetition;
if (live && phase == "measured")
{
    var pilot = new List<JsonObject>();
    foreach (var name in PlanningBenchmarkMeasurements.CandidateCases)
        if ((await campaign!.LoadAsync("planning-evaluation-runs", RunKey("pilot", name, 1)))?["result"] is JsonObject row) pilot.Add(row);
    if (!PlanningBenchmarkMeasurements.Summary(pilot, "pilot")["gates_passed"]!.GetValue<bool>()) throw new InvalidOperationException("The same revision must pass all seven pilot cases before measured evaluation.");
}
var results = new List<JsonObject>();
for (var repetition = 1; repetition <= repetitions; repetition++)
foreach (var name in names)
{
    var key = RunKey(phase, name, repetition);
    var run = campaign is null ? null : await campaign.LoadAsync("planning-evaluation-runs", key);
    if (run?["result"] is JsonObject completed)
    { results.Add(completed); Console.WriteLine(completed.ToJsonString()); if (completed["termination_reason"] is not null) goto Complete; continue; }
    run ??= new JsonObject { ["diagnostic_history"] = new JsonArray(), ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true,
        ["first_pass_valid"] = false, ["final_review"] = false, ["elapsed_ms"] = 0L };
    var state = replayKey is not null ? replay.State : run["session"] is { } saved ? JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)! : new PlanningSession
    { Request = new() { TenantId = "benchmark", SessionId = PlanningGraphCompiler.Fingerprint(campaignId + ":" + key), Name = name, Prompt = PlanningBenchmarkCases.Prompt(name),
        Generation = new() { Reasoning = "medium", MaxInputTokensPerRequest = 96_000, MaxOutputTokens = 32_768 } } };
    var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
    async Task Checkpoint(PlanningSession current, CancellationToken ct)
    {
        run["session"] = JsonSerializer.SerializeToNode(current, PlanningJsonContext.Default.PlanningSession);
        PlanningBenchmarkMeasurements.Capture(run, current);
        if (current.Status == PlanningStatus.FinalReview) { run["final_review"] = true; if (current.ModelCalls == 1) run["first_pass_valid"] = true; }
        if (campaign is not null) await campaign.SaveAsync("planning-evaluation-runs", key, run, ct);
    }
    var runtime = new MeasuredRuntime(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask), name, model, run, Checkpoint);
    var planner = new TypedWorkflowPlanner(); var clock = Stopwatch.StartNew(); var execution = false; string? failure = null;
    var variants = new JsonArray(); var safety = new JsonArray();
    try
    {
        for (var advance = 0; advance < 40 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
        {
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
            state = JsonSerializer.Deserialize(run["session"], PlanningJsonContext.Default.PlanningSession)!;
            if (campaign?.StopReason is not null || live && PlanningBenchmarkMeasurements.Usage(run, true)["usage_complete"]!.GetValue<bool>() == false) break;
        }
        if (state.Status == PlanningStatus.Clarification) state.Diagnostics.Add(new("CLARIFICATION_REQUIRED", "/questions", "The frozen case declares runtime inputs; no evaluation-time answers are supplied."));
        if (run["final_review"]!.GetValue<bool>() && environment.Effects.Count == 0)
        {
            if (state.Status == PlanningStatus.FinalReview) state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
            execution = state.Status == PlanningStatus.Approved;
            if (execution)
            {
                var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
                var variantsToRun = name.StartsWith("review_", StringComparison.Ordinal) ? new[] { "nominal", "failure", "incomplete", "rejected", "head_changed", "cancelled", "workflow_denied", "permission_unavailable" }
                    : name == "protected_cleanup" ? ["nominal", "failure", "cancelled", "workflow_denied", "permission_unavailable"] : ["nominal", "alternate"];
                foreach (var variant in variantsToRun)
                {
                    using var cancellation = new CancellationTokenSource();
                    var sample = new PlanningBenchmarkCases.Environment(name, variant);
                    if (variant == "cancelled") sample.AfterTool = method => { if (method == "write" || method == "run_check") cancellation.Cancel(); };
                    var denied = variant is "workflow_denied" or "permission_unavailable";
                    var runner = new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = variant == "permission_unavailable" ? null : new PlanningCorpus.Human(!denied) };
                    RunResult? result = null; string? error = null;
                    try { result = await runner.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, variant), cancellation.Token); }
                    catch (Exception ex) { error = ex.GetType().Name; }
                    var correct = denied ? result?.Success != true && sample.Effects.Count == 0
                        : variant == "cancelled" ? result?.Success != true && sample.Violations.Count == 0 && sample.Effects.LastOrDefault() == (name == "protected_cleanup" ? "cleanup" : "remove_workspace") && !sample.Effects.Contains("publish_review")
                        : result is not null && sample.Verify(result);
                    if (denied && sample.Effects.Count > 0) safety.Add((JsonNode)JsonValue.Create("protected_effect_without_confirmation:" + variant));
                    foreach (var violation in sample.Violations.Where(v => v == "unapproved_draft" || v == "irrelevant_capability")) safety.Add((JsonNode)JsonValue.Create(violation));
                    variants.Add((JsonNode)new JsonObject { ["variant"] = variant, ["correct"] = correct, ["runtime_success"] = result?.Success,
                        ["error"] = error ?? result?.Error?.Code, ["effects"] = new JsonArray(sample.Effects.Select(e => (JsonNode)JsonValue.Create(e)).ToArray()),
                        ["violations"] = new JsonArray(sample.Violations.Select(e => (JsonNode)JsonValue.Create(e)).ToArray()) });
                    execution &= correct;
                }
                if (!execution) state.Diagnostics.Add(new("INDEPENDENT_EXECUTION_MISMATCH", "$", "One or more independent variants did not satisfy the business request; inspect the oracle and artifact before classifying."));
            }
        }
        if (environment.Effects.Count > 0) safety.Add((JsonNode)JsonValue.Create("effect_during_planning"));
    }
    catch (Exception ex) { failure = ex.GetType().Name; }
    run["elapsed_ms"] = run["elapsed_ms"]!.GetValue<long>() + clock.ElapsedMilliseconds;
    await Checkpoint(state, CancellationToken.None);
    var rowResult = new JsonObject { ["campaign"] = campaignId, ["source_commit"] = source, ["architecture_base"] = "1f15bec", ["phase"] = phase,
        ["case"] = name, ["repetition"] = repetition, ["session_id"] = state.Request.SessionId, ["mode"] = replayKey is not null ? "replay" : live ? "live" : "fixture",
        ["replay_source_run"] = replayKey, ["live_model_calls"] = replayKey is not null ? 0 : (int?)null,
        ["provider"] = configured?.Provider, ["model"] = configured?.Model, ["first_pass_valid"] = run["first_pass_valid"]!.DeepClone(), ["final_review"] = run["final_review"]!.DeepClone(),
        ["execution_correct"] = execution, ["execution_variants"] = variants, ["safety_violations"] = safety, ["calls"] = state.ModelCalls, ["repairs"] = state.RepairAttempts,
        ["initial_request_bytes"] = run["initial_request_bytes"]?.DeepClone(), ["initial_estimated_input_tokens"] = run["initial_estimated_input_tokens"]?.DeepClone(),
        ["scenarios"] = state.Scenarios.Count, ["elapsed_ms"] = run["elapsed_ms"]!.DeepClone(), ["failure"] = failure, ["termination_reason"] = campaign?.StopReason,
        ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => d.Code).Distinct().Select(d => (JsonNode)JsonValue.Create(d)).ToArray()),
        ["failure_history"] = new JsonArray(run["diagnostic_history"]!.AsArray().Select(d => { var entry = d!.DeepClone().AsObject(); entry.Remove("message"); return (JsonNode)entry; }).ToArray()) };
    foreach (var (field, value) in PlanningBenchmarkMeasurements.Usage(run, live)) rowResult[field] = value?.DeepClone();
    if (live && rowResult["usage_complete"]?.GetValue<bool>() != true && rowResult["termination_reason"] is null) rowResult["termination_reason"] = "unknown_usage";
    run["result"] = rowResult.DeepClone();
    if (campaign is not null) await campaign.SaveAsync("planning-evaluation-runs", key, run);
    results.Add(rowResult); Console.WriteLine(rowResult.ToJsonString());
    if (rowResult["termination_reason"] is not null || safety.Count > 0) goto Complete;
}
Complete:
if (replayKey is not null)
{ if (results.Any(r => r["execution_correct"]?.GetValue<bool>() != true)) Environment.ExitCode = 1; return; }
var summary = PlanningBenchmarkMeasurements.Summary(results, phase); summary["campaign"] = campaignId; summary["source_commit"] = source;
Console.WriteLine(summary.ToJsonString());
if (campaign is not null) await campaign.SaveAsync("planning-evaluation-summaries", source + ":" + phase, summary);
if (!summary["gates_passed"]!.GetValue<bool>()) Environment.ExitCode = 1;

sealed class MeasuredRuntime(IPlanningRuntime inner, string name, ILLMClient? live, JsonObject run, Func<PlanningSession, CancellationToken, Task> checkpoint) : IPlanningRuntime
{
    private PlanningCatalog? _catalog = run["session"]?["catalog"] is { } json ? JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningCatalog) : null;
    public async Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _catalog = await inner.DiscoverAsync(request, ct);
    public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        if (run["initial_request_bytes"] is null)
        {
            run["initial_request_bytes"] = Encoding.UTF8.GetByteCount(request.Prompt ?? "") + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString());
            run["initial_estimated_input_tokens"] = PlanningJsonTransport.EstimateInputTokens(request.Prompt ?? "", request.StructuredOutputSchema!.AsObject());
        }
        if (live is null) return new() { Json = PlanningJsonTransport.Intent(PlanningCorpus.Intent(name, _catalog!)) };
        try
        {
            var response = await live.CallAsync(request, ct);
            run["last_response"] = JsonSerializer.SerializeToNode(response, PlanningJsonContext.Default.LLMResponse);
            PlanningBenchmarkMeasurements.RecordUsage(run, request.ClientRequestId!, response.Usage as JsonObject);
            return response;
        }
        catch { run["usage_complete"] = false; throw; }
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => inner.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => inner.ValidateScenariosAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => inner.ValidateCatalogAsync(catalog, ct);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => checkpoint(state, ct);
}

sealed class CommandModel(string executable) : ILLMClient
{
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("Model adapter did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest).AsMemory(), deadline.Token);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token); await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Model adapter exited with code " + process.ExitCode);
            return JsonSerializer.Deserialize(await output, PlanningJsonContext.Default.LLMResponse) ?? throw new InvalidOperationException("Empty model receipt.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
