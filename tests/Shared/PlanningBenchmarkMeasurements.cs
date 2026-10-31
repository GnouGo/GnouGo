using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Planning.Examples;

public static class PlanningBenchmarkMeasurements
{
    public static string CandidateHash(PlanningSession state) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["request"] = state.Request.Prompt,
            ["revision"] = state.Request.RevisionContext,
            ["answers"] = JsonSerializer.SerializeToNode(state.AnswerHistory, PlanningJsonContext.Default.ListPlanningAnswerBatch),
            ["requirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            ["catalog"] = JsonSerializer.SerializeToNode(state.Catalog, PlanningJsonContext.Default.PlanningCatalog),
            ["discovery"] = JsonSerializer.SerializeToNode(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState),
            ["plan"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan) }.ToJsonString())));

    // Captured limits cannot change within an active-format cohort.
    public static void ConfigureRequest(PlanningRequest request, string[] args, bool retained)
    {
        var diagnostic = args.Contains("--diagnostic-optimization-thresholds", StringComparer.Ordinal);
        if (retained && diagnostic && !PlanningTokenThresholds.OptimizationAdvisory(request))
            throw new InvalidOperationException("Optimization policy differs from the frozen run. Start a separate cohort.");
        if (!retained && diagnostic) request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var advisory = args.Contains("--advisory-token-thresholds", StringComparer.Ordinal);
        if (retained && advisory && !PlanningTokenThresholds.Advisory(request))
            throw new InvalidOperationException("Token policy differs from the frozen run. Start a separate cohort.");
        if (!retained && advisory) request.Options[PlanningTokenThresholds.OptionName] = "advisory";
        int? Number(string name, bool zeroAllowed = false)
        {
            var positions = args.Select((value, index) => (value, index)).Where(p => p.value == name).Select(p => p.index).ToArray();
            if (positions.Length == 0) return null;
            if (positions.Length != 1 || !int.TryParse(args.ElementAtOrDefault(positions[0] + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < (zeroAllowed ? 0 : 1))
                throw new ArgumentException("Supply one valid value for " + name + ".");
            return value;
        }
        var calls = Number("--max-model-calls"); var repairs = Number("--max-repairs", true);
        var input = Number("--max-input-tokens"); var output = Number("--max-output-tokens");
        var phaseArguments = args.Select((value, index) => (value, index)).Where(p => p.value == "--phase-policy").ToArray();
        if (phaseArguments.Length > 1) throw new ArgumentException("Supply one phase policy.");
        PlanningConstructionPhasePolicy? selectedPolicy = null;
        if (phaseArguments.Length == 1)
        {
            var file = args.ElementAtOrDefault(phaseArguments[0].index + 1) ?? throw new ArgumentException("Missing phase policy file.");
            var probe = new PlanningRequest { Options = new()
            {
                ["construction_limits"] = new JsonObject { ["phasePolicy"] = JsonNode.Parse(File.ReadAllText(file)) }
            } };
            if (request.Options.TryGetPropertyValue(PlanningTokenThresholds.OptimizationOptionName, out var optimization))
                probe.Options[PlanningTokenThresholds.OptimizationOptionName] = optimization?.DeepClone();
            selectedPolicy = PlanningConstructionPhasePolicies.Read(probe)
                ?? throw new ArgumentException("Supply a complete phase policy.");
        }
        var presetPositions = args.Select((value, index) => (value, index)).Where(p => p.value == "--construction-preset").ToArray();
        if (presetPositions.Length > 1) throw new ArgumentException("Supply one construction preset.");
        var preset = presetPositions.Length == 0 ? null : PlanningConstructionPresets.Validate(
            args.ElementAtOrDefault(presetPositions[0].index + 1) ?? throw new ArgumentException("Missing construction preset."));
        var totalTokens = Number("--max-total-tokens"); var elapsed = Number("--max-elapsed-ms");
        if (totalTokens > 15_000_000 && !PlanningTokenThresholds.Advisory(request) ||
            elapsed > 18_000_000 && !PlanningTokenThresholds.OptimizationAdvisory(request))
            throw new ArgumentException("Cumulative benchmark allowances cannot exceed the authorized creation ceilings.");
        var automatic = args.Contains("--automatic-correction", StringComparer.Ordinal);
        var firstConstructionConfiguration = request.Options["construction_limits"] is null;
        var capturedPreset = request.Options["construction_preset"]?.ToString();
        if (args.Contains("--business-judge", StringComparer.Ordinal) || args.Contains("--no-business-judge", StringComparer.Ordinal))
            throw new ArgumentException("Business review is mandatory and has no configuration switch.");
        if (retained && (calls is not null && calls != request.MaxModelCalls || repairs is not null && repairs != request.MaxReplanAttempts ||
            input is not null && input != request.Generation.MaxInputTokensPerRequest || output is not null && output != request.Generation.MaxOutputTokens ||
            preset is not null && preset != request.Options["construction_preset"]?.ToString() ||
            totalTokens is not null && totalTokens != request.Options["llm_budget"]?["max_total_tokens"]?.GetValue<int>() ||
            elapsed is not null && elapsed != request.Options["llm_budget"]?["max_elapsed_ms"]?.GetValue<int>() ||
            automatic && request.Options["automatic_correction"]?.GetValue<bool>() != true))
            throw new InvalidOperationException("Explicit settings differ from the frozen run. Start a separate cohort.");
        if (retained)
        {
            if (selectedPolicy is not null && !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(PlanningConstructionPhasePolicies.Capture(request, selectedPolicy), PlanningJsonContext.Default.PlanningConstructionPhasePolicy),
                request.Options["construction_limits"]?["phasePolicy"]))
                throw new InvalidOperationException("The phase policy differs from the frozen run. Start a separate cohort.");
            return;
        }
        PlanningTransportPolicies.Capture(request);
        request.Options["construction_preset"] = preset ?? request.Options["construction_preset"]?.ToString() ?? PlanningConstructionPresets.Ordinary;
        request.Options["construction_limits"] ??= JsonSerializer.SerializeToNode(new PlanningConstructionOptions(), PlanningJsonContext.Default.PlanningConstructionOptions);
        request.Options["llm_budget"] ??= new JsonObject { ["max_total_tokens"] = 15_000_000, ["max_elapsed_ms"] = 18_000_000 };
        if (totalTokens is not null) request.Options["llm_budget"]!["max_total_tokens"] = totalTokens.Value;
        if (elapsed is not null) request.Options["llm_budget"]!["max_elapsed_ms"] = elapsed.Value;
        request.Options["benchmark_planning_elapsed_ms"] = (long)(PlanningBudgetOptions.Parse(request.Options)?.MaxElapsed ?? TimeSpan.FromHours(5)).TotalMilliseconds;
        if (preset == PlanningConstructionPresets.Large && (firstConstructionConfiguration || capturedPreset != preset))
        {
            request.MaxModelCalls = PlanningConstructionPresets.LargeModelCalls; request.MaxReplanAttempts = PlanningConstructionPresets.LargeCorrections;
            request.Policy.MaxStepsTotal = PlanningConstructionPresets.LargeGraphNodes;
            request.Policy.MaxStepsPerWorkflow = PlanningConstructionPresets.LargeGraphNodes;
            request.Policy.MaxWorkflows = PlanningConstructionPresets.LargeWorkflows;
        }
        request.MaxModelCalls = calls ?? request.MaxModelCalls; request.MaxReplanAttempts = repairs ?? request.MaxReplanAttempts;
        request.Generation.MaxInputTokensPerRequest = input ?? request.Generation.MaxInputTokensPerRequest;
        request.Generation.MaxOutputTokens = output ?? Math.Min(request.Generation.MaxOutputTokens, 8192);
        var phasePolicy = selectedPolicy ?? PlanningConstructionPhasePolicies.Read(request) ?? new PlanningConstructionPhasePolicy
        {
            Navigation = new() { TargetInputTokens = 3_000, MaxInputTokens = 6_000, Reasoning = request.Generation.Reasoning },
            Draft = new() { TargetInputTokens = 8_000, MaxInputTokens = 16_000, Reasoning = request.Generation.Reasoning },
            Review = new() { TargetInputTokens = 4_000, MaxInputTokens = 8_000, Reasoning = request.Generation.Reasoning, OutputTokens = 2_048 }
        };
        request.Options["construction_limits"]!["maxNonProductiveModelCalls"] = 6;
        request.Options["construction_limits"]!["phasePolicy"] = JsonSerializer.SerializeToNode(
            PlanningConstructionPhasePolicies.Capture(request, phasePolicy), PlanningJsonContext.Default.PlanningConstructionPhasePolicy);
        request.Options["construction_limits"] = JsonSerializer.SerializeToNode(
            request.Options["construction_limits"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionOptions)!,
            PlanningJsonContext.Default.PlanningConstructionOptions);
        if (automatic) request.Options["automatic_correction"] = true;
    }

    public static JsonObject Configuration(PlanningRequest request)
    {
        var result = new JsonObject
        {
            ["planning_format"] = 11,
            ["calls"] = request.MaxModelCalls, ["repairs"] = request.MaxReplanAttempts,
            ["input_tokens"] = request.Generation.MaxInputTokensPerRequest, ["output_tokens"] = request.Generation.MaxOutputTokens,
            ["reasoning"] = request.Generation.Reasoning
        };
        foreach (var key in new[] { "construction_preset", "construction_limits", "llm_budget", "automatic_correction",
            PlanningTransportPolicies.OptionName, "benchmark_planning_elapsed_ms", PlanningTokenThresholds.OptionName, PlanningTokenThresholds.OptimizationOptionName })
            if (request.Options[key] is { } value) result[key] = value.DeepClone();
        result["policy"] = JsonSerializer.SerializeToNode(request.Policy, PlanningJsonContext.Default.PlanningPolicy);
        if (PhasePolicyHash(request) is { } profileHash) result["phase_policy_hash"] = profileHash;
        return result;
    }

    public static TimeSpan PlanningElapsedLimit(PlanningRequest request)
    {
        var captured = request.Options["benchmark_planning_elapsed_ms"];
        var milliseconds = captured?.GetValue<long>() ?? throw new InvalidOperationException("The captured benchmark planning duration is required.");
        if (milliseconds <= 0 || milliseconds > 18_000_000 && !PlanningTokenThresholds.OptimizationAdvisory(request))
            throw new InvalidOperationException("The captured benchmark planning duration is invalid.");
        var host = TimeSpan.FromMilliseconds(milliseconds);
        return PlanningBudgetOptions.Parse(request.Options)?.MaxElapsed is { } planner && planner < host ? planner : host;
    }

    public static TimeSpan RemainingPlanningTime(PlanningSession state, double priorMilliseconds, double interruptedMilliseconds = 0)
    {
        if (!double.IsFinite(priorMilliseconds) || !double.IsFinite(interruptedMilliseconds) || priorMilliseconds < 0 || interruptedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(priorMilliseconds));
        var elapsed = Math.Max(priorMilliseconds, state.ActiveMilliseconds) + interruptedMilliseconds;
        var target = PlanningElapsedLimit(state.Request);
        if (PlanningTokenThresholds.OptimizationAdvisory(state.Request))
        {
            if (elapsed >= target.TotalMilliseconds)
                OptimizationWarning(state, "elapsed_ms", checked((long)elapsed), checked((long)target.TotalMilliseconds));
            return Timeout.InfiniteTimeSpan;
        }
        var remaining = target - TimeSpan.FromMilliseconds(elapsed);
        if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("Active planning time is exhausted; continuation cannot reset it.");
        return remaining;
    }

    public static bool CanAdvance(PlanningSession state, long advances)
    {
        if (advances < 0) throw new ArgumentOutOfRangeException(nameof(advances));
        if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) return false;
        var target = Math.Max(40L, (long)state.Request.MaxModelCalls * 4);
        if (advances < target) return true;
        if (!PlanningTokenThresholds.OptimizationAdvisory(state.Request)) return false;
        OptimizationWarning(state, "host_advances", checked(advances + 1), target);
        return true;
    }

    private static void OptimizationWarning(PlanningSession state, string kind, long current, long threshold)
    {
        PlanningTokenThresholds.Warn("benchmark_host", kind, current, threshold);
        var warning = new PlanningThresholdWarning(kind, current, threshold, "retained_host_measurement", "benchmark_host")
        {
            SessionId = state.Request.SessionId, RequestId = state.PendingCall?.Id,
            BlockId = state.Construction?.Compact?.CurrentBlockId, StepId = state.Construction?.Compact?.CurrentStep?.Id, Checkpoint = state.Construction?.Checkpoints.Count
        };
        var warnings = state.ThresholdWarnings ??= [];
        if (!warnings.Contains(warning)) warnings.Add(warning);
    }

    public static string? Cohort(string[] args)
    {
        var positions = args.Select((value, index) => (value, index)).Where(p => p.value == "--cohort").ToArray();
        if (positions.Length == 0) return null;
        var value = positions.Length == 1 ? args.ElementAtOrDefault(positions[0].index + 1) : null;
        if (string.IsNullOrEmpty(value) || value.Length > 30 || !value.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Supply one alphanumeric cohort identity of at most 30 characters.");
        return value;
    }

    public static string CorpusRunKey(string source, string phase, string? arm, string? cohort, string name, int repetition)
        => source + ":" + phase + ":" + (arm is null ? "" : arm + ":") +
            (cohort is null ? "" : "cohort:" + cohort + ":") + name + ":" + repetition;

    public static string? PhasePolicyHash(PlanningRequest request)
        => PlanningConstructionPhasePolicies.Read(request) is { } policy
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(policy, PlanningJsonContext.Default.PlanningConstructionPhasePolicy)))) : null;

    public static PlanningRequest RestoreConfiguration(JsonObject settings)
    {
        if (settings["planning_format"]?.GetValue<int>() != 11)
            throw new InvalidOperationException("The frozen configuration belongs to a retired planning format. Start a fresh cohort; its archive is unchanged.");
        var result = new PlanningRequest
        {
            MaxModelCalls = settings["calls"]!.GetValue<int>(), MaxReplanAttempts = settings["repairs"]!.GetValue<int>(),
            Generation = new() { Reasoning = settings["reasoning"]!.ToString(), MaxInputTokensPerRequest = settings["input_tokens"]!.GetValue<int>(), MaxOutputTokens = settings["output_tokens"]!.GetValue<int>() },
            Policy = settings["policy"]!.Deserialize(PlanningJsonContext.Default.PlanningPolicy)!
        };
        foreach (var key in new[] { "construction_preset", "construction_limits", "llm_budget", "automatic_correction",
            PlanningTransportPolicies.OptionName, "benchmark_planning_elapsed_ms", PlanningTokenThresholds.OptionName, PlanningTokenThresholds.OptimizationOptionName })
            if (settings[key] is { } value) result.Options[key] = value.DeepClone();
        return result;
    }

    public static void CaptureConstructionSettings(PlanningSession state)
    {
        if (state.SchemaVersion != 11) throw new InvalidOperationException("Only the active planning format can construct work.");
        if (state.Construction is null && state.Revision == 0 && state.ModelCalls == 0 && state.PendingCall is null && state.Plan is null &&
            state.Request.Options["construction_limits"] is { } limits)
            state.Construction = new() { Limits = limits.Deserialize(PlanningJsonContext.Default.PlanningConstructionOptions)!, Compact = new() };
    }

    public static void RecordBusinessOracle(JsonObject run, PlanningSession state, bool correct, string oracleVersion)
    {
        var hash = CandidateHash(state);
        foreach (var candidate in (run["candidate_reviews"] as JsonArray ?? []).OfType<JsonObject>().Where(c => c["candidate_hash"]?.ToString() == hash))
        { candidate["business_correct"] = correct; candidate["oracle_version"] = oracleVersion; }
        if (run["first_proposal"] is JsonObject first && first["candidate_hash"]?.ToString() == hash)
            first["business_correct"] = correct;
    }

    // Read-only reporting over authored, frozen business obligations. The caller
    // supplies independently reviewed artifact coverage, never planner task,
    // requirement or checkpoint counts. Runtime acceptance is a separate result.
    public static JsonObject UsefulWork(JsonObject measurements, IReadOnlyList<string> frozenObligations,
        IReadOnlyList<string>? acceptedArtifactObligations, string? artifactHash, bool? runtimeAccepted)
    {
        if (frozenObligations.Any(string.IsNullOrWhiteSpace) || acceptedArtifactObligations?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Business obligation identities must be nonempty.");
        var original = frozenObligations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var accepted = acceptedArtifactObligations?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (accepted?.Any(id => !original.Contains(id, StringComparer.Ordinal)) == true ||
            accepted is { Length: > 0 } && string.IsNullOrWhiteSpace(artifactHash))
            throw new ArgumentException("Reviewed obligations must belong to the frozen set and identify their accepted artifact.");
        var count = accepted?.Length;
        decimal? Number(string key) => decimal.TryParse(measurements[key]?.ToJsonString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
        decimal? Per(decimal? value) => count is > 0 && value is not null ? value / count.Value : null;
        var input = Number("input_tokens"); var output = Number("output_tokens");
        var identities = new JsonArray(original.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return new()
        {
            ["unit"] = "distinct frozen business obligation covered by independently accepted planned artifact, with retained review uncertainty",
            ["obligation_ids"] = identities,
            ["obligation_set_hash"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identities.ToJsonString()))),
            ["accepted_artifact_hash"] = accepted is { Length: > 0 } ? artifactHash : null,
            ["accepted_obligation_ids"] = accepted is null ? null : new JsonArray(accepted.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["accepted_obligations"] = count, ["runtime_accepted"] = runtimeAccepted,
            ["per_accepted_obligation"] = new JsonObject
            {
                ["logical_calls"] = Per(Number("logical_planning_calls")), ["physical_attempts"] = Per(Number("physical_attempts")),
                ["provider_input_tokens"] = Per(input),
                ["provider_output_tokens"] = Per(output), ["provider_total_tokens"] = Per(input + output),
                ["cost_eur"] = Per(Number("estimated_cost_eur")), ["planning_ms"] = Per(Number("planning_ms"))
            },
            ["interpretation"] = "Artifact review establishes planned obligation coverage with its retained uncertainty; it does not prove execution. Runtime acceptance is reported independently. Zero/unknown coverage or unknown usage yields null ratios. Reasoning is already included in provider output tokens. No planner state, accounting or authority is changed."
        };
    }

    public static long? PhysicalAttempts(JsonObject run, int expectedPlanningCalls)
    {
        if (run["usage_receipts"] is not JsonObject receipts || receipts.Count != expectedPlanningCalls) return null;
        long total = 0;
        foreach (var receipt in receipts.Select(p => p.Value))
        {
            if (receipt?["transport_attempts"] is not JsonValue value || !value.TryGetValue<int>(out var attempts) || attempts < 0) return null;
            total += attempts;
        }
        return total;
    }

    public static JsonObject JudgeMetrics(JsonObject run)
    {
        var candidates = (run["candidate_reviews"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var labeled = candidates.Where(c => c["business_correct"] is JsonValue && c["judge_findings"] is JsonValue).ToArray();
        int Count(bool correct, bool flagged) => labeled.Count(c => c["business_correct"]!.GetValue<bool>() == correct && (c["judge_findings"]!.GetValue<int>() > 0) == flagged);
        var final = run.ContainsKey("current_candidate_hash")
            ? candidates.SingleOrDefault(c => c["candidate_hash"]?.ToString() == run["current_candidate_hash"]?.ToString())
            : candidates.LastOrDefault();
        return new() { ["unit"] = "independently labeled candidate, not individual diagnosis", ["candidates"] = candidates.Length,
            ["true_positives"] = Count(false, true), ["false_positives"] = Count(true, true),
            ["false_negatives"] = Count(false, false), ["true_negatives"] = Count(true, false),
            ["unknown_candidates"] = candidates.Count(c => c["business_correct"] is null),
            ["unjudged_candidates"] = candidates.Count(c => c["judge_findings"] is null),
            ["converged"] = final?["business_correct"]?.DeepClone(),
            ["introduced_regression"] = final?["business_correct"] is JsonValue correctness
                ? JsonValue.Create(!correctness.GetValue<bool>() && candidates.Any(c => c["business_correct"]?.GetValue<bool>() == true)) : null,
            ["stop_status"] = run["current_status"]?.DeepClone() };
    }

    public static void RecordCall(JsonObject run, string id, string purpose, double elapsedMilliseconds)
    {
        var calls = (run["call_stages"] ??= new JsonObject()).AsObject();
        var previous = calls[id]?["elapsed_ms"]?.GetValue<double>() ?? 0;
        calls[id] = new JsonObject { ["purpose"] = purpose, ["elapsed_ms"] = previous + elapsedMilliseconds };
    }

    public static void RecordRequest(JsonObject run, LLMRequest request, string purpose)
    {
        var planning = run["session"]?["request"]?.Deserialize(PlanningJsonContext.Default.PlanningRequest);
        var limit = planning is null ? 32_000 : PlanningConstructionInputPolicy.EffectiveLimit(planning, purpose);
        var measurement = PlanningRequestMeasurements.Measure(request, purpose, limit,
            run["session"]?["construction"]?["compact"]?["currentStep"]?["id"]?.ToString());
        measurement.Admission = "admitted";
        var measured = (run["request_measurements"] ??= new JsonObject()).AsObject();
        measured.TryAdd(measurement.RequestHash, JsonSerializer.SerializeToNode(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement));
    }

    public static JsonObject RequestMetrics(JsonObject run, JsonObject? accounting = null)
    {
        var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var measurement in (run["request_measurements"] as JsonObject ?? []).Select(p => p.Value).OfType<JsonObject>())
            records[measurement["requestHash"]!.ToString()] = measurement.DeepClone().AsObject();
        foreach (var measurement in (run["session"]?["requestMeasurements"] as JsonArray ?? []).OfType<JsonObject>())
            records[measurement["requestHash"]!.ToString()] = measurement.DeepClone().AsObject();
        foreach (var measurement in records.Values)
        {
            if (measurement["requestId"]?.ToString() is not { Length: > 0 } id) continue;
            var usage = run["usage_receipts"]?[id];
            var response = run["response_measurements"]?[id];
            measurement["provider_input_tokens"] = (usage?["input_tokens"] ?? measurement["inputTokens"])?.DeepClone();
            measurement["provider_output_tokens"] = (usage?["output_tokens"] ?? measurement["outputTokens"])?.DeepClone();
            measurement["provider_reasoning_tokens"] = (response?["reasoning_tokens"] ?? measurement["reasoningTokens"])?.DeepClone();
            measurement["known_cost_eur"] = usage?["benchmark_cost_eur"]?.DeepClone();
            measurement["retained_reservation_eur"] = usage?["reserved_cost_eur"]?.DeepClone();
            measurement["physical_attempts"] = usage?["transport_attempts"]?.DeepClone();
            measurement["elapsed_ms"] = run["call_stages"]?[id]?["elapsed_ms"]?.DeepClone();
            measurement["completion_status"] = (response?["completion_status"] ?? measurement["result"])?.DeepClone();
        }
        var result = RequestMetricsForRecords(records.Values.ToArray());
        var warnings = result["warnings"]!.AsArray();
        var combined = new JsonArray();
        foreach (var row in warnings.OfType<JsonObject>()) Add(row, "request");
        foreach (var warning in (run["session"]?["thresholdWarnings"] as JsonArray ?? []).OfType<JsonObject>())
            Add(new JsonObject { ["request_id"] = warning["requestId"]?.DeepClone(), ["phase"] = warning["stage"]?.DeepClone(),
                ["block"] = warning["blockId"]?.DeepClone(), ["step"] = warning["stepId"]?.DeepClone(), ["warning"] = warning.DeepClone() }, "session");
        accounting ??= run["result"]?["accounting"] as JsonObject;
        foreach (var scope in new[] { "planning", "execution" })
            foreach (var warning in (accounting?[scope]?["warnings"] as JsonArray ?? []).OfType<JsonObject>())
                Add(new JsonObject { ["request_id"] = warning["request_id"]?.DeepClone(), ["phase"] = warning["stage"]?.DeepClone(),
                    ["warning"] = warning.DeepClone() }, "http_" + scope);
        result["warnings"] = combined;
        return result;

        void Add(JsonObject row, string origin)
        {
            var warning = row["warning"]!.AsObject();
            var id = row["request_id"]?.ToString();
            var existing = combined.OfType<JsonObject>().FirstOrDefault(previous =>
                previous["request_id"]?.ToString() == id &&
                new[] { "kind", "current", "threshold", "stage" }.All(key => JsonNode.DeepEquals(previous["warning"]?[key], warning[key])) &&
                (id is not null || new[] { "sessionId", "blockId", "stepId", "checkpoint" }.All(key => JsonNode.DeepEquals(previous["warning"]?[key], warning[key]))));
            if (existing is null)
            {
                existing = row.DeepClone().AsObject(); existing["origins"] = new JsonArray(origin); combined.Add(existing);
            }
            else
            {
                var origins = existing["origins"]!.AsArray();
                if (!origins.Any(value => value?.ToString() == origin)) origins.Add(origin);
                foreach (var (key, value) in warning)
                    if (existing["warning"]![key] is null) existing["warning"]![key] = value?.DeepClone();
            }
        }
    }

    private static JsonObject RequestMetricsForRecords(IReadOnlyList<JsonObject> records)
    {
        var productive = records.Where(m => (m["transitions"] as JsonArray ?? []).Any(t => t?["productiveChanges"]?.GetValue<int>() > 0)).ToArray();
        var knownBlocks = productive.All(record => record["blockId"] is JsonValue);
        var phases = new JsonObject();
        foreach (var group in records.GroupBy(m => m["phase"]!.ToString()))
        {
            var prepared = group.ToArray();
            var admitted = group.Where(m => m["admission"]?.ToString() == "admitted").ToArray();
            var sizes = prepared.Select(m => m["estimatedInputTokens"]!.GetValue<long>()).Order().ToArray();
            var bytes = prepared.Select(m => m["completeRequestBytes"]!.GetValue<long>()).Order().ToArray();
            var components = new JsonObject();
            foreach (var component in prepared.SelectMany(m => m["components"]!.AsArray().OfType<JsonObject>()).GroupBy(c => c["component"]!.ToString()))
                components[component.Key] = new JsonObject
                {
                    ["serialized_bytes"] = component.Sum(c => c["serializedBytes"]!.GetValue<long>()),
                    ["estimated_tokens"] = component.Sum(c => c["estimatedTokens"]!.GetValue<long>())
                };
            phases[group.Key] = new JsonObject
            {
                ["prepared_requests"] = prepared.Length, ["requests"] = admitted.Length,
                ["rejected_before_dispatch"] = prepared.Count(m => m["admission"]?.ToString() == "rejected_before_dispatch"),
                ["above_target_warnings"] = prepared.Count(m => m["targetWarning"] is not null),
                ["structured_warnings"] = prepared.Sum(m => (m["warnings"] as JsonArray)?.Count ?? 0),
                ["productive_transitions"] = admitted.Sum(m => (m["transitions"] as JsonArray ?? []).Count(t => t?["kind"]?.ToString() == "executable_behavior")),
                ["peak_complete_request_bytes"] = prepared.Max(m => m["completeRequestBytes"]!.GetValue<long>()),
                ["p50_complete_request_bytes"] = (bytes[(bytes.Length - 1) / 2] + bytes[bytes.Length / 2]) / 2.0,
                ["p95_complete_request_bytes"] = bytes[(int)Math.Ceiling(bytes.Length * .95) - 1],
                ["peak_estimated_input_tokens"] = sizes[^1],
                ["peak_admitted_request_bytes"] = admitted.Length == 0 ? null : admitted.Max(m => m["completeRequestBytes"]!.GetValue<long>()),
                ["peak_admitted_input_tokens"] = admitted.Length == 0 ? null : admitted.Max(m => m["estimatedInputTokens"]!.GetValue<long>()),
                ["p50_estimated_input_tokens"] = sizes.Length == 0 ? null : (sizes[(sizes.Length - 1) / 2] + sizes[sizes.Length / 2]) / 2.0,
                ["p95_estimated_input_tokens"] = sizes.Length == 0 ? null : sizes[(int)Math.Ceiling(sizes.Length * .95) - 1],
                ["within_captured_limits"] = prepared.All(m => m["estimatedInputTokens"]!.GetValue<long>() <= m["inputLimit"]!.GetValue<int>()),
                ["admitted_within_captured_limits"] = admitted.All(m => m["estimatedInputTokens"]!.GetValue<long>() <= m["inputLimit"]!.GetValue<int>()),
                ["components"] = components
            };
        }
        return new()
        {
            ["estimator"] = "serialized_utf8_div3_plus256_v1", ["phases"] = phases,
            ["productive_business_blocks"] = knownBlocks ? productive.Select(m => m["blockId"]!.ToString()).Distinct(StringComparer.Ordinal).Count() : (int?)null,
            ["business_block_identity_complete"] = knownBlocks,
            ["productive_implementation_steps"] = productive.Select(m => m["stepId"]?.ToString()).OfType<string>().Distinct(StringComparer.Ordinal).Count(),
            ["warnings"] = new JsonArray(records.SelectMany(m => (m["warnings"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(warning => (JsonNode)new JsonObject { ["request_id"] = m["requestId"]?.DeepClone(),
                    ["phase"] = m["phase"]?.DeepClone(), ["block"] = m["blockId"]?.DeepClone(), ["step"] = m["stepId"]?.DeepClone(), ["warning"] = warning.DeepClone() })).ToArray()),
            ["requests"] = new JsonArray(records.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["interpretation"] = "Peaks, percentiles and component sums include all prepared requests, including rejected requests. Requests counts admitted requests; admitted peaks and limits are explicit. Component estimates describe serialized request bytes, not billed usage. Judge is a phase; component token rounding is not additive. Provider input/output usage is measured for each whole call."
        };
    }

    // Compact construction intentionally spends more small calls. Historical
    // first-proposal and lower-call-count gates do not apply to this comparison.
    public static JsonObject CompareCompactConstruction(IReadOnlyList<JsonObject> before, IReadOnlyList<JsonObject> after, IReadOnlyList<string> cases,
        JsonObject? comparisonPolicy = null)
    {
        var rows = before.Concat(after).ToArray();
        bool Complete(IReadOnlyList<JsonObject> arm) => cases.Count > 0 && arm.Count == cases.Count * 3 &&
            cases.All(name => Enumerable.Range(1, 3).All(i => arm.Count(r => r["case"]?.ToString() == name && r["repetition"]?.GetValue<int>() == i) == 1)) &&
            arm.Select(r => r["source_commit"]?.ToString()).Distinct(StringComparer.Ordinal).Count() == 1 && arm.All(r => !string.IsNullOrEmpty(r["source_commit"]?.ToString()));
        var complete = Complete(before) && Complete(after);
        var frozen = rows.Length > 0 && new[] { "comparison_manifest", "provider", "model", "campaign" }.All(key =>
            rows.All(r => !string.IsNullOrEmpty(r[key]?.ToString())) && rows.Select(r => r[key]!.ToString()).Distinct(StringComparer.Ordinal).Count() == 1);
        var settings = rows.Select(r => r["configuration"] as JsonObject).ToArray();
        var matchedAllowances = settings.Length > 0 && settings.All(s => s is not null) &&
            new[] { "calls", "repairs", "input_tokens", "output_tokens", "reasoning", "llm_budget", "policy" }.All(key =>
                settings.All(s => s![key] is not null && JsonNode.DeepEquals(s[key], settings[0]![key])));
        JsonObject PhaseGeneration(JsonObject configuration)
        {
            var request = RestoreConfiguration(configuration);
            var phases = new JsonObject();
            foreach (var purpose in new[] { "construction_select", "construction_draft", "construction_local_judge" })
            {
                var resolved = PlanningConstructionPhasePolicies.Resolve(request, purpose);
                phases[purpose] = new JsonObject { ["reasoning"] = resolved.Reasoning, ["output_tokens"] = resolved.OutputTokens };
            }
            return phases;
        }
        if (matchedAllowances)
        {
            var first = PhaseGeneration(settings[0]!);
            matchedAllowances = settings.All(s => JsonNode.DeepEquals(first, PhaseGeneration(s!)));
        }
        JsonObject? FrozenSettings(IReadOnlyList<JsonObject> arm)
            => arm.Count > 0 && arm[0]["configuration"] is JsonObject first &&
                new[] { "calls", "repairs", "input_tokens", "output_tokens", "reasoning", "llm_budget", "policy" }.All(k => first[k] is not null) &&
                arm.All(r => JsonNode.DeepEquals(first, r["configuration"])) ? first : null;
        var baselineSettings = FrozenSettings(before); var candidateSettings = FrozenSettings(after);
        var frozenSettings = baselineSettings is not null && candidateSettings is not null;
        var sharedAllowances = frozenSettings && new[] { "calls", "repairs", "llm_budget", "policy" }
            .All(k => JsonNode.DeepEquals(baselineSettings![k], candidateSettings![k]));
        JsonNode? StructuralLimits(JsonObject? configuration)
        {
            var limits = configuration?["construction_limits"]?.DeepClone() as JsonObject;
            foreach (var key in new[] { "phasePolicy", "inputLimits", "draftOutputTokens", "reviewOutputTokens" }) limits?.Remove(key);
            return limits;
        }
        sharedAllowances &= JsonNode.DeepEquals(StructuralLimits(baselineSettings), StructuralLimits(candidateSettings));
        var intentionalDifferences = new JsonArray();
        if (frozenSettings)
            foreach (var key in baselineSettings!.Select(p => p.Key).Concat(candidateSettings!.Select(p => p.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                if (!JsonNode.DeepEquals(baselineSettings![key], candidateSettings![key]))
                    intentionalDifferences.Add(new JsonObject { ["setting"] = key, ["baseline"] = baselineSettings[key]?.DeepClone(),
                        ["candidate"] = candidateSettings[key]?.DeepClone() });
        var validatedConfigurations = comparisonPolicy is not null;
        if (validatedConfigurations && (comparisonPolicy!["version"]?.GetValue<int>() != 2 ||
            comparisonPolicy["mode"]?.ToString() != "validated-configurations"))
            throw new ArgumentException("Unsupported compact comparison policy.");
        var declaredConfigurations = validatedConfigurations && frozenSettings &&
            comparisonPolicy!["comparison_manifest"]?.ToString() == rows[0]["comparison_manifest"]?.ToString() &&
            JsonNode.DeepEquals(comparisonPolicy["baseline_configuration"], baselineSettings) &&
            JsonNode.DeepEquals(comparisonPolicy["candidate_configuration"], candidateSettings);
        var observed = rows.All(r => r["mode"]?.ToString() == "live" && r["independent_business_success"] is JsonValue value && value.TryGetValue<bool>(out _) &&
            r["safety_violations"] is JsonArray && r["request_metrics"]?["requests"] is JsonArray { Count: > 0 });
        var knownUsage = rows.Length > 0 && rows.All(r => new[] { "calls", "input_tokens", "output_tokens", "estimated_cost_eur", "elapsed_ms" }.All(key =>
            decimal.TryParse(r[key]?.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0));
        var comparable = complete && frozen && frozenSettings && observed && knownUsage &&
            (validatedConfigurations ? declaredConfigurations && sharedAllowances : matchedAllowances);
        var business = after.Count > 0 && after.All(r => r["independent_business_success"]?.GetValue<bool>() == true);
        var safe = after.All(r => r["safety_violations"] is JsonArray { Count: 0 });
        var bounded = after.All(r => (r["request_metrics"]?["requests"] as JsonArray ?? []).OfType<JsonObject>()
            .All(m => m["estimatedInputTokens"]!.GetValue<long>() <= m["inputLimit"]!.GetValue<int>()));
        JsonObject Summary(IReadOnlyList<JsonObject> arm)
        {
            var result = new JsonObject { ["runs"] = arm.Count, ["business_successes"] = arm.Count(r => r["independent_business_success"]?.GetValue<bool>() == true) };
            foreach (var key in new[] { "calls", "input_tokens", "output_tokens", "estimated_cost_eur", "elapsed_ms" })
            {
                var numbers = arm.Select(r => decimal.TryParse(r[key]?.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? (decimal?)value : null).ToArray();
                var sorted = numbers.OfType<decimal>().Order().ToArray();
                var allKnown = sorted.Length > 0 && sorted.Length == numbers.Length;
                result[key] = new JsonObject
                {
                    ["known_runs"] = sorted.Length, ["known_sum"] = sorted.Sum(),
                    ["total"] = allKnown ? sorted.Sum() : null,
                    ["median"] = allKnown ? (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2 : null,
                    ["minimum"] = allKnown ? sorted[0] : null, ["maximum"] = allKnown ? sorted[^1] : null,
                    ["p95"] = allKnown ? sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] : null
                };
            }
            // Receipt measurements deduplicate within one retained run. Equal
            // payload hashes in separate runs still represent separate requests.
            var requests = arm.SelectMany(r => (r["request_metrics"]?["requests"] as JsonArray ?? []).OfType<JsonObject>()
                .GroupBy(m => m["requestHash"]!.ToString(), StringComparer.Ordinal).Select(g => g.Last())).ToArray();
            var admitted = requests.Where(m => m["admission"]?.ToString() == "admitted").ToArray();
            var peaks = requests.Select(m => m["estimatedInputTokens"]!.GetValue<long>()).Order().ToArray();
            result["prepared_request_count"] = requests.Length;
            result["request_count"] = admitted.Length;
            result["rejected_before_dispatch"] = requests.Count(m => m["admission"]?.ToString() == "rejected_before_dispatch");
            result["peak_complete_request_bytes"] = requests.Length == 0 ? null : requests.Max(m => m["completeRequestBytes"]!.GetValue<long>());
            result["peak_estimated_input_tokens"] = peaks.Length == 0 ? null : peaks[^1];
            result["peak_admitted_input_tokens"] = admitted.Length == 0 ? null : admitted.Max(m => m["estimatedInputTokens"]!.GetValue<long>());
            result["p50_estimated_input_tokens"] = peaks.Length == 0 ? null : (peaks[(peaks.Length - 1) / 2] + peaks[peaks.Length / 2]) / 2.0;
            result["p95_estimated_input_tokens"] = peaks.Length == 0 ? null : peaks[(int)Math.Ceiling(peaks.Length * .95) - 1];
            result["request_phases"] = RequestMetricsForRecords(requests)["phases"]!.DeepClone();
            return result;
        }
        var baseline = Summary(before); var candidate = Summary(after);
        var perCase = new JsonObject();
        var phaseFamiliesImproved = true;
        foreach (var name in cases)
        {
            var oldRuns = before.Where(r => r["case"]?.ToString() == name).ToArray();
            var newRuns = after.Where(r => r["case"]?.ToString() == name).ToArray();
            var families = new JsonObject();
            foreach (var (family, purposes) in new[]
            {
                ("generation", new[] { "construction_draft", "tasks" }),
                ("judge", new[] { "construction_local_judge", "construction_global_judge" })
            })
            {
                JsonObject[] Measurements(IEnumerable<JsonObject> arm) => arm.SelectMany(r =>
                    (r["request_metrics"]?["requests"] as JsonArray ?? []).OfType<JsonObject>()
                        .GroupBy(m => m["requestHash"]!.ToString(), StringComparer.Ordinal).Select(g => g.Last()))
                    .Where(m => purposes.Contains(m["phase"]!.ToString(), StringComparer.Ordinal)).ToArray();
                var oldRequests = Measurements(oldRuns); var newRequests = Measurements(newRuns);
                long? Peak(JsonObject[] values) => values.Length == 0 ? null : values.Max(m => m["estimatedInputTokens"]!.GetValue<long>());
                var oldPeak = Peak(oldRequests); var newPeak = Peak(newRequests);
                var improved = oldPeak is null || newPeak is not null && newPeak.Value < oldPeak.Value;
                phaseFamiliesImproved &= improved;
                families[family] = new JsonObject
                {
                    ["status"] = oldPeak is null ? "not_comparable" : newPeak is null ? "missing_candidate_evidence" : improved ? "passed" : "failed",
                    ["reason"] = oldPeak is null ? "No baseline request in this phase family." : newPeak is null ? "Baseline phase evidence has no candidate counterpart." : null,
                    ["baseline_prepared_requests"] = oldRequests.Length, ["candidate_prepared_requests"] = newRequests.Length,
                    ["baseline_peak_estimated_input_tokens"] = oldPeak, ["candidate_peak_estimated_input_tokens"] = newPeak,
                    ["baseline_input_limits"] = new JsonArray(oldRequests.Select(m => m["inputLimit"]!.GetValue<int>()).Distinct().Order().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    ["candidate_input_limits"] = new JsonArray(newRequests.Select(m => m["inputLimit"]!.GetValue<int>()).Distinct().Order().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    ["candidate_within_captured_limits"] = newRequests.All(m => m["estimatedInputTokens"]!.GetValue<long>() <= m["inputLimit"]!.GetValue<int>()),
                    ["peak_reduction"] = oldPeak is > 0 && newPeak is not null ? 1 - newPeak.Value / (double)oldPeak.Value : (double?)null
                };
            }
            perCase[name] = new JsonObject
            {
                ["baseline"] = Summary(oldRuns), ["candidate"] = Summary(newRuns), ["phase_comparisons"] = families
            };
        }
        var reduction = baseline["peak_estimated_input_tokens"]?.GetValue<long>() is > 0 and var peak && candidate["peak_estimated_input_tokens"] is { } current
            ? 1 - current.GetValue<long>() / (double)peak : (double?)null;
        var passed = comparable && business && safe && bounded && reduction > 0 && phaseFamiliesImproved;
        return new() { ["status"] = !comparable ? "inconclusive" : passed ? "passed" : "failed", ["passed"] = passed,
            ["complete_three_repetitions"] = complete, ["frozen_comparison_context"] = frozen, ["matched_cumulative_and_output_allowances"] = matchedAllowances,
            ["comparison_mode"] = validatedConfigurations ? "validated-configurations" : "matched-settings",
            ["frozen_settings_within_each_arm"] = frozenSettings, ["shared_cumulative_and_safety_allowances"] = sharedAllowances,
            ["declared_configurations_match"] = validatedConfigurations ? declaredConfigurations : (bool?)null,
            ["baseline_configuration"] = baselineSettings?.DeepClone(), ["candidate_configuration"] = candidateSettings?.DeepClone(),
            ["intentional_setting_differences"] = intentionalDifferences,
            ["independent_outcomes_known"] = observed, ["known_usage"] = knownUsage, ["all_candidate_business_outcomes_pass"] = business, ["no_safety_regression"] = safe,
            ["within_captured_phase_limits"] = bounded, ["comparable_phase_families_improved"] = phaseFamiliesImproved,
            ["peak_request_reduction"] = reduction, ["baseline"] = baseline, ["candidate"] = candidate, ["cases"] = perCase,
            ["interpretation"] = validatedConfigurations
                ? "Original baseline configuration versus validated candidate configuration. Intentional settings differ; this is not an architecture-only causal experiment. Compare completion before cost and latency; failed runs are not completed-work efficiency. Three repetitions are descriptive evidence, not a general reliability claim."
                : "More small calls are permitted. Incomplete, mixed-context or unknown-outcome cohorts are inconclusive; three repetitions are descriptive evidence, not a general reliability claim." };
    }

    private static void CaptureCandidate(JsonObject run, PlanningSession state)
    {
        run["current_status"] = state.Status;
        if (state.Plan is null || state.Graph is null) { run["current_candidate_hash"] = null; return; }
        if (state.PendingCall is not null) return;
        var hash = CandidateHash(state);
        run["current_candidate_hash"] = hash;
        var candidates = (run["candidate_reviews"] ??= new JsonArray()).AsArray();
        var candidate = candidates.OfType<JsonObject>().LastOrDefault(c => c["candidate_hash"]?.ToString() == hash);
        if (candidate is null)
        {
            candidate = new JsonObject { ["candidate_hash"] = hash, ["call"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts,
                ["business_correct"] = null, ["judge_findings"] = null,
                ["plan"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan) };
            candidates.Add((JsonNode)candidate);
        }
        var summary = state.ValidationResults.SingleOrDefault(r => r.Id == "judge:summary");
        if (summary is not null)
        {
            candidate["judge_status"] = summary.Outcome;
            if (summary.Outcome is "unverified" or "revision_required")
                candidate["judge_findings"] = state.ValidationResults.Count(r => r.Id.StartsWith("judge:", StringComparison.Ordinal) && r.Id != "judge:summary");
            else candidate["judge_findings"] = null;
        }
    }
    // This gate is separate from historical execution/review comparisons. Unknown
    // business judgments or usage never count as successes or zero-cost failures.
    public static JsonObject CompareFirstProposals(IReadOnlyList<JsonObject> before, IReadOnlyList<JsonObject> after, IReadOnlyList<string> cases)
    {
        var rows = before.Concat(after).ToArray();
        bool Complete(IReadOnlyList<JsonObject> cohort) => cohort.Count == cases.Count * 3 &&
            cases.All(name => Enumerable.Range(1, 3).All(i => cohort.Count(r => r["case"]?.ToString() == name && r["repetition"]?.GetValue<int>() == i) == 1)) &&
            cohort.Select(r => r["source_commit"]?.ToString() ?? r["source"]?.ToString()).Distinct().Count() == 1 &&
            cohort.All(r => !string.IsNullOrWhiteSpace(r["source_commit"]?.ToString() ?? r["source"]?.ToString()) &&
                r["mode"]?.ToString() == "live" && r["first_proposal_business_correct"] is JsonValue value && value.TryGetValue<bool>(out _));
        var complete = cases.Count > 0 && Complete(before) && Complete(after);
        var frozen = rows.Length > 0 && new[] { "manifest", "provider", "model", "campaign" }.All(key =>
            rows.All(r => !string.IsNullOrWhiteSpace(r[key]?.ToString())) && rows.Select(r => r[key]!.ToString()).Distinct().Count() == 1);
        frozen &= rows.All(r => r["configuration"] is null) || rows.All(r => r["configuration"] is not null && JsonNode.DeepEquals(r["configuration"], rows[0]["configuration"]));
        double? Median(IReadOnlyList<JsonObject> cohort, bool tokens)
        {
            double? Value(JsonObject r) => tokens
                ? Number(r["input_tokens"]) is { } input && Number(r["output_tokens"]) is { } output ? input + output : null
                : Number(r["calls"]);
            var values = cohort.Select(Value).ToArray();
            if (values.Length == 0 || values.Any(v => v is null)) return null;
            var sorted = values.Select(v => v!.Value).Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
        var oldCalls = Median(before, false); var newCalls = Median(after, false); var oldTokens = Median(before, true); var newTokens = Median(after, true);
        var known = oldCalls is not null && newCalls is not null && oldTokens is not null && newTokens is not null;
        var failuresBefore = before.Count(r => r["first_proposal_business_correct"]?.GetValue<bool>() == false);
        var failuresAfter = after.Count(r => r["first_proposal_business_correct"]?.GetValue<bool>() == false);
        double? reduction = !complete || failuresBefore == 0 ? null : (double)(failuresBefore - failuresAfter) / failuresBefore;
        var safe = after.All(r => r["safety_violations"] is JsonArray { Count: 0 });
        var comparable = complete && frozen && known && rows.All(r => r["safety_violations"] is JsonArray);
        var passed = comparable && reduction >= .25 && safe && newCalls <= oldCalls && newTokens <= oldTokens;
        return new() { ["status"] = !comparable || failuresBefore == 0 && failuresAfter == 0 ? "inconclusive" : passed ? "passed" : "failed", ["passed"] = passed,
            ["complete_three_repetitions"] = complete, ["frozen_context"] = frozen, ["known_usage"] = known,
            ["baseline_failures"] = failuresBefore, ["candidate_failures"] = failuresAfter, ["relative_failure_reduction"] = reduction,
            ["zero_baseline_preserved"] = failuresBefore == 0 ? failuresAfter == 0 : null,
            ["baseline_median_calls"] = oldCalls, ["candidate_median_calls"] = newCalls, ["baseline_median_tokens"] = oldTokens, ["candidate_median_tokens"] = newTokens,
            ["no_safety_regression"] = safe, ["baseline_failure_interval_95"] = Interval(failuresBefore, before.Count), ["candidate_failure_interval_95"] = Interval(failuresAfter, after.Count),
            ["uncertainty"] = "Descriptive Wilson intervals; three repetitions per case are a small, case-dependent sample, not a general reliability estimate." };

        static double? Number(JsonNode? n) => double.TryParse(n?.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
        static JsonNode? Interval(int failures, int count)
        {
            if (count == 0) return null;
            const double z = 1.959963984540054; var p = (double)failures / count; var denominator = 1 + z * z / count;
            var center = (p + z * z / (2 * count)) / denominator; var radius = z * Math.Sqrt(p * (1 - p) / count + z * z / (4 * count * count)) / denominator;
            return new JsonArray(center - radius, center + radius);
        }
    }
    public static void RecordResponse(JsonObject run, LLMRequest request, LLMResponse response, int call, string? purpose = null)
    {
        var responseMeasurements = (run["response_measurements"] ??= new JsonObject()).AsObject();
        responseMeasurements[request.ClientRequestId ?? "call:" + call] = new JsonObject
        {
            ["purpose"] = purpose,
            ["completion_status"] = response.CompletionStatus,
            ["content_bytes"] = System.Text.Encoding.UTF8.GetByteCount(response.Json?.ToJsonString() ?? response.Text ?? ""),
            ["reasoning_tokens"] = (response.Usage?["reasoning_tokens"] ?? response.Usage?["output_tokens_details"]?["reasoning_tokens"] ??
                response.Usage?["completion_tokens_details"]?["reasoning_tokens"])?.DeepClone()
        };
        if (purpose is not (null or "tasks" or "replan") || run["first_proposal"] is not null || response.Json is not JsonObject json || json["plan"] is null) return;
        run["first_proposal"] = new JsonObject { ["request_id"] = request.ClientRequestId, ["call"] = call,
            ["response"] = json.DeepClone(), ["structurally_valid"] = null, ["business_correct"] = null,
            ["omitted_requirements"] = null, ["extra_operations"] = null, ["redundant_tasks"] = null };
    }

    public static void ProposalMetrics(JsonObject run, JsonObject result)
    {
        result["request_metrics"] = RequestMetrics(run, result["accounting"] as JsonObject);
        result["response_metrics"] = run["response_measurements"]?.DeepClone();
        var first = run["first_proposal"];
        result["first_proposal_call"] = first?["call"]?.DeepClone();
        result["first_proposal_structurally_valid"] = first?["structurally_valid"]?.DeepClone();
        result["first_proposal_business_correct"] = first?["business_correct"]?.DeepClone();
        result["omitted_requirements"] = first?["omitted_requirements"]?.DeepClone();
        result["extra_operations"] = first?["extra_operations"]?.DeepClone();
        result["redundant_tasks"] = first?["redundant_tasks"]?.DeepClone();
        result["first_proposal_diagnostics"] = first?["diagnostics"]?.DeepClone();
        result["business_review"] = JudgeMetrics(run);
        var stages = new JsonObject();
        foreach (var group in (run["call_stages"] as JsonObject ?? []).GroupBy(p => p.Value!["purpose"]!.ToString()))
        {
            var receipts = new JsonObject(group.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, run["usage_receipts"]?[p.Key]?.DeepClone())));
            var stage = Usage(new JsonObject { ["usage_receipts"] = receipts, ["usage_complete"] = true }, result["mode"]?.ToString() == "live");
            stage["logical_calls"] = group.Count(); stage["elapsed_ms"] = group.Sum(p => p.Value!["elapsed_ms"]!.GetValue<double>());
            stage["physical_attempts"] = group.All(p => run["usage_receipts"]?[p.Key]?["transport_attempts"] is not null)
                ? JsonValue.Create(group.Sum(p => run["usage_receipts"]![p.Key]!["transport_attempts"]!.GetValue<int>())) : null;
            stages[group.Key] = stage;
        }
        result["planning_stages"] = stages;
    }
    public static readonly string[] CandidateCases = ["local", "read_transform", "nullable_defaults", "conditional", "collections", "protected_cleanup", "review_french", "review_distractors"];
    public static string[] Select(string? selection)
    {
        var names = selection?.Split(',', StringSplitOptions.TrimEntries) ?? CandidateCases;
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !PlanningBenchmarkCases.Names.Contains(n, StringComparer.Ordinal) &&
            !PlanningBenchmarkCases.ProofCases.Contains(n, StringComparer.Ordinal)))
            throw new ArgumentException("Select distinct frozen benchmark cases.");
        return names;
    }
    // Codes supply a provisional diagnosis; reproduction and business evidence establish the final classification.
    public static string Category(string code) => code switch
    {
        "MODEL_OUTPUT_LIMIT" => "received_output_exhaustion",
        "MODEL_DISPATCH_UNVERIFIABLE" or "LLM_BUDGET_UNVERIFIABLE" => "provider_transport_failure",
        "PLANNING_HOST_CONTRACT" or "TASK_COMPILER_VALIDATION" => "deterministic_builder_defect",
        "HOLE_UNRESOLVED" or "SCHEMA_UNESTABLISHED" => "type_inference_limitation",
        "CAPABILITY_NOT_EXPOSED" => "capability_retrieval_miss",
        "INDEPENDENT_EXECUTION_MISMATCH" or "CLARIFICATION_REQUIRED" => "semantic_misunderstanding",
        "ORACLE_DEFECT" or "UNSAFE_APPROVAL" => "validator_defect",
        _ => "invalid_intent"
    };
    public static void Capture(JsonObject run, PlanningSession state)
    {
        CaptureCandidate(run, state);
        // A reservation still carries the preceding candidate's diagnostics. Do not
        // attribute those to the new call before its response has been validated.
        if (state.PendingCall is not null && state.Status == PlanningStatus.Generating) return;
        if (run["first_proposal"] is JsonObject first && first["diagnostics"] is null &&
            first["call"]?.GetValue<int>() == state.ModelCalls && state.PendingCall is null)
        {
            first["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic);
            first["structurally_valid"] = state.Graph is not null && !state.Diagnostics.Any(d => d.Required);
            first["final_review"] = state.Status == PlanningStatus.FinalReview;
            first["artifact_hash"] = state.ComputeArtifactHash();
            if (state.Plan is not null) first["candidate_hash"] = CandidateHash(state);
        }
        var history = run["diagnostic_history"]!.AsArray();
        foreach (var finding in state.Diagnostics.Where(d => d.Required))
        {
            var entry = new JsonObject { ["calls"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts, ["code"] = finding.Code, ["location"] = finding.Location,
                ["message"] = finding.Message, ["category"] = Category(finding.Code), ["classification_status"] = "provisional" };
            if (!history.Any(existing => JsonNode.DeepEquals(existing, entry))) history.Add((JsonNode)entry);
        }
    }
    public static void RecordUsage(JsonObject run, string requestId, JsonObject? usage)
    {
        var receipts = run["usage_receipts"]!.AsObject();
        receipts[requestId] = usage?.DeepClone(); // Replay replaces the same receipt instead of charging twice.
    }
    public static JsonObject Usage(JsonObject run, bool live)
    {
        long input = 0, output = 0, reservedInput = 0, reservedOutput = 0; decimal cost = 0, reservedCost = 0; var bounded = true; var complete = run["usage_complete"]!.GetValue<bool>();
        foreach (var receipt in run["usage_receipts"]!.AsObject().Select(p => p.Value))
        {
            long? Tokens(params string[] keys) => keys.Select(k => receipt?[k]).OfType<JsonValue>().Select(v => v.TryGetValue<long>(out var n) ? (long?)n : v.TryGetValue<int>(out var small) ? small : null).FirstOrDefault(n => n is not null);
            var i = Tokens("input_tokens", "prompt_tokens", "inputTokens"); var o = Tokens("output_tokens", "completion_tokens", "outputTokens");
            input += i ?? 0; output += o ?? 0;
            var priced = receipt?["benchmark_cost_eur"] is JsonValue value && value.TryGetValue<decimal>(out var amount);
            if (receipt?["benchmark_cost_eur"] is JsonValue price && price.TryGetValue<decimal>(out var known)) cost += known;
            var usageKnown = i is not null && o is not null && priced;
            var uncertain = Tokens("uncertain_attempts") ?? 0;
            reservedInput += Tokens("reserved_input_tokens") ?? 0; reservedOutput += Tokens("reserved_output_tokens") ?? 0;
            reservedCost += receipt?["reserved_cost_eur"]?.GetValue<decimal>() ?? 0;
            complete &= usageKnown && uncertain == 0;
            bounded &= usageKnown && (uncertain == 0 || receipt?["benchmark_usage_bounded"]?.GetValue<bool>() == true);
        }
        return new() { ["input_tokens"] = live && complete ? input : null, ["output_tokens"] = live && complete ? output : null,
            ["known_input_tokens"] = live ? input : null, ["known_output_tokens"] = live ? output : null, ["estimated_cost_eur"] = live && complete ? cost : null,
            ["known_cost_eur"] = live ? cost : null, ["usage_complete"] = live ? complete : null,
            ["usage_bounded"] = live ? bounded && run["usage_complete"]!.GetValue<bool>() : null,
            ["reserved_input_tokens"] = live ? reservedInput : null, ["reserved_output_tokens"] = live ? reservedOutput : null,
            ["reserved_cost_eur"] = live ? reservedCost : null };
    }
    public static int ExtraTransportCalls(JsonObject run) => run["usage_receipts"]!.AsObject()
        // ModelCalls includes logical reservations. An empty durable HTTP journal
        // proves that reservation never became a physical attempt.
        .Sum(p => Math.Max(0, p.Value?["transport_attempts"]?.GetValue<int>() ?? 1) - 1);

    public static JsonObject Compare(IReadOnlyList<JsonObject> parent, IReadOnlyList<JsonObject> candidate, string retainedCase, IReadOnlyList<JsonObject>? reference = null)
    {
        if (!CandidateCases.Contains(retainedCase, StringComparer.Ordinal)) throw new ArgumentException("Choose a frozen retained case.");
        JsonObject[] Rows(IReadOnlyList<JsonObject> runs) => runs.Select(r => r["result"]).OfType<JsonObject>().ToArray();
        var before = Rows(parent); var after = Rows(candidate); var previous = Rows(reference ?? []);
        bool Complete(JsonObject[] rows) => Summary(rows, "measured")["coverage_complete"]!.GetValue<bool>() && rows.All(r => r["mode"]?.ToString() == "live");
        var complete = Complete(before) && Complete(after) && (reference is null || Complete(previous));
        var all = parent.Concat(candidate).Concat(reference ?? []).ToArray(); var rows = before.Concat(after).Concat(previous).ToArray();
        var sameModel = rows.Length > 0 && new[] { "provider", "model", "campaign" }.All(key =>
            rows.All(r => !string.IsNullOrWhiteSpace(r[key]?.ToString())) && rows.Select(r => r[key]!.ToString()).Distinct(StringComparer.Ordinal).Count() == 1);
        JsonObject Limits(JsonObject run)
        {
            var request = run["session"]?["request"]; var generation = request?["generation"];
            return new() { ["calls"] = request?["maxModelCalls"]?.DeepClone(), ["repairs"] = request?["maxReplanAttempts"]?.DeepClone(),
                ["reasoning"] = generation?["reasoning"]?.DeepClone(), ["input"] = generation?["maxInputTokensPerRequest"]?.DeepClone(), ["output"] = generation?["maxOutputTokens"]?.DeepClone() };
        }
        var limits = all.Select(Limits).ToArray();
        var sameLimits = limits.Length > 0 && limits.All(l => l.All(p => p.Value is not null) && JsonNode.DeepEquals(l, limits[0]));
        int Correct(IEnumerable<JsonObject> values) => values.Count(r => r["execution_correct"]?.GetValue<bool>() == true);
        var perCase = new JsonArray(CandidateCases.Select(name => (JsonNode)new JsonObject
        { ["case"] = name, ["parent_correct"] = Correct(before.Where(r => r["case"]!.ToString() == name)),
            ["candidate_correct"] = Correct(after.Where(r => r["case"]!.ToString() == name)),
            ["reference_correct"] = reference is null ? null : Correct(previous.Where(r => r["case"]!.ToString() == name)) }).ToArray());
        var noRegression = perCase.All(r => r!["candidate_correct"]!.GetValue<int>() >= r["parent_correct"]!.GetValue<int>());
        var noReferenceRegression = reference is null || perCase.All(r => r!["candidate_correct"]!.GetValue<int>() >= r["reference_correct"]!.GetValue<int>());
        var retained = perCase.Single(r => r!["case"]!.ToString() == retainedCase)!;
        var improved = retained["parent_correct"]!.GetValue<int>() < 3 && retained["candidate_correct"]!.GetValue<int>() > retained["parent_correct"]!.GetValue<int>();
        var parentMedian = Summary(before, "measured")["median_calls"]?.GetValue<double>();
        var candidateMedian = Summary(after, "measured")["median_calls"]?.GetValue<double>();
        var reduced = candidateMedian is not null && parentMedian is not null && candidateMedian < parentMedian;
        var safe = after.All(r => r["safety_violations"]!.AsArray().Count == 0 && (r["final_review"]?.GetValue<bool>() != true || r["execution_correct"]?.GetValue<bool>() == true));
        var bounded = rows.All(r => r["usage_bounded"]?.GetValue<bool>() == true);
        var comparable = complete && sameModel && sameLimits && bounded;
        var passed = comparable && noRegression && noReferenceRegression && improved && reduced && safe;
        return new() { ["status"] = !comparable ? "inconclusive" : passed ? "passed" : "failed", ["passed"] = passed,
            ["complete_three_repetitions"] = complete, ["same_model_and_campaign"] = sameModel, ["same_limits"] = sameLimits, ["usage_bounded"] = bounded,
            ["parent_correct"] = Correct(before), ["candidate_correct"] = Correct(after), ["no_case_regression"] = noRegression,
            ["reference_correct"] = reference is null ? null : Correct(previous), ["no_reference_case_regression"] = reference is null ? null : noReferenceRegression,
            ["reference_median_calls"] = reference is null ? null : Summary(previous, "measured")["median_calls"]?.DeepClone(),
            ["retained_case"] = retainedCase, ["retained_case_improved"] = improved,
            ["parent_median_calls"] = parentMedian, ["candidate_median_calls"] = candidateMedian, ["median_calls_reduced"] = reduced,
            ["approved_execution_correct_and_safe"] = after.Any(r => r["execution_variants"] is JsonArray { Count: > 0 }) ? safe : (bool?)null,
            ["cases"] = perCase };
    }

    // A server failure does not establish whether provider work was billed.
    // A later successful receipt settles only its own attempt.
    public static bool RequiresHttpUsageReservation(int? status, bool hasVerifiedUsage, bool retainAllFailures = false) =>
        retainAllFailures ? !hasVerifiedUsage || status is not (>= 200 and < 300)
            : status is null or >= 500 and < 600 || !hasVerifiedUsage && status is >= 200 and < 300;

    public static bool RetainsAllHttpFailures(JsonObject journal) => journal["effective_transport_policy"]?["version"]?.GetValue<int>() == 2;

    /// <summary>Derives bounded partial measurements from complete durable HTTP journals; never fabricates observed tokens.</summary>
    public static JsonObject? ClosedHttpUsage(IReadOnlyList<JsonObject> journals, int expectedAttempts)
    {
        long calls = 0, input = 0, output = 0, reservedInput = 0, reservedOutput = 0, uncertain = 0;
        decimal cost = 0, reservedCost = 0;
        foreach (var journal in journals)
        {
            if (journal["transport"]?["Attempts"] is not JsonArray attempts || journal["input_ceiling"]?.GetValue<long>() is not > 0 ||
                journal["output_ceiling"]?.GetValue<long>() is not > 0 || journal["cost_ceiling_eur"]?.GetValue<decimal>() is not > 0) return null;
            calls += attempts.Count;
            var usage = journal["usage"];
            if (usage is not null)
            {
                if (usage["input_tokens"]?.GetValue<long>() is not >= 0 || usage["output_tokens"]?.GetValue<long>() is not >= 0 || usage["benchmark_cost_eur"]?.GetValue<decimal>() is not >= 0 ||
                    attempts.LastOrDefault()?["Status"]?.GetValue<int>() is not (>= 200 and < 300)) return null;
                input += usage["input_tokens"]!.GetValue<long>(); output += usage["output_tokens"]!.GetValue<long>(); cost += usage["benchmark_cost_eur"]!.GetValue<decimal>();
            }
            var unknown = attempts.Count(a => RequiresHttpUsageReservation(a?["Status"]?.GetValue<int>(), usage is not null, RetainsAllHttpFailures(journal)));
            uncertain += unknown;
            reservedInput += unknown * journal["input_ceiling"]!.GetValue<long>(); reservedOutput += unknown * journal["output_ceiling"]!.GetValue<long>();
            reservedCost += unknown * journal["cost_ceiling_eur"]!.GetValue<decimal>();
        }
        if (calls != expectedAttempts || journals.Count == 0) return null;
        return new() { ["calls"] = checked((int)calls), ["input_tokens"] = uncertain == 0 ? input : null, ["output_tokens"] = uncertain == 0 ? output : null,
            ["known_input_tokens"] = input, ["known_output_tokens"] = output, ["known_cost_eur"] = cost, ["estimated_cost_eur"] = uncertain == 0 ? cost : null,
            ["usage_complete"] = uncertain == 0, ["usage_bounded"] = true, ["reserved_input_tokens"] = reservedInput, ["reserved_output_tokens"] = reservedOutput, ["reserved_cost_eur"] = reservedCost };
    }

    /// <summary>Per-case correctness against every retained baseline; efficiency is reported separately.</summary>
    public static JsonObject CompareBest(IReadOnlyList<string> cases, IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> baselines, IReadOnlyList<JsonObject> candidate)
    {
        if (cases.Count == 0 || cases.Distinct(StringComparer.Ordinal).Count() != cases.Count || cases.Any(c => !CandidateCases.Contains(c)) || baselines.Count == 0)
            throw new ArgumentException("Declare distinct frozen cases and retained baselines.");
        var cohorts = baselines.Concat(new[] { new KeyValuePair<string, IReadOnlyList<JsonObject>>("candidate", candidate) }).ToArray();
        var runs = cohorts.SelectMany(c => c.Value).ToArray(); var rows = runs.Select(r => r["result"] as JsonObject).OfType<JsonObject>().ToArray();
        bool Complete(IReadOnlyList<JsonObject> values) => values.Count == cases.Count * 3 && values.All(v => v["result"] is JsonObject) &&
            cases.All(name => Enumerable.Range(1, 3).All(i => values.Count(v => v["result"]!["case"]?.ToString() == name && v["result"]!["repetition"]?.GetValue<int>() == i) == 1)) &&
            values.All(v => v["result"]!["mode"]?.ToString() == "live") && values.Select(v => v["result"]!["source_commit"]?.ToString()).Distinct().Count() == 1;
        var complete = cohorts.All(c => Complete(c.Value));
        var sameModel = rows.Length > 0 && new[] { "provider", "model", "campaign" }.All(k => rows.All(r => !string.IsNullOrWhiteSpace(r[k]?.ToString())) && rows.Select(r => r[k]!.ToString()).Distinct().Count() == 1);
        JsonObject Limits(JsonObject run) => new() { ["calls"] = run["session"]?["request"]?["maxModelCalls"]?.DeepClone(), ["repairs"] = run["session"]?["request"]?["maxReplanAttempts"]?.DeepClone(),
            ["generation"] = run["session"]?["request"]?["generation"]?.DeepClone() };
        var limits = runs.Select(Limits).ToArray();
        var sameLimits = limits.Length > 0 && limits.All(l => l.All(p => p.Value is not null) && JsonNode.DeepEquals(l, limits[0]));
        var bounded = rows.Length > 0 && rows.All(r => r["usage_bounded"]?.GetValue<bool>() == true);
        int Correct(IEnumerable<JsonObject> values, string name) => values.Count(v => v["result"]?["case"]?.ToString() == name && v["result"]?["execution_correct"]?.GetValue<bool>() == true);
        var comparisons = new JsonArray(cases.Select(name => (JsonNode)new JsonObject
        {
            ["case"] = name, ["baselines"] = new JsonObject(baselines.Select(b => new KeyValuePair<string, JsonNode?>(b.Key, JsonValue.Create(Correct(b.Value, name))))),
            ["best_retained_correct"] = baselines.Max(b => Correct(b.Value, name)), ["candidate_correct"] = Correct(candidate, name)
        }).ToArray());
        var noRegression = comparisons.All(c => c!["candidate_correct"]!.GetValue<int>() >= c["best_retained_correct"]!.GetValue<int>());
        var safe = candidate.All(v => v["result"]?["safety_violations"] is JsonArray { Count: 0 } &&
            (v["result"]?["final_review"]?.GetValue<bool>() != true || v["result"]?["execution_correct"]?.GetValue<bool>() == true));
        var comparable = complete && sameModel && sameLimits && bounded;
        return new() { ["status"] = !comparable ? "inconclusive" : noRegression && safe ? "passed" : "failed", ["passed"] = comparable && noRegression && safe,
            ["coverage_complete"] = complete, ["same_model_and_campaign"] = sameModel, ["same_limits"] = sameLimits, ["usage_bounded"] = bounded,
            ["no_per_case_regression"] = noRegression, ["zero_unsafe_or_incorrect_approvals"] = safe, ["cases"] = comparisons,
            ["metrics"] = new JsonObject(cohorts.Select(c => new KeyValuePair<string, JsonNode?>(c.Key, Metrics(c.Value)))) };
        static JsonObject Metrics(IReadOnlyList<JsonObject> cohort)
        {
            var values = cohort.Select(r => r["result"]).OfType<JsonObject>().ToArray();
            long? Attempts(JsonObject row) => row["physical_attempts"] is JsonValue value &&
                long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0 ? count : null;
            var attempts = values.Select(Attempts).ToArray();
            double? Median(string key) { var numbers = values.Select(v => double.TryParse(v[key]?.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? (double?)n : null).Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray(); return numbers.Length == 0 ? null : (numbers[(numbers.Length - 1) / 2] + numbers[numbers.Length / 2]) / 2; }
            return new() { ["outcomes"] = values.Length, ["physical_attempts"] = attempts.Length > 0 && attempts.All(a => a.HasValue) ? attempts.Sum(a => a!.Value) : (long?)null, ["median_calls"] = Median("calls"),
                ["median_latency_ms"] = Median("elapsed_ms"), ["known_input_tokens"] = values.Sum(v => v["known_input_tokens"]?.GetValue<long>() ?? 0), ["known_output_tokens"] = values.Sum(v => v["known_output_tokens"]?.GetValue<long>() ?? 0),
                ["usage_complete"] = values.All(v => v["usage_complete"]?.GetValue<bool>() == true) };
        }
    }

    public static JsonObject Summary(IReadOnlyList<JsonObject> rows, string phase)
    {
        var calls = rows.Select(r => r["calls"]!.GetValue<int>()).Order().ToArray(); var count = calls.Length;
        var expected = phase == "measured" ? CandidateCases.Length * 3 : phase == "pilot" ? CandidateCases.Length : count;
        var requiredRepetitions = phase == "measured" ? 3 : 1;
        var complete = count == expected && (phase == "fixture" || CandidateCases.All(name => Enumerable.Range(1, requiredRepetitions)
            .All(repetition => rows.Count(r => r["case"]!.ToString() == name && r["repetition"]!.GetValue<int>() == repetition) == 1)))
            && rows.Select(r => r["source_commit"]!.ToString()).Distinct().Count() == 1;
        var reviewed = rows.Count(r => r["final_review"]!.GetValue<bool>()); var fast = rows.Count(r => r["final_review"]!.GetValue<bool>() && r["calls"]!.GetValue<int>() <= 2);
        var safe = rows.All(r => r["safety_violations"]!.AsArray().Count == 0); var correct = rows.All(r => !r["final_review"]!.GetValue<bool>() || r["execution_correct"]!.GetValue<bool>());
        double? median = count == 0 ? null : (calls[(count - 1) / 2] + calls[count / 2]) / 2.0;
        return new() { ["summary"] = true, ["phase"] = phase, ["runs"] = count, ["expected_runs"] = expected, ["coverage_complete"] = complete,
            ["first_pass_valid_rate"] = count == 0 ? null : rows.Count(r => r["first_pass_valid"]!.GetValue<bool>()) / (double)count,
            ["final_review_rate"] = count == 0 ? null : reviewed / (double)count, ["final_review_within_two_calls_rate"] = count == 0 ? null : fast / (double)count,
            ["median_calls"] = median, ["p75_calls"] = count == 0 ? null : calls[(int)Math.Ceiling(count * .75) - 1],
            ["approved_execution_correct"] = rows.Any(r => r["execution_variants"] is JsonArray { Count: > 0 }) ? correct : (bool?)null,
            ["zero_safety_violations"] = safe,
            ["gates_passed"] = complete && safe && correct && (phase == "measured" ? reviewed >= Math.Ceiling(expected * 19.0 / 21) && fast >= Math.Ceiling(expected * 16.0 / 21) && median <= 2 : reviewed == count) };
    }
}
