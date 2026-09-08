using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    private async Task<bool> ReassessFailedObservationConstructionAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var units = state.ConstructionUnits.Where(u => u.Status != "validated" && u.Status != "superseded" && u.RepairCalls > 0 && u.Candidate is not null &&
            u.Diagnostics.Any(d => d.Code is "UNIT_HELPER_DEPENDENCY_INVALID" or "COMPUTATION_FIELD_UNDECLARED")).Take(state.Request.MaxConcurrency).ToArray();
        if (units.Length == 0) return false;
        var fingerprint = PlanningGraphCompiler.Fingerprint(state.Preparation!.Fingerprint + string.Join("\n", units.Select(u => u.Key + ":" + u.CandidateHash)));
        if (state.PreparationReviewFingerprint == fingerprint) return false;
        var nodes = new JsonArray(); var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var wi = state.Graph!.Workflows.FindIndex(w => w.Key == unit.WorkflowKey); var workflow = state.Graph.Workflows[wi];
            PlanningWorkflow? effective = null;
            try { effective = PlanningConstruction.Apply(state.Graph, unit, unit.Candidate!, state.Preparation!).Workflows.Single(w => w.Key == workflow.Key); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { /* Unconverted inputs are explicitly unknown, not absent capabilities. */ }
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, "/workflows/" + wi + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/workflows/" + wi + "/finally")))
            {
                if (!unit.NodeKeys.Contains(node.Key, StringComparer.Ordinal)) continue;
                operations.UnionWith(node.OperationIds);
                var resolved = effective is null ? null : PlanningGraphCompiler.Enumerate(effective.Steps.Concat(effective.Finally)).Single(n => n.Key == node.Key);
                nodes.Add((JsonNode)new JsonObject { ["location"] = path + "/preparation", ["operation"] = DescribeNode(resolved ?? node, state.Preparation),
                    ["effectiveInputsResolved"] = resolved is not null,
                    ["candidate"] = unit.Candidate!["nodes"]?[node.Key]?.DeepClone(), ["helpers"] = unit.Candidate["functions"]?.DeepClone() });
            }
        }
        foreach (var capability in state.Preparation.Capabilities.Where(c => c.OperationIds.Intersect(operations).Any()).ToArray()) operations.UnionWith(capability.InputOperationIds);
        var producers = new JsonArray(state.Graph!.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Where(n => n.OperationIds.Intersect(operations).Any()).Select(n => (JsonNode)DescribeNode(n, state.Preparation)).ToArray());
        var evidence = new JsonObject { ["affected"] = nodes, ["declaredProducers"] = producers, ["lockedContract"] = RelevantContract(state.Preparation, operations.ToList()),
            ["producerContracts"] = new JsonArray(state.Preparation.Capabilities.Where(c => c.OperationIds.Intersect(operations).Any()).Select(c => (JsonNode)new JsonObject
            { ["id"] = c.Id, ["description"] = c.Description, ["outputSchema"] = c.OutputSchema.DeepClone() }).ToArray()) };
        try
        {
            var findings = await ReviewAsync(state, runtime, ct, evidence);
            state.PreparationReviewFingerprint = fingerprint;
            state.Attempts.Add(new(fingerprint, "construction_observation_review", 0, false, findings));
            if (RequiresPreparationReassessment(state, findings)) return true;
            await runtime.CheckpointAsync(state, ct);
        }
        catch (SemanticAssessmentException ex)
        { state.Diagnostics = ex.Diagnostics; state.Status = PlanningStatus.Recovery; state.CurrentPhase = PlanningPhase.Capabilities; return true; }
        return false;
    }

    private bool RequiresPreparationReassessment(PlanningSnapshot state, List<PlanningDiagnostic> findings)
    {
        var targets = SemanticTargets(state.Graph!);
        var preparation = findings.Where(d => d.Required && targets.ContainsKey(d.Location) && d.Location.EndsWith("/preparation", StringComparison.Ordinal)).ToList();
        if (preparation.Count == 0) return false;
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.Graph!), "preparation_review", 9, false, findings.ToList()));
        if (state.PreparationReassessments >= state.Request.MaxRepairs)
        {
            state.Diagnostics = findings.ToList();
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
        // Capability repair does not satisfy independent behavior or implementation
        // findings. Keep them active for the new behavior assessment and construction,
        // separately from the immutable request and retained user answers.
        state.Feedback = "Resolve these evidenced coverage findings while preserving every existing request, answer and locked obligation:\n" +
            string.Join("\n", findings.Where(d => d.Required).Select(d => d.Location + ": " + d.Message));
        state.RepairAttempt = 0; state.NonImprovingAttempts = 0;
        state.IntentChecked = true; state.Status = PlanningStatus.Created; state.CurrentPhase = PlanningPhase.Capabilities;
        state.Diagnostics = findings.ToList();
        var fingerprint = PlanningGraphCompiler.Fingerprint("decision-contract-v1\n" + JsonSerializer.Serialize(EffectiveRequest(state), PlanningJsonContext.Default.PlanningRequest));
        state.PreparationCheckpoint = new() { Fingerprint = fingerprint, Version = PlanningPreparationCheckpoint.CurrentVersion };
        if (discovery is not null) state.PreparationCheckpoint.ValidatedResults["discovery"] = discovery;
        state.Events.Add(new("preparation_reassessment_required", PlanningPhase.Capabilities, _time.GetUtcNow(), preparation.Count));
        return true;
    }
}
