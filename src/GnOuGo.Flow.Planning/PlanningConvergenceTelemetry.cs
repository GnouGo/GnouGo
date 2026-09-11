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

    public static void Observe(PlanningSnapshot before, PlanningSnapshot after, Action<string, IReadOnlyList<KeyValuePair<string, object?>>> emit)
    {
        var tenant = new KeyValuePair<string, object?>("tenant.id", after.Request.TenantId);
        foreach (var workflow in after.Construction.Workflows)
        {
            var previous = before.Construction.Workflows.SingleOrDefault(w => w.WorkflowKey == workflow.WorkflowKey);
            if (previous?.TotalHoles != workflow.TotalHoles || previous.ResolvedHoles != workflow.ResolvedHoles || previous.ModelHoleExposures != workflow.ModelHoleExposures)
            {
                foreach (var (kind, count) in new[] { ("total", workflow.TotalHoles), ("deterministic", workflow.DeterministicallyResolvedHoles), ("model", workflow.ModelHoles) })
                    Holes.Record(count, tenant, new("kind", kind));
                emit("planning.convergence", [tenant, new("workflow", workflow.WorkflowKey), new("revision", after.Revision), new("holes.total", workflow.TotalHoles),
                    new("holes.deterministic", workflow.DeterministicallyResolvedHoles), new("holes.model", workflow.ModelHoles), new("holes.exposures", workflow.ModelHoleExposures)]);
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
        foreach (var allowance in after.RepairAllowances)
        {
            var delta = allowance.Attempts - (before.RepairAllowances.SingleOrDefault(a => a.WorkflowKey == allowance.WorkflowKey && a.Gate == allowance.Gate)?.Attempts ?? 0);
            if (delta <= 0) continue;
            Repairs.Add(delta, tenant, new("gate", allowance.Gate));
            emit("planning.gate_repair", [tenant, new("workflow", allowance.WorkflowKey), new("gate", allowance.Gate), new("attempts", allowance.Attempts)]);
        }
        foreach (var gate in after.GateProgress)
        {
            var delta = gate.Failures - (before.GateProgress.SingleOrDefault(g => g.WorkflowKey == gate.WorkflowKey && g.Gate == gate.Gate)?.Failures ?? 0);
            if (delta <= 0) continue;
            Failures.Add(delta, tenant, new("gate", gate.Gate));
            emit("planning.gate_failure", [tenant, new("workflow", gate.WorkflowKey), new("gate", gate.Gate), new("failures", gate.Failures)]);
        }
    }
}
