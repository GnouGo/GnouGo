using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

internal static class LiveCampaignEvidence
{
    internal static async Task<JsonObject> PinAsync(BenchmarkCampaign campaign, string source, string provider, string model, string configurationFingerprint, string cohort)
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
            ["scenario_count"] = 2, ["repetitions"] = 3, ["oracle_version"] = "real-workflows-v1"
        };
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
            var pending = physical.Count(a => a!["Status"] is null || usage is null && a["Status"]!.GetValue<int>() is >= 200 and < 300);
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

    internal static async Task<JsonObject> ReportAsync(BenchmarkCampaign campaign, string cohort = "final")
    {
        var rows = new JsonArray(); var passed = 0;
        foreach (var scenario in new[] { "amazon", "code" })
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                var label = $"{cohort}-{scenario}-{repetition}";
                var run = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + label);
                if (run is not null)
                {
                    if (run["manifest"] is not JsonObject manifest || await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, cohort + "-manifest") is not { } saved)
                        throw new InvalidOperationException("An existing final run has no comparable manifest.");
                    RequireMatch(saved, manifest);
                }
                var result = run?["result"]?.DeepClone().AsObject() ?? new JsonObject { ["status"] = run is null ? "not_started" : "interrupted", ["execution_oracle"] = false };
                if (run?["execution_started"] is not null && run["execution_ms"] is null)
                { result["execution_status"] = "interrupted"; result["execution_oracle"] = false; }
                if (result["execution_oracle"]?.GetValue<bool>() == true) passed++;
                result["run"] = label; result["accounting"] = await AccountingAsync(campaign, label); rows.Add(result);
            }
        return new() { ["campaign"] = campaign.Id, ["passed"] = passed, ["required"] = 6, ["complete"] = passed == 6,
            ["manifest"] = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, cohort + "-manifest"))?.DeepClone(), ["runs"] = rows,
            ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign) };
    }
}
