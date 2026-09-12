using System.Diagnostics.Metrics;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Shared redacted observations for Agent.Server and workflow.plan runtime hosts.</summary>
public static class PlanningConvergenceTelemetry
{
    public const string MeterName = "GnOuGo.Flow.Planning";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<int> Holes = Meter.CreateHistogram<int>("gnougo.planning.holes", "{hole}");
    private static readonly Histogram<int> Choices = Meter.CreateHistogram<int>("gnougo.planning.binding_candidates", "{binding}");
    private static readonly Counter<long> Repairs = Meter.CreateCounter<long>("gnougo.planning.repairs", "{attempt}");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("gnougo.planning.gate_failures", "{evaluation}");
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("gnougo.planning.model_calls", "{call}");
    private static readonly Histogram<long> Tokens = Meter.CreateHistogram<long>("gnougo.planning.request_tokens", "{token}");
    private static readonly Counter<long> Avoidable = Meter.CreateCounter<long>("gnougo.planning.avoidable_calls", "{call}");

    private static readonly Histogram<double> ContextUtilization = Meter.CreateHistogram<double>("gnougo.planning.context_utilization", "%");
    private static readonly Counter<long> DecisionPages = Meter.CreateCounter<long>("gnougo.planning.decision_pages", "{page}");
    private static readonly Counter<long> Stops = Meter.CreateCounter<long>("gnougo.planning.technical_stops", "{stop}");

