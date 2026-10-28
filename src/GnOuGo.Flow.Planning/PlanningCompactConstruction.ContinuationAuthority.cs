using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // Review receipts authorize exact existing bindings; they are not reusable
    // business propositions or a parallel dependency-proof protocol.
    internal static string ContinuationConsumerHash(PlanningConstructionSuspendedStep item)
    {
        var value = PlanningConstruction.Node(item, PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!.AsObject();
        value.Remove("status"); value.Remove("lastPrerequisiteCheckpoint");
        return PlanningGraphCompiler.Fingerprint(value.ToJsonString());
    }

    private static bool CurrentContinuationAuthority(PlanningSession state, PlanningConstructionCheckpoint checkpoint,
        PlanningConstructionSuspendedStep item)
    {
        if (checkpoint.InvalidatedTaskIds.Count != 0 || checkpoint.Step?.ContinuationBinding is not { } association ||
            association.ContinuationId != item.Id || !ContinuationCurrent(state, item) ||
            HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint) is not { } review ||
            (review["consumers"] as JsonArray ?? []).Any(entry => State(state).SuspendedSteps?.SingleOrDefault(consumer => consumer.Id == entry?["id"]?.ToString())
                is not { Status: "waiting" or "resuming" or "completed" or "prerequisite_discharged" }) ||
            !ValidContinuationDischarge(state, association)) return false;
        var leaf = association.Binding;
        while (leaf.Kind == "field" && leaf.Items.Count == 1) leaf = leaf.Items[0];
        return leaf.Source is { } producer && CheckpointProducerCurrent(state, checkpoint, producer);
    }

    private static void RecordReviewedContinuation(PlanningSession state, PlanningConstructionCheckpoint checkpoint, string baseline)
    {
        var compact = State(state); var step = compact.CurrentStep!;
        var review = HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint)
            ?? throw new PlanningConflictException("The consumer association has no applicable business review.");
        checkpoint.Reviews.Add(new JsonObject
        {
            ["purpose"] = "construction_transition", ["transition"] = "resume_reviewed_consumer",
            ["reason"] = "exact_reviewed_binding", ["currentBaseline"] = baseline,
            ["checkpoint"] = checkpoint.Sequence, ["originCandidateHash"] = checkpoint.PlanHash,
            ["continuationId"] = compact.ResumingStepId, ["consumerStep"] = step.Id,
            ["consumerHash"] = PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(step, PlanningJsonContext.Default.PlanningConstructionStep)!.ToJsonString()),
            ["binding"] = PlanningConstruction.Node(checkpoint.Step!.ContinuationBinding!.Binding, PlanningJsonContext.Default.TaskValue),
            ["reviewRequestId"] = review["requestId"]?.DeepClone(), ["reviewRequestHash"] = review["requestHash"]?.DeepClone(),
            ["reviewResponseHash"] = review["responseHash"]?.DeepClone()
        });
    }

    internal static JsonObject? ReviewedContinuationProvenance(PlanningSession state)
    {
        if (State(state).CurrentStep is not { } step || State(state).ResumingStepId is not { } id) return null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var consumer = PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(step, PlanningJsonContext.Default.PlanningConstructionStep)!.ToJsonString());
        foreach (var checkpoint in state.Construction!.Checkpoints.AsEnumerable().Reverse())
        {
            var row = checkpoint.Reviews.LastOrDefault(row => row["purpose"]?.ToString() == "construction_transition" &&
                row["transition"]?.ToString() == "resume_reviewed_consumer" && row["continuationId"]?.ToString() == id &&
                row["consumerStep"]?.ToString() == step.Id && row["currentBaseline"]?.ToString() == baseline && row["consumerHash"]?.ToString() == consumer);
            if (row is null || checkpoint.InvalidatedTaskIds.Count != 0 ||
                HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint) is not { } review ||
                row["reviewRequestId"]?.ToString() != review["requestId"]?.ToString() ||
                row["reviewRequestHash"]?.ToString() != review["requestHash"]?.ToString() ||
                row["reviewResponseHash"]?.ToString() != review["responseHash"]?.ToString()) continue;
            return row.DeepClone().AsObject();
        }
        return null;
    }
}
