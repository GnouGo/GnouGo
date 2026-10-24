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

if (args.FirstOrDefault() == "--compact-comparison")
{
    if (args.Length != 2 || new FileInfo(args[1]).Length > 16_777_216)
        throw new ArgumentException("Supply one bounded JSON document containing sanitized baseline and candidate measurement rows.");
    var comparison = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!.AsObject();
    var cases = comparison["cases"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    if (cases.Length == 0 || cases.Distinct(StringComparer.Ordinal).Count() != cases.Length)
        throw new ArgumentException("Supply distinct comparison cases.");
    var report = PlanningBenchmarkMeasurements.CompareCompactConstruction(
        comparison["baseline"]!.AsArray().Select(n => n!.AsObject()).ToArray(),
        comparison["candidate"]!.AsArray().Select(n => n!.AsObject()).ToArray(), cases, comparison["comparison_policy"] as JsonObject);
    Console.WriteLine(report.ToJsonString());
    if (report["passed"]?.GetValue<bool>() != true) Environment.ExitCode = 1;
    return;
}

if (args.FirstOrDefault() == "--mapping-fixture") { await MappingLiveEvaluation.ServeAsync(args[1]); return; }
if (args.FirstOrDefault() == "--stabilization") { await StabilizationBenchmark.RunAsync(args); return; }
if (args.FirstOrDefault() == "--real-contract-planning") { await RealContractPlanningProbe.RunAsync(args); return; }
if (args.FirstOrDefault() == "--schema-portability") { await SchemaPortabilityCampaign.RunAsync(args); return; }

string? Option(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("Missing " + name); }
var replayKey = Option("--replay-run"); var inspectKey = Option("--inspect-run");
var retainKey = Option("--retain-inconclusive-run"); var compareParent = Option("--compare-parent");
var inspectCampaign = args.Contains("--inspect-campaign", StringComparer.Ordinal);
if ((replayKey is null ? 0 : 1) + (inspectKey is null ? 0 : 1) + (inspectCampaign ? 1 : 0) + (retainKey is null ? 0 : 1) + (compareParent is null ? 0 : 1) > 1) throw new ArgumentException("Choose one replay or inspection command.");
var readOnly = replayKey is not null || inspectKey is not null || inspectCampaign || retainKey is not null || compareParent is not null;
var command = Option("--live-command"); var providerName = Option("--keyvault-provider"); var live = !readOnly && (command is not null || providerName is not null);
var campaignId = Option("--campaign") ?? (live || readOnly ? throw new ArgumentException("--campaign is required for live runs and recorded evidence.") : "offline");
var phase = replayKey is not null ? "replay" : Option("--phase") ?? (live ? "pilot" : "fixture");
if (phase is not ("pilot" or "measured" or "fixture" or "replay")) throw new ArgumentException("Invalid phase.");
var arm = Option("--arm");
if (arm is not (null or "judge" or "defaults")) throw new ArgumentException("Choose an optional judge or defaults comparison arm.");
var cohort = PlanningBenchmarkMeasurements.Cohort(args);
var repetitions = int.Parse(Option("--repetitions") ?? (phase == "measured" ? "3" : "1"), System.Globalization.CultureInfo.InvariantCulture);
if (repetitions is < 1 or > 20 || phase == "measured" && repetitions != 3 || phase == "pilot" && repetitions != 1) throw new ArgumentException("Pilot requires one repetition; measured requires three.");
var firstRepetition = int.Parse(Option("--first-repetition") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
if (firstRepetition < 1 || firstRepetition > repetitions || firstRepetition != 1 && phase != "fixture")
    throw new ArgumentException("A starting repetition is supported only for an existing fixture cohort and cannot exceed --repetitions.");
var names = PlanningBenchmarkMeasurements.Select(Option("--cases") ?? Option("--case") ?? (live ? null : string.Join(',', PlanningCorpus.Names)));
if (Option("--delegated-authorization") is not null || Option("--delegated-review") is not null)
{
    if (!live || Option("--delegated-authorization") is null || Option("--delegated-review") is null ||
        Option("--review-run") is null || Option("--review-command") is not null ||
        names.Length != 1 || names[0] is not ("local" or "collections"))
        throw new ArgumentException("Delegated synthetic review requires one local/collections case, an exact --review-run, and both authorization and review files.");
}
string Git(string arguments) { using var process = Process.Start(new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!; var value = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Cannot identify source revision."); return value; }
var harnessSource = Git("rev-parse HEAD");
var source = Option("--evaluation-source") ?? harnessSource;
if (source != harnessSource)
{
    // A harness-only continuation must retain the exact production tree, corpus,
    // model adapter, accounting and independent oracles of the recorded revision.
    if (source.Length != 40 || !source.All(Uri.IsHexDigit) ||
        Git("diff --name-only " + source + " " + harnessSource).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(path => path is not ("tests/GnOuGo.Agent.Planning.Benchmark/Program.cs" or "tests/GnOuGo.Agent.Planning.Benchmark/README.md")))
        throw new ArgumentException("--evaluation-source permits only runner entry-point/documentation changes; the evaluated source and measurement contracts must be identical.");
}
if (live && Git("status --porcelain").Length != 0) throw new InvalidOperationException("Commit the tested source before live evaluation.");
var workspace = Path.GetFullPath(Option("--workspace") ?? GnOuGoWorkspace.ResolveDefaultWorkingDirectory());
BenchmarkCampaign? evidenceStore = live || readOnly ? new(KeyVaultBenchmarkModel.WorkspaceRecords(workspace), campaignId) : null;
BenchmarkCampaign? campaign = live ? evidenceStore : null;
var leasePath = live || retainKey is not null ? Path.Combine(workspace, ".GnOuGo", "data", "planning-evaluation", campaignId + ".lock") : null;
if (leasePath is not null) Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
await using var lease = leasePath is null ? null : new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
if (retainKey is not null) { Console.WriteLine((await evidenceStore!.RetainInconclusiveAsync(retainKey, CancellationToken.None)).ToJsonString()); return; }
if (inspectCampaign) { Console.WriteLine((await evidenceStore!.InspectAsync()).ToJsonString()); return; }
if (compareParent is not null)
{
    if (cohort is not null) throw new ArgumentException("Use --compact-comparison with exact frozen rows for named profile cohorts.");
    var comparisonVariable = args.Contains("--judge-comparison") ? "judge" : args.Contains("--defaults-comparison") ? "defaults" : null;
    if (args.Contains("--judge-comparison") && args.Contains("--defaults-comparison")) throw new ArgumentException("Compare judge and creation defaults separately.");
    var parentPhase = Option("--parent-phase") ?? "measured"; var candidateSource = Option("--candidate") ?? source;
    var candidatePhase = Option("--candidate-phase") ?? "measured";
    if (candidatePhase is not ("measured" or "fixture")) throw new ArgumentException("Compare measured or live fixture cohorts.");
    var referenceSource = Option("--compare-reference"); var referencePhase = Option("--reference-phase") ?? "fixture";
    var stabilizationSource = Option("--compare-stabilization");
    var before = new List<JsonObject>(); var after = new List<JsonObject>(); var reference = new List<JsonObject>(); var stabilization = new List<JsonObject>();
    foreach (var name in stabilizationSource is null ? PlanningBenchmarkMeasurements.CandidateCases : names)
        for (var repetition = 1; repetition <= 3; repetition++)
        {
            if (await evidenceStore!.LoadAsync("planning-evaluation-runs", $"{compareParent}:{parentPhase}:{name}:{repetition}") is { } old) before.Add(old);
            if (await evidenceStore!.LoadAsync("planning-evaluation-runs", $"{candidateSource}:{candidatePhase}:{(comparisonVariable is null ? "" : comparisonVariable + ":")}{name}:{repetition}") is { } next) after.Add(next);
            if (referenceSource is not null && await evidenceStore.LoadAsync("planning-evaluation-runs", $"{referenceSource}:{referencePhase}:{name}:{repetition}") is { } prior) reference.Add(prior);
            if (stabilizationSource is not null && await evidenceStore.LoadAsync("planning-evaluation-runs", $"{stabilizationSource}:fixture:{name}:{repetition}") is { } stable) stabilization.Add(stable);
        }
    var retainedCase = Option("--retained-case") ?? "review_french";
    JsonObject Compare() => comparisonVariable is not null
        ? PlanningBenchmarkMeasurements.CompareArms(before.Select(r => r["result"]!.AsObject()).ToArray(), after.Select(r => r["result"]!.AsObject()).ToArray(), PlanningBenchmarkMeasurements.CandidateCases, comparisonVariable)
        : args.Contains("--first-proposal-gate")
        ? PlanningBenchmarkMeasurements.CompareFirstProposals(before.Select(r => r["result"]!.AsObject()).ToArray(), after.Select(r => r["result"]!.AsObject()).ToArray(), PlanningBenchmarkMeasurements.CandidateCases)
        : stabilizationSource is null ? PlanningBenchmarkMeasurements.Compare(before, after, retainedCase, referenceSource is null ? null : reference)
        : PlanningBenchmarkMeasurements.CompareBest(names, new Dictionary<string, IReadOnlyList<JsonObject>> { ["parent"] = before, ["previous_candidate"] = reference, ["stabilization"] = stabilization }, after);
    var original = Compare();
    var audits = new JsonArray();
    if (args.Contains("--audit-admission-denials", StringComparer.Ordinal))
        foreach (var auditedRun in before.Concat(after).Concat(reference).Concat(stabilization))
        {
            var row = auditedRun["result"]!;
            var key = $"{row["source_commit"]}:{row["phase"]}:{(row["arm"] is null ? "" : row["arm"] + ":")}{row["case"]}:{row["repetition"]}";
            if (await evidenceStore!.AuditAdmissionDenialAsync(key, auditedRun) is not { } audit) continue;
            // Only detached measurements change; failed outcomes, diagnostics,
            // original records, closures, receipts and the spending ledger do not.
            PlanningBenchmarkMeasurements.RecordUsage(auditedRun, audit["request_id"]!.ToString(), BenchmarkHttpJournal.UndispatchedUsage());
            auditedRun["usage_complete"] = true;
            foreach (var (field, value) in PlanningBenchmarkMeasurements.Usage(auditedRun, true)) row[field] = value?.DeepClone();
            row["calls"] = auditedRun["session"]!["modelCalls"]!.GetValue<int>() + PlanningBenchmarkMeasurements.ExtraTransportCalls(auditedRun);
            audit["audited_result"] = row.DeepClone(); audits.Add((JsonNode)audit);
        }
    if (args.Contains("--audit-closed-http", StringComparer.Ordinal))
        foreach (var auditedRun in before.Concat(after).Concat(reference).Concat(stabilization))
        {
            var row = auditedRun["result"]!;
            var key = $"{row["source_commit"]}:{row["phase"]}:{(row["arm"] is null ? "" : row["arm"] + ":")}{row["case"]}:{row["repetition"]}";
            if (await evidenceStore!.AuditClosedHttpSessionAsync(key, auditedRun) is not { } audit) continue;
            foreach (var (field, value) in audit["audited_usage"]!.AsObject()) row[field] = value?.DeepClone();
            audit["audited_result"] = row.DeepClone(); audits.Add((JsonNode)audit);
        }
    var comparison = Compare();
    comparison["stabilization_commit"] = stabilizationSource;
    if (args.Contains("--audit-admission-denials", StringComparer.Ordinal) || args.Contains("--audit-closed-http", StringComparer.Ordinal))
    {
        comparison["original_comparison"] = original;
        comparison["admission_audits"] = audits;
        comparison["measurement_commit"] = harnessSource;
    }
    comparison["reference_commit"] = referenceSource; comparison["reference_phase"] = referenceSource is null ? null : referencePhase;
    comparison["parent_phase"] = parentPhase; comparison["candidate_phase"] = candidatePhase;
    comparison["parent_commit"] = compareParent; comparison["candidate_commit"] = candidateSource; comparison["campaign"] = campaignId;
    Console.WriteLine(comparison.ToJsonString()); Environment.ExitCode = (comparison[comparisonVariable is null ? "passed" : "comparable"]?.GetValue<bool>() == true) ? 0 : 2; return;
}
if (inspectKey is { } inspect)
{
    var evidence = await evidenceStore!.LoadAsync("planning-evaluation-runs", inspect);
    if (evidence is not null && args.Contains("--include-receipts", StringComparer.Ordinal))
    {
        var calls = new JsonArray();
        var pending = evidence["session"]?["pendingCall"]?["id"]?.ToString();
        foreach (var id in evidence["usage_receipts"]!.AsObject().Select(p => p.Key).Concat(pending is null ? [] : [pending]).Distinct(StringComparer.Ordinal))
            calls.Add((JsonNode)new JsonObject { ["request"] = await evidenceStore.LoadAsync("planning-evaluation-requests", id), ["receipt"] = await evidenceStore.LoadAsync("planning-evaluation-receipts", id),
                ["failure"] = await evidenceStore.LoadAsync("planning-evaluation-failures", id) });
        evidence["model_requests"] = calls;
    }
    Console.WriteLine(evidence?.ToJsonString() ?? "null"); return;
}
(PlanningSession State, ILLMClient Client)? replay = null;
if (replayKey is not null)
{
    try { replay = await evidenceStore!.ReadReplayAsync(replayKey); }
    catch (InvalidOperationException)
    {
        Console.WriteLine(new JsonObject { ["mode"] = "replay", ["diagnostic"] = "REPLAY_UNAVAILABLE", ["live_model_calls"] = 0,
            ["message"] = "The original reservation, response schema and completion receipt are required; no request was dispatched." }.ToJsonString());
        Environment.ExitCode = 2; return;
    }
}
if (replay is { } recorded) { names = PlanningBenchmarkMeasurements.Select(recorded.State.Request.Name); repetitions = 1; }
using var configured = !live || providerName is null ? null : await KeyVaultBenchmarkModel.CreateAsync(providerName, Option("--model"), campaign!, workspace, CancellationToken.None);
ILLMClient? model = replay?.Client ?? (ILLMClient?)configured ?? (!live || command is null ? null : new CommandModel(command));
string RunKey(string runPhase, string name, int repetition)
    => PlanningBenchmarkMeasurements.CorpusRunKey(source, runPhase, arm, cohort, name, repetition);
if (live && phase == "measured")
{
    var pilot = new List<JsonObject>();
    foreach (var name in PlanningBenchmarkMeasurements.CandidateCases)
        if ((await campaign!.LoadAsync("planning-evaluation-runs", RunKey("pilot", name, 1)))?["result"] is JsonObject row) pilot.Add(row);
    if (!PlanningBenchmarkMeasurements.Summary(pilot, "pilot")["gates_passed"]!.GetValue<bool>()) throw new InvalidOperationException("The same revision must pass all eight pilot cases before measured evaluation.");
}
var creation = new PlanningRequest();
if (campaign is not null)
{
    var configurationKey = "corpus-settings:" + source + ":" + phase + ":" + (arm ?? "control") +
        (cohort is null ? "" : ":cohort:" + cohort);
    var configuration = await campaign.LoadAsync("planning-evaluation-configuration", configurationKey);
    if (configuration is null)
        foreach (var name in names)
        {
            for (var i = firstRepetition; i <= repetitions && configuration is null; i++)
                if ((await campaign.LoadAsync("planning-evaluation-runs", RunKey(phase, name, i)))?["session"] is { } saved)
                    configuration = PlanningBenchmarkMeasurements.Configuration(JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)!.Request);
            if (configuration is not null) break;
        }
    if (configuration is not null) creation = PlanningBenchmarkMeasurements.RestoreConfiguration(configuration);
    PlanningBenchmarkMeasurements.ConfigureRequest(creation, args, configuration is not null);
    if (await campaign.LoadAsync("planning-evaluation-configuration", configurationKey) is null)
        await campaign.SaveAsync("planning-evaluation-configuration", configurationKey, PlanningBenchmarkMeasurements.Configuration(creation));
}
else PlanningBenchmarkMeasurements.ConfigureRequest(creation, args, false);
var results = new List<JsonObject>();
for (var repetition = firstRepetition; repetition <= repetitions; repetition++)
foreach (var name in names)
{
    var key = RunKey(phase, name, repetition);
    var run = campaign is null ? null : await campaign.LoadAsync("planning-evaluation-runs", key);
    if (campaign is not null)
        await campaign.RequireRunNotRetainedAsync(run?["session"]?["request"]?["sessionId"]?.ToString()
            ?? PlanningGraphCompiler.Fingerprint(campaignId + ":" + key), CancellationToken.None);
    if (run?["session"] is { } retainedSession)
    {
        var retainedRequest = JsonSerializer.Deserialize(retainedSession, PlanningJsonContext.Default.PlanningSession)!.Request;
        PlanningBenchmarkMeasurements.ConfigureRequest(retainedRequest, args, true);
        if (!JsonNode.DeepEquals(PlanningBenchmarkMeasurements.Configuration(retainedRequest), PlanningBenchmarkMeasurements.Configuration(creation)))
            throw new InvalidOperationException("The cohort contains incompatible frozen settings. Retained evidence is unchanged.");
    }
    if (run?["result"] is JsonObject failedResult && failedResult["termination_reason"] is not null &&
        run["session"]?["pendingCall"]?["id"]?.ToString() is { } pendingId &&
        await campaign!.LoadAsync(BenchmarkHttpJournal.Collection, pendingId) is not null &&
        await campaign.LoadAsync("planning-evaluation-closures", pendingId) is null)
    {
        // Re-enter the same reserved call. The HTTP journal replays its receipt or enforces
        // the remaining attempt allowance; neither session accounting nor evidence is reset.
        (run["previous_results"] ??= new JsonArray()).AsArray().Add(failedResult.DeepClone());
        run.Remove("result"); run["session"]!["status"] = PlanningStatus.Generating;
    }
    if (run?["result"] is JsonObject pendingReview && pendingReview["review_pending"]?.GetValue<bool>() == true && (Option("--review-command") is not null || Option("--delegated-review") is not null) && Option("--review-run") == key)
    {
        (run["previous_results"] ??= new JsonArray()).AsArray().Add(pendingReview.DeepClone()); run.Remove("result");
    }
    if (run?["result"] is JsonObject completed)
    { results.Add(completed); Console.WriteLine(completed.ToJsonString()); if (completed["termination_reason"] is not null) goto Complete; continue; }
    var freshRun = run is null;
    run ??= new JsonObject { ["artifact_oracle_version"] = SyntheticArtifactOracle.Version,
        ["diagnostic_history"] = new JsonArray(), ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true,
        ["first_pass_valid"] = false, ["final_review"] = false, ["elapsed_ms"] = 0L };
    var state = replay?.State ?? (run["session"] is { } saved ? JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)! : new PlanningSession
    { Request = new() { TenantId = "benchmark", SessionId = PlanningGraphCompiler.Fingerprint(campaignId + ":" + key), Name = name, Prompt = PlanningBenchmarkCases.Prompt(name),
        MaxModelCalls = creation.MaxModelCalls, MaxReplanAttempts = creation.MaxReplanAttempts,
        Generation = new() { Reasoning = creation.Generation.Reasoning, MaxInputTokensPerRequest = creation.Generation.MaxInputTokensPerRequest, MaxOutputTokens = creation.Generation.MaxOutputTokens },
        Policy = JsonSerializer.Deserialize(JsonSerializer.Serialize(creation.Policy, PlanningJsonContext.Default.PlanningPolicy), PlanningJsonContext.Default.PlanningPolicy)!,
        Options = (JsonObject)creation.Options.DeepClone() } });
    PlanningBenchmarkMeasurements.ConfigureRequest(state.Request, args, run["session"] is not null || replay is not null);
    if (!live && replay is null && run["session"] is null)
        state = PlanningCorpus.RetainedSession(state); // Offline frozen whole-plan corpus only; live requests remain fresh incremental sessions.
    else if (live && run["session"] is null) PlanningBenchmarkMeasurements.CaptureConstructionSettings(state);
    if (model is KeyVaultBenchmarkModel capturedModel)
    {
        await capturedModel.CapturePlanningPolicyAsync(state.Request, CancellationToken.None,
            state.Construction is not null || run["session"] is null && replay is null, state.ActiveMilliseconds);
        if (state.PendingCall is { } pending) await capturedModel.RecoverSavedResponseAsync(pending.Request, CancellationToken.None);
    }
    var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
    async Task Checkpoint(PlanningSession current, CancellationToken ct)
    {
        run["session"] = JsonSerializer.SerializeToNode(current, PlanningJsonContext.Default.PlanningSession);
        PlanningBenchmarkMeasurements.Capture(run, current);
        if (current.Status == PlanningStatus.FinalReview) run["final_review"] = true;
        // Retain the historical column as an alias, now defined by the first proposal.
        run["first_pass_valid"] = run["first_proposal"]?["structurally_valid"]?.DeepClone() ?? JsonValue.Create(false);
        if (campaign is not null) await campaign.SaveAsync("planning-evaluation-runs", key, run, ct);
    }
    var runtime = new MeasuredRuntime(new PlanningCorpus.Runtime(name, engine), model, run, Checkpoint);
    var planner = new HybridWorkflowPlanner(); var clock = Stopwatch.StartNew(); var execution = false; string? failure = null;
    var variants = new JsonArray(); var safety = new JsonArray();
    string[]? reviewedObligations = null;
    var planningStarted = !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status);
    long? planningMilliseconds = null;
    try
    {
        for (var advance = 0; advance < Math.Max(40, state.Request.MaxModelCalls * 4) && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
        {
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
            state = JsonSerializer.Deserialize(run["session"], PlanningJsonContext.Default.PlanningSession)!;
            if (campaign?.StopReason is not null || live && PlanningBenchmarkMeasurements.Usage(run, true)["usage_bounded"]!.GetValue<bool>() == false) break;
        }
        planningMilliseconds = clock.ElapsedMilliseconds;
        // Capture before approval/execution. Historical or interrupted runs with
        // no separate measurement retain unknown planning duration.
        if (planningStarted && (freshRun || run["planning_ms"] is JsonValue))
            run["planning_ms"] = (run["planning_ms"]?.GetValue<long>() ?? 0) + planningMilliseconds.Value;
        if (state.Status == PlanningStatus.Clarification) state.Diagnostics.Add(new("CLARIFICATION_REQUIRED", "/questions", "The frozen case declares runtime inputs; no evaluation-time answers are supplied."));
        var artifactFindings = Array.Empty<string>();
        if (run["final_review"]!.GetValue<bool>() && run["artifact_oracle_version"] is { } oracleVersion)
        {
            if (oracleVersion.ToString() != SyntheticArtifactOracle.Version)
                throw new InvalidOperationException("Unknown retained synthetic artifact oracle.");
            artifactFindings = SyntheticArtifactOracle.Validate(name, state.Plan).ToArray();
            run["artifact_oracle"] = new JsonObject { ["version"] = SyntheticArtifactOracle.Version,
                ["artifact_hash"] = state.ComputeArtifactHash(),
                ["passed"] = artifactFindings.Length == 0,
                ["findings"] = new JsonArray(artifactFindings.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()) };
            if (artifactFindings.Length > 0)
                state.Diagnostics.Add(new("INDEPENDENT_ARTIFACT_MISMATCH", "/plan",
                    "The artifact fails independent structure or configured runtime checks: " + string.Join(", ", artifactFindings)));
        }
        if (run["final_review"]!.GetValue<bool>() && environment.Effects.Count == 0 && artifactFindings.Length == 0)
        {
            DelegatedTestReviewAuthority? delegated = null;
            if (Option("--review-run") == key && Option("--delegated-review") is { } delegatedFile)
            {
                if (!live || campaign is null || name is not ("local" or "collections") || Option("--review-command") is not null)
                    throw new ArgumentException("Synthetic delegation supports only isolated local/collections cases without a human review command.");
                var authorizationFile = Option("--delegated-authorization") ?? throw new ArgumentException("Supply the frozen delegated authorization.");
                var authorization = JsonSerializer.Deserialize(await File.ReadAllTextAsync(authorizationFile), DelegatedTestJsonContext.Default.DelegatedTestAuthorization)
                    ?? throw new ArgumentException("Missing delegated authorization.");
                var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(delegatedFile), DelegatedTestJsonContext.Default.DelegatedTestReviewDocument)
                    ?? throw new ArgumentException("Missing delegated artifact review.");
                delegated = new(authorization, document, campaignId, state.Request.SessionId, "synthetic",
                    workspace, harnessSource);
                delegated.Validate(state);
                await delegated.PinAsync(campaign, CancellationToken.None);
            }
            if (state.Status == PlanningStatus.FinalReview)
            {
                PlanningCommand? review = delegated?.Command() ?? (Option("--review-run") == key && Option("--review-command") is { } reviewFile
                    ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(reviewFile), PlanningJsonContext.Default.PlanningCommand)
                    : !live && replayKey is null ? new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state),
                        ReviewedRequirementIds = PlanningCorpus.Requirements(name).Outcomes.Select(r => r.Id).ToList() } : null);
                if (review is not null)
                {
                    if (review.Kind != "approve") throw new ArgumentException("The review command must be an explicit approval.");
                    state = await planner.AdvanceAsync(state, review, delegated is null ? runtime : new DelegatedTestReviewAuthority.Runtime(runtime, delegated), CancellationToken.None);
                }
            }
            execution = state.Status == PlanningStatus.Approved;
            if (execution)
            {
                PlanningArtifactApproval.Verify(state, delegated?.Receipt);
                if (delegated is not null) reviewedObligations = DelegatedTestReviewAuthority.Obligations("synthetic");
                if (run["execution_started"] is not null) throw new InvalidOperationException("Execution already started; never repeat an uncertain execution.");
                run["execution_started"] = true;
                if (campaign is not null) await campaign.SaveAsync("planning-evaluation-runs", key, run);
                var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
                var variantsToRun = name.StartsWith("review_", StringComparison.Ordinal) ? new[] { "nominal", "failure", "incomplete", "rejected", "head_changed", "cancelled", "workflow_denied", "permission_unavailable" }
                    : name == "protected_cleanup" ? ["nominal", "failure", "cancelled", "workflow_denied", "permission_unavailable"] : ["nominal", "alternate"];
                foreach (var variant in variantsToRun)
                {
                    using var cancellation = new CancellationTokenSource();
                    var sample = new PlanningBenchmarkCases.Environment(name, variant);
                    if (variant == "cancelled") sample.CancelDuringWork(cancellation);
                    var denied = variant is "workflow_denied" or "permission_unavailable";
                    var runner = new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = delegated is not null || variant == "permission_unavailable" ? null : new PlanningCorpus.Human(!denied) };
                    RunResult? result = null; string? error = null;
                    var runtimeClock = Stopwatch.StartNew();
                    try { result = await runner.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, variant), cancellation.Token); }
                    catch (Exception ex) { error = ex.GetType().Name; }
                    runtimeClock.Stop();
                    var correct = denied ? result?.Success != true && sample.Effects.Count == 0
                        : variant == "cancelled" ? result?.Success != true && sample.Violations.Count == 0 && sample.Effects.LastOrDefault() == (name == "protected_cleanup" ? "cleanup" : "remove_workspace") && !sample.Effects.Contains("publish_review")
                        : result is not null && sample.Verify(result);
                    if (denied && sample.Effects.Count > 0) safety.Add((JsonNode)JsonValue.Create("protected_effect_without_confirmation:" + variant));
                    foreach (var violation in sample.Violations.Where(v => v == "unapproved_draft" || v == "irrelevant_capability")) safety.Add((JsonNode)JsonValue.Create(violation));
                    variants.Add((JsonNode)new JsonObject { ["variant"] = variant, ["correct"] = correct, ["runtime_success"] = result?.Success,
                        ["elapsed_ms"] = runtimeClock.Elapsed.TotalMilliseconds,
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
    if (planningStarted && planningMilliseconds is null && (freshRun || run["planning_ms"] is JsonValue))
        run["planning_ms"] = (run["planning_ms"]?.GetValue<long>() ?? 0) + clock.ElapsedMilliseconds;
    run["elapsed_ms"] = run["elapsed_ms"]!.GetValue<long>() + clock.ElapsedMilliseconds;
    await Checkpoint(state, CancellationToken.None);
    var rowResult = new JsonObject { ["campaign"] = campaignId, ["source_commit"] = source, ["harness_commit"] = harnessSource, ["phase"] = phase,
        ["manifest"] = SchemaPortabilityCampaign.MeasurementManifest(state.Request),
        ["comparison_manifest"] = SchemaPortabilityCampaign.MeasurementManifest(state.Request, comparison: true),
        ["configuration"] = PlanningBenchmarkMeasurements.Configuration(state.Request),
        ["arm"] = arm,
        ["case"] = name, ["repetition"] = repetition, ["session_id"] = state.Request.SessionId, ["mode"] = replayKey is not null ? "replay" : live ? "live" : "fixture",
        ["replay_source_run"] = replayKey, ["live_model_calls"] = replayKey is not null ? 0 : (int?)null,
        ["provider"] = configured?.Provider, ["model"] = configured?.Model, ["first_pass_valid"] = run["first_pass_valid"]!.DeepClone(), ["final_review"] = run["final_review"]!.DeepClone(),
        ["execution_correct"] = execution, ["execution_variants"] = variants, ["safety_violations"] = safety, ["calls"] = state.ModelCalls + PlanningBenchmarkMeasurements.ExtraTransportCalls(run), ["repairs"] = state.ReplanAttempts,
        ["artifact_oracle"] = run["artifact_oracle"]?.DeepClone(),
        ["review_pending"] = state.Status == PlanningStatus.FinalReview && run["artifact_oracle"]?["passed"]?.GetValue<bool>() != false,
        ["logical_planning_calls"] = state.ModelCalls,
        ["physical_attempts"] = PlanningBenchmarkMeasurements.PhysicalAttempts(run, state.ModelCalls),
        ["planning_ms"] = run["planning_ms"]?.DeepClone(),
        ["initial_request_bytes"] = run["initial_request_bytes"]?.DeepClone(), ["initial_estimated_input_tokens"] = run["initial_estimated_input_tokens"]?.DeepClone(),
        ["validation_results"] = state.ValidationResults.Count, ["elapsed_ms"] = run["elapsed_ms"]!.DeepClone(), ["failure"] = failure, ["termination_reason"] = campaign?.StopReason,
        ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => d.Code).Distinct().Select(d => (JsonNode)JsonValue.Create(d)).ToArray()),
        ["failure_history"] = new JsonArray(run["diagnostic_history"]!.AsArray().Select(d => { var entry = d!.DeepClone().AsObject(); entry.Remove("message"); return (JsonNode)entry; }).ToArray()) };
    if (cohort is not null) rowResult["cohort"] = cohort;
    foreach (var (field, value) in PlanningBenchmarkMeasurements.Usage(run, live)) rowResult[field] = value?.DeepClone();
    if (variants.Count == (name.StartsWith("review_", StringComparison.Ordinal) ? 8 : name == "protected_cleanup" ? 5 : 2)) PlanningBenchmarkMeasurements.RecordBusinessOracle(run, state, execution, "existing-business-variants-v1");
    PlanningBenchmarkMeasurements.ProposalMetrics(run, rowResult);
    if (name is "local" or "collections")
        rowResult["useful_work"] = PlanningBenchmarkMeasurements.UsefulWork(rowResult,
            DelegatedTestReviewAuthority.Obligations("synthetic"), reviewedObligations,
            reviewedObligations is null ? null : state.ComputeArtifactHash(), variants.Count == 2 ? execution : null);
    if (live && rowResult["usage_bounded"]?.GetValue<bool>() != true && rowResult["termination_reason"] is null) rowResult["termination_reason"] = "unknown_usage";
    run["result"] = rowResult.DeepClone();
    if (campaign is not null) await campaign.SaveAsync("planning-evaluation-runs", key, run);
    results.Add(rowResult); Console.WriteLine(rowResult.ToJsonString());
    if (rowResult["termination_reason"] is not null || safety.Count > 0) goto Complete;
}
Complete:
if (replayKey is not null)
{ if (results.Any(r => r["execution_correct"]?.GetValue<bool>() != true)) Environment.ExitCode = 1; return; }
var summary = PlanningBenchmarkMeasurements.Summary(results, phase); summary["campaign"] = campaignId; summary["source_commit"] = source; summary["harness_commit"] = harnessSource; summary["first_repetition"] = firstRepetition; summary["last_repetition"] = repetitions;
if (campaign is not null) summary["campaign_accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign);
if (cohort is not null) summary["cohort"] = cohort;
Console.WriteLine(summary.ToJsonString());
if (campaign is not null) await campaign.SaveAsync("planning-evaluation-summaries", source + ":" + phase +
    (cohort is null ? "" : (arm is null ? "" : ":" + arm) + ":cohort:" + cohort) + (firstRepetition == 1 ? "" : ":repetitions:" + firstRepetition + "-" + repetitions), summary);
if (!summary["gates_passed"]!.GetValue<bool>()) Environment.ExitCode = 1;

sealed class MeasuredRuntime(IPlanningRuntime inner, ILLMClient? live, JsonObject run, Func<PlanningSession, CancellationToken, Task> checkpoint) : IPlanningRuntime, IPlanningConstructionAdmission
{
    public Task AdmitConstructionAsync(LLMRequest request, int inputTokenCeiling, IReadOnlyList<int> outputTokenCeilings, CancellationToken ct)
        => live is IPlanningConstructionAdmission admission ? admission.AdmitConstructionAsync(request, inputTokenCeiling, outputTokenCeilings, ct)
            : inner is IPlanningConstructionAdmission fallback ? fallback.AdmitConstructionAsync(request, inputTokenCeiling, outputTokenCeilings, ct) : Task.CompletedTask;
    public ICapabilityCatalog Capabilities => inner.Capabilities;
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => inner.DiscoverAsync(request, ct);
    public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        PlanningBenchmarkMeasurements.RecordRequest(run, request, purpose);
        if (run["initial_request_bytes"] is null)
        {
            run["initial_request_bytes"] = Encoding.UTF8.GetByteCount(request.Prompt ?? "") + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString());
            run["initial_estimated_input_tokens"] = PlanningJsonTransport.EstimateInputTokens(request.Prompt ?? "", request.StructuredOutputSchema!.AsObject());
        }
        var callClock = Stopwatch.StartNew();
        try
        {
            var response = live is null && purpose == "judge" ? new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(), ["uncertainty"] = "Scripted no-findings fixture, not a live evaluation." } }
                : live is null ? await inner.CallAsync(request, purpose, ct) : await live.CallAsync(request, ct);
            PlanningBenchmarkMeasurements.RecordResponse(run, request, response, run["session"]?["modelCalls"]?.GetValue<int>() ?? 0, purpose);
            run["usage_complete"] = true;
            run["last_response"] = JsonSerializer.SerializeToNode(response, PlanningJsonContext.Default.LLMResponse);
            PlanningBenchmarkMeasurements.RecordUsage(run, request.ClientRequestId!, response.Usage as JsonObject);
            return response;
        }
        catch
        {
            JsonObject? partial = null;
            if (live is KeyVaultBenchmarkModel configured)
            {
                partial = await configured.PartialUsageAsync(request.ClientRequestId!, CancellationToken.None);
                PlanningBenchmarkMeasurements.RecordUsage(run, request.ClientRequestId!, partial);
            }
            run["usage_complete"] = partial?["input_tokens"] is not null && partial["output_tokens"] is not null && partial["benchmark_cost_eur"] is not null;
            throw;
        }
        finally { PlanningBenchmarkMeasurements.RecordCall(run, request.ClientRequestId!, purpose, callClock.Elapsed.TotalMilliseconds); }
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => inner.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => inner.ValidateCatalogAsync(catalog, ct);
    public async Task CheckpointAsync(PlanningSession state, CancellationToken ct) { await inner.CheckpointAsync(state, ct); await checkpoint(state, ct); }
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