    public static void Observe(PlanningSnapshot before, PlanningSnapshot after, Action<string, IReadOnlyList<KeyValuePair<string, object?>>> emit)
    {
        var tenant = new KeyValuePair<string, object?>("tenant.id", after.Request.TenantId);
        foreach (var page in after.DecisionPages)
        {
            var prior = before.DecisionPages.SingleOrDefault(p => p.Id == page.Id);
            if (prior?.Status == page.Status && prior.RequestId == page.RequestId) continue;
            if (prior is null)
            {
                DecisionPages.Add(1, tenant, new("phase", page.Phase));
                if (after.Request.Generation.MaxInputTokensPerRequest > 0) ContextUtilization.Record(100.0 * page.EstimatedInputTokens / after.Request.Generation.MaxInputTokensPerRequest, tenant, new("phase", page.Phase));
            }
            emit("planning.decision_page", [tenant, new("page", page.Id), new("parent", page.ParentId), new("phase", page.Phase),
                new("workflow", page.WorkflowKey), new("status", page.Status), new("request", page.RequestId), new("decisions", page.Decisions.Count),
                new("estimated_input_tokens", page.EstimatedInputTokens), new("input_target_tokens", page.InputTargetTokens), new("estimated_answer_tokens", page.EstimatedAnswerTokens), new("correction", page.Correction)]);
        }
        if (after.TechnicalStop is { } stop && before.TechnicalStop != stop)
        {
            Stops.Add(1, tenant, new("phase", stop.Phase), new("code", stop.Code));
            emit("planning.technical_stop", [tenant, new("phase", stop.Phase), new("code", stop.Code), new("location", stop.Location), new("unverifiable", stop.Unverifiable)]);
        }
        if (before.Outcome != after.Outcome)
            emit("planning.outcome", [tenant, new("outcome", after.Outcome?.Name), new("revision", after.Revision)]);
        foreach (var workflow in after.Construction.Workflows)
        {
            var previous = before.Construction.Workflows.SingleOrDefault(w => w.WorkflowKey == workflow.WorkflowKey);
            if (previous?.TotalHoles != workflow.TotalHoles || previous.ResolvedHoles != workflow.ResolvedHoles || previous.ModelHoleExposures != workflow.ModelHoleExposures ||
                previous.DeterministicSchemaHoles != workflow.DeterministicSchemaHoles || previous.ModelSchemaHoles != workflow.ModelSchemaHoles ||
                previous.ModelRequired != workflow.ModelRequired || previous.ModelUsed != workflow.ModelUsed)
            {
                foreach (var (kind, count) in new[] { ("total", workflow.TotalHoles), ("deterministic", workflow.DeterministicallyResolvedHoles), ("model", workflow.ModelHoles),
                    ("schema_deterministic", workflow.DeterministicSchemaHoles), ("schema_model", workflow.ModelSchemaHoles) })
                    Holes.Record(count, tenant, new("kind", kind));
                if (workflow.ModelRequired is { } required) Holes.Record(required, tenant, new("kind", "model_required"));
                if (workflow.ModelUsed is { } used) Holes.Record(used, tenant, new("kind", "model_used"));
                emit("planning.convergence", [tenant, new("workflow", workflow.WorkflowKey), new("revision", after.Revision), new("holes.total", workflow.TotalHoles),
                    new("holes.deterministic", workflow.DeterministicallyResolvedHoles), new("holes.model", workflow.ModelHoles), new("holes.exposures", workflow.ModelHoleExposures),
                    new("holes.schema_deterministic", workflow.DeterministicSchemaHoles), new("holes.schema_model", workflow.ModelSchemaHoles),
                    new("model_required", workflow.ModelRequired), new("model_used", workflow.ModelUsed)]);
            }
            foreach (var hole in workflow.HoleChoices)
            {
                var prior = previous?.HoleChoices.SingleOrDefault(h => h.Id == hole.Id);
                if (hole == prior) continue;
                if (hole.DirectBindings is { } direct) Choices.Record(direct, tenant, new("kind", "direct"));
                if (hole.ComputationParameters is { } compute) Choices.Record(compute, tenant, new("kind", "computation"));
                emit("planning.hole_choices", [tenant, new("workflow", workflow.WorkflowKey), new("hole", hole.Id), new("bindings.direct", hole.DirectBindings), new("bindings.computation", hole.ComputationParameters)]);
            }
        }
        foreach (var request in after.RequestAccounting)
        {
            var prior = before.RequestAccounting.SingleOrDefault(a => a.Id == request.Id);
            if (prior?.Evidence == request.Evidence) continue;
            emit("planning.request_convergence", [tenant, new("request", request.Id), new("workflow", request.WorkflowKey), new("phase", request.Phase), new("gate", request.Gate),
                new("evidence", request.Evidence), new("purpose", request.Purpose), new("reasoning", request.Reasoning), new("estimated_input_tokens", request.EstimatedInputTokens),
                new("input_tokens", request.InputTokens), new("output_tokens", request.OutputTokens), new("avoidable_dispatches", request.AvoidableDispatches), new("avoidable_extra_requests", request.AvoidableExtraRequests)]);
            foreach (var (hole, reason) in request.HoleReasons)
                emit("planning.hole_request", [tenant, new("request", request.Id), new("hole", hole), new("reason", reason), new("evidence", request.Evidence)]);
            if (request.Evidence != "receipt") continue;
            KeyValuePair<string, object?>[] tags = [tenant, new("phase", request.Phase), new("gate", request.Gate), new("purpose", request.Purpose)];
            Calls.Add(1, tags);
            Tokens.Record(request.EstimatedInputTokens, [.. tags, new("kind", "estimated_input")]);
            if (request.InputTokens is { } input) Tokens.Record(input, [.. tags, new("kind", "input")]);
            if (request.OutputTokens is { } output) Tokens.Record(output, [.. tags, new("kind", "output")]);
            if (request.AvoidableDispatches is { } dispatches) Avoidable.Add(dispatches, [.. tags, new("kind", "deterministic")]);
            if (request.AvoidableExtraRequests is { } extras) Avoidable.Add(extras, [.. tags, new("kind", "batching")]);
        }
        foreach (var allowance in after.RepairAllowances)
        {
            var delta = allowance.Attempts - (before.RepairAllowances.SingleOrDefault(a => a.WorkflowKey == allowance.WorkflowKey && a.Gate == allowance.Gate)?.Attempts ?? 0);
            if (delta <= 0) continue;
            var phase = after.RequestAccounting.LastOrDefault(r => r.WorkflowKey == allowance.WorkflowKey && r.Gate == allowance.Gate && r.Repair == true)?.Phase ?? "unknown";
            Repairs.Add(delta, tenant, new("phase", phase), new("gate", allowance.Gate));
            emit("planning.gate_repair", [tenant, new("workflow", allowance.WorkflowKey), new("phase", phase), new("gate", allowance.Gate), new("attempts", allowance.Attempts)]);
        }
        foreach (var gate in after.GateProgress)
        {
            var prior = before.GateProgress.SingleOrDefault(g => g.WorkflowKey == gate.WorkflowKey && g.Gate == gate.Gate);
            var delta = gate.Failures - (prior?.Failures ?? 0);
            if (delta <= 0) continue;
            var evaluations = gate.Evaluations.Where(id => prior?.Evaluations.Contains(id, StringComparer.Ordinal) != true).ToArray();
            foreach (var group in evaluations.GroupBy(id => gate.EvaluationPhases.GetValueOrDefault(id, "unknown")))
            {
                Failures.Add(group.Count(), tenant, new("phase", group.Key), new("gate", gate.Gate));
                emit("planning.gate_failure", [tenant, new("workflow", gate.WorkflowKey), new("phase", group.Key), new("gate", gate.Gate), new("new_failures", group.Count()), new("failures", gate.Failures)]);
            }
            if (delta > evaluations.Length)
            {
                Failures.Add(delta - evaluations.Length, tenant, new("phase", "unknown"), new("gate", gate.Gate));
                emit("planning.gate_failure", [tenant, new("workflow", gate.WorkflowKey), new("phase", "unknown"), new("gate", gate.Gate), new("new_failures", delta - evaluations.Length), new("failures", gate.Failures)]);
            }
        }
    }
}
