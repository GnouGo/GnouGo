using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    private bool RequiresPreparationReassessment(PlanningSnapshot state, List<PlanningDiagnostic> findings)
    {
        var targets = SemanticTargets(state.Graph!);
        var preparation = findings.Where(d => d.Required && targets.ContainsKey(d.Location) && d.Location.EndsWith("/preparation", StringComparison.Ordinal)).ToList();
        if (preparation.Count == 0) return false;
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.Graph!), "preparation_review", 9, false, findings.ToList()));
        if (state.PreparationReassessments >= state.Request.MaxRepairs)
        {
            state.Diagnostics = preparation;
            state.Diagnostics.Add(new("PREPARATION_REASSESSMENT_LIMIT", "/preparation", "Required runtime observations remain unresolved after the configured preparation reassessments. The current candidate is retained and cannot be approved."));
            state.Status = PlanningStatus.Recovery; state.CurrentPhase = PlanningPhase.Capabilities;
            state.ApprovedHash = null; state.ArtifactHash = null;
            return true;
        }
        Remember(state);
        var discovery = state.PreparationCheckpoint?.ValidatedResults["discovery"]?.DeepClone();
        state.PreparationFeedback = preparation;
        state.PreparationReassessments++;
        state.Preparation = null; state.Graph = null; state.Fragments.Clear(); state.BestFragments.Clear();
        state.BestGraph = null; state.BestScenarios.Clear(); state.BestDiagnostics.Clear(); state.ReviewedGraph = null;
        ResetBehavior(state); state.BehaviorRevisionSource = null; state.BehaviorRevisionPatch = null;
        state.ApprovedHash = null; state.ArtifactHash = null; state.Yaml = null; state.Scenarios.Clear();
        state.Feedback = null; state.RepairAttempt = 0; state.NonImprovingAttempts = 0;
        state.IntentChecked = true; state.Status = PlanningStatus.Created; state.CurrentPhase = PlanningPhase.Capabilities;
        state.Diagnostics = preparation;
        var fingerprint = PlanningGraphCompiler.Fingerprint("decision-contract-v1\n" + JsonSerializer.Serialize(EffectiveRequest(state), PlanningJsonContext.Default.PlanningRequest));
        state.PreparationCheckpoint = new() { Fingerprint = fingerprint, Version = PlanningPreparationCheckpoint.CurrentVersion };
        if (discovery is not null) state.PreparationCheckpoint.ValidatedResults["discovery"] = discovery;
        state.Events.Add(new("preparation_reassessment_required", PlanningPhase.Capabilities, _time.GetUtcNow(), preparation.Count));
        return true;
    }
}
