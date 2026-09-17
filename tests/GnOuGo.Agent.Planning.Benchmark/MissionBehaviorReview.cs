using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

// Independent assertions for the frozen benchmark's business specification.
// These names and expectations never participate in production planning.
internal static class MissionBehaviorReview
{
    internal static void Require(PlanningSnapshot state, int stage)
    {
        Check(stage is >= 1 and <= 3 && state.BehaviorPlan is not null && state.Preparation is not null,
            "A current behavior and locked preparation are required.");
        var plan = state.BehaviorPlan!; var preparation = state.Preparation!;
        Check(!PlanningBehaviorPlans.Validate(plan, preparation).Any(d => d.Required), "Behavior contracts must validate.");
        var operations = state.Obligations.Where(o => o.OperationAdmission is not null).ToArray();
        Check(!string.IsNullOrEmpty(state.OperationAdmissionFingerprint) && operations.Length != 0, "Canonical operation admission is required.");
        var nodes = plan.Workflows.SelectMany(w => PlanningBehaviorPlans.Enumerate(w.Steps.Concat(w.Finally))).ToArray();
        Check(operations.All(o => o.OperationAdmission is { Version: 17 } && o.Disposition == "admitted" &&
            (!o.Required || nodes.Any(n => n.OperationIds.Contains(o.Id)))), "All required current effects must be represented.");
        foreach (var operation in operations.Where(o => o.Required)) RuntimeAdmissionDiagnosticRules.RequirePositiveSupports(operation);
        foreach (var workflow in plan.Workflows)
        foreach (var (port, direction) in workflow.Inputs.Select(p => (p, "input")).Concat(workflow.Outputs.Select(p => (p, "output"))))
            Check(state.Declarations.Any(d => d.Id == port.DeclarationId && d.Direction == direction && d.WorkflowScope == workflow.Key &&
                d.Required == port.Required), "Public behavior ports retain canonical declaration ownership.");

        if (stage == 1)
        {
            ProgressiveRules.RequireStageOneDeclarations(state);
            ProgressiveRules.RequireStageOneOperations(state);
            Check(plan.Workflows.Single().Finally.Count == 0 && preparation.Capabilities.All(c => c.Server is null),
                "Local classification introduces no external effect or finalizer.");
            var local = operations.Single();
            const string rules = "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.";
            const string description = "This is deterministic, local, in-memory business processing.";
            const string preservation = "Preserve the original id and amount.";
            RuntimeAdmissionDiagnosticRules.RequireSemanticEvidence(state, local, "request", state.Request.Prompt.IndexOf(rules, StringComparison.Ordinal), rules.Length);
            RuntimeAdmissionDiagnosticRules.RequireGoverningOnly(state, local, "request", state.Request.Prompt.IndexOf(description, StringComparison.Ordinal), description.Length);
            RuntimeAdmissionDiagnosticRules.RequireNoSupportOverlap(state, operations, "request", state.Request.Prompt.IndexOf(preservation, StringComparison.Ordinal), preservation.Length);
        }
        else if (stage == 2)
            RequireBatchTopology(plan, preparation);
        else
            RequireCodeReviewTopology(plan, preparation, operations);
    }

    internal static void RequireBatchTopology(PlanningBehaviorPlan plan, PlanningPreparation preparation)
        {
            Check(plan.Entrypoint == "main" && plan.Workflows.Count == 3 && plan.Workflows.Select(w => w.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["main", "classify_record", "summarize_batch"]), "The three declared workflow boundaries must remain distinct.");
            var main = plan.Workflows.Single(w => w.Key == "main");
            Check(Ports(main.Inputs, ["batchId", "threshold"]) && Ports(main.Outputs, ["summary"]), "Batch public inputs and summary are preserved.");
            var classifier = plan.Workflows.Single(w => w.Key == "classify_record");
            var summary = plan.Workflows.Single(w => w.Key == "summarize_batch");
            Check(Ports(classifier.Inputs, ["record", "threshold"]) && Ports(classifier.Outputs, ["classifiedResult"]) &&
                Ports(summary.Inputs, ["classifiedResults"]) && Ports(summary.Outputs, ["summary"]), "Callee public contracts are preserved.");
            var mainNodes = PlanningBehaviorPlans.Enumerate(main.Steps).ToArray();
            var loops = mainNodes.Where(n => n.Kind == "loop").ToArray();
            Check(loops.Length == 1 && PlanningBehaviorPlans.Enumerate(loops[0].Steps).Count(n => n.Kind == "workflow" && n.WorkflowKey == "classify_record") == 1 &&
                mainNodes.Count(n => n.Kind == "workflow" && n.WorkflowKey == "classify_record") == 1 &&
                mainNodes.Count(n => n.Kind == "workflow" && n.WorkflowKey == "summarize_batch") == 1 &&
                !PlanningBehaviorPlans.Enumerate(loops[0].Steps).Any(n => n.WorkflowKey == "summarize_batch"), "Classification iterates; aggregation runs once outside the loop.");
            var reads = preparation.Capabilities.Where(c => c.Server is not null).ToArray();
            Check(reads.Length == 1 && reads[0].Method == "load_record_batch" && mainNodes.Count(n => n.CapabilityId == reads[0].Id) == 1 &&
                !PlanningBehaviorPlans.Enumerate(loops[0].Steps).Any(n => n.CapabilityId == reads[0].Id), "Read the batch exactly once outside iteration.");
            Check(main.Finally.Count == 1 && main.Finally[0].CapabilityId is { } finalizer &&
                preparation.Capabilities.Any(c => c.Id == finalizer && c.StepType == "set") &&
                classifier.Finally.Count == 0 && summary.Finally.Count == 0, "Only main owns the native set finalizer.");
        }
    internal static void RequireCodeReviewTopology(PlanningBehaviorPlan plan, PlanningPreparation preparation, IReadOnlyList<PlanningObligation> operations)
        {
            var nodes = plan.Workflows.SelectMany(w => PlanningBehaviorPlans.Enumerate(w.Steps.Concat(w.Finally))).ToArray();
            foreach (var method in new[] { "git_clone", "git_compare_refs", "copilot_review", "pull_request_review_write" })
            {
                var capabilities = preparation.Capabilities.Where(c => c.Method == method).ToArray();
                Check(capabilities.Length == 1 && nodes.Count(n => n.CapabilityId == capabilities[0].Id) == 1 &&
                    !nodes.Where(n => n.Kind == "loop").Any(n => PlanningBehaviorPlans.Enumerate(n.Steps).Any(child => child.CapabilityId == capabilities[0].Id)),
                    "CodeReview requires one owned " + method + " behavior.");
            }
            Check(nodes.Any(n => n.Kind == "confirmation") && preparation.Decisions.Any(d =>
                d.ContractSource == PlanningDecisionContract.HumanConfirmation && d.EffectOperationIds.Count > 0 && d.NoEffectValues.Count > 0),
                "Publication requires an explicit permission contract with a no-effect rejection.");
            var cleanup = operations.Where(o => o.Kind == "cleanup").Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            Check(cleanup.Count > 0 && cleanup.All(id => plan.Workflows.Any(w =>
                PlanningBehaviorPlans.Enumerate(w.Finally).Any(n => n.OperationIds.Contains(id)))), "Owned cleanup must remain in finalizers.");
        }

    private static void Check(bool condition, string invariant)
    { if (!condition) throw new InvalidOperationException("MISSION_BEHAVIOR_REVIEW: " + invariant); }

    private static bool Ports(IEnumerable<PlanningBehaviorPort> ports, string[] expected)
        => ports.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal));
}
