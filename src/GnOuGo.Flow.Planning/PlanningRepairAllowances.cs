using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningRepairAllowances
{
    internal static PlanningGateAllowance Get(PlanningSnapshot state, string workflow, string gate)
    {
        var existing = state.RepairAllowances.SingleOrDefault(a => a.WorkflowKey == workflow && a.Gate == gate);
        if (existing is not null) return existing;
        var allowance = new PlanningGateAllowance { WorkflowKey = workflow, Gate = gate };
        state.RepairAllowances.Add(allowance); return allowance;
    }
    internal static bool Available(PlanningSnapshot state, string workflow, string gate)
    {
        if (Get(state, workflow, gate).Attempts < state.Request.MaxRepairsPerWorkflowGate) return true;
        PlanningContext.Stop(state, "REPAIR_EXHAUSTED", "The repair allowance for workflow '" + workflow + "' and gate '" + gate + "' is exhausted.", workflow);
        return false;
    }
    internal static void Reserved(PlanningSnapshot state, string workflow, string gate)
    {
        Get(state, workflow, gate).Attempts++;
        if (state.Construction.Workflows.SingleOrDefault(w => w.WorkflowKey == workflow) is { } progress)
        { progress.RepairCalls++; progress.Gate = gate; }
    }
}
