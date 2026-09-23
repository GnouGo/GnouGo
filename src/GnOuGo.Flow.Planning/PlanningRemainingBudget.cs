using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningRemainingBudget
{
    internal static void Require(PlanningSession state, CapabilityGrounding coverage, int? selectionCalls = null)
    {
        var pages = coverage.Pages.Count(p => p.CapabilityIds.Count > 0 && !coverage.Results.Any(r => r.PageId == p.Id));
        var selection = selectionCalls ?? (coverage.Selections is null && pages == 0 && coverage.Results.SelectMany(r => r.Decisions)
            .GroupBy(d => d.ActionId).Any(g => !((coverage.RetainedSelections ?? []).Any(s => s.ActionId == g.Key)) &&
                g.SelectMany(d => d.Matches).DistinctBy(m => m.CapabilityId).Count() > 1) ? 1 : 0);
        var binding = state.GroundedPlan is not null ? 0 : coverage.Selections is not null ? GroundedBindingBatches.EstimateCalls(state) : 1;
        var scenarios = state.Fixtures is null && state.Diagnostics.Any(d => d.Code == "SCENARIO_FIXTURE_REQUIRED") ? 1 : 0;
        var required = pages + selection + binding + scenarios;
        var remaining = state.Request.MaxModelCalls - state.ModelCalls;
        if (required > remaining)
            throw new WorkflowRuntimeException("GROUNDING_BUDGET_INSUFFICIENT",
                $"Remaining work requires at least {required} model calls ({pages} catalog pages, {selection} known selections, {binding} binding batches, {scenarios} known scenario requests); the remaining allowance is {remaining}. Further selections, scenarios or repairs may require additional calls. No limits were increased.",
                details: new JsonObject { ["location"] = "/grounding" });
    }
}
