using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    private static JsonObject NecessitySchema(JsonObject boundaries, bool baseline)
    {
        var unspecified = PlanningHoleRequests.Object(("state", PlanningHoleRequests.Enum(["unspecified"])),
            ("evidence", PlanningHoleRequests.Type("null")));
        // Existing nodes must remain implementable, including conditional nodes. A
        // baseline node has no optional-capability flag for interpretation to override.
        return baseline ? unspecified : new JsonObject { ["anyOf"] = new JsonArray(unspecified,
            PlanningHoleRequests.Object(("state", PlanningHoleRequests.Enum(["required", "optional"])),
                ("evidence", boundaries.DeepClone().AsObject()))) };
    }

    private static PlanningOperationNecessity ParseNecessity(string? state) => state switch
    {
        "unspecified" => PlanningOperationNecessity.Unspecified,
        "required" => PlanningOperationNecessity.Required,
        "optional" => PlanningOperationNecessity.Optional,
        _ => throw Failure("$plan", "Runtime evidence requires a typed necessity state.")
    };

    private static void ValidateNecessity(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        if (evidence.Necessity == PlanningOperationNecessity.Unspecified)
        {
            if (evidence.NecessityReference is not null)
                throw Failure(evidence.Id, "Unspecified necessity cannot assert explicit necessity evidence.");
            return;
        }
        if (evidence.Necessity is not (PlanningOperationNecessity.Required or PlanningOperationNecessity.Optional) ||
            evidence.NecessityReference is null || evidence.BaselineReference is not null)
            throw Failure(evidence.Id, "Explicit necessity requires owned requested-behavior evidence; baseline necessity is engine owned.");
        var source = state.References.Single(r => r.Id == evidence.SourceReference);
        var proof = state.References.SingleOrDefault(r => r.Id == evidence.NecessityReference);
        if (proof is null || !PlanningChoiceEvidence.Current(state, proof.Id) || proof.SourceId != source.SourceId ||
            proof.Start < source.Start || proof.Length <= 0 || proof.Start + proof.Length > source.Start + source.Length)
            throw Failure(evidence.Id, "Necessity evidence must be inside the action's owned interpretation scope.");
    }

    private static bool ResolveRequiredness(PlanningSnapshot state, IReadOnlyList<PlanningOperationAssignment> assignments)
    {
        var required = false;
        var optional = false;
        foreach (var assignment in assignments)
        {
            var evidence = state.RuntimeEvidence.SingleOrDefault(e => e.Id == assignment.RuntimeEvidenceId)
                ?? throw Failure(assignment.ClauseReference, "Requiredness has no current runtime evidence.");
            ValidateRuntime(state, evidence);
            if (assignment.Necessity != evidence.Necessity)
                throw Failure(assignment.ClauseReference, "The assignment changed its necessity evidence.");
            // Baseline identity is separately validated; conditions do not make its
            // executor or capability optional. Unspecified requested actions default
            // only after all contributions have been considered.
            required |= evidence.BaselineReference is not null || evidence.Necessity == PlanningOperationNecessity.Required;
            optional |= evidence.Necessity == PlanningOperationNecessity.Optional;
            if (required && optional)
                throw Failure(assignment.ClauseReference, "The same runtime occurrence has conflicting explicit required and optional evidence.");
        }
        return !optional;
    }
}
