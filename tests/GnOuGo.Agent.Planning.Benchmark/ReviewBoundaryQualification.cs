using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

// A targeted protocol qualification, separate from ordinary business convergence.
// Observing journals never selects a planner action or changes its plan.
internal static class ReviewBoundaryQualification
{
    internal const string Scenario = "review-boundary";
    internal const string Version = "unchanged-business-review-boundary-v1";
    internal const string Prompt = "Accept one required numeric public input unitPrice, with no default. " +
        "Calculate the lineTotal for two units. Then calculate invoiceTotal by adding a fixed handling fee of 1 to lineTotal. " +
        "Return exactly the two numeric public outputs lineTotal and invoiceTotal. Bind unitPrice at runtime. " +
        "These are two business responsibilities: establish the line price, then apply invoice handling. " +
        "This targeted planning evaluation must exercise a business review of already accepted work: first accept a useful line-pricing contribution " +
        "without claiming business completion or closing an original requirement. In a following planning decision request business review of that " +
        "unchanged accepted contribution, without adding executable work, closing a scope or closing an original requirement. " +
        "After that review succeeds, continue with invoice handling and finish through ordinary global review. " +
        "This checkpoint is a planning review, not a runtime approval, marker, report or dummy task. Use native arithmetic; no external effects or runtime inference.";

    internal static JsonObject Inputs(string variant) => new() { ["unitPrice"] = Price(variant) };
    private static decimal Price(string variant) => variant switch
    { "nominal" => 20.5m, "alternate" => -2.5m, _ => throw new ArgumentException("Unknown boundary input variant.", nameof(variant)) };

    internal static bool Outputs(bool success, JsonNode? result, string variant) => success && result is JsonObject outputs && outputs.Count == 2 &&
        Number(outputs["lineTotal"], Price(variant) * 2) && Number(outputs["invoiceTotal"], Price(variant) * 2 + 1);

    private static bool Number(JsonNode? value, decimal expected) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number &&
        decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var actual) && actual == expected;

    internal static IReadOnlyList<string> Artifact(TaskPlan? plan, PlanningCatalog? catalog)
    {
        var findings = SyntheticArtifactOracle.Validate("local", plan, catalog).ToList();
        if (plan is null) return findings;
        if (plan.Inputs.Count != 1 || plan.Inputs[0] is not { Name: "unitPrice", Required: true, Default: null, Type.Kind: "number", Type.Nullable: false })
            findings.Add("wrong_runtime_price_input");
        if (plan.Root.Outputs.Count != 2 || new[] { "lineTotal", "invoiceTotal" }.Any(name => plan.Root.Outputs.Count(o => o.Name == name) != 1))
            findings.Add("wrong_public_invoice_outputs");
        return findings;
    }

    internal static JsonObject Evidence(PlanningSession state)
    {
        var construction = state.Construction;
        foreach (var claim in construction?.Checkpoints ?? [])
        {
            if (claim.Step is not { Kind: "close", Scope: "/root", CloseScopes.Count: 0 } step ||
                step.CloseRequirementIds.Count != 0 || claim.TaskIds.Count != 0 || claim.InvalidatedTaskIds.Count != 0 ||
                claim.BaseHash != claim.PlanHash || claim.ClosedScopes.Count != 0 || claim.ClosedRequirementIds is not { Count: 0 } ||
                !Flag(claim.Fragment["completeBlock"], true) || !Flag(claim.Fragment["complete"], false) ||
                !Flag(claim.Fragment["businessReviewPending"], false) ||
                !Empty(claim.Fragment["tasks"]) || !Empty(claim.Fragment["outputs"]) ||
                claim.Fragment["groups"] is JsonArray { Count: > 0 } || claim.Fragment["choices"] is JsonArray { Count: > 0 }) continue;
            var earlier = construction!.Checkpoints.Where(c => c.Sequence < claim.Sequence).ToArray();
            var prior = earlier.LastOrDefault();
            var owned = earlier.Where(c => c.Step?.BlockId == step.BlockId && c.TaskIds.Count > 0 && c.InvalidatedTaskIds.Count == 0).ToArray();
            if (prior?.PlanHash != claim.BaseHash || owned.Length == 0 ||
                earlier.Any(c => c.ClosedScopes.Contains("/root", StringComparer.Ordinal) || c.ClosedRequirementIds is { Count: > 0 }) ||
                construction.Compact?.Blocks?.SingleOrDefault(b => b.Id == step.BlockId)?.CompletionCheckpoint != claim.Sequence) continue;
            var reviews = claim.Reviews.Where(r => r["purpose"]?.ToString() == "construction_local_judge" &&
                r["candidateHash"]?.ToString() == claim.PlanHash && r["baselineHash"]?.ToString() == claim.BaseHash).ToArray();
            var accepted = reviews.LastOrDefault(r => r["outcome"]?.ToString() == "ok" &&
                Flag(r["claims"]?["completeBlock"], true) && Flag(r["claims"]?["complete"], false) &&
                Empty(r["claims"]?["closedScopes"]) && Empty(r["claims"]?["closedRequirements"]) &&
                r["openScopes"] is JsonArray scopes && scopes.Any(s => s?.ToString() == "/root") &&
                r["reviewedTaskIds"] is JsonArray ids && owned.SelectMany(c => c.TaskIds).Any(id => ids.Any(v => v?.ToString() == id)));
            if (accepted is null) continue;
            var firstReviewCall = reviews.Select(r => Call(r["requestId"]?.ToString())).Where(n => n > 0).DefaultIfEmpty(0).Min();
            var selections = state.RequestMeasurements?.Where(m => Call(m.RequestId) == firstReviewCall - 1).ToArray() ?? [];
            if (selections.Length != 1) continue;
            var selected = selections[0];
            if (firstReviewCall == 0 || selected.Phase is not ("construction_discovery" or "construction_implementation" or "construction_select")) continue;
            return new JsonObject { ["version"] = Version, ["observed"] = true, ["block_id"] = step.BlockId,
                ["contribution_checkpoints"] = new JsonArray(owned.Select(c => (JsonNode)JsonValue.Create(c.Sequence)!).ToArray()),
                ["review_checkpoint"] = claim.Sequence, ["accepted_plan_hash"] = claim.BaseHash, ["reviewed_plan_hash"] = claim.PlanHash,
                ["added_task_count"] = 0, ["closed_scope_count"] = 0, ["closed_original_requirement_count"] = 0,
                ["root_open_during_review"] = true, ["selection_request_id"] = selected.RequestId,
                ["first_review_call"] = firstReviewCall, ["drafting_echo_between_selection_and_review"] = false,
                ["review_request_id"] = accepted["requestId"]?.DeepClone(), ["review_verdict"] = "ok" };
        }
        return new JsonObject { ["version"] = Version, ["observed"] = false };
    }

    private static bool Flag(JsonNode? node, bool expected) => node is JsonValue value && value.TryGetValue<bool>(out var actual) && actual == expected;
    private static bool Empty(JsonNode? node) => node is JsonArray { Count: 0 };
    private static long Call(string? id) => id?.Split(':') is [_, var count, ..] && long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
