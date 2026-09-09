using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    private async Task<bool> ReassessFailedObservationConstructionAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        foreach (var retained in state.ConstructionUnits.Where(u => u.Status == "invalid" && u.Candidate is not null && u.ProducerReviewBaseline is null &&
            u.Dependencies.All(key => state.ConstructionUnits.Single(other => other.Key == key).Status == "validated")))
        {
            try
            {
                var preview = PlanningConstruction.Preview(state.Graph!, retained, retained.Candidate!, state.Preparation!);
                preview.Workflows.Single(w => w.Key == retained.WorkflowKey).Functions = MergeFunctions(state, retained, retained.Candidate!["functions"]?.GetValue<string>());
                var findings = CandidateFindings(state, preview, retained, retained.Candidate!);
                findings.AddRange(PlanningConstruction.ShapeFindings(retained.Candidate!, PlanningConstruction.Schema(
                    state.Graph!.Workflows.Single(w => w.Key == retained.WorkflowKey), retained, state.Preparation!, state.Graph), retained));
                retained.Diagnostics = findings;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { /* Conversion findings remain unresolved. */ }
        }
        var units = state.ConstructionUnits.Where(u => u.Status != "validated" && u.Status != "superseded" && u.RepairCalls > 0 && u.Candidate is not null &&
            u.Diagnostics.Any(d => d.Code is "UNIT_HELPER_DEPENDENCY_INVALID" or "COMPUTATION_COLLECTION_FIELD_INVALID" or "BUSINESS_INPUT_BINDING_MISSING" ||
                d.Code == "COMPUTATION_FIELD_UNDECLARED" && RepairedCurrentField(state, u, d))).Take(state.Request.MaxConcurrency).ToArray();
        if (units.Length == 0) return false;
        var fingerprint = PlanningGraphCompiler.Fingerprint(state.Preparation!.Fingerprint + string.Join("\n", units.Select(u => u.Key + ":" + u.CandidateHash)));
        if (state.PreparationReviewFingerprint == fingerprint) return false;
        var assessInputs = units.Any(u => u.Diagnostics.Any(d => d.Code == "BUSINESS_INPUT_BINDING_MISSING"));
        var assessBehavior = assessInputs || units.Any(u => u.Diagnostics.Any(d => d.Code == "COMPUTATION_COLLECTION_FIELD_INVALID"));
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
                if (assessInputs && !unit.Diagnostics.Any(d => d.Code == "BUSINESS_INPUT_BINDING_MISSING" && d.Location == path + "/input")) continue;
                operations.UnionWith(node.OperationIds);
                var resolved = effective is null ? null : PlanningGraphCompiler.Enumerate(effective.Steps.Concat(effective.Finally)).Single(n => n.Key == node.Key);
                var acceptedWorkflow = state.BehaviorPlan?.Workflows.Single(w => w.Key == workflow.Key);
                var accepted = acceptedWorkflow is null ? null : PlanningBehaviorPlans.Enumerate(acceptedWorkflow.Steps.Concat(acceptedWorkflow.Finally)).Single(n => n.Key == node.Key);
                nodes.Add((JsonNode)new JsonObject { ["location"] = path + "/preparation", ["operation"] = assessInputs ? BusinessDependencyNode(resolved ?? node) : DescribeNode(resolved ?? node, state.Preparation),
                    ["effectiveInputsResolved"] = resolved is not null,
                    ["acceptedInputDependencies"] = new JsonArray((accepted?.InputDependencies ?? []).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                    ["actualBusinessInputs"] = resolved is null ? null : new JsonArray(PlanningDataflow.BusinessInputs(effective!, resolved).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                    ["consumedBindings"] = assessInputs && resolved is not null ? BusinessDependencyBindings(state.Graph, effective!, resolved, state.Preparation) : null,
                    ["inputContract"] = state.Preparation.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId)?.InputSchema.DeepClone(),
                    ["candidate"] = assessInputs || resolved is not null ? null : unit.Candidate!["nodes"]?[node.Key]?.DeepClone(), ["helpers"] = assessInputs ? null : unit.Candidate!["functions"]?.DeepClone() });
            }
        }
        foreach (var capability in state.Preparation.Capabilities.Where(c => c.OperationIds.Intersect(operations).Any()).ToArray()) operations.UnionWith(capability.InputOperationIds);
        var producers = new JsonArray(state.Graph!.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Where(n => n.OperationIds.Intersect(operations).Any() && !units.Any(u => u.NodeKeys.Contains(n.Key, StringComparer.Ordinal)))
            .Select(n => (JsonNode)(assessInputs ? BusinessDependencyNode(n) : DescribeNode(n, state.Preparation))).ToArray());
        var evidence = new JsonObject { ["assessment"] = assessInputs ? "business_input_dependencies" : "observations", ["affected"] = nodes, ["declaredProducers"] = producers, ["lockedContract"] = RelevantContract(state.Preparation, operations.ToList()),
            ["producerContracts"] = new JsonArray(state.Preparation.Capabilities.Where(c => c.OperationIds.Intersect(operations).Any()).Select(c => (JsonNode)new JsonObject
            { ["id"] = c.Id, ["description"] = c.Description, ["outputSchema"] = assessInputs ? null : c.OutputSchema.DeepClone() }).ToArray()) };
        try
        {
            var findings = await ReviewAsync(state, runtime, ct, evidence, assessBehavior);
            if (assessInputs) findings = findings.Select(d => d.Location.EndsWith("/behavior", StringComparison.Ordinal) ? d with { ValidationStage = "business_input_review" } : d).ToList();
            state.PreparationReviewFingerprint = fingerprint;
            state.Attempts.Add(new(fingerprint, "construction_observation_review", 0, false, findings));
            if (RequiresPreparationReassessment(state, findings) || assessBehavior && RequiresBehaviorReassessment(state, findings)) return true;
            await runtime.CheckpointAsync(state, ct);
        }
        catch (SemanticAssessmentException ex) when (ex.Diagnostics.All(d => d.Code == "PREPARATION_REVIEW_CONTEXT_TOO_LARGE"))
        {
            // This optional early assessment cannot suppress an available field repair.
            // Full semantic and scenario validation still gates artifact approval.
            state.PreparationReviewFingerprint = fingerprint;
            state.Attempts.Add(new(fingerprint, "construction_observation_review_deferred", 0, false, ex.Diagnostics.ToList()));
            state.Events.Add(new("observation_review_deferred", "repair_unit", _time.GetUtcNow()));
            return false;
        }
        catch (SemanticAssessmentException ex)
        { state.Diagnostics = ex.Diagnostics; state.Status = PlanningStatus.Recovery; state.CurrentPhase = PlanningPhase.Capabilities; return true; }
        return false;
    }

    internal static void RecordFieldRepair(PlanningSnapshot state, PlanningConstructionUnit unit)
    {
        unit.RepairedFieldCandidateHash = null;
        unit.RepairedFieldFindings = unit.Diagnostics.Where(d => d.Code == "COMPUTATION_FIELD_UNDECLARED")
            .Select(d => FieldRepairFingerprint(state, unit, d)).ToList();
    }

    internal static bool RepairedCurrentField(PlanningSnapshot state, PlanningConstructionUnit unit, PlanningDiagnostic diagnostic)
        => unit.RepairedFieldCandidateHash is not null && unit.RepairedFieldCandidateHash == unit.CandidateHash &&
            unit.RepairedFieldFindings.Contains(FieldRepairFingerprint(state, unit, diagnostic), StringComparer.Ordinal);

    private static string FieldRepairFingerprint(PlanningSnapshot state, PlanningConstructionUnit unit, PlanningDiagnostic diagnostic)
        => PlanningGraphCompiler.Fingerprint(UnitFingerprint(state, unit) + "\n" + diagnostic.Code + "\n" + diagnostic.Location + "\n" + diagnostic.Message);

    private static JsonObject BusinessDependencyNode(PlanningNode node) => new()
    {
        ["key"] = node.Key, ["purpose"] = node.Purpose, ["type"] = node.Type, ["capabilityId"] = node.CapabilityId,
        ["operationIds"] = new JsonArray(node.OperationIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
    };

    internal static JsonArray BusinessDependencyBindings(PlanningGraph graph, PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation)
    {
        var bindings = new JsonArray(); var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, preparation);
        foreach (var reference in PlanningDataflow.References(node.Input).DistinctBy(PlanningOutputBindings.Id))
        {
            var item = new JsonObject { ["value"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(reference, PlanningJsonContext.Default.PlanningValue)) };
            try
            {
                var schema = resolve(reference);
                // This assessment checks dependency ownership, not projection validity.
                // A bounded summary must never be interpreted as an absent field catalog.
                item["resolved"] = true; item["type"] = schema["type"]?.DeepClone(); item["description"] = schema["description"]?.DeepClone();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { item["resolved"] = false; }
            bindings.Add((JsonNode)item);
        }
        return bindings;
    }

    private bool RequiresPreparationReassessment(PlanningSnapshot state, List<PlanningDiagnostic> findings)
    {
        var targets = SemanticTargets(state.Graph!);
        var preparation = findings.Where(d => d.Required && targets.ContainsKey(d.Location) && d.Location.EndsWith("/preparation", StringComparison.Ordinal)).ToList();
        if (preparation.Count == 0) return false;
        if (UsesWholeWorkflow(state))
        {
            StopSource(state, "JS_REVIEW_REQUIRED", "The preparation contract requires a reviewed revision. The candidate and findings are retained.", findings);
            return true;
        }
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.Graph!), "preparation_review", 9, false, findings.ToList()));
        if (state.PreparationReassessments - state.PreparationReassessmentsAtRetry >= state.Request.MaxRepairs)
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
        state.Feedback = AssessmentFeedback(findings);
        state.FeedbackSource = "assessment"; state.FeedbackAssessmentHash = PlanningGraphCompiler.Fingerprint(state.PreviousGraph!);
        state.RepairAttempt = 0; state.NonImprovingAttempts = 0;
        state.IntentChecked = true; state.Status = PlanningStatus.Created; state.CurrentPhase = PlanningPhase.Capabilities;
        state.Diagnostics = findings.ToList();
        var fingerprint = PlanningGraphCompiler.Fingerprint("decision-contract-v1\n" + JsonSerializer.Serialize(EffectiveRequest(state), PlanningJsonContext.Default.PlanningRequest));
        state.PreparationCheckpoint = new() { Fingerprint = fingerprint, Version = PlanningPreparationCheckpoint.CurrentVersion };
        if (discovery is not null)
        {
            state.PreparationCheckpoint.ValidatedResults["discovery"] = discovery;
            state.PreparationCheckpoint.FeedbackCatalogHash = PlanningPreparationCheckpoint.CatalogHash(discovery);
        }
        state.Events.Add(new("preparation_reassessment_required", PlanningPhase.Capabilities, _time.GetUtcNow(), preparation.Count));
        return true;
    }
}
