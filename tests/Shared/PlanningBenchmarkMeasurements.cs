using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Planning.Examples;

public static class PlanningBenchmarkMeasurements
{
    public static readonly string[] CandidateCases = ["local", "read_transform", "nullable_defaults", "conditional", "collections", "protected_cleanup", "review_french", "review_distractors"];
    public static string[] Select(string? selection)
    {
        var names = selection?.Split(',', StringSplitOptions.TrimEntries) ?? CandidateCases;
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !PlanningBenchmarkCases.Names.Contains(n, StringComparer.Ordinal)))
            throw new ArgumentException("Select distinct frozen benchmark cases.");
        return names;
    }
    // Codes supply a provisional diagnosis; reproduction and business evidence establish the final classification.
    public static string Category(string code) => code switch
    {
        "MODEL_DISPATCH_UNVERIFIABLE" or "MODEL_OUTPUT_LIMIT" or "LLM_BUDGET_UNVERIFIABLE" => "provider_transport_failure",
        "PLANNING_HOST_CONTRACT" => "deterministic_builder_defect",
        "HOLE_UNRESOLVED" or "SCHEMA_UNESTABLISHED" => "type_inference_limitation",
        "CAPABILITY_NOT_EXPOSED" => "capability_retrieval_miss",
        "INDEPENDENT_EXECUTION_MISMATCH" or "CLARIFICATION_REQUIRED" => "semantic_misunderstanding",
        "ORACLE_DEFECT" or "UNSAFE_APPROVAL" => "validator_defect",
        _ => "invalid_intent"
    };
    public static void Capture(JsonObject run, PlanningSession state)
    {
        // A reservation still carries the preceding candidate's diagnostics. Do not
        // attribute those to the new call before its response has been validated.
        if (state.PendingCall is not null && state.Status == PlanningStatus.Generating) return;
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
            ["approved_execution_correct_and_safe"] = safe, ["cases"] = perCase };
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
            ["approved_execution_correct"] = correct, ["zero_safety_violations"] = safe,
            ["gates_passed"] = complete && safe && correct && (phase == "measured" ? reviewed >= Math.Ceiling(expected * 19.0 / 21) && fast >= Math.Ceiling(expected * 16.0 / 21) && median <= 2 : reviewed == count) };
    }
}
