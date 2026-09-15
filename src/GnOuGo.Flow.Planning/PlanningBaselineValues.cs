using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Reuse established pure constants from an unchanged revision baseline.</summary>
internal static class PlanningBaselineValues
{
    internal static PlanningNode? PureProducer(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode node)
    {
        if (node.Type != "set" || state.Request.Baseline is null) return null;
        var owner = state.Request.Baseline.Workflows.SingleOrDefault(w => w.Key == workflow.Key);
        var previous = owner is null ? null : PlanningGraphCompiler.Enumerate(owner.Steps.Concat(owner.Finally)).SingleOrDefault(n => n.Key == node.Key);
        if (previous is null || previous.Type != node.Type || previous.Purpose != node.Purpose || previous.CapabilityId != node.CapabilityId ||
            !previous.OperationIds.SequenceEqual(node.OperationIds, StringComparer.Ordinal)) return null;
        if (state.BehaviorRevision?.Fields.Any(f => f.CanonicalLocation.StartsWith("/workflows/" + PlanningFieldPaths.Escape("@" + workflow.Key) + "/", StringComparison.Ordinal) &&
            !f.CanonicalLocation.Contains("/inputs/", StringComparison.Ordinal) && !f.CanonicalLocation.Contains("/outputs/", StringComparison.Ordinal)) == true) return null;
        return previous;
    }

    internal static PlanningSchema? ResultSchema(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode node)
    {
        var producer = PureProducer(state, workflow, node);
        var schema = producer?.OutputSchema;
        if (producer is not null && PlanningGraphValidation.IsLiteral(producer.Input) && (schema is null || !Inline(schema)))
            schema = PlanningGraphImporter.Schema(PlanningGraphValidation.ValueContractResolver(state.Graph!, workflow, state.Preparation!)(producer.Input));
        if (schema is null || !Inline(schema) || !PlanningSchemaPropagation.Established(PlanningGraphCompiler.ToJsonSchema(schema, state.Preparation!))) return null;
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(schema, PlanningJsonContext.Default.PlanningSchema), PlanningJsonContext.Default.PlanningSchema);
        static bool Inline(PlanningSchema value) => value.CapabilityId is null && value.Type != PlanningGraphSkeleton.Unresolved &&
            (value.Items is null || Inline(value.Items)) && value.Properties.All(p => Inline(p.Schema)) &&
            (value.AdditionalProperties is null || Inline(value.AdditionalProperties));
    }

    internal static PlanningValue? Literal(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode node, PlanningHole hole)
    {
        if (!hole.Path.EndsWith("/input", StringComparison.Ordinal) || PureProducer(state, workflow, node) is not { } previous ||
            !PlanningGraphValidation.IsLiteral(previous.Input) || PlanningHoleRequests.Expected(state, workflow, hole) is not { } schema ||
            PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(previous.Input), schema).Count != 0) return null;
        return previous.Input;
    }
}
