using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    internal static LLMRequest DraftRequest(PlanningSession state, bool repair)
    {
        var context = PlanningCompactConstruction.Context(state);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        PlanningCompactConstruction.ConstrainDraftEvidence(state, context, schema);
        var instructions = PlanningCompactConstruction.DraftGuidance(state) + (repair
            ? "\nFor a replacement extension, preserve its target scope and accepted requirements; the accepted TaskPlan is immutable."
            : "");
        return PlanningCompactConstruction.Request(state, instructions, context, schema,
            state.Phase == PlanningPhase.Replanning ? "replan" : DraftPurpose);
    }
}
