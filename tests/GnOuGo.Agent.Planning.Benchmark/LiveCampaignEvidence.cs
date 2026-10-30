using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

internal static class LiveCampaignEvidence
{
    internal static async Task<JsonObject> PinAsync(BenchmarkCampaign campaign, string source, string provider, string model, string configurationFingerprint, string cohort, int productLimit = 3, JsonObject? planningConfiguration = null)
    {
        var manifest = new JsonObject
        {
            ["production_sha"] = source, ["harness_sha"] = source, ["cohort"] = cohort,
            ["provider"] = provider, ["model"] = model, ["reasoning"] = "medium",
            ["host_configuration_hash"] = configurationFingerprint,
            ["max_input_tokens"] = 96000, ["max_output_tokens"] = 32768,
            ["planning_attempts"] = 8, ["repairs"] = 2, ["cost_ceiling_eur"] = await campaign.CostCeilingAsync(),
            ["browser_keep_open"] = false,
            ["os"] = RuntimeInformation.OSDescription, ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["execution_path_hash"] = PlanningGraphCompiler.Fingerprint(Environment.GetEnvironmentVariable("PATH") ?? ""),
            ["amazon_prompt"] = PlanningGraphCompiler.Fingerprint(LiveWorkflowEvaluation.AmazonPrompt),
            ["code_prompt"] = PlanningGraphCompiler.Fingerprint(LiveWorkflowEvaluation.CodePrompt),
            ["harness_tree"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD:tests/GnOuGo.Agent.Planning.Benchmark"),
            ["scenario_count"] = 3, ["repetitions"] = 2, ["oracle_version"] = "real-workflows-v1"
        };
        if (productLimit != 3) manifest["max_products"] = productLimit;
        manifest["planning_format"] = 11;
        if (planningConfiguration is not null)
        {
            manifest["planning_configuration"] = planningConfiguration.DeepClone();
            manifest["planning_attempts"] = planningConfiguration["calls"]!.DeepClone();
            manifest["repairs"] = planningConfiguration["repairs"]!.DeepClone();
            manifest["max_input_tokens"] = planningConfiguration["input_tokens"]!.DeepClone();
            manifest["max_output_tokens"] = planningConfiguration["output_tokens"]!.DeepClone();
        }
        var saved = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, cohort + "-manifest");
        if (saved is not null) RequireMatch(saved, manifest);
        else await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, cohort + "-manifest", manifest);
        return manifest;
    }

    internal static void RequireMatch(JsonObject saved, JsonObject current)
    { if (!JsonNode.DeepEquals(saved, current)) throw new InvalidOperationException("Final cohort source, harness or environment changed. Retain the incomplete cohort; do not pool revisions."); }

    internal static async Task<JsonObject> AccountingAsync(BenchmarkCampaign campaign, string run)
    {
        var prefix = campaign.Id + ":";
        var rows = (await campaign.Records.ListAsync(BenchmarkHttpJournal.Collection, "benchmark", BenchmarkCampaign.Author))
            .Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(r => (Id: r.Key[prefix.Length..], Row: JsonNode.Parse(r.Value)!.AsObject())).ToArray();
        var planning = Summarize(rows.Where(r => r.Id.StartsWith(run + ":", StringComparison.Ordinal)).Select(r => r.Row));
        var execution = Summarize(rows.Where(r => r.Id.StartsWith(run + "-execution:", StringComparison.Ordinal) || r.Id.StartsWith(run + "-copilot:", StringComparison.Ordinal)).Select(r => r.Row));
        return new() { ["planning"] = planning, ["execution"] = execution,
            ["campaign"] = await BenchmarkHttpJournal.AccountingAsync(campaign) };
    }

    internal static JsonObject Summarize(IEnumerable<JsonObject> records)
    {
        long calls = 0, attempts = 0, input = 0, output = 0, unknown = 0, reservedInput = 0, reservedOutput = 0;
        decimal cost = 0, reservedCost = 0;
        foreach (var row in records)
        {
            calls++; var physical = row["transport"]!["Attempts"]!.AsArray(); attempts += physical.Count;
            var usage = row["usage"];
            var pending = physical.Count(a => PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(a?["Status"]?.GetValue<int>(), usage is not null, PlanningBenchmarkMeasurements.RetainsAllHttpFailures(row)));
            unknown += pending;
            reservedInput += pending * row["input_ceiling"]!.GetValue<long>(); reservedOutput += pending * row["output_ceiling"]!.GetValue<long>();
            reservedCost += pending * row["cost_ceiling_eur"]!.GetValue<decimal>();
            if (usage is null) continue;
            input += usage["input_tokens"]!.GetValue<long>(); output += usage["output_tokens"]!.GetValue<long>(); cost += usage["benchmark_cost_eur"]!.GetValue<decimal>();
        }
        return new() { ["logical_calls"] = calls, ["physical_attempts"] = attempts, ["known_input_tokens"] = input, ["known_output_tokens"] = output,
            ["known_cost_eur"] = cost, ["unknown_attempts"] = unknown, ["reserved_input_tokens"] = reservedInput,
            ["reserved_output_tokens"] = reservedOutput, ["reserved_cost_eur"] = reservedCost };
    }

    internal static async Task<JsonObject> ReportAsync(BenchmarkCampaign campaign, string cohort = "final",
        string? source = null, string? amazonCohort = null, string? codeCohort = null, string? collectionsCohort = null)
    {
        var cohorts = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["amazon"] = amazonCohort ?? cohort, ["code"] = codeCohort ?? cohort, ["collections"] = collectionsCohort ?? cohort };
        foreach (var value in cohorts.Values)
            _ = PlanningBenchmarkMeasurements.Cohort(["--cohort", value]);
        var manifests = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (var scenario in new[] { "amazon", "code" })
        {
            var manifest = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, cohorts[scenario] + "-manifest");
            manifests[scenario] = manifest;
            source ??= manifest?["production_sha"]?.ToString();
            if (manifest is not null && (manifest["production_sha"]?.ToString() != source ||
                manifest["harness_sha"]?.ToString() != source))
                throw new InvalidOperationException("Qualification cannot combine different production or harness sources.");
        }
        var prefix = campaign.Id + ":";
        var live = (await campaign.Records.ListAsync(SchemaPortabilityCampaign.Collection, "benchmark", BenchmarkCampaign.Author))
            .Where(r => r.Key.StartsWith(prefix + "run:", StringComparison.Ordinal)).ToArray();
        var corpus = (await campaign.Records.ListAsync("planning-evaluation-runs", "benchmark", BenchmarkCampaign.Author))
            .Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var rows = new JsonArray(); var scenarios = new JsonObject(); var passed = 0;
        foreach (var (scenario, selectedCohort) in cohorts)
        {
            var selected = new List<(int Repetition, string Id, JsonObject Run, JsonObject Profile)>();
            if (scenario == "collections")
            {
                var keyPrefix = prefix + source + ":fixture:cohort:" + selectedCohort + ":collections:";
                var configuration = await campaign.LoadAsync("planning-evaluation-configuration",
                    "corpus-settings:" + source + ":fixture:control:cohort:" + selectedCohort);
                foreach (var record in corpus.Where(r => r.Key.StartsWith(keyPrefix, StringComparison.Ordinal)))
                {
                    var repetition = Repetition(record.Key[keyPrefix.Length..]);
                    var run = JsonNode.Parse(record.Value)!.AsObject(); var result = run["result"] as JsonObject;
                    var key = record.Key[prefix.Length..];
                    if (configuration is null || run["session"]?["request"]?["sessionId"]?.ToString() != PlanningGraphCompiler.Fingerprint(campaign.Id + ":" + key) ||
                        result is not null && (result["source_commit"]?.ToString() != source || result["harness_commit"]?.ToString() != source ||
                        result["case"]?.ToString() != scenario || result["cohort"]?.ToString() != selectedCohort ||
                        result["repetition"]?.GetValue<int>() != repetition || !JsonNode.DeepEquals(result["configuration"], configuration)))
                        throw new InvalidOperationException("Collections qualification requires exact source, identity and frozen configuration.");
                    var profile = new JsonObject { ["configuration"] = configuration.DeepClone(),
                        ["provider"] = result?["provider"]?.DeepClone(), ["model"] = result?["model"]?.DeepClone(),
                        ["manifest"] = result?["manifest"]?.DeepClone(), ["oracle_version"] = run["artifact_oracle_version"]?.DeepClone() };
                    selected.Add((repetition, key, run, profile));
                }
            }
            else
            {
                var keyPrefix = prefix + "run:" + selectedCohort + "-" + scenario + "-";
                foreach (var record in live.Where(r => r.Key.StartsWith(keyPrefix, StringComparison.Ordinal)))
                {
                    var repetition = Repetition(record.Key[keyPrefix.Length..]);
                    var run = JsonNode.Parse(record.Value)!.AsObject(); var manifest = manifests[scenario];
                    if (manifest is null || run["manifest"] is not JsonObject saved)
                        throw new InvalidOperationException("An existing final run has no comparable manifest.");
                    RequireMatch(manifest, saved);
                    if (run["source"]?.ToString() != source || run["label"]?.ToString() != record.Key[(prefix.Length + 4)..] ||
                        run["scenario"]?.ToString() != scenario || run["session"]?["request"]?["sessionId"]?.ToString() != run["label"]?.ToString())
                        throw new InvalidOperationException("Live qualification requires the exact recorded run identity.");
                    selected.Add((repetition, record.Key[(prefix.Length + 4)..], run, manifest));
                }
            }
            selected.Sort((a, b) => a.Repetition.CompareTo(b.Repetition));
            var streak = 0; var previous = 0; JsonObject? previousProfile = null;
            foreach (var attempt in selected)
            {
                var result = attempt.Run["result"]?.DeepClone().AsObject() ?? new JsonObject { ["status"] = "interrupted", ["execution_oracle"] = false };
                if (scenario != "collections" && attempt.Run["execution_started"] is not null && attempt.Run["execution_ms"] is null)
                { result["execution_status"] = "interrupted"; result["execution_oracle"] = false; }
                var qualified = Qualifies(attempt.Run, scenario) && source is { Length: 40 } && source.All(Uri.IsHexDigit) &&
                    attempt.Profile["provider"] is not null && attempt.Profile["model"] is not null;
                streak = qualified ? previous == attempt.Repetition - 1 && JsonNode.DeepEquals(previousProfile, attempt.Profile) ? streak + 1 : 1 : 0;
                previous = attempt.Repetition; previousProfile = attempt.Profile;
                result["run"] = attempt.Id; result["case"] = scenario; result["repetition"] = attempt.Repetition;
                result["qualifying_success"] = qualified;
                result["profile_hash"] = PlanningGraphCompiler.Fingerprint(attempt.Profile.ToJsonString());
                result["accounting"] = await AccountingAsync(campaign, attempt.Run["session"]!["request"]!["sessionId"]!.ToString());
                rows.Add(result);
            }
            for (var missing = selected.Count; missing < 2; missing++)
                rows.Add(new JsonObject { ["case"] = scenario, ["status"] = "not_started", ["qualifying_success"] = false });
            var credited = Math.Min(streak, 2); passed += credited;
            scenarios[scenario] = new JsonObject { ["cohort"] = selectedCohort, ["attempts"] = selected.Count,
                ["consecutive_successes"] = streak, ["required"] = 2, ["qualified"] = streak >= 2 };
        }
        return new() { ["campaign"] = campaign.Id, ["source"] = source, ["passed"] = passed, ["required"] = 6,
            ["complete"] = passed == 6, ["qualification"] = scenarios, ["runs"] = rows,
            ["manifest"] = manifests["amazon"]?.DeepClone(), ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign) };

        static int Repetition(string value) => int.TryParse(value, out var number) && number is >= 1 and <= 20
            ? number : throw new InvalidOperationException("Invalid recorded qualification repetition.");
    }

    private static bool Qualifies(JsonObject run, string scenario)
    {
        var result = run["result"]; var state = run["session"]; var approval = state?["approvalReview"];
        var hash = state?["approvedHash"]?.ToString();
        // These are durable results of the ordinary compiler, mandatory business review,
        // artifact approval and independent runtime oracle, not a second validation engine.
        if (result?["mode"]?.ToString() != "live" || state?["schemaVersion"]?.GetValue<int>() != 11 ||
            state["status"]?.ToString() != PlanningStatus.Approved || hash is not { Length: 64 } ||
            state["plan"] is null || state["graph"] is null || string.IsNullOrWhiteSpace(state["yaml"]?.ToString()) ||
            state["pendingCall"] is not null || state["construction"]?["complete"]?.GetValue<bool>() != true ||
            approval?["origin"]?.ToString() != "delegated_test" || approval["artifactHash"]?.ToString() != hash ||
            run["execution_started"] is null || run["planning_revisions"] is JsonArray { Count: > 0 } ||
            (result["review_revisions"]?.GetValue<int>() ?? 0) != 0 || result["failure"] is not null || run["failure"] is not null)
            return false;
        if (scenario != "collections")
            return run["artifact_hash"]?.ToString() == hash && run["execution_ms"] is not null &&
                run["execution"]?["success"]?.GetValue<bool>() == true && run["oracle"]?["passed"]?.GetValue<bool>() == true &&
                result["execution_status"]?.ToString() == "completed" && result["execution_oracle"]?.GetValue<bool>() == true;
        return result["final_review"]?.GetValue<bool>() == true && result["artifact_oracle"]?["passed"]?.GetValue<bool>() == true &&
            result["execution_correct"]?.GetValue<bool>() == true && result["safety_violations"] is JsonArray { Count: 0 } &&
            result["execution_variants"] is JsonArray { Count: 2 } variants &&
            new[] { "nominal", "alternate" }.All(name => variants.OfType<JsonObject>().Count(v =>
                v["variant"]?.ToString() == name && v["correct"]?.GetValue<bool>() == true &&
                v["runtime_success"]?.GetValue<bool>() == true && v["violations"] is JsonArray { Count: 0 } &&
                v["effects"] is JsonArray { Count: 0 }) == 1);
    }
}
